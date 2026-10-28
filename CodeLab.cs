using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

// One coding exercise: a longer problem you solve by writing real code, checked by running tests
class CodeProblem
{
    public string Id, Title, Cls, Prompt, Starter, Tests, Hint, Solution;
    public int Level = 1, Minutes;
}

class TestResult
{
    public string Test, Got;
    public bool Ok;
}

class RunResult
{
    public List<TestResult> Tests = new List<TestResult>();
    public string Error, ErrorLine, Output;
    public bool TimedOut, NoPython;
    public int Passed { get { return Tests.Count(t => t.Ok); } }
    public bool AllPassed { get { return Error == null && !TimedOut && !NoPython && Tests.Count > 0 && Tests.All(t => t.Ok); } }
}

// Reads the problem files and runs code against their tests with the Python installed on this PC
static class CodeRunner
{
    // Problem files look like this:
    //   === Title
    //   Class: CS 4 - Programming I (Python)
    //   Tier: 2             (1 = Beginner, 2 = Intermediate, 3 = Advanced, 4 = Expert)
    //   Minutes: 25         (about how long it should take)
    //   --- prompt          (then the text; lines indented 4 spaces show as code)
    //   --- starter         (the code you start with)
    //   --- tests           (one Python expression per line that should be True;
    //                        a line starting with "> " is run as setup instead)
    //   --- hint
    //   --- solution
    public static List<CodeProblem> Load(string dir)
    {
        var list = new List<CodeProblem>();
        if (!Directory.Exists(dir)) return list;
        foreach (var path in Directory.GetFiles(dir, "*.txt").OrderBy(p => p))
        {
            CodeProblem cur = null;
            string section = null;
            var buf = new StringBuilder();
            Action flushSection = () =>
            {
                if (cur == null || section == null) return;
                string text = buf.ToString().Trim('\r', '\n');
                switch (section)
                {
                    case "prompt": cur.Prompt = text; break;
                    case "starter": cur.Starter = text; break;
                    case "tests": cur.Tests = text; break;
                    case "hint": cur.Hint = text; break;
                    case "solution": cur.Solution = text; break;
                }
                buf.Clear();
            };
            Action flush = () =>
            {
                flushSection();
                if (cur != null && !string.IsNullOrWhiteSpace(cur.Tests)) list.Add(cur);
                cur = null;
                section = null;
            };
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (raw.StartsWith("=== "))
                {
                    flush();
                    string title = raw.Substring(4).Trim();
                    cur = new CodeProblem { Title = title, Id = Slug(title), Prompt = "", Starter = "", Hint = "", Solution = "", Cls = "" };
                    continue;
                }
                if (cur == null) continue; // comments and notes before the first problem
                if (raw.StartsWith("--- "))
                {
                    flushSection();
                    section = raw.Substring(4).Trim().ToLowerInvariant();
                    continue;
                }
                if (section == null)
                {
                    var line = raw.Trim();
                    int c = line.IndexOf(':');
                    if (c < 0) continue;
                    string key = line.Substring(0, c).Trim().ToLowerInvariant(), val = line.Substring(c + 1).Trim();
                    int n;
                    if (key == "class") cur.Cls = val;
                    else if ((key == "tier" || key == "level") && int.TryParse(val, out n)) cur.Level = Math.Max(1, Math.Min(4, n));
                    else if (key == "minutes" && int.TryParse(val, out n)) cur.Minutes = n;
                    continue;
                }
                buf.Append(raw.TrimEnd()).Append('\n');
            }
            flush();
        }
        return list;
    }

    static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
        return sb.ToString().Trim('-');
    }

    // The real python.exe, not the Microsoft Store shortcut that only opens the Store
    static string python;
    public static string FindPython()
    {
        if (python != null && File.Exists(python)) return python;
        var candidates = new List<string>();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var root in new[] { System.IO.Path.Combine(local, "Programs", "Python"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"C:\" })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                candidates.AddRange(Directory.GetDirectories(root, "Python3*").OrderByDescending(d => d).Select(d => System.IO.Path.Combine(d, "python.exe")));
            }
            catch { }
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (dir.Length == 0 || dir.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            candidates.Add(System.IO.Path.Combine(dir.Trim(), "python.exe"));
        }
        python = candidates.FirstOrDefault(p => { try { return File.Exists(p); } catch { return false; } });
        return python;
    }

    // Runs the student's code, then each test line, in a separate Python process with a time limit.
    // Output comes back as tagged lines so the student's own print() calls can't confuse it.
    const string Harness = @"import sys, io, traceback
SEP = '\x1f'
def out(kind, *parts):
    sys.__stdout__.write('@@' + kind + SEP + SEP.join(str(p).replace('\r', '').replace('\n', '\\n') for p in parts) + '\n')
    sys.__stdout__.flush()
src = open(sys.argv[1], encoding='utf-8').read()
tests = open(sys.argv[2], encoding='utf-8').read().splitlines()
ns = {'__name__': '__quizzahh__'}
buf = io.StringIO()
sys.stdout = buf
def where(e):
    for f in reversed(traceback.extract_tb(e.__traceback__)):
        if f.filename == 'your_code.py':
            return f.lineno
    return ''
try:
    exec(compile(src, 'your_code.py', 'exec'), ns)
except SyntaxError as e:
    sys.stdout = sys.__stdout__
    out('ERR', '%s: %s' % (type(e).__name__, e.msg), e.lineno or '')
    sys.exit(0)
except BaseException as e:
    sys.stdout = sys.__stdout__
    out('ERR', '%s: %s' % (type(e).__name__, e), where(e))
    sys.exit(0)
def _raises(exc, fn, *args, **kw):
    try:
        fn(*args, **kw)
    except exc:
        return True
    except BaseException:
        return False
    return False
ns.setdefault('raises', _raises)
for t in tests:
    if not t.strip() or t.lstrip().startswith('#'):
        continue
    if t.startswith('> '):
        try:
            exec(t[2:], ns)
        except BaseException as e:
            out('T', 0, t[2:], 'setup failed: %s: %s' % (type(e).__name__, e))
        continue
    try:
        ok = bool(eval(t, ns))
        got = ''
        if not ok and ' == ' in t:
            try:
                got = repr(eval(t.split(' == ')[0], ns))
            except BaseException as e:
                got = '%s: %s' % (type(e).__name__, e)
        out('T', 1 if ok else 0, t, got[:300])
    except BaseException as e:
        line = where(e)
        out('T', 0, t, '%s: %s%s' % (type(e).__name__, e, (' (your line %s)' % line) if line else ''))
sys.stdout = sys.__stdout__
printed = buf.getvalue()
if printed:
    out('OUT', printed[-3000:])
";

    public static RunResult Run(string code, string tests, int timeoutMs = 10000)
    {
        var r = new RunResult();
        string py = FindPython();
        if (py == null) { r.NoPython = true; return r; }
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quizzahh-code");
        Directory.CreateDirectory(dir);
        string id = Guid.NewGuid().ToString("N").Substring(0, 8);
        string h = System.IO.Path.Combine(dir, "harness.py"), c = System.IO.Path.Combine(dir, "code-" + id + ".py"), t = System.IO.Path.Combine(dir, "tests-" + id + ".txt");
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(h, Harness, utf8);
        File.WriteAllText(c, (code ?? "").Replace("\r\n", "\n"), utf8);
        File.WriteAllText(t, (tests ?? "").Replace("\r\n", "\n"), utf8);
        try
        {
            var psi = new ProcessStartInfo(py, "-X utf8 \"" + h + "\" \"" + c + "\" \"" + t + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONDONTWRITEBYTECODE"] = "1";
            using (var p = Process.Start(psi))
            {
                p.StandardInput.Close(); // input() gets end-of-file instead of waiting forever
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (stdout) stdout.Append(e.Data).Append('\n'); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (stderr) stderr.Append(e.Data).Append('\n'); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    r.TimedOut = true;
                    try { p.Kill(); } catch { }
                }
                p.WaitForExit(2000);
                string all;
                lock (stdout) all = stdout.ToString();
                foreach (var line in all.Split('\n'))
                {
                    if (!line.StartsWith("@@")) continue;
                    var parts = line.Substring(2).Split('\x1f');
                    Func<int, string> part = i => i < parts.Length ? parts[i].Replace("\\n", "\n") : "";
                    if (parts[0] == "T") r.Tests.Add(new TestResult { Ok = part(1) == "1", Test = part(2), Got = part(3) });
                    else if (parts[0] == "ERR") { r.Error = part(1); r.ErrorLine = part(2); }
                    else if (parts[0] == "OUT") r.Output = part(1);
                }
                string err;
                lock (stderr) err = stderr.ToString().Trim();
                if (r.Error == null && !r.TimedOut && r.Tests.Count == 0 && err.Length > 0)
                    r.Error = err.Split('\n').Last().Trim();
            }
        }
        catch (Exception ex) { r.Error = "Couldn't start Python: " + ex.Message; }
        finally
        {
            try { File.Delete(c); File.Delete(t); } catch { }
        }
        return r;
    }
}

