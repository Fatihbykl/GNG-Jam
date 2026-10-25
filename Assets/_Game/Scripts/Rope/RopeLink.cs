using Tensio;
using Unity.Mathematics;
using UnityEngine;

namespace KatKat
{
    /// <summary>İp ucu: Tensio pin hedefi + oyun tarafındaki anlamı (eşya mı, ankraj mı).</summary>
    public struct RopeEndpoint
    {
        public Transform Target;
        public Vector3 LocalOffset;
        public Item Item;
        public AnchorPoint Anchor;

        public bool IsValid => Target != null;
        public Vector3 World => Target.TransformPoint(LocalOffset);

        public static RopeEndpoint ForItem(Item item, Vector3 worldPoint)
        {
            return new RopeEndpoint
            {
                Target = item.Body.transform,
                LocalOffset = item.Body.transform.InverseTransformPoint(worldPoint),
                Item = item
            };
        }

        public static RopeEndpoint ForAnchor(AnchorPoint anchor)
        {
            var target = anchor.PinTarget;
            return new RopeEndpoint
            {
                Target = target,
                LocalOffset = target.InverseTransformPoint(anchor.transform.position),
                Anchor = anchor
            };
        }

        public EndpointRef ToRef()
        {
            return new EndpointRef
            {
                Kind = Item != null ? EndpointKind.Item : EndpointKind.Anchor,
                Index = Item != null ? Item.Index : Anchor.Index,
                LocalOffset = LocalOffset
            };
        }

        public static bool TryResolve(EndpointRef r, Floor floor, out RopeEndpoint endpoint)
        {
            endpoint = default;
            if (r.Kind == EndpointKind.Item)
            {
                if (r.Index < 0 || r.Index >= floor.Items.Count) return false;
                var item = floor.Items[r.Index];
                endpoint = new RopeEndpoint { Target = item.Body.transform, LocalOffset = r.LocalOffset, Item = item };
            }
            else
            {
                if (r.Index < 0 || r.Index >= floor.Anchors.Count) return false;
                var anchor = floor.Anchors[r.Index];
                endpoint = new RopeEndpoint { Target = anchor.PinTarget, LocalOffset = r.LocalOffset, Anchor = anchor };
            }
            return true;
        }
    }

    /// <summary>
    /// Bir ipin oyun mantığı. Tensio'nun RopeController'ı bu objenin çocuğunda yaşar:
    /// Split ip objesini klonladığı için oyun mantığı klonlanmasın diye üst objede duruyoruz.
    /// </summary>
    public class RopeLink : MonoBehaviour
    {
        public RopeTypeSO Type;
        public int TypeIndex;
        public RopeEndpoint A;
        public RopeEndpoint B;
        public float Length;
        public float Cost;
        /// <summary>Bir filenin küçük eşyayı ana eşyaya bağlayan paket ipi.</summary>
        public bool IsNetChild;
        /// <summary>File ise paketlediği eşyaların ipleri (silinince onlar da gider).</summary>
        public readonly System.Collections.Generic.List<RopeLink> NetChildren = new System.Collections.Generic.List<RopeLink>();

        public bool IsBroken { get; private set; }
        /// <summary>Bağlı olduğu şarjlı kolon patladı; ip bir ucundan serbest sallanıyor.</summary>
        public bool IsReleased { get; private set; }
        public bool IsHolding => !IsBroken && !IsReleased;
        public bool Armed { get; private set; }
        public float BreakMultiplier { get; private set; } = 1f;
        public float CurrentForce { get; private set; }
        public float PeakForce { get; private set; }
        public float EffectiveBreakForce => Type.BreakForce * BreakMultiplier;
        public float ForceRatio => IsHolding ? CurrentForce / Mathf.Max(1f, EffectiveBreakForce) : 0f;
        public float PeakRatio => PeakForce / Mathf.Max(1f, EffectiveBreakForce);

