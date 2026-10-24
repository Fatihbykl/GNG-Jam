using System.Collections.Generic;
using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Seed'den bir kat üretir: kolon ızgarası, tavan döşemeleri, dış duvarlar, zemin halkaları,
    /// eşyalar ve kolon sıralarına bağlı patlayıcılar. Aynı seed her zaman aynı katı verir;
    /// hızlı sıfırlama buna dayanıyor. Parçalar <see cref="GameContentSO"/> prefablarından kurulur;
    /// prefab atanmamış parçalar için prototip küpü kullanılır.
    /// </summary>
    public static class FloorGenerator
    {
        private const float EdgeInset = 0.6f;
        private const float RingRadius = 0.3f;

        public static Floor Build(Transform parent, int index, int floorNumber, int seed, float baseY, GameConfig cfg,
            GameContentSO content)
        {
            var rng = new System.Random(seed);
            var diff = FloorDifficulty.For(floorNumber, cfg);

            var root = new GameObject($"Floor_{index}");
            root.transform.SetParent(parent, false);
            var floor = root.AddComponent<Floor>();
            floor.Index = index;
            floor.Seed = seed;
            floor.BaseY = baseY;
            floor.Content = content;
            floor.Footprint = new Rect(-cfg.FloorWidth * 0.5f, -cfg.FloorDepth * 0.5f, cfg.FloorWidth, cfg.FloorDepth);

            var columns = BuildColumns(floor, cfg, PickBearing(cfg, rng, diff.BearingRatio));
            BuildCeiling(floor, cfg, columns);
            BuildWalls(floor, cfg, columns, rng);

            var occupied = new List<Vector3>(); // xz + yarıçap (y bileşeninde)
            foreach (var c in floor.Columns) occupied.Add(new Vector3(c.WorldCenter.x, 0.45f, c.WorldCenter.z));

            BuildRings(floor, cfg, rng, diff.RingAnchors, occupied);
            BuildItems(floor, rng, diff, floorNumber, occupied);
            BuildCharges(floor, cfg, rng, columns);

            floor.SetSimulated(false);
            return floor;
        }

        /// <summary>Şarj konmayacak (taşıyıcı) kolonları rastgele seçer.</summary>
        private static bool[,] PickBearing(GameConfig cfg, System.Random rng, float ratio)
        {
            int nx = cfg.gridX + 1, nz = cfg.gridZ + 1, total = nx * nz;
            int count = Mathf.Clamp(Mathf.RoundToInt(total * ratio), 0, total - 1);

            var indices = new List<int>(total);
            for (int i = 0; i < total; i++) indices.Add(i);
            for (int i = total - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (indices[i], indices[j]) = (indices[j], indices[i]);
            }

            var bearing = new bool[nx, nz];
            for (int k = 0; k < count; k++) bearing[indices[k] / nz, indices[k] % nz] = true;
            return bearing;
        }

        private static StructuralPiece[,] BuildColumns(Floor floor, GameConfig cfg, bool[,] bearing)
        {
            var fp = floor.Footprint;
            float h = cfg.floorHeight - cfg.slabThickness;
            var grid = new StructuralPiece[cfg.gridX + 1, cfg.gridZ + 1];
            Vector2 center = fp.center;

            for (int i = 0; i <= cfg.gridX; i++)
            for (int j = 0; j <= cfg.gridZ; j++)
            {
                bool isBearing = bearing[i, j];
                var pos = new Vector3(fp.xMin + i * cfg.cellSize, floor.BaseY + h * 0.5f, fp.yMin + j * cfg.cellSize);
                var slot = new Vector3(cfg.columnSize, h, cfg.columnSize);
                var content = floor.Content;
                var prefab = isBearing ? content.ResolveBearingColumn() : content.ColumnPrefab;
                var go = SpawnStructure(floor, prefab, isBearing ? $"BearingColumn_{i}_{j}" : $"Column_{i}_{j}", pos,
                    Quaternion.identity, slot, isBearing ? GameMaterials.BearingColumnColor : GameMaterials.ColumnColor);
                if (isBearing && prefab != null && content.BearingColumnPrefab == null) TintBearing(go);
                var piece = AddPiece(floor, go, PieceKind.Column, cfg.columnMass, pos, slot);
                piece.IsBearing = isBearing;
                grid[i, j] = piece;
                floor.Columns.Add(piece);

                // Zemine bağlı (connectedBody = null → dünya). Taşıyıcı kolonun tabanı çok daha sağlam.
                float baseScale = isBearing ? cfg.bearingBaseJointScale : 1f;
                StructuralPiece.Join(piece, null, null, cfg.jointBreakForce * baseScale, cfg.jointBreakTorque * baseScale);

                // İç tarafa bakan ankraj
                Vector3 inward = new Vector3(center.x - pos.x, 0f, center.y - pos.z).normalized;
                Vector3 anchorPos = new Vector3(pos.x, floor.BaseY + 1.2f, pos.z) + inward * (cfg.columnSize * 0.5f + 0.08f);
                var anchor = AnchorPoint.Create(AnchorKind.Column, go.transform, anchorPos, Quaternion.LookRotation(inward),
                    floor.Anchors.Count, floor.Content.ColumnAnchorPrefab);
                floor.Anchors.Add(anchor);
            }

            return grid;
        }

        private static void BuildCeiling(Floor floor, GameConfig cfg, StructuralPiece[,] columns)
        {
            var fp = floor.Footprint;
            var tiles = new StructuralPiece[cfg.gridX, cfg.gridZ];
            float y = floor.BaseY + cfg.floorHeight - cfg.slabThickness * 0.5f;

            for (int i = 0; i < cfg.gridX; i++)
            for (int j = 0; j < cfg.gridZ; j++)
            {
                var pos = new Vector3(fp.xMin + (i + 0.5f) * cfg.cellSize, y, fp.yMin + (j + 0.5f) * cfg.cellSize);
                var go = SpawnStructure(floor, floor.Content.CeilingPrefab, $"Ceiling_{i}_{j}", pos, Quaternion.identity,
                    new Vector3(cfg.cellSize, cfg.slabThickness, cfg.cellSize), GameMaterials.Concrete);
                var tile = AddPiece(floor, go, PieceKind.Ceiling, cfg.slabMass, pos,
                    new Vector3(cfg.cellSize, cfg.slabThickness, cfg.cellSize));
                tiles[i, j] = tile;
                floor.CeilingTiles.Add(tile);

                JoinToColumn(tile, columns[i, j], cfg);
                JoinToColumn(tile, columns[i + 1, j], cfg);
                JoinToColumn(tile, columns[i, j + 1], cfg);
                JoinToColumn(tile, columns[i + 1, j + 1], cfg);
                if (i > 0) JoinPieces(tile, tiles[i - 1, j], cfg);
                if (j > 0) JoinPieces(tile, tiles[i, j - 1], cfg);
            }
        }

        private static void BuildWalls(Floor floor, GameConfig cfg, StructuralPiece[,] columns, System.Random rng)
        {
            var fp = floor.Footprint;
            float h = cfg.floorHeight - cfg.slabThickness;
            float len = cfg.cellSize - cfg.columnSize;
            float y = floor.BaseY + h * 0.5f;
            var size = new Vector3(len, h, cfg.wallThickness); // yerel: X boy, Y yükseklik, Z kalınlık (+Z dışarı)

            // X yönündeki kenarlar (ön ve arka)
            for (int i = 0; i < cfg.gridX; i++)
            {
                float x = fp.xMin + (i + 0.5f) * cfg.cellSize;
                TryWall(floor, cfg, rng, new Vector3(x, y, fp.yMin), size, Vector3.back, columns[i, 0], columns[i + 1, 0], FindTile(floor, i, 0, cfg));
                TryWall(floor, cfg, rng, new Vector3(x, y, fp.yMax), size, Vector3.forward, columns[i, cfg.gridZ], columns[i + 1, cfg.gridZ], FindTile(floor, i, cfg.gridZ - 1, cfg));
            }

            // Z yönündeki kenarlar (sol ve sağ)
            for (int j = 0; j < cfg.gridZ; j++)
            {
                float z = fp.yMin + (j + 0.5f) * cfg.cellSize;
                TryWall(floor, cfg, rng, new Vector3(fp.xMin, y, z), size, Vector3.left, columns[0, j], columns[0, j + 1], FindTile(floor, 0, j, cfg));
                TryWall(floor, cfg, rng, new Vector3(fp.xMax, y, z), size, Vector3.right, columns[cfg.gridX, j], columns[cfg.gridX, j + 1], FindTile(floor, cfg.gridX - 1, j, cfg));
            }
        }

        private static StructuralPiece FindTile(Floor floor, int i, int j, GameConfig cfg)
        {
            return floor.CeilingTiles[i * cfg.gridZ + j];
        }

        private static void TryWall(Floor floor, GameConfig cfg, System.Random rng, Vector3 pos, Vector3 size,
            Vector3 outward, StructuralPiece colA, StructuralPiece colB, StructuralPiece tile)
        {
            if (rng.NextDouble() < cfg.openEdgeChance) return;

            var go = SpawnStructure(floor, floor.Content.WallPrefab, "Wall", pos, Quaternion.LookRotation(outward), size,
                GameMaterials.WallColor);
            var wall = AddPiece(floor, go, PieceKind.Wall, cfg.wallMass, pos, size);
            wall.LocalOutward = Vector3.forward;
            floor.Walls.Add(wall);
            JoinToColumn(wall, colA, cfg);
            JoinToColumn(wall, colB, cfg);
            JoinPieces(wall, tile, cfg);
        }

        private static void BuildRings(Floor floor, GameConfig cfg, System.Random rng, int count, List<Vector3> occupied)
        {
            var fp = floor.Footprint;
            for (int n = 0; n < count; n++)
            {
                if (!TryFindSpot(fp, rng, RingRadius, occupied, out Vector3 spot)) continue;
                var anchor = AnchorPoint.Create(AnchorKind.FloorRing, floor.transform,
                    new Vector3(spot.x, floor.BaseY + 0.08f, spot.z), Quaternion.identity, floor.Anchors.Count,
                    floor.Content.FloorRingPrefab);
                floor.Anchors.Add(anchor);
            }
        }

        private static void BuildItems(Floor floor, System.Random rng, FloorDifficulty diff, int floorNumber,
            List<Vector3> occupied)
        {
            var extra = new List<Rigidbody>();

            void Spawn(ItemDefinitionSO def)
            {
                if (def == null) return;
                float radius = def.ResolveFootprintRadius();
                if (!TryFindSpot(floor.Footprint, rng, radius, occupied, out Vector3 spot)) return;
                float yaw = rng.Next(0, 4) * 90f;
                extra.Clear();
                var item = ItemCatalog.Build(def, floor.transform, new Vector3(spot.x, floor.BaseY, spot.z), yaw, extra);
                item.Index = floor.Items.Count;
                floor.Items.Add(item);
                floor.Bodies.Add(item.Body);
                floor.Bodies.AddRange(extra);
            }

            var content = floor.Content;
            for (int i = 0; i < diff.BigItems; i++) Spawn(content.Pick(ItemSize.Big, floorNumber, rng));
            for (int i = 0; i < diff.SmallItems; i++) Spawn(content.Pick(ItemSize.Small, floorNumber, rng));
        }

        private static void BuildCharges(Floor floor, GameConfig cfg, System.Random rng, StructuralPiece[,] columns)
        {
            // Patlayıcılar bir eksen boyunca kolon sıralarına yerleşir; sıra, çöküş yönünü belirler.
            bool alongX = rng.NextDouble() < 0.5;
            int groups = alongX ? cfg.gridX + 1 : cfg.gridZ + 1;
            int perGroup = alongX ? cfg.gridZ + 1 : cfg.gridX + 1;

            for (int g = 0; g < groups; g++)
            {
                var charged = new List<StructuralPiece>();
                for (int k = 0; k < perGroup; k++)
                {
                    var column = alongX ? columns[g, k] : columns[k, g];
                    if (!column.IsBearing) charged.Add(column);
                }
                if (charged.Count == 0) continue; // sıranın tamamı taşıyıcı

                var go = new GameObject($"Charge_{g}");
                go.transform.SetParent(floor.transform, false);
                var charge = go.AddComponent<ExplosiveCharge>();
                charge.Label = $"{(alongX ? "X" : "Z")}{g + 1}";
                charge.Floor = floor;
                foreach (var column in charged)
                {
                    charge.AddColumn(column, floor.Content.ChargeMarkerPrefab);
                    column.Charge = charge;
                }
                floor.Charges.Add(charge);
            }
        }

        private static bool TryFindSpot(Rect fp, System.Random rng, float radius, List<Vector3> occupied, out Vector3 spot)
        {
            for (int attempt = 0; attempt < 40; attempt++)
            {
                float x = Mathf.Lerp(fp.xMin + EdgeInset + radius, fp.xMax - EdgeInset - radius, (float)rng.NextDouble());
                float z = Mathf.Lerp(fp.yMin + EdgeInset + radius, fp.yMax - EdgeInset - radius, (float)rng.NextDouble());
                bool free = true;
                foreach (var o in occupied)
                {
                    float dx = o.x - x, dz = o.z - z, r = o.y + radius;
                    if (dx * dx + dz * dz < r * r)
                    {
                        free = false;
                        break;
                    }
                }

                if (!free) continue;
                spot = new Vector3(x, 0f, z);
                occupied.Add(new Vector3(x, radius, z));
                return true;
            }

            spot = default;
            return false;
        }

        private static StructuralPiece AddPiece(Floor floor, GameObject go, PieceKind kind, float mass, Vector3 slotCenter,
            Vector3 slotSize)
        {
            var rb = PrefabPlacement.EnsureBody(go, mass, continuous: false);
            var piece = PrefabPlacement.GetOrAdd<StructuralPiece>(go);
            piece.Kind = kind;
            piece.SlotSize = slotSize;
            piece.LocalCenter = go.transform.InverseTransformPoint(slotCenter);
            floor.Pieces.Add(piece);
            floor.Bodies.Add(rb);
            return piece;
        }

        private static GameObject SpawnStructure(Floor floor, GameObject prefab, string name, Vector3 center,
            Quaternion rotation, Vector3 size, Color fallbackColor)
        {
            if (prefab != null)
                return PrefabPlacement.Place(prefab, name, floor.transform, center, rotation, size, floor.Content.FitStructureToSlot);

            var go = GameMaterials.CreateBox(name, floor.transform, center, size, fallbackColor);
            go.transform.rotation = rotation;
            return go;
        }

        /// <summary>Ayrı taşıyıcı prefabı yoksa normal kolon modelini koyu renge boyar.</summary>
        private static void TintBearing(GameObject go)
        {
            int baseColor = Shader.PropertyToID("_BaseColor");
            var tint = new Color(0.45f, 0.55f, 0.8f);
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                foreach (var m in r.materials)
                {
                    if (m.HasProperty(baseColor)) m.SetColor(baseColor, m.GetColor(baseColor) * tint);
                }
            }
        }

        private static void JoinPieces(StructuralPiece a, StructuralPiece b, GameConfig cfg)
        {
            StructuralPiece.Join(a, b.Body, b, cfg.jointBreakForce, cfg.jointBreakTorque);
        }

        /// <summary>
        /// Taşıyıcı kolona bağlantı zayıftır: çevresi çökünce tavan/duvar kolondan kopar ve kolon ayakta kalır.
        /// </summary>
        private static void JoinToColumn(StructuralPiece a, StructuralPiece column, GameConfig cfg)
        {
            float scale = column.IsBearing ? cfg.bearingTopJointScale : 1f;
            StructuralPiece.Join(a, column.Body, column, cfg.jointBreakForce * scale, cfg.jointBreakTorque * scale);
        }
    }
}
