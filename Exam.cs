using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

// Practice exams. A class file can describe a full-length test in its header (above the first question):
//   Exam: Placement test | 30                       <- title | how many questions
//   Section: Real numbers = Real Numbers, Percents  <- a section and the topics it draws from, in test order
//   Placement: 46 = MATH 1050, STAT 1040            <- the score needed for those courses
// The exam hides the choices until you've worked the problem out, takes "I don't know", gives no feedback
// until the end, and then estimates a 0-100 score with a section breakdown and placement.
class ExamInfo
{
    public string Title = "Practice exam";
    public int Count = 30;
    public List<ExamSection> Sections = new List<ExamSection>();
    public List<KeyValuePair<int, string>> Placements = new List<KeyValuePair<int, string>>();

    public static ExamInfo Read(string path)
    {
        ExamInfo e = null;
        try
        {
            foreach (var raw in File.ReadLines(path, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.StartsWith("Q:", StringComparison.OrdinalIgnoreCase)) break;
                int colon = line.IndexOf(':');
                if (colon < 0 || line.StartsWith("#")) continue;
                string tag = line.Substring(0, colon).Trim().ToLowerInvariant(), val = line.Substring(colon + 1).Trim();
                if (tag == "exam")
                {
                    if (e == null) e = new ExamInfo();
                    var p = val.Split('|');
                    if (p[0].Trim().Length > 0) e.Title = p[0].Trim();
                    int n;
                    if (p.Length > 1 && int.TryParse(p[1].Trim(), out n) && n > 0) e.Count = Math.Min(n, 200);
                }
                else if (tag == "section" || tag == "placement")
                {
                    int eq = val.IndexOf('=');
                    if (eq < 0) continue;
                    if (e == null) e = new ExamInfo();
                    string left = val.Substring(0, eq).Trim(), right = val.Substring(eq + 1).Trim();
                    if (tag == "section")
                    {
                        var topics = right.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0);
                        e.Sections.Add(new ExamSection { Name = left, Topics = new HashSet<string>(topics, StringComparer.OrdinalIgnoreCase) });
                    }
                    else
                    {
                        int min;
                        if (int.TryParse(left, out min)) e.Placements.Add(new KeyValuePair<int, string>(min, right));
                    }
                }
            }
        }
        catch { }
        if (e != null) e.Placements.Sort((a, b) => a.Key.CompareTo(b.Key));
        return e;
    }

    public string SectionOf(string topic)
    {
        foreach (var s in Sections) if (s.Topics.Contains(topic)) return s.Name;
        return null;
    }
}

class ExamSection
{
    public string Name;
    public HashSet<string> Topics;
}

partial class QuizWindow
{
    ExamInfo exam;
    string examClass;
    List<Q> examQs;
    int[] examPicks; // the choice you picked; -1 = "I don't know", -2 = not answered yet
    int examIdx;
    bool examRevealed;
    readonly Stopwatch examClock = new Stopwatch();
    DispatcherTimer examTick;
    TextBlock examClockLabel;
    List<Border> examCards;
    Border examChoices, examRevealRow;