// The Code Lab: a big sheet over the middle and right of the screen with the problem on the left,
// an editor on the right, and test results underneath. Heather stays visible on the left to react.
partial class QuizWindow
{
    static readonly string CodingDir = System.IO.Path.Combine(DataDir, "coding");
    static readonly string CodingSaves = System.IO.Path.Combine(DataDir, "coding", "saves");

    Grid codeSheet, codeListView, codeEditView;
    TranslateTransform codeSlide;
    StackPanel codeList, codePromptPanel, codeResults;
    StackPanel codeFilterRow;
    TextBox codeEditor;
    TextBlock codeGutter, codeStatus;
    TranslateTransform codeGutterShift;
    Btn codeRunBtn;
    bool codeOpen, codeRunning;
    int codeTier;
    List<CodeProblem> codeProblems = new List<CodeProblem>();
    CodeProblem codeCurrent;
    HashSet<string> codeSolved = new HashSet<string>();
    DispatcherTimerLite codeSaveTimer;

    // A tiny one-shot timer (saves the code a moment after you stop typing)
    class DispatcherTimerLite
    {
        readonly System.Windows.Threading.DispatcherTimer t;
        public DispatcherTimerLite(int ms, Action a)
        {
            t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (s, e) => { t.Stop(); a(); };
        }
        public void Kick() { t.Stop(); t.Start(); }
        public void Flush(Action a) { if (t.IsEnabled) { t.Stop(); a(); } }
    }

    void BuildCodeLab(Grid host, double W, double H, double colW)
    {
        codeSheet = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Right, Width = Math.Max(760, W - colW + 6),
            Margin = new Thickness(0, 18, 24, 18), Visibility = Visibility.Collapsed
        };
        codeSlide = new TranslateTransform(0, -H);
        codeSheet.RenderTransform = codeSlide;
        Panel.SetZIndex(codeSheet, 44);
        var card = new Border
        {
            CornerRadius = new CornerRadius(22), Background = Hex("#F8130D0C"), BorderBrush = Hex("#55C23B30"), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 34, ShadowDepth = 5, Opacity = 0.6, Color = Colors.Black }
        };
        codeSheet.Children.Add(card);
        var root = new Grid();
        card.Child = root;
        root.Children.Add(Grain(22));

        // ----- Problem list -----
        codeListView = new Grid { Margin = new Thickness(30, 22, 30, 24) };
        codeListView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        codeListView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new DockPanel();
        var close = SmallIcon("", "Close the Code Lab (Esc)", CloseCodeLab);
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "CODE LAB", FontSize = 30, FontWeight = FontWeights.Bold, FontFamily = TitleFont, Foreground = TitleRed });
        titles.Children.Add(T("Real programming practice. Read the problem, write the code yourself, and run the tests until they all pass. " +
            "These take 10 to 60 minutes each, like homework problems.", 14, FontWeights.Normal, Muted));
        head.Children.Add(titles);
        codeListView.Children.Add(head);
        var listScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 14, 0, 0) };
        Grid.SetRow(listScroll, 1);
        var listStack = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        codeFilterRow = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        listStack.Children.Add(codeFilterRow);
        codeList = new StackPanel();
        listStack.Children.Add(codeList);
        listScroll.Content = listStack;
        codeListView.Children.Add(listScroll);
        root.Children.Add(codeListView);

        // ----- One problem: prompt | editor + results -----
        codeEditView = new Grid { Margin = new Thickness(24, 18, 24, 20), Visibility = Visibility.Collapsed };
        codeEditView.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
        codeEditView.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        codeEditView.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8, GridUnitType.Star) });
        var promptScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        codePromptPanel = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        promptScroll.Content = codePromptPanel;
        codeEditView.Children.Add(promptScroll);

        var right = new Grid();
        Grid.SetColumn(right, 2);
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(62, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38, GridUnitType.Star) });
        codeEditView.Children.Add(right);

        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var barRight = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(barRight, Dock.Right);
        barRight.Children.Add(SmallIcon("", "Back to all problems", ShowCodeList));
        barRight.Children.Add(SmallIcon("", "Close the Code Lab (Esc)", CloseCodeLab));
        bar.Children.Add(barRight);
        var barLeft = new StackPanel { Orientation = Orientation.Horizontal };
        codeRunBtn = CodeButton("▶  RUN TESTS", "Run your code against the tests (Ctrl+Enter)", true, () => RunCodeTests(false));
        barLeft.Children.Add(codeRunBtn);
        barLeft.Children.Add(CodeButton("hint", "Get a nudge in the right direction", false, ShowCodeHint));
        barLeft.Children.Add(CodeButton("solution", "See one working solution (try first!)", false, ShowCodeSolution));
        barLeft.Children.Add(CodeButton("reset", "Start over from the starter code", false, ResetCode));
        bar.Children.Add(barLeft);
        right.Children.Add(bar);

        // Editor with line numbers (Python errors point at line numbers)
        var edFrame = new Border
        {
            CornerRadius = new CornerRadius(12), Background = Hex("#E60A0707"), BorderBrush = CardStroke, BorderThickness = new Thickness(1),
            ClipToBounds = true
        };
        Grid.SetRow(edFrame, 1);
        var edGrid = new Grid();
        edGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        edGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var gutterHost = new Border { Background = Hex("#0CFFFFFF"), ClipToBounds = true };
        codeGutterShift = new TranslateTransform();
        codeGutter = new TextBlock
        {
            FontFamily = CodeFont, FontSize = 15, Foreground = Hex("#5E554E"), TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 10, 8, 0), LineHeight = 20, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            RenderTransform = codeGutterShift
        };
        gutterHost.Child = codeGutter;
        edGrid.Children.Add(gutterHost);
        codeEditor = new TextBox
        {
            FontFamily = CodeFont, FontSize = 15, Foreground = Hex("#E6DCC8"), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            CaretBrush = Brushes.White, SelectionBrush = Blood, AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8, 10, 8, 10), VerticalContentAlignment = VerticalAlignment.Top
        };
        TextBlock.SetLineHeight(codeEditor, 20);
        TextBlock.SetLineStackingStrategy(codeEditor, LineStackingStrategy.BlockLineHeight);
        Grid.SetColumn(codeEditor, 1);
        edGrid.Children.Add(codeEditor);
        edFrame.Child = edGrid;
        right.Children.Add(edFrame);
        codeEditor.PreviewKeyDown += CodeEditorKey;
        codeEditor.TextChanged += (s, e) => { UpdateGutter(); if (codeSaveTimer != null) codeSaveTimer.Kick(); };
        codeEditor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((s, e) => codeGutterShift.Y = -e.VerticalOffset));
        codeSaveTimer = new DispatcherTimerLite(600, SaveCurrentCode);

        // Test results
        var resFrame = new Border
        {
            CornerRadius = new CornerRadius(12), Background = Hex("#0EFFFFFF"), BorderBrush = CardStroke, BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(14, 10, 14, 10)
        };
        Grid.SetRow(resFrame, 2);
        var resDock = new DockPanel();
        codeStatus = T("Write your code, then press RUN TESTS (or Ctrl+Enter).", 14, FontWeights.SemiBold, Muted);
        codeStatus.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(codeStatus, Dock.Top);
        resDock.Children.Add(codeStatus);
        var resScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        codeResults = new StackPanel();
        resScroll.Content = codeResults;
        resDock.Children.Add(resScroll);
        resFrame.Child = resDock;
        right.Children.Add(resFrame);
        root.Children.Add(codeEditView);

        host.Children.Add(codeSheet);
    }

    Btn CodeButton(string text, string tip, bool primary, Action click)
    {
        var b = new Btn
        {
            CornerRadius = new CornerRadius(11), Background = primary ? Accent : CardBg, BorderBrush = primary ? Hex("#55C23B30") : CardStroke,
            BorderThickness = new Thickness(1), Padding = new Thickness(16, 8, 16, 9), Margin = new Thickness(0, 0, 8, 0), ToolTip = tip,
            Child = new TextBlock
            {
                Text = text, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = primary ? Hex("#F2E8DA") : TextMain,
                FontFamily = primary ? TitleFont : UiFont
            }
        };
        if (!primary)
        {
            b.MouseEnter += (s, e) => b.Background = CardHover;
            b.MouseLeave += (s, e) => b.Background = CardBg;
        }
        return Clickable(b, click);
    }

    // ----- Opening, closing, the list -----
    void ToggleCodeLab() { if (codeOpen) CloseCodeLab(); else OpenCodeLab(); }

    void OpenCodeLab()
    {
        if (codeOpen || codeSheet == null) return;
        if (scratchOpen) CloseScratch();
        codeProblems = CodeRunner.Load(CodingDir);
        LoadCodeProgress();
        codeOpen = true;
        codeSheet.Visibility = Visibility.Visible;
        if (scratchTab != null) scratchTab.Visibility = Visibility.Hidden;
        ShowCodeList();
        var slide = new DoubleAnimation(0, TimeSpan.FromMilliseconds(360)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        slide.Completed += (s, e) => { if (codeOpen && heatherView != null) heatherView.Visibility = Visibility.Hidden; };
        codeSlide.BeginAnimation(TranslateTransform.YProperty, slide);
        if (CodeRunner.FindPython() == null)
            Say("The Code Lab needs Python installed to run your code. Get it from python.org, then come back.");
        else
            Say(Pick(new[] { "Code Lab. Pick a problem and actually write it. No multiple choice to hide behind.",
                             "Okay, real coding. Start with Beginner if you're new to it.",
                             "Time to type. Run the tests as often as you want." }));
    }

    void CloseCodeLab()
    {
        if (!codeOpen) return;
        codeSaveTimer.Flush(SaveCurrentCode);
        codeOpen = false;
        if (scratchTab != null) scratchTab.Visibility = Visibility.Visible;
        if (heatherView != null) heatherView.Visibility = Visibility.Visible;
        var slide = new DoubleAnimation(-Height, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        slide.Completed += (s, e) => { if (!codeOpen) codeSheet.Visibility = Visibility.Collapsed; };
        codeSlide.BeginAnimation(TranslateTransform.YProperty, slide);
        Keyboard.ClearFocus();
    }

    void ShowCodeList()
    {
        if (codeCurrent != null) codeSaveTimer.Flush(SaveCurrentCode);
        codeCurrent = null;
        codeEditView.Visibility = Visibility.Collapsed;
        codeListView.Visibility = Visibility.Visible;
        codeFilterRow.Children.Clear();
        codeList.Children.Clear();

        string py = CodeRunner.FindPython();
        if (py == null)
        {
            var warn = T("Python isn't installed, so your code can't be run yet. Install it from python.org (check \"Add python.exe to PATH\"), then reopen the Code Lab.", 14, FontWeights.SemiBold, Bad);
            warn.Margin = new Thickness(0, 0, 0, 12);
            codeList.Children.Add(warn);
        }
        if (codeProblems.Count == 0)
        {
            codeList.Children.Add(T("No coding problems found. They live in " + CodingDir + " as .txt files.", 14, FontWeights.Normal, Muted));
            return;
        }

        // Tiers, Beginner and beyond. Each tab shows how far you are through it.
        if (codeTier < 1 || codeTier > 4)
        {
            // Open on the first tier you haven't finished
            codeTier = 1;
            for (int t = 1; t <= 4; t++)
            {
                var inTier = codeProblems.Where(p => p.Level == t).ToList();
                if (inTier.Count > 0 && inTier.Any(p => !codeSolved.Contains(p.Id))) { codeTier = t; break; }
            }
        }
        var tiers = new UniformGrid { Rows = 1, Columns = 4, Margin = new Thickness(-4, 0, -4, 4) };
        for (int t = 1; t <= 4; t++)
        {
            int tier = t;
            var inTier = codeProblems.Where(p => p.Level == tier).ToList();
            int done = inTier.Count(p => codeSolved.Contains(p.Id));
            tiers.Children.Add(TierCard(tier, done, inTier.Count, tier == codeTier, () => { codeTier = tier; ShowCodeList(); }));
        }
        codeFilterRow.Children.Add(tiers);

        var shown = codeProblems.Where(p => p.Level == codeTier).ToList();
        int solved = shown.Count(p => codeSolved.Contains(p.Id));
        var blurb = T(TierBlurbs[codeTier], 14, FontWeights.Normal, TextMain);
        blurb.Margin = new Thickness(2, 10, 0, 4);
        codeList.Children.Add(blurb);
        var sum = T(TierNames[codeTier].ToUpperInvariant() + "  ·  " + solved + " OF " + shown.Count + " SOLVED", 12, FontWeights.Bold, solved == shown.Count && shown.Count > 0 ? Good : Hex("#E08A7A"));
        sum.FontFamily = TitleFont;
        sum.Margin = new Thickness(2, 6, 0, 10);
        codeList.Children.Add(sum);
        if (codeTier > 1)
        {
            var prev = codeProblems.Where(p => p.Level == codeTier - 1).ToList();
            int prevDone = prev.Count(p => codeSolved.Contains(p.Id));
            if (prev.Count > 0 && prevDone * 2 < prev.Count)
            {
                var tip = T("Tip: these build on " + TierNames[codeTier - 1] + ". You've solved " + prevDone + " of " + prev.Count + " there, so you may want to warm up first.", 13, FontWeights.Normal, Gold);
                tip.Margin = new Thickness(2, 0, 0, 10);
                codeList.Children.Add(tip);
            }
        }

        int i = 0;
        foreach (var prob in shown)
        {
            var p = prob;
            bool done = codeSolved.Contains(p.Id);
            bool started = File.Exists(SavePath(p));
            string sub = p.Cls + "  ·  about " + Math.Max(5, p.Minutes) + " min"
                + (done ? "  ·  ✓ solved" : started ? "  ·  in progress" : "");
            var card = Choice(done ? "✓" : (++i).ToString(), p.Title, sub, () => OpenProblem(p), true);
            if (done)
            {
                var badge = (Border)((Grid)card.Child).Children[0];
                badge.Background = Good;
                ((TextBlock)badge.Child).Foreground = Hex("#160E0C");
            }
            codeList.Children.Add(card);
        }
        var note = T("Your code saves automatically. Problems are plain text files in " + CodingDir + ", so you can add your own.", 12, FontWeights.Normal, Muted);
        note.Margin = new Thickness(2, 10, 0, 0);
        codeList.Children.Add(note);
    }

    // The big Code Lab button on the home screen
    Btn CodeLabCard()
    {
        var probs = CodeRunner.Load(CodingDir);
        LoadCodeProgress();
        int done = probs.Count(p => codeSolved.Contains(p.Id));
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new Border
        {
            Width = 46, Height = 46, CornerRadius = new CornerRadius(12), Background = Hex("#33C23B30"), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 20, Foreground = TitleRed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });
        var sp = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(sp, 1);
        var t = T("CODE LAB", 19, FontWeights.Bold, TextMain);
        t.FontFamily = TitleFont;
        sp.Children.Add(t);
        sp.Children.Add(T("Write real code and run the tests  ·  Beginner → Intermediate → Advanced → Expert", 13, FontWeights.Normal, Muted));
        var tiers = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        for (int tier = 1; tier <= 4; tier++)
        {
            int k = tier;
            var inTier = probs.Where(p => p.Level == k).ToList();
            int d = inTier.Count(p => codeSolved.Contains(p.Id));
            tiers.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8), Background = Hex("#14FFFFFF"), Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(0, 0, 6, 4),
                Child = T(TierNames[k] + " " + d + "/" + inTier.Count, 11.5, FontWeights.SemiBold, d == inTier.Count && inTier.Count > 0 ? Good : Hex(TierColors[k]))
            });
        }
        sp.Children.Add(tiers);
        g.Children.Add(sp);
        var b = new Btn
        {
            CornerRadius = new CornerRadius(16), Background = Hex("#1AC23B30"), BorderBrush = Hex("#66C23B30"), BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12, 16, 12), Margin = new Thickness(0, 14, 0, 0), Child = g,
            ToolTip = done + " of " + probs.Count + " coding problems solved"
        };
        b.MouseEnter += (s, e) => b.Background = Hex("#2AC23B30");
        b.MouseLeave += (s, e) => b.Background = Hex("#1AC23B30");
        return Clickable(b, OpenCodeLab);
    }

    static readonly string[] TierNames ={ "", "Beginner", "Intermediate", "Advanced", "Expert" };
    static readonly string[] TierBlurbs =
    {
        "",
        "Beginner: variables, if-statements, loops, strings and lists. If you've never written a program before, start here.",
        "Intermediate: functions working together, dictionaries, sets, files of data, and classes. Like a first-year programming assignment.",
        "Advanced: build the data structures yourself (stacks, linked lists, hash maps, heaps, trees) and classic algorithms. Second-year material.",
        "Expert: graph algorithms, dynamic programming, and parsing. Interview-level problems that take real planning."
    };
    static readonly string[] TierColors = { "", "#7FB58A", "#C9A13B", "#E0823F", "#C8433A" };

    Btn TierCard(int tier, int done, int total, bool on, Action click)
    {
        var sp = new StackPanel();
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        for (int k = 1; k <= 4; k++)
            dots.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 4, 0), Background = k <= tier ? Hex(TierColors[tier]) : Hex("#22FFFFFF") });
        sp.Children.Add(dots);
        var name = T(TierNames[tier], 16, FontWeights.Bold, on ? Brushes.White : TextMain);
        name.FontFamily = TitleFont;
        sp.Children.Add(name);
        sp.Children.Add(T(total == 0 ? "coming soon" : done + " / " + total + " solved", 12, FontWeights.Normal, on ? Hex("#E8DDD0") : Muted));
        var bar = new Grid { Height = 4, Margin = new Thickness(0, 6, 0, 0) };
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = Hex("#1EFFFFFF") });
        bar.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(2), Background = Hex(TierColors[tier]), HorizontalAlignment = HorizontalAlignment.Left,
            Width = total == 0 ? 0 : 1, RenderTransformOrigin = new Point(0, 0.5)
        });
        var fill = (Border)bar.Children[1];
        bar.SizeChanged += (s, e) => fill.Width = total == 0 ? 0 : bar.ActualWidth * done / total;
        sp.Children.Add(bar);
        var b = new Btn
        {
            CornerRadius = new CornerRadius(14), Background = on ? Accent : CardBg, BorderBrush = on ? Hex("#55C23B30") : CardStroke,
            BorderThickness = new Thickness(1), Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(4), Child = sp
        };
        if (!on)
        {
            b.MouseEnter += (s, e) => b.Background = CardHover;
            b.MouseLeave += (s, e) => b.Background = CardBg;
        }
        return Clickable(b, click);
    }

    // ----- One problem -----
    void OpenProblem(CodeProblem p)
    {
        codeCurrent = p;
        codeListView.Visibility = Visibility.Collapsed;
        codeEditView.Visibility = Visibility.Visible;

        codePromptPanel.Children.Clear();
        var meta = T(TierNames[p.Level].ToUpperInvariant() + "  ·  ABOUT " + Math.Max(5, p.Minutes) + " MIN  ·  " + p.Cls.ToUpperInvariant(), 12, FontWeights.Bold, Hex("#E08A7A"));
        meta.FontFamily = TitleFont;
        codePromptPanel.Children.Add(meta);
        var title = new TextBlock { Text = p.Title, FontSize = 26, FontWeight = FontWeights.Bold, FontFamily = TitleFont, Foreground = TextMain, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) };
        codePromptPanel.Children.Add(title);
        foreach (var block in PromptBlocks(p.Prompt))
            codePromptPanel.Children.Add(block);

        string saved = null;
        try { if (File.Exists(SavePath(p))) saved = File.ReadAllText(SavePath(p), Encoding.UTF8); } catch { }
        codeEditor.Text = (saved ?? p.Starter).Replace("\r\n", "\n").Replace("\n", "\r\n");
        codeSaveTimer.Flush(() => { }); // loading isn't an edit
        codeResults.Children.Clear();
        codeStatus.Text = codeSolved.Contains(p.Id) ? "✓ You've solved this one. Run it again any time." : "Write your code, then press RUN TESTS (or Ctrl+Enter).";
        codeStatus.Foreground = codeSolved.Contains(p.Id) ? Good : Muted;
        int tests = p.Tests.Split('\n').Count(l => l.Trim().Length > 0 && !l.StartsWith("> ") && !l.TrimStart().StartsWith("#"));
        var testsNote = T(tests + " tests will check your code. Run them as often as you like.", 13, FontWeights.Normal, Muted);
        testsNote.Margin = new Thickness(0, 14, 0, 0);
        codePromptPanel.Children.Add(testsNote);
        UpdateGutter();
        codeEditor.Focus();
        codeEditor.CaretIndex = 0;
        Say(Pick(new[] { "Read the whole problem first. Then plan, then type.", "This one's " + (p.Level == 1 ? "a beginner problem. You've got this." : p.Level == 2 ? "intermediate. Plan it before you type." : p.Level == 3 ? "advanced. Sketch it on the scratch paper first." : "expert level. Take your time, it's supposed to be hard."),
                         "Start small. Get one test passing, then the next." }));
    }

    // Prompt text: blank lines split paragraphs, and paragraphs indented 4 spaces show as code
    IEnumerable<FrameworkElement> PromptBlocks(string text)
    {
        var paras = new List<List<string>>();
        var cur = new List<string>();
        foreach (var line in (text ?? "").Split('\n'))
        {
            if (line.Trim().Length == 0) { if (cur.Count > 0) { paras.Add(cur); cur = new List<string>(); } continue; }
            cur.Add(line);
        }
        if (cur.Count > 0) paras.Add(cur);
        foreach (var para in paras)
        {
            if (para.All(l => l.StartsWith("    ")))
            {
                var box = CodeBox(string.Join("\n", para.Select(l => l.Substring(4))));
                box.Margin = new Thickness(0, 2, 0, 12);
                yield return box;
            }
            else
            {
                var tb = T(string.Join(" ", para.Select(l => l.Trim())), 15, FontWeights.Normal, TextMain);
                tb.LineHeight = 23;
                tb.Margin = new Thickness(0, 0, 0, 10);
                yield return tb;
            }
        }
    }

    void UpdateGutter()
    {
        int lines = Math.Max(1, codeEditor.LineCount > 0 ? codeEditor.LineCount : codeEditor.Text.Split('\n').Length);
        var sb = new StringBuilder();
        for (int i = 1; i <= lines; i++) sb.Append(i).Append('\n');
        codeGutter.Text = sb.ToString().TrimEnd('\n');
    }

    void ShowCodeHint()
    {
        if (codeCurrent == null) return;
        string hint = string.IsNullOrWhiteSpace(codeCurrent.Hint) ? "Break it into small steps and test each one." : codeCurrent.Hint;
        AddPromptSection("HINT", hint, Gold);
        Say("Here's a nudge. " + FirstSentence(hint));
    }

    void ShowCodeSolution()
    {
        if (codeCurrent == null || string.IsNullOrWhiteSpace(codeCurrent.Solution)) return;
        AddPromptSection("ONE WORKING SOLUTION", null, Good);
        var box = CodeBox(codeCurrent.Solution);
        codePromptPanel.Children.Add(box);
        box.BringIntoView();
        Say("There's one way to do it. Now close it and write your own version from memory. That's how it sticks.");
    }

    void AddPromptSection(string label, string body, Brush color)
    {
        var l = T(label, 12, FontWeights.Bold, color);
        l.FontFamily = TitleFont;
        l.Margin = new Thickness(0, 16, 0, 6);
        codePromptPanel.Children.Add(l);
        if (body == null) { l.BringIntoView(); return; }
        foreach (var block in PromptBlocks(body)) codePromptPanel.Children.Add(block);
        l.BringIntoView();
    }

    static string FirstSentence(string s)
    {
        s = s.Replace("\n", " ").Trim();
        int dot = s.IndexOf(". ");
        return dot > 0 && dot < 160 ? s.Substring(0, dot + 1) : s.Length > 160 ? s.Substring(0, 160) + "..." : s;
    }

    void ResetCode()
    {
        if (codeCurrent == null) return;
        if (MessageBox.Show(this, "Throw away your code for \"" + codeCurrent.Title + "\" and start over from the starter code?", "Reset",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        codeEditor.Text = codeCurrent.Starter.Replace("\n", "\r\n");
        codeResults.Children.Clear();
        SaveCurrentCode();
    }

    // ----- Running the tests -----
    void RunCodeTests(bool sync)
    {
        if (codeCurrent == null || codeRunning) return;
        SaveCurrentCode();
        var prob = codeCurrent;
        string code = codeEditor.Text;
        codeRunning = true;
        codeRunBtn.Opacity = 0.5;
        codeStatus.Text = "Running your code...";
        codeStatus.Foreground = Muted;
        codeResults.Children.Clear();
        if (sync) { ShowRunResult(prob, CodeRunner.Run(code, prob.Tests)); return; }
        var th = new Thread(() =>
        {
            var r = CodeRunner.Run(code, prob.Tests);
            Dispatcher.BeginInvoke(new Action(() => ShowRunResult(prob, r)));
        }) { IsBackground = true };
        th.Start();
    }

    void ShowRunResult(CodeProblem prob, RunResult r)
    {
        codeRunning = false;
        codeRunBtn.Opacity = 1;
        if (codeCurrent != prob) return;
        codeResults.Children.Clear();
        Func<string, Brush, double, TextBlock> line = (text, brush, size) =>
        {
            var tb = T(text, size, FontWeights.Normal, brush);
            tb.FontFamily = CodeFont;
            tb.Margin = new Thickness(0, 2, 0, 2);
            return tb;
        };

        if (r.NoPython)
        {
            codeStatus.Text = "Python isn't installed, so your code can't run. Get it from python.org.";
            codeStatus.Foreground = Bad;
            return;
        }
        if (r.TimedOut)
        {
            codeStatus.Text = "⏱ Stopped after 10 seconds. Is there a loop that never ends?";
            codeStatus.Foreground = Bad;
            HeadShake();
            Say("It ran for ten seconds without finishing. Check your loops. Does something actually change each time around?");
        }
        else if (r.Error != null)
        {
            int ln;
            bool hasLine = int.TryParse(r.ErrorLine, out ln);
            codeStatus.Text = "✗ Your code crashed" + (hasLine ? " on line " + ln : "") + " before the tests could run";
            codeStatus.Foreground = Bad;
            codeResults.Children.Add(line(r.Error, Bad, 14));
            if (hasLine) JumpToLine(ln);
            HeadShake();
            string kind = r.Error.Split(':')[0];
            Say(kind == "SyntaxError" || kind == "IndentationError"
                ? "Python can't even read line " + (hasLine ? ln.ToString() : "that") + ". Check for a missing colon, bracket, or quote, and your indentation."
                : "It crashed with a " + kind + (hasLine ? " on line " + ln : "") + ". Read the message, it usually tells you exactly what's wrong.");
        }
        else
        {
            int pass = r.Passed, total = r.Tests.Count;
            bool all = r.AllPassed;
            codeStatus.Text = (all ? "✓ " : "") + pass + " of " + total + " tests passed" + (all ? ". Nice work!" : "");
            codeStatus.Foreground = all ? Good : pass > 0 ? Gold : Bad;
            foreach (var t in r.Tests.OrderBy(t => t.Ok))
            {
                codeResults.Children.Add(line((t.Ok ? "✓  " : "✗  ") + t.Test, t.Ok ? Good : Bad, 13.5));
                if (!t.Ok && !string.IsNullOrEmpty(t.Got))
                {
                    var got = line("     got: " + t.Got, Muted, 13);
                    codeResults.Children.Add(got);
                }
            }
            if (all)
            {
                bool first = codeSolved.Add(prob.Id);
                if (first)
                {
                    int xp = 30 * prob.Level;
                    stats.Xp += xp;
                    stats.Save();
                    SaveCodeProgress();
                    codeStatus.Text += "  +" + xp + " XP";
                }
                Cheer();
                Sfx.Play(Sfx.Correct);
                Say(Pick(new[] { "All tests pass. You wrote that yourself. That's the real thing.", "Every test green. Good. Now could you explain how it works out loud?",
                                 "Done, and it works. On to the next one?" }), Good);
            }
            else
            {
                var firstFail = r.Tests.FirstOrDefault(t => !t.Ok);
                Say(pass + " of " + total + " passing. " + (pass == 0 ? "Start by getting just the first test right." : "Look at the first red test and what your code returned."));
                if (firstFail != null) HeadShake();
            }
        }
        if (!string.IsNullOrEmpty(r.Output))
        {
            var lbl = T("YOUR print() OUTPUT", 11, FontWeights.Bold, Muted);
            lbl.FontFamily = TitleFont;
            lbl.Margin = new Thickness(0, 12, 0, 4);
            codeResults.Children.Add(lbl);
            codeResults.Children.Add(line(r.Output.TrimEnd(), TextMain, 13));
        }
    }

    void JumpToLine(int ln)
    {
        try
        {
            int idx = codeEditor.GetCharacterIndexFromLineIndex(Math.Max(0, Math.Min(codeEditor.LineCount - 1, ln - 1)));
            int len = codeEditor.GetLineLength(Math.Max(0, Math.Min(codeEditor.LineCount - 1, ln - 1)));
            codeEditor.Focus();
            codeEditor.Select(idx, Math.Max(0, len));
            codeEditor.ScrollToLine(Math.Max(0, ln - 1));
        }
        catch { }
    }

    // ----- Editor keys: Python-friendly tabs and auto-indent -----
    void CodeEditorKey(object sender, KeyEventArgs e)
    {
        var tb = codeEditor;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && e.Key == Key.Enter) { e.Handled = true; RunCodeTests(false); return; }
        if (e.Key == Key.Tab)
        {
            e.Handled = true;
            if (tb.SelectionLength > 0 && tb.SelectedText.Contains("\n")) { IndentLines(!shift); return; }
            if (shift) { IndentLines(false); return; }
            int col = tb.CaretIndex - tb.GetCharacterIndexFromLineIndex(tb.GetLineIndexFromCharacterIndex(tb.CaretIndex));
            tb.SelectedText = new string(' ', 4 - col % 4);
            tb.CaretIndex += tb.SelectionLength; tb.SelectionLength = 0;
            return;
        }
        if (e.Key == Key.Enter && !ctrl)
        {
            e.Handled = true;
            int li = tb.GetLineIndexFromCharacterIndex(tb.CaretIndex);
            int start = tb.GetCharacterIndexFromLineIndex(li);
            string before = tb.Text.Substring(start, tb.CaretIndex - start);
            int indent = before.Length - before.TrimStart(' ').Length;
            string trimmed = before.TrimEnd();
            if (trimmed.EndsWith(":")) indent += 4;
            else if (trimmed == "return" || trimmed.StartsWith("return ") || trimmed == "pass" || trimmed == "break" || trimmed == "continue")
                indent = Math.Max(0, indent - 4);
            string ins = "\r\n" + new string(' ', indent);
            tb.SelectedText = ins;
            tb.CaretIndex = tb.SelectionStart + ins.Length;
            tb.SelectionLength = 0;
            return;
        }
        if (e.Key == Key.Back && tb.SelectionLength == 0 && tb.CaretIndex > 0)
        {
            int start = tb.GetCharacterIndexFromLineIndex(tb.GetLineIndexFromCharacterIndex(tb.CaretIndex));
            string before = tb.Text.Substring(start, tb.CaretIndex - start);
            if (before.Length > 0 && before.Trim().Length == 0)
            {
                e.Handled = true;
                int remove = before.Length % 4 == 0 ? 4 : before.Length % 4;
                int at = tb.CaretIndex - remove;
                tb.Text = tb.Text.Remove(at, remove);
                tb.CaretIndex = at;
            }
        }
    }

    void IndentLines(bool add)
    {
        var tb = codeEditor;
        int first = tb.GetLineIndexFromCharacterIndex(tb.SelectionStart);
        int last = tb.GetLineIndexFromCharacterIndex(tb.SelectionStart + Math.Max(0, tb.SelectionLength - 1));
        int s = tb.GetCharacterIndexFromLineIndex(first);
        int e = last + 1 < tb.LineCount ? tb.GetCharacterIndexFromLineIndex(last + 1) : tb.Text.Length;
        var lines = tb.Text.Substring(s, e - s).Split(new[] { "\r\n" }, StringSplitOptions.None);
        for (int i = 0; i < lines.Length; i++)
        {
            if (i == lines.Length - 1 && lines[i].Length == 0) continue;
            if (add) lines[i] = "    " + lines[i];
            else { int n = 0; while (n < 4 && n < lines[i].Length && lines[i][n] == ' ') n++; lines[i] = lines[i].Substring(n); }
        }
        string block = string.Join("\r\n", lines);
        tb.Text = tb.Text.Substring(0, s) + block + tb.Text.Substring(e);
        tb.Select(s, block.TrimEnd('\r', '\n').Length);
    }

    // Keys while the Code Lab is open (outside the editor). Returns true when the key was used.
    bool CodeKey(KeyEventArgs e)
    {
        if (!codeOpen) return false;
        if (e.Key == Key.Escape) { CloseCodeLab(); return true; }
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { RunCodeTests(false); return true; }
        return false;
    }

    // ----- Saving -----
    static string SavePath(CodeProblem p) { return System.IO.Path.Combine(CodingSaves, p.Id + ".py"); }

    void SaveCurrentCode()
    {
        if (codeCurrent == null || Stats.ReadOnly) return;
        try
        {
            Directory.CreateDirectory(CodingSaves);
            string text = codeEditor.Text.Replace("\r\n", "\n");
            if (text.Trim() == (codeCurrent.Starter ?? "").Trim() && !File.Exists(SavePath(codeCurrent))) return;
            File.WriteAllText(SavePath(codeCurrent), text, new UTF8Encoding(false));
        }
        catch { }
    }

    void LoadCodeProgress()
    {
        codeSolved = new HashSet<string>();
        try
        {
            string f = System.IO.Path.Combine(CodingSaves, "solved.txt");
            if (File.Exists(f)) foreach (var l in File.ReadAllLines(f)) if (l.Trim().Length > 0) codeSolved.Add(l.Trim());
        }
        catch { }
    }

    void SaveCodeProgress()
    {
        if (Stats.ReadOnly) return;
        try
        {
            Directory.CreateDirectory(CodingSaves);
            File.WriteAllLines(System.IO.Path.Combine(CodingSaves, "solved.txt"), codeSolved.OrderBy(s => s));
        }
        catch { }
    }

    // ----- Self-test: every reference solution must pass its own tests, and no starter code may -----
    void SelfTestCode(StringBuilder log, Action<string> bad)
    {
        var probs = CodeRunner.Load(CodingDir);
        log.AppendLine("Coding problems: " + probs.Count + (CodeRunner.FindPython() == null ? " (Python not found, not run)" : ""));
        if (CodeRunner.FindPython() == null) return;
        foreach (var dup in probs.GroupBy(p => p.Id).Where(g => g.Count() > 1)) bad("coding: two problems named " + dup.Key);
        foreach (var p in probs)
        {
            if (string.IsNullOrWhiteSpace(p.Solution)) { bad("coding: " + p.Title + " has no solution"); continue; }
            var r = CodeRunner.Run(p.Solution, p.Tests, 20000);
            if (!r.AllPassed)
                bad("coding: solution for " + p.Title + " fails: " + (r.Error ?? (r.TimedOut ? "timed out" : string.Join(" | ", r.Tests.Where(t => !t.Ok).Take(3).Select(t => t.Test + " -> " + t.Got)))));
            var s = CodeRunner.Run(p.Starter, p.Tests, 20000);
            if (s.AllPassed) bad("coding: the starter code for " + p.Title + " already passes every test");
            log.AppendLine("  " + p.Cls + " / " + p.Title + ": " + r.Passed + "/" + r.Tests.Count + " with the solution, " + s.Passed + " with the starter");
        }
    }
}
