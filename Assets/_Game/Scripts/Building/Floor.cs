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
        public readonly List<AnchorPoint> Anchors = new List<AnchorPoint>();
        public readonly List<ExplosiveCharge> Charges = new List<ExplosiveCharge>();

        public bool IsSimulated { get; private set; }

        private MaterialPropertyBlock _block;

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
