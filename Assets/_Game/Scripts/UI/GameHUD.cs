using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace KatKat
{
    /// <summary>
    /// Prototip arayüzü (IMGUI): durum paneli, patlama sırası, sonuçlar, ağır çekim uyarısı ve dünya etiketleri.
    /// 1080p referansına göre ölçeklenir. F1: ip kuvveti debug etiketleri.
    /// </summary>
    public class GameHUD : MonoBehaviour
    {
        private const float RefHeight = 1080f;
        private const float Pad = 16f;

        private const float FloatDuration = 1.6f;
        private const float FloatRise = 50f;

        private static readonly List<Rect> UiRects = new List<Rect>();
        private static float _scale = 1f;

        private struct FloatText
        {
            public Vector3 World;
            public string Text;
            public Color Color;
            public float Born;
        }

        private static readonly List<FloatText> Floats = new List<FloatText>();

        /// <summary>Dünyadaki bir noktadan yükselip sönen kısa yazı (ör. "−1 CAN", "İP KOPTU"). Gerçek zamanla akar.</summary>
        public static void Float(Vector3 world, string text, Color color)
        {
            Floats.Add(new FloatText { World = world, Text = text, Color = color, Born = Time.unscaledTime });
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            UiRects.Clear();
            Floats.Clear();
            _scale = 1f;
        }

        /// <summary>Ekran koordinatındaki (Input System) fare bir HUD panelinin üstünde mi?</summary>
        public static bool IsPointerOverUI(Vector2 screenPos)
        {
            var p = new Vector2(screenPos.x, Screen.height - screenPos.y) / _scale;
            foreach (var r in UiRects)
            {
                if (r.Contains(p)) return true;
            }
            return false;
        }

        private GameManager _gm;
        private Camera _cam;
        private bool _debug;
        private GUIStyle _box, _label, _title, _titleCenter, _big, _small, _button, _tag;
        private Texture2D _boxTex, _barTex;

        public void Init(GameManager gm) => _gm = gm;

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.f1Key.wasPressedThisFrame) _debug = !_debug;
        }

        private void OnGUI()
        {
            if (_gm == null || _gm.Run == null) return;
            if (_cam == null) _cam = Camera.main;
            EnsureStyles();

            _scale = Screen.height / RefHeight;
            GUI.matrix = Matrix4x4.Scale(new Vector3(_scale, _scale, 1f));
            UiRects.Clear();
            float width = Screen.width / _scale;

            DrawWorldLabels();
            DrawFloats();
            DrawStatus();

            switch (_gm.State)
            {
                case GameState.Planning: DrawSequencePanel(width); break;
                case GameState.Detonating:
                case GameState.Settling: DrawRunningPanel(width); break;
                case GameState.Results: DrawResults(width); break;
                case GameState.GameOver: DrawGameOver(width); break;
            }

            DrawSlowMo(width);
            DrawMessage(width);
            DrawHover();
            DrawHelp(width);
        }

        private void DrawStatus()
        {
            var cfg = _gm.Config;
            var rect = Panel(new Rect(Pad, Pad, 380f, 150f + _gm.RopeTypes.Length * 34f));
            float x = rect.x + 14f, y = rect.y + 10f, w = rect.width - 28f;

            GUI.Label(new Rect(x, y, w, 36f), $"KAT {_gm.FloorNumber + 1}", _title);
            GUI.Label(new Rect(x + 150f, y + 6f, w - 150f, 30f),
                $"Can {_gm.Run.Lives}/{cfg.startLives}   Skor {_gm.Run.FloorsDescended}", _label);
            y += 42f;

            var budget = _gm.Budget;
            GUI.Label(new Rect(x, y, w, 24f), $"İp bütçesi  {budget.Remaining:0.0} / {budget.Total:0.0}", _label);
            y += 26f;
            DrawBar(new Rect(x, y, w, 12f), budget.Total > 0f ? budget.Remaining / budget.Total : 0f,
                new Color(0.35f, 0.85f, 0.45f));
            y += 22f;

            for (int i = 0; i < _gm.RopeTypes.Length; i++)
            {
                var t = _gm.RopeTypes[i];
                bool selected = i == _gm.SelectedRopeType;
                string text = $"{(selected ? "> " : "   ")}[{i + 1}] {t.DisplayName}   {t.CostPerMeter:0.0}/m   ~{t.BreakForce:0} N";
                if (GUI.Button(new Rect(x, y, w, 30f), text, _button)) _gm.SelectRopeType(i);
                y += 34f;
            }

            int slowMo = _gm.SlowMo.IsMonitoring ? _gm.SlowMo.ChargesLeft : cfg.slowMoCharges;
            GUI.Label(new Rect(x, y + 4f, w, 24f), $"Ağır çekim hakkı: {slowMo}", _label);
        }

        private void DrawSequencePanel(float width)
        {
            var seq = _gm.Sequence;
            float h = 190f + seq.Order.Count * 40f + 100f;
            var rect = Panel(new Rect(width - 400f - Pad, Pad, 400f, h));
            float x = rect.x + 14f, y = rect.y + 10f, w = rect.width - 28f;

            GUI.Label(new Rect(x, y, w, 32f), "PATLAMA SIRASI", _title);
            y += 36f;
            GUI.Label(new Rect(x, y, w, 40f), "Önce patlayan sıra, binanın çökeceği yönü belirler.", _small);
            y += 44f;
            GUI.Label(new Rect(x, y, w, 64f),
                "Koyu mavi kolon: taşıyıcı, patlamaz.\nŞarjlı kolona bağlı ip, kolon patlayınca boşa çıkar.", _small);
            y += 70f;

            for (int i = 0; i < seq.Order.Count; i++)
            {
                var charge = seq.Order[i];
                GUI.Label(new Rect(x, y + 4f, 120f, 30f), $"{i + 1}.  Sıra {charge.Label}", _label);
                if (GUI.Button(new Rect(x + 125f, y, 34f, 32f), "↑", _button)) seq.Move(i, -1);
                if (GUI.Button(new Rect(x + 163f, y, 34f, 32f), "↓", _button)) seq.Move(i, 1);
                if (GUI.Button(new Rect(x + 215f, y, 34f, 32f), "–", _button))
                    charge.Delay = Mathf.Max(0f, charge.Delay - 0.2f);
                GUI.Label(new Rect(x + 252f, y + 4f, 60f, 30f), $"{charge.Delay:0.0} sn", _label);
                if (GUI.Button(new Rect(x + 315f, y, 34f, 32f), "+", _button))
                    charge.Delay = Mathf.Min(ExplosiveCharge.MaxDelay, charge.Delay + 0.2f);
                y += 40f;
            }

            y += 10f;
            if (GUI.Button(new Rect(x, y, w, 46f), "PATLAT   [Enter]", _big)) _gm.Detonate();
            y += 52f;
            if (GUI.Button(new Rect(x, y, w, 32f), "Katı sıfırla (ipler kalır)   [R]", _button)) _gm.QuickReset();
        }

        private void DrawRunningPanel(float width)
        {
            var rect = Panel(new Rect(width - 400f - Pad, Pad, 400f, 150f));
            float x = rect.x + 14f, y = rect.y + 10f, w = rect.width - 28f;
            var seq = _gm.Sequence;
            string status = _gm.State == GameState.Detonating
                ? $"Patlama  {seq.FiredCount}/{seq.Order.Count}"
                : "Enkaz duruluyor…";
            GUI.Label(new Rect(x, y, w, 32f), status, _title);
            y += 40f;
            GUI.Label(new Rect(x, y, w, 26f), $"[Boşluk] Ağır çekim  ({_gm.SlowMo.ChargesLeft} hak)", _label);
            y += 36f;
            if (GUI.Button(new Rect(x, y, w, 32f), "Planlamaya dön   [R]", _button)) _gm.QuickReset();
        }

        private void DrawResults(float width)
        {
            var r = _gm.LastResult;
            var rect = Panel(new Rect(width * 0.5f - 250f, 220f, 500f, 380f));
            float x = rect.x + 20f, y = rect.y + 14f, w = rect.width - 40f;

            GUI.Label(new Rect(x, y, w, 36f), $"KAT {_gm.FloorNumber + 1} SONUCU", _title);
            y += 48f;
            Row(ref y, x, w, "Kurtarılan eşya", $"{r.SavedItems} / {r.TotalItems}");
            Row(ref y, x, w, $"Bölge dışı parça  (×{FloorScorer.PiecePenalty})", r.OutPieces.ToString());
            Row(ref y, x, w, $"Bölge dışı küçük eşya  (×{FloorScorer.SmallItemPenalty})", r.OutSmallItems.ToString());
            Row(ref y, x, w, $"Komşu binaya çarpan  (×{FloorScorer.NeighborPenalty})", r.NeighborHits.ToString());
            Row(ref y, x, w, "Bölge dışı büyük eşya", r.OutBigItems > 0 ? $"{r.OutBigItems}  →  −{r.LivesLost} can" : "0");
            y += 8f;
            GUI.Label(new Rect(x, y, w, 30f),
                $"Ceza {r.Penalty}  →  bütçeden −{r.Penalty * _gm.Config.penaltyBudgetCost:0.0}", _label);
            y += 30f;
            GUI.Label(new Rect(x, y, w, 30f),
                $"Devreden ip: {_gm.Budget.Remaining:0.0}   Sonraki kat bütçesi: {_gm.NextFloorBudget():0.0}", _label);
            y += 44f;

            if (GUI.Button(new Rect(x, y, w * 0.55f, 46f), "Devam   [Enter]", _big)) _gm.ContinueAfterResults();
            if (GUI.Button(new Rect(x + w * 0.58f, y, w * 0.42f, 46f), "Tekrar dene [R]", _button)) _gm.QuickReset();
        }

        private void DrawGameOver(float width)
        {
            var rect = Panel(new Rect(width * 0.5f - 230f, 280f, 460f, 230f));
            float x = rect.x + 20f, y = rect.y + 20f, w = rect.width - 40f;
            GUI.Label(new Rect(x, y, w, 40f), "OYUN BİTTİ", _title);
            y += 52f;
            GUI.Label(new Rect(x, y, w, 30f), $"{_gm.Run.FloorsDescended} kat indin.   Toplam ceza: {_gm.Run.TotalPenalty}", _label);
            y += 60f;
            if (GUI.Button(new Rect(x, y, w, 46f), "Yeni oyun   [Enter]", _big)) _gm.NewRun();
        }

        private void DrawSlowMo(float width)
        {
            if (!_gm.SlowMo.IsActive) return;
            var rect = new Rect(width * 0.5f - 300f, Pad, 600f, 86f);
            GUI.Box(rect, GUIContent.none, _box);
            GUI.Label(new Rect(rect.x, rect.y + 6f, rect.width, 40f), "AĞIR ÇEKİM", _titleCenter);
            GUI.Label(new Rect(rect.x, rect.y + 46f, rect.width, 30f),
                $"İpe tıkla: destekle ({_gm.Config.reinforceCost:0.0})   •   Uçan eşyaya tıkla: ip at", _tag);
        }

        private void DrawMessage(float width)
        {
            if (string.IsNullOrEmpty(_gm.Message)) return;
            float y = _gm.SlowMo.IsActive ? 116f : Pad;
            var rect = new Rect(width * 0.5f - 300f, y, 600f, 44f);
            GUI.Box(rect, GUIContent.none, _box);
            GUI.Label(rect, _gm.Message, _tag);
        }

        private void DrawHover()
        {
            var text = _gm.Placer.HoverText;
            if (string.IsNullOrEmpty(text) || Mouse.current == null) return;
            Vector2 mp = Mouse.current.position.ReadValue();
            var p = new Vector2(mp.x, Screen.height - mp.y) / _scale;
            var size = _small.CalcSize(new GUIContent(text));
            var rect = new Rect(p.x + 18f, p.y + 12f, size.x + 16f, size.y + 10f);
            GUI.Box(rect, GUIContent.none, _box);
            GUI.Label(new Rect(rect.x + 8f, rect.y + 5f, size.x, size.y), text, _small);
        }

        private void DrawHelp(float width)
        {
            string text;
            switch (_gm.State)
            {
                case GameState.Planning:
                    text = "Sol tık: eşya → ankraj / eşya   •   Sağ tık: iptal / ipi sil   •   Sağ sürükle: döndür   •   " +
                           "Tekerlek: yakınlaş   •   Q/E: açı   •   F: odakla   •   1-3: ip tipi   •   F1: debug";
                    break;
                case GameState.Detonating:
                case GameState.Settling:
                    text = "Boşluk: ağır çekim   •   R: planlamaya dön   •   Sağ sürükle: döndür";
                    break;
                default:
                    text = "Enter: devam   •   R: tekrar dene";
                    break;
            }
            var rect = new Rect(Pad, RefHeight - 44f, width - Pad * 2f, 32f);
            GUI.Box(rect, GUIContent.none, _box);
            GUI.Label(rect, text, _tag);
        }

        private void DrawWorldLabels()
        {
            if (_cam == null) return;

            if (_gm.State == GameState.Planning || _gm.State == GameState.Detonating)
            {
                var order = _gm.Sequence.Order;
                for (int i = 0; i < order.Count; i++)
                {
                    if (order[i] == null || order[i].HasDetonated) continue;
                    if (!ToGui(order[i].LabelPosition, out var p)) continue;
                    GUI.Box(new Rect(p.x - 22f, p.y - 16f, 44f, 32f), GUIContent.none, _box);
                    GUI.Label(new Rect(p.x - 22f, p.y - 16f, 44f, 32f), $"{i + 1}", _tag);
                }
            }

            if (_gm.State == GameState.Planning)
            {
                var collapse = _gm.Collapse;
                string text = collapse.HasDirection ? "Tahmini çöküş yönü" : "Tahmini çöküş: merkeze";
                if (ToGui(collapse.HasDirection ? collapse.Tip : collapse.Center, out var p))
                {
                    var size = _small.CalcSize(new GUIContent(text));
                    var r = new Rect(p.x - size.x * 0.5f - 8f, p.y - 40f, size.x + 16f, 26f);
                    GUI.Box(r, GUIContent.none, _box);
                    GUI.Label(new Rect(r.x + 8f, r.y + 3f, size.x, 22f), text, _small);
                }
            }

            if (!_debug) return;
            foreach (var link in _gm.Ropes)
            {
                if (link == null || link.Rope == null || link.Rope.Solver == null) continue;
                var rc = link.Rope;
                if (!ToGui(rc.GetParticlePosition(rc.ParticleCount / 2), out var p)) continue;
                string text = link.IsBroken ? "KOPTU"
                    : link.IsReleased ? "BOŞA ÇIKTI"
                    : $"{link.CurrentForce:0}/{link.EffectiveBreakForce:0} N  tepe {link.PeakForce:0}  uzama {rc.CurrentStressRatio:0.00}";
                var size = _small.CalcSize(new GUIContent(text));
                GUI.Box(new Rect(p.x - size.x * 0.5f - 6f, p.y - 12f, size.x + 12f, 24f), GUIContent.none, _box);
                GUI.Label(new Rect(p.x - size.x * 0.5f, p.y - 10f, size.x, 22f), text, _small);
            }
        }

        private void DrawFloats()
        {
            float now = Time.unscaledTime;
            Floats.RemoveAll(f => now - f.Born > FloatDuration);
            if (_cam == null) return;

            var old = GUI.color;
            foreach (var f in Floats)
            {
                if (!ToGui(f.World, out var p)) continue;
                float t = (now - f.Born) / FloatDuration;
                float alpha = t < 0.7f ? 1f : 1f - (t - 0.7f) / 0.3f;
                p.y -= FloatRise * t;
                var size = _tag.CalcSize(new GUIContent(f.Text));
                var rect = new Rect(p.x - size.x * 0.5f, p.y - size.y * 0.5f, size.x, size.y);

                GUI.color = new Color(0f, 0f, 0f, 0.7f * alpha);
                GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), f.Text, _tag);
                GUI.color = new Color(f.Color.r, f.Color.g, f.Color.b, alpha);
                GUI.Label(rect, f.Text, _tag);
            }
            GUI.color = old;
        }

        private bool ToGui(Vector3 world, out Vector2 gui)
        {
            Vector3 sp = _cam.WorldToScreenPoint(world);
            gui = new Vector2(sp.x, Screen.height - sp.y) / _scale;
            return sp.z > 0f;
        }

        private void Row(ref float y, float x, float w, string label, string value)
        {
            GUI.Label(new Rect(x, y, w * 0.72f, 28f), label, _label);
            GUI.Label(new Rect(x + w * 0.72f, y, w * 0.28f, 28f), value, _label);
            y += 32f;
        }

        private Rect Panel(Rect rect)
        {
            UiRects.Add(rect);
            GUI.Box(rect, GUIContent.none, _box);
            return rect;
        }

        private void DrawBar(Rect rect, float fill, Color color)
        {
            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(rect, _barTex);
            GUI.color = color;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01(fill), rect.height), _barTex);
            GUI.color = old;
        }

        private void EnsureStyles()
        {
            if (_box != null) return;

            _boxTex = new Texture2D(1, 1);
            _boxTex.SetPixel(0, 0, new Color(0.08f, 0.09f, 0.12f, 0.82f));
            _boxTex.Apply();
            _barTex = Texture2D.whiteTexture;

            _box = new GUIStyle { normal = { background = _boxTex } };
            _label = new GUIStyle(GUI.skin.label) { fontSize = 19, normal = { textColor = Color.white } };
            _small = new GUIStyle(_label) { fontSize = 16, wordWrap = true, normal = { textColor = new Color(0.85f, 0.87f, 0.9f) } };
            _title = new GUIStyle(_label) { fontSize = 28, fontStyle = FontStyle.Bold };
            _titleCenter = new GUIStyle(_title) { alignment = TextAnchor.UpperCenter };
            _tag = new GUIStyle(_label) { alignment = TextAnchor.MiddleCenter, fontSize = 18, fontStyle = FontStyle.Bold };
            _button = new GUIStyle(GUI.skin.button) { fontSize = 17, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(10, 6, 4, 4) };
            _big = new GUIStyle(GUI.skin.button) { fontSize = 24, fontStyle = FontStyle.Bold };
        }
    }
}
