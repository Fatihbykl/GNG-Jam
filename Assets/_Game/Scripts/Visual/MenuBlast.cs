using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Oyundaki şarj patlaması efektini (<see cref="Vfx.Blast"/>) oyun dışı sahnelerde, ör. ana menüde oynatır.
    /// Objenin konumunda patlar; aralık 0'dan büyükse belirli aralıklarla tekrarlar. <see cref="Play"/> bir butona da bağlanabilir.
    /// </summary>
    public class MenuBlast : MonoBehaviour
    {
        [Tooltip("Parçacık materyali için. Boşsa varsayılan materyal kullanılır.")]
        public GameContentSO Content;
        public bool PlayOnStart = true;
        [Tooltip("Saniye; 0 = tekrar yok.")]
        public float RepeatInterval = 3f;
        [Tooltip("Her patlamada konuma eklenen rastgele sapma (m).")]
        public float RandomRadius = 0f;

        private float _next;

        private void Start()
        {
            if (Content != null) Vfx.Init(Content);
            if (PlayOnStart) Play();
            _next = Time.time + RepeatInterval;
        }

        private void Update()
        {
            if (RepeatInterval <= 0f || Time.time < _next) return;
            _next = Time.time + RepeatInterval;
            Play();
        }

        public void Play()
        {
            Vector3 pos = transform.position + Random.insideUnitSphere * RandomRadius;
            Vfx.Blast(pos, null, pos.y);
        }
    }
}
