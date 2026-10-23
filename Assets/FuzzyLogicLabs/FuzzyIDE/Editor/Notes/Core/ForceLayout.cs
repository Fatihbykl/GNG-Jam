using System;
using System.Collections.Generic;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core
{
    /// <summary>Strengths of the forces (Obsidian's graph "Forces" sliders).</summary>
    [Serializable]
    public sealed class ForceSettings
    {
        public float CenterStrength = 0.05f;
        public float RepelStrength = 900f;
        public float LinkStrength = 0.6f;
        public float LinkDistance = 70f;
    }

    /// <summary>
    /// Force-directed layout in the style of d3-force: nodes repel each other, links pull like springs and a weak
    /// force keeps everything centred. The "temperature" (<see cref="Alpha"/>) cools down so the layout settles;
    /// dragging or rebuilding reheats it. Deterministic for a given seed. Repulsion is exact up to
    /// <see cref="BarnesHutThreshold"/> nodes and uses a Barnes-Hut quadtree above that.
    /// </summary>
    public sealed class ForceLayout
    {
        public const int BarnesHutThreshold = 500;
        public const float AlphaMin = 0.005f;
        private const float AlphaDecay = 0.0228f; // d3 default: ~300 steps from 1 to AlphaMin
        private const float VelocityDecay = 0.4f;
        private const float Theta2 = 0.81f;       // Barnes-Hut accuracy (theta 0.9)
        private const float MinDistance2 = 1f;

        public readonly float[] X;
        public readonly float[] Y;
        private readonly float[] m_Vx;
        private readonly float[] m_Vy;
        private readonly bool[] m_Fixed;
        private readonly int[] m_EdgeA;
        private readonly int[] m_EdgeB;
        private readonly float[] m_EdgeBias;   // share of the spring moving the A end (low-degree ends move more)
        private readonly float[] m_EdgeScale;  // 1 / min(degree) like d3, so hubs are not torn apart

        public ForceSettings Settings = new ForceSettings();
        public float Alpha { get; private set; } = 1f;
        public int Count => X.Length;
        public bool IsHot => Alpha > AlphaMin;
        /// <summary>Forces Barnes-Hut (true) or exact repulsion (false); null decides by node count.</summary>
        public bool? UseBarnesHut;

        public ForceLayout(int count, IReadOnlyList<GraphEdge> edges, int seed = 1)
        {
            X = new float[count];
            Y = new float[count];
            m_Vx = new float[count];
            m_Vy = new float[count];
            m_Fixed = new bool[count];

            // phyllotaxis start like d3: evenly spread, then a tiny seeded jitter so symmetric graphs can untangle
            var random = new Random(seed);
            for (int i = 0; i < count; i++)
            {
                double radius = 10 * Math.Sqrt(0.5 + i);
                double angle = i * Math.PI * (3 - Math.Sqrt(5));
                X[i] = (float)(radius * Math.Cos(angle) + (random.NextDouble() - 0.5));
                Y[i] = (float)(radius * Math.Sin(angle) + (random.NextDouble() - 0.5));
            }

            var degree = new int[count];
            foreach (var e in edges) { degree[e.A]++; degree[e.B]++; }
            m_EdgeA = new int[edges.Count];
            m_EdgeB = new int[edges.Count];
            m_EdgeBias = new float[edges.Count];
            m_EdgeScale = new float[edges.Count];
            for (int k = 0; k < edges.Count; k++)
            {
                int a = edges[k].A, b = edges[k].B;
                m_EdgeA[k] = a;
                m_EdgeB[k] = b;
                m_EdgeBias[k] = degree[a] / (float)(degree[a] + degree[b]);
                m_EdgeScale[k] = 1f / Math.Max(1, Math.Min(degree[a], degree[b]));
            }
        }

        /// <summary>Moves a node (e.g. kept from the previous layout, or dragged) and clears its velocity.</summary>
        public void SetPosition(int i, float x, float y)
        {
            X[i] = x;
            Y[i] = y;
            m_Vx[i] = m_Vy[i] = 0f;
        }

        /// <summary>A fixed node keeps its position (while it is dragged) but still pushes and pulls the others.</summary>
        public void SetFixed(int i, bool isFixed) => m_Fixed[i] = isFixed;

        public void Reheat(float alpha = 0.3f) => Alpha = Math.Max(Alpha, Math.Min(1f, alpha));

        /// <summary>Runs <paramref name="steps"/> simulation ticks (stops early once settled).</summary>
        public void Run(int steps)
        {
            for (int s = 0; s < steps && IsHot; s++) Step();
        }

        public void Step()
        {
            int n = Count;
            if (n == 0) { Alpha = 0f; return; }
            Alpha += (0f - Alpha) * AlphaDecay;
            float alpha = Alpha;

            ApplyLinks(alpha);
            if (UseBarnesHut ?? n > BarnesHutThreshold) ApplyRepulsionBarnesHut(alpha);
            else ApplyRepulsionExact(alpha);

            float center = Settings.CenterStrength * alpha;
            for (int i = 0; i < n; i++)
            {
                m_Vx[i] -= X[i] * center;
                m_Vy[i] -= Y[i] * center;
            }

            for (int i = 0; i < n; i++)
            {
                if (m_Fixed[i]) { m_Vx[i] = m_Vy[i] = 0f; continue; }
                m_Vx[i] *= 1f - VelocityDecay;
                m_Vy[i] *= 1f - VelocityDecay;
                X[i] += m_Vx[i];
                Y[i] += m_Vy[i];
            }
        }

        private void ApplyLinks(float alpha)
        {
            for (int k = 0; k < m_EdgeA.Length; k++)
            {
                int a = m_EdgeA[k], b = m_EdgeB[k];
                float dx = X[b] + m_Vx[b] - X[a] - m_Vx[a];
                float dy = Y[b] + m_Vy[b] - Y[a] - m_Vy[a];
                float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                if (dist < 1e-3f) { dx = 0.01f * (k % 2 == 0 ? 1 : -1); dy = 0.01f; dist = 0.0141f; }

                float f = (dist - Settings.LinkDistance) / dist * alpha * Settings.LinkStrength * m_EdgeScale[k];
                dx *= f;
                dy *= f;
                float bias = m_EdgeBias[k];
                m_Vx[b] -= dx * bias;
                m_Vy[b] -= dy * bias;
                m_Vx[a] += dx * (1f - bias);
                m_Vy[a] += dy * (1f - bias);
            }
        }

        private void ApplyRepulsionExact(float alpha)
        {
            int n = Count;
            float strength = Settings.RepelStrength * alpha;
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    float dx = X[i] - X[j], dy = Y[i] - Y[j];
                    float d2 = dx * dx + dy * dy;
                    if (d2 < MinDistance2) { dx = ((i - j) % 3 + 0.5f) * 0.1f; dy = 0.1f; d2 = MinDistance2; }
                    float f = strength / d2;
                    m_Vx[i] += dx * f; m_Vy[i] += dy * f;
                    m_Vx[j] -= dx * f; m_Vy[j] -= dy * f;
                }
            }
        }

        // =====================================================================
        // Barnes-Hut: far groups of nodes act as one mass at their centre
        // =====================================================================

        private sealed class Quad
        {
            public float X0, Y0, Size;
            public float Mass, Cx, Cy;
            public int Body = -1;     // the single node of a leaf
            public Quad[] Children;   // null for leaves
        }

        private void ApplyRepulsionBarnesHut(float alpha)
        {
            int n = Count;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                minX = Math.Min(minX, X[i]); maxX = Math.Max(maxX, X[i]);
                minY = Math.Min(minY, Y[i]); maxY = Math.Max(maxY, Y[i]);
            }
            var root = new Quad { X0 = minX, Y0 = minY, Size = Math.Max(1f, Math.Max(maxX - minX, maxY - minY)) + 1f };
            for (int i = 0; i < n; i++) Insert(root, i, 0);
            ComputeMass(root);

            float strength = Settings.RepelStrength * alpha;
            for (int i = 0; i < n; i++) Repel(root, i, strength);
        }

        private void Insert(Quad q, int i, int depth)
        {
            if (q.Children == null)
            {
                if (q.Body < 0 && q.Mass == 0f) { q.Body = i; q.Mass = 1f; return; }
                if (depth > 24) { q.Mass += 1f; return; } // coincident points: just stack the mass
                int existing = q.Body;
                q.Body = -1;
                q.Mass = 0f;
                q.Children = new Quad[4];
                if (existing >= 0) InsertChild(q, existing, depth);
            }
            InsertChild(q, i, depth);
        }

        private void InsertChild(Quad q, int i, int depth)
        {
            float half = q.Size / 2f;
            int cx = X[i] >= q.X0 + half ? 1 : 0;
            int cy = Y[i] >= q.Y0 + half ? 1 : 0;
            int k = cy * 2 + cx;
            if (q.Children[k] == null) q.Children[k] = new Quad { X0 = q.X0 + cx * half, Y0 = q.Y0 + cy * half, Size = half };
            Insert(q.Children[k], i, depth + 1);
        }

        private void ComputeMass(Quad q)
        {
            if (q.Children == null)
            {
                if (q.Body >= 0) { q.Cx = X[q.Body]; q.Cy = Y[q.Body]; }
                return;
            }
            float mass = 0f, sx = 0f, sy = 0f;
            foreach (var c in q.Children)
            {
                if (c == null) continue;
                ComputeMass(c);
                mass += c.Mass;
                sx += c.Cx * c.Mass;
                sy += c.Cy * c.Mass;
            }
            q.Mass = mass;
            if (mass > 0f) { q.Cx = sx / mass; q.Cy = sy / mass; }
        }

        private void Repel(Quad q, int i, float strength)
        {
            if (q == null || q.Mass == 0f || q.Body == i && q.Children == null) return;

            float dx = X[i] - q.Cx, dy = Y[i] - q.Cy;
            float d2 = dx * dx + dy * dy;
            bool leaf = q.Children == null;
            if (leaf || q.Size * q.Size / Math.Max(d2, 1e-6f) < Theta2)
            {
                if (d2 < MinDistance2) { dx = (i % 3 + 0.5f) * 0.1f; dy = 0.1f; d2 = MinDistance2; }
                float f = strength * q.Mass / d2;
                m_Vx[i] += dx * f;
                m_Vy[i] += dy * f;
                return;
            }
            foreach (var c in q.Children) Repel(c, i, strength);
        }
    }
}
