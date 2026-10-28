using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Speech.Synthesis;
using System.Text;
using System.Threading;
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

class Q
{
    public string Topic, Text, Why;
    public string[] Options;
    public int Answer;
    public int Seconds; // from a "Time:" line; 0 = estimate it (see Timing)
}

// How long a question should reasonably take: reading the choices plus thinking time that depends on
// what kind of question it is (recall, formula, code tracing, or a multi-step calculation).
static class Timing
{
    // Calculus and other multi-step work: worth extra time on top of the arithmetic itself
    static readonly string[] Calc =
    {
        "∫", "lim", "d/dx", "derivative", "series", "converge", "taylor", "maclaurin", "eigenvalue", "determinant",
        "inverse of this", "compute ab", "rotated", "volume", "tangent line", "critical point", "inflection",
        "linear approximation", "l'hôpital", "antiderivative", "z-score for", "standard deviation of"
    };
    // Word problems: you have to set up the equation before you can solve it
    static readonly string[] Setup =
    {
        "how fast", "how far", "how much", "how long", "at what rate", "largest possible", "grows at", "per second",
        "meters", "newtons", "joules", "volts", "ohms", "watts", "coulomb", "farad", " kg", "how many ways",
        "how many bit", "how many edges", "committee", "arranged", "subnet", "hosts"
    };
    // Code you have to trace in your head, even when it's on the same line as the question
    static readonly string[] InlineCode =
    {
        "print(", "len(", "range(", "split(", "sort(", "[x ", "s[", "d[", "{\"", "==", "//", " % ", "**", "select ", "where "
    };

    public static int For(string cls, Q q)
    {
        if (q.Seconds > 0) return q.Seconds;
        string text = q.Text ?? "", lower = text.ToLowerInvariant();
        cls = (cls ?? "").ToLowerInvariant();
        bool quant = cls.StartsWith("math") || cls.StartsWith("physics") || cls.StartsWith("stats");

        // The timer starts after she reads the question aloud, so mostly count reading the choices
        double secs = q.Options.Sum(o => Words(o)) / 3.2 + Words(text) / 9.0;

        int codeLines = text.Split('\n').Length - 1;
        // How much math is actually in the question: digits, operators and math symbols
        int load = text.Count(c => char.IsDigit(c) || "+-−×÷*/^=<>²³ⁿ√∑∫π".IndexOf(c) >= 0);
        bool calc = Calc.Any(k => lower.Contains(k));
        bool setup = Setup.Any(k => lower.Contains(k)) && text.Count(char.IsDigit) >= 2;
        bool inline = InlineCode.Any(k => lower.Contains(k));

        if (lower.StartsWith("formula check") || lower.StartsWith("must-know")) secs += 10 + (setup ? 15 : 0);
        else if (codeLines > 0) secs += quant ? 15 + 2.5 * load + (calc ? 15 : 0) : 12 + 3 * codeLines;
        else if (setup) secs += 35 + 2 * load + (calc ? 20 : 0);
        else if (quant && load > 0) secs += 12 + 2.5 * load + (calc ? 15 : 0) + (lower.Contains("∫") && lower.Contains(" to ") ? 15 : 0);
        else if (inline) secs += 15;
        else if (quant && calc) secs += 15;
        else if (load >= 3) secs += 15;
        else secs += 7;

        secs = Math.Max(10, Math.Min(180, secs));
        return (int)(Math.Round(secs / 5) * 5);
    }

    static int Words(string s) { return string.IsNullOrEmpty(s) ? 0 : s.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length; }

    public static string Clock(double secs)
    {
        int s = (int)Math.Ceiling(Math.Max(0, secs));
        return s >= 60 ? (s / 60) + ":" + (s % 60).ToString("00") : s + "s";
    }
}

class Missed
{
    public Q Question;
    public string Yours;
}

// Marker type so dragging the widget ignores presses that start on a button
class Btn : Border { }

