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
    /// GDD puanlama kuralları (yalnızca eşyalar): bölge dışındaki küçük eşya ceza, komşu binaya çarpan eşya büyük ceza,
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

        /// <summary>
        /// Kat cezası sınırı kattaki küçük eşya sayısından gelir (büyük eşya zaten can götürür): küçük eşyaların
        /// belli bir oranı düşebilir, oran katlar indikçe azalır. En az bir küçük eşyanın düşmesine izin verir.
        /// </summary>
        public static int PenaltyLimitFor(int smallItems, int floorNumber, GameConfig cfg)
        {
            float ratio = Mathf.Max(cfg.minPenaltyLimitItemRatio,
                cfg.penaltyLimitItemRatio - cfg.penaltyLimitRatioDecreasePerFloor * floorNumber);
            return Mathf.Max(SmallItemPenalty, Mathf.RoundToInt(smallItems * ratio) * SmallItemPenalty);
        }

        public static int PenaltyLimitFor(Floor floor, int floorNumber, GameConfig cfg)
        {
            int small = 0;
            if (floor != null)
            {
                foreach (var item in floor.Items)
                {
                    if (item != null && item.Size == ItemSize.Small) small++;
                }
            }
            return PenaltyLimitFor(small, floorNumber, cfg);
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

        /// <summary>Yalnızca eşyalar puanlanır; bina parçaları (duvar, kolon, tavan, moloz) her durumda dağıldığı için sayılmaz.</summary>
        public static FloorResult Evaluate(Floor floor, SafeZone zone, ICollection<Rigidbody> neighborHits,
            int penaltyLimit = int.MaxValue)
        {
            var outcomes = new List<BodyOutcome>(floor.Items.Count);
            foreach (var item in floor.Items)
            {
                if (item == null) continue;
                outcomes.Add(new BodyOutcome
                {
                    IsItem = true,
                    IsBig = item.Size == ItemSize.Big,
                    Inside = IsInside(item, zone),
                    HitNeighbor = neighborHits.Contains(item.Body)
                });
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
                if (item.Exploded) text = "bağsızdı, patladı (ikinci patlama)";
                else if (inside)
                {
                    if (best != null) text = $"{best.Type.DisplayName} tuttu (tepe %{best.PeakRatio * 100f:0})";
                    else if (packed) text = "pakette kaldı";
                    else if (item.SnappedRope != null) text = $"{item.SnappedRope} koptu ama bölgede kaldı";
                    else if (item.ReleasedBy != null) text = $"ip {ReleaseCause(item)} boşa çıktı, şanslıydı";
                    else text = "bağsız, yerinde kaldı";
                }
                else
                {
                    if (item.SnappedRope != null) text = $"{item.SnappedRope} koptu, düştü";
                    else if (item.ReleasedBy != null) text = $"ip {ReleaseCause(item)} boşa çıktı, düştü";
                    else if (best != null) text = "ipte asılı ama bölge dışında";
                    else if (packed) text = "paketle birlikte düştü";
                    else text = "bağsızdı, düştü";
                }

                reports.Add(new ItemReport { Name = item.DisplayName, IsBig = item.Size == ItemSize.Big, Inside = inside, Text = text });
            }
            return reports;
        }

        private static string ReleaseCause(Item item)
        {
            return item.ReleasedBy == BearingCrumble.ReleaseLabel ? "taşıyıcı kolon devrilince" : $"{item.ReleasedBy} patlayınca";
        }
    }
}
