using UnityEditor;
using UnityEngine;

namespace Tensio.Editor
{
    [InitializeOnLoad]
    public class TensioWelcomeWindow : EditorWindow
    {
        private const string SessionKey = "TensioWelcome_ShownThisSession";
        private const string PrefsKey = "Tensio_Welcome_Shown_V1.2.3";

        private const string AssetStoreURL = "https://assetstore.unity.com/packages/tools/physics/tensio-advanced-rope-physics-358842";
        private const string DocsURL = "https://fatihbykl.github.io/docs/tensio/";
        private const string DiscordURL = "https://discord.gg/CGpunqn49e";
        private const string SupportEmail = "devbayk@gmail.com";

        private Texture2D _bannerTexture;
        private GUIStyle _headerStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _sectionTitleStyle;

        static TensioWelcomeWindow()
        {
            EditorApplication.delayCall += OnEditorStartup;
        }

        private static void OnEditorStartup()
        {
            if (SessionState.GetBool(SessionKey, false))
                return;

            SessionState.SetBool(SessionKey, true);

            if (!EditorPrefs.HasKey(PrefsKey))
            {
                ShowWindow();
            }
        }

        [MenuItem("Window/Tensio/Welcome Window")]
        public static void ShowWindow()
        {
            TensioWelcomeWindow window = GetWindow<TensioWelcomeWindow>(true, "Tensio | Welcome", true);
            window.minSize = new Vector2(550, 700);
            window.maxSize = new Vector2(550, 700);
            window.ShowUtility();
        }

        private void OnEnable()
        {
            _bannerTexture = Resources.Load<Texture2D>("TensioBanner");
        }

        private void OnGUI()
        {
            InitStyles();

            if (_bannerTexture != null)
            {
                float headerWidth = position.width;
                float headerHeight = headerWidth * (_bannerTexture.height / (float)_bannerTexture.width);
                Rect bannerRect = GUILayoutUtility.GetRect(headerWidth, headerHeight);
                GUI.DrawTexture(bannerRect, _bannerTexture, ScaleMode.ScaleToFit);
            }
            else
            {
                Rect rect = GUILayoutUtility.GetRect(position.width, 120);
                EditorGUI.DrawRect(rect, new Color(0.12f, 0.12f, 0.12f));
                GUI.Label(rect, "FUZZY LOGIC LABS", new GUIStyle(EditorStyles.whiteLargeLabel) { alignment = TextAnchor.MiddleCenter, fontSize = 24 });
            }

            GUILayout.Space(10);

            EditorGUILayout.LabelField("WELCOME TO TENSIO V1.2.3!", _headerStyle);
            GUILayout.Space(5);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Space(5);
                EditorGUILayout.LabelField("What's New", _sectionTitleStyle);
                EditorGUILayout.LabelField(
                "<b>Material Validation & Safe Job Completion:</b> Resolved AtomicSafetyHandle errors caused by unassigned materials under batching. Added explicit runtime warning messages, safe job completion fallbacks, and inspector warnings \n" +
                "<b>Auto-Initialization & Pin Management:</b> Implemented automatic control point generation via Reset() and OnEnable(), anchoring pins directly to transform.position and eliminating empty initial states.\n" +
                "<b>Scene Framing & Transform Snapping:</b> Added 'Focus Pins' actions to both Inspector and Scene Toolbar, alongside one-click snapping tools to synchronize divergent rope points and GameObject positions.\n" +
                "<b>Streamlined Workflow & Menu Items:</b> Fixed the scene reset action to target the current transform, and added GameObject/Hierarchy creation shortcuts (GameObject > Tensio) that auto-configure rope components, materials, and camera focus.\n" + 
                "<b>Compatibility Issues:</b> Errors that appeared in some unity versions have been fixed."
                , _bodyStyle);
                ;
                GUILayout.Space(5);
            }

            GUILayout.Space(10);

            EditorGUILayout.LabelField("A note from the developer:", _sectionTitleStyle);
            EditorGUILayout.LabelField(
                "Thanks for purchasing Tensio! " +
                "If you like it, leaving a review would be a massive help for the project's growth! " +
                "For any issues, feedback, or bugs, feel free to reach out via email or Discord.", _bodyStyle);

            GUILayout.Space(15);

#if !TENSIO_SPLINES_SUPPORT
            EditorGUILayout.HelpBox("💡 Did you know? Tensio supports Unity Splines! Install 'com.unity.splines' from the Package Manager to unlock Spline Integration.", MessageType.Info);
#else
            EditorGUILayout.HelpBox("✅ Unity Splines Integration is Active.", MessageType.None);
#endif

            float buttonHeight = 32f;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("📖 Documentation", GUILayout.Height(buttonHeight)))
                    Application.OpenURL(DocsURL);

                GUI.backgroundColor = new Color(1f, 0.85f, 0.3f);
                if (GUILayout.Button("⭐ Leave a Review", GUILayout.Height(buttonHeight)))
                    Application.OpenURL(AssetStoreURL);
                GUI.backgroundColor = Color.white;
            }

            GUILayout.Space(2);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.backgroundColor = new Color(0.6f, 0.65f, 1f);
                if (GUILayout.Button("💬 Discord Support", GUILayout.Height(buttonHeight)))
                    Application.OpenURL(DiscordURL);

                GUI.backgroundColor = new Color(1f, 0.85f, 0.85f);
                if (GUILayout.Button("🐛 Email Support", GUILayout.Height(buttonHeight)))
                {
                    string mailto = $"mailto:{SupportEmail}?subject=Tensio v1.2 Support Request";
                    Application.OpenURL(mailto);
                }
                GUI.backgroundColor = Color.white;
            }

            GUILayout.FlexibleSpace();

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                bool showOnStart = !EditorPrefs.HasKey(PrefsKey);
                bool toggle = EditorGUILayout.ToggleLeft(" Show this window on startup", showOnStart);

                if (toggle != showOnStart)
                {
                    if (toggle) EditorPrefs.DeleteKey(PrefsKey);
                    else EditorPrefs.SetInt(PrefsKey, 1);
                }
            }
        }

        private void InitStyles()
        {
            if (_headerStyle != null) return;

            _headerStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };

            _headerStyle.normal.textColor = new Color(0.3f, 0.7f, 1.0f);

            _sectionTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 15
            };

            _sectionTitleStyle.normal.textColor = Color.white;

            _bodyStyle = new GUIStyle(EditorStyles.label)
            {
                wordWrap = true,
                fontSize = 12,
                richText = true,
                padding = new RectOffset(5, 5, 0, 0)
            };
        }
    }
}