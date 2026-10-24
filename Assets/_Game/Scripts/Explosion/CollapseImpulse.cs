using System.Collections.Generic;
using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Çöküş itkisinin kuralları. Eşyalar kaotik çöküş fiziğinden değil bu kurallardan itilir: oyunda uygulanan kuvvet
    /// (<see cref="CollapseShove"/>) ile planlamadaki tahmin (<see cref="Simulate"/>) aynı formülleri kullanır, böylece
    /// oyuncu ip ve sıra seçiminin sonucunu önceden görür. Tüm hesaplar yatay düzlemde (x, z → Vector2).
    /// </summary>
    public static class CollapseImpulse
    {
        private const float Gravity = 9.81f;
        private const float StepTime = 0.02f;
        private const float MaxSimTime = 8f;
        private const float StopSpeed = 0.05f;
        private const float SlideThreshold = 0.5f;

        /// <summary>Bir şarjın itki dalgası: sıra başladıktan kaç saniye sonra patladığı ve kolonlarının konumu.</summary>
        public struct Wave
        {
            public float Time;
            public List<Vector2> Columns;
        }

        /// <summary>Kat boyunca sabit itki ayarları (Detonate anında kilitlenir).</summary>
        public struct PushContext
        {
            public Vector2 Center;
            /// <summary>Yatay çöküş yönü (birim ya da sıfır).</summary>
            public Vector2 Direction;
            /// <summary>0 = eşyalar merkezden dışa saçılır, 1 = tamamen çöküş yönünde kayar.</summary>
            public float Share;
            public float Duration;
            public float Radius;

            public static PushContext For(Rect footprint, Vector3 direction, float strength, GameConfig cfg)
            {
                var dir = new Vector2(direction.x, direction.z);
                return new PushContext
                {
                    Center = footprint.center,
                    Direction = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector2.zero,
                    Share = dir.sqrMagnitude > 1e-6f ? DirectionalShare(strength, cfg) : 0f,
                    Duration = cfg.pushDuration,
                    Radius = cfg.waveRadius
                };
            }
        }

        public static Vector2 Flat(Vector3 v) => new Vector2(v.x, v.z);

        /// <summary>Çöküş okunun gücünü (bkz. <see cref="CollapsePreview.Strength"/>) yön payına çevirir.</summary>
        public static float DirectionalShare(float strength, GameConfig cfg)
        {
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(cfg.pushRadialBelow, cfg.pushDirectionalAbove, strength));
        }

        /// <summary>Simetrik çöküşte merkezden dışa, tek yönlü çöküşte ok yönünde.</summary>
        public static Vector2 PushDirection(Vector2 offsetFromCenter, Vector2 collapseDir, float share)
        {
            Vector2 radial = offsetFromCenter.sqrMagnitude > 1e-4f ? offsetFromCenter.normalized : collapseDir;
            Vector2 d = Vector2.Lerp(radial, collapseDir, share);
            return d.sqrMagnitude > 1e-6f ? d.normalized : radial;
        }

        /// <summary>Patlayan kolonlara yakın eşya tam, uzaktaki zayıf itilir (karesel azalma).</summary>
        public static float WaveWeight(Vector2 pos, IList<Vector2> columns, float radius)
        {
            if (columns == null || columns.Count == 0 || radius <= 0f) return 0f;
            float best = float.MaxValue;
            foreach (var c in columns) best = Mathf.Min(best, Vector2.Distance(pos, c));
            float k = 1f - Mathf.Clamp01(best / radius);
            return k * k;
        }

        public static float PushAccel(ItemSize size, int floorNumber, GameConfig cfg)
        {
            return (size == ItemSize.Big ? cfg.pushAccelBig : cfg.pushAccelSmall) + cfg.pushAccelPerFloor * floorNumber;
        }

        /// <summary><paramref name="t"/> anında (sıra başlangıcından itibaren) eşyaya uygulanan yatay ivme.</summary>
        public static Vector2 AccelAt(Vector2 pos, float baseAccel, IList<Wave> waves, float t, in PushContext ctx)
        {
            float sum = 0f;
            foreach (var w in waves)
            {
                if (t >= w.Time && t < w.Time + ctx.Duration) sum += WaveWeight(pos, w.Columns, ctx.Radius);
            }
            if (sum <= 0f) return Vector2.zero;
            return PushDirection(pos - ctx.Center, ctx.Direction, ctx.Share) * (baseAccel * sum);
        }

        /// <summary>Bina bu duvarın tarafına yatıyorsa duvar dışa düşer; karşı ve yan taraflarda içe çekilir.</summary>
        public static bool WallFallsOutward(Vector2 wallOutward, in PushContext ctx, GameConfig cfg)
        {
            return ctx.Share >= 0.5f && Vector2.Dot(ctx.Direction, wallOutward.normalized) > cfg.wallOutwardDot;
        }

        /// <summary>Ip yükü yaklaşık hesabı: kütle × (ipi uzatan ivme + gerilme anındaki hız / şok süresi).</summary>
        public static float RopeLoad(float mass, float outwardAccel, float outwardSpeed, float shockTime)
        {
            return mass * (Mathf.Max(0f, outwardAccel) + Mathf.Max(0f, outwardSpeed) / Mathf.Max(0.05f, shockTime));
        }

        /// <summary>Elastik ip şoku daha uzun sürede emer, tepe yükü düşer.</summary>
        public static float ShockTime(float compliance) => Mathf.Lerp(0.2f, 0.6f, Mathf.Clamp01(compliance * 2f));

        /// <summary>
        /// Eşya sürtünmeyle kayarken itki bittikten sonra ne kadar yol alır (düz zemin, ip yok). Yalnızca test ve
        /// ayar kolaylığı için; asıl tahmin <see cref="Simulate"/>.
        /// </summary>
        public static float PredictSlide(float accel, float duration, float friction)
        {
            float mu = friction * Gravity;
            float v = Mathf.Max(0f, accel - mu) * duration;
            if (v <= 0f) return 0f;
            return v * duration * 0.5f + v * v / (2f * Mathf.Max(0.01f, mu));
        }

        #region Tahmin

        public enum Outcome
        {
            Stays,
            Slides,
            Hangs,
            Falls
        }

        public class SimBody
        {
            public Vector2 Position;
            public float Mass;
            public float BaseAccel;
            public float Radius;

            public Vector2 End;
            public Outcome Result;
        }

        public class SimTie
        {
            public int Body;
            /// <summary>Diğer uç da eşyaysa onun indeksi (paket); değilse -1 ve <see cref="Anchor"/> geçerli.</summary>
            public int OtherBody = -1;
            /// <summary>İpin eşyadaki ucu (başlangıçta, dünya xz).</summary>
            public Vector2 Attach;
            public Vector2 Anchor;
            /// <summary>İpin yatay düzleme izdüşen uzunluğu.</summary>
            public float Length;
            public float BreakForce;
            public float ShockTime = 0.2f;
            /// <summary>Ankrajın kolonu bu anda patlar ve ip boşa çıkar.</summary>
            public float ReleaseTime = float.PositiveInfinity;

            public float PeakLoad;
            public float SnapTime = float.PositiveInfinity;
            public bool Released;

            public bool Snapped => !float.IsPositiveInfinity(SnapTime);
            public float PeakRatio => BreakForce > 0f ? PeakLoad / BreakForce : 0f;
        }

        public class SimWall
        {
            public Vector2 Center;
            public Vector2 Outward;
            public float HalfLength;
            /// <summary>Bu anda devrilir ve önü açılır; hiç devrilmezse sonsuz.</summary>
            public float FallTime = float.PositiveInfinity;
        }

        private class Group
        {
            public readonly List<int> Members = new List<int>();
            public readonly List<SimTie> Ties = new List<SimTie>();
            public float Mass;
            public Vector2 Offset;
            public Vector2 Velocity;
            public bool Done;
        }

        private static readonly List<SimTie> Taut = new List<SimTie>();
        private static readonly List<float> TautLoads = new List<float>();

        /// <summary>
        /// Eşyaları itki dalgaları, sürtünme, ipler ve kenar duvarları altında yatayda adım adım ilerletir.
        /// Birbirine bağlı eşyalar (paket) tek kütle olarak hareket eder. Sonuçlar gövde ve ip nesnelerine yazılır.
        /// </summary>
        public static void Simulate(IList<SimBody> bodies, IList<SimTie> ties, IList<SimWall> walls, IList<Wave> waves,
            in PushContext ctx, Rect footprint, float friction)
        {
            var groups = BuildGroups(bodies, ties);
            float lastWaveEnd = 0f;
            foreach (var w in waves) lastWaveEnd = Mathf.Max(lastWaveEnd, w.Time + ctx.Duration);
            float mu = friction * Gravity;

            for (float t = 0f; t < MaxSimTime; t += StepTime)
            {
                bool moving = t < lastWaveEnd;
                foreach (var g in groups)
                {
                    if (g.Done) continue;
                    StepGroup(g, bodies, walls, waves, ctx, footprint, mu, t);
                    if (g.Velocity.magnitude > StopSpeed) moving = true;
                }
                if (!moving) break;
            }

            foreach (var g in groups)
            {
                foreach (int m in g.Members)
                {
                    var b = bodies[m];
                    if (g.Done)
                    {
                        b.Result = Outcome.Falls;
                        continue;
                    }
                    b.End = b.Position + g.Offset;
                    if (!footprint.Contains(b.End)) b.Result = Outcome.Hangs;
                    else b.Result = g.Offset.magnitude > SlideThreshold ? Outcome.Slides : Outcome.Stays;
                }
            }
        }

        private static List<Group> BuildGroups(IList<SimBody> bodies, IList<SimTie> ties)
        {
            int n = bodies.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);

            foreach (var tie in ties)
            {
                if (tie.OtherBody >= 0 && tie.OtherBody < n && tie.Body >= 0 && tie.Body < n)
                    parent[Find(tie.Body)] = Find(tie.OtherBody);
            }

            var byRoot = new Dictionary<int, Group>();
            var groups = new List<Group>();
            for (int i = 0; i < n; i++)
            {
                bodies[i].End = bodies[i].Position;
                bodies[i].Result = Outcome.Stays;
                int root = Find(i);
                if (!byRoot.TryGetValue(root, out var g))
                {
                    g = new Group();
                    byRoot[root] = g;
                    groups.Add(g);
                }
                g.Members.Add(i);
                g.Mass += Mathf.Max(0.1f, bodies[i].Mass);
            }

            foreach (var tie in ties)
            {
                tie.PeakLoad = 0f;
                tie.SnapTime = float.PositiveInfinity;
                tie.Released = false;
                if (tie.OtherBody < 0 && tie.Body >= 0 && tie.Body < n) byRoot[Find(tie.Body)].Ties.Add(tie);
            }
            return groups;
        }

        private static void StepGroup(Group g, IList<SimBody> bodies, IList<SimWall> walls, IList<Wave> waves,
            in PushContext ctx, Rect footprint, float mu, float t)
        {
            // İtki: paketin kütle ağırlıklı ortalaması
            Vector2 force = Vector2.zero;
            foreach (int m in g.Members)
            {
                var b = bodies[m];
                force += b.Mass * AccelAt(b.Position + g.Offset, b.BaseAccel, waves, t, ctx);
            }
            Vector2 drive = force / g.Mass;

            // Sürtünme: durağanken itki sürtünmeyi yenmeli; kayarken hızın tersine yavaşlatır
            Vector2 accel;
            if (g.Velocity.magnitude < StopSpeed)
            {
                g.Velocity = Vector2.zero;
                accel = drive.magnitude <= mu ? Vector2.zero : drive - drive.normalized * mu;
            }
            else
            {
                accel = drive - g.Velocity.normalized * mu;
            }

            Vector2 before = g.Velocity;
            g.Velocity += accel * StepTime;
            if (drive.sqrMagnitude < 1e-6f && Vector2.Dot(before, g.Velocity) <= 0f) g.Velocity = Vector2.zero;
            g.Offset += g.Velocity * StepTime;

            bool anyOutside = false;
            foreach (int m in g.Members)
            {
                if (!footprint.Contains(bodies[m].Position + g.Offset)) anyOutside = true;
            }

            ApplyTies(g, drive, anyOutside, t);
            bool tied = HasActiveTie(g, t);

            // Kenar: ayakta duran duvar durdurur; açık kenardan bağsız eşya düşer
            foreach (int m in g.Members)
            {
                var b = bodies[m];
                Vector2 c = b.Position + g.Offset;
                var inset = new Rect(footprint.xMin + b.Radius, footprint.yMin + b.Radius,
                    Mathf.Max(0.01f, footprint.width - b.Radius * 2f), Mathf.Max(0.01f, footprint.height - b.Radius * 2f));
                BlockAxis(g, walls, footprint, inset, c, t, true);
                c = b.Position + g.Offset;
                BlockAxis(g, walls, footprint, inset, c, t, false);
                c = b.Position + g.Offset;

                if (!footprint.Contains(c) && !tied)
                {
                    g.Done = true;
                    foreach (int other in g.Members) bodies[other].End = bodies[other].Position + g.Offset;
                    return;
                }
            }
        }

        private static bool HasActiveTie(Group g, float t)
        {
            foreach (var tie in g.Ties)
            {
                if (!tie.Snapped && t < tie.ReleaseTime) return true;
            }
            return false;
        }

        private static void ApplyTies(Group g, Vector2 drive, bool hanging, float t)
        {
            Taut.Clear();
            TautLoads.Clear();
            foreach (var tie in g.Ties)
            {
                if (tie.Snapped) continue;
                if (t >= tie.ReleaseTime)
                {
                    tie.Released = true;
                    continue;
                }

                Vector2 r = tie.Attach + g.Offset - tie.Anchor;
                float dist = r.magnitude;
                if (dist <= tie.Length || dist < 1e-4f) continue;

                Vector2 u = r / dist;
                g.Offset -= u * (dist - tie.Length);
                float vOut = Vector2.Dot(g.Velocity, u);
                if (vOut > 0f) g.Velocity -= u * vOut;

                float load = RopeLoad(g.Mass, Vector2.Dot(drive, u), vOut, tie.ShockTime);
                if (hanging) load += g.Mass * Gravity;
                Taut.Add(tie);
                TautLoads.Add(load);
            }

            for (int i = 0; i < Taut.Count; i++)
            {
                var tie = Taut[i];
                float load = TautLoads[i] / Taut.Count;
                tie.PeakLoad = Mathf.Max(tie.PeakLoad, load);
                if (load > tie.BreakForce) tie.SnapTime = t;
            }
        }

        /// <summary>Bir eksende kenarı aşan eşyayı, o noktadaki duvar hâlâ ayaktaysa içeri geri iter.</summary>
        private static void BlockAxis(Group g, IList<SimWall> walls, Rect footprint, Rect inset, Vector2 c, float t, bool xAxis)
        {
            float pos = xAxis ? c.x : c.y;
            float min = xAxis ? inset.xMin : inset.yMin, max = xAxis ? inset.xMax : inset.yMax;
            if (pos >= min && pos <= max) return;

            Vector2 normal = xAxis ? new Vector2(pos < min ? -1f : 1f, 0f) : new Vector2(0f, pos < min ? -1f : 1f);
            Vector2 edgePoint = new Vector2(Mathf.Clamp(c.x, footprint.xMin, footprint.xMax),
                Mathf.Clamp(c.y, footprint.yMin, footprint.yMax));
            if (!WallStands(walls, edgePoint, normal, t)) return;

            float overshoot = pos < min ? pos - min : pos - max;
            g.Offset -= normal * Mathf.Abs(overshoot);
            float vn = Vector2.Dot(g.Velocity, normal);
            if (vn > 0f) g.Velocity -= normal * vn;
        }

        private static bool WallStands(IList<SimWall> walls, Vector2 edgePoint, Vector2 normal, float t)
        {
            foreach (var w in walls)
            {
                if (t >= w.FallTime || Vector2.Dot(w.Outward.normalized, normal) < 0.9f) continue;
                Vector2 tangent = new Vector2(-normal.y, normal.x);
                if (Mathf.Abs(Vector2.Dot(edgePoint - w.Center, tangent)) <= w.HalfLength &&
                    Mathf.Abs(Vector2.Dot(edgePoint - w.Center, normal)) < 0.5f) return true;
            }
            return false;
        }

        #endregion
    }
}