    static string ExamLogPath
    {
        get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "quizzahh", "exams.txt"); }
    }

    ExamInfo ExamFor(string cls)
    {
        return cls == null ? null : ExamInfo.Read(System.IO.Path.Combine(ClassesDir, cls + ".txt"));
    }

    // Past scores for a class, oldest first
    static List<int> ExamHistory(string cls)
    {
        var list = new List<int>();
        try
        {
            if (!File.Exists(ExamLogPath)) return list;
            foreach (var line in File.ReadAllLines(ExamLogPath, Encoding.UTF8))
            {
                var p = line.Split('|');
                int sc;
                if (p.Length >= 3 && p[1] == cls && int.TryParse(p[2], out sc)) list.Add(sc);
            }
        }
        catch { }
        return list;
    }

    void LogExam(int score, int correct, int total, int secs)
    {
        if (Stats.ReadOnly) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ExamLogPath));
            File.AppendAllText(ExamLogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "|" + examClass + "|" +
                score + "|" + correct + "|" + total + "|" + secs + "\r\n", Encoding.UTF8);
        }
        catch { }
    }

    // The card on a class's topic screen
    UIElement ExamCard(ExamInfo info)
    {
        var sp = new StackPanel();
        var t = T("📝  " + info.Title.ToUpper(), 15, FontWeights.Bold, Gold);
        t.FontFamily = TitleFont;
        sp.Children.Add(t);
        int sections = Math.Max(1, info.Sections.Count);
        var d = T(info.Count + " questions across " + sections + " sections, run like the real thing: work each one out before you see the choices, " +
            "say \"I don't know\" when you don't, and get no answers until the end. Then you get an estimated score" +
            (info.Placements.Count > 0 ? " and the courses it would place you into." : "."), 13, FontWeights.Normal, TextMain);
        d.Margin = new Thickness(0, 6, 0, 0);
        sp.Children.Add(d);
        var hist = ExamHistory(currentClass);
        if (hist.Count > 0)
        {
            var h = T("Your scores: " + string.Join("  →  ", hist.Skip(Math.Max(0, hist.Count - 6))) + "     best " + hist.Max(), 13, FontWeights.SemiBold, Good);
            h.Margin = new Thickness(0, 8, 0, 0);
            sp.Children.Add(h);
        }
        var b = BigButton("TAKE THE PRACTICE EXAM", () => StartExam(info));
        b.Margin = new Thickness(0, 12, 0, 0);
        sp.Children.Add(b);
        return new Border
        {
            CornerRadius = new CornerRadius(16), Background = Hex("#1AC9A13B"), BorderBrush = Hex("#55C9A13B"),
            BorderThickness = new Thickness(1), Padding = new Thickness(16, 14, 16, 16), Margin = new Thickness(0, 18, 0, 0),
            Child = sp
        };
    }

    void StartExam(ExamInfo info)
    {
        StopTalking();
        exam = info;
        examClass = currentClass;
        quizClass = currentClass;
        var bank = Bank_();
        var sections = info.Sections.Count > 0 ? info.Sections : new List<ExamSection>
        {
            new ExamSection { Name = "All topics", Topics = new HashSet<string>(bank.Select(q => q.Topic), StringComparer.OrdinalIgnoreCase) }
        };
        var pools = sections.Select(s => bank.Where(q => s.Topics.Contains(q.Topic)).ToList()).ToList();
        int poolSum = pools.Sum(p => p.Count);
        int total = Math.Min(info.Count, poolSum);

        // Share the questions out by section size, at least one from every section
        var take = new int[pools.Count];
        var exact = new double[pools.Count];
        for (int i = 0; i < pools.Count; i++)
        {
            exact[i] = poolSum == 0 ? 0 : (double)total * pools[i].Count / poolSum;
            take[i] = Math.Min(pools[i].Count, Math.Max(pools[i].Count > 0 ? 1 : 0, (int)Math.Floor(exact[i])));
        }
        while (take.Sum() < total)
        {
            int best = -1;
            for (int i = 0; i < pools.Count; i++)
                if (take[i] < pools[i].Count && (best < 0 || exact[i] - take[i] > exact[best] - take[best])) best = i;
            if (best < 0) break;
            take[best]++;
        }
        while (take.Sum() > total)
        {
            int best = -1;
            for (int i = 0; i < pools.Count; i++)
                if (take[i] > 1 && (best < 0 || exact[i] - take[i] < exact[best] - take[best])) best = i;
            if (best < 0) break;
            take[best]--;
        }

        // Within a section, spread the picks over its topics. Sections stay in order, easier material first.
        examQs = new List<Q>();
        for (int i = 0; i < pools.Count; i++)
        {
            var groups = Shuffle(pools[i].GroupBy(q => q.Topic).Select(g => new Queue<Q>(Shuffle(g))));
            var picked = new List<Q>();
            while (picked.Count < take[i] && groups.Any(g => g.Count > 0))
                foreach (var g in groups)
                    if (g.Count > 0 && picked.Count < take[i]) picked.Add(g.Dequeue());
            examQs.AddRange(picked.Select(q => Shuffled(q)));
        }
        if (examQs.Count == 0) return;

        examPicks = Enumerable.Repeat(-2, examQs.Count).ToArray();
        examIdx = 0;
        examClock.Reset();
        examClock.Start();
        if (examTick == null)
        {
            examTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            examTick.Tick += (s, e) =>
            {
                if (mode != "exam") { examTick.Stop(); return; }
                if (examClockLabel != null) examClockLabel.Text = ExamClockText();
            };
        }
        examTick.Start();
        ShowExamQuestion();
        Say(Pick(new[]
        {
            "Practice exam. Work each one out before you look at the choices. No peeking.",
            "Okay, test conditions. Solve it first, then check the choices. And no answers till the end.",
            "Exam mode. If you don't know one, just say so. Guessing only lies to you."
        }));
    }

    string ExamClockText()
    {
        var t = examClock.Elapsed;
        return "elapsed " + (int)t.TotalMinutes + ":" + t.Seconds.ToString("00");
    }

    void ShowExamQuestion()
    {
        mode = "exam";
        examRevealed = false;
        ScratchNewQuestion();
        var q = examQs[examIdx];
        int n = examQs.Count;
        var sp = new StackPanel();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(T(exam.Title.ToUpper(), 12, FontWeights.Bold, Gold));
        examClockLabel = T(ExamClockText(), 12, FontWeights.SemiBold, Muted);
        Grid.SetColumn(examClockLabel, 1);
        head.Children.Add(examClockLabel);
        sp.Children.Add(head);

        var bar = new Grid { Height = 6, Margin = new Thickness(0, 8, 0, 10) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(examIdx, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(n - examIdx, GridUnitType.Star) });
        var back = new Border { CornerRadius = new CornerRadius(3), Background = Hex("#1EFFFFFF") };
        Grid.SetColumnSpan(back, 2);
        bar.Children.Add(back);
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = Grad("#E8C77A", "#B8860B") });
        sp.Children.Add(bar);

        string section = exam.SectionOf(q.Topic);
        var meta = T("QUESTION " + (examIdx + 1) + " / " + n + (section != null ? "   ·   " + section.ToUpper() : ""), 13, FontWeights.SemiBold, Muted);
        meta.FontFamily = TitleFont;
        sp.Children.Add(meta);

        string prompt, code;
        SplitCode(q.Text, out prompt, out code);
        var ql = T(prompt, 21, FontWeights.SemiBold, TextMain);
        ql.Margin = new Thickness(0, 12, 0, code == null ? 16 : 10);
        sp.Children.Add(ql);
        if (code != null) sp.Children.Add(CodeBox(code));
        calcBody = null;
        if (q.Calculator) sp.Children.Add(CalculatorPanel());

        var rv = new StackPanel();
        rv.Children.Add(T("Work it out on scratch paper first. The real test makes you type the answer, so no choices to lean on.", 13, FontWeights.Normal, Muted));
        var reveal = BigButton("I HAVE MY ANSWER  →  SHOW CHOICES", RevealExamChoices);
        reveal.Margin = new Thickness(0, 12, 0, 0);
        rv.Children.Add(reveal);
        examRevealRow = new Border { Child = rv };
        sp.Children.Add(examRevealRow);

        var cs = new StackPanel();
        examCards = new List<Border>();
        for (int i = 0; i < q.Options.Length; i++)
        {
            int k = i;
            var card = Choice((i + 1).ToString(), q.Options[i], null, () => ExamPick(k));
            examCards.Add(card);
            cs.Children.Add(card);
        }
        examChoices = new Border { Child = cs, Visibility = Visibility.Collapsed };
        sp.Children.Add(examChoices);

        var row = new UniformGrid { Columns = 2, Margin = new Thickness(-5, 10, -5, 0) };
        row.Children.Add(SecondaryButton("I don't know", () => ExamPick(-1)));
        Btn end = null;
        bool armed = false;
        end = SecondaryButton("end exam early", () =>
        {
            if (!armed) { armed = true; ((TextBlock)end.Child).Text = "click again to end"; return; }
            ShowExamResults();
        });
        row.Children.Add(end);
        sp.Children.Add(row);

        SetScreen(sp);
        React("think", 2);
    }

    void RevealExamChoices()
    {
        if (mode != "exam" || examRevealed) return;
        examRevealed = true;
        examRevealRow.Visibility = Visibility.Collapsed;
        examChoices.Visibility = Visibility.Visible;
        examChoices.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
    }

    void ExamPick(int i)
    {
        if (mode != "exam" || examPicks[examIdx] != -2) return;
        var q = examQs[examIdx];
        examPicks[examIdx] = i;
        bool right = i == q.Answer;
        stats.Answered++;
        if (right) stats.Correct++;
        mastery.Record(examClass, q, right);
        stats.Save();
        if (i >= 0 && i < examCards.Count) { examCards[i].BorderBrush = Gold; examCards[i].Background = Hex("#22C9A13B"); }
        // No right/wrong yet; just move on
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(i >= 0 ? 260 : 80) };
        t.Tick += (s, e) =>
        {
            t.Stop();
            if (mode != "exam") return;
            examIdx++;
            if (examIdx < examQs.Count) ShowExamQuestion(); else ShowExamResults();
        };
        t.Start();
    }

    void ShowExamResults()
    {
        mode = "examresults";
        examClock.Stop();
        if (examTick != null) examTick.Stop();
        int n = examQs.Count;
        int correct = Enumerable.Range(0, n).Count(k => examPicks[k] == examQs[k].Answer);
        int score = (int)Math.Round(100.0 * correct / n);
        int secs = (int)examClock.Elapsed.TotalSeconds;
        var prev = ExamHistory(examClass);
        LogExam(score, correct, n, secs);
        int xp = correct * 4;
        stats.Xp += xp;
        stats.Save();
        UpdateHud(true);

        var sp = new StackPanel();
        var cap = T("ESTIMATED SCORE", 14, FontWeights.Bold, Gold);
        cap.FontFamily = TitleFont;
        cap.TextAlignment = TextAlignment.Center;
        sp.Children.Add(cap);
        string a = score >= 76 ? "#6EE7B7" : score >= 46 ? "#93C5FD" : score >= 30 ? "#FDE047" : "#FDA4AF";
        string b = score >= 76 ? "#10B981" : score >= 46 ? "#6366F1" : score >= 30 ? "#EAB308" : "#E11D48";
        sp.Children.Add(new TextBlock
        {
            Text = score.ToString(), FontSize = 96, FontWeight = FontWeights.Bold, FontFamily = TitleFont, Foreground = Grad(a, b),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -8, 0, -10)
        });
        var line = T("out of 100  ·  " + correct + " of " + n + " right  ·  " + (secs / 60) + " min  ·  +" + xp + " XP", 15, FontWeights.Normal, Muted);
        line.TextAlignment = TextAlignment.Center;
        sp.Children.Add(line);
        if (prev.Count > 0)
        {
            int last = prev[prev.Count - 1], diff = score - last;
            var pl = T("last time " + last + (diff > 0 ? "   ▲ up " + diff : diff < 0 ? "   ▼ down " + (-diff) : "   same as before"), 15, FontWeights.SemiBold,
                diff > 0 ? Good : diff < 0 ? Bad : Muted);
            pl.TextAlignment = TextAlignment.Center;
            pl.Margin = new Thickness(0, 4, 0, 0);
            sp.Children.Add(pl);
        }

        // Placement
        string placedInto = null;
        if (exam.Placements.Count > 0)
        {
            sp.Children.Add(Label("WHERE THIS SCORE PLACES YOU"));
            var reached = exam.Placements.Where(p => p.Key <= score).ToList();
            var next = exam.Placements.FirstOrDefault(p => p.Key > score);
            var box = new StackPanel();
            if (reached.Count > 0)
            {
                placedInto = reached[reached.Count - 1].Value;
                box.Children.Add(T(placedInto, 19, FontWeights.Bold, Good));
                box.Children.Add(T("(and anything below it on the chart)", 12, FontWeights.Normal, Muted));
            }
            if (next.Value != null)
            {
                var nx = T("Next: " + next.Value + " at " + next.Key + "+, " + (next.Key - score) + " more points", 14, FontWeights.SemiBold, Gold);
                nx.Margin = new Thickness(0, 8, 0, 0);
                box.Children.Add(nx);
            }
            box.Children.Add(new Border { Height = 1, Background = CardStroke, Margin = new Thickness(0, 10, 0, 6) });
            foreach (var p in exam.Placements)
            {
                bool got = p.Key <= score, here = reached.Count > 0 && p.Key == reached[reached.Count - 1].Key;
                var r = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
                r.ColumnDefinitions.Add(new ColumnDefinition());
                r.Children.Add(T((got ? "✓ " : "   ") + p.Key + "+", 13, here ? FontWeights.Bold : FontWeights.Normal, here ? Gold : got ? Good : Muted));
                var c = T(p.Value, 13, here ? FontWeights.Bold : FontWeights.Normal, here ? Gold : got ? TextMain : Muted);
                Grid.SetColumn(c, 1);
                r.Children.Add(c);
                box.Children.Add(r);
            }
            sp.Children.Add(ExamBox(box));
        }

        // Sections, weakest first
        sp.Children.Add(Label("BY SECTION  ·  WEAKEST FIRST"));
        var bySection = Enumerable.Range(0, n)
            .GroupBy(k => exam.SectionOf(examQs[k].Topic) ?? examQs[k].Topic)
            .Select(g => new { Name = g.Key, Asked = g.Count(), Right = g.Count(k => examPicks[k] == examQs[k].Answer) })
            .OrderBy(s => (double)s.Right / s.Asked).ThenBy(s => s.Asked)
            .ToList();
        var secBox = new StackPanel();
        foreach (var s in bySection)
        {
            double frac = (double)s.Right / s.Asked;
            var g = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(T(s.Name + (frac < 0.7 ? "   ← study this" : ""), 14, FontWeights.SemiBold, frac < 0.7 ? Fire : TextMain));
            var cnt = T(s.Right + " / " + s.Asked, 14, FontWeights.SemiBold, frac < 0.7 ? Fire : Good);
            Grid.SetColumn(cnt, 1);
            g.Children.Add(cnt);
            secBox.Children.Add(g);
            var track = new Grid { Height = 6, Margin = new Thickness(0, 4, 0, 6) };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(s.Right, GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(s.Asked - s.Right, GridUnitType.Star) });
            var tb = new Border { CornerRadius = new CornerRadius(3), Background = Hex("#1EFFFFFF") };
            Grid.SetColumnSpan(tb, 2);
            track.Children.Add(tb);
            track.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = frac < 0.7 ? Fire : Good });
            secBox.Children.Add(track);
        }
        sp.Children.Add(ExamBox(secBox));

        // Review
        var missed = Enumerable.Range(0, n).Where(k => examPicks[k] != examQs[k].Answer).ToList();
        sp.Children.Add(Label(missed.Count == 0 ? "NOTHING MISSED" : "WHAT YOU MISSED (" + missed.Count + ")  ·  WORK THESE AGAIN TOMORROW"));
        foreach (int k in missed)
        {
            var q = examQs[k];
            var ms = new StackPanel();
            string mPrompt, mCode;
            SplitCode(q.Text, out mPrompt, out mCode);
            ms.Children.Add(T(mPrompt, 15, FontWeights.SemiBold, TextMain));
            if (mCode != null)
            {
                var cb = CodeBox(mCode);
                cb.Margin = new Thickness(0, 8, 0, 0);
                ms.Children.Add(cb);
            }
            int pick = examPicks[k];
            var you = T("you: " + (pick >= 0 ? q.Options[pick] : pick == -1 ? "I don't know" : "(not reached)"), 14, FontWeights.Normal, Bad);
            you.Margin = new Thickness(0, 7, 0, 0);
            ms.Children.Add(you);
            ms.Children.Add(T("answer: " + q.Options[q.Answer], 14, FontWeights.Normal, Good));
            if (q.HasWork)
            {
                var wc = WorkedSolution(q);
                wc.Margin = new Thickness(0, 8, 0, 0);
                ms.Children.Add(wc);
            }
            var why = T(q.Why, 13.5, FontWeights.Normal, Muted);
            why.Margin = new Thickness(0, 6, 0, 0);
            ms.Children.Add(why);
            Btn note = null;
            var qq = q;
            note = SecondaryButton("+ add to notes", () => AddToNotes(qq, note));
            note.Margin = new Thickness(0, 10, 0, 0);
            note.HorizontalAlignment = HorizontalAlignment.Left;
            note.Padding = new Thickness(14, 8, 14, 8);
            ms.Children.Add(note);
            sp.Children.Add(ExamBox(ms));
        }

        var again = BigButton("TAKE ANOTHER PRACTICE EXAM", () => StartExam(exam));
        sp.Children.Add(again);
        var row = new UniformGrid { Columns = 2, Margin = new Thickness(-5, 8, -5, 0) };
        row.Children.Add(SecondaryButton("← back to the class", ShowTopics));
        row.Children.Add(SecondaryButton("home", ShowHome));
        sp.Children.Add(row);
        var fine = T("This is an estimate from a " + n + "-question practice test, not an official score. Each one draws a new mix, so take a few and watch the trend.",
            12, FontWeights.Normal, Muted);
        fine.TextAlignment = TextAlignment.Center;
        fine.Margin = new Thickness(0, 12, 0, 0);
        sp.Children.Add(fine);

        SetScreen(sp);
        string verdict = score >= 85 ? "Okay. That's actually really good." :
                         score >= 70 ? "Solid. A little more and you're set." :
                         score >= 50 ? "Not bad. Look at the weak sections and hit those." :
                         "Rough one. That's fine, that's what practice is for. Start with the weakest section.";
        if (placedInto != null) verdict += " That would place you into " + placedInto.Split(',')[0].Trim() + ".";
        React(score >= 70 ? "fist" : "think", 2.4);
        Say(verdict);
    }

    static Border ExamBox(UIElement child)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(14), Background = CardBg, BorderBrush = CardStroke,
            BorderThickness = new Thickness(1), Padding = new Thickness(16, 13, 16, 14),
            Margin = new Thickness(0, 0, 0, 10), Child = child
        };
    }

    // For the snapshot: answer the rest of the exam (most of it right) and show the results
    void FinishExamForDemo()
    {
        if (examQs == null) return;
        for (int k = 0; k < examQs.Count; k++)
            if (examPicks[k] == -2) examPicks[k] = k % 5 == 3 ? -1 : k % 4 == 1 ? (examQs[k].Answer + 1) % examQs[k].Options.Length : examQs[k].Answer;
        ShowExamResults();
    }

    void SelfTestExams(StringBuilder log, Action<string> bad)
    {
        foreach (var kv in classes)
        {
            var info = ExamFor(kv.Key);
            if (info == null) continue;
            var topics = new HashSet<string>(kv.Value.Select(q => q.Topic), StringComparer.OrdinalIgnoreCase);
            foreach (var s in info.Sections)
                foreach (var t in s.Topics)
                    if (!topics.Contains(t)) bad(kv.Key + ": exam section \"" + s.Name + "\" lists a topic with no questions: " + t);
            int inExam = kv.Value.Count(q => info.Sections.Count == 0 || info.SectionOf(q.Topic) != null);
            var left = topics.Where(t => info.Sections.Count > 0 && info.SectionOf(t) == null).ToList();
            if (inExam < info.Count) bad(kv.Key + ": exam wants " + info.Count + " questions but only " + inExam + " fit its sections");
            log.AppendLine("Exam in " + kv.Key + ": " + info.Count + " questions from " + inExam + ", " + info.Sections.Count + " sections, " +
                info.Placements.Count + " placement rows" + (left.Count > 0 ? "; not on the exam: " + string.Join(", ", left) : ""));
        }
    }
}
