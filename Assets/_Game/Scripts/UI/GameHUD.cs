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

        private const float RowRevealInterval = 0.28f;
        private const float RowCountDuration = 0.35f;
        private const float BannerDuration = 2.2f;

        private static readonly Color Accent = new Color(1f, 0.78f, 0.18f);
        private static readonly Color Good = new Color(0.45f, 0.95f, 0.5f);
        private static readonly Color Warn = new Color(1f, 0.78f, 0.25f);
        private static readonly Color Bad = new Color(1f, 0.35f, 0.3f);

        private GameManager _gm;
        private Camera _cam;
        private bool _debug;
        private GUIStyle _box, _label, _title, _titleCenter, _big, _small, _button, _buttonOn, _buttonCenter, _tag, _banner, _hearts;
        private Texture2D _barTex;

        private GameState _lastState = (GameState)(-1);
        private float _resultsStart;
        private int _revealedRows;
        private int _bannerFloor = -1;
        private float _bannerStart = -10f;

        public void Init(GameManager gm) => _gm = gm;

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.f1Key.wasPressedThisFrame) _debug = !_debug;
            if (_gm == null || _gm.Run == null) return;

            var state = _gm.State;
            if (state != _lastState)
            {
                if (state == GameState.Results)
                {
                    _resultsStart = Time.unscaledTime;
                    _revealedRows = 0;
                }
                _lastState = state;
            }

            // Yeni kata geçişte büyük başlık (aynı katı sıfırlamak başlık göstermez)
            if (state == GameState.Planning && _gm.FloorNumber != _bannerFloor)
            {
                _bannerFloor = _gm.FloorNumber;
                _bannerStart = Time.unscaledTime;
                AudioDirector.Play(Sound.FloorStart);
            }
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
            DrawBanner(width);
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
            DrawHearts(new Rect(x + 130f, y + 2f, 130f, 34f), _gm.Run.Lives, cfg.startLives);
            GUI.Label(new Rect(x + 262f, y + 6f, w - 262f, 30f), $"Skor {_gm.Run.FloorsDescended}", _label);
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
                string text = $"[{i + 1}]  {t.DisplayName}   {t.CostPerMeter:0.0}/m   ~{t.BreakForce:0} N";
                var r = new Rect(x, y, w, 30f);
                if (Button(r, text, selected ? _buttonOn : _button)) _gm.SelectRopeType(i);
                DrawSwatch(new Rect(r.xMax - 18f, r.y + 9f, 10f, 12f), t.Color);
                y += 34f;
            }

            int slowMo = _gm.SlowMo.IsMonitoring ? _gm.SlowMo.ChargesLeft : cfg.slowMoCharges;
            GUI.Label(new Rect(x, y + 4f, w, 24f), $"Ağır çekim hakkı: {slowMo}", _label);
        }

        private void DrawSequencePanel(float width)
        {
            var seq = _gm.Sequence;
            float h = 190f + seq.Order.Count * 40f + 100f + 96f;
            var rect = Panel(new Rect(width - 400f - Pad, Pad, 400f, h));
            float x = rect.x + 14f, y = rect.y + 10f, w = rect.width - 28f;

            GUI.Label(new Rect(x, y, w, 32f), "PATLAMA SIRASI", _title);
            y += 36f;
            GUI.Label(new Rect(x, y, w, 40f), "Önce patlayan sıra, binanın çökeceği yönü belirler.", _small);
            y += 44f;
            GUI.Label(new Rect(x, y, w, 64f),
                "Koyu mavi kolon: taşıyıcı, patlamaz.\nŞarjlı kolona bağlı ip, kolon patlayınca boşa çıkar.", _small);
            y += 70f;
            DrawPrediction(x, ref y, w);

            for (int i = 0; i < seq.Order.Count; i++)
            {
                var charge = seq.Order[i];
                GUI.Label(new Rect(x, y + 4f, 120f, 30f), $"{i + 1}.  Sıra {charge.Label}", _label);
                if (Button(new Rect(x + 125f, y, 34f, 32f), "↑", _buttonCenter)) seq.Move(i, -1);
                if (Button(new Rect(x + 163f, y, 34f, 32f), "↓", _buttonCenter)) seq.Move(i, 1);
                if (Button(new Rect(x + 215f, y, 34f, 32f), "–", _buttonCenter))
                    charge.Delay = Mathf.Max(0f, charge.Delay - 0.2f);
                GUI.Label(new Rect(x + 252f, y + 4f, 60f, 30f), $"{charge.Delay:0.0} sn", _label);
                if (Button(new Rect(x + 315f, y, 34f, 32f), "+", _buttonCenter))
                    charge.Delay = Mathf.Min(ExplosiveCharge.MaxDelay, charge.Delay + 0.2f);
                y += 40f;
            }

            y += 10f;
            if (Button(new Rect(x, y, w, 46f), "PATLAT   [Enter]", _big)) _gm.Detonate();
            y += 52f;
            if (Button(new Rect(x, y, w, 32f), "Katı sıfırla (ipler kalır)   [R]", _button)) _gm.QuickReset();
        }

        /// <summary>Planlamadaki tahmin özeti: kaç eşya düşer, kaç ip kopar, tahmini ceza ve sınır.</summary>
        private void DrawPrediction(float x, ref float y, float w)
        {
            var impact = _gm.Impact;
            if (impact == null || !impact.HasData)
            {
                y += 96f;
                return;
            }

            int limit = _gm.PenaltyLimit;
            bool lifeRisk = impact.BigFalls > 0 || impact.EstimatedPenalty > limit;
            var old = GUI.color;
            GUI.color = lifeRisk ? Bad : impact.SmallFalls > 0 || impact.Snaps > 0 ? Warn : Good;
            string falls = impact.BigFalls + impact.SmallFalls == 0
                ? "Tahmin: hiçbir eşya düşmez"
                : $"Tahmin: {impact.BigFalls} büyük, {impact.SmallFalls} küçük eşya düşer";
            GUI.Label(new Rect(x, y, w, 26f), falls, _label);
            GUI.color = old;
            y += 28f;
            GUI.Label(new Rect(x, y, w, 24f),
                $"{impact.Snaps} ip kopar  •  {impact.OutwardWalls} duvar dışa  •  ceza ~{impact.EstimatedPenalty} / sınır {limit}",
                _small);
            y += 26f;
            GUI.Label(new Rect(x, y, w, 40f), "Oklar yaklaşık: yeşil kalır, sarı kayar, kırmızı düşer.", _small);
            y += 42f;
        }

        private void DrawRunningPanel(float width)
        {
            var rect = Panel(new Rect(width - 400f - Pad, Pad, 400f, 200f));
            float x = rect.x + 14f, y = rect.y + 10f, w = rect.width - 28f;
            var seq = _gm.Sequence;
            string status = _gm.State == GameState.Detonating
                ? $"Patlama  {seq.FiredCount}/{seq.Order.Count}"
                : "Enkaz duruluyor…";
            GUI.Label(new Rect(x, y, w, 32f), status, _title);
            y += 40f;
            GUI.Label(new Rect(x, y, w, 26f), $"[Boşluk] Ağır çekim  ({_gm.SlowMo.ChargesLeft} hak)", _label);
            y += 32f;

            // Anlık ceza; sınır çizgisini geçerse bir can gider
            var live = _gm.LiveResult;
            int limit = _gm.PenaltyLimit;
            GUI.Label(new Rect(x, y, w, 24f), $"Ceza {live.Penalty} / sınır {limit}", _label);
            y += 26f;
            float scaleMax = Mathf.Max(1f, limit * 1.5f);
            var bar = new Rect(x, y, w, 12f);
            DrawBar(bar, live.Penalty / scaleMax, live.Penalty > limit ? Bad : live.Penalty > limit * 0.7f ? Warn : Good);
            DrawSwatch(new Rect(bar.x + bar.width * (limit / scaleMax) - 1.5f, bar.y - 4f, 3f, bar.height + 8f), Color.white);
            y += 24f;
            if (Button(new Rect(x, y, w, 32f), "Planlamaya dön   [R]", _button)) _gm.QuickReset();
        }

        private void DrawResults(float width)
        {
            var r = _gm.LastResult;
            var rect = Panel(new Rect(width * 0.5f - 260f, 200f, 520f, 460f));
            DrawItemReports(new Rect(rect.xMax + 12f, rect.y, 420f, 0f));
            float x = rect.x + 24f, y = rect.y + 16f, w = rect.width - 48f;
            float t = Time.unscaledTime - _resultsStart;

            GUI.Label(new Rect(x, y, w, 36f), $"KAT {_gm.FloorNumber + 1} SONUCU", _title);
            y += 50f;

            int row = 0;
            ResultRow(ref y, x, w, t, row++, "Kurtarılan eşya", r.SavedItems, $" / {r.TotalItems}",
                r.SavedItems == r.TotalItems ? Good : Warn, false);
            ResultRow(ref y, x, w, t, row++, $"Bölge dışı parça  (×{FloorScorer.PiecePenalty})", r.OutPieces, "",
                r.OutPieces == 0 ? Good : Warn, false);
            ResultRow(ref y, x, w, t, row++, $"Bölge dışı küçük eşya  (×{FloorScorer.SmallItemPenalty})", r.OutSmallItems, "",
                r.OutSmallItems == 0 ? Good : Warn, false);
            ResultRow(ref y, x, w, t, row++, $"Komşu binaya çarpan  (×{FloorScorer.NeighborPenalty})", r.NeighborHits, "",
                r.NeighborHits == 0 ? Good : Bad, false);
            ResultRow(ref y, x, w, t, row++, "Bölge dışı büyük eşya", r.OutBigItems,
                r.OutBigItems > 0 ? $"   →  −{r.OutBigItems} can" : "", r.OutBigItems == 0 ? Good : Bad, r.OutBigItems > 0);
            ResultRow(ref y, x, w, t, row++, $"Ceza / sınır {r.PenaltyLimit}", r.Penalty,
                r.OverLimit ? "   →  aşıldı, −1 can" : "", r.OverLimit ? Bad : Good, r.OverLimit);

            // Özet, tüm satırlar geldikten sonra belirir
            if (t >= row * RowRevealInterval)
            {
                y += 10f;
                var old = GUI.color;
                GUI.color = r.Penalty == 0 ? Good : r.Penalty <= 5 ? Warn : Bad;
                GUI.Label(new Rect(x, y, w, 34f),
                    $"Ceza {r.Penalty}   →   bütçeden −{r.Penalty * _gm.Config.penaltyBudgetCost:0.0}", _title);
                GUI.color = old;
                y += 40f;
                GUI.Label(new Rect(x, y, w, 28f),
                    $"Devreden ip: {_gm.Budget.Remaining:0.0}     Sonraki kat bütçesi: {_gm.NextFloorBudget():0.0}", _label);
                y += 38f;
            }
            else y += 88f;
            y += 30f;

            if (Button(new Rect(x, y, w * 0.55f, 48f), "Devam   [Enter]", _big)) _gm.ContinueAfterResults();
            if (Button(new Rect(x + w * 0.58f, y, w * 0.42f, 48f), "Tekrar dene  [R]", _button)) _gm.QuickReset();
        }

        /// <summary>Eşya başına ne olduğu: hangi ip tuttu, hangisi koptu ya da boşa çıktı.</summary>
        private void DrawItemReports(Rect at)
        {
            var reports = _gm.LastItemReports;
            if (reports == null || reports.Count == 0) return;
            float t = Time.unscaledTime - _resultsStart;
            var rect = Panel(new Rect(at.x, at.y, at.width, 56f + reports.Count * 30f));
            float x = rect.x + 16f, y = rect.y + 12f, w = rect.width - 32f;
            GUI.Label(new Rect(x, y, w, 30f), "EŞYALAR", _title);
            y += 40f;

            var old = GUI.color;
            for (int i = 0; i < reports.Count; i++)
            {
                float alpha = Mathf.Clamp01((t - i * 0.12f) / 0.2f);
                var r = reports[i];
                Color c = r.Inside ? Good : r.IsBig ? Bad : Warn;
                GUI.color = new Color(c.r, c.g, c.b, alpha);
                GUI.Label(new Rect(x, y, 130f, 28f), r.IsBig ? $"■ {r.Name}" : $"• {r.Name}", _small);
                GUI.color = new Color(1f, 1f, 1f, alpha);
                GUI.Label(new Rect(x + 132f, y, w - 132f, 28f), r.Text, _small);
                y += 30f;
            }
            GUI.color = old;
        }

        private void DrawGameOver(float width)
        {
            var rect = Panel(new Rect(width * 0.5f - 230f, 280f, 460f, 230f));
            float x = rect.x + 20f, y = rect.y + 20f, w = rect.width - 40f;
            GUI.Label(new Rect(x, y, w, 40f), "OYUN BİTTİ", _title);
            y += 52f;
            GUI.Label(new Rect(x, y, w, 30f), $"{_gm.Run.FloorsDescended} kat indin.   Toplam ceza: {_gm.Run.TotalPenalty}", _label);
            y += 60f;
            if (Button(new Rect(x, y, w, 46f), "Yeni oyun   [Enter]", _big)) _gm.NewRun();
        }

        private void DrawSlowMo(float width)
        {
            if (!_gm.SlowMo.IsActive) return;
            var rect = new Rect(width * 0.5f - 300f, Pad, 600f, 86f);
            GUI.Box(rect, GUIContent.none, _box);
            var oldColor = GUI.color;
            GUI.color = Color.Lerp(Color.white, Accent, 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f));
            GUI.Label(new Rect(rect.x, rect.y + 6f, rect.width, 40f), "AĞIR ÇEKİM", _titleCenter);
            GUI.color = oldColor;
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

            if (_gm.State == GameState.Planning && _gm.Impact != null)
            {
                var old = GUI.color;
                foreach (var label in _gm.Impact.Labels)
                {
                    if (!ToGui(label.World, out var lp)) continue;
                    var size = _small.CalcSize(new GUIContent(label.Text));
                    var r = new Rect(lp.x - size.x * 0.5f - 6f, lp.y - 12f, size.x + 12f, 24f);
                    GUI.Box(r, GUIContent.none, _box);
                    GUI.color = label.Color;
                    GUI.Label(new Rect(r.x + 6f, r.y + 1f, size.x, 22f), label.Text, _small);
                    GUI.color = old;
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

        /// <summary>Tıklandığında ses çalan buton.</summary>
        private static bool Button(Rect rect, string text, GUIStyle style)
        {
            if (!GUI.Button(rect, text, style)) return false;
            AudioDirector.Play(Sound.UiClick);
            return true;
        }

        /// <summary>Sonuç satırı: sırayla belirir, sayı sıfırdan sayarak gelir; geldiği anda ses çalar.</summary>
        private void ResultRow(ref float y, float x, float w, float t, int index, string label, int value, string suffix,
            Color color, bool lifeLost)
        {
            float start = index * RowRevealInterval;
            if (t < start)
            {
                y += 34f;
                return;
            }

            if (index >= _revealedRows)
            {
                _revealedRows = index + 1;
                AudioDirector.Play(lifeLost ? Sound.LifeLost : Sound.ResultsTick);
            }

            float k = Mathf.Clamp01((t - start) / RowCountDuration);
            float alpha = Mathf.Clamp01((t - start) / 0.15f);
            int shown = Mathf.RoundToInt(Mathf.Lerp(0f, value, 1f - (1f - k) * (1f - k)));

            var old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.Label(new Rect(x + (1f - alpha) * 20f, y, w * 0.68f, 30f), label, _label);
            GUI.color = new Color(color.r, color.g, color.b, alpha);
            GUI.Label(new Rect(x + w * 0.68f, y, w * 0.32f, 30f), shown + suffix, _label);
            GUI.color = old;
            y += 34f;
        }

        private void DrawHearts(Rect rect, int lives, int max)
        {
            var old = GUI.color;
            for (int i = 0; i < max; i++)
            {
                GUI.color = i < lives ? Bad : new Color(1f, 1f, 1f, 0.22f);
                GUI.Label(new Rect(rect.x + i * 30f, rect.y, 30f, rect.height), "♥", _hearts);
            }
            GUI.color = old;
        }

        private void DrawSwatch(Rect rect, Color color)
        {
            var old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, _barTex);
            GUI.color = old;
        }

        /// <summary>Yeni kat başlarken ortada büyüyüp sönen "KAT N" başlığı.</summary>
        private void DrawBanner(float width)
        {
            float t = (Time.unscaledTime - _bannerStart) / BannerDuration;
            if (t < 0f || t > 1f || _gm.State != GameState.Planning) return;

            float alpha = t < 0.12f ? t / 0.12f : t > 0.75f ? 1f - (t - 0.75f) / 0.25f : 1f;
            float scale = Mathf.Lerp(1.15f, 1f, Mathf.Clamp01(t / 0.2f));
            var rect = new Rect(0f, RefHeight * 0.32f, width, 120f);
            string text = $"KAT {_gm.FloorNumber + 1}";

            var oldMatrix = GUI.matrix;
            GUIUtility.ScaleAroundPivot(new Vector2(scale, scale), new Vector2(width * 0.5f, rect.center.y) * _scale);
            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f * alpha);
            GUI.Label(new Rect(rect.x + 4f, rect.y + 4f, rect.width, rect.height), text, _banner);
            GUI.color = new Color(Accent.r, Accent.g, Accent.b, alpha);
            GUI.Label(rect, text, _banner);
            GUI.color = new Color(1f, 1f, 1f, alpha * 0.9f);
            GUI.Label(new Rect(rect.x, rect.yMax - 8f, rect.width, 32f), "Planla  •  Bağla  •  Patlat", _tag);
            GUI.color = old;
            GUI.matrix = oldMatrix;
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

            _barTex = Texture2D.whiteTexture;
            var font = _gm.Content != null ? _gm.Content.UiFont : null;
            var dark = new Color(0.1f, 0.09f, 0.07f);

            _box = new GUIStyle
            {
                normal = { background = Rounded(new Color(0.07f, 0.08f, 0.11f, 0.86f), new Color(1f, 0.78f, 0.18f, 0.28f), 10) },
                border = new RectOffset(12, 12, 12, 12)
            };
            _label = new GUIStyle(GUI.skin.label) { font = font, fontSize = 19, normal = { textColor = Color.white } };
            _small = new GUIStyle(_label) { fontSize = 16, wordWrap = true, normal = { textColor = new Color(0.82f, 0.85f, 0.9f) } };
            _title = new GUIStyle(_label) { fontSize = 28, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.96f, 0.88f) } };
            _titleCenter = new GUIStyle(_title) { alignment = TextAnchor.UpperCenter };
            _tag = new GUIStyle(_label) { alignment = TextAnchor.MiddleCenter, fontSize = 18, fontStyle = FontStyle.Bold };
            _banner = new GUIStyle(_label) { alignment = TextAnchor.MiddleCenter, fontSize = 96, fontStyle = FontStyle.Bold };
            _hearts = new GUIStyle(_label) { fontSize = 28, alignment = TextAnchor.MiddleLeft };

            _button = ButtonStyle(font, 17, TextAnchor.MiddleLeft, new Color(0.17f, 0.19f, 0.25f, 0.95f),
                new Color(0.25f, 0.28f, 0.36f, 0.98f), Accent, Color.white, dark);
            _buttonOn = ButtonStyle(font, 17, TextAnchor.MiddleLeft, new Color(0.36f, 0.3f, 0.12f, 0.98f),
                new Color(0.45f, 0.38f, 0.15f, 1f), Accent, Accent, dark);
            _buttonOn.fontStyle = FontStyle.Bold;
            _buttonCenter = new GUIStyle(_button) { alignment = TextAnchor.MiddleCenter, padding = new RectOffset(0, 0, 2, 2) };
            _big = ButtonStyle(font, 24, TextAnchor.MiddleCenter, Accent, new Color(1f, 0.86f, 0.38f),
                new Color(0.85f, 0.6f, 0.1f), dark, dark);
            _big.fontStyle = FontStyle.Bold;
        }

        private static GUIStyle ButtonStyle(Font font, int size, TextAnchor align, Color normal, Color hover, Color active,
            Color text, Color activeText)
        {
            var edge = new Color(1f, 1f, 1f, 0.08f);
            return new GUIStyle
            {
                font = font,
                fontSize = size,
                alignment = align,
                padding = new RectOffset(12, 8, 4, 4),
                border = new RectOffset(9, 9, 9, 9),
                normal = { background = Rounded(normal, edge, 7), textColor = text },
                hover = { background = Rounded(hover, edge, 7), textColor = text },
                active = { background = Rounded(active, edge, 7), textColor = activeText },
                focused = { background = Rounded(normal, edge, 7), textColor = text }
            };
        }

        /// <summary>Kenarı yumuşatılmış, ince çerçeveli yuvarlak köşeli doku (9-slice ile esnetilir).</summary>
        private static Texture2D Rounded(Color fill, Color border, int radius)
        {
            int size = radius * 2 + 4;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
                { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // Yuvarlak köşeli dikdörtgene işaretli uzaklık (içeride negatif)
                float qx = Mathf.Abs(x + 0.5f - half) - (half - radius);
                float qy = Mathf.Abs(y + 0.5f - half) - (half - radius);
                float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
                float d = outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
                float cover = Mathf.Clamp01(0.5f - d);
                float edge = Mathf.Clamp01(1.5f - Mathf.Abs(d + 1f));
                var c = Color.Lerp(fill, new Color(border.r, border.g, border.b, 1f), border.a * edge);
                c.a = Mathf.Max(fill.a, border.a * edge) * cover;
                px[y * size + x] = c;
            }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }
    }
}