static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    [STAThread]
    static void Main(string[] args)
    {
        // Debug mode: render screenshots off-screen and exit
        if (args.Length == 2 && args[0] == "--snap")
        {
            var snapApp = new Application();
            var snapWin = new QuizWindow(args[1]);
            snapApp.Run(snapWin);
            return;
        }

        bool created;
        var mutex = new Mutex(true, "quizzahh_single_instance", out created);
        if (!created)
        {
            // Already running (maybe hidden in the tray): ask it to show itself. We were just started
            // by the user, so we may hand it the right to come to the front.
            try { AllowSetForegroundWindow(-1); } catch { }
            try { EventWaitHandle.OpenExisting("quizzahh_show").Set(); } catch { }
            return;
        }
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "quizzahh_show");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var win = new QuizWindow(null);
        win.Closed += (s, e) => app.Shutdown();
        var listener = new Thread(() =>
        {
            while (true)
            {
                showSignal.WaitOne();
                win.Dispatcher.BeginInvoke(new Action(win.ShowWidget));
            }
        });
        listener.IsBackground = true;
        listener.Start();
        if (args.Contains("--startup")) win.StartInBackground(); else win.ShowWidget();
        app.Run();
        GC.KeepAlive(mutex);
    }
}

class Stats
{
    public static bool ReadOnly;
    public int Xp, BestStreak, Answered, Correct;
    public double Left = double.NaN, Top = double.NaN;
    public bool Pinned, Muted, Quiet, NotesOpen, BuddyOff;
    public double BuddyX = double.NaN;
    public string LastClass = "", VoiceName = "", ScratchPaper = "grid";
    public int YearFilter; // 0 = all years

    static string Dir
    {
        get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "quizzahh"); }
    }

    static string FilePath { get { return System.IO.Path.Combine(Dir, "stats.txt"); } }

    public static Stats Load()
    {
        var s = new Stats();
        var ci = CultureInfo.InvariantCulture;
        try
        {
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var p = line.Split(new[] { '=' }, 2);
                if (p.Length < 2) continue;
                switch (p[0])
                {
                    case "xp": s.Xp = int.Parse(p[1]); break;
                    case "best": s.BestStreak = int.Parse(p[1]); break;
                    case "answered": s.Answered = int.Parse(p[1]); break;
                    case "correct": s.Correct = int.Parse(p[1]); break;
                    case "left": s.Left = double.Parse(p[1], ci); break;
                    case "top": s.Top = double.Parse(p[1], ci); break;
                    case "pinned": s.Pinned = p[1] == "1"; break;
                    case "muted": s.Muted = p[1] == "1"; break;
                    case "quiet": s.Quiet = p[1] == "1"; break;
                    case "notes": s.NotesOpen = p[1] == "1"; break;
                    case "class": s.LastClass = p[1]; break;
                    case "buddyx": s.BuddyX = double.Parse(p[1], ci); break;
                    case "buddyoff": s.BuddyOff = p[1] == "1"; break;
                    case "voicename": s.VoiceName = p[1]; break;
                    case "paper": s.ScratchPaper = p[1]; break;
                    case "year": s.YearFilter = int.Parse(p[1]); break;
                }
            }
        }
        catch { }
        return s;
    }

    public void Save()
    {
        if (ReadOnly) return;
        var ci = CultureInfo.InvariantCulture;
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllLines(FilePath, new[]
            {
                "xp=" + Xp, "best=" + BestStreak, "answered=" + Answered, "correct=" + Correct,
                "left=" + Left.ToString(ci), "top=" + Top.ToString(ci),
                "pinned=" + (Pinned ? 1 : 0), "muted=" + (Muted ? 1 : 0), "quiet=" + (Quiet ? 1 : 0),
                "notes=" + (NotesOpen ? 1 : 0), "class=" + LastClass,
                "buddyx=" + BuddyX.ToString(ci), "buddyoff=" + (BuddyOff ? 1 : 0), "voicename=" + VoiceName, "paper=" + ScratchPaper, "year=" + YearFilter
            });
        }
        catch { }
    }
}

// Reads and writes the plain-text question files (one file per class)
static class QuestionFile
{
    public const string Header =
        "# quizzahh question file. The file name is the class name.\r\n" +
        "#\r\n" +
        "# Each question looks like this (leave a blank line between questions):\r\n" +
        "#\r\n" +
        "#   Q: What is the powerhouse of the cell?\r\n" +
        "#   Topic: Cells\r\n" +
        "#   - Nucleus\r\n" +
        "#   * Mitochondria        <- the star marks the correct answer\r\n" +
        "#   - Ribosome\r\n" +
        "#   - Golgi apparatus\r\n" +
        "#   Why: Mitochondria make ATP, the cell's energy.\r\n" +
        "#\r\n" +
        "# Lines starting with # are ignored.\r\n";

