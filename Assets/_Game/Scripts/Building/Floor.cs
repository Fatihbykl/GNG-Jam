using System.Collections.Generic;
using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Tek bir katın çalışma zamanı verisi. Kat, alt katın tavanı üstünde durur;
    /// kendi tavanı da bir üst katın zeminidir.
    /// </summary>
    public class Floor : MonoBehaviour
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        public int Index;
        public int Seed;
        /// <summary>Katın zemin yüzeyi (alt katın tavanının üstü).</summary>
        public float BaseY;
        public Rect Footprint;
        public GameContentSO Content;

        public readonly List<Rigidbody> Bodies = new List<Rigidbody>();
        public readonly List<StructuralPiece> Pieces = new List<StructuralPiece>();
        public readonly List<StructuralPiece> Columns = new List<StructuralPiece>();
        public readonly List<StructuralPiece> CeilingTiles = new List<StructuralPiece>();
        public readonly List<StructuralPiece> Walls = new List<StructuralPiece>();
        public readonly List<Item> Items = new List<Item>();
        /// <summary>Eşyaların gevşek parçaları (ör. kitaplar); puanlamada sayılmaz.</summary>
        public readonly HashSet<Rigidbody> ItemParts = new HashSet<Rigidbody>();
        public readonly List<AnchorPoint> Anchors = new List<AnchorPoint>();
        public readonly List<ExplosiveCharge> Charges = new List<ExplosiveCharge>();

        public bool IsSimulated { get; private set; }
        /// <summary>Bu katta kırık versiyonuna bölünen tuğla sayısı (bkz. <see cref="ShardBreaker"/>).</summary>
        public int BrokenShards { get; set; }

        private MaterialPropertyBlock _block;
        private readonly List<Renderer> _hiddenRenderers = new List<Renderer>();
        private readonly List<Collider> _hiddenColliders = new List<Collider>();

        public void SetSimulated(bool simulated)
        {
            IsSimulated = simulated;
            foreach (var rb in Bodies)
            {
                if (rb == null) continue;
                rb.isKinematic = !simulated;
                if (simulated) rb.WakeUp();
            }
        }

        /// <summary>Bekleyen (alt) katlar loş görünür.</summary>
        public void SetDimmed(bool dimmed)
        {
            if (_block == null) _block = new MaterialPropertyBlock();
            foreach (var r in GetComponentsInChildren<Renderer>())
            {
                if (r.sharedMaterial == null || !r.sharedMaterial.HasProperty(BaseColorId)) continue;
                Color c = r.sharedMaterial.GetColor(BaseColorId);
                r.GetPropertyBlock(_block);
                _block.SetColor(BaseColorId, dimmed ? c * 0.45f : c);
                r.SetPropertyBlock(_block);
            }
        }

        /// <summary>
        /// Bekleyen alt katın yalnızca tavanı (aktif katın zemini) kalır; kolon, duvar, eşya ve halkalar gizlenir.
        /// Zaten kapalı olanlara (ör. kırık parçalar) dokunulmaz, açılınca yalnızca gizlenenler geri gelir.
        /// </summary>
        public void SetOnlyCeiling(bool only)
        {
            if (!only)
            {
                foreach (var r in _hiddenRenderers) if (r != null) r.enabled = true;
                foreach (var c in _hiddenColliders) if (c != null) c.enabled = true;
                _hiddenRenderers.Clear();
                _hiddenColliders.Clear();
                return;
            }
            if (_hiddenRenderers.Count > 0 || _hiddenColliders.Count > 0) return;

            var ceiling = new HashSet<Transform>();
            foreach (var tile in CeilingTiles) if (tile != null) ceiling.Add(tile.transform);

            foreach (var r in GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || IsUnder(r.transform, ceiling)) continue;
                r.enabled = false;
                _hiddenRenderers.Add(r);
            }
            foreach (var c in GetComponentsInChildren<Collider>())
            {
                if (!c.enabled || IsUnder(c.transform, ceiling)) continue;
                c.enabled = false;
                _hiddenColliders.Add(c);
            }
        }

        private bool IsUnder(Transform t, HashSet<Transform> roots)
        {
            for (; t != null && t != transform; t = t.parent)
            {
                if (roots.Contains(t)) return true;
            }
            return false;
        }

        /// <summary>Planlamada tavan görünmez ama gölge düşürmeye devam eder (bebek evi kesiti).</summary>
        public void SetCeilingCutaway(bool cutaway)
        {
            foreach (var tile in CeilingTiles)
            {
                if (tile != null) tile.SetCutaway(cutaway);
            }
        }
    }
}
