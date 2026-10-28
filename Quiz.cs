using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using SD = System.Drawing;
using WF = System.Windows.Forms;

// The quiz, visual-novel style: Heather fills most of the screen and talks you through
// everything in a dialogue box, while your choices appear on a panel beside her.
partial class QuizWindow : Window
{
    // ---------- Look (Silent Hill: rust, dried blood, bone, fog) ----------
    static Color C(string h) { return (Color)ColorConverter.ConvertFromString(h); }
    static Brush Hex(string h) { var b = new SolidColorBrush(C(h)); b.Freeze(); return b; }
    static Brush Grad(string a, string b)
    {
        var g = new LinearGradientBrush(C(a), C(b), new Point(0, 0), new Point(1, 1));
        g.Freeze();
        return g;
    }

    static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable Display, Segoe UI");
    static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly FontFamily TitleFont = new FontFamily("Georgia");
    static readonly Brush TextMain = Hex("#EAE3D6");
    static readonly Brush Muted = Hex("#9A8E84");
    static readonly Brush CardBg = Hex("#16FFFFFF");
    static readonly Brush CardHover = Hex("#28FFFFFF");
    static readonly Brush CardStroke = Hex("#22FFFFFF");
    static readonly Brush FieldBg = Hex("#10FFFFFF");
    static readonly Brush Good = Hex("#7FB58A");
    static readonly Brush GoodTint = Hex("#2E7FB58A");
    static readonly Brush Bad = Hex("#C8433A");
    static readonly Brush BadTint = Hex("#2EC8433A");
    static readonly Brush Gold = Hex("#C9A13B");
    static readonly Brush Fire = Hex("#B8612E");
    static readonly Brush Blood = Hex("#B3201B");
    static readonly Brush TitleRed = Hex("#C23B30");
    static readonly Brush Accent = Grad("#9E1B16", "#4A0B0B");

    const double TimerSecs = 15, NotesW = 380, DialogH = 200;

    // How far the jaw would open for each SAPI viseme; drives her head bobs while talking
    static readonly double[] VisemeOpen =
        { 0, 1, 1, 0.8, 0.6, 0.5, 0.35, 0.4, 0.7, 0.9, 0.7, 0.9, 0.4, 0.4, 0.4, 0.2, 0.3, 0.2, 0.15, 0.3, 0.4, 0 };

    static readonly string DataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "quizzahh");
    static readonly string ClassesDir = System.IO.Path.Combine(DataDir, "classes");
    static readonly string NotesDir = System.IO.Path.Combine(DataDir, "notes");
    static readonly string VoiceDir = System.IO.Path.Combine(DataDir, "voice");
    static readonly string[] ClipFolders = { "greeting", "correct", "streak", "wrong", "levelup", "results_good", "results_bad" };
    static readonly string[] ClipTypes = { ".wav", ".mp3", ".wma", ".m4a" };

    static readonly string[] Titles =
    {
        "Script Kiddie", "Copy-Paste Engineer", "Code Monkey", "Bug Squasher", "Stack Overflow Survivor",
        "Merge Conflict Menace", "Algorithm Enjoyer", "10x Engineer", "Kernel Wizard", "Turing's Heir"
    };

    // Heather's lines
    static readonly string[] Greetings =
    {
        "Hey. Let's get this over with.", "I'd rather be at Happy Burger, but fine.",
        "Beats running from monsters in a mall, I guess.", "It's foggy out anyway. Might as well study.",
        "Oh, you're back. Good."
    };
    static readonly string[] Right =
    {
        "Huh. Not bad.", "Okay, you actually know this one.", "See? You're not hopeless.",
        "Correct. I'm almost impressed.", "Yep, that's it.", "Look at you, paying attention."
    };
    static readonly string[] Hot =
    {
        "Okay, now you're just showing off.", "Who are you and what did you do with my student?",
        "Another one? Don't get cocky.", "You're on a roll. Seriously.", "Fine, you're good. Happy now?"
    };
    static readonly string[] Wrong =
    {
        "Nope.", "Seriously? No.", "Not even close. Okay, kind of close. Still no.",
        "Ugh. Okay, listen.", "Wrong. But don't freak out about it.", "Nice try. Not really, though."
    };
    static readonly string[] LostStreak =
    {
        "And there goes your streak. Great.", "Streak's dead. Like everything else in this town.",
        "Well, that streak didn't last."
    };

    // ---------- State ----------
    readonly bool snapshot;
    readonly string snapDir;
    readonly Stats stats = Stats.Load();
    readonly Mastery mastery = Mastery.Load();
    int sessionLearned;
    readonly Random rng = new Random();
    readonly Stopwatch sw = new Stopwatch();
    Dictionary<string, List<Q>> classes = new Dictionary<string, List<Q>>();
    string currentClass, quizClass;
    HashSet<string> selected = new HashSet<string>();
    int count = 10; // 0 = all

    List<Q> quiz;
    List<Missed> missed;
    List<bool> history;
    int idx, score, streak, runBest, sessionXp, levelAtStart;
    bool answered, quitting;
    string mode = "";

    // ---------- UI refs ----------
    Grid outer;
    Border panelCard, notesPanel, nextRow, verdictBanner;
    Grid dialogWrap;
    ScrollViewer scroller, dialogScroll;
    TextBlock dialogThinking;
    List<string> homeOrder = new List<string>();
    Canvas fx;
    Ellipse aura;
    TextBlock dialogText, classLabel, levelText, xpText, streakText, muteGlyph, voiceGlyph, notesGlyph, verdictText, homeTitle;
    TextBlock notesClassLabel, notesSaved, notesPlaceholder;
    TextBox notesBox;
    Grid xpTrack;
    Border xpFill;
    ScaleTransform timerScale;
    List<Border> answerCards;
    DispatcherTimer typer, notesTimer;
    WF.NotifyIcon tray;
    bool trayTipShown, loadingNotes;
    string notesFor;
    HeatherView heatherView;
    BuddyWindow buddy;

    // ---------- Voice ----------
    MediaPlayer clipPlayer;
    DispatcherTimer flap;
    int clipToken;
    SpeechSynthesizer voice;
    Prompt currentPrompt;
    TextBlock talkTarget;
    string talkDisplay;
    int talkSpokenLen, talkSkip;

    public QuizWindow(string snapDir)
    {
        this.snapDir = snapDir;
        snapshot = snapDir != null;
        if (snapshot) { Stats.ReadOnly = true; Sfx.Muted = true; }
        else { Sfx.Muted = stats.Muted; SetupVoice(); EnsureVoiceFolders(); }

        LoadClasses();

        Title = "quizzahh";
        var icon = LoadImage("avatar.png");
        if (icon != null) Icon = icon;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = UiFont;
        // See-through: the desktop shows behind Heather and the panels, and clicks on
        // empty areas go straight through to it
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        if (!snapshot) CleanSpeechCache();
        WindowStartupLocation = WindowStartupLocation.Manual;
        var wa = SystemParameters.WorkArea;
        Left = wa.Left;
        Top = wa.Top;
        Width = wa.Width;
        Height = wa.Height;
        if (snapshot) { Left = -8000; ShowActivated = false; ShowInTaskbar = false; }

        Content = BuildShell();
        PreviewKeyDown += OnKey;
        Closing += (s, e) =>
        {
            // Closing the quiz just puts it away; Heather stays on the taskbar.
            // Only "Quit" from the tray menu really exits.
            if (!quitting && !snapshot)
            {
                e.Cancel = true;
                HideWidget();
                return;
            }
            SaveNotes();
            SaveScratch();
            if (snapshot) return;
            if (voice != null) voice.SpeakAsyncCancelAll();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (buddy != null) buddy.Close();
        };
        if (!snapshot)
        {
            SetupTray();
            try
            {
                buddy = new BuddyWindow(LoadModel(), stats.BuddyX, x => { stats.BuddyX = x; stats.Save(); }, ToggleWidget);
                buddy.Disabled = stats.BuddyOff;
                buddy.Snoozed = howLong =>
                {
                    if (tray != null)
                        tray.ShowBalloonTip(4000, "Heather is hiding " + howLong,
                            "To bring her back sooner, right-click the quizzahh icon in the tray and pick \"Bring Heather back\".", WF.ToolTipIcon.None);
                };
            }
            catch (Exception ex)
            {
                buddy = null;
                try { File.WriteAllText(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "quizzahh", "error.txt"), ex.ToString()); } catch { }
            }
        }
        UpdateHud(false);
        LoadNotes(currentClass);
        ShowHome();
        if (snapshot) Loaded += (s, e) => RunSnapshot();
    }

    static BitmapImage LoadImage(string name)
    {
        try
        {
            var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (s == null) return null;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = s;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    public static HeatherModel LoadModel()
    {
        HeatherModel model;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("heather.dae"))
            model = new HeatherModel(s, name => LoadImage(name));
        // The game model carries 19 extra pre-posed hands (fists, grips...) that SH3 swaps in;
        // drawn together they pile up on her hands, so keep only her normal fingered hands.
        // Mesh 40 is an exact duplicate of 39.
        for (int i = 19; i <= 37; i++) model.Hidden.Add(i);
        model.Hidden.Add(40);
        return model;
    }

    // ---------- Classes ----------
    // First run from a fresh copy: fill an empty data folder from the data\ folder next to the exe.
    static void SeedFolder(string name, string dest)
    {
        try
        {
            string src = System.IO.Path.Combine(AppDir, "data", name);
            if (!Directory.Exists(src)) return;
            Directory.CreateDirectory(dest);
            if (Directory.GetFiles(dest, "*.txt").Any()) return;
            foreach (var f in Directory.GetFiles(src, "*.txt"))
                File.Copy(f, System.IO.Path.Combine(dest, System.IO.Path.GetFileName(f)));
        }
        catch { }
    }

    void LoadClasses()
    {
        var loaded = new Dictionary<string, List<Q>>();
        var years = new Dictionary<string, int>();
        try
        {
            Directory.CreateDirectory(ClassesDir);
            SeedFolder("classes", ClassesDir);
            SeedFolder("coding", System.IO.Path.Combine(DataDir, "coding"));
            if (!Directory.GetFiles(ClassesDir, "*.txt").Any())
                QuestionFile.WriteAll(System.IO.Path.Combine(ClassesDir, "Computer Science.txt"), Bank.Load());
            foreach (var f in Directory.GetFiles(ClassesDir, "*.txt").OrderBy(f => NaturalKey(System.IO.Path.GetFileNameWithoutExtension(f))))
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(f);
                try
                {
                    var qs = QuestionFile.Read(f);
                    if (qs.Count == 0) continue; // an empty or broken file shouldn't show up as a class
                    loaded[name] = qs;
                    years[name] = QuestionFile.ReadYear(f);
                }
                catch { }
            }
        }
        catch { }
        if (loaded.Count == 0) loaded["Computer Science"] = Bank.Load();
        classes = loaded;
        classYears = years;

        string want = currentClass ?? stats.LastClass;
        string prev = currentClass;
        currentClass = classes.ContainsKey(want ?? "") ? want : classes.Keys.First();
        if (prev != currentClass || selected.Count == 0) selected = new HashSet<string>(Topics());
        else selected.IntersectWith(Topics());
    }

    List<Q> Bank_() { return classes[currentClass]; }

    // Year of the degree each class belongs to (from its file; 0 = not set)
    Dictionary<string, int> classYears = new Dictionary<string, int>();
    int YearOf(string cls) { int y; return classYears.TryGetValue(cls, out y) ? y : 0; }

    static readonly string[] YearBlurbs =
    {
        "Every class, grouped by subject.",
        "Start here: programming basics, Calculus I, and your first gen eds.",
        "Core CS: data structures, how computers work inside, Calculus II, discrete math and physics.",
        "Upper-level CS: algorithms, operating systems, networks, databases, software engineering and linear algebra.",
        "Advanced CS: theory, security, AI, ethics, plus a review of the whole major."
    };

    // "CS 10" sorts after "CS 9"
    static string NaturalKey(string name)
    {
        return System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), @"\d+", m => m.Value.PadLeft(6, '0'));
    }

    // Home screen sections, by the first word of the class name
    static string GroupFor(string cls)
    {
        string first = cls.Split(' ')[0].ToUpperInvariant();
        switch (first)
        {
            case "CS": return "COMPUTER SCIENCE";
            case "MATH": case "STATS": return "MATH";
            case "BIO": case "CHEM": case "PHYSICS": return "SCIENCE";
            case "ENGLISH": case "GOV": case "HISTORY": case "PSYCH": case "SPEECH": case "ECON":
            case "PHILOSOPHY": case "SOCIOLOGY": case "ARTS": return "GEN EDS";
            default: return "MY CLASSES";
        }
    }

    static int GroupOrder(string group)
    {
        switch (group)
        {
            case "COMPUTER SCIENCE": return 0;
            case "MATH": return 1;
            case "SCIENCE": return 2;
            case "GEN EDS": return 3;
            default: return 4;
        }
    }

    IEnumerable<string> Topics() { return Bank_().Select(q => q.Topic).Distinct(); }

    static string SafeName(string name)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
        return name.Trim();
    }

    void PickClass(string cls)
    {
        if (cls != currentClass)
        {
            currentClass = cls;
            stats.LastClass = cls;
            stats.Save();
            selected = new HashSet<string>(Topics());
            LoadNotes(cls);
        }
        ShowTopics();
    }

    void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(ClassesDir);
            Process.Start("explorer.exe", "\"" + ClassesDir + "\"");
        }
        catch { }
    }

    // ---------- Layout ----------
    UIElement BuildShell()
    {
        double W = Width, H = Height;
        // No backdrop: just Heather and the panels floating over the desktop
        outer = new Grid { Background = Brushes.Transparent, ClipToBounds = true };

        // Heather in the middle, big, leaning on the bottom edge of the screen
        double colW = Math.Round(W * 0.25);
        double stageW = W - 2 * colW;
        aura = new Ellipse
        {
            Width = stageW * 0.8, Height = stageW * 0.8, Opacity = 0, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, -stageW * 0.15),
            Fill = new RadialGradientBrush(C("#887FB58A"), C("#007FB58A"))
        };
        outer.Children.Add(aura);
        try
        {
            // Head near the top third, forearms resting right on the bottom edge
            double wpd = 260.0 / (H * 0.66);
            heatherView = new HeatherView(LoadModel(), false, stageW, H, new Point3D(0, 5, 570), wpd,
                new Vector3D(0, 1, -0.06), H * 0.9)
            {
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                Leaning = true
            };
            outer.Children.Add(heatherView);
        }
        catch { heatherView = null; }

        // Choices panel on the right
        panelCard = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right, Width = colW - 40, Margin = new Thickness(0, 24, 30, 24),
            CornerRadius = new CornerRadius(22), Background = Hex("#EE140E0C"), BorderBrush = Hex("#33C23B30"),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 4, Opacity = 0.55, Color = Colors.Black }
        };
        var pg = new Grid();
        pg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panelCard.Child = pg;
        var panelGrain = Grain(21);
        Grid.SetRowSpan(panelGrain, 3);
        pg.Children.Add(panelGrain);
        pg.Children.Add(BuildHeader());
        var hud = BuildHud();
        Grid.SetRow(hud, 1);
        pg.Children.Add(hud);
        scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Padding = new Thickness(26, 6, 26, 24),
            RenderTransform = new TranslateTransform()
        };
        Grid.SetRow(scroller, 2);
        pg.Children.Add(scroller);
        outer.Children.Add(panelCard);

        // Left column: her speech bubble (pointing at her), and notes underneath when open
        var leftCol = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Width = colW - 40, Margin = new Thickness(30, 24, 0, 24) };
        leftCol.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftCol.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outer.Children.Add(leftCol);

        dialogWrap = new Grid { Margin = new Thickness(0, stats.NotesOpen && !snapshot ? 18 : H * 0.2, 0, 0) };
        leftCol.Children.Add(dialogWrap);
        var bubbleFill = new LinearGradientBrush(C("#F0130D0C"), C("#F0201211"), new Point(0, 0), new Point(0, 1));
        var dialog = new Border
        {
            MinHeight = 170, MaxHeight = H * 0.5,
            CornerRadius = new CornerRadius(20), BorderBrush = Hex("#77C23B30"), BorderThickness = new Thickness(1.5),
            Background = bubbleFill,
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 4, Opacity = 0.6, Color = Colors.Black }
        };
        dialogText = new TextBlock { FontSize = 20, LineHeight = 30, Foreground = TextMain, TextWrapping = TextWrapping.Wrap };
        // Dots that pulse while her voice is being prepared
        dialogThinking = new TextBlock { Text = "●  ●  ●", FontSize = 12, Foreground = Muted, Margin = new Thickness(2, 8, 0, 0), Visibility = Visibility.Collapsed };
        var dialogStack = new StackPanel();
        dialogStack.Children.Add(dialogText);
        dialogStack.Children.Add(dialogThinking);
        dialogScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = dialogStack };
        // Long explanations keep the newest words in view
        dialogStack.SizeChanged += (s, e) => dialogScroll.ScrollToEnd();
        var dialogInner = new Grid();
        dialogInner.Children.Add(Grain(19));
        dialogInner.Children.Add(new Border { Padding = new Thickness(28, 32, 28, 22), Child = dialogScroll });
        dialog.Child = dialogInner;
        dialogWrap.Children.Add(dialog);
        // Tail pointing right, toward Heather
        dialogWrap.Children.Add(new Polygon
        {
            Points = new PointCollection { new Point(0, 0), new Point(22, 14), new Point(0, 28) },
            Fill = Hex("#F01A100F"), Stroke = Hex("#77C23B30"), StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 60, -20, 0)
        });
        dialogWrap.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(24, -16, 0, 0), Background = Accent,
            BorderBrush = Hex("#88C23B30"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 5, 18, 6),
            Child = new TextBlock { Text = "HEATHER", FontFamily = TitleFont, FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Hex("#F2E8DA") }
        });

        notesPanel = BuildNotes();
        notesPanel.Width = double.NaN;
        notesPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        notesPanel.VerticalAlignment = VerticalAlignment.Stretch;
        notesPanel.Margin = new Thickness(0, 22, 0, 0);
        Grid.SetRow(notesPanel, 1);
        leftCol.Children.Add(notesPanel);

        if (!snapshot)
        {
            // Now and then the title flickers like a dying light
            var flicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            flicker.Tick += (s, e) =>
            {
                flicker.Interval = TimeSpan.FromSeconds(4 + rng.NextDouble() * 6);
                if (homeTitle == null || !IsVisible || mode != "home") return;
                var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
                double[] vals = { 1, 0.25, 0.9, 0.15, 1, 0.6, 1 };
                for (int k = 0; k < vals.Length; k++)
                    a.KeyFrames.Add(new DiscreteDoubleKeyFrame(vals[k], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(k * 60))));
                homeTitle.BeginAnimation(OpacityProperty, a);
            };
            flicker.Start();
        }

        // Scratch paper: a tab at the top of the screen pulls down a drawing sheet over the middle
        BuildScratch(outer, H, stageW);
        // Code Lab: real programming problems, over the middle and right of the screen
        BuildCodeLab(outer, W, H, colW);

        fx = new Canvas { IsHitTestVisible = false };
        Panel.SetZIndex(fx, 60);
        outer.Children.Add(fx);
        return outer;
    }

    // Static Silent Hill film grain, clipped to a rounded panel
    static ImageBrush grainBrush;

    static Rectangle Grain(double radius)
    {
        if (grainBrush == null)
        {
            grainBrush = new ImageBrush(MakeNoise())
            {
                TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 128, 128)
            };
            grainBrush.Freeze();
        }
        return new Rectangle { Fill = grainBrush, Opacity = 0.06, IsHitTestVisible = false, RadiusX = radius, RadiusY = radius };
    }

    static BitmapSource MakeNoise()
    {
        var r = new Random(7);
        const int n = 128;
        var px = new byte[n * n * 4];
        for (int i = 0; i < n * n; i++)
        {
            byte v = (byte)r.Next(256);
            px[i * 4] = v; px[i * 4 + 1] = v; px[i * 4 + 2] = v;
            px[i * 4 + 3] = (byte)r.Next(40, 255);
        }
        var bmp = BitmapSource.Create(n, n, 96, 96, PixelFormats.Bgra32, null, px, n * 4);
        bmp.Freeze();
        return bmp;
    }

    UIElement BuildHeader()
    {
        var dp = new DockPanel { Margin = new Thickness(30, 20, 20, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(buttons, Dock.Right);
        var notes = IconButton("", "Notes", ToggleNotes);
        notesGlyph = (TextBlock)notes.Child;
        notesGlyph.Foreground = stats.NotesOpen ? TitleRed : Muted;
        var talk = IconButton("", "Heather's voice on/off", ToggleVoice);
        voiceGlyph = (TextBlock)talk.Child;
        voiceGlyph.Opacity = stats.Quiet ? 0.35 : 1;
        var mute = IconButton(stats.Muted ? "" : "", "Sound effects on/off", ToggleMute);
        muteGlyph = (TextBlock)mute.Child;
        buttons.Children.Add(IconButton("", "Code Lab: write real code, Beginner to Expert", ToggleCodeLab));
        buttons.Children.Add(IconButton("", "Scratch paper: draw it out by hand", ToggleScratch));
        buttons.Children.Add(notes);
        buttons.Children.Add(talk);
        buttons.Children.Add(mute);
        buttons.Children.Add(IconButton("", "Home", ShowHome));
        buttons.Children.Add(IconButton("", "Close (Heather stays on your taskbar)", HideWidget));
        dp.Children.Add(buttons);

        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = "QUIZZAHH", FontSize = 17, FontWeight = FontWeights.Bold, FontFamily = TitleFont, Foreground = TitleRed });
        classLabel = new TextBlock { FontSize = 14, Foreground = Muted, Margin = new Thickness(10, 2, 0, 0), MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Children.Add(classLabel);
        dp.Children.Add(title);
        return dp;
    }

    UIElement BuildHud()
    {
        var sp = new StackPanel { Margin = new Thickness(30, 8, 30, 12) };
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        levelText = T("", 14, FontWeights.Bold, TextMain);
        streakText = new TextBlock
        {
            FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Fire, ToolTip = "Streak",
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1)
        };
        Grid.SetColumn(streakText, 1);
        top.Children.Add(levelText);
        top.Children.Add(streakText);
        sp.Children.Add(top);
        xpTrack = new Grid { Height = 8, Margin = new Thickness(0, 7, 0, 3) };
        xpTrack.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = Hex("#1EFFFFFF") });
        xpFill = new Border { CornerRadius = new CornerRadius(4), Background = Accent, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        xpTrack.Children.Add(xpFill);
        xpTrack.SizeChanged += (s, e) => UpdateHud(false);
        sp.Children.Add(xpTrack);
        xpText = T("", 12, FontWeights.Normal, Muted);
        xpText.HorizontalAlignment = HorizontalAlignment.Right;
        sp.Children.Add(xpText);
        return sp;
    }

    // ---------- Notes ----------
    Border BuildNotes()
    {
        var panel = new Border
        {
            Width = NotesW, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 40, DialogH + 50),
            CornerRadius = new CornerRadius(22), BorderBrush = Hex("#33C23B30"), BorderThickness = new Thickness(1),
            Background = Hex("#F0140E0C"),
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 4, Opacity = 0.55, Color = Colors.Black },
            Padding = new Thickness(20, 18, 20, 18),
            Visibility = stats.NotesOpen && !snapshot ? Visibility.Visible : Visibility.Collapsed
        };
        Panel.SetZIndex(panel, 5);
        var dp = new DockPanel();
        panel.Child = dp;

        var header = new Grid { Margin = new Thickness(2, 0, 2, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var hs = new StackPanel();
        var lbl = T("NOTES", 12, FontWeights.Bold, Muted);
        lbl.FontFamily = TitleFont;
        hs.Children.Add(lbl);
        notesClassLabel = T("", 19, FontWeights.Bold, TextMain);
        hs.Children.Add(notesClassLabel);
        header.Children.Add(hs);
        notesSaved = T("", 12, FontWeights.Normal, Muted);
        notesSaved.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(notesSaved, 1);
        header.Children.Add(notesSaved);
        DockPanel.SetDock(header, Dock.Top);
        dp.Children.Add(header);

        var foot = T("Saves automatically. Each class has its own notes.", 11.5, FontWeights.Normal, Muted);
        foot.Margin = new Thickness(2, 10, 0, 0);
        DockPanel.SetDock(foot, Dock.Bottom);
        dp.Children.Add(foot);

        var box = new Grid();
        var frame = new Border
        {
            CornerRadius = new CornerRadius(14), Background = FieldBg, BorderBrush = CardStroke,
            BorderThickness = new Thickness(1), Child = box
        };
        notesBox = new TextBox
        {
            AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = TextMain,
            CaretBrush = Brushes.White, SelectionBrush = Blood, FontSize = 15, FontFamily = UiFont,
            Padding = new Thickness(10, 12, 10, 12)
        };
        notesPlaceholder = T("Write stuff here so it sticks.\n\nTip: after a question, hit \"add to notes\" and Heather will write it down for you.",
            14, FontWeights.Normal, Muted);
        notesPlaceholder.Margin = new Thickness(15, 13, 15, 0);
        notesPlaceholder.IsHitTestVisible = false;
        box.Children.Add(notesBox);
        box.Children.Add(notesPlaceholder);
        dp.Children.Add(frame);

        notesBox.GotFocus += (s, e) => frame.BorderBrush = Hex("#66C23B30");
        notesBox.LostFocus += (s, e) => { frame.BorderBrush = CardStroke; SaveNotes(); };
        notesTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        notesTimer.Tick += (s, e) => { notesTimer.Stop(); SaveNotes(); notesSaved.Text = "saved ✓"; };
        notesBox.TextChanged += (s, e) =>
        {
            notesPlaceholder.Visibility = notesBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (loadingNotes) return;
            notesSaved.Text = "saving...";
            notesTimer.Stop();
            notesTimer.Start();
        };
        return panel;
    }

    static string NotesPath(string cls) { return System.IO.Path.Combine(NotesDir, SafeName(cls) + ".txt"); }

    void LoadNotes(string cls)
    {
        SaveNotes();
        loadingNotes = true;
        try { notesBox.Text = File.Exists(NotesPath(cls)) ? File.ReadAllText(NotesPath(cls), Encoding.UTF8) : ""; }
        catch { notesBox.Text = ""; }
        loadingNotes = false;
        notesFor = cls;
        notesClassLabel.Text = cls;
        notesSaved.Text = "";
    }

    void SaveNotes()
    {
        if (notesFor == null || Stats.ReadOnly) return;
        if (notesTimer != null) notesTimer.Stop();
        try
        {
            Directory.CreateDirectory(NotesDir);
            File.WriteAllText(NotesPath(notesFor), notesBox.Text, Encoding.UTF8);
        }
        catch { }
    }

    void ToggleNotes()
    {
        SetNotesOpen(!stats.NotesOpen);
        if (stats.NotesOpen)
        {
            Say("Good. Writing stuff down actually helps.", null, false);
            notesBox.Focus();
            notesBox.CaretIndex = notesBox.Text.Length;
        }
    }

    void SetNotesOpen(bool open)
    {
        stats.NotesOpen = open;
        stats.Save();
        notesPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        notesGlyph.Foreground = open ? TitleRed : Muted;
        dialogWrap.Margin = new Thickness(0, open ? 18 : Height * 0.2, 0, 0);
        if (!open) SaveNotes();
    }

    void AddToNotes(Q q, Btn button)
    {
        if (notesFor != quizClass) LoadNotes(quizClass);
        string entry = "• " + q.Text + "\n   → " + q.Options[q.Answer] + (string.IsNullOrWhiteSpace(q.Why) ? "" : "\n   " + q.Why) + "\n\n";
        string text = notesBox.Text;
        if (text.Length > 0 && !text.EndsWith("\n\n")) entry = (text.EndsWith("\n") ? "\n" : "\n\n") + entry;
        notesBox.AppendText(entry);
        notesBox.ScrollToEnd();
        SaveNotes();
        notesSaved.Text = "saved ✓";
        if (!stats.NotesOpen) SetNotesOpen(true);
        ((TextBlock)button.Child).Text = "✓  added to notes";
        button.IsHitTestVisible = false;
        Say(Pick(new[] { "Written down. Future you says thanks.", "Good call. Now actually read it later.", "Noted. Literally." }), null, false);
    }

    // ---------- Questions with code ----------
    // The first line is the question; any lines after it are code shown in a box
    static void SplitCode(string text, out string prompt, out string code)
    {
        int nl = text.IndexOf('\n');
        if (nl < 0) { prompt = text; code = null; return; }
        prompt = text.Substring(0, nl).Trim();
        code = text.Substring(nl + 1).TrimEnd();
        if (code.Trim().Length == 0) code = null;
    }

    // What Heather says out loud: the question, not the code
    static string SpokenQuestion(Q q)
    {
        string prompt, code;
        SplitCode(q.Text, out prompt, out code);
        return code == null ? prompt : prompt + " Read the code carefully.";
    }

    static readonly FontFamily CodeFont = new FontFamily("Cascadia Mono, Consolas, Courier New");

    static Border CodeBox(string code)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(12), Background = Hex("#B0080606"), BorderBrush = Hex("#2AFFFFFF"),
            BorderThickness = new Thickness(1), Padding = new Thickness(16, 12, 16, 13), Margin = new Thickness(0, 0, 0, 18),
            Child = new TextBlock
            {
                Text = code, FontFamily = CodeFont, FontSize = 15, LineHeight = 22, Foreground = Hex("#E6DCC8"),
                TextWrapping = TextWrapping.Wrap
            }
        };
    }

    // ---------- Helpers ----------
    static TextBlock T(string text, double size, FontWeight weight, Brush fg)
    {
        return new TextBlock { Text = text, FontSize = size, FontWeight = weight, Foreground = fg, TextWrapping = TextWrapping.Wrap };
    }

    static TextBlock Label(string text)
    {
        var t = T(text, 13, FontWeights.Bold, Muted);
        t.FontFamily = TitleFont;
        t.Margin = new Thickness(2, 22, 0, 10);
        return t;
    }

    static DoubleAnimation Anim(double to, int ms, IEasingFunction ease = null)
    {
        return new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut }
        };
    }

    static void ScaleTo(FrameworkElement el, double to, int ms)
    {
        var st = el.RenderTransform as ScaleTransform;
        if (st == null) return;
        var a = Anim(to, ms);
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    static void Pulse(FrameworkElement el, double from, double to, int ms)
    {
        var st = el.RenderTransform as ScaleTransform;
        if (st == null) return;
        var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    T2 Pick<T2>(T2[] items) { return items[rng.Next(items.Length)]; }

    List<T2> Shuffle<T2>(IEnumerable<T2> items)
    {
        var a = items.ToList();
        for (int i = a.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            var t = a[i]; a[i] = a[j]; a[j] = t;
        }
        return a;
    }

    static Btn Clickable(Btn b, Action click, bool scale = true)
    {
        b.Cursor = Cursors.Hand;
        if (scale)
        {
            b.RenderTransformOrigin = new Point(0.5, 0.5);
            b.RenderTransform = new ScaleTransform(1, 1);
            b.MouseEnter += (s, e) => ScaleTo(b, 1.02, 120);
            b.MouseLeave += (s, e) => ScaleTo(b, 1, 160);
            b.PreviewMouseLeftButtonDown += (s, e) => ScaleTo(b, 0.97, 70);
        }
        b.MouseLeftButtonUp += (s, e) =>
        {
            if (scale) ScaleTo(b, 1.02, 110);
            Sfx.Play(Sfx.Click);
            click();
        };
        return b;
    }

    static Btn IconButton(string glyph, string tip, Action click)
    {
        var b = new Btn
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(9), Background = Brushes.Transparent,
            ToolTip = tip, Margin = new Thickness(2, 0, 0, 0),
            Child = new TextBlock
            {
                Text = glyph, FontFamily = IconFont, FontSize = 13, Foreground = Muted,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        b.MouseEnter += (s, e) => b.Background = CardHover;
        b.MouseLeave += (s, e) => b.Background = Brushes.Transparent;
        return Clickable(b, click, false);
    }

    static Btn Pill(string text, bool on)
    {
        var b = new Btn
        {
            CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 8, 16, 9), Margin = new Thickness(0, 0, 9, 9),
            Child = new TextBlock { Text = text, FontSize = 14.5, FontWeight = FontWeights.SemiBold }
        };
        SetPill(b, on);
        return b;
    }

    static void SetPill(Border b, bool on)
    {
        var tb = (TextBlock)b.Child;
        b.Background = on ? Accent : CardBg;
        b.BorderBrush = on ? Hex("#55C23B30") : CardStroke;
        tb.Foreground = on ? Brushes.White : Muted;
    }

    static Btn BigButton(string text, Action click)
    {
        var b = new Btn
        {
            CornerRadius = new CornerRadius(16), Background = Accent,
            Padding = new Thickness(0, 16, 0, 16), Margin = new Thickness(0, 22, 0, 0),
            BorderBrush = Hex("#55C23B30"), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { Color = C("#B3201B"), BlurRadius = 22, ShadowDepth = 0, Opacity = 0.45 },
            Child = new TextBlock
            {
                Text = text, FontSize = 18, FontWeight = FontWeights.Bold, FontFamily = TitleFont, Foreground = Hex("#F2E8DA"),
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
        return Clickable(b, click);
    }

    static Btn SecondaryButton(string text, Action click)
    {
        var b = new Btn
        {
            CornerRadius = new CornerRadius(14), Background = CardBg, BorderBrush = CardStroke,
            BorderThickness = new Thickness(1), Padding = new Thickness(8, 12, 8, 12), Margin = new Thickness(5),
            Child = new TextBlock
            {
                Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = TextMain,
                HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
        b.MouseEnter += (s, e) => b.Background = CardHover;
        b.MouseLeave += (s, e) => b.Background = CardBg;
        return Clickable(b, click);
    }

    // A big numbered choice, used for classes and answers
    static Btn Choice(string badge, string text, string sub, Action click, bool compact = false)
    {
        var card = new Btn
        {
            CornerRadius = new CornerRadius(compact ? 13 : 16), Background = CardBg, BorderBrush = CardStroke,
            BorderThickness = new Thickness(1),
            Padding = compact ? new Thickness(10, 8, 14, 8) : new Thickness(14, 14, 18, 14),
            Margin = new Thickness(0, 0, 0, compact ? 6 : 10)
        };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new Border
        {
            Width = compact ? 28 : 34, Height = compact ? 28 : 34, CornerRadius = new CornerRadius(compact ? 8 : 10), Background = Hex("#1EFFFFFF"),
            VerticalAlignment = compact ? VerticalAlignment.Center : VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = badge, FontSize = compact ? 13 : 15, FontWeight = FontWeights.Bold, Foreground = Muted,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        });
        var sp = new StackPanel { Margin = new Thickness(compact ? 12 : 14, compact ? 0 : 5, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(sp, 1);
        sp.Children.Add(T(text, compact ? 15 : 17, compact ? FontWeights.SemiBold : FontWeights.Normal, TextMain));
        if (sub != null) sp.Children.Add(T(sub, compact ? 12 : 13, FontWeights.Normal, Muted));
        g.Children.Add(sp);
        card.Child = g;
        card.MouseEnter += (s, e) => { if (card.IsHitTestVisible) card.Background = CardHover; };
        card.MouseLeave += (s, e) => { if (card.IsHitTestVisible) card.Background = CardBg; };
        return Clickable(card, click);
    }

    static Border StatCard(string value, string label)
    {
        var sp = new StackPanel();
        sp.Children.Add(T(value, 22, FontWeights.Bold, TextMain));
        sp.Children.Add(T(label, 12.5, FontWeights.Normal, Muted));
        return new Border
        {
            CornerRadius = new CornerRadius(14), Background = CardBg, BorderBrush = CardStroke,
            BorderThickness = new Thickness(1), Padding = new Thickness(14, 10, 14, 11), Margin = new Thickness(5), Child = sp
        };
    }

    static TextBox Field(Panel parent, string text, bool multi)
    {
        var tb = new TextBox
        {
            Text = text, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = TextMain,
            CaretBrush = Brushes.White, SelectionBrush = Blood, FontSize = 15, FontFamily = UiFont,
            Padding = new Thickness(10, 9, 10, 9), TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multi ? 64 : 0, VerticalContentAlignment = VerticalAlignment.Top
        };
        var frame = new Border
        {
            CornerRadius = new CornerRadius(12), Background = FieldBg, BorderBrush = CardStroke,
            BorderThickness = new Thickness(1), Child = tb
        };
        tb.GotFocus += (s, e) => frame.BorderBrush = Hex("#66C23B30");
        tb.LostFocus += (s, e) => frame.BorderBrush = CardStroke;
        parent.Children.Add(frame);
        return tb;
    }

    void SetScreen(FrameworkElement content)
    {
        content.Opacity = 0;
        var tt = new TranslateTransform(0, 16);
        content.RenderTransform = tt;
        scroller.Content = content;
        scroller.ScrollToTop();
        content.BeginAnimation(OpacityProperty, Anim(1, 300));
        tt.BeginAnimation(TranslateTransform.YProperty, Anim(0, 360));
    }

    // ---------- Heather ----------
    void Nod() { if (heatherView != null) heatherView.Nod(); }
    void Hop() { if (heatherView != null) heatherView.Hop(); }
    void HeadShake() { if (heatherView != null) heatherView.Shake(); }
    void Cheer() { if (heatherView != null) heatherView.Cheer(); }
    void React(string pose, double seconds) { if (heatherView != null) heatherView.React(pose, seconds); }

    // Her "talking" level comes from the voice; ms > 70 means the speech is over
    void SetMouth(double open, int ms = 70)
    {
        if (heatherView == null) return;
        heatherView.Talk = open;
        if (open > 0) heatherView.Talking = true;
        else if (ms > 70) heatherView.Talking = false;
    }

    void Flash(Brush brush)
    {
        var c = ((SolidColorBrush)brush).Color;
        aura.Fill = new RadialGradientBrush(Color.FromArgb(0x66, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1600) };
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1600))));
        aura.BeginAnimation(OpacityProperty, a);
    }

    // Everything Heather says goes in the dialogue box; with speak, she says it out loud too.
    // clip: a voice-clip folder to play first in her real voice. Its clip stands in for the
    // first quipLen characters (her quick reaction), and the regular voice says the rest.
    void Say(string text, Brush flash = null, bool speak = true, string clip = null, int quipLen = 0)
    {
        if (flash != null) Flash(flash);
        // Never talk out loud while the quiz is hidden (e.g. when the app starts with Windows)
        if (!IsVisible && !snapshot) speak = false;
        StopClip();
        if (speak && clip != null)
        {
            FinishTalk();
            if (typer != null) typer.Stop();
            if (voice != null) voice.SpeakAsyncCancelAll();
            currentPrompt = null;
            int shown = Math.Max(0, Math.Min(quipLen, text.Length));
            dialogText.Text = text.Substring(0, shown);
            if (PlayClip(clip, () => Talk(dialogText, text, true, shown))) return;
        }
        Talk(dialogText, text, speak);
    }

    void Greet()
    {
        string hi = Pick(Greetings);
        Say(hi + " Pick a class on the right, and we'll get started.", null, true, "greeting", hi.Length);
    }

    void SetupVoice()
    {
        try
        {
            voice = new SpeechSynthesizer();
            UseVoice(stats.VoiceName);
            voice.SetOutputToDefaultAudioDevice();
            voice.SpeakProgress += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (e.Prompt != currentPrompt || talkTarget == null || talkSpokenLen == 0) return;
                int shown = talkSkip + (int)Math.Ceiling((double)(talkDisplay.Length - talkSkip) * (e.CharacterPosition + e.CharacterCount) / talkSpokenLen);
                talkTarget.Text = talkDisplay.Substring(0, Math.Min(talkDisplay.Length, Math.Max(shown, talkTarget.Text.Length)));
            }));
            voice.VisemeReached += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (e.Prompt != currentPrompt) return;
                int v = Math.Max(0, Math.Min(VisemeOpen.Length - 1, e.Viseme));
                SetMouth(VisemeOpen[v] * (0.8 + rng.NextDouble() * 0.2));
            }));
            voice.SpeakCompleted += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (e.Prompt != currentPrompt) return;
                SetMouth(0, 120);
                FinishTalk();
                OnTalkDone();
            }));
        }
        catch { voice = null; }
    }

    static IEnumerable<VoiceInfo> InstalledVoices(SpeechSynthesizer s)
    {
        return s.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo);
    }

    // Picks the requested voice, or else the most natural-sounding female voice installed.
    // Windows 11 natural voices (Aria, Jenny, ...) show up here once NaturalVoiceSAPIAdapter is installed.
    // Some voices are listed but can't actually speak (blocked online voices, Narrator-only
    // licenses), so test one quietly before trusting it. The built-in desktop voices always work.
    static bool VoiceWorks(string name)
    {
        if (name.EndsWith("Desktop")) return true;
        try
        {
            using (var test = new SpeechSynthesizer())
            using (var ms = new MemoryStream())
            {
                test.SelectVoice(name);
                test.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                test.Speak("Hi.");
                return ms.Length > 0;
            }
        }
        catch { return false; }
    }

    // Returns false if the requested voice doesn't work (and keeps a working one instead)
    bool UseVoice(string name)
    {
        // Kokoro voices are stored as "kokoro:<speaker id>", Piper as "piper:<model file name>".
        // With nothing chosen yet: Kokoro (nicole) if it's there, then Piper, then Windows voices.
        kokoroSid = -1;
        if (KokoroReady && (string.IsNullOrEmpty(name) || name.StartsWith("kokoro:")))
        {
            int sid;
            kokoroSid = !string.IsNullOrEmpty(name) && int.TryParse(name.Substring(7), out sid) ? sid : 6;
            piperModel = null;
            return true;
        }
        var piper = PiperModels();
        if (!string.IsNullOrEmpty(name) && name.StartsWith("piper:"))
        {
            var m = piper.FirstOrDefault(f => System.IO.Path.GetFileName(f) == name.Substring(6));
            if (m != null) { piperModel = m; return true; }
        }
        else if (string.IsNullOrEmpty(name) && piper.Count > 0)
        {
            piperModel = piper.FirstOrDefault(f => f.Contains("hfc_female")) ?? piper[0];
            return true;
        }
        piperModel = null;
        if (voice == null) return false;
        var voices = InstalledVoices(voice).ToList();
        var asked = voices.FirstOrDefault(v => v.Name == name);
        if (asked != null && VoiceWorks(asked.Name))
        {
            voice.SelectVoice(asked.Name);
            voice.Rate = asked.Name.Contains("Desktop") ? 1 : 0;
            return true;
        }
        VoiceInfo pick = null;
        {
            // Best fits for Heather first ("Ana" is a child's voice, so it's left out)
            string[] preferred = { "Microsoft Ava", "Microsoft Aria", "Microsoft Jenny", "Microsoft Emma", "Microsoft Michelle" };
            Func<VoiceInfo, int> rank = v =>
            {
                for (int i = 0; i < preferred.Length; i++)
                    if (v.Name.StartsWith(preferred[i]) && v.Name.IndexOf("Multilingual", StringComparison.OrdinalIgnoreCase) < 0) return i;
                return preferred.Length;
            };
            pick = voices
                .OrderByDescending(v => v.Name.IndexOf("Natural", StringComparison.OrdinalIgnoreCase) >= 0 || v.Name.IndexOf("Neural", StringComparison.OrdinalIgnoreCase) >= 0 || v.Name.IndexOf("Online", StringComparison.OrdinalIgnoreCase) >= 0)
                .ThenBy(rank)
                .ThenByDescending(v => v.Gender == VoiceGender.Female)
                .ThenByDescending(v => v.Culture.Name.StartsWith("en"))
                .Take(3)
                .FirstOrDefault(v => VoiceWorks(v.Name));
            // Nothing natural works: use the built-in female voice
            if (pick == null)
                pick = voices.Where(v => v.Name.EndsWith("Desktop"))
                    .OrderByDescending(v => v.Gender == VoiceGender.Female).FirstOrDefault();
        }
        if (pick == null) return false;
        voice.SelectVoice(pick.Name);
        // Natural voices already speak at a nice pace; the old desktop voices sound better a bit faster
        voice.Rate = pick.Name.Contains("Desktop") ? 1 : 0;
        return asked == null;
    }

    // ---------- Real voice clips ----------
    void EnsureVoiceFolders()
    {
        try
        {
            foreach (var f in ClipFolders) Directory.CreateDirectory(System.IO.Path.Combine(VoiceDir, f));
            string readme = System.IO.Path.Combine(VoiceDir, "READ ME.txt");
            if (!File.Exists(readme))
                File.WriteAllText(readme,
                    "Put short audio clips of Heather in these folders (.wav, .mp3, .wma or .m4a).\r\n" +
                    "When something happens, quizzahh plays a random clip from the matching folder,\r\n" +
                    "then the regular voice reads the explanation.\r\n\r\n" +
                    "  greeting      - when you open the quiz\r\n" +
                    "  correct       - you got one right\r\n" +
                    "  streak        - 3+ right in a row\r\n" +
                    "  wrong         - you got one wrong\r\n" +
                    "  levelup       - you leveled up\r\n" +
                    "  results_good  - you finished with 70% or better\r\n" +
                    "  results_bad   - you finished under 70%\r\n\r\n" +
                    "Empty folders are fine - that moment just uses the regular voice.\r\n" +
                    "Short clips (1-3 seconds) work best.\r\n", Encoding.UTF8);
        }
        catch { }
    }

    string RandomClip(string folder)
    {
        try
        {
            var dir = System.IO.Path.Combine(VoiceDir, folder);
            if (!Directory.Exists(dir)) return null;
            var files = Directory.GetFiles(dir).Where(f => ClipTypes.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant())).ToArray();
            return files.Length == 0 ? null : files[rng.Next(files.Length)];
        }
        catch { return null; }
    }

    // Plays one of her real clips (moving like she's talking), then calls after
    bool PlayClip(string folder, Action after)
    {
        string file = folder == null || stats.Quiet || snapshot ? null : RandomClip(folder);
        if (file == null) return false;
        int token = ++clipToken;
        if (clipPlayer == null)
        {
            clipPlayer = new MediaPlayer();
            clipPlayer.MediaEnded += (s, e) => ClipDone();
            clipPlayer.MediaFailed += (s, e) => ClipDone();
        }
        pendingAfterClip = after;
        pendingToken = token;
        if (flap == null)
        {
            flap = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
            flap.Tick += (s, e) => SetMouth(0.25 + rng.NextDouble() * 0.75);
        }
        flap.Start();
        clipPlayer.Open(new Uri(file));
        clipPlayer.Play();
        return true;
    }

    Action pendingAfterClip;
    int pendingToken;

    void ClipDone()
    {
        if (flap != null) flap.Stop();
        SetMouth(0, 120);
        var after = pendingAfterClip;
        pendingAfterClip = null;
        if (after != null && pendingToken == clipToken) after();
    }

    void StopClip()
    {
        clipToken++;
        pendingAfterClip = null;
        if (flap != null) flap.Stop();
        if (clipPlayer != null) clipPlayer.Stop();
    }

    // Says the text and reveals it in the dialogue box as she says it.
    // skip: that many leading characters are already showing and aren't spoken (a clip said them).
    void Talk(TextBlock target, string display, bool speak, int skip = 0)
    {
        FinishTalk();
        if (typer != null) typer.Stop();
        if (voice != null) voice.SpeakAsyncCancelAll();
        currentPrompt = null;
        StopPiper();
        skip = Math.Max(0, Math.Min(skip, display.Length));
        talkTarget = target;
        talkDisplay = display;
        target.Text = display.Substring(0, skip);
        if (skip >= display.Length) { talkTarget = null; OnTalkDone(); return; }

        if (speak && !stats.Quiet && kokoroSid >= 0)
        {
            KokoroTalk(target, display, skip);
        }
        else if (speak && !stats.Quiet && piperModel != null)
        {
            PiperTalk(target, display, skip);
        }
        else if (speak && voice != null && !stats.Quiet)
        {
            string spoken = Speakable(display.Substring(skip));
            talkSkip = skip;
            talkSpokenLen = spoken.Length;
            currentPrompt = voice.SpeakAsync(spoken);
        }
        else
        {
            // Typewriter effect, with her talking gestures along with it
            int tick = 0;
            typer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            typer.Tick += (s, e) =>
            {
                int n = Math.Min(display.Length, target.Text.Length + 2);
                target.Text = display.Substring(0, n);
                if (++tick % 5 == 0) SetMouth(0.2 + rng.NextDouble() * 0.7);
                if (n >= display.Length) { typer.Stop(); SetMouth(0, 120); talkTarget = null; OnTalkDone(); }
            };
            typer.Start();
        }
    }

    void FinishTalk()
    {
        if (talkTarget != null) talkTarget.Text = talkDisplay;
        talkTarget = null;
    }

    // She finished saying something (or it was shown silently)
    void OnTalkDone()
    {
        ShowThinking(false);
        StartSpeedTimer();
    }

    void ShowThinking(bool on)
    {
        if (dialogThinking == null) return;
        dialogThinking.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        dialogThinking.BeginAnimation(OpacityProperty, on
            ? new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(550)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    // ---------- Piper: natural offline voice (piper\piper.exe + piper\voices\*.onnx next to the app) ----------
    static readonly string AppDir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    static readonly string PiperExe = System.IO.Path.Combine(AppDir, "piper", "piper", "piper.exe");
    static readonly string PiperVoicesDir = System.IO.Path.Combine(AppDir, "piper", "voices");
    string piperModel;
    MediaPlayer speechPlayer;
    DispatcherTimer speechTimer;
    int speechToken;

    static List<string> PiperModels()
    {
        try
        {
            if (!File.Exists(PiperExe) || !Directory.Exists(PiperVoicesDir)) return new List<string>();
            return Directory.GetFiles(PiperVoicesDir, "*.onnx").OrderBy(f => f).ToList();
        }
        catch { return new List<string>(); }
    }

    // "en_US-hfc_female-medium.onnx" -> "Piper: hfc female"
    static string PiperLabel(string model)
    {
        var parts = System.IO.Path.GetFileNameWithoutExtension(model).Split('-');
        return "Piper: " + (parts.Length > 1 ? parts[1].Replace('_', ' ') : parts[0]);
    }

    void StopPiper()
    {
        speechToken++;
        if (speechTimer != null) speechTimer.Stop();
        if (speechPlayer != null) { speechPlayer.Stop(); speechPlayer.Close(); }
    }

    // Generates the line with Piper in the background, then plays it, revealing the text in step
    // with the audio and bobbing her head with how loud each moment is
    void PiperTalk(TextBlock target, string display, int skip)
    {
        int token = ++speechToken;
        kokoroJob = null;
        ShowThinking(true);
        string model = piperModel;
        // Piper reads plain ASCII from stdin
        string text = Speakable(display.Substring(skip)).Replace('’', '\'').Replace('‘', '\'').Replace('“', '"').Replace('”', '"').Replace("—", ", ");
        text = new string(text.Where(ch => ch < 128).ToArray()).Replace("\r", " ").Replace("\n", " ");
        string wav = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quizzahh_say_" + token + ".wav");
        SetMouth(0.15);
        System.Threading.Tasks.Task.Factory.StartNew(() =>
        {
            double dur = 0;
            double[] env = null;
            try
            {
                var psi = new ProcessStartInfo(PiperExe, "--model \"" + model + "\" --output_file \"" + wav + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(PiperExe)
                };
                using (var p = Process.Start(psi))
                {
                    p.OutputDataReceived += (s, e) => { };
                    p.ErrorDataReceived += (s, e) => { };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.StandardInput.WriteLine(text);
                    p.StandardInput.Close();
                    p.WaitForExit(30000);
                }
                env = Envelope(wav, out dur);
            }
            catch { env = null; }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (token != speechToken) return;
                if (env == null || dur <= 0) { Talk(target, display, false, skip); return; }
                PlaySpeech(target, display, skip, wav, env, dur, token);
            }));
        });
    }

    void PlaySpeech(TextBlock target, string display, int skip, string wav, double[] env, double dur, int token)
    {
        if (speechPlayer == null)
        {
            speechPlayer = new MediaPlayer();
            speechPlayer.MediaEnded += (s, e) => OnSpeechEnded();
            speechPlayer.MediaFailed += (s, e) => OnSpeechEnded();
        }
        if (speechTimer == null)
        {
            speechTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            speechTimer.Tick += (s, e) => { if (speechTick != null) speechTick(); };
        }
        playingToken = token;
        ShowThinking(false);
        speechPlayer.Open(new Uri(wav));
        speechPlayer.Play();
        speechTick = () =>
        {
            if (token != speechToken) return;
            double pos = speechPlayer.Position.TotalSeconds;
            double frac = Math.Min(1, pos / dur);
            if (talkTarget == target)
            {
                int shown = skip + (int)Math.Ceiling((display.Length - skip) * Math.Min(1, frac * 1.08));
                target.Text = display.Substring(0, Math.Min(display.Length, Math.Max(shown, target.Text.Length)));
            }
            int i = (int)(pos / 0.05);
            SetMouth(i < env.Length ? env[i] : 0);
        };
        speechTimer.Start();
    }

    Action speechTick;

    // ---------- Kokoro: the most natural offline voice, run through sherpa-onnx ----------
    static readonly string KokoroDir = System.IO.Path.Combine(AppDir, "kokoro");
    // sherpa-onnx 1.13.x on purpose: the 2.x line dropped espeak-ng, so any word missing from
    // the lexicon (numbers, acronyms, names, even "opened") was silently skipped. 1.x falls
    // back to espeak-ng for those words.
    static readonly string KokoroExe = FirstExisting(
        System.IO.Path.Combine(KokoroDir, "sherpa-onnx-v1.13.8-win-x64-shared-MT-Release", "bin", "sherpa-onnx-offline-tts.exe"),
        System.IO.Path.Combine(KokoroDir, "sherpa-onnx-v2.0.0-win-x64-shared-MT-Release", "bin", "sherpa-onnx-offline-tts.exe"));

    static string FirstExisting(params string[] paths)
    {
        foreach (var p in paths) if (File.Exists(p)) return p;
        return paths[0];
    }

    // Generated sentences are kept on disk, so repeated lines and prefetched questions play instantly
    static readonly string SpeechCacheDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "quizzahh", "speech-cache");
    const string SpeechCacheVersion = "k1.13.8|n3"; // change when the engine or text cleanup changes
    int playingToken;
    static readonly string KokoroModelDir = System.IO.Path.Combine(KokoroDir, "kokoro-multi-lang-v1_0");
    // Speaker IDs in the Kokoro v1.0 model, best fits for Heather first
    static readonly string[][] KokoroVoices =
    {
        new[] { "6", "nicole" }, new[] { "3", "heart" }, new[] { "2", "bella" }, new[] { "9", "sarah" },
        new[] { "10", "sky" }, new[] { "4", "jessica" }, new[] { "7", "nova" }
    };
    // At most 3 sentences generate at once (each run loads the model, ~300 MB)
    static readonly System.Threading.SemaphoreSlim kokoroSlots = new System.Threading.SemaphoreSlim(3);
    int kokoroSid = -1;

    static bool KokoroReady
    {
        get { return File.Exists(KokoroExe) && File.Exists(System.IO.Path.Combine(KokoroModelDir, "model.onnx")); }
    }

    class KokoroJob
    {
        public int Token, Current, DoneChars, TotalChars, Skip;
        public List<string> Chunks;
        public string[] Wavs;
        public double[][] Envs;
        public double[] Durs;
        public bool[] Ready;
        public bool Waiting = true;
        public TextBlock Target;
        public string Display;
    }

    KokoroJob kokoroJob;

    static string Ascii(string s)
    {
        s = s.Replace('’', '\'').Replace('‘', '\'').Replace('“', '\'').Replace('”', '\'').Replace('"', '\'')
            .Replace("—", ", ").Replace("…", "...");
        // Accented letters read as their plain versions (Königsberg -> Konigsberg) instead of vanishing
        var sb = new StringBuilder();
        foreach (char ch in s.Normalize(NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (ch < 128) sb.Append(ch);
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString().Replace("\r", " ").Replace("\n", " "), @"\s{2,}", " ").Trim();
    }

    // Splits into sentences, keeping the first one short so she starts talking quickly
    static List<string> Chunks(string text)
    {
        var parts = System.Text.RegularExpressions.Regex.Split(text, @"(?<=[.!?])\s+").Where(p => p.Trim().Length > 0).ToList();
        var res = new List<string>();
        string cur = "";
        foreach (var p in parts)
        {
            cur = cur.Length == 0 ? p : cur + " " + p;
            if (cur.Length >= (res.Count == 0 ? 12 : 40)) { res.Add(cur); cur = ""; }
        }
        if (cur.Length > 0)
        {
            if (res.Count > 0 && cur.Length < 20) res[res.Count - 1] += " " + cur;
            else res.Add(cur);
        }
        return res;
    }

    static string CachePath(int sid, string text)
    {
        using (var md5 = System.Security.Cryptography.MD5.Create())
        {
            var h = md5.ComputeHash(Encoding.UTF8.GetBytes(SpeechCacheVersion + "|" + sid + "|" + text));
            return System.IO.Path.Combine(SpeechCacheDir, "k_" + BitConverter.ToString(h).Replace("-", "").Substring(0, 24) + ".wav");
        }
    }

    static bool Usable(string wav)
    {
        try { return File.Exists(wav) && new FileInfo(wav).Length > 2000; } catch { return false; }
    }

    // Returns a WAV of the sentence, from the cache or freshly generated (null if it failed)
    static string KokoroGenerate(int sid, string text)
    {
        string wav = CachePath(sid, text);
        if (Usable(wav)) { try { File.SetLastWriteTimeUtc(wav, DateTime.UtcNow); } catch { } return wav; }
        kokoroSlots.Wait();
        try
        {
            if (Usable(wav)) return wav; // made by another thread while we waited
            Directory.CreateDirectory(SpeechCacheDir);
            string tmp = wav.Substring(0, wav.Length - 4) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".part.wav";
            string m = KokoroModelDir;
            string args =
                "\"--kokoro-model=" + System.IO.Path.Combine(m, "model.onnx") + "\" " +
                "\"--kokoro-voices=" + System.IO.Path.Combine(m, "voices.bin") + "\" " +
                "\"--kokoro-tokens=" + System.IO.Path.Combine(m, "tokens.txt") + "\" " +
                "\"--kokoro-data-dir=" + System.IO.Path.Combine(m, "espeak-ng-data") + "\" " +
                "\"--kokoro-lexicon=" + System.IO.Path.Combine(m, "lexicon-us-en.txt") + "\" " +
                "--num-threads=4 --sid=" + sid + " \"--output-filename=" + tmp + "\" \"" + text + "\"";
            var psi = new ProcessStartInfo(KokoroExe, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(KokoroExe)
            };
            using (var p = Process.Start(psi))
            {
                p.OutputDataReceived += (s, e) => { };
                p.ErrorDataReceived += (s, e) => { };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } }
            }
            if (!Usable(tmp)) { try { File.Delete(tmp); } catch { } return null; }
            try
            {
                if (Usable(wav)) File.Delete(tmp);
                else File.Move(tmp, wav);
            }
            catch { return Usable(wav) ? wav : null; }
            return wav;
        }
        catch { return null; }
        finally { kokoroSlots.Release(); }
    }

    // Generates the next lines ahead of time so she can start talking right away
    void PrefetchSpeech(string text)
    {
        if (kokoroSid < 0 || stats.Quiet || snapshot) return;
        int sid = kokoroSid;
        foreach (var chunk in Chunks(Ascii(Speakable(text))))
        {
            string c = chunk;
            System.Threading.Tasks.Task.Factory.StartNew(() => KokoroGenerate(sid, c));
        }
    }

    // Keeps the speech cache under ~250 MB and clears leftovers from older versions
    static void CleanSpeechCache()
    {
        System.Threading.Tasks.Task.Factory.StartNew(() =>
        {
            try
            {
                foreach (var pat in new[] { "quizzahh_k_*.wav", "quizzahh_say_*.wav" })
                    foreach (var f in Directory.GetFiles(System.IO.Path.GetTempPath(), pat))
                        try { File.Delete(f); } catch { }
                if (!Directory.Exists(SpeechCacheDir)) return;
                foreach (var f in Directory.GetFiles(SpeechCacheDir, "*.part.wav")) try { File.Delete(f); } catch { }
                var files = new DirectoryInfo(SpeechCacheDir).GetFiles("k_*.wav").OrderBy(f => f.LastWriteTimeUtc).ToList();
                long total = files.Sum(f => f.Length);
                foreach (var f in files)
                {
                    if (total < 250L * 1024 * 1024) break;
                    total -= f.Length;
                    try { f.Delete(); } catch { }
                }
            }
            catch { }
        });
    }

    void KokoroTalk(TextBlock target, string display, int skip)
    {
        int token = ++speechToken;
        var chunks = Chunks(Ascii(Speakable(display.Substring(skip))));
        if (chunks.Count == 0) { EndSpeech(); return; }
        var job = new KokoroJob
        {
            Token = token, Chunks = chunks, TotalChars = chunks.Sum(c => c.Length), Skip = skip,
            Wavs = new string[chunks.Count], Envs = new double[chunks.Count][], Durs = new double[chunks.Count],
            Ready = new bool[chunks.Count], Target = target, Display = display
        };
        kokoroJob = job;
        ShowThinking(true);
        int sid = kokoroSid;
        for (int i = 0; i < chunks.Count; i++)
        {
            int ci = i;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                string made = token == speechToken ? KokoroGenerate(sid, chunks[ci]) : null;
                double dur = 0;
                double[] env = null;
                try { if (made != null) env = Envelope(made, out dur); } catch { env = null; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (token != speechToken) return;
                    job.Wavs[ci] = env != null ? made : null;
                    job.Envs[ci] = env;
                    job.Durs[ci] = dur;
                    job.Ready[ci] = true;
                    if (job.Waiting && ci == job.Current) PlayNextChunk(job);
                }));
            });
        }
    }

    void PlayNextChunk(KokoroJob job)
    {
        if (job.Token != speechToken) return;
        while (job.Current < job.Chunks.Count && job.Ready[job.Current] && job.Wavs[job.Current] == null)
        {
            job.DoneChars += job.Chunks[job.Current].Length; // that sentence failed; skip it
            job.Current++;
        }
        if (job.Current >= job.Chunks.Count) { kokoroJob = null; EndSpeech(); return; }
        if (!job.Ready[job.Current]) { job.Waiting = true; return; }
        job.Waiting = false;

        if (speechPlayer == null)
        {
            speechPlayer = new MediaPlayer();
            speechPlayer.MediaEnded += (s, e) => OnSpeechEnded();
            speechPlayer.MediaFailed += (s, e) => OnSpeechEnded();
        }
        if (speechTimer == null)
        {
            speechTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            speechTimer.Tick += (s, e) => { if (speechTick != null) speechTick(); };
        }
        int c = job.Current;
        double dur = job.Durs[c];
        var env = job.Envs[c];
        playingToken = job.Token;
        ShowThinking(false);
        speechPlayer.Open(new Uri(job.Wavs[c]));
        speechPlayer.Play();
        speechTick = () =>
        {
            if (job.Token != speechToken) return;
            double pos = speechPlayer.Position.TotalSeconds;
            double frac = (job.DoneChars + job.Chunks[c].Length * Math.Min(1, pos / dur * 1.05)) / job.TotalChars;
            if (talkTarget == job.Target)
            {
                int shown = job.Skip + (int)Math.Ceiling((job.Display.Length - job.Skip) * Math.Min(1, frac));
                job.Target.Text = job.Display.Substring(0, Math.Min(job.Display.Length, Math.Max(shown, job.Target.Text.Length)));
            }
            int i = (int)(pos / 0.05);
            SetMouth(i < env.Length ? env[i] : 0);
        };
        speechTimer.Start();
    }

    // One audio file finished: move to the next Kokoro sentence, or wrap up
    void OnSpeechEnded()
    {
        // Ignore stray events from audio that was already interrupted
        if (playingToken != speechToken) return;
        var job = kokoroJob;
        if (job != null && job.Token == speechToken)
        {
            if (job.Current < job.Chunks.Count)
            {
                job.DoneChars += job.Chunks[job.Current].Length;
                job.Current++;
            }
            PlayNextChunk(job);
            return;
        }
        EndSpeech();
    }

    void EndSpeech()
    {
        if (speechTimer != null) speechTimer.Stop();
        SetMouth(0, 120);
        FinishTalk();
        OnTalkDone();
    }

    // Loudness (0..1) of a 16-bit mono WAV in 50 ms steps, plus its length in seconds
    static double[] Envelope(string wav, out double seconds)
    {
        seconds = 0;
        var bytes = File.ReadAllBytes(wav);
        int rate = 22050, pos = 12, dataStart = -1, dataLen = 0;
        while (pos + 8 <= bytes.Length)
        {
            string id = Encoding.ASCII.GetString(bytes, pos, 4);
            int len = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "fmt ") rate = BitConverter.ToInt32(bytes, pos + 12);
            if (id == "data") { dataStart = pos + 8; dataLen = Math.Min(len, bytes.Length - dataStart); break; }
            pos += 8 + len;
        }
        if (dataStart < 0 || dataLen <= 0) return null;
        int samples = dataLen / 2;
        seconds = (double)samples / rate;
        int win = rate / 20;
        var env = new double[samples / win + 1];
        double max = 1e-6;
        for (int w = 0; w < env.Length; w++)
        {
            double sum = 0;
            int n = 0;
            for (int k = w * win; k < Math.Min(samples, (w + 1) * win); k++, n++)
            {
                double v = BitConverter.ToInt16(bytes, dataStart + k * 2) / 32768.0;
                sum += v * v;
            }
            env[w] = n > 0 ? Math.Sqrt(sum / n) : 0;
            max = Math.Max(max, env[w]);
        }
        for (int w = 0; w < env.Length; w++) env[w] = Math.Min(1, env[w] / max * 1.2);
        return env;
    }

    void StopTalking()
    {
        ShowThinking(false);
        StopPiper();
        StopClip();
        FinishTalk();
        currentPrompt = null;
        if (typer != null) { typer.Stop(); typer = null; }
        if (voice != null) voice.SpeakAsyncCancelAll();
        SetMouth(0, 120);
    }

    // Rewrites symbols, abbreviations and math so the voice reads them naturally
    static string Speakable(string s)
    {
        Func<string, string, string, string> rx = (t, pat, rep) => System.Text.RegularExpressions.Regex.Replace(t, pat, rep);
        Func<string, string, System.Text.RegularExpressions.MatchEvaluator, string> rxm = (t, pat, ev) => System.Text.RegularExpressions.Regex.Replace(t, pat, ev);
        // The real minus sign (−) reads the same as a hyphen-minus; x̄ / ȳ are "bar", p̂ is "hat"
        s = s.Replace("−", "-").Replace("ȳ", "y bar");
        s = rx(s, @"(\w)̄", "$1 bar");
        s = rx(s, @"(\w)̂", "$1 hat");
        // Exponents with a sign inside them: xⁿ⁺¹, xⁿ⁻¹, 2ʰ⁺¹
        s = s.Replace("ⁿ⁺¹", " to the n plus 1").Replace("ⁿ⁻¹", " to the n minus 1").Replace("ⁿ⁻ᵏ", " to the n minus k")
            .Replace("ʰ⁺¹", " to the h plus 1").Replace("ʰ", " to the h");
        // Ranges of years: "1846-48" or "1955–56" -> "1846 to 48"
        s = rx(s, @"\b(\d{4})\s*[-–]\s*(\d{2,4})\b", "$1 to $2");
        // "$15 million" -> "15 million dollars"
        s = rx(s, @"\$(\d+(?:[.,]\d+)?)\s*(thousand|million|billion|trillion)", "$1 $2 dollars");
        // Calculus: limits, derivatives, logs and trig read the way a teacher says them
        s = rx(s, @"\blim\s*([a-z])\s*→\s*", "the limit as $1 approaches ");
        s = rx(s, @"\bd²y\s*/\s*dx²", "the second derivative of y");
        s = rx(s, @"\bd([A-Za-z])\s*/\s*d([a-z])\b", "d$1 d$2");
        s = rx(s, @"′\s*/\s*", " prime over ");
        s = rx(s, @"([⁰¹²³⁴⁵⁶⁷⁸⁹ⁿ₀₁₂₃₄₅₆₇₈₉])\s*/\s*(\(|[0-9]+|[a-z](?![a-z]))", "$1 over $2");
        s = rx(s, @"\bd\s*/\s*dx\b", "the derivative of");
        s = rx(s, @"\bln\s*\(", "the natural log of (");
        s = rx(s, @"\bln\s+", "the natural log of ");
        s = rx(s, @"\barc(sin|cos|tan)\b", "arc $1");
        s = rx(s, @"\bsin\b", "sine");
        s = rx(s, @"\bcos\b", "cosine");
        s = rx(s, @"\btan\b", "tangent");
        s = rx(s, @"\bsec\b", "secant");
        s = rx(s, @"\bcsc\b", "cosecant");
        s = rx(s, @"\bcot\b", "cotangent");
        s = s.Replace("⁻¹", " to the negative 1").Replace("⁻²", " to the negative 2").Replace("⁻ˣ", " to the negative x")
            .Replace("″", " double prime");
        // Ion charges: "Na⁺" -> "Na plus", "SO₄²⁻" -> "S O 4, 2 minus" (exponents like 10⁻³ are left alone)
        s = rxm(s, @"([²³]?)([⁺⁻])(?![⁰¹²³⁴⁵⁶⁷⁸⁹ˣⁿ])", m => " " + (m.Groups[1].Value == "²" ? "2 " : m.Groups[1].Value == "³" ? "3 " : "")
            + (m.Groups[2].Value == "⁺" ? "plus" : "minus") + " ");
        // Network prefixes: "10.0.0.0/8" -> "10.0.0.0 slash 8"
        s = rx(s, @"(\d+\.\d+\.\d+\.\d+)/(\d{1,3})", "$1 slash $2");
        // Units after numbers: "10 Mbps", "4 KB", "9.8 m/s²", "5 kg", "20 ms"...
        s = rx(s, @"(\d)\s*m/s²", "$1 meters per second squared");
        s = rx(s, @"(\d)\s*m/s\b", "$1 meters per second");
        s = rx(s, @"(\d)\s*km/h\b", "$1 kilometers per hour");
        s = rx(s, @"(\d)\s*g/mol\b", "$1 grams per mole");
        s = rxm(s, @"(\d)\s*([KMGT])bps\b", m => m.Groups[1].Value + " " + new Dictionary<string, string> { { "K", "kilobits" }, { "M", "megabits" }, { "G", "gigabits" }, { "T", "terabits" } }[m.Groups[2].Value] + " per second");
        s = rxm(s, @"(\d)\s*([KMGT])B\b", m => m.Groups[1].Value + " " + new Dictionary<string, string> { { "K", "kilobytes" }, { "M", "megabytes" }, { "G", "gigabytes" }, { "T", "terabytes" } }[m.Groups[2].Value]);
        s = rxm(s, @"(\d)\s*(GHz|MHz|kHz|Hz)\b", m => m.Groups[1].Value + " " + new Dictionary<string, string> { { "GHz", "gigahertz" }, { "MHz", "megahertz" }, { "kHz", "kilohertz" }, { "Hz", "hertz" } }[m.Groups[2].Value]);
        s = rxm(s, @"(\d)\s*(ms|ns|µs|kg|km|cm|mm|mL|kJ|kPa|atm|mol)\b", m => m.Groups[1].Value + " " + new Dictionary<string, string>
            {
                { "ms", "milliseconds" }, { "ns", "nanoseconds" }, { "µs", "microseconds" }, { "kg", "kilograms" }, { "km", "kilometers" },
                { "cm", "centimeters" }, { "mm", "millimeters" }, { "mL", "milliliters" }, { "kJ", "kilojoules" }, { "kPa", "kilopascals" },
                { "atm", "atmospheres" }, { "mol", "moles" }
            }[m.Groups[2].Value]);
        s = rxm(s, @"(\d)\s+(N|J|W|m|s|g|L)\b(?![.'’]\w)", m => m.Groups[1].Value + " " + new Dictionary<string, string>
            {
                { "N", "newtons" }, { "J", "joules" }, { "W", "watts" }, { "m", "meters" }, { "s", "seconds" }, { "g", "grams" }, { "L", "liters" }
            }[m.Groups[2].Value]);
        // "36°30'" -> "36 degrees 30 minutes", "30°" -> "30 degrees"
        s = rx(s, @"(\d+)°\s*(\d+)['′]", "$1 degrees $2 minutes");
        s = s.Replace("°", " degrees");
        // Class names like "CS 1 - Intro" pause instead of saying "minus"
        s = rx(s, @"\b([A-Z][A-Za-z]*\s+\d+)\s+-\s+", "$1, ");
        // Absolute value: |x - 3| -> the absolute value of x - 3
        s = rx(s, @"\|([^|]{1,25})\|", " the absolute value of $1 ");
        // n! -> n factorial (single letters/numbers only, so "Got it!" stays normal)
        s = rx(s, @"(?<![A-Za-z0-9])([a-z]|\d+)!", "$1 factorial");
        // Logic, sets, more Greek letters and chemistry subscripts (discrete math, physics, chemistry)
        s = s.Replace("∀", " for all ").Replace("∃", " there exists ").Replace("↔", " if and only if ").Replace("⇔", " if and only if ")
            .Replace("⇒", " implies ").Replace("⊆", " is a subset of ").Replace("⊂", " is a proper subset of ").Replace("∉", " is not in ")
            .Replace("∅", " the empty set ").Replace("ℝ", " the real numbers ").Replace("ℤ", " the integers ").Replace("ℕ", " the natural numbers ")
            .Replace("ℚ", " the rational numbers ").Replace("≡", " is congruent to ").Replace("⌊", " the floor of ").Replace("⌋", " ")
            .Replace("⌈", " the ceiling of ").Replace("⌉", " ").Replace("∘", " composed with ").Replace("δ", " delta ").Replace("ε", " epsilon ")
            .Replace("ω", " omega ").Replace("ρ", " rho ").Replace("τ", " tau ").Replace("β", " beta ").Replace("γ", " gamma ")
            .Replace("∂", " partial ").Replace("∇", " del ").Replace("∏", " the product of ").Replace("∝", " is proportional to ")
            .Replace("∴", " therefore ").Replace("←", " gets ").Replace("⟹", " implies ").Replace("½", " one half ")
            .Replace("log₁₀", "log base 10").Replace("₃", " 3").Replace("₄", " 4").Replace("₅", " 5").Replace("₆", " 6")
            .Replace("₇", " 7").Replace("₈", " 8").Replace("₉", " 9").Replace("ⁱ", " to the i").Replace("ˣ", " to the x").Replace("ᵖ", " to the p").Replace("ᵏ", " to the k").Replace("ᵐ", " to the m").Replace("ᵀ", " transpose ")
            .Replace("&&", " and ").Replace("||", " or ");
        s = rx(s, @"φ(?!\()", " phi ");
        // Common abbreviations and symbols
        s = s.Replace("e.g.", "for example").Replace("i.e.", "that is").Replace("etc.", "et cetera")
            .Replace(" vs. ", " versus ").Replace(" vs ", " versus ").Replace("U.S.", "U S").Replace("a.k.a.", "also known as")
            .Replace("C++", "C plus plus").Replace("C#", "C sharp").Replace(".NET", "dot net").Replace("Node.js", "node J S")
            .Replace(" & ", " and ").Replace("&", " and ").Replace("≈", " approximately ").Replace("≠", " is not equal to ")
            .Replace(" => ", " goes to ").Replace(" == ", " equals ").Replace(" != ", " is not equal to ")
            .Replace(" >= ", " is at least ").Replace(" <= ", " is at most ").Replace(" > ", " greater than ").Replace(" < ", " less than ")
            .Replace(" = ", " equals ").Replace("∞", " infinity ").Replace("∑", " the sum of ").Replace("∫", " the integral of ")
            .Replace("π", " pi ").Replace("θ", " theta ").Replace("λ", " lambda ").Replace("μ", " mu ").Replace("σ", " sigma ")
            .Replace("Δ", " delta ").Replace("∈", " in ").Replace("∪", " union ").Replace("∩", " intersect ")
            .Replace("¬", " not ").Replace("∧", " and ").Replace("∨", " or ").Replace("⊕", " X O R ").Replace("′", " prime");
        // Math minus between terms: "x - 3" -> "x minus 3"
        s = rx(s, @"(?<=[\w\)²³ⁿ])\s-\s(?=[\w\(√])", " minus ");
        // Math: "3/2" or "(x + 6)/3" is read "over", powers like x⁴ or 2^(t/3) as "to the"
        s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<![A-Za-z])([0-9]+|[a-z]|\))\s*/\s*(\(|[0-9]+|[a-z](?![a-z]))", "$1 over $2");
        s = s.Replace("ŷ", "y hat").Replace("α", "alpha").Replace("±", " plus or minus ").Replace("√(", "square root of (").Replace("^", " to the ").Replace("⁻³", " to the negative 3").Replace("⁻", " to the negative ")
            .Replace("⁴", " to the 4th").Replace("⁵", " to the 5th").Replace("⁰", " to the zero").Replace("¹²", " to the 12th")
            .Replace("·", " times ").Replace("→", ", then ");
        return s.Replace("O(1)", "O of 1").Replace("O(", "O of ").Replace("Θ(", "theta of ").Replace("Ω(", "omega of ")
            .Replace("log₂", "log base 2").Replace("φ(", "phi of ").Replace("√", "square root of ").Replace("α", "alpha")
            .Replace("≤p", " reduces to ").Replace("≤", " at most ").Replace("≥", " at least ").Replace("·", " times ")
            .Replace("²", " squared").Replace("³", " cubed").Replace("ⁿ", " to the n").Replace("¹⁰", " to the 10th")
            .Replace("⁹", " to the 9th").Replace("⁷", " to the 7th").Replace("⁶", " to the 6th").Replace("ₙ", " n")
            .Replace("^(", " to the ").Replace("×", " times ").Replace("÷", " divided by ")
            .Replace("→", " to ").Replace("–", " to ").Replace("e.g.", "for example").Replace(" % ", " mod ")
            .Replace("%", " percent").Replace("/", " slash ").Replace("~", "about ")
            .Replace("₁", " 1").Replace("₀", " 0").Replace("₂", " 2").Replace("¹", " to the 1st").Replace("⁸", " to the 8th")
            .Replace("+", " plus ").Replace("⁺", " plus ");
    }

    // ---------- HUD ----------
    static int Level(int xp)
    {
        int into, need;
        return Level(xp, out into, out need);
    }

    static int Level(int xp, out int into, out int need)
    {
        int lvl = 1;
        need = 100;
        while (xp >= need) { xp -= need; lvl++; need = 100 + 50 * (lvl - 1); }
        into = xp;
        return lvl;
    }

    static string TitleFor(int lvl) { return Titles[Math.Min(lvl - 1, Titles.Length - 1)]; }

    void UpdateHud(bool animate)
    {
        int into, need;
        int lvl = Level(stats.Xp, out into, out need);
        levelText.Text = "LV " + lvl + "  ·  " + TitleFor(lvl);
        xpText.Text = into + " / " + need + " XP";
        double w = xpTrack.ActualWidth * into / need;
        if (animate) xpFill.BeginAnimation(WidthProperty, Anim(w, 600));
        else { xpFill.BeginAnimation(WidthProperty, null); xpFill.Width = w; }
        streakText.Text = "🔥 " + streak;
        streakText.Opacity = streak > 0 ? 1 : 0.35;
    }

    // ---------- Step 1: pick a class ----------
    void ShowHome()
    {
        mode = "home";
        StopTalking();
        LoadClasses();
        if (notesFor != currentClass) LoadNotes(currentClass);
        classLabel.Text = "";

        var sp = new StackPanel();
        homeTitle = new TextBlock
        {
            Text = "QUIZZAHH", FontSize = 48, FontWeight = FontWeights.Bold, FontFamily = TitleFont, Foreground = TitleRed,
            Effect = new DropShadowEffect { Color = C("#B3201B"), BlurRadius = 20, ShadowDepth = 0, Opacity = 0.6 }
        };
        sp.Children.Add(homeTitle);
        sp.Children.Add(T("In my restless dreams, I see that test.", 15, FontWeights.Normal, Muted));

        var cards = new UniformGrid { Columns = 4, Margin = new Thickness(-5, 16, -5, 0) };
        cards.Children.Add(StatCard(stats.BestStreak.ToString(), "best streak"));
        cards.Children.Add(StatCard(stats.Answered.ToString(), "answered"));
        cards.Children.Add(StatCard(stats.Answered > 0 ? (100 * stats.Correct / stats.Answered) + "%" : "—", "accuracy"));
        int learnedAll = classes.Sum(kv => mastery.LearnedCount(kv.Key, kv.Value));
        int totalAll = classes.Sum(kv => kv.Value.Count);
        cards.Children.Add(StatCard((totalAll > 0 ? 100 * learnedAll / totalAll : 0) + "%", "learned"));
        sp.Children.Add(cards);
        sp.Children.Add(CodeLabCard());

        sp.Children.Add(Label("CHOOSE A CLASS"));
        // Year of the degree: Year 1 is where to start
        var classList = new StackPanel();
        if (classYears.Values.Any(y => y > 0))
        {
            if (stats.YearFilter < 0 || stats.YearFilter > 4) stats.YearFilter = 0;
            var yearRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
            var yearPills = new List<Btn>();
            var blurb = T(YearBlurbs[stats.YearFilter], 13, FontWeights.Normal, Muted);
            blurb.Margin = new Thickness(2, 0, 0, 4);
            string[] yearNames = { "All years", "Year 1", "Year 2", "Year 3", "Year 4" };
            for (int yi = 0; yi < yearNames.Length; yi++)
            {
                int y = yi;
                var pill = Pill(yearNames[yi], y == stats.YearFilter);
                pill.ToolTip = YearBlurbs[y];
                // compact, so all five fit on one row
                pill.Padding = new Thickness(11, 6, 11, 7);
                pill.Margin = new Thickness(0, 0, 6, 6);
                ((TextBlock)pill.Child).FontSize = 13;
                Clickable(pill, () =>
                {
                    stats.YearFilter = y;
                    stats.Save();
                    for (int k = 0; k < yearPills.Count; k++) SetPill(yearPills[k], k == y);
                    blurb.Text = YearBlurbs[y];
                    FillClassList(classList);
                }, false);
                yearPills.Add(pill);
                yearRow.Children.Add(pill);
            }
            sp.Children.Add(yearRow);
            sp.Children.Add(blurb);
        }
        else stats.YearFilter = 0;
        FillClassList(classList);
        sp.Children.Add(classList);
        sp.Children.Add(Choice("+", "New class", "Make a class and add its first question", () => ShowAdd(true)));
        var row = new UniformGrid { Columns = 2, Margin = new Thickness(-5, 6, -5, 0) };
        row.Children.Add(SecondaryButton("+ add questions", () => ShowAdd(false)));
        row.Children.Add(SecondaryButton("open questions folder", OpenFolder));
        sp.Children.Add(row);

        SetScreen(sp);
        React("point", 2.2);
        Greet();
    }

    // The class cards on the home screen, for the chosen year, grouped by subject
    void FillClassList(StackPanel list)
    {
        list.Children.Clear();
        homeOrder = new List<string>();
        int yf = stats.YearFilter;
        var names = classes.Keys.Where(c => yf == 0 || YearOf(c) == yf).ToList();
        int n = 1;
        foreach (var grp in names.GroupBy(GroupFor).OrderBy(g => GroupOrder(g.Key)))
        {
            var gl = T(grp.Key, 12, FontWeights.Bold, Gold);
            gl.FontFamily = TitleFont;
            gl.Margin = new Thickness(2, 10, 0, 7);
            list.Children.Add(gl);
            foreach (var name in grp)
            {
                string cls = name;
                homeOrder.Add(cls);
                int got = mastery.LearnedCount(cls, classes[cls]);
                int pctL = classes[cls].Count > 0 ? 100 * got / classes[cls].Count : 0;
                int y = YearOf(cls);
                string sub = (yf == 0 && y > 0 ? "Year " + y + "  ·  " : "") + classes[cls].Count + " questions  ·  " + pctL + "% learned";
                list.Children.Add(Choice(n <= 9 ? (n++).ToString() : "•", cls, sub, () => PickClass(cls), true));
            }
        }
        if (names.Count == 0)
        {
            var none = T("No classes are marked for this year yet. Add a line like \"Year: 2\" near the top of a class file to put it here.", 13, FontWeights.Normal, Muted);
            none.Margin = new Thickness(2, 10, 0, 10);
            list.Children.Add(none);
        }
    }

    // ---------- Step 2: topics and how many ----------
    void ShowTopics()
    {
        mode = "topics";
        StopTalking();
        classLabel.Text = "·  " + currentClass;
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = currentClass, FontSize = 34, FontWeight = FontWeights.Bold, FontFamily = TitleFont, Foreground = TextMain, TextWrapping = TextWrapping.Wrap });

        // How much of this class you've learned (right twice in a row)
        int learned = mastery.LearnedCount(currentClass, Bank_());
        int total = Bank_().Count;
        double frac = total > 0 ? (double)learned / total : 0;
        var lg = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        lg.ColumnDefinitions.Add(new ColumnDefinition());
        lg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lt = T("📘 LEARNED", 13, FontWeights.Bold, Good);
        lt.FontFamily = TitleFont;
        lg.Children.Add(lt);
        var lc = T(learned + " / " + total + "  (" + (int)Math.Round(frac * 100) + "%)", 13, FontWeights.SemiBold, TextMain);
        Grid.SetColumn(lc, 1);
        lg.Children.Add(lc);
        sp.Children.Add(lg);
        var track = new Grid { Height = 10, Margin = new Thickness(0, 6, 0, 0) };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(5), Background = Hex("#1EFFFFFF") });
        var fill = new Border { CornerRadius = new CornerRadius(5), Background = Grad("#7FB58A", "#4E8A5C"), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        track.Children.Add(fill);
        track.SizeChanged += (s, e) => fill.BeginAnimation(WidthProperty, Anim(track.ActualWidth * frac, 700));
        sp.Children.Add(track);
        var lh = T("A question counts as learned once you get it right twice in a row.", 12, FontWeights.Normal, Muted);
        lh.Margin = new Thickness(0, 5, 0, 0);
        sp.Children.Add(lh);

        Btn start = null;
        TextBlock hint = null;
        Action refresh = () =>
        {
            int pool = Bank_().Count(q => selected.Contains(q.Topic));
            start.Opacity = pool > 0 ? 1 : 0.35;
            start.IsHitTestVisible = pool > 0;
            hint.Text = pool > 0
                ? pool + " questions in the pool  ·  1–4 to answer  ·  enter for next"
                : "No questions picked. Choose at least one topic.";
        };

        if (Topics().Any())
        {
            sp.Children.Add(Label("TOPICS"));
            var wrap = new WrapPanel();
            var topicPills = new List<Btn>();
            int topicCount = Topics().Count();
            var allPill = Pill("everything", selected.Count == topicCount);
            Clickable(allPill, () =>
            {
                bool all = selected.Count == topicCount;
                selected.Clear();
                if (!all) foreach (var t in Topics()) selected.Add(t);
                foreach (var p in topicPills) SetPill(p, !all);
                SetPill(allPill, !all);
                refresh();
            });
            wrap.Children.Add(allPill);
            foreach (var topic in Topics())
            {
                string t = topic;
                var p = Pill(t, selected.Contains(t));
                Clickable(p, () =>
                {
                    if (!selected.Remove(t)) selected.Add(t);
                    SetPill(p, selected.Contains(t));
                    SetPill(allPill, selected.Count == topicCount);
                    refresh();
                });
                topicPills.Add(p);
                wrap.Children.Add(p);
            }
            sp.Children.Add(wrap);
        }

        sp.Children.Add(Label("HOW MANY QUESTIONS"));
        var countRow = new WrapPanel();
        var countPills = new Dictionary<Btn, int>();
        foreach (int n in new[] { 5, 10, 20, 0 })
        {
            int value = n;
            var p = Pill(n == 0 ? "all of them" : n.ToString(), count == n);
            Clickable(p, () =>
            {
                count = value;
                foreach (var kv in countPills) SetPill(kv.Key, kv.Value == value);
            });
            countPills[p] = n;
            countRow.Children.Add(p);
        }
        sp.Children.Add(countRow);

        start = BigButton("START", StartNew);
        sp.Children.Add(start);
        var row = new UniformGrid { Columns = 2, Margin = new Thickness(-5, 8, -5, 0) };
        row.Children.Add(SecondaryButton("← different class", ShowHome));
        row.Children.Add(SecondaryButton("+ add questions here", () => ShowAdd(false)));
        sp.Children.Add(row);
        hint = T("", 13, FontWeights.Normal, Muted);
        hint.TextAlignment = TextAlignment.Center;
        hint.Margin = new Thickness(0, 12, 0, 0);
        sp.Children.Add(hint);
        refresh();

        SetScreen(sp);
        React("think", 3);
        Say(Pick(new[] { "Okay, " + currentClass + ".", currentClass + ". Fine.", "Alright, " + currentClass + " it is." }) +
            " Pick the topics you want and how many questions, then hit start.");
    }

    // ---------- Add questions ----------
    void ShowAdd(bool newClass)
    {
        mode = "add";
        StopTalking();
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = newClass ? "New class" : "Add a question", FontSize = 30, FontWeight = FontWeights.Bold, FontFamily = TitleFont, Foreground = TextMain });
        sp.Children.Add(T("It's saved to that class's question file, so you can also edit it later in Notepad.", 14, FontWeights.Normal, Muted));

        sp.Children.Add(Label("CLASS"));
        var classBox = Field(sp, newClass ? "" : currentClass, false);
        if (!newClass && classes.Count > 1)
        {
            var pills = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var name in classes.Keys)
            {
                string cls = name;
                var p = Pill(cls, false);
                Clickable(p, () => classBox.Text = cls);
                pills.Children.Add(p);
            }
            sp.Children.Add(pills);
        }

        sp.Children.Add(Label("TOPIC  ·  like a chapter or unit"));
        var topicBox = Field(sp, "", false);
        sp.Children.Add(Label("QUESTION"));
        var questionBox = Field(sp, "", true);

        sp.Children.Add(Label("ANSWER CHOICES  ·  tap the circle next to the right one"));
        int correct = 0;
        var optionBoxes = new List<TextBox>();
        var markers = new List<Btn>();
        Action paint = () =>
        {
            for (int i = 0; i < markers.Count; i++)
            {
                markers[i].Background = i == correct ? Good : FieldBg;
                markers[i].BorderBrush = i == correct ? Good : CardStroke;
                ((TextBlock)markers[i].Child).Text = i == correct ? "" : "";
            }
        };
        for (int i = 0; i < 4; i++)
        {
            int k = i;
            var g = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var marker = new Btn
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(1.5),
                Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Mark as correct",
                Child = new TextBlock
                {
                    FontFamily = IconFont, FontSize = 12, Foreground = Hex("#160E0C"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            Clickable(marker, () => { correct = k; paint(); });
            markers.Add(marker);
            g.Children.Add(marker);
            var holder = new Grid();
            Grid.SetColumn(holder, 1);
            g.Children.Add(holder);
            optionBoxes.Add(Field(holder, "", false));
            sp.Children.Add(g);
        }
        paint();

        sp.Children.Add(Label("EXPLANATION  ·  what Heather tells you about it"));
        var whyBox = Field(sp, "", true);

        sp.Children.Add(BigButton("SAVE QUESTION", () =>
        {
            string cls = SafeName(classBox.Text);
            var opts = new List<string>();
            int answer = -1;
            for (int i = 0; i < optionBoxes.Count; i++)
            {
                string o = optionBoxes[i].Text.Trim();
                if (o.Length == 0) continue;
                if (i == correct) answer = opts.Count;
                opts.Add(o);
            }
            if (cls.Length == 0) { Say("Which class is this for?", Bad); classBox.Focus(); return; }
            if (questionBox.Text.Trim().Length == 0) { Say("You forgot the actual question.", Bad); questionBox.Focus(); return; }
            if (opts.Count < 2) { Say("Give me at least two answer choices.", Bad); return; }
            if (answer < 0) { Say("The one you marked right is empty.", Bad); return; }

            var q = new Q
            {
                Topic = topicBox.Text.Trim().Length > 0 ? topicBox.Text.Trim() : "General",
                Text = questionBox.Text.Trim(), Options = opts.ToArray(), Answer = answer, Why = whyBox.Text.Trim()
            };
            try
            {
                Directory.CreateDirectory(ClassesDir);
                QuestionFile.Append(System.IO.Path.Combine(ClassesDir, cls + ".txt"), q);
            }
            catch { Say("Couldn't save that. Is the file open somewhere?", Bad); return; }

            bool isNewClass = !classes.ContainsKey(cls);
            currentClass = cls;
            stats.LastClass = cls;
            stats.Save();
            LoadClasses();
            selected.Add(q.Topic);
            if (notesFor != cls) LoadNotes(cls);

            Sfx.Play(Sfx.Correct);
            Nod();
            Say(isNewClass ? "New class: " + cls + ". Let's fill it up. Add another one." : "Got it. That's " + classes[cls].Count + " questions in " + cls + " now. Keep going, or hit done.", Good);
            questionBox.Text = "";
            whyBox.Text = "";
            foreach (var ob in optionBoxes) ob.Text = "";
            correct = 0;
            paint();
            questionBox.Focus();
        }));
        var row = new UniformGrid { Columns = 2, Margin = new Thickness(-5, 8, -5, 0) };
        row.Children.Add(SecondaryButton("done", ShowHome));
        row.Children.Add(SecondaryButton("open questions folder", OpenFolder));
        sp.Children.Add(row);

        SetScreen(sp);
        React("cross", 3);
        Say(newClass
            ? "A new class? Okay. Type its name, then write a question, the answer choices, and tap the circle next to the right one."
            : "Okay, teach me something. Write the question, the answer choices, and tap the circle next to the right one. Then hit save.");
        Dispatcher.BeginInvoke(new Action(() => (newClass ? classBox : questionBox).Focus()), DispatcherPriority.Loaded);
    }

    // ---------- Quiz ----------
    // Questions you haven't learned yet come first, so practice goes where it's needed;
    // the picked set is shuffled so learned ones don't always land at the end
    void StartNew()
    {
        var pool = Bank_().Where(q => selected.Contains(q.Topic)).ToList();
        if (pool.Count == 0) return;
        var fresh = Shuffle(pool.Where(q => !mastery.IsLearned(currentClass, q)));
        var known = Shuffle(pool.Where(q => mastery.IsLearned(currentClass, q)));
        var ordered = fresh.Concat(known).ToList();
        StartQuiz(Shuffle(count == 0 ? ordered : ordered.Take(count)));
    }

    void StartQuiz(List<Q> source)
    {
        quizClass = currentClass;
        quiz = source.Select(q =>
        {
            var order = Shuffle(Enumerable.Range(0, q.Options.Length));
            return new Q
            {
                Topic = q.Topic, Text = q.Text, Why = q.Why, Seconds = Timing.For(currentClass, q),
                Options = order.Select(i => q.Options[i]).ToArray(),
                Answer = order.IndexOf(q.Answer)
            };
        }).ToList();
        idx = 0; score = 0; runBest = 0; sessionXp = 0; sessionLearned = 0;
        firstLead = Pick(new[] { "Okay, focus. ", "First one. ", "Here we go. " });
        levelAtStart = Level(stats.Xp);
        missed = new List<Missed>();
        history = new List<bool>();
        ShowQuestion();
    }

    void ShowQuestion()
    {
        mode = "question";
        answered = false;
        ScratchNewQuestion();
        var q = quiz[idx];
        var sp = new StackPanel();

        var segs = new UniformGrid { Rows = 1, Columns = quiz.Count, Height = 6, Margin = new Thickness(-2, 0, -2, 16) };
        for (int i = 0; i < quiz.Count; i++)
        {
            segs.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(3), Margin = new Thickness(2, 0, 2, 0),
                Background = i < idx ? (history[i] ? Good : Bad) : i == idx ? Hex("#AAFFFFFF") : Hex("#1EFFFFFF")
            });
        }
        sp.Children.Add(segs);

        var meta = new Grid();
        meta.ColumnDefinitions.Add(new ColumnDefinition());
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        meta.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8), Background = Hex("#33B3201B"), Padding = new Thickness(10, 4, 10, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = T(q.Topic.ToUpper(), 12, FontWeights.Bold, Hex("#E08A7A"))
        });
        var counter = T("QUESTION " + (idx + 1) + " / " + quiz.Count, 13, FontWeights.SemiBold, Muted);
        counter.FontFamily = TitleFont;
        counter.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(counter, 1);
        meta.Children.Add(counter);
        sp.Children.Add(meta);

        questionSecs = q.Seconds > 0 ? q.Seconds : TimerSecs;
        var timer = new Grid
        {
            Margin = new Thickness(0, 14, 0, 0),
            ToolTip = "Answer within " + Timing.Clock(questionSecs) + " for bonus XP (time is set by how much work the question takes)"
        };
        timer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        timer.ColumnDefinitions.Add(new ColumnDefinition());
        timer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        timerLabel = T(Timing.Clock(questionSecs), 12, FontWeights.SemiBold, Gold);
        timerLabel.Margin = new Thickness(10, 0, 0, 0);
        timerLabel.MinWidth = 34;
        timerLabel.TextAlignment = TextAlignment.Right;
        timerLabel.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(timerLabel, 2);
        timer.Children.Add(timerLabel);
        timer.Children.Add(new TextBlock
        {
            Text = "", FontFamily = IconFont, FontSize = 12, Foreground = Gold,
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
        });
        var track = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(track, 1);
        track.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = Hex("#1EFFFFFF") });
        timerScale = new ScaleTransform(1, 1);
        track.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(2), Background = Grad("#E8C77A", "#B8860B"),
            RenderTransformOrigin = new Point(0, 0.5), RenderTransform = timerScale
        });
        timer.Children.Add(track);
        sp.Children.Add(timer);

        // The question, plus any code that comes with it in a monospace box
        string prompt, code;
        SplitCode(q.Text, out prompt, out code);
        var ql = T(prompt, 21, FontWeights.SemiBold, TextMain);
        ql.Margin = new Thickness(0, 16, 0, code == null ? 18 : 10);
        sp.Children.Add(ql);
        if (code != null) sp.Children.Add(CodeBox(code));

        answerCards = new List<Border>();
        for (int i = 0; i < q.Options.Length; i++)
        {
            int k = i;
            var card = Choice((i + 1).ToString(), q.Options[i], null, () => Choose(k));
            answerCards.Add(card);
            sp.Children.Add(card);
        }

        verdictBanner = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 10, 16, 11), Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1)
        };
        verdictText = T("", 15, FontWeights.Bold, Good);
        verdictBanner.Child = verdictText;
        sp.Children.Add(verdictBanner);

        var nr = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        nr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        nr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var nextBtn = BigButton(idx + 1 < quiz.Count ? "NEXT  →" : "SEE RESULTS", Next);
        nextBtn.Margin = new Thickness(0, 12, 6, 0);
        nr.Children.Add(nextBtn);
        Btn noteBtn = null;
        noteBtn = SecondaryButton("+ add to notes", () => AddToNotes(q, noteBtn));
        noteBtn.Margin = new Thickness(6, 12, 0, 0);
        Grid.SetColumn(noteBtn, 1);
        nr.Children.Add(noteBtn);
        nextRow = new Border { Child = nr, Visibility = Visibility.Collapsed };
        sp.Children.Add(nextRow);

        SetScreen(sp);
        // The speed-bonus timer starts once she's done reading the question out loud
        sw.Reset();
        timerStarted = false;
        timerPending = true;

        // She asks the question, pointing at it
        React("point", 2.4);
        Say(LeadFor(idx) + SpokenQuestion(q));
    }

    string firstLead = "";
    bool timerPending, timerStarted;
    double questionSecs = TimerSecs;
    TextBlock timerLabel;
    DispatcherTimer clockTick;

    void StopClock()
    {
        if (clockTick != null) clockTick.Stop();
    }

    string LeadFor(int i)
    {
        return i == 0 ? firstLead : i + 1 == quiz.Count ? "Last one. " : "";
    }

    void StartSpeedTimer()
    {
        if (!timerPending || answered || mode != "question" || timerScale == null) return;
        timerPending = false;
        timerStarted = true;
        sw.Restart();
        var drain = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(questionSecs));
        Timeline.SetDesiredFrameRate(drain, 30);
        timerScale.BeginAnimation(ScaleTransform.ScaleXProperty, drain);
        if (clockTick == null)
        {
            clockTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            clockTick.Tick += (s, e) =>
            {
                if (timerLabel == null || answered || mode != "question") { StopClock(); return; }
                double left = questionSecs - sw.Elapsed.TotalSeconds;
                timerLabel.Text = left > 0 ? Timing.Clock(left) : "0s";
                timerLabel.Foreground = left > 0 ? Gold : Muted;
                if (left <= 0) StopClock();
            };
        }
        clockTick.Start();
    }

    void Mark(Border card, Brush tint, Brush stroke, string glyph)
    {
        card.Background = tint;
        card.BorderBrush = stroke;
        var badge = (Border)((Grid)card.Child).Children[0];
        badge.Background = stroke;
        var tb = (TextBlock)badge.Child;
        tb.Text = glyph;
        tb.FontFamily = IconFont;
        tb.FontSize = 13;
        tb.Foreground = Hex("#160E0C");
    }

    void Choose(int i)
    {
        if (answered) return;
        answered = true;
        var q = quiz[idx];
        // Answering before she even finishes reading counts as fast
        bool fast = !timerStarted || sw.Elapsed.TotalSeconds < questionSecs;
        timerPending = false;
        StopClock();
        double left = timerScale.ScaleX;
        timerScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        timerScale.ScaleX = left;

        foreach (var c in answerCards) { c.IsHitTestVisible = false; c.Cursor = Cursors.Arrow; }
        var right = answerCards[q.Answer];
        Mark(right, GoodTint, Good, "");
        for (int k = 0; k < answerCards.Count; k++)
            if (k != q.Answer && k != i) answerCards[k].BeginAnimation(OpacityProperty, Anim(0.35, 250));

        stats.Answered++;
        bool nowLearned = mastery.Record(quizClass, q, i == q.Answer);
        if (nowLearned) sessionLearned++;
        string spoken, clip;
        int quipLen;
        if (i == q.Answer)
        {
            score++;
            streak++;
            stats.Correct++;
            runBest = Math.Max(runBest, streak);
            stats.BestStreak = Math.Max(stats.BestStreak, streak);
            int xp = 10 + Math.Min(streak - 1, 5) * 2 + (fast ? 5 : 0);
            int before = Level(stats.Xp);
            stats.Xp += xp;
            sessionXp += xp;
            history.Add(true);

            Pulse(right, 1, 1.04, 140);
            Pulse(streakText, 1.6, 1, 380);
            FloatText("+" + xp + " XP", Gold, right);
            React("fist", 1.6);
            verdictBanner.Background = GoodTint;
            verdictBanner.BorderBrush = Hex("#557FB58A");
            verdictText.Foreground = Good;
            verdictText.Text = "✓ CORRECT   +" + xp + " XP" + (fast ? "   ⚡ speedy" : "") + (streak >= 3 ? "   🔥 x" + streak : "") + (nowLearned ? "   📘 LEARNED" : "");
            string praise = streak >= 3 ? Pick(Hot) : Pick(Right);
            bool leveled = Level(stats.Xp) > before;
            if (leveled) praise = "Level up. You're a " + TitleFor(Level(stats.Xp)) + " now. Congrats, I guess. " + praise;
            spoken = (praise + " " + q.Why).Trim();
            clip = leveled ? "levelup" : streak >= 3 ? "streak" : "correct";
            quipLen = praise.Length;
            if (leveled) { Sfx.Play(Sfx.LevelUp); Cheer(); }
            else
            {
                Sfx.Play(streak >= 3 ? Sfx.Combo : Sfx.Correct);
                if (streak >= 3) Hop(); else Nod();
            }
            Flash(leveled ? Gold : Good);
        }
        else
        {
            Mark(answerCards[i], BadTint, Bad, "");
            int lost = streak;
            streak = 0;
            missed.Add(new Missed { Question = q, Yours = q.Options[i] });
            history.Add(false);

            Shake();
            HeadShake();
            React("facepalm", 2.2);
            Sfx.Play(Sfx.Wrong);
            verdictBanner.Background = BadTint;
            verdictBanner.BorderBrush = Hex("#55C8433A");
            verdictText.Foreground = Bad;
            verdictText.Text = "✗ THE ANSWER IS: " + q.Options[q.Answer].ToUpper();
            string reaction = (lost >= 3 ? Pick(LostStreak) + " " : "") + Pick(Wrong);
            spoken = (reaction + " The answer is " + q.Options[q.Answer] + ". " + q.Why).Trim();
            clip = "wrong";
            quipLen = reaction.Length;
            Flash(Bad);
        }
        stats.Save();
        UpdateHud(true);

        verdictBanner.Visibility = Visibility.Visible;
        Pulse(verdictBanner, 0.9, 1, 300);
        nextRow.Visibility = Visibility.Visible;
        nextRow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
        Dispatcher.BeginInvoke(new Action(() => nextRow.BringIntoView()), DispatcherPriority.Loaded);

        // Her reaction (a real clip if you added some), then she explains it out loud
        Say(spoken, null, true, clip, quipLen);
        // Get the next question's voice ready while she explains this one
        if (idx + 1 < quiz.Count) PrefetchSpeech(LeadFor(idx + 1) + SpokenQuestion(quiz[idx + 1]));
    }

    void Next()
    {
        StopTalking();
        idx++;
        if (idx < quiz.Count) ShowQuestion(); else ShowResults();
    }

    // ---------- Results ----------
    void ShowResults()
    {
        mode = "results";
        int n = quiz.Count;
        int pct = (int)Math.Round(100.0 * score / n);
        string grade, verdict, a, b;
        if (pct == 100) { grade = "S"; a = "#FDE68A"; b = "#F59E0B"; verdict = "Perfect. Okay, fine. I'm impressed."; }
        else if (pct >= 85) { grade = "A"; a = "#6EE7B7"; b = "#10B981"; verdict = "Nice work. You actually listened."; }
        else if (pct >= 70) { grade = "B"; a = "#93C5FD"; b = "#6366F1"; verdict = "Pretty solid. Don't let it go to your head."; }
        else if (pct >= 55) { grade = "C"; a = "#FDE047"; b = "#EAB308"; verdict = "Could be worse. Could be a lot better."; }
        else if (pct >= 40) { grade = "D"; a = "#FDBA74"; b = "#F97316"; verdict = "Rough. Read what you missed, okay?"; }
        else { grade = "F"; a = "#FDA4AF"; b = "#E11D48"; verdict = "Yikes. We're going over this again."; }

        var sp = new StackPanel();
        var gradeTb = new TextBlock
        {
            Text = grade, FontSize = 120, FontWeight = FontWeights.Bold, FontFamily = TitleFont, Foreground = Grad(a, b),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -10, 0, -14),
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0, 0),
            Effect = new DropShadowEffect { Color = C(b), BlurRadius = 34, ShadowDepth = 0, Opacity = 0.55 }
        };
        sp.Children.Add(gradeTb);
        var line = T(score + " / " + n + " correct  ·  +" + sessionXp + " XP  ·  best streak " + runBest, 16, FontWeights.Normal, Muted);
        line.TextAlignment = TextAlignment.Center;
        sp.Children.Add(line);
        int learnedNow = mastery.LearnedCount(quizClass, classes.ContainsKey(quizClass) ? classes[quizClass] : new List<Q>());
        int classTotal = classes.ContainsKey(quizClass) ? classes[quizClass].Count : 0;
        var learnedLine = T("📘  +" + sessionLearned + " newly learned  ·  " + learnedNow + " / " + classTotal + " learned in " + quizClass, 15, FontWeights.SemiBold, Good);
        learnedLine.TextAlignment = TextAlignment.Center;
        learnedLine.Margin = new Thickness(0, 6, 0, 0);
        sp.Children.Add(learnedLine);

        int lvl = Level(stats.Xp);
        if (lvl > levelAtStart)
        {
            var banner = new Border
            {
                CornerRadius = new CornerRadius(14), Background = Grad("#E8C77A", "#B8860B"),
                Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 18, 0, 0),
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
                Child = new TextBlock
                {
                    Text = "LEVEL UP  →  LV " + lvl + " · " + TitleFor(lvl), FontSize = 17, FontWeight = FontWeights.Bold, FontFamily = TitleFont,
                    Foreground = Hex("#2A1A00"), HorizontalAlignment = HorizontalAlignment.Center
                }
            };
            sp.Children.Add(banner);
            var st = (ScaleTransform)banner.RenderTransform;
            var pulse = new DoubleAnimation(1, 1.04, TimeSpan.FromMilliseconds(500)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(4) };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        }

        sp.Children.Add(BigButton("PLAY AGAIN", StartNew));
        var row = new UniformGrid { Columns = missed.Count > 0 ? 3 : 2, Margin = new Thickness(-5, 8, -5, 0) };
        if (missed.Count > 0)
        {
            var wrong = missed.Select(m => m.Question).ToList();
            row.Children.Add(SecondaryButton("retry missed (" + missed.Count + ")", () => StartQuiz(Shuffle(wrong))));
        }
        row.Children.Add(SecondaryButton("change topics", ShowTopics));
        row.Children.Add(SecondaryButton("home", ShowHome));
        sp.Children.Add(row);

        // How each topic went this round, weakest first
        var byTopic = quiz.Select((q, i) => new { q.Topic, Right = i < history.Count && history[i] })
            .GroupBy(x => x.Topic)
            .Select(g => new { Topic = g.Key, Right = g.Count(x => x.Right), Total = g.Count() })
            .OrderBy(x => (double)x.Right / x.Total).ThenBy(x => x.Topic).ToList();
        if (byTopic.Count > 1)
        {
            sp.Children.Add(Label("BY TOPIC"));
            foreach (var t in byTopic)
            {
                double ratio = (double)t.Right / t.Total;
                var color = ratio >= 0.8 ? Good : ratio >= 0.5 ? Gold : Bad;
                var tg = new Grid { Margin = new Thickness(2, 0, 2, 4) };
                tg.ColumnDefinitions.Add(new ColumnDefinition());
                tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                tg.Children.Add(T(t.Topic, 14, FontWeights.SemiBold, TextMain));
                var tc = T(t.Right + " / " + t.Total, 14, FontWeights.Bold, color);
                Grid.SetColumn(tc, 1);
                tg.Children.Add(tc);
                sp.Children.Add(tg);
                var bar = new Grid { Height = 5, Margin = new Thickness(2, 0, 2, 10) };
                bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = Hex("#1EFFFFFF") });
                var fillBar = new Border { CornerRadius = new CornerRadius(3), Background = color, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
                bar.Children.Add(fillBar);
                bar.SizeChanged += (s, e) => fillBar.Width = bar.ActualWidth * ratio;
                sp.Children.Add(bar);
            }
        }

        if (missed.Count > 0)
        {
            sp.Children.Add(Label("WHAT YOU MISSED"));
            foreach (var m in missed)
            {
                var ms = new StackPanel();
                string mPrompt, mCode;
                SplitCode(m.Question.Text, out mPrompt, out mCode);
                ms.Children.Add(T(mPrompt, 15, FontWeights.SemiBold, TextMain));
                if (mCode != null)
                {
                    var cb = CodeBox(mCode);
                    cb.Margin = new Thickness(0, 8, 0, 0);
                    ((TextBlock)cb.Child).FontSize = 13;
                    ((TextBlock)cb.Child).LineHeight = 19;
                    ms.Children.Add(cb);
                }
                var you = T("you: " + m.Yours, 14, FontWeights.Normal, Bad);
                you.Margin = new Thickness(0, 7, 0, 0);
                ms.Children.Add(you);
                ms.Children.Add(T("answer: " + m.Question.Options[m.Question.Answer], 14, FontWeights.Normal, Good));
                var why = T(m.Question.Why, 13.5, FontWeights.Normal, Muted);
                why.Margin = new Thickness(0, 6, 0, 0);
                ms.Children.Add(why);
                sp.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(14), Background = CardBg, BorderBrush = CardStroke,
                    BorderThickness = new Thickness(1), Padding = new Thickness(16, 13, 16, 14),
                    Margin = new Thickness(0, 0, 0, 10), Child = ms
                });
            }
        }

        SetScreen(sp);
        var popIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(800))
        {
            BeginTime = TimeSpan.FromMilliseconds(150),
            EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut }
        };
        ((ScaleTransform)gradeTb.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, popIn);
        ((ScaleTransform)gradeTb.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, popIn);

        if (pct >= 80) { Ash(); Sfx.Play(Sfx.LevelUp); Cheer(); }
        else if (pct < 50) { HeadShake(); React("facepalm", 2); }
        else React("hips", 3);
        string next = missed.Count > 0
            ? " Look over what you missed, then play again or retry the ones you got wrong."
            : " Play again, or pick something else.";
        Say("You got " + score + " out of " + n + ". " + verdict + next, pct >= 70 ? Gold : null, true, pct >= 70 ? "results_good" : "results_bad", 0);
    }

    // ---------- Effects ----------
    void Shake()
    {
        var tt = (TranslateTransform)scroller.RenderTransform;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        double[] xs = { 0, -12, 11, -9, 6, -3, 1, 0 };
        for (int k = 0; k < xs.Length; k++)
            a.KeyFrames.Add(new LinearDoubleKeyFrame(xs[k], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(k * 60))));
        tt.BeginAnimation(TranslateTransform.XProperty, a);
    }

    void FloatText(string text, Brush color, FrameworkElement anchor)
    {
        var p = anchor.TranslatePoint(new Point(anchor.ActualWidth - 130, 4), fx);
        var tb = new TextBlock
        {
            Text = text, FontSize = 22, FontWeight = FontWeights.Black, Foreground = color,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.6 }
        };
        Canvas.SetLeft(tb, p.X);
        Canvas.SetTop(tb, p.Y);
        fx.Children.Add(tb);
        tb.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(p.Y, p.Y - 56, TimeSpan.FromMilliseconds(1000))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500)) { BeginTime = TimeSpan.FromMilliseconds(600) };
        fade.Completed += (s, e) => fx.Children.Remove(tb);
        tb.BeginAnimation(OpacityProperty, fade);
    }

    // Ash drifting down, Silent Hill style
    void Ash()
    {
        double w = ActualWidth, h = ActualHeight;
        string[] colors = { "#9E9E9E", "#6B6B6B", "#BDB6AE", "#7A3B2E", "#B3201B", "#4A4A4A" };
        for (int k = 0; k < 140; k++)
        {
            var rot = new RotateTransform();
            var r = new Rectangle
            {
                Width = rng.Next(4, 8), Height = rng.Next(4, 9), RadiusX = 1.5, RadiusY = 1.5,
                Fill = Hex(colors[rng.Next(colors.Length)]), Opacity = 0.85,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = rot
            };
            double x = rng.NextDouble() * w;
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, -20);
            fx.Children.Add(r);
            var dur = TimeSpan.FromSeconds(3 + rng.NextDouble() * 3);
            var delay = TimeSpan.FromSeconds(rng.NextDouble() * 1.5);
            var fall = new DoubleAnimation(-20, h + 20, dur) { BeginTime = delay };
            fall.Completed += (s, e) => fx.Children.Remove(r);
            r.BeginAnimation(Canvas.TopProperty, fall);
            r.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(x, x + rng.Next(-120, 120), dur) { BeginTime = delay });
            rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, rng.Next(180, 720) * (rng.Next(2) == 0 ? 1 : -1), dur) { BeginTime = delay });
        }
    }

    // ---------- Window controls ----------
    void SetupTray()
    {
        tray = new WF.NotifyIcon();
        try { tray.Icon = SD.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); }
        catch { tray.Icon = SD.SystemIcons.Application; }
        tray.Text = "quizzahh";
        tray.Visible = true;
        tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) ToggleWidget(); };
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("Open quiz", null, (s, e) => ShowWidget());
        menu.Items.Add("Open questions folder", null, (s, e) => OpenFolder());
        // Shown only while she's hiding (from her right-click menu)
        var back = new WF.ToolStripMenuItem("Bring Heather back");
        back.Click += (s, e) => { if (buddy != null) buddy.Unsnooze(); };
        menu.Items.Add(back);
        var hide = new WF.ToolStripMenuItem("Hide Heather for 1 hour");
        hide.Click += (s, e) => { if (buddy != null) buddy.Snooze(60); };
        menu.Items.Add(hide);
        menu.Opening += (s, e) =>
        {
            bool snoozed = buddy != null && buddy.IsSnoozed;
            back.Visible = snoozed;
            hide.Visible = buddy != null && !snoozed && !stats.BuddyOff;
        };
        menu.Items.Add("Taskbar Heather on/off", null, (s, e) => ToggleBuddy());

        // Pick which installed voice reads the explanations
        var voiceMenu = new WF.ToolStripMenuItem("Heather's voice");
        voiceMenu.DropDownItems.Add("(loading)");
        voiceMenu.DropDownOpening += (s, e) =>
        {
            voiceMenu.DropDownItems.Clear();
            if (KokoroReady)
            {
                foreach (var kv in KokoroVoices)
                {
                    string key = "kokoro:" + kv[0];
                    var ki = new WF.ToolStripMenuItem("Kokoro: " + kv[1] + (kv[1] == "nicole" ? "  (recommended)" : ""))
                    {
                        Checked = kokoroSid.ToString() == kv[0]
                    };
                    ki.Click += (s2, e2) =>
                    {
                        UseVoice(key);
                        stats.VoiceName = key;
                        stats.Save();
                        if (IsVisible) Say("How's this voice?");
                        else Talk(dialogText, "How's this voice?", true);
                    };
                    voiceMenu.DropDownItems.Add(ki);
                }
                voiceMenu.DropDownItems.Add(new WF.ToolStripSeparator());
            }
            foreach (var model in PiperModels())
            {
                string key = "piper:" + System.IO.Path.GetFileName(model);
                var pi = new WF.ToolStripMenuItem(PiperLabel(model)) { Checked = piperModel == model };
                pi.Click += (s2, e2) =>
                {
                    UseVoice(key);
                    stats.VoiceName = key;
                    stats.Save();
                    if (IsVisible) Say("How's this voice?");
                    else Talk(dialogText, "How's this voice?", true);
                };
                voiceMenu.DropDownItems.Add(pi);
            }
            if (voiceMenu.DropDownItems.Count > 0) voiceMenu.DropDownItems.Add(new WF.ToolStripSeparator());
            if (voice == null) { voiceMenu.DropDownItems.Add("No voices found"); return; }
            foreach (var v in InstalledVoices(voice))
            {
                string name = v.Name;
                var item = new WF.ToolStripMenuItem(name) { Checked = voice.Voice.Name == name };
                item.Click += (s2, e2) =>
                {
                    bool ok = UseVoice(name);
                    if (ok) { stats.VoiceName = name; stats.Save(); }
                    string line = ok ? "How's this voice?" : "That voice isn't working on this PC right now, so I'm sticking with this one.";
                    if (IsVisible) Say(line);
                    else { voice.SpeakAsyncCancelAll(); voice.SpeakAsync(line); }
                };
                voiceMenu.DropDownItems.Add(item);
            }
        };
        menu.Items.Add(voiceMenu);
        menu.Items.Add("Open voice clips folder", null, (s, e) =>
        {
            EnsureVoiceFolders();
            try { Process.Start("explorer.exe", "\"" + VoiceDir + "\""); } catch { }
        });
        menu.Items.Add("Quit", null, (s, e) => Quit());
        tray.ContextMenuStrip = menu;
    }

    public void ShowWidget()
    {
        bool wasHidden = !IsVisible;
        // The quiz floats over everything while it's open: the window is see-through, so a click on an
        // empty spot reaches the app behind it, and without this that app would cover the quiz.
        Topmost = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (buddy != null) { buddy.Suppressed = true; buddy.Refresh(); }
        if (wasHidden && mode == "home") Greet();
    }

    // Starts hidden (e.g. at Windows startup): only taskbar Heather shows
    public void StartInBackground()
    {
        if (buddy != null) { buddy.Suppressed = false; buddy.Refresh(); buddy.Say("I'll be right here."); }
        else ShowWidget();
    }

    void Quit()
    {
        quitting = true;
        Close();
    }

    void ToggleWidget()
    {
        if (IsVisible) HideWidget(); else ShowWidget();
    }

    void HideWidget()
    {
        StopTalking();
        SaveNotes();
        SaveScratch();
        Hide();
        if (buddy != null && !buddy.Disabled)
        {
            buddy.Suppressed = false;
            buddy.Refresh();
            if (!buddy.IsSnoozed) buddy.Say(Pick(new[] { "I'll be down here.", "Come back soon, okay?", "Break time. Don't go far." }));
        }
        else if (!trayTipShown && tray != null)
        {
            trayTipShown = true;
            tray.ShowBalloonTip(2500, "quizzahh", "I'll be in the tray. Click me when you're ready.", WF.ToolTipIcon.None);
        }
    }

    void ToggleBuddy()
    {
        stats.BuddyOff = !stats.BuddyOff;
        stats.Save();
        if (buddy == null) return;
        buddy.Disabled = stats.BuddyOff;
        if (!stats.BuddyOff) buddy.Unsnooze();
        buddy.Refresh();
    }

    void ToggleMute()
    {
        stats.Muted = !stats.Muted;
        Sfx.Muted = stats.Muted;
        muteGlyph.Text = stats.Muted ? "" : "";
        stats.Save();
    }

    void ToggleVoice()
    {
        stats.Quiet = !stats.Quiet;
        voiceGlyph.Opacity = stats.Quiet ? 0.35 : 1;
        stats.Save();
        if (stats.Quiet) StopTalking();
        Say(stats.Quiet ? "Fine. I'll keep quiet." : "Okay, I'll talk you through it.", null, !stats.Quiet);
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox)
        {
            if (e.Key == Key.Escape) { Keyboard.ClearFocus(); scroller.Focus(); e.Handled = true; }
            return;
        }
        if (CodeKey(e)) { e.Handled = true; return; }
        if (codeOpen) return;
        if (ScratchKey(e)) { e.Handled = true; return; }
        if (e.Key == Key.Escape) { HideWidget(); return; }
        int n = -1;
        if (e.Key >= Key.D1 && e.Key <= Key.D9) n = e.Key - Key.D1;
        else if (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9) n = e.Key - Key.NumPad1;
        if (mode == "question")
        {
            if (!answered)
            {
                if (n >= 0 && n < answerCards.Count) { Choose(n); e.Handled = true; }
            }
            else if (e.Key == Key.Enter || e.Key == Key.Space) { e.Handled = true; Next(); }
        }
        else if (mode == "home" && n >= 0 && n < homeOrder.Count)
        {
            e.Handled = true;
            PickClass(homeOrder[n]);
        }
        else if (e.Key == Key.Enter && (mode == "topics" || mode == "results"))
        {
            e.Handled = true;
            StartNew();
        }
    }

    // ---------- Debug snapshots (run with --snap <folder>) ----------
    void RunSnapshot()
    {
        var steps = new List<Action>
        {
            () => Snap("home.png"),
            () => PickClass(currentClass),
            () => Snap("topics.png"),
            () => { count = 5; StartNew(); },
            () => Snap("question.png"),
            () => { OpenScratch(); DemoScratch(); },
            () => Snap("scratch.png"),
            () => CloseScratch(),
            () => OpenCodeLab(),
            () => Snap("codelab.png"),
            () => { if (codeProblems.Count > 0) { OpenProblem(codeProblems[0]); RunCodeTests(true); } },
            () => Snap("codelab-problem.png"),
            () => CloseCodeLab(),
            () => Choose(quiz[0].Answer == 0 ? 1 : 0),
            () => Snap("wrong.png"),
            () => { idx = quiz.Count - 1; ShowResults(); },
            () => Snap("results.png"),
            () => ShowAdd(false),
            () => Snap("add.png"),
            () => { RunSelfTest(); quitting = true; Close(); }
        };
        int step = 0;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        t.Tick += (s, e) => { if (step < steps.Count) steps[step++](); else t.Stop(); };
        t.Start();
    }

    // Plays every class start to finish (right and wrong answers, retries, every screen) and
    // checks every question and every line of speech. Writes selftest.txt next to the snapshots.
    void RunSelfTest()
    {
        var log = new StringBuilder(); var timingLog = new StringBuilder();
        int problems = 0;
        Action<string> bad = msg => { problems++; log.AppendLine("PROBLEM: " + msg); };
        var oddSymbols = new SortedSet<char>();
        int totalQuestions = 0;
        try
        {
            LoadClasses();
            log.AppendLine("Classes: " + classes.Count);
            foreach (var kv in classes.ToList())
            {
                var list = kv.Value;
                totalQuestions += list.Count;
                var times = list.Select(q => Timing.For(kv.Key, q)).OrderBy(t => t).ToList();
                log.AppendLine("  " + kv.Key + ": " + list.Count + " questions, " + list.Select(q => q.Topic).Distinct().Count() + " topics, time "
                    + (times.Count > 0 ? times[0] + "-" + times[times.Count - 1] + "s (median " + times[times.Count / 2] + "s)" : "-"));
                timingLog.AppendLine("=== " + kv.Key);
                foreach (var q in list) timingLog.AppendLine(Timing.For(kv.Key, q).ToString().PadLeft(4) + "  " + q.Text.Replace("\n", " / "));
                if (list.Count == 0) bad(kv.Key + ": has no questions");
                foreach (var dup in list.GroupBy(q => q.Text.Trim()).Where(g => g.Count() > 1)) bad(kv.Key + ": duplicate question: " + dup.Key);
                foreach (var q in list)
                {
                    if (q.Options.Length < 2) bad(kv.Key + ": fewer than 2 choices: " + q.Text);
                    if (q.Answer < 0 || q.Answer >= q.Options.Length) bad(kv.Key + ": bad answer index: " + q.Text);
                    if (q.Options.Select(o => o.Trim().ToLowerInvariant()).Distinct().Count() != q.Options.Length) bad(kv.Key + ": two identical choices: " + q.Text);
                    if (string.IsNullOrWhiteSpace(q.Why)) bad(kv.Key + ": no explanation: " + q.Text);
                    if (Mastery.Key(kv.Key, q).IndexOfAny(new[] { '\n', '\r', '\t' }) >= 0) bad(kv.Key + ": progress for this question can't be saved: " + q.Text);
                    foreach (var s in new[] { q.Text, q.Why }.Concat(q.Options))
                    {
                        string spoken = Ascii(Speakable(s));
                        if (s.Trim().Length > 0 && Chunks(spoken).Count == 0) bad(kv.Key + ": nothing to say for: " + s);
                        foreach (char ch in Speakable(s)) if (ch > 127 && !char.IsLetter(ch)) oddSymbols.Add(ch);
                    }
                }

                // Play the whole class: mostly right answers, every third one wrong
                currentClass = kv.Key;
                selected = new HashSet<string>(Topics());
                count = 0;
                PickClass(kv.Key);
                StartNew();
                int guard = 0;
                bool noted = false;
                while (mode == "question" && guard++ < 5000)
                {
                    var q = quiz[idx];
                    Choose(guard % 3 == 0 ? (q.Answer + 1) % q.Options.Length : q.Answer);
                    if (!noted) { AddToNotes(q, SecondaryButton("test", () => { })); noted = true; }
                    Next();
                }
                if (mode != "results") bad(kv.Key + ": the quiz never reached the results screen");
                if (score != quiz.Count - missed.Count) bad(kv.Key + ": score doesn't match the missed list");
                if (missed.Count > 0)
                {
                    StartQuiz(Shuffle(missed.Select(m => m.Question)));
                    guard = 0;
                    while (mode == "question" && guard++ < 5000) { Choose(quiz[idx].Answer); Next(); }
                    if (mode != "results") bad(kv.Key + ": retrying missed questions never finished");
                    if (missed.Count != 0) bad(kv.Key + ": all-correct retry still has misses");
                }
            }
            ShowHome();
            ShowAdd(true);
            ShowAdd(false);
            SetNotesOpen(true);
            SetNotesOpen(false);
            ShowHome();

            // Scratch paper: drawing, undo/redo, clear, a fresh page per question, flipping back, shapes
            OpenScratch();
            int pagesBefore = pages.Count;
            ink.Strokes.Clear();
            var stroke = new System.Windows.Ink.Stroke(new System.Windows.Input.StylusPointCollection(new[] { new System.Windows.Input.StylusPoint(10, 10), new System.Windows.Input.StylusPoint(60, 40) }));
            ink.Strokes.Add(stroke);
            if (ink.Strokes.Count != 1) bad("scratch: a stroke didn't stick");
            ScratchUndo();
            if (ink.Strokes.Count != 0) bad("scratch: undo didn't remove the stroke");
            ScratchRedo();
            if (ink.Strokes.Count != 1) bad("scratch: redo didn't bring the stroke back");
            ScratchClear();
            if (ink.Strokes.Count != 0) bad("scratch: clear didn't clear the page");
            ScratchUndo();
            if (ink.Strokes.Count != 1) bad("scratch: undo after clear didn't restore the page");
            ScratchNewQuestion();
            if (ink.Strokes.Count != 0 || pages.Count != pagesBefore + 1) bad("scratch: a new question didn't get a fresh page");
            GoPage(pageIdx - 1);
            if (ink.Strokes.Count != 1) bad("scratch: flipping back lost the drawing");
            if (pages.Count != pagesBefore) bad("scratch: an empty page was left behind");
            foreach (var tool in new[] { "line", "box", "circle" })
            {
                SetScratchTool(tool);
                var pts = ShapePoints(new Point(5, 5), new Point(45, 25), true);
                if (pts.Count < 2) bad("scratch: the " + tool + " tool makes no points");
            }
            foreach (var tool in new[] { "pen", "marker", "eraser", "select" }) SetScratchTool(tool);
            SetScratchTool("pen");
            CyclePaper(); CyclePaper(); CyclePaper();
            ink.Strokes.Clear();
            CloseScratch();
            SelfTestCode(log, bad);
            foreach (var line in Greetings.Concat(Right).Concat(Hot).Concat(Wrong).Concat(LostStreak))
                if (Chunks(Ascii(Speakable(line))).Count == 0) bad("nothing to say for Heather's line: " + line);
        }
        catch (Exception ex) { bad("CRASH: " + ex); }
        log.AppendLine("Total questions: " + totalQuestions);
        log.AppendLine("Symbols the voice skips: " + (oddSymbols.Count == 0 ? "none" : string.Join(" ", oddSymbols)));
        log.Insert(0, "Self-test: " + (problems == 0 ? "PASSED" : problems + " problem(s)") + "\r\n");
        File.WriteAllText(System.IO.Path.Combine(snapDir, "selftest.txt"), log.ToString(), Encoding.UTF8);
        File.WriteAllText(System.IO.Path.Combine(snapDir, "timing.txt"), timingLog.ToString(), Encoding.UTF8);
    }

    // The window is see-through, so snapshots get a stand-in desktop behind them
    void Snap(string file)
    {
        var old = outer.Background;
        outer.Background = new LinearGradientBrush(C("#1F3A5F"), C("#0B1220"), new Point(0, 0), new Point(1, 1));
        outer.UpdateLayout();
        Render(outer, file, 0.5);
        outer.Background = old;
    }

    void Render(FrameworkElement el, string file, double scale)
    {
        int w = (int)(el.ActualWidth * scale), h = (int)(el.ActualHeight * scale);
        var rtb = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var f = File.Create(System.IO.Path.Combine(snapDir, file))) enc.Save(f);
    }
}