    public static List<Q> Read(string path)
    {
        var list = new List<Q>();
        Q cur = null;
        var opts = new List<string>();
        int ans = -1;
        Action flush = () =>
        {
            if (cur != null && !string.IsNullOrWhiteSpace(cur.Text) && opts.Count >= 2 && ans >= 0)
            {
                cur.Options = opts.ToArray();
                cur.Answer = ans;
                if (string.IsNullOrWhiteSpace(cur.Topic)) cur.Topic = "General";
                if (cur.Why == null) cur.Why = "";
                list.Add(cur);
            }
            cur = null;
        };
        // Lines right after "Q:" (before the topic or choices) belong to the question, keeping their
        // indentation, so a question can include a few lines of code.
        bool inText = false;
        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            if (Starts(line, "Q:"))
            {
                flush();
                cur = new Q { Text = After(line) };
                opts = new List<string>();
                ans = -1;
                inText = true;
            }
            else if (cur == null) continue;
            else if (Starts(line, "Topic:")) { cur.Topic = After(line); inText = false; }
            else if (Starts(line, "Why:")) { cur.Why = After(line); inText = false; }
            else if (Starts(line, "Time:")) { cur.Seconds = ParseTime(After(line)); inText = false; }
            else if (line.StartsWith("*")) { inText = false; if (ans < 0) ans = opts.Count; opts.Add(line.Substring(1).Trim()); }
            else if (line.StartsWith("-")) { inText = false; opts.Add(line.Substring(1).Trim()); }
            else if (inText) cur.Text += "\n" + raw.TrimEnd().Replace("\t", "    ");
            else if (cur.Why != null) cur.Why += " " + line;
        }
        flush();
        return list;
    }

    // Which year of the degree a class belongs to: a "Year: 2" line above the first question (0 = not set)
    public static int ReadYear(string path)
    {
        try
        {
            foreach (var raw in File.ReadLines(path, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (Starts(line, "Q:")) break;
                if (!Starts(line, "Year:")) continue;
                int y;
                if (int.TryParse(After(line).Trim(), out y) && y >= 1 && y <= 6) return y;
            }
        }
        catch { }
        return 0;
    }

    // "90", "90s" or "1:30" -> seconds (0 if it can't be read)
    static int ParseTime(string s)
    {
        s = s.Trim().TrimEnd('s', 'S').Trim();
        int m, sec;
        var p = s.Split(':');
        if (p.Length == 2 && int.TryParse(p[0], out m) && int.TryParse(p[1], out sec)) return m * 60 + sec;
        return int.TryParse(s, out sec) && sec > 0 ? Math.Min(sec, 900) : 0;
    }

    static bool Starts(string line, string tag) { return line.StartsWith(tag, StringComparison.OrdinalIgnoreCase); }
    static string After(string line) { return line.Substring(line.IndexOf(':') + 1).Trim(); }

    public static string Format(Q q)
    {
        var sb = new StringBuilder();
        sb.Append("Q: ").Append(q.Text).Append("\r\n");
        sb.Append("Topic: ").Append(q.Topic).Append("\r\n");
        if (q.Seconds > 0) sb.Append("Time: ").Append(q.Seconds).Append("\r\n");
        for (int i = 0; i < q.Options.Length; i++)
            sb.Append(i == q.Answer ? "* " : "- ").Append(q.Options[i]).Append("\r\n");
        if (!string.IsNullOrWhiteSpace(q.Why)) sb.Append("Why: ").Append(q.Why).Append("\r\n");
        return sb.ToString();
    }

    public static void WriteAll(string path, IEnumerable<Q> qs)
    {
        var sb = new StringBuilder(Header);
        foreach (var q in qs) sb.Append("\r\n").Append(Format(q));
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    public static void Append(string path, Q q)
    {
        string text = (File.Exists(path) ? "" : Header) + "\r\n" + Format(q);
        File.AppendAllText(path, text, Encoding.UTF8);
    }
}

// Little synthesized sound effects, generated in memory
static class Sfx
{
    public static bool Muted;
    public static readonly byte[] Click = Tone(false, 0.12, 1400, 0.025);
    public static readonly byte[] Correct = Tone(false, 0.3, 523.25, 0.12, 783.99, 0.4);
    public static readonly byte[] Combo = Tone(false, 0.3, 523.25, 0.08, 659.25, 0.08, 1046.5, 0.4);
    public static readonly byte[] Wrong = Static(0.45, 0.28);

    // Radio static, like the radio in Silent Hill when something's close
    static byte[] Static(double dur, double vol)
    {
        const int rate = 22050;
        var r = new Random();
        var samples = new List<short>();
        int len = (int)(rate * dur);
        double last = 0;
        for (int k = 0; k < len; k++)
        {
            double t = (double)k / rate;
            double crackle = Math.Pow(0.5 + 0.5 * Math.Sin(t * 2 * Math.PI * 11 + Math.Sin(t * 40)), 2);
            double env = Math.Min(1, k / (rate * 0.01)) * Math.Exp(-1.2 * t / dur) * (0.4 + 0.6 * crackle);
            last = 0.6 * last + 0.4 * (r.NextDouble() * 2 - 1);
            double s = last + 0.15 * Math.Sin(2 * Math.PI * 60 * t);
            samples.Add((short)(Math.Max(-1, Math.Min(1, s)) * env * vol * short.MaxValue));
        }
        return Wav(samples);
    }
    public static readonly byte[] LevelUp = Tone(false, 0.35, 523.25, 0.08, 659.25, 0.08, 783.99, 0.08, 1046.5, 0.3);

    static byte[] Tone(bool harsh, double vol, params double[] seq)
    {
        const int rate = 22050;
        var samples = new List<short>();
        for (int n = 0; n < seq.Length; n += 2)
        {
            double f = seq[n], d = seq[n + 1];
            int len = (int)(rate * d);
            for (int k = 0; k < len; k++)
            {
                double t = (double)k / rate;
                double env = Math.Min(1, k / (rate * 0.004)) * Math.Exp(-3.0 * t / d);
                double s = Math.Sin(2 * Math.PI * f * t);
                s = harsh ? 0.6 * Math.Sign(s) + 0.4 * s : 0.8 * s + 0.2 * Math.Sin(4 * Math.PI * f * t);
                samples.Add((short)(s * env * vol * short.MaxValue));
            }
        }
        return Wav(samples);
    }

    static byte[] Wav(List<short> samples)
    {
        const int rate = 22050;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            int dataLen = samples.Count * 2;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataLen);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
            w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2);
            w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataLen);
            foreach (var s in samples) w.Write(s);
            w.Flush();
            return ms.ToArray();
        }
    }

    public static void Play(byte[] wav)
    {
        if (Muted) return;
        try { new SoundPlayer(new MemoryStream(wav)).Play(); } catch { }
    }
}


