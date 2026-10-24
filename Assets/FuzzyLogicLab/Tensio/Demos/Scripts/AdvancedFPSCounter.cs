using System.Collections.Generic;
using UnityEngine;

namespace Tensio.Demos
{
    [AddComponentMenu("Tensio/Demo/Advanced FPS Counter")]
    public class AdvancedFPSCounter : MonoBehaviour
    {
        [Header("Settings")] public Color graphColor = Color.green;
        public Vector2 position = new Vector2(10, 10);
        public int graphHeight = 50;
        public int graphWidth = 150;
        public float updateInterval = 0.5f;

        private float accum = 0;
        private int frames = 0;
        private float timeLeft;

        private float currentFPS;
        private float minFPS = float.MaxValue;
        private float maxFPS = 0;
        private float avgFPS;
        private float msec;

        private List<float> fpsHistory = new List<float>();
        private GUIStyle textStyle = new GUIStyle();
        private Texture2D graphBackground;
        private Material lineMaterial;

        void Awake()
        {
            timeLeft = updateInterval;
            textStyle.normal.textColor = Color.white;
            textStyle.fontSize = 14;
            textStyle.fontStyle = FontStyle.Bold;

            graphBackground = new Texture2D(1, 1);
            graphBackground.SetPixel(0, 0, new Color(0, 0, 0, 0.5f));
            graphBackground.Apply();

            // Create a simple material for drawing lines
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            lineMaterial = new Material(shader);
            lineMaterial.hideFlags = HideFlags.HideAndDontSave;
            // Turn on alpha blending
            lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            // Turn backface culling off
            lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            // Turn off depth writes
            lineMaterial.SetInt("_ZWrite", 0);
        }

        void Update()
        {
            float deltaTime = Time.unscaledDeltaTime;
            accum += Time.unscaledDeltaTime;
            frames++;
            msec = deltaTime * 1000.0f;

            timeLeft -= deltaTime;
            if (timeLeft <= 0.0)
            {
                currentFPS = frames / accum;

                if (currentFPS < minFPS && frames > 10) minFPS = currentFPS;
                if (currentFPS > maxFPS) maxFPS = currentFPS;

                fpsHistory.Add(currentFPS);
                if (fpsHistory.Count > graphWidth) fpsHistory.RemoveAt(0);

                // Calculate Average from history
                float total = 0f;
                for (int i = 0; i < fpsHistory.Count; i++)
                {
                    total += fpsHistory[i];
                }
                avgFPS = total / fpsHistory.Count;

                timeLeft = updateInterval;
                accum = 0.0f;
                frames = 0;
            }
        }

        void OnGUI()
        {
            GUI.DrawTexture(new Rect(position.x, position.y, graphWidth + 10, graphHeight + 85), graphBackground);

            string label = string.Format(" FPS: {0:F1}\n MS: {1:F1}\n AVG: {2:F1}\n MIN: {3:F1} | MAX: {4:F1}",
                currentFPS, msec, avgFPS, minFPS == float.MaxValue ? 0 : minFPS, maxFPS);

            GUI.Label(new Rect(position.x + 5, position.y + 5, 200, 100), label, textStyle);

            if (Event.current.type == EventType.Repaint)
            {
                DrawGraphGL();
            }
        }

        void DrawGraphGL()
        {
            if (fpsHistory.Count < 2) return;

            lineMaterial.SetPass(0);

            GL.PushMatrix();
            GL.LoadPixelMatrix();
            GL.Begin(GL.LINES);
            GL.Color(graphColor);

            for (int i = 0; i < fpsHistory.Count - 1; i++)
            {
                float x1 = position.x + 5 + i;
                float x2 = position.x + 5 + i + 1;

                float val1 = Mathf.Clamp(fpsHistory[i], 0, 120);
                float val2 = Mathf.Clamp(fpsHistory[i + 1], 0, 120);

                float y1 = position.y + 80 + graphHeight - (val1 * (graphHeight / 120f));
                float y2 = position.y + 80 + graphHeight - (val2 * (graphHeight / 120f));

                GL.Vertex3(x1, y1, 0);
                GL.Vertex3(x2, y2, 0);
            }

            GL.End();
            GL.PopMatrix();
        }
    }
}