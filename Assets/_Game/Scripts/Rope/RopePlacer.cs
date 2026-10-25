using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace KatKat
{
    /// <summary>
    /// Planlama: eşyaya tıkla → ankraja ya da başka eşyaya tıkla. Sağ tık: iptal / ipi sil.
    /// Ağır çekim: ipe tıkla = destekle, eşyaya tıkla = en yakın ankraja ip at.
    /// </summary>
    public class RopePlacer : MonoBehaviour
    {
        private const float RopePickTolerance = 0.18f;

        public string HoverText { get; private set; }
        public bool HasPending { get; private set; }

        private GameManager _gm;
        private OrbitCamera _orbit;
        private Camera _cam;
        private LineRenderer _preview;
        private RopeEndpoint _pending;
        private readonly List<RaycastHit> _hits = new List<RaycastHit>();

        public void Init(GameManager gm, OrbitCamera orbit)
        {
            _gm = gm;
            _orbit = orbit;
            _cam = orbit.GetComponent<Camera>();

            var go = new GameObject("RopePreview");
            go.transform.SetParent(transform, false);
            _preview = go.AddComponent<LineRenderer>();
            _preview.positionCount = 2;
            _preview.widthMultiplier = 0.04f;
            _preview.shadowCastingMode = ShadowCastingMode.Off;
            _preview.sharedMaterial = GameMaterials.Get(Color.white);
            _preview.enabled = false;
        }

        public void Cancel()
        {
            HasPending = false;
            if (_preview != null) _preview.enabled = false;
        }

        private void Update()
        {
            HoverText = null;
            var mouse = Mouse.current;
            var kb = Keyboard.current;
            if (mouse == null || _gm.ActiveFloor == null) return;

            bool planning = _gm.State == GameState.Planning;
            bool slowMo = _gm.SlowMo.IsActive;
            if (!planning && !slowMo)
            {
                Cancel();
                return;
            }

            if (kb != null)
            {
                for (int i = 0; i < _gm.RopeTypes.Length && i < 9; i++)
                {
                    if (kb[Key.Digit1 + i].wasPressedThisFrame) _gm.SelectRopeType(i);
                }
                if (kb.escapeKey.wasPressedThisFrame) Cancel();
            }

            Vector2 mousePos = mouse.position.ReadValue();
            if (GameHUD.IsPointerOverUI(mousePos))
            {
                if (_preview.enabled) _preview.enabled = false;
                return;
            }

            Ray ray = _cam.ScreenPointToRay(mousePos);
            bool hasHit = PickWorld(ray, out RaycastHit hit);
            Item item = hasHit ? hit.collider.GetComponentInParent<Item>() : null;
            AnchorPoint anchor = hasHit ? hit.collider.GetComponent<AnchorPoint>() : null;
            RopeLink rope = PickRope(ray, hasHit ? hit.distance + 0.3f : float.MaxValue);

            UpdateHover(item, anchor, rope);

            if (kb != null && kb.fKey.wasPressedThisFrame && item != null) _orbit.SetFocus(item.transform.position);

            if (planning) HandlePlanning(mouse, hasHit, hit, item, anchor, rope);
            else HandleSlowMo(mouse, hit, item, rope);

            UpdatePreview(ray, hasHit, hit, item, anchor);
        }

        private void HandlePlanning(Mouse mouse, bool hasHit, RaycastHit hit, Item item, AnchorPoint anchor, RopeLink rope)
        {
            if (mouse.leftButton.wasPressedThisFrame)
            {
                if (!HasPending)
                {
                    if (item != null && item.CanBeTied)
                    {
                        _pending = RopeEndpoint.ForItem(item, hit.point);
                        HasPending = true;
                    }
                    else if (item != null)
                    {
                        _gm.ShowMessage($"{item.DisplayName} bağlanamaz");
                    }
                }
                else
                {
                    RopeEndpoint target = default;
                    if (item != null && item.CanBeTied) target = RopeEndpoint.ForItem(item, hit.point);
                    else if (anchor != null) target = RopeEndpoint.ForAnchor(anchor);

                    if (!target.IsValid)
                    {
                        _gm.ShowMessage("İpi bir kolona, zemin halkasına ya da başka bir eşyaya bağla");
                    }
                    else if (_gm.TryCreateRope(_pending, target, _gm.SelectedRopeType, out string error) != null)
                    {
                        AudioDirector.Play(Sound.RopePlace);
                        Cancel();
                    }
                    else
                    {
                        _gm.ShowMessage(error);
                    }
                }
            }

            if (mouse.rightButton.wasReleasedThisFrame && !_orbit.RightDragged)
            {
                if (HasPending) Cancel();
                else if (rope != null) _gm.RemoveRope(rope);
            }
        }

        private void HandleSlowMo(Mouse mouse, RaycastHit hit, Item item, RopeLink rope)
        {
            if (!mouse.leftButton.wasPressedThisFrame) return;
            if (rope != null) _gm.ReinforceRope(rope);
            else if (item != null) _gm.ThrowRope(item, hit.point);
        }

        private void UpdateHover(Item item, AnchorPoint anchor, RopeLink rope)
        {
            if (rope != null)
            {
                string action = _gm.State == GameState.Planning ? "sağ tık: sil" : "tık: destekle";
                HoverText = $"{rope.Type.DisplayName}  {rope.Length:0.0} m  ({action})";
            }
            else if (item != null)
            {
                string size = item.Size == ItemSize.Big ? "büyük" : "küçük";
                HoverText = $"{item.DisplayName} ({size}, {item.Body.mass:0} kg){(item.IsTied ? "  • bağlı" : "")}";
            }
            else if (anchor != null)
            {
                HoverText = DescribeAnchor(anchor);
            }
        }

        private string DescribeAnchor(AnchorPoint anchor)
        {
            if (anchor.Kind == AnchorKind.FloorRing) return "Zemin halkası: sağlam, patlamaz";

            var column = anchor.Column;
            if (column == null || column.IsBearing) return "Taşıyıcı kolon: patlamaz, sağlam ankraj";
            if (column.Charge == null) return "Kolon ankrajı";

            int order = _gm.Sequence.Order.IndexOf(column.Charge) + 1;
            float t = _gm.Sequence.FireTimeOf(column.Charge);
            return $"Şarjlı kolon: {order}. sırada ({column.Charge.Label}, {t:0.0} sn) patlar, ip o anda boşa çıkar";
        }

        private void UpdatePreview(Ray ray, bool hasHit, RaycastHit hit, Item item, AnchorPoint anchor)
        {
            if (!HasPending || !_pending.IsValid)
            {
                _preview.enabled = false;
                return;
            }

            Vector3 start = _pending.World;
            Vector3 end;
            bool validTarget = (item != null && item.CanBeTied && item != _pending.Item) || anchor != null;
            if (anchor != null) end = anchor.transform.position;
            else if (hasHit) end = hit.point;
            else end = ray.GetPoint(Vector3.Distance(ray.origin, start));

            float length = Vector3.Distance(start, end) * _gm.Config.ropeSlack;
            float cost = RopeBudget.CostOf(length, _gm.RopeTypes[_gm.SelectedRopeType]);
            bool ok = validTarget && length <= _gm.Config.maxRopeLength && _gm.Budget.CanAfford(cost);

            bool risky = anchor != null && anchor.Column != null && !anchor.Column.IsBearing;
            Color c = !ok ? new Color(1f, 0.3f, 0.25f)
                : risky ? new Color(1f, 0.7f, 0.15f)
                : new Color(0.3f, 0.95f, 0.4f);
            _preview.startColor = _preview.endColor = c;
            _preview.sharedMaterial = GameMaterials.Get(c);
            _preview.SetPosition(0, start);
            _preview.SetPosition(1, end);
            _preview.enabled = true;
            var type = _gm.RopeTypes[_gm.SelectedRopeType];
            string net = "";
            if (type.IsNet && _pending.Item != null)
            {
                int n = _gm.NetCandidates(_pending.Item, type).Count;
                net = n > 0 ? $"\nFile: yakındaki {n} küçük eşya pakete katılır (+{n * type.NetCostPerItem:0.0} ve ip)"
                    : "\nFile: yakında paketlenecek küçük eşya yok";
            }
            HoverText = $"{length:0.0} m  •  maliyet {cost:0.0}" + net + (HoverText != null ? $"\n{HoverText}" : "");
        }

        /// <summary>
        /// Kesit görünümünde gizlenen (yalnızca gölge düşüren) tavan ve duvarları atlayan ışın testi.
        /// </summary>
        private bool PickWorld(Ray ray, out RaycastHit result)
        {
            _hits.Clear();
            _hits.AddRange(Physics.RaycastAll(ray, 200f, ~0, QueryTriggerInteraction.Collide));
            _hits.Sort((x, y) => x.distance.CompareTo(y.distance));

            foreach (var h in _hits)
            {
                var piece = h.collider.GetComponentInParent<StructuralPiece>();
                if (piece != null && (piece.IsCutaway || piece.IsFaded)) continue;
                if (h.collider.isTrigger && h.collider.GetComponent<AnchorPoint>() == null) continue;
                result = h;
                return true;
            }

            result = default;
            return false;
        }

        private RopeLink PickRope(Ray ray, float maxDistance)
        {
            RopeLink best = null;
            float bestDist = maxDistance;
            foreach (var link in _gm.Ropes)
            {
                if (link == null || !link.IsHolding) continue;
                if (link.Raycast(ray, RopePickTolerance, out float d) && d < bestDist)
                {
                    bestDist = d;
                    best = link;
                }
            }
            return best;
        }
    }
}
