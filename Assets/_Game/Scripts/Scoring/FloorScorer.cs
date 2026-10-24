using System.Collections.Generic;
using UnityEngine;

namespace KatKat
{
    public struct BodyOutcome
    {
        public bool IsItem;
        public bool IsBig;
        public bool Inside;
        public bool HitNeighbor;
    }

    public struct FloorResult
    {
        public int OutPieces;
        public int OutSmallItems;
        public int OutBigItems;
        public int NeighborHits;
        public int SavedItems;
        public int Penalty;
        public int LivesLost;
    }

    /// <summary>
    /// GDD puanlama kuralları: bölge dışındaki her parça ceza, komşu binaya çarpan parça büyük ceza,
    /// bölge dışına çıkan büyük eşya bir can.
    /// </summary>
    public static class FloorScorer
    {
        public const int PiecePenalty = 1;
        public const int SmallItemPenalty = 2;
        public const int NeighborPenalty = 5;

        public static int ComputePenalty(int outPieces, int outSmallItems, int neighborHits)
        {
            return outPieces * PiecePenalty + outSmallItems * SmallItemPenalty + neighborHits * NeighborPenalty;
        }

        public static FloorResult Tally(IEnumerable<BodyOutcome> outcomes)
        {
            var r = new FloorResult();
            foreach (var o in outcomes)
            {
                if (o.HitNeighbor) r.NeighborHits++;
                if (o.IsItem)
                {
                    if (o.Inside) r.SavedItems++;
                    else if (o.IsBig) r.OutBigItems++;
                    else r.OutSmallItems++;
                }
                else if (!o.Inside)
                {
                    r.OutPieces++;
                }
            }

            r.LivesLost = r.OutBigItems;
            r.Penalty = ComputePenalty(r.OutPieces, r.OutSmallItems, r.NeighborHits);
            return r;
        }

        public static FloorResult Evaluate(Floor floor, SafeZone zone, ICollection<Rigidbody> neighborHits)
        {
            var outcomes = new List<BodyOutcome>(floor.Bodies.Count);
            var itemBodies = new Dictionary<Rigidbody, Item>();
            foreach (var item in floor.Items)
            {
                if (item != null) itemBodies[item.Body] = item;
            }

            // Kırılan parçanın kopan parçaları tek parça sayılır: kütlesinin yarısı bölge içindeyse içeride.
            var fragments = new Dictionary<StructuralPiece, (float inside, float total, bool hit)>();

            foreach (var rb in floor.Bodies)
            {
                if (rb == null) continue;
                if (rb.TryGetComponent<Fragment>(out var fragment) && fragment.Source != null)
                {
                    fragments.TryGetValue(fragment.Source, out var acc);
                    bool fragInside = rb.gameObject.activeSelf && zone.Contains(rb.worldCenterOfMass);
                    fragments[fragment.Source] = (acc.inside + (fragInside ? rb.mass : 0f), acc.total + rb.mass,
                        acc.hit || neighborHits.Contains(rb));
                    continue;
                }

                bool isItem = itemBodies.TryGetValue(rb, out var item);
                outcomes.Add(new BodyOutcome
                {
                    IsItem = isItem,
                    IsBig = isItem && item.Size == ItemSize.Big,
                    Inside = rb.gameObject.activeSelf && zone.Contains(rb.worldCenterOfMass),
                    HitNeighbor = neighborHits.Contains(rb)
                });
            }

            foreach (var (inside, total, hit) in fragments.Values)
            {
                outcomes.Add(new BodyOutcome { Inside = inside >= total * 0.5f, HitNeighbor = hit });
            }

            return Tally(outcomes);
        }
    }
}
