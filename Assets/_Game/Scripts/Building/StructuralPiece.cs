using System.Collections.Generic;
using UnityEngine;

namespace KatKat
{
    public enum PieceKind
    {
        Column,
        Ceiling,
        Wall,
        Prop
    }

    /// <summary>
    /// Binanın modüler parçası. Kendi eklemlerini ve kendisine bağlanan eklemleri takip eder,
    /// böylece patlamada tüm bağlantıları tek seferde kesebiliriz.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class StructuralPiece : MonoBehaviour
    {
        public PieceKind Kind;
        [Tooltip("Duvarlar için binanın dışına bakan yön (yerel uzayda).")]
        public Vector3 LocalOutward;
        [Tooltip("Kolonlar için: şarj yok, patlamaz.")]
        public bool IsBearing;
        /// <summary>Kolonlar için: bu kolonu kesen şarj (taşıyıcıysa null).</summary>
        public ExplosiveCharge Charge;

        public Rigidbody Body { get; private set; }
        public Renderer MainRenderer { get; private set; }

        private readonly List<Joint> _joints = new List<Joint>();

        private void Awake()
        {
            Body = GetComponent<Rigidbody>();
            MainRenderer = GetComponent<Renderer>();
        }

        public Vector3 WorldOutward => transform.TransformDirection(LocalOutward);

        public static FixedJoint Join(StructuralPiece a, Rigidbody b, StructuralPiece bPiece, float breakForce,
            float breakTorque)
        {
            var joint = a.gameObject.AddComponent<FixedJoint>();
            joint.connectedBody = b;
            joint.breakForce = breakForce;
            joint.breakTorque = breakTorque;
            joint.enablePreprocessing = false;
            a._joints.Add(joint);
            if (bPiece != null) bPiece._joints.Add(joint);
            return joint;
        }

        /// <summary>Bu parçaya dokunan tüm eklemleri yok eder.</summary>
        public void DetachAll()
        {
            foreach (var joint in _joints)
            {
                if (joint != null) Destroy(joint);
            }
            _joints.Clear();
        }
    }
}
