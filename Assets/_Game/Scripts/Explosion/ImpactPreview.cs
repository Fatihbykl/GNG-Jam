using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace KatKat
{
    /// <summary>
    /// Planlamada patlamanın sonucunu tahmin edip gösterir: her eşyanın kayacağı yer (yeşil kalır, sarı kayar,
    /// kırmızı düşer), her ipin tahmini yükü ve her duvarın devrileceği yön. Patlama sırası ya da ipler değiştikçe
    /// yenilenir. Tahmin, oyunda uygulanan itkiyle aynı kuralları kullanır (<see cref="CollapseImpulse"/>);
    /// düşen tavan gibi kaotik çarpmalar hesaba girmez, bu yüzden "yaklaşık".
    /// </summary>
    public class ImpactPreview : MonoBehaviour
    {
        private const float RefreshInterval = 0.1f;
        private const float LineWidth = 0.09f;
        private const float MinArrow = 0.2f;

        public static readonly Color SafeColor = new Color(0.4f, 0.95f, 0.5f);
        public static readonly Color SlideColor = new Color(1f, 0.8f, 0.25f);
        public static readonly Color FallColor = new Color(1f, 0.3f, 0.25f);
        public static readonly Color IdleColor = new Color(0.75f, 0.78f, 0.82f);

        public struct Label
        {
            public Vector3 World;
            public string Text;
            public Color Color;
        }

        public readonly List<Label> Labels = new List<Label>();
        public bool HasData { get; private set; }
        public int BigFalls { get; private set; }
        public int SmallFalls { get; private set; }
        public int Snaps { get; private set; }
        public int OutwardWalls { get; private set; }
        /// <summary>Bağsız kalıp patlayacak gaz tüpü sayısı (küçük düşenlere de sayılır).</summary>
        public int GasBlasts { get; private set; }
        public int EstimatedPenalty { get; private set; }

        private GameManager _gm;
        private float _next;
        private int _usedLines;
        private readonly List<LineRenderer> _lines = new List<LineRenderer>();

        private readonly List<CollapseImpulse.SimBody> _bodies = new List<CollapseImpulse.SimBody>();
        private readonly List<CollapseImpulse.SimTie> _ties = new List<CollapseImpulse.SimTie>();
        private readonly List<RopeLink> _tieLinks = new List<RopeLink>();
        private readonly List<string> _tieRelease = new List<string>();
        private readonly List<CollapseImpulse.SimWall> _walls = new List<CollapseImpulse.SimWall>();
        private readonly List<StructuralPiece> _wallPieces = new List<StructuralPiece>();
        private readonly List<CollapseImpulse.Wave> _waves = new List<CollapseImpulse.Wave>();

        public void Init(GameManager gm) => _gm = gm;

        private void LateUpdate()
        {
            if (_gm == null || _gm.State != GameState.Planning || _gm.ActiveFloor == null)
            {
                HasData = false;
                Labels.Clear();
                HideLines(0);
                return;
            }

            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + RefreshInterval;
            Rebuild();
        }

        private void Rebuild()
        {
            var floor = _gm.ActiveFloor;
            var cfg = _gm.Config;
            var seq = _gm.Sequence;
            var collapse = _gm.Collapse;
            var ctx = CollapseImpulse.PushContext.For(floor.Footprint, collapse.HasDirection ? collapse.Direction : Vector3.zero,
                collapse.Strength, cfg);

            _waves.Clear();
            foreach (var charge in seq.Order)
            {
                if (charge == null) continue;
                var columns = new List<Vector2>();
                foreach (var c in charge.Columns)
                {
                    if (c != null) columns.Add(CollapseImpulse.Flat(c.WorldCenter));
                }
                _waves.Add(new CollapseImpulse.Wave { Time = seq.FireTimeOf(charge), Columns = columns });
            }

            _bodies.Clear();
            foreach (var item in floor.Items)
            {
                var body = new CollapseImpulse.SimBody();
                if (item != null)
                {
                    body.Position = CollapseImpulse.Flat(item.Body.worldCenterOfMass);
                    body.Mass = item.Body.mass;
                    body.BaseAccel = CollapseImpulse.PushAccel(item.Size, _gm.FloorNumber, cfg);
                    body.Radius = item.Definition != null ? item.Definition.ResolveFootprintRadius() * 0.6f : 0.25f;
                    body.Volatile = item.IsVolatile;
                }
                _bodies.Add(body);
            }

            CollectTies(seq);
            CollectWalls(floor, cfg, seq, ctx);

            CollapseImpulse.Simulate(_bodies, _ties, _walls, _waves, ctx, floor.Footprint, cfg.itemFriction);
            Present(floor, ctx, cfg);
            HasData = true;
        }

        private void CollectTies(DetonationSequence seq)
        {
            _ties.Clear();
            _tieLinks.Clear();
            _tieRelease.Clear();
            foreach (var link in _gm.Ropes)
            {
                if (link == null || !link.IsHolding) continue;

                var tie = new CollapseImpulse.SimTie
                {
                    BreakForce = link.EffectiveBreakForce,
                    ShockTime = CollapseImpulse.ShockTime(link.Type.Compliance)
                };
                string release = null;

                if (link.A.Item != null && link.B.Item != null)
                {
                    tie.Body = link.A.Item.Index;
                    tie.OtherBody = link.B.Item.Index;
                }
                else
                {
                    var itemEnd = link.A.Item != null ? link.A : link.B;
                    var anchorEnd = link.A.Item != null ? link.B : link.A;
                    if (itemEnd.Item == null || !anchorEnd.IsValid) continue;

                    Vector3 attach = itemEnd.World, anchor = anchorEnd.World;
                    float dy = anchor.y - attach.y;
                    tie.Body = itemEnd.Item.Index;
                    tie.Attach = CollapseImpulse.Flat(attach);
                    tie.Anchor = CollapseImpulse.Flat(anchor);
                    tie.Length = Mathf.Sqrt(Mathf.Max(0f, link.Length * link.Length - dy * dy));

                    var column = anchorEnd.Anchor != null ? anchorEnd.Anchor.Column : null;
                    if (column != null && !column.IsBearing && column.Charge != null)
                    {
                        tie.ReleaseTime = seq.FireTimeOf(column.Charge);
                        release = column.Charge.Label;
                    }
                }

                _ties.Add(tie);
                _tieLinks.Add(link);
                _tieRelease.Add(release);
            }
        }

        private void CollectWalls(Floor floor, GameConfig cfg, DetonationSequence seq, in CollapseImpulse.PushContext ctx)
        {
            _walls.Clear();
            _wallPieces.Clear();
            foreach (var wall in floor.Walls)
            {
                if (wall == null) continue;
                float fall = float.PositiveInfinity;
                foreach (var column in wall.SupportColumns)
                {
                    if (column == null || column.IsBearing || column.Charge == null) continue;
                    float t = seq.FireTimeOf(column.Charge);
                    if (t >= 0f) fall = Mathf.Min(fall, t);
                }
                _walls.Add(new CollapseImpulse.SimWall
                {
                    Center = CollapseImpulse.Flat(wall.WorldCenter),
                    Outward = CollapseImpulse.Flat(wall.WorldOutward).normalized,
                    HalfLength = cfg.cellSize * 0.5f,
                    FallTime = fall
                });
                _wallPieces.Add(wall);
            }
        }

        private void Present(Floor floor, in CollapseImpulse.PushContext ctx, GameConfig cfg)
        {
            Labels.Clear();
            _usedLines = 0;
            BigFalls = SmallFalls = Snaps = OutwardWalls = GasBlasts = 0;
            float y = floor.BaseY + 0.12f;

            for (int i = 0; i < _bodies.Count && i < floor.Items.Count; i++)
            {
                var item = floor.Items[i];
                if (item == null) continue;
                var b = _bodies[i];
                Vector2 delta = b.End - b.Position;
                Color color;
                switch (b.Result)
                {
                    case CollapseImpulse.Outcome.Explodes:
                    case CollapseImpulse.Outcome.Falls: color = FallColor; break;
                    case CollapseImpulse.Outcome.Hangs:
                    case CollapseImpulse.Outcome.Slides: color = SlideColor; break;
                    default: color = SafeColor; break;
                }

                if (b.Result == CollapseImpulse.Outcome.Falls)
                {
                    if (item.Size == ItemSize.Big) BigFalls++;
                    else SmallFalls++;
                    // Düşen eşyanın oku kenardan biraz taşsın
                    if (delta.sqrMagnitude > 1e-4f) delta += delta.normalized * 0.6f;
                }

                if (delta.magnitude > MinArrow)
                {
                    var from = new Vector3(b.Position.x, y, b.Position.y);
                    var to = new Vector3(b.Position.x + delta.x, y, b.Position.y + delta.y);
                    DrawArrow(from, to, color, 0.35f);
                }

                Vector3 top = item.Body.worldCenterOfMass + Vector3.up * 0.9f;
                if (b.Result == CollapseImpulse.Outcome.Explodes)
                {
                    GasBlasts++;
                    SmallFalls++;
                    Labels.Add(new Label { World = top, Text = "PATLAR! bağla", Color = FallColor });
                }
                else if (b.Result == CollapseImpulse.Outcome.Falls)
                    Labels.Add(new Label { World = top, Text = item.Size == ItemSize.Big ? "DÜŞER  −1 CAN" : "DÜŞER", Color = FallColor });
                else if (b.Result == CollapseImpulse.Outcome.Hangs)
                    Labels.Add(new Label { World = top, Text = "ASILI KALIR", Color = SlideColor });
            }

            for (int i = 0; i < _ties.Count; i++)
            {
                var tie = _ties[i];
                var link = _tieLinks[i];
                if (tie.OtherBody >= 0) continue;
                Vector3 mid = (link.A.World + link.B.World) * 0.5f + Vector3.up * 0.25f;
                string text;
                Color color;
                if (tie.Snapped)
                {
                    Snaps++;
                    text = $"KOPAR  %{tie.PeakRatio * 100f:0}";
                    color = FallColor;
                }
                else if (tie.Released && tie.PeakRatio < 0.05f)
                {
                    text = $"{_tieRelease[i]} patlayınca boşa çıkar";
                    color = SlideColor;
                }
                else if (tie.PeakRatio < 0.05f)
                {
                    text = "yük yok";
                    color = IdleColor;
                }
                else
                {
                    text = tie.Released ? $"~%{tie.PeakRatio * 100f:0}, sonra {_tieRelease[i]}'de boşa çıkar" : $"~%{tie.PeakRatio * 100f:0}";
                    color = tie.PeakRatio < 0.6f ? SafeColor : SlideColor;
                }
                Labels.Add(new Label { World = mid, Text = text, Color = color });
            }

            for (int i = 0; i < _walls.Count; i++)
            {
                var w = _walls[i];
                if (float.IsPositiveInfinity(w.FallTime)) continue;
                bool outward = CollapseImpulse.WallFallsOutward(w.Outward, ctx, cfg);
                if (outward) OutwardWalls++;
                var piece = _wallPieces[i];
                Vector3 top = piece.WorldCenter + Vector3.up * (piece.SlotSize.y * 0.5f + 0.2f);
                Vector3 dir = new Vector3(w.Outward.x, 0f, w.Outward.y) * (outward ? 1f : -1f);
                DrawArrow(top - dir * 0.45f, top + dir * 0.55f, outward ? FallColor : SafeColor, 0.3f);
            }

            EstimatedPenalty = SmallFalls * FloorScorer.SmallItemPenalty + OutwardWalls * FloorScorer.PiecePenalty;
            HideLines(_usedLines);
        }

        private void DrawArrow(Vector3 from, Vector3 to, Color color, float head)
        {
            var line = NextLine();
            Vector3 dir = to - from;
            dir.y = 0f;
            dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward;
            Vector3 side = Vector3.Cross(Vector3.up, dir) * (head * 0.6f);
            Vector3 back = to - dir * head;
            line.SetPosition(0, from);
            line.SetPosition(1, to);
            line.SetPosition(2, back + side);
            line.SetPosition(3, to);
            line.SetPosition(4, back - side);
            line.sharedMaterial = GameMaterials.Get(color);
            line.enabled = true;
        }

        private LineRenderer NextLine()
        {
            if (_usedLines < _lines.Count) return _lines[_usedLines++];

            var go = new GameObject("PreviewArrow");
            go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.positionCount = 5;
            line.widthMultiplier = LineWidth;
            line.numCornerVertices = 2;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            _lines.Add(line);
            _usedLines++;
            return line;
        }

        private void HideLines(int from)
        {
            for (int i = from; i < _lines.Count; i++)
            {
                if (_lines[i] != null) _lines[i].enabled = false;
            }
        }
    }
}
