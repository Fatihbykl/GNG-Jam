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

        [Test]
        public void Exceeding_Penalty_Limit_Costs_An_Extra_Life()
        {
            var outcomes = new List<BodyOutcome>
            {
                new BodyOutcome { IsItem = true, IsBig = true, Inside = false },
                new BodyOutcome { Inside = false, HitNeighbor = true },
                new BodyOutcome { Inside = false, HitNeighbor = true },
            };
            var under = FloorScorer.Tally(outcomes, 20);
            Assert.IsFalse(under.OverLimit);
            Assert.AreEqual(1, under.LivesLost);

            var over = FloorScorer.Tally(outcomes, 5);
            Assert.IsTrue(over.OverLimit);
            Assert.AreEqual(2, over.LivesLost);
        }
    }

    public class CollapseImpulseTests
    {
        private static CollapseImpulse.PushContext Ctx(Vector2 dir, float share) => new CollapseImpulse.PushContext
        {
            Center = Vector2.zero, Direction = dir, Share = share, Duration = 0.4f, Radius = 4.5f
        };

        private static List<CollapseImpulse.Wave> WaveAt(Vector2 column) => new List<CollapseImpulse.Wave>
        {
            new CollapseImpulse.Wave { Time = 0f, Columns = new List<Vector2> { column } }
        };

        [Test]
        public void Symmetric_Collapse_Pushes_Radially_Outward()
        {
            Vector2 d = CollapseImpulse.PushDirection(new Vector2(-2f, 0f), Vector2.right, 0f);
            Assert.Less(d.x, -0.99f);
        }

        [Test]
        public void Directional_Collapse_Pushes_Along_Arrow()
        {
            Vector2 d = CollapseImpulse.PushDirection(new Vector2(-2f, 0f), Vector2.right, 1f);
            Assert.Greater(d.x, 0.99f);
        }

        [Test]
        public void Wave_Fades_With_Distance_From_Fired_Columns()
        {
            var cols = new List<Vector2> { Vector2.zero };
            Assert.AreEqual(1f, CollapseImpulse.WaveWeight(Vector2.zero, cols, 4f), 1e-4f);
            Assert.Greater(CollapseImpulse.WaveWeight(new Vector2(1f, 0f), cols, 4f),
                CollapseImpulse.WaveWeight(new Vector2(3f, 0f), cols, 4f));
            Assert.AreEqual(0f, CollapseImpulse.WaveWeight(new Vector2(5f, 0f), cols, 4f), 1e-4f);
        }

        [Test]
        public void Wall_On_Lean_Side_Falls_Outward_Others_Inward()
        {
            var cfg = new GameConfig();
            var lean = Ctx(Vector2.right, 1f);
            Assert.IsTrue(CollapseImpulse.WallFallsOutward(Vector2.right, lean, cfg));
            Assert.IsFalse(CollapseImpulse.WallFallsOutward(Vector2.left, lean, cfg));
            Assert.IsFalse(CollapseImpulse.WallFallsOutward(Vector2.up, lean, cfg));
            Assert.IsFalse(CollapseImpulse.WallFallsOutward(Vector2.right, Ctx(Vector2.right, 0f), cfg));
        }

        [Test]
        public void Push_Below_Friction_Does_Not_Slide()
        {
            Assert.AreEqual(0f, CollapseImpulse.PredictSlide(3f, 0.4f, 0.4f), 1e-4f);
            Assert.Greater(CollapseImpulse.PredictSlide(15f, 0.4f, 0.4f), 1f);
        }

        [Test]
        public void Untied_Item_Near_Open_Edge_Falls()
        {
            var fp = new Rect(-4.5f, -3f, 9f, 6f);
            var bodies = new List<CollapseImpulse.SimBody>
            {
                new CollapseImpulse.SimBody { Position = new Vector2(3.5f, 0f), Mass = 90f, BaseAccel = 18f, Radius = 0.3f }
            };
            CollapseImpulse.Simulate(bodies, new List<CollapseImpulse.SimTie>(), new List<CollapseImpulse.SimWall>(),
                WaveAt(new Vector2(3.5f, 0f)), Ctx(Vector2.right, 1f), fp, 0.4f);
            Assert.AreEqual(CollapseImpulse.Outcome.Falls, bodies[0].Result);
        }

        [Test]
        public void Standing_Wall_Stops_Item()
        {
            var fp = new Rect(-4.5f, -3f, 9f, 6f);
            var bodies = new List<CollapseImpulse.SimBody>
            {
                new CollapseImpulse.SimBody { Position = new Vector2(3.5f, 0f), Mass = 90f, BaseAccel = 18f, Radius = 0.3f }
            };
            var walls = new List<CollapseImpulse.SimWall>
            {
                new CollapseImpulse.SimWall { Center = new Vector2(4.5f, 0f), Outward = Vector2.right, HalfLength = 1.5f }
            };
            CollapseImpulse.Simulate(bodies, new List<CollapseImpulse.SimTie>(), walls, WaveAt(new Vector2(3.5f, 0f)),
                Ctx(Vector2.right, 1f), fp, 0.4f);
            Assert.AreNotEqual(CollapseImpulse.Outcome.Falls, bodies[0].Result);
        }

        [Test]
        public void Thin_Rope_Snaps_On_Heavy_Item_Steel_Holds()
        {
            var fp = new Rect(-4.5f, -3f, 9f, 6f);
            foreach (var (breakForce, shouldSnap) in new[] { (1400f, true), (9000f, false) })
            {
                var bodies = new List<CollapseImpulse.SimBody>
                {
                    new CollapseImpulse.SimBody { Position = new Vector2(3f, 0f), Mass = 90f, BaseAccel = 18f, Radius = 0.3f }
                };
                var tie = new CollapseImpulse.SimTie
                {
                    Body = 0, Attach = new Vector2(3f, 0f), Anchor = new Vector2(1f, 0f), Length = 2.1f, BreakForce = breakForce
                };
                CollapseImpulse.Simulate(bodies, new List<CollapseImpulse.SimTie> { tie }, new List<CollapseImpulse.SimWall>(),
                    WaveAt(new Vector2(3f, 0f)), Ctx(Vector2.right, 1f), fp, 0.4f);
                Assert.AreEqual(shouldSnap, tie.Snapped, $"break {breakForce}");
                if (!shouldSnap) Assert.AreNotEqual(CollapseImpulse.Outcome.Falls, bodies[0].Result);
            }
        }

        [Test]
        public void Untied_Gas_Tank_Explodes_And_Pushes_Neighbors_Tied_One_Does_Not()
        {
            var fp = new Rect(-4.5f, -3f, 9f, 6f);
            var ctx = Ctx(Vector2.right, 1f);
            ctx.Radius = 1.5f;
            ctx.GasFuse = 0.1f;
            ctx.GasStrength = 1.6f;
            ctx.GasRadius = 3.5f;

            foreach (bool tied in new[] { false, true })
            {
                var bodies = new List<CollapseImpulse.SimBody>
                {
                    // Gaz tüpü dalganın içinde; komşu eşya dalganın dışında, tüpün solunda
                    new CollapseImpulse.SimBody { Position = new Vector2(-1f, 0f), Mass = 15f, BaseAccel = 15f, Radius = 0.2f, Volatile = true },
                    new CollapseImpulse.SimBody { Position = new Vector2(-2.5f, 0f), Mass = 8f, BaseAccel = 15f, Radius = 0.25f },
                };
                var ties = new List<CollapseImpulse.SimTie>();
                if (tied)
                {
                    ties.Add(new CollapseImpulse.SimTie
                    {
                        Body = 0, Attach = new Vector2(-1f, 0f), Anchor = new Vector2(-1.5f, 0f), Length = 0.6f, BreakForce = 9000f
                    });
                }
                var waves = WaveAt(new Vector2(-1f, 0f));
                CollapseImpulse.Simulate(bodies, ties, new List<CollapseImpulse.SimWall>(), waves, ctx, fp, 0.4f);

                if (tied)
                {
                    Assert.AreNotEqual(CollapseImpulse.Outcome.Explodes, bodies[0].Result);
                    Assert.AreEqual(CollapseImpulse.Outcome.Stays, bodies[1].Result);
                }
                else
                {
                    Assert.AreEqual(CollapseImpulse.Outcome.Explodes, bodies[0].Result);
                    Assert.Less(bodies[1].End.x, -2.8f, "patlama komşuyu dışa (sola) itmeli");
                }
            }
        }

        [Test]
        public void Packed_Items_Move_As_One_Mass()
        {
            var fp = new Rect(-4.5f, -3f, 9f, 6f);
            var bodies = new List<CollapseImpulse.SimBody>
            {
                new CollapseImpulse.SimBody { Position = new Vector2(0f, 0f), Mass = 90f, BaseAccel = 18f, Radius = 0.3f },
                new CollapseImpulse.SimBody { Position = new Vector2(0.8f, 0f), Mass = 8f, BaseAccel = 15f, Radius = 0.25f },
            };
            var ties = new List<CollapseImpulse.SimTie>
            {
                new CollapseImpulse.SimTie { Body = 1, OtherBody = 0 },
                new CollapseImpulse.SimTie
                {
                    Body = 0, Attach = new Vector2(0f, 0f), Anchor = new Vector2(-1f, 0f), Length = 1.05f, BreakForce = 9000f
                },
            };
            CollapseImpulse.Simulate(bodies, ties, new List<CollapseImpulse.SimWall>(), WaveAt(new Vector2(0f, 0f)),
                Ctx(Vector2.right, 1f), fp, 0.4f);
            Assert.AreEqual((bodies[0].End - bodies[0].Position).x, (bodies[1].End - bodies[1].Position).x, 1e-4f);
            Assert.Less(bodies[1].End.x, 1f, "paket ankraja bağlı ana eşyayla birlikte tutulmalı");
        }

        [Test]
        public void Rope_Released_Before_Wave_Does_Not_Hold()
        {
            var fp = new Rect(-4.5f, -3f, 9f, 6f);
            var bodies = new List<CollapseImpulse.SimBody>
            {
                new CollapseImpulse.SimBody { Position = new Vector2(3.5f, 0f), Mass = 20f, BaseAccel = 15f, Radius = 0.3f }
            };
            var tie = new CollapseImpulse.SimTie
            {
                Body = 0, Attach = new Vector2(3.5f, 0f), Anchor = new Vector2(1.5f, 0f), Length = 2.1f, BreakForce = 9000f,
                ReleaseTime = 0f
            };
            CollapseImpulse.Simulate(bodies, new List<CollapseImpulse.SimTie> { tie }, new List<CollapseImpulse.SimWall>(),
                WaveAt(new Vector2(3.5f, 0f)), Ctx(Vector2.right, 1f), fp, 0.4f);
            Assert.IsTrue(tie.Released);
            Assert.AreEqual(CollapseImpulse.Outcome.Falls, bodies[0].Result);
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
