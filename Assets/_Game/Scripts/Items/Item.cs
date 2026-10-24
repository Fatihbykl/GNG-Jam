using UnityEngine;

namespace KatKat
{
    public enum ItemSize
    {
        Small,
        Big
    }

    /// <summary>
    /// Kattaki eşya. Büyük eşyalar güvenli bölgeden çıkarsa can gider.
    /// Bağlandıkça hafifçe parlar (GDD: "bağlanmış eşyalar hafif parlıyor").
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class Item : MonoBehaviour
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        public string DisplayName;
        public ItemDefinitionSO Definition;
        public ItemSize Size;
        public bool CanBeTied = true;
        public int Index;

        public Rigidbody Body => _body != null ? _body : _body = GetComponent<Rigidbody>();
        public bool IsTied => _tieCount > 0;

        private Rigidbody _body;
        private int _tieCount;
        private Renderer[] _renderers;
        private MaterialPropertyBlock _block;

        private void Awake()
        {
            _block = new MaterialPropertyBlock();
            RefreshRenderers();
        }

        /// <summary>Parlama için renderer listesini yeniler (gevşek parçalar ayrıldıktan sonra çağrılır).</summary>
        public void RefreshRenderers() => _renderers = GetComponentsInChildren<Renderer>();

        public void AddTie()
        {
            _tieCount++;
            RefreshGlow();
        }

        public void RemoveTie()
        {
            _tieCount = Mathf.Max(0, _tieCount - 1);
            RefreshGlow();
        }

        private void RefreshGlow()
        {
            foreach (var r in _renderers)
            {
                if (r == null || r.sharedMaterial == null || !r.sharedMaterial.HasProperty(BaseColorId)) continue;
                Color baseColor = r.sharedMaterial.GetColor(BaseColorId);
                r.GetPropertyBlock(_block);
                _block.SetColor(BaseColorId, IsTied ? Color.Lerp(baseColor, new Color(0.6f, 1f, 0.6f), 0.45f) : baseColor);
                r.SetPropertyBlock(_block);
            }
        }
    }
}
