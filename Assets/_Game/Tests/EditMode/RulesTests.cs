using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace KatKat.Tests
{
    public class RopeBudgetTests
    {
        [Test]
        public void Spend_And_Refund_Track_Remaining()
        {
            var budget = new RopeBudget(10f);
            Assert.IsTrue(budget.TrySpend(4f));
            Assert.AreEqual(6f, budget.Remaining, 1e-4f);
            budget.Refund(4f);
            Assert.AreEqual(10f, budget.Remaining, 1e-4f);
        }

        [Test]
        public void Cannot_Overspend()
        {
            var budget = new RopeBudget(5f);
            Assert.IsFalse(budget.TrySpend(5.5f));
            Assert.AreEqual(5f, budget.Remaining, 1e-4f);
        }

        [Test]
        public void Refund_Never_Goes_Above_Total()
        {
            var budget = new RopeBudget(5f);
            budget.Refund(3f);
            Assert.AreEqual(5f, budget.Remaining, 1e-4f);
        }

        [Test]
        public void Cost_Scales_With_Type()
        {
            var steel = RopeTypeSO.Create("steel", 2.5f, 1f, 1f, 0f, 1f, 0.1f, Color.gray);
            Assert.AreEqual(10f, RopeBudget.CostOf(4f, steel), 1e-4f);
            Object.DestroyImmediate(steel);
        }

        [Test]
        public void Next_Budget_Adds_Carry_Subtracts_Penalty_And_Clamps()
        {
            Assert.AreEqual(23f, RopeBudget.NextFloorBudget(20f, 5f, 4, 0.5f, 10f), 1e-4f);
            Assert.AreEqual(10f, RopeBudget.NextFloorBudget(12f, 0f, 20, 0.5f, 10f), 1e-4f);
        }
    }

    public class FloorScorerTests
    {
        [Test]
        public void Big_Item_Outside_Costs_A_Life()
        {
            var r = FloorScorer.Tally(new List<BodyOutcome>
            {
                new BodyOutcome { IsItem = true, IsBig = true, Inside = false },
                new BodyOutcome { IsItem = true, IsBig = true, Inside = true },
            });
            Assert.AreEqual(1, r.LivesLost);
            Assert.AreEqual(1, r.SavedItems);
            Assert.AreEqual(0, r.Penalty);
        }

        [Test]
        public void Penalties_Follow_GDD_Weights()
        {
            var r = FloorScorer.Tally(new List<BodyOutcome>
            {
                new BodyOutcome { Inside = false },
                new BodyOutcome { Inside = false, HitNeighbor = true },
                new BodyOutcome { IsItem = true, Inside = false },
                new BodyOutcome { Inside = true },
            });
            Assert.AreEqual(2, r.OutPieces);
            Assert.AreEqual(1, r.OutSmallItems);
            Assert.AreEqual(1, r.NeighborHits);
            Assert.AreEqual(2 * FloorScorer.PiecePenalty + FloorScorer.SmallItemPenalty + FloorScorer.NeighborPenalty,
                r.Penalty);
            Assert.AreEqual(0, r.LivesLost);
        }
    }

    public class CollapsePreviewTests
    {
        [Test]
        public void Collapses_Toward_Side_That_Fires_First()
        {
            var charged = new List<(Vector2, float)>
            {
                (new Vector2(4f, 0f), 0.2f),
                (new Vector2(-4f, 0f), 1.8f),
            };
            Vector2 dir = CollapsePreview.Estimate(charged, new List<Vector2>(), out float strength);
            Assert.Greater(dir.x, 0.9f);
            Assert.Greater(strength, 0.3f);
        }

        [Test]
        public void Simultaneous_Symmetric_Charges_Collapse_To_Center()
        {
            var charged = new List<(Vector2, float)>
            {
                (new Vector2(4f, 0f), 0.4f),
                (new Vector2(-4f, 0f), 0.4f),
            };
            CollapsePreview.Estimate(charged, new List<Vector2>(), out float strength);
            Assert.Less(strength, 0.01f);
        }

        [Test]
        public void Collapses_Away_From_Bearing_Columns()
        {
            var charged = new List<(Vector2, float)> { (new Vector2(0f, 0f), 0.4f) };
            var bearing = new List<Vector2> { new Vector2(0f, 3f) };
            Vector2 dir = CollapsePreview.Estimate(charged, bearing, out _);
            Assert.Less(dir.y, -0.9f);
        }
    }

    public class TimeAndDifficultyTests
    {
        [Test]
        public void FixedDelta_Scales_With_TimeScale()
        {
            Assert.AreEqual(0.02f * 0.15f, TimeController.FixedDeltaFor(0.02f, 0.15f), 1e-6f);
            Assert.AreEqual(0.02f, TimeController.FixedDeltaFor(0.02f, 3f), 1e-6f);
        }

        [Test]
        public void Difficulty_Grows_And_Budget_Shrinks()
        {
            var cfg = new GameConfig();
            var first = FloorDifficulty.For(0, cfg);
            var later = FloorDifficulty.For(8, cfg);
            Assert.Greater(later.SmallItems, first.SmallItems);
            Assert.GreaterOrEqual(later.BigItems, first.BigItems);
            Assert.Less(later.BaseBudget, first.BaseBudget);
            Assert.Less(later.BearingRatio, first.BearingRatio);
            Assert.GreaterOrEqual(FloorDifficulty.For(100, cfg).BearingRatio, cfg.minBearingRatio);
            Assert.GreaterOrEqual(FloorDifficulty.For(100, cfg).BaseBudget, cfg.minBudget);
        }
    }
}
