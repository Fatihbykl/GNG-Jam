using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Oyuncunun ayarladığı ateşleme sırası. Gecikmeler scaled zamanla beklenir;
    /// ağır çekim sırayı da yavaşlatır.
    /// </summary>
    public class DetonationSequence : MonoBehaviour
    {
        public readonly List<ExplosiveCharge> Order = new List<ExplosiveCharge>();

        public bool IsPlaying { get; private set; }
        public int FiredCount { get; private set; }

        /// <summary>Şarj patlamadan hemen önce (kolonlar henüz yerindeyken) tetiklenir.</summary>
        public event Action<ExplosiveCharge> ChargeFiring;

        /// <summary>Şarjın, sıra başladıktan kaç saniye sonra patlayacağı.</summary>
        public float FireTimeOf(ExplosiveCharge charge)
        {
            float t = 0f;
            foreach (var c in Order)
            {
                t += c.Delay;
                if (c == charge) return t;
            }
            return -1f;
        }

        private Coroutine _routine;

        public void SetCharges(IEnumerable<ExplosiveCharge> charges)
        {
            Stop();
            Order.Clear();
            Order.AddRange(charges);
            FiredCount = 0;
        }

        public void Move(int index, int direction)
        {
            int target = index + direction;
            if (IsPlaying || index < 0 || index >= Order.Count || target < 0 || target >= Order.Count) return;
            (Order[index], Order[target]) = (Order[target], Order[index]);
        }

        public void Play(GameConfig config, GameObject vfxPrefab, Action onFinished)
        {
            Stop();
            _routine = StartCoroutine(Run(config, vfxPrefab, onFinished));
        }

        public void Stop()
        {
            if (_routine != null) StopCoroutine(_routine);
            _routine = null;
            IsPlaying = false;
        }

        private IEnumerator Run(GameConfig config, GameObject vfxPrefab, Action onFinished)
        {
            IsPlaying = true;
            FiredCount = 0;
            foreach (var charge in Order)
            {
                if (charge.Delay > 0f) yield return new WaitForSeconds(charge.Delay);
                ChargeFiring?.Invoke(charge);
                charge.Detonate(config, vfxPrefab);
                FiredCount++;
            }

            IsPlaying = false;
            _routine = null;
            onFinished?.Invoke();
        }
    }
}
