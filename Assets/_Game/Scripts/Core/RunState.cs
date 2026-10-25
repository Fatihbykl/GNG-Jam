using UnityEngine;

namespace KatKat
{
    /// <summary>Bir oturum (run) boyunca katlar arasında taşınan durum.</summary>
    public class RunState
    {
        public int Seed;
        public int Lives;
        public int FloorsDescended;
        public float CarriedRope;
        public int TotalPenalty;

        public RunState(GameConfig config, int seed)
        {
            Seed = seed;
            Lives = config.startLives;
            FloorsDescended = 0;
            CarriedRope = 0f;
            TotalPenalty = 0;
        }

        public int FloorSeed(int floorIndex) => Seed * 7919 + floorIndex * 104729;
    }

    /// <summary>Kat numarasına göre zorluk değerleri.</summary>
    public struct FloorDifficulty
    {
        public int BigItems;
        public int SmallItems;
        public int RingAnchors;
        public float BaseBudget;
        public float BearingRatio;

        public static FloorDifficulty For(int floorNumber, GameConfig config)
        {
            return new FloorDifficulty
            {
                BearingRatio = Mathf.Max(config.minBearingRatio,
                    config.bearingRatio - config.bearingRatioDecreasePerFloor * floorNumber),
                BigItems = Mathf.Min(1 + floorNumber / 2, 4),
                SmallItems = Mathf.Min(2 + floorNumber, 8),
                RingAnchors = Mathf.Max(4 - floorNumber / 3, 2),
                BaseBudget = Mathf.Max(config.minBudget, config.baseBudget - config.budgetDecreasePerFloor * floorNumber)
            };
        }
    }
}