// Tracks which questions you've learned: one counts as learned once you get it right
// twice in a row (a wrong answer resets it). Saved per class in %AppData%\quizzahh\mastery.txt
class Mastery
{
    public const int Needed = 2;
    readonly Dictionary<string, int> streaks = new Dictionary<string, int>();

    static string FilePath
    {
        get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "quizzahh", "mastery.txt"); }
    }

    // One line per question in the file, so line breaks (questions with code) are written as \n
    public static string Key(string cls, Q q)
    {
        return cls + "|" + q.Text.Trim().Replace("\r", "").Replace("\n", "\\n").Replace("\t", " ");
    }

    public static Mastery Load()
    {
        var m = new Mastery();
        try
        {
            foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                int tab = line.LastIndexOf('\t');
                int n;
                // Every real entry starts with "class|"; anything else is a fragment from an older save
                if (tab > 0 && line.IndexOf('|') > 0 && line.IndexOf('|') < tab && int.TryParse(line.Substring(tab + 1), out n))
                    m.streaks[line.Substring(0, tab)] = n;
            }
        }
        catch { }
        return m;
    }

    void Save()
    {
        if (Stats.ReadOnly) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
            File.WriteAllLines(FilePath, streaks.Select(kv => kv.Key + "\t" + kv.Value), Encoding.UTF8);
        }
        catch { }
    }

    // Returns true if this answer just made the question count as learned
    public bool Record(string cls, Q q, bool correct)
    {
        string k = Key(cls, q);
        int before;
        streaks.TryGetValue(k, out before);
        int after = correct ? before + 1 : 0;
        streaks[k] = after;
        Save();
        return before < Needed && after >= Needed;
    }

    public bool IsLearned(string cls, Q q)
    {
        int n;
        return streaks.TryGetValue(Key(cls, q), out n) && n >= Needed;
    }

    public int LearnedCount(string cls, IEnumerable<Q> qs) { return qs.Count(q => IsLearned(cls, q)); }
}