        /// <summary>Uçlardan biri bu kolondaki ankraja bağlıysa o kolon.</summary>
        public StructuralPiece AnchorColumn
        {
            get
            {
                if (A.Anchor != null && A.Anchor.Column != null) return A.Anchor.Column;
                if (B.Anchor != null && B.Anchor.Column != null) return B.Anchor.Column;
                return null;
            }
        }
        public RopeController Rope => _rope;

        public event System.Action<RopeLink> Broken;

        /// <summary>Kopmaya bu oranda yaklaşınca ip nabız gibi kalınlaşıp incelir.</summary>
        private const float DangerRatio = 0.6f;
        private const float DangerPulse = 0.7f;
        private const float DangerPulseSpeed = 18f;
        private static readonly Color SnapTextColor = new Color(1f, 0.35f, 0.25f);

        private RopeController _rope;
        private RopeRenderer _renderer;
        private float _baseRadius;
        private Vector3? _snapPoint;
        private float _armTime;
        private float _warmup;

        public static RopeLink Create(Transform parent, RopeTypeSO type, int typeIndex, RopeEndpoint a, RopeEndpoint b,
            float length, float cost, GameConfig cfg)
        {
            var go = new GameObject($"RopeLink_{type.DisplayName}");
            go.transform.SetParent(parent, false);
            var link = go.AddComponent<RopeLink>();
            link.Type = type;
            link.TypeIndex = typeIndex;
            link.A = a;
            link.B = b;
            link.Length = length;
            link.Cost = cost;
            link._warmup = cfg.ropeBreakWarmup;
            link.BuildRope(cfg);
            if (a.Item != null) a.Item.AddTie();
            if (b.Item != null) b.Item.AddTie();
            return link;
        }

        public RopePlanEntry ToPlanEntry() => new RopePlanEntry { TypeIndex = TypeIndex, A = A.ToRef(), B = B.ToRef() };

        private void BuildRope(GameConfig cfg)
        {
            Vector3 start = A.World;
            Vector3 end = B.World;
            int count = Mathf.Clamp(Mathf.RoundToInt(Length * 4f), 8, 40);

            var go = new GameObject("Rope");
            go.transform.SetParent(transform, false);
            go.transform.position = start;

            _rope = go.AddComponent<RopeController>();
            _rope.UseBezierCurve = false;

            var init = _rope.InitSettings;
            init.Type = RopeController.RopeInitSettings.RopeType.Elastic;
            init.ParticleCount = count;
            init.Length = Length;
            init.AutoCalculateLength = false;
            init.PinFirstParticle = false;

            var ps = _rope.PhysicsSettings;
            ps.Gravity = Physics.gravity;
            ps.SubSteps = cfg.ropeSubSteps;
            ps.Compliance = Type.Compliance;
            ps.ParticleMass = Type.ParticleMass;
            ps.ParticleRadius = Type.Radius;
            ps.LinearDamping = 0.02f;
            ps.BreakOnStretch = false; // kopmayı kendimiz yönetiyoruz (kuvvet tabanlı)
            ps.VisualizeStretch = true;
            ps.StretchColor = new Color(1f, 0.15f, 0.1f);
            ps.MaxThinningScale = 0.75f;
            ps.CollisionMask = 0;
            ps.UnpinnedDebrisMode = RopeController.RopeInitSettings.DebrisMode.None;
            _rope.AeroSettings.AirDrag = 0.2f;

            _rope.PinConstraints.Clear();
            _rope.PinConstraints.Add(new RopeController.PinConstraint
                { ParticleIndex = 0, Target = A.Target, LocalOffset = A.LocalOffset });
            _rope.PinConstraints.Add(new RopeController.PinConstraint
                { ParticleIndex = count - 1, Target = B.Target, LocalOffset = B.LocalOffset });

            _rope.InitializeRope();

            // Hafif sarkık başlangıç şekli
            float dist = Vector3.Distance(start, end);
            float sag = Mathf.Sqrt(Mathf.Max(0f, Length * Length - dist * dist)) * 0.5f;
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)(count - 1);
                Vector3 p = Vector3.Lerp(start, end, t) + Vector3.down * (sag * 4f * t * (1f - t));
                _rope.SetParticlePosition(i, p);
                _rope.SetVelocities(i, Vector3.zero);
            }

