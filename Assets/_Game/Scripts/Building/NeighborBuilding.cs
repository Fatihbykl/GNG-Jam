using System.Collections.Generic;
using UnityEngine;

namespace KatKat
{
    /// <summary>Komşu bina. Çarpan her farklı Rigidbody büyük ceza sayılır.</summary>
    public class NeighborBuilding : MonoBehaviour
    {
        public static readonly HashSet<Rigidbody> HitBodies = new HashSet<Rigidbody>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => HitBodies.Clear();

        private void OnCollisionEnter(Collision collision)
        {
            if (collision.rigidbody != null) HitBodies.Add(collision.rigidbody);
        }
    }
}
