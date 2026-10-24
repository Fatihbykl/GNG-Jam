using UnityEngine;

namespace KatKat
{
    /// <summary>Kat başı ip bütçesi. Saf C#; EditMode testleriyle doğrulanır.</summary>
    public class RopeBudget
    {
        public float Total { get; private set; }
        public float Used { get; private set; }
        public float Remaining => Mathf.Max(0f, Total - Used);

        public RopeBudget(float total)
        {
            Total = Mathf.Max(0f, total);
        }

        public static float CostOf(float length, RopeTypeSO type) => length * type.CostPerMeter;

        public bool CanAfford(float cost) => cost <= Remaining + 0.0001f;

        public bool TrySpend(float cost)
        {
            if (cost < 0f || !CanAfford(cost)) return false;
            Used += cost;
            return true;
        }

        public void Refund(float cost)
        {
            Used = Mathf.Max(0f, Used - Mathf.Max(0f, cost));
        }

        /// <summary>Bir sonraki katın bütçesi: taban + devreden ip − ceza.</summary>
        public static float NextFloorBudget(float baseBudget, float carried, int penalty, float penaltyCost, float minBudget)
        {
            return Mathf.Max(minBudget, baseBudget + Mathf.Max(0f, carried) - penalty * penaltyCost);
        }
    }
}
