using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Kırılan bir yapı parçasından kopan parça ya da kolon molozu. Puanlamada aynı gruptaki parçalar tek parça sayılır.
    /// Grup, kaynak parçanın kimliği; kaynak yok edilse de geçerli kalır.
    /// </summary>
    public class Fragment : MonoBehaviour
    {
        public int Group;

        private void OnCollisionEnter(Collision collision) => AudioDirector.Impact(Sound.DebrisImpact, collision);

        public static void Mark(GameObject go, StructuralPiece source) => go.AddComponent<Fragment>().Group = source.GetInstanceID();

        /// <summary>Parçanın da bölündüğü durumda (ör. kırılan tuğla) yeni parçaları aynı gruba katar.</summary>
        public static void MarkGroup(GameObject go, int group) => go.AddComponent<Fragment>().Group = group;
    }
}
