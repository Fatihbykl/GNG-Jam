using System;
using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Taşıyıcı kolonun hasarı. Taşıyıcı kolon patlamaz ve ankrajı güvenilir kalmalı; ama etrafı çökerken dimdik
    /// durması da gerçekçi değil. Bu yüzden:
    /// <list type="bullet">
    /// <item>Yakındaki şarj patlayınca ya da molozun sert çarpmasıyla tepesinden tuğla döker. Dökülme ankrajın
    /// biraz üstünde durur, yani ip hep tutunacak yer bulur.</item>
    /// <item>Tabanı kopup belli bir açıdan fazla yatarsa tamamen tuğlalarına dağılır ve ona bağlı ipler boşa çıkar
    /// (<see cref="Toppled"/>).</item>
    /// </list>
    /// Yalnızca sağlam/kırık çifti olan kolon modellerinde çalışır (bkz. <see cref="Fracturable.FindFracturedSet"/>).
    /// </summary>
    [RequireComponent(typeof(Fracturable))]
    public class BearingCrumble : MonoBehaviour
    {
        /// <summary>İpi boşa çıkaran nedenin adı (sonuç ekranı için).</summary>
        public const string ReleaseLabel = "devrilen taşıyıcı";
        private const float HitCooldown = 0.15f;

        /// <summary>Kolon devrilip dağıldı; ona bağlı ipler boşa çıkmalı.</summary>
        public static event Action<StructuralPiece> Toppled;

        private StructuralPiece _piece;
        private Fracturable _fracturable;
        private GameConfig _cfg;
        private float _cutY = float.PositiveInfinity;
        private float _lastHit = float.NegativeInfinity;

        public static BearingCrumble Add(StructuralPiece piece, Fracturable fracturable, GameConfig cfg)
        {
            var c = PrefabPlacement.GetOrAdd<BearingCrumble>(piece.gameObject);
            c._piece = piece;
            c._fracturable = fracturable;
            c._cfg = cfg;
            return c;
        }

        private float Top => _piece.WorldCenter.y + _piece.SlotSize.y * 0.5f;

        /// <summary>Dökülmenin inebileceği en alt yükseklik: ankrajın biraz üstü (ankraj yoksa kolonun ortası).</summary>
        private float MinCutY
        {
            get
            {
                var anchor = GetComponentInChildren<AnchorPoint>();
                return anchor != null
                    ? anchor.transform.position.y + _cfg.bearingKeepAboveAnchor
                    : _piece.WorldCenter.y;
            }
        }

        /// <summary>Yakındaki şarj patladı: uzaklığa göre tepeden bir miktar tuğla döker.</summary>
        public void OnBlast(Vector3 origin)
        {
            if (!CanCrumble) return;
            Vector3 d = _piece.WorldCenter - origin;
            d.y = 0f;
            float k = 1f - d.magnitude / _cfg.bearingShedBlastRadius;
            if (k <= 0f) return;
            Shed(_cfg.bearingShedDepth * k, origin);
        }

        private bool CanCrumble => _piece != null && !_fracturable.IsShattered && !_piece.Body.isKinematic;

        private void Shed(float depth, Vector3 origin)
        {
            float current = Mathf.Min(_cutY, Top);
            float cut = Mathf.Max(MinCutY, current - depth);
            if (cut >= current - 0.05f) return;
            _cutY = cut;
            _fracturable.ShedAbove(cut, _cfg.bearingShedRagged, origin, _cfg.bearingShedKick);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!CanCrumble || collision.rigidbody == null || Time.time - _lastHit < HitCooldown) return;
            if (collision.relativeVelocity.magnitude < _cfg.bearingShedSpeed) return;
            _lastHit = Time.time;
            var contact = collision.GetContact(0);
            // Çarpma noktasının üstü dökülür; tepeye yakın darbe az, aşağıdan darbe daha çok götürür.
            float current = Mathf.Min(_cutY, Top);
            float depth = Mathf.Max(_cfg.bearingShedPerHit, current - contact.point.y);
            Shed(Mathf.Min(depth, _cfg.bearingShedDepth), contact.point);
        }

        private void FixedUpdate()
        {
            if (!CanCrumble) return;
            if (Vector3.Angle(transform.up, Vector3.up) < _cfg.bearingToppleAngle) return;

            foreach (var anchor in GetComponentsInChildren<AnchorPoint>(true)) Destroy(anchor.gameObject);
            Toppled?.Invoke(_piece);
            _fracturable.Shatter(null, 0f);
        }
    }
}
