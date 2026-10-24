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
        public int TotalItems;
        public int Penalty;
        public int PenaltyLimit;
        /// <summary>Ceza kat sınırını aştı: bir can daha gider.</summary>
        public bool OverLimit;
        public int LivesLost;
    }

    /// <summary>Sonuç ekranında bir eşyanın başına ne geldiği (oyuncu nedenini görsün).</summary>
    public struct ItemReport
    {
        public string Name;
        public bool IsBig;
        public bool Inside;
        public string Text;
    }

    /// <summary>
    /// GDD puanlama kuralları: bölge dışındaki her parça ceza, komşu binaya çarpan parça büyük ceza,
    /// bölge dışına çıkan büyük eşya bir can; kat cezası sınırı aşarsa bir can daha.
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

        public static FloorResult Tally(IEnumerable<BodyOutcome> outcomes, int penaltyLimit = int.MaxValue)
        {
            var r = new FloorResult();
            foreach (var o in outcomes)
            {
                if (o.HitNeighbor) r.NeighborHits++;
                if (o.IsItem)
                {
                    r.TotalItems++;
                    if (o.Inside) r.SavedItems++;
                    else if (o.IsBig) r.OutBigItems++;
                    else r.OutSmallItems++;
                }
                else if (!o.Inside)
                {
                    r.OutPieces++;
                }
            }

            r.Penalty = ComputePenalty(r.OutPieces, r.OutSmallItems, r.NeighborHits);
            r.PenaltyLimit = penaltyLimit;
            r.OverLimit = r.Penalty > penaltyLimit;
            r.LivesLost = r.OutBigItems + (r.OverLimit ? 1 : 0);
            return r;
        }

        public static FloorResult Evaluate(Floor floor, SafeZone zone, ICollection<Rigidbody> neighborHits,
            int penaltyLimit = int.MaxValue)
        {
            var outcomes = new List<BodyOutcome>(floor.Bodies.Count);
            var itemBodies = new Dictionary<Rigidbody, Item>();
            foreach (var item in floor.Items)
            {
                if (item != null) itemBodies[item.Body] = item;
            }

            // Kırılan parçanın kopan parçaları (ve kolon molozu) tek parça sayılır: kütlesinin yarısı bölge içindeyse içeride.
            var fragments = new Dictionary<int, (float inside, float total, bool hit)>();

            foreach (var rb in floor.Bodies)
            {
                if (rb == null || floor.ItemParts.Contains(rb)) continue;
                if (rb.TryGetComponent<Fragment>(out var fragment))
                {
                    fragments.TryGetValue(fragment.Group, out var acc);
                    bool fragInside = rb.gameObject.activeSelf && zone.Contains(rb.worldCenterOfMass);
                    fragments[fragment.Group] = (acc.inside + (fragInside ? rb.mass : 0f), acc.total + rb.mass,
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

            return Tally(outcomes, penaltyLimit);
        }

        public static bool IsInside(Item item, SafeZone zone)
        {
            return item != null && item.gameObject.activeSelf && zone.Contains(item.Body.worldCenterOfMass);
        }

        /// <summary>Her eşya için tek satırlık neden-sonuç: hangi ip tuttu, hangisi koptu ya da boşa çıktı.</summary>
        public static List<ItemReport> DescribeItems(Floor floor, SafeZone zone, IList<RopeLink> ropes)
        {
            var reports = new List<ItemReport>(floor.Items.Count);
            foreach (var item in floor.Items)
            {
                if (item == null) continue;
                bool inside = IsInside(item, zone);

                RopeLink best = null;
                bool packed = false;
                foreach (var link in ropes)
                {
                    if (link == null || !link.IsHolding || (link.A.Item != item && link.B.Item != item)) continue;
                    if (link.A.Item != null && link.B.Item != null)
                    {
                        packed = true;
                        continue;
                    }
                    if (best == null || link.PeakRatio > best.PeakRatio) best = link;
                }

                string text;
                if (inside)
                {
                    if (best != null) text = $"{best.Type.DisplayName} tuttu (tepe %{best.PeakRatio * 100f:0})";
                    else if (packed) text = "pakette kaldı";
                    else if (item.SnappedRope != null) text = $"{item.SnappedRope} koptu ama bölgede kaldı";
                    else if (item.ReleasedBy != null) text = $"ip {item.ReleasedBy} patlayınca boşa çıktı, şanslıydı";
                    else text = "bağsız, yerinde kaldı";
                }
                else
                {
                    if (item.SnappedRope != null) text = $"{item.SnappedRope} koptu, düştü";
                    else if (item.ReleasedBy != null) text = $"ip {item.ReleasedBy} patlayınca boşa çıktı, düştü";
                    else if (best != null) text = "ipte asılı ama bölge dışında";
                    else if (packed) text = "paketle birlikte düştü";
                    else text = "bağsızdı, düştü";
                }

                reports.Add(new ItemReport { Name = item.DisplayName, IsBig = item.Size == ItemSize.Big, Inside = inside, Text = text });
            }
            return reports;
        }
    }
}
