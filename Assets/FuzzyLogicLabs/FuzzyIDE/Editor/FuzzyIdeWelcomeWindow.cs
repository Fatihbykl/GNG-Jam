using System;
using FuzzyLogicLabs.FuzzyIDE.Editor.Git;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI;
using FuzzyLogicLabs.FuzzyIDE.Editor.Terminal;
using FuzzyLogicLabs.FuzzyIDE.Editor.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE
{
    /// <summary>
    /// Welcome window: shown once per install (and from the Window menu). Opens the tools, points to the docs and
    /// asks for the two things that help the project most: an Asset Store review and joining the Discord.
    /// </summary>
    [InitializeOnLoad]
    public class FuzzyIdeWelcomeWindow : EditorWindow
    {
        private const string Version = "1.1.0";
        private const string SessionKey = "FuzzyIdeWelcome_ShownThisSession";
        private const string PrefsKey = "FuzzyIde_Welcome_Shown_V1.1.0";
        private const string ReviewClickedKey = "FuzzyIde_Welcome_ReviewClicked";

        private const string AssetStoreURL = "https://assetstore.unity.com/packages/tools/utilities/fuzzyide-integrated-code-terminal-environment-395140";
        private const string ReviewURL = AssetStoreURL + "#reviews";
        private const string DocsURL = "https://fatihbykl.github.io/docs/fuzzyide/";
        private const string DiscordURL = "https://discord.gg/CGpunqn49e";
        private const string SupportEmail = "devbayk@gmail.com";

        private static readonly Vector2 WindowSize = new Vector2(640, 760);

        // Palette, picked per editor skin.
        private Color m_Background, m_Surface, m_SurfaceHover, m_Border, m_Text, m_Muted, m_Accent;
        private static readonly Color Gold = new Color(0.98f, 0.76f, 0.22f);
        private static readonly Color Blurple = new Color(0.345f, 0.396f, 0.949f); // Discord #5865F2

        static FuzzyIdeWelcomeWindow()
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

        [MenuItem("Window/Fuzzy Logic Labs/Fuzzy IDE/Welcome Window")]
        public static void ShowWindow()
        {
            var window = GetWindow<FuzzyIdeWelcomeWindow>(true, "Welcome to Fuzzy IDE", true);
            window.minSize = WindowSize;
            window.maxSize = WindowSize;
            window.ShowUtility();
        }

        public void CreateGUI()
        {
            bool dark = EditorGUIUtility.isProSkin;
            m_Background = dark ? Hex(0x1B1C1F) : Hex(0xECEDEF);
            m_Surface = dark ? Hex(0x25272B) : Hex(0xFFFFFF);
            m_SurfaceHover = dark ? Hex(0x2E3136) : Hex(0xF3F5F8);
            m_Border = dark ? Hex(0x34373D) : Hex(0xD5D8DD);
            m_Text = dark ? Hex(0xE8E9EB) : Hex(0x1F2124);
            m_Muted = dark ? Hex(0x9A9DA3) : Hex(0x5F636A);
            m_Accent = dark ? Hex(0x4FA3FF) : Hex(0x1F6FD1);

            var root = rootVisualElement;
            root.Clear();
            root.style.backgroundColor = m_Background;

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            root.Add(scroll);

            scroll.Add(BuildHero());

            var body = new VisualElement();
            body.style.paddingLeft = 20; body.style.paddingRight = 20; body.style.paddingTop = 16; body.style.paddingBottom = 12;
            scroll.Add(body);

            body.Add(BuildSupportSection());
            body.Add(SectionTitle("Open a tool"));
            body.Add(BuildToolGrid());
            body.Add(SectionTitle("Learn & get in touch"));
            body.Add(BuildContactRow());

            root.Add(BuildFooter());
        }

        // ---- Sections -----------------------------------------------------------------------------------------

        private VisualElement BuildHero()
        {
            var hero = new VisualElement();
            hero.style.height = 190;
            hero.style.overflow = Overflow.Hidden;
            hero.style.backgroundColor = Hex(0x111214);

            var banner = Resources.Load<Texture2D>("FuzzyLogicLabsBanner");
            if (banner != null)
            {
                var image = new Image { image = banner, scaleMode = ScaleMode.ScaleAndCrop };
                image.style.position = Position.Absolute;
                image.style.left = 0; image.style.right = 0; image.style.top = 0; image.style.bottom = 0;
                hero.Add(image);
            }

            // Darkening strip at the bottom keeps the title readable over any banner.
            var shade = new VisualElement();
            shade.style.position = Position.Absolute;
            shade.style.left = 0; shade.style.right = 0; shade.style.bottom = 0; shade.style.height = 92;
            shade.style.backgroundColor = new Color(0f, 0f, 0f, 0.62f);
            hero.Add(shade);

            var titleBox = new VisualElement();
            titleBox.style.position = Position.Absolute;
            titleBox.style.left = 22; titleBox.style.right = 22; titleBox.style.bottom = 16;
            hero.Add(titleBox);

            var titleRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            var title = new Label("Welcome to Fuzzy IDE");
            title.style.fontSize = 24;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = Color.white;
            titleRow.Add(title);
            titleRow.Add(Pill("v" + Version, new Color(1f, 1f, 1f, 0.16f), Color.white));
            titleBox.Add(titleRow);

            var tagline = new Label("Code, terminal, git and notes... without ever leaving Unity.");
            tagline.style.fontSize = 13;
            tagline.style.color = new Color(1f, 1f, 1f, 0.82f);
            tagline.style.marginTop = 4;
            titleBox.Add(tagline);
            return hero;
        }

        private VisualElement BuildSupportSection()
        {
            var section = new VisualElement();
            section.style.marginBottom = 6;

            // Review: the most prominent card in the window.
            var review = Card(Gold, highlighted: true);
            var reviewTop = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            var stars = new Label("\u2605\u2605\u2605\u2605\u2605");
            stars.style.fontSize = 20;
            stars.style.color = Gold;
            stars.style.marginRight = 10;
            reviewTop.Add(stars);
            var reviewTitle = new Label("Enjoying Fuzzy IDE? A review means the world.");
            reviewTitle.style.fontSize = 15;
            reviewTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            reviewTitle.style.color = m_Text;
            reviewTitle.style.flexShrink = 1;
            reviewTitle.style.whiteSpace = WhiteSpace.Normal;
            reviewTop.Add(reviewTitle);
            review.Add(reviewTop);

            review.Add(Body("Fuzzy IDE is built and maintained by a single developer. A short rating on the Asset Store " +
                            "is the biggest help there is: it helps other Unity developers find the tool and keeps " +
                            "updates coming. It takes less than a minute."));

            bool alreadyReviewed = EditorPrefs.GetBool(ReviewClickedKey, false);
            var reviewButton = ActionButton(alreadyReviewed ? "\u2605  Thank you! Open the Asset Store page" : "\u2605  Leave a Review on the Asset Store",
                Gold, Hex(0x2A1F00), () =>
                {
                    EditorPrefs.SetBool(ReviewClickedKey, true);
                    Application.OpenURL(ReviewURL);
                });
            reviewButton.style.marginTop = 12;
            reviewButton.style.height = 38;
            reviewButton.style.fontSize = 14;
            review.Add(reviewButton);
            section.Add(review);

            // Discord: second call to action.
            var discord = Card(Blurple, highlighted: true);
            discord.style.marginTop = 12;
            var discordRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            var discordText = new VisualElement { style = { flexGrow = 1, flexShrink = 1, marginRight = 14 } };
            var discordTitle = new Label("Join the community on Discord");
            discordTitle.style.fontSize = 15;
            discordTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            discordTitle.style.color = m_Text;
            discordText.Add(discordTitle);
            discordText.Add(Body("The fastest way to get help. Report bugs, request features, share feedback and see what's coming next."));
            discordRow.Add(discordText);

            var discordButton = ActionButton("Join Discord", Blurple, Color.white, () => Application.OpenURL(DiscordURL));
            discordButton.style.width = 150;
            discordButton.style.height = 38;
            discordButton.style.fontSize = 14;
            discordButton.style.flexShrink = 0;
            discordRow.Add(discordButton);
            discord.Add(discordRow);
            section.Add(discord);
            return section;
        }

        private VisualElement BuildToolGrid()
        {
            var grid = new VisualElement();
            grid.style.flexDirection = FlexDirection.Row;
            grid.style.flexWrap = Wrap.Wrap;
            grid.style.justifyContent = Justify.SpaceBetween;

            grid.Add(ToolCard("</>", Hex(0x4FA3FF), "IDE Window", "Roslyn-powered C# editor", VirtualIDEWindow.ShowWindow));
            grid.Add(ToolCard(">_", Hex(0x3DDC97), "Terminal", "Native shell inside the editor", NativeTerminalWindow.ShowWindow));
            grid.Add(ToolCard("git", Hex(0xF0703C), "Git", "Commit, branch, diff, resolve", GitWindow.ShowWindow));
            grid.Add(ToolCard("#", Hex(0xB48CFF), "Fuzzy Notes", "Notes linked to code & assets", FuzzyNotesWindow.ShowWindow));
            grid.Add(ToolCard("+", Hex(0xF4C152), "Quick Scratchpad", "Capture an idea (Ctrl+Alt+N)", QuickCaptureWindow.OpenQuickCapture));
            grid.Add(ToolCard("\u2699", Hex(0x9AA3AE), "Settings", "Themes, keys, smart code", () => SettingsService.OpenUserPreferences("Preferences/Fuzzy Logic Labs/Code IDE")));
            return grid;
        }

        private VisualElement BuildContactRow()
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row } };

            var docs = Card(m_Accent, highlighted: false);
            docs.style.flexGrow = 1;
            docs.style.flexBasis = 0;
            docs.style.marginRight = 6;
            docs.Add(CardTitle("Online documentation"));
            docs.Add(Body("Guides for every feature, shortcuts and troubleshooting."));
            var docsButton = ActionButton("Open Documentation", m_Accent, Color.white, () => Application.OpenURL(DocsURL));
            docsButton.style.marginTop = 10;
            docs.Add(docsButton);
            row.Add(docs);

            var mail = Card(m_Muted, highlighted: false);
            mail.style.flexGrow = 1;
            mail.style.flexBasis = 0;
            mail.style.marginLeft = 6;
            mail.Add(CardTitle("Email support"));
            var address = new Label(SupportEmail);
            address.style.color = m_Accent;
            address.style.fontSize = 13;
            address.style.marginTop = 4;
            mail.Add(address);
            var mailButtons = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 10 } };
            var write = ActionButton("Write an Email", m_SurfaceHover, m_Text, () =>
                Application.OpenURL($"mailto:{SupportEmail}?subject=Fuzzy%20IDE%20Support%20Request"));
            write.style.flexGrow = 1;
            write.style.marginRight = 6;
            mailButtons.Add(write);
            Button copy = null;
            copy = ActionButton("Copy", m_SurfaceHover, m_Text, () =>
            {
                EditorGUIUtility.systemCopyBuffer = SupportEmail;
                copy.text = "Copied";
                copy.schedule.Execute(() => copy.text = "Copy").StartingIn(1500);
            });
            copy.style.width = 70;
            mailButtons.Add(copy);
            mail.Add(mailButtons);
            row.Add(mail);
            return row;
        }

        private VisualElement BuildFooter()
        {
            var footer = new VisualElement();
            footer.style.flexDirection = FlexDirection.Row;
            footer.style.alignItems = Align.Center;
            footer.style.height = 38;
            footer.style.flexShrink = 0;
            footer.style.paddingLeft = 20; footer.style.paddingRight = 20;
            footer.style.backgroundColor = m_Surface;
            footer.style.borderTopWidth = 1;
            footer.style.borderTopColor = m_Border;

            var toggle = new Toggle("Show this window on startup") { value = !EditorPrefs.HasKey(PrefsKey) };
            toggle.labelElement.style.minWidth = 0;
            toggle.labelElement.style.color = m_Muted;
            toggle.style.flexDirection = FlexDirection.RowReverse;
            toggle.RegisterValueChangedCallback(e =>
            {
                if (e.newValue) EditorPrefs.DeleteKey(PrefsKey);
                else EditorPrefs.SetInt(PrefsKey, 1);
            });
            footer.Add(toggle);

            footer.Add(new VisualElement { style = { flexGrow = 1 } });
            var brand = new Label("Made with care by Fuzzy Logic Labs");
            brand.style.color = m_Muted;
            brand.style.fontSize = 11;
            footer.Add(brand);
            return footer;
        }

        // ---- Building blocks ----------------------------------------------------------------------------------

        private VisualElement Card(Color accent, bool highlighted)
        {
            var card = new VisualElement();
            card.style.backgroundColor = m_Surface;
            card.style.paddingLeft = 16; card.style.paddingRight = 16; card.style.paddingTop = 14; card.style.paddingBottom = 14;
            SetRadius(card, 8);
            SetBorder(card, highlighted ? new Color(accent.r, accent.g, accent.b, 0.55f) : m_Border, 1);
            if (highlighted)
            {
                card.style.borderLeftWidth = 4;
                card.style.borderLeftColor = accent;
            }
            return card;
        }

        private VisualElement ToolCard(string glyph, Color color, string title, string description, Action open)
        {
            var card = new VisualElement();
            card.style.width = new Length(32.2f, LengthUnit.Percent);
            card.style.marginBottom = 10;
            card.style.flexDirection = FlexDirection.Row;
            card.style.alignItems = Align.Center;
            card.style.backgroundColor = m_Surface;
            card.style.paddingLeft = 10; card.style.paddingRight = 8; card.style.paddingTop = 10; card.style.paddingBottom = 10;
            SetRadius(card, 8);
            SetBorder(card, m_Border, 1);

            var badge = new Label(glyph);
            badge.style.width = 34; badge.style.height = 34;
            badge.style.flexShrink = 0;
            badge.style.unityTextAlign = TextAnchor.MiddleCenter;
            badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            badge.style.fontSize = glyph.Length > 2 ? 11 : 14;
            badge.style.color = color;
            badge.style.backgroundColor = new Color(color.r, color.g, color.b, 0.16f);
            badge.style.marginRight = 9;
            SetRadius(badge, 7);
            card.Add(badge);

            var text = new VisualElement { style = { flexShrink = 1 } };
            var name = new Label(title);
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.color = m_Text;
            name.style.fontSize = 12;
            text.Add(name);
            var detail = new Label(description);
            detail.style.color = m_Muted;
            detail.style.fontSize = 10;
            detail.style.whiteSpace = WhiteSpace.Normal;
            text.Add(detail);
            card.Add(text);

            card.tooltip = "Open " + title;
            card.RegisterCallback<MouseEnterEvent>(e => { card.style.backgroundColor = m_SurfaceHover; SetBorder(card, color, 1); });
            card.RegisterCallback<MouseLeaveEvent>(e => { card.style.backgroundColor = m_Surface; SetBorder(card, m_Border, 1); });
            card.RegisterCallback<ClickEvent>(e => open());
            return card;
        }

        private Button ActionButton(string text, Color background, Color foreground, Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.style.height = 30;
            button.style.marginLeft = 0; button.style.marginRight = 0;
            button.style.backgroundColor = background;
            button.style.color = foreground;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            SetRadius(button, 6);
            SetBorder(button, Color.clear, 0);
            Color hover = Color.Lerp(background, Color.white, 0.12f);
            button.RegisterCallback<MouseEnterEvent>(e => button.style.backgroundColor = hover);
            button.RegisterCallback<MouseLeaveEvent>(e => button.style.backgroundColor = background);
            return button;
        }

        private Label SectionTitle(string text)
        {
            var label = new Label(text.ToUpperInvariant());
            label.style.color = m_Muted;
            label.style.fontSize = 11;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.letterSpacing = 1;
            label.style.marginTop = 16;
            label.style.marginBottom = 8;
            return label;
        }

        private Label CardTitle(string text)
        {
            var label = new Label(text);
            label.style.fontSize = 13;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.color = m_Text;
            return label;
        }

        private Label Body(string text)
        {
            var label = new Label(text);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.color = m_Muted;
            label.style.fontSize = 12;
            label.style.marginTop = 6;
            return label;
        }

        private static Label Pill(string text, Color background, Color foreground)
        {
            var pill = new Label(text);
            pill.style.fontSize = 11;
            pill.style.color = foreground;
            pill.style.backgroundColor = background;
            pill.style.paddingLeft = 7; pill.style.paddingRight = 7; pill.style.paddingTop = 2; pill.style.paddingBottom = 2;
            pill.style.marginLeft = 10;
            SetRadius(pill, 9);
            return pill;
        }

        private static void SetRadius(VisualElement element, float radius)
        {
            element.style.borderTopLeftRadius = radius;
            element.style.borderTopRightRadius = radius;
            element.style.borderBottomLeftRadius = radius;
            element.style.borderBottomRightRadius = radius;
        }

        private static void SetBorder(VisualElement element, Color color, float width)
        {
            element.style.borderTopWidth = width; element.style.borderBottomWidth = width;
            element.style.borderLeftWidth = width; element.style.borderRightWidth = width;
            element.style.borderTopColor = color; element.style.borderBottomColor = color;
            element.style.borderLeftColor = color; element.style.borderRightColor = color;
        }

        private static Color Hex(int rgb) => new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
    }
}
