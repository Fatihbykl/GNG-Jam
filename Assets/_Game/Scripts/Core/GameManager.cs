using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace KatKat
{
    public enum GameState
    {
        Planning,
        Detonating,
        Settling,
        Results,
        GameOver
    }

    /// <summary>
    /// Oyun döngüsü: Planlama → Patlama (ağır çekim pencereleriyle) → Durma → Sonuç → bir kat aşağı.
    /// Sahnede tek bir GameManager yeterli; kamera, ışık ve diğer sistemler eksikse kendisi kurar.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class GameManager : MonoBehaviour
    {
        private const float MessageDuration = 2.5f;

        public GameConfig Config = new GameConfig();
        [Tooltip("Işık, gökyüzü, sis ve post-processing. Play sırasında canlı ayarlanır.")]
        public VisualConfig Visuals = new VisualConfig();
        [Tooltip("Yapı parçalarının ve eşyaların prefabları. Boşsa prototip küpler kullanılır.")]
        public GameContentSO Content;
        public RopeTypeSO[] RopeTypes;
        public GameObject ExplosionVfxPrefab;
        [Tooltip("0 = her oyunda rastgele.")]
        public int RunSeed;

        public GameState State { get; set; }
        public RunState Run { get; private set; }
        public Floor ActiveFloor { get; private set; }
        public Floor BelowFloor { get; private set; }
        public RopeBudget Budget { get; private set; }
        public FloorResult LastResult { get; private set; }
        /// <summary>Sonuç ekranındaki eşya başına neden-sonuç satırları.</summary>
        public List<ItemReport> LastItemReports { get; private set; } = new List<ItemReport>();
        /// <summary>Patlama sürerken anlık ceza (HUD çubuğu için, kısa aralıklarla yenilenir).</summary>
        public FloorResult LiveResult { get; private set; }
        public int PenaltyLimit => FloorScorer.PenaltyLimitFor(ActiveFloor, FloorNumber, Config);
        public int SelectedRopeType { get; private set; }
        public string Message { get; private set; }
        public int FloorNumber => Run.FloorsDescended;

        public readonly List<RopeLink> Ropes = new List<RopeLink>();

        public SafeZone Zone { get; private set; }
        public DetonationSequence Sequence { get; private set; }
        public CollapsePreview Collapse { get; private set; }
        public ImpactPreview Impact { get; private set; }
        public CollapseShove Shove { get; private set; }
        public SlowMoDirector SlowMo { get; private set; }
        public RopePlacer Placer { get; private set; }
        public OrbitCamera Orbit { get; private set; }
        public LookDirector Look { get; private set; }

        private Transform _world;
        private GameObject _towerBody;
        private int _floorIndex;
        private float _floorStartBudget;
        private RopePlan _plan = new RopePlan();
        private List<(int index, float delay)> _chargeSetup;
        private float _messageUntil;
        private float _nextLiveScore;
        private Coroutine _settleRoutine;
        private readonly List<NeighborFacade> _facades = new List<NeighborFacade>();

        #region Kurulum

        private void Awake()
        {
            if (RopeTypes == null || RopeTypes.Length == 0) RopeTypes = RopeTypeSO.CreateDefaults();
            EnsureNetType();
            if (Content == null) Content = GameContentSO.CreateDefault();

            Physics.defaultSolverIterations = 12;
            Physics.defaultSolverVelocityIterations = 4;

            EnsureCameraAndLight();
            Zone = new GameObject("SafeZone").AddComponent<SafeZone>();
            Zone.transform.SetParent(transform, false);
            Sequence = gameObject.AddComponent<DetonationSequence>();
            Sequence.ChargeFiring += OnChargeFiring;
            BearingCrumble.Toppled += OnBearingToppled;
            Collapse = gameObject.AddComponent<CollapsePreview>();
            Collapse.Init(this);
            Impact = gameObject.AddComponent<ImpactPreview>();
            Impact.Init(this);
            Shove = gameObject.AddComponent<CollapseShove>();
            Shove.GasIgnited += OnGasIgnited;
            Shove.GasResolved += OnGasResolved;
            SlowMo = gameObject.AddComponent<SlowMoDirector>();
            SlowMo.Init(this);
            Placer = gameObject.AddComponent<RopePlacer>();
            Placer.Init(this, Orbit);
            gameObject.AddComponent<GameHUD>().Init(this);
            gameObject.AddComponent<ItemIndicators>().Init(this);
        }

        /// <summary>Sahnedeki ip listesinde file yoksa varsayılan file eklenir (Data/Rope_Net ile ayarlanabilir).</summary>
        private void EnsureNetType()
        {
            foreach (var t in RopeTypes)
            {
                if (t != null && t.IsNet) return;
            }
            var list = new List<RopeTypeSO>(RopeTypes) { RopeTypeSO.CreateNet() };
            RopeTypes = list.ToArray();
        }

        private void EnsureCameraAndLight()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }

            cam.farClipPlane = 500f;
            Orbit = cam.GetComponent<OrbitCamera>();
            if (Orbit == null) Orbit = cam.gameObject.AddComponent<OrbitCamera>();
            var fader = cam.GetComponent<WallFader>();
            if (fader == null) fader = cam.gameObject.AddComponent<WallFader>();
            fader.Init(this);

            // Güneş, ortam ışığı, gökyüzü, sis ve post-processing
            Look = gameObject.AddComponent<LookDirector>();
            Look.Init(this, cam);
            gameObject.AddComponent<AudioDirector>().Init(this, Content.Sounds, cam);
            Vfx.Init(Content);
        }

        private void Start() => NewRun();

        private void OnDestroy()
        {
            BearingCrumble.Toppled -= OnBearingToppled;
            TimeController.ResetTime();
        }

        #endregion

        #region Oyun akışı

        public void NewRun()
        {
            StopFlow();
            if (_world != null) Destroy(_world.gameObject);
            _world = new GameObject("World").transform;
            _facades.Clear();
            _towerBody = null;

            Run = new RunState(Config, RunSeed != 0 ? RunSeed : Random.Range(1, 99999));
            _floorIndex = 0;
            //BuildNeighbors();

            ActiveFloor = BuildFloor(_floorIndex);
            BelowFloor = BuildFloor(_floorIndex + 1);
            PositionTower();
            BeginFloor(FloorDifficulty.For(0, Config).BaseBudget, null, null);
            Orbit.SetFocus(FocusPoint(), snap: true);
        }

        private Floor BuildFloor(int index)
        {
            return FloorGenerator.Build(_world, index, index, Run.FloorSeed(index), -index * Config.floorHeight, Config,
                Content);
        }

        private void BeginFloor(float budget, RopePlan replay, List<(int index, float delay)> chargeSetup)
        {
            State = GameState.Planning;
            TimeController.ResetTime();

            ActiveFloor.SetDimmed(false);
            ActiveFloor.SetOnlyCeiling(false);
            ActiveFloor.SetCeilingCutaway(true);
            BelowFloor.SetDimmed(true);
            // Alt kattan yalnızca aktif katın zemini olan tavanı görünür
            BelowFloor.SetOnlyCeiling(true);
            Zone.Configure(ActiveFloor.Footprint, ActiveFloor.BaseY, Config.safeZoneMargin);

            Budget = new RopeBudget(budget);
            _floorStartBudget = budget;
            Ropes.Clear();
            NeighborBuilding.HitBodies.Clear();
            LiveResult = default;

            Sequence.SetCharges(ActiveFloor.Charges);
            if (chargeSetup != null) ApplyChargeSetup(chargeSetup);

            if (replay != null)
            {
                foreach (var entry in replay.Entries)
                {
                    if (RopeEndpoint.TryResolve(entry.A, ActiveFloor, out var a) &&
                        RopeEndpoint.TryResolve(entry.B, ActiveFloor, out var b))
                    {
                        TryCreateRope(a, b, entry.TypeIndex, out _);
                    }
                }
            }

            Placer.Cancel();
            Orbit.SetFocus(FocusPoint());
        }

        public void Detonate()
        {
            if (State != GameState.Planning) return;

            _plan = CapturePlan();
            _chargeSetup = CaptureChargeSetup();

            Placer.Cancel();
            ActiveFloor.SetCeilingCutaway(false);
            ActiveFloor.SetSimulated(true);
            foreach (var link in Ropes) link.Arm();

            State = GameState.Detonating;
            AudioDirector.Play(Sound.Detonate);
            SlowMo.Begin(Config.slowMoCharges);
            Orbit.SetCinematic(true);
            // Planlamadaki çöküş oku, eşyaların kayacağı ve duvarların devrileceği yönü belirler
            Shove.Arm(ActiveFloor, Collapse.HasDirection ? Collapse.Direction : Vector3.zero, Collapse.Strength, FloorNumber,
                Config);
            Sequence.Play(Config, Visuals.useCustomBlastFx ? null : ExplosionVfxPrefab, () => _settleRoutine = StartCoroutine(SettleRoutine()));
        }

        private void OnRopeBroken(RopeLink link)
        {
            if (link.Armed) Orbit.ShakeFrom(link.LastSnapPoint, Orbit.ShakeOnCharge * 0.4f);
        }

        /// <summary>Bağsız gaz tüpünün fitili yandı: ağır çekim açılır, oyuncu ip atarak söndürebilir.</summary>
        private void OnGasIgnited(Item item)
        {
            GameHUD.Float(item.Body.worldCenterOfMass + Vector3.up * 0.8f, "FİTİL!", new Color(1f, 0.55f, 0.1f));
            ShowMessage($"{item.DisplayName} tutuştu! Tıklayıp ip at, yoksa patlar");
            SlowMo.TryTrigger();
        }

        private void OnGasResolved(Item item, bool exploded)
        {
            Vector3 pos = item.Body.worldCenterOfMass;
            if (!exploded)
            {
                GameHUD.Float(pos + Vector3.up * 0.8f, "SÖNDÜ", new Color(0.45f, 0.95f, 0.5f));
                ShowMessage($"{item.DisplayName} sabitlendi, fitil söndü");
                return;
            }
            Orbit.ShakeFrom(pos, Orbit.ShakeOnCharge * 1.3f);
            SlowMo.HitStop(Visuals.hitStopDuration, Visuals.hitStopScale);
            ShowMessage($"{item.DisplayName} patladı!");
        }

        /// <summary>Şarjlı kolon patlarken ona bağlı ipler boşa çıkar.</summary>
        private void OnChargeFiring(ExplosiveCharge charge)
        {
            Shove.OnChargeFired(charge);
            Orbit.ShakeFrom(charge.LabelPosition, Orbit.ShakeOnCharge);
            SlowMo.HitStop(Visuals.hitStopDuration, Visuals.hitStopScale);
            int released = 0;
            foreach (var link in Ropes)
            {
                if (link == null) continue;
                foreach (var column in charge.Columns)
                {
                    if (link.ReleaseFromColumn(column))
                    {
                        released++;
                        break;
                    }
                }
            }

            if (released > 0) ShowMessage($"Sıra {charge.Label} patladı: {released} ip boşa çıktı");
        }

        /// <summary>Tabanı kopup devrilen taşıyıcı kolon dağıldı: ona bağlı ipler boşa çıkar.</summary>
        private void OnBearingToppled(StructuralPiece column)
        {
            int released = 0;
            foreach (var link in Ropes)
            {
                if (link != null && link.ReleaseFromColumn(column, BearingCrumble.ReleaseLabel)) released++;
            }
            if (released > 0) ShowMessage($"Taşıyıcı kolon devrildi: {released} ip boşa çıktı");
        }

        private IEnumerator SettleRoutine()
        {
            State = GameState.Settling;
            var fp = ActiveFloor.Footprint;
            Vfx.SettleDust(new Vector3(fp.center.x, ActiveFloor.BaseY + 0.3f, fp.center.y), fp.size);
            float elapsed = 0f, hold = 0f;
            const float minWait = 1f;

            while (true)
            {
                float maxSpeed = 0f;
                float killY = ActiveFloor.BaseY - Config.killDepth;
                foreach (var rb in ActiveFloor.Bodies)
                {
                    if (rb == null || !rb.gameObject.activeSelf || rb.isKinematic) continue;
                    if (rb.position.y < killY)
                    {
                        rb.gameObject.SetActive(false);
                        continue;
                    }
                    maxSpeed = Mathf.Max(maxSpeed, rb.linearVelocity.magnitude);
                }

                elapsed += Time.deltaTime;
                hold = maxSpeed < Config.settleSpeed ? hold + Time.deltaTime : 0f;
                if (elapsed > minWait && (hold >= Config.settleHoldTime || elapsed >= Config.settleTimeout)) break;
                yield return null;
            }

            SlowMo.End();
            Orbit.SetCinematic(false);
            LastResult = FloorScorer.Evaluate(ActiveFloor, Zone, NeighborBuilding.HitBodies, PenaltyLimit);
            LastItemReports = FloorScorer.DescribeItems(ActiveFloor, Zone, Ropes);
            State = GameState.Results;
            _settleRoutine = null;
        }

        /// <summary>Sonuç ekranındaki sonuca göre bir alt katın ip bütçesi (taban + devreden ip − ceza).</summary>
        public float NextFloorBudget()
        {
            return RopeBudget.NextFloorBudget(FloorDifficulty.For(Run.FloorsDescended + 1, Config).BaseBudget,
                Budget.Remaining, LastResult.Penalty, Config.penaltyBudgetCost, Config.minBudget);
        }

        /// <summary>Sonucu uygular ve bir kat aşağı iner.</summary>
        public void ContinueAfterResults()
        {
            if (State != GameState.Results) return;

            var r = LastResult;
            float next = NextFloorBudget();
            Run.Lives -= r.LivesLost;
            Run.TotalPenalty += r.Penalty;
            Run.FloorsDescended++;
            Run.CarriedRope = Budget.Remaining;

            if (Run.Lives <= 0)
            {
                State = GameState.GameOver;
                AudioDirector.Play(Sound.GameOver);
                return;
            }


            DestroyFloor(ActiveFloor);
            _floorIndex++;
            ActiveFloor = BelowFloor;
            BelowFloor = BuildFloor(_floorIndex + 1);
            PositionTower();
            BeginFloor(next, null, null);
        }

        /// <summary>
        /// Hızlı sıfırlama: katı aynı seed ile yeniden üretir, planlanan ipleri ve patlama sırasını geri kurar.
        /// </summary>
        public void QuickReset()
        {
            if (State == GameState.GameOver) return;

            var plan = State == GameState.Planning ? CapturePlan() : _plan;
            var charges = State == GameState.Planning ? CaptureChargeSetup() : _chargeSetup;

            StopFlow();
            DestroyFloor(ActiveFloor);
            ActiveFloor = BuildFloor(_floorIndex);
            BeginFloor(_floorStartBudget, plan, charges);
        }

        private void StopFlow()
        {
            if (_settleRoutine != null) StopCoroutine(_settleRoutine);
            _settleRoutine = null;
            if (Sequence != null) Sequence.Stop();
            if (Shove != null) Shove.Stop();
            if (SlowMo != null) SlowMo.End();
            if (Orbit != null) Orbit.SetCinematic(false);
            TimeController.ResetTime();
        }

        private static void DestroyFloor(Floor floor)
        {
            if (floor == null) return;
            floor.gameObject.SetActive(false);
            Destroy(floor.gameObject);
        }

        private void PositionTower()
        {
            if (_towerBody == null)
            {
                //_towerBody = GameMaterials.CreateBox("TowerBelow", _world, Vector3.zero,
                //    new Vector3(Config.FloorWidth, 300f, Config.FloorDepth), GameMaterials.TowerColor);
            }

            //float top = BelowFloor.BaseY;
            //_towerBody.transform.position = new Vector3(0f, top - 150f, 0f);
            //foreach (var facade in _facades) facade.Follow(ActiveFloor.BaseY);
        }

        private void BuildNeighbors()
        {
            float gap = Config.safeZoneMargin + 1.5f;
            float w = Config.FloorWidth, d = Config.FloorDepth;
            CreateNeighbor(new Vector3(-(w * 0.5f + gap + 2.5f), 0f, 0f), new Vector3(5f, 0f, d + 4f));
            CreateNeighbor(new Vector3(w * 0.25f, 0f, d * 0.5f + gap + 2.5f), new Vector3(w * 0.8f, 0f, 5f));
        }

        private void CreateNeighbor(Vector3 xz, Vector3 size)
        {
            // Sonsuz kule boyunca uzanan komşu bina; tepesi en üst katın biraz üstünde.
            const float top = 8f, height = 1000f;
            var go = GameMaterials.CreateBox("Neighbor", _world, new Vector3(xz.x, top - height * 0.5f, xz.z),
                new Vector3(size.x, height, size.z), GameMaterials.NeighborColor);
            go.AddComponent<NeighborBuilding>();
            _facades.Add(NeighborFacade.Create(_world, new Vector3(xz.x, top, xz.z), size, Config.floorHeight, _facades.Count + 1));
        }

        private Vector3 FocusPoint() => new Vector3(0f, ActiveFloor.BaseY + 1f, 0f);

        #endregion

        #region İp işlemleri

        public void SelectRopeType(int index)
        {
            if (index >= 0 && index < RopeTypes.Length) SelectedRopeType = index;
        }

        public RopeLink TryCreateRope(RopeEndpoint a, RopeEndpoint b, int typeIndex, out string error)
        {
            error = null;
            if (!a.IsValid || !b.IsValid) error = "Geçersiz bağlantı noktası";
            else if (a.Item != null && a.Item == b.Item) error = "Eşya kendisine bağlanamaz";
            else if (a.Anchor != null && b.Anchor != null) error = "En az bir uç eşya olmalı";
            if (error != null) return null;

            float distance = Vector3.Distance(a.World, b.World);
            float length = Mathf.Max(0.5f, distance * Config.ropeSlack);
            if (length > Config.maxRopeLength)
            {
                error = $"İp çok uzun ({length:0.0} m, en fazla {Config.maxRopeLength:0} m)";
                return null;
            }

            var type = RopeTypes[typeIndex];
            float cost = RopeBudget.CostOf(length, type);
            if (!Budget.TrySpend(cost))
            {
                error = $"Bütçe yetmiyor ({cost:0.0} gerekli, {Budget.Remaining:0.0} kaldı)";
                return null;
            }

            var link = RopeLink.Create(ActiveFloor.transform, type, typeIndex, a, b, length, cost, Config);
            link.Broken += OnRopeBroken;
            Ropes.Add(link);
            if (type.IsNet) WrapNet(link, a.Item != null ? a.Item : b.Item);
            return link;
        }

        /// <summary>Filenin paketleyeceği eşyalar: yakındaki bağsız küçük eşyalar, en yakından başlayarak.</summary>
        public List<Item> NetCandidates(Item host, RopeTypeSO type)
        {
            var result = new List<Item>();
            if (host == null || type == null || !type.IsNet || ActiveFloor == null) return result;
            Vector3 center = host.Body.worldCenterOfMass;
            foreach (var item in ActiveFloor.Items)
            {
                if (item == null || item == host || !item.CanBeTied || item.IsTied || item.Size != ItemSize.Small ||
                    !item.gameObject.activeInHierarchy) continue;
                Vector3 d = item.Body.worldCenterOfMass - center;
                d.y = 0f;
                if (d.magnitude <= type.NetRadius) result.Add(item);
            }
            result.Sort((x, y) => Vector3.Distance(x.Body.worldCenterOfMass, center)
                .CompareTo(Vector3.Distance(y.Body.worldCenterOfMass, center)));
            if (result.Count > type.NetMaxItems) result.RemoveRange(type.NetMaxItems, result.Count - type.NetMaxItems);
            return result;
        }

        /// <summary>File: yakındaki küçük eşyaları kısa iplerle ana eşyaya paketler; paket tek kütle gibi davranır.</summary>
        private void WrapNet(RopeLink net, Item host)
        {
            if (host == null) return;
            var type = net.Type;
            int wrapped = 0;
            foreach (var item in NetCandidates(host, type))
            {
                Vector3 from = item.Body.worldCenterOfMass, to = host.Body.worldCenterOfMass;
                float length = Mathf.Max(0.5f, Vector3.Distance(from, to) * Config.ropeSlack);
                float cost = RopeBudget.CostOf(length, type) + type.NetCostPerItem;
                if (length > Config.maxRopeLength || !Budget.TrySpend(cost)) break;

                var child = RopeLink.Create(ActiveFloor.transform, type, net.TypeIndex, RopeEndpoint.ForItem(item, from),
                    RopeEndpoint.ForItem(host, to), length, cost, Config);
                child.IsNetChild = true;
                child.Broken += OnRopeBroken;
                if (State != GameState.Planning) child.Arm(); // ağır çekimde atılan file hemen devrede
                net.NetChildren.Add(child);
                Ropes.Add(child);
                wrapped++;
            }
            if (wrapped > 0) ShowMessage($"File: {wrapped} eşya {host.DisplayName} ile paketlendi");
        }

        public void RemoveRope(RopeLink link)
        {
            if (State != GameState.Planning || link == null) return;
            foreach (var child in link.NetChildren)
            {
                if (child == null || !Ropes.Remove(child)) continue;
                Budget.Refund(child.Cost);
                Destroy(child.gameObject);
            }
            Budget.Refund(link.Cost);
            Ropes.Remove(link);
            Destroy(link.gameObject);
            AudioDirector.Play(Sound.RopeRemove);
        }

        /// <summary>Ağır çekimde: gergin ipi destekler.</summary>
        public void ReinforceRope(RopeLink link)
        {
            if (link == null || !link.IsHolding) return;
            if (!Budget.TrySpend(Config.reinforceCost))
            {
                ShowMessage("Destek için bütçe yok");
                return;
            }
            link.Reinforce(Config.reinforceMultiplier);
            AudioDirector.Play(Sound.RopeReinforce);
            ShowMessage($"{link.Type.DisplayName} desteklendi");
        }

        /// <summary>Ağır çekimde: uçan eşyaya en yakın ankrajdan ip atar.</summary>
        public void ThrowRope(Item item, Vector3 hitPoint)
        {
            if (item == null || !item.CanBeTied) return;

            AnchorPoint best = null;
            float bestDist = Config.slowMoRopeAttachRange;
            foreach (var anchor in ActiveFloor.Anchors)
            {
                if (anchor == null || !anchor.gameObject.activeInHierarchy) continue;
                if (anchor.Column != null && !anchor.Column.IsBearing) continue; // yakında patlayacak kolonu seçme
                float d = Vector3.Distance(anchor.transform.position, hitPoint);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = anchor;
                }
            }

            if (best == null)
            {
                ShowMessage("Yakında ankraj yok");
                return;
            }

            var link = TryCreateRope(RopeEndpoint.ForItem(item, hitPoint), RopeEndpoint.ForAnchor(best), SelectedRopeType,
                out string error);
            if (link == null)
            {
                ShowMessage(error);
                return;
            }
            link.Arm();
            AudioDirector.Play(Sound.RopePlace, hitPoint);
            ShowMessage($"{item.DisplayName} yakalandı!");
        }

        private RopePlan CapturePlan()
        {
            var plan = new RopePlan();
            foreach (var link in Ropes)
            {
                // Filenin paket ipleri, file yeniden kurulunca kendiliğinden gelir
                if (link != null && !link.IsNetChild) plan.Entries.Add(link.ToPlanEntry());
            }
            return plan;
        }

        private List<(int index, float delay)> CaptureChargeSetup()
        {
            var list = new List<(int, float)>();
            foreach (var charge in Sequence.Order) list.Add((ActiveFloor.Charges.IndexOf(charge), charge.Delay));
            return list;
        }

        private void ApplyChargeSetup(List<(int index, float delay)> setup)
        {
            var ordered = new List<ExplosiveCharge>();
            foreach (var (index, delay) in setup)
            {
                if (index < 0 || index >= ActiveFloor.Charges.Count) continue;
                var charge = ActiveFloor.Charges[index];
                charge.Delay = delay;
                ordered.Add(charge);
            }
            if (ordered.Count == ActiveFloor.Charges.Count) Sequence.SetCharges(ordered);
        }

        #endregion

        public void ShowMessage(string message)
        {
            Message = message;
            _messageUntil = Time.unscaledTime + MessageDuration;
        }

        private void Update()
        {
            if (Message != null && Time.unscaledTime > _messageUntil) Message = null;

            if ((State == GameState.Detonating || State == GameState.Settling) && Time.unscaledTime >= _nextLiveScore)
            {
                _nextLiveScore = Time.unscaledTime + 0.25f;
                LiveResult = FloorScorer.Evaluate(ActiveFloor, Zone, NeighborBuilding.HitBodies, PenaltyLimit);
            }

            var kb = Keyboard.current;
            if (kb == null) return;

            if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
            {
                switch (State)
                {
                    case GameState.Planning: Detonate(); break;
                    case GameState.Results: ContinueAfterResults(); break;
                    case GameState.GameOver: NewRun(); break;
                }
            }

            if (kb.rKey.wasPressedThisFrame) QuickReset();
        }
    }
}