            // Malzeme RopeRenderer.Awake'ten önce hazır olmalı, yoksa Tensio "Missing Material" hatası basar.
            var material = Type.ResolveMaterial();
            if (go.GetComponent<MeshFilter>() == null) go.AddComponent<MeshFilter>();
            var meshRenderer = go.GetComponent<MeshRenderer>();
            if (meshRenderer == null) meshRenderer = go.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            _renderer = go.AddComponent<RopeRenderer>();
            _renderer.Material = material;
            _renderer.Radius = Type.Radius;
            _baseRadius = Type.Radius;

            _rope.OnRopeCut += HandleRopeCut;
        }

        public void Arm()
        {
            Armed = true;
            _armTime = Time.time;
            CurrentForce = 0f;
            PeakForce = 0f;
        }

        /// <summary>
        /// Kolon patlarken çağrılır: o kolondaki ankraja bağlı ucu çözer (GDD: "kolon giderse ipi de boşa çıkar").
        /// Böylece ip kolonla birlikte eşyayı dışarı sürüklemez.
        /// </summary>
        /// <param name="label">İpi boşa çıkaran neden; boşsa kolonun şarj sırası.</param>
        public bool ReleaseFromColumn(StructuralPiece column, string label = null)
        {
            if (!IsHolding || _rope == null || column == null) return false;

            int index;
            if (A.Anchor != null && A.Anchor.Column == column) index = 0;
            else if (B.Anchor != null && B.Anchor.Column == column) index = _rope.ParticleCount - 1;
            else return false;

            _rope.UnpinParticle(index);
            IsReleased = true;
            CurrentForce = 0f;
            if (label == null) label = column.Charge != null ? column.Charge.Label : "?";
            if (A.Item != null)
            {
                A.Item.RemoveTie();
                A.Item.NoteRopeReleased(label);
            }
            if (B.Item != null)
            {
                B.Item.RemoveTie();
                B.Item.NoteRopeReleased(label);
            }
            return true;
        }

        public void Reinforce(float multiplier)
        {
            if (!IsHolding) return;
            BreakMultiplier *= multiplier;
            _baseRadius *= 1.25f;
            foreach (var r in GetComponentsInChildren<RopeRenderer>()) r.Radius *= 1.25f;
        }

        private void FixedUpdate()
        {
            if (!Armed || !IsHolding || _rope == null || _rope.Solver == null || !_rope.Solver.IsInitialized) return;

            // PinImpulses, alt adımlar boyunca biriken XPBD konum lambdalarıdır: Σλ = F·dt²/n → F = Σλ·n/dt².
            // Statik ankraj tarafı impuls kaydetmez; kuvvet dinamik gövde (eşya/kolon) tarafından okunur.
            var impulses = _rope.Solver.PinImpulses;
            float maxLambda = 0f;
            for (int i = 0; i < impulses.Length; i++) maxLambda = Mathf.Max(maxLambda, math.length(impulses[i]));

            float dt = Time.fixedDeltaTime;
            float force = maxLambda * _rope.PhysicsSettings.SubSteps / (dt * dt);
            CurrentForce = Mathf.Lerp(CurrentForce, force, 0.5f);
            PeakForce = Mathf.Max(PeakForce, CurrentForce);

            if (Time.time - _armTime < _warmup) return;

            float stretchLimit = Type.MaxStretchRatio * (1f + (BreakMultiplier - 1f) * 0.5f);
            if (CurrentForce > EffectiveBreakForce || _rope.CurrentStressRatio > stretchLimit)
            {
                Snap();
            }
        }

        /// <summary>Gerilen ip kopmaya yaklaştıkça kalınlığı nabız gibi atar (gerçek zamanla, ağır çekimde de okunur).</summary>
        private void Update()
        {
            if (_renderer == null || !Armed || !IsHolding) return;
            float danger = Mathf.InverseLerp(DangerRatio, 1f, ForceRatio);
            float pulse = danger * (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * DangerPulseSpeed));
            _renderer.Radius = _baseRadius * (1f + DangerPulse * pulse);
        }

        /// <summary>İpi ortasından koparır.</summary>
        public void Snap()
        {
            if (IsBroken || _rope == null) return;
            _snapPoint = MidPoint();
            _rope.Split(Mathf.Max(0, _rope.ParticleCount / 2 - 1), 0.5f);
            if (!IsBroken) MarkBroken(); // Split reddedilirse bile mantıksal olarak kopmuş say
        }

        private void HandleRopeCut()
        {
            MarkBroken();
        }

        private void MarkBroken()
        {
            if (IsBroken) return;
            bool wasHolding = IsHolding;
            IsBroken = true;
            CurrentForce = 0f;
            if (wasHolding)
            {
                if (A.Item != null) A.Item.RemoveTie();
                if (B.Item != null) B.Item.RemoveTie();
            }
            if (_rope != null) _rope.OnRopeCut -= HandleRopeCut;
            if (_renderer != null) _renderer.Radius = _baseRadius;

            if (Armed)
            {
                if (wasHolding)
                {
                    if (A.Item != null) A.Item.NoteRopeSnapped(Type.DisplayName);
                    if (B.Item != null) B.Item.NoteRopeSnapped(Type.DisplayName);
                }
                Vector3 p = _snapPoint ?? MidPoint();
                LastSnapPoint = p;
                Vfx.RopeSnap(p, Type.Color);
                AudioDirector.Play(Sound.RopeSnap, p);
                GameHUD.Float(p + Vector3.up * 0.4f, "İP KOPTU", SnapTextColor);
            }
            Broken?.Invoke(this);
        }

        /// <summary>Son kopma noktası (efekt ve kamera sarsıntısı için).</summary>
        public Vector3 LastSnapPoint { get; private set; }

        private Vector3 MidPoint()
        {
            if (_rope != null && _rope.Solver != null && _rope.Solver.IsInitialized && _rope.ParticleCount > 0)
                return _rope.GetParticlePosition(_rope.ParticleCount / 2);
            return (A.IsValid && B.IsValid) ? (A.World + B.World) * 0.5f : transform.position;
        }

        /// <summary>Ekran ışınının bu ipin herhangi bir parçasına yakın geçip geçmediğini sınar.</summary>
        public bool Raycast(Ray ray, float tolerance, out float distance)
        {
            distance = float.MaxValue;
            bool hit = false;
            foreach (var rc in GetComponentsInChildren<RopeController>())
            {
                if (rc.Solver == null || !rc.Solver.IsInitialized) continue;
                for (int i = 0; i < rc.ParticleCount - 1; i++)
                {
                    float d = RaySegmentDistance(ray, rc.GetParticlePosition(i), rc.GetParticlePosition(i + 1), out float along);
                    if (d < tolerance && along < distance)
                    {
                        distance = along;
                        hit = true;
                    }
                }
            }
            return hit;
        }

        private static float RaySegmentDistance(Ray ray, Vector3 p0, Vector3 p1, out float along)
        {
            Vector3 u = ray.direction, v = p1 - p0, w = ray.origin - p0;
            float a = Vector3.Dot(u, u), b = Vector3.Dot(u, v), c = Vector3.Dot(v, v);
            float d = Vector3.Dot(u, w), e = Vector3.Dot(v, w);
            float denom = a * c - b * b;
            float s, t;
            if (denom < 1e-6f)
            {
                s = 0f;
                t = c > 1e-6f ? Mathf.Clamp01(e / c) : 0f;
            }
            else
            {
                s = Mathf.Max(0f, (b * e - c * d) / denom);
                t = Mathf.Clamp01((a * e - b * d) / denom);
            }
            along = s;
            return Vector3.Distance(ray.origin + u * s, p0 + v * t);
        }

        private void OnDestroy()
        {
            if (IsHolding)
            {
                if (A.Item != null) A.Item.RemoveTie();
                if (B.Item != null) B.Item.RemoveTie();
            }
        }
    }
}
