using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

// A scientific calculator for questions marked "Calculator: yes" (the ones a real placement test
// would hand you a calculator for). Type an expression like 5000(1 + 0.06/12)^48 and press Enter.
static class MathEval
{
    public static bool Degrees = true;

    public static double Eval(string expr, double ans)
    {
        var p = new Parser(Normalize(expr), ans);
        double v = p.Expr();
        p.Ws();
        if (p.i < p.s.Length) throw new FormatException("didn't expect \"" + p.s[p.i] + "\"");
        if (double.IsNaN(v) || double.IsInfinity(v)) throw new FormatException("undefined (like dividing by 0 or a log of a negative)");
        return v;
    }

    public static string Format(double v)
    {
        if (Math.Abs(v) < 1e-12) return "0";
        double r = double.Parse(v.ToString("G10", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (Math.Abs(r) >= 1e15 || Math.Abs(r) < 1e-6) return r.ToString("0.#########E+0", CultureInfo.InvariantCulture);
        return r.ToString("#,0.##########", CultureInfo.InvariantCulture);
    }

    static string Normalize(string s)
    {
        return s.Replace("×", "*").Replace("·", "*").Replace("÷", "/").Replace("−", "-").Replace("**", "^")
                .Replace("²", "^2").Replace("³", "^3").Replace(",", "");
    }

    class Parser
    {
        public string s;
        public int i;
        readonly double ans;
        public Parser(string s, double ans) { this.s = s; this.ans = ans; }

        public void Ws() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
        bool Eat(char c) { Ws(); if (i < s.Length && s[i] == c) { i++; return true; } return false; }

        public double Expr()
        {
            double v = Term();
            while (true)
            {
                if (Eat('+')) v += Term();
                else if (Eat('-')) v -= Term();
                else return v;
            }
        }

        double Term()
        {
            double v = Unary();
            while (true)
            {
                if (Eat('*')) v *= Unary();
                else if (Eat('/')) v /= Unary();
                else if (StartsFactor()) v *= Power(); // 2π, 3(4 + 1), 2sin(30)
                else return v;
            }
        }

        bool StartsFactor()
        {
            Ws();
            if (i >= s.Length) return false;
            char c = s[i];
            return char.IsDigit(c) || c == '.' || c == '(' || c == '√' || c == 'π' || char.IsLetter(c);
        }

        double Unary()
        {
            if (Eat('-')) return -Unary();
            if (Eat('+')) return Unary();
            return Power();
        }

        // Right-associative, and binds tighter than a leading minus: -2^2 = -4, 2^-1 = 0.5
        double Power()
        {
            double b = Postfix();
            if (Eat('^')) return Math.Pow(b, Unary());
            return b;
        }

        double Postfix()
        {
            double v = Primary();
            while (true)
            {
                if (Eat('!'))
                {
                    if (v < 0 || v > 170 || v != Math.Floor(v)) throw new FormatException("! needs a whole number from 0 to 170");
                    double f = 1;
                    for (int k = 2; k <= (int)v; k++) f *= k;
                    v = f;
                }
                else if (Eat('%')) v /= 100;
                else return v;
            }
        }

        double Primary()
        {
            Ws();
            if (i >= s.Length) throw new FormatException("it ends too early");
            char c = s[i];
            if (c == '(')
            {
                i++;
                double v = Expr();
                if (!Eat(')')) throw new FormatException("missing )");
                return v;
            }
            if (c == '√') { i++; return Math.Sqrt(Power()); }
            if (c == 'π') { i++; return Math.PI; }
            if (char.IsDigit(c) || c == '.')
            {
                int st = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                if (i < s.Length && (s[i] == 'E') && i + 1 < s.Length && (char.IsDigit(s[i + 1]) || s[i + 1] == '-'))
                {
                    i += 2;
                    while (i < s.Length && char.IsDigit(s[i])) i++;
                }
                double v;
                if (!double.TryParse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    throw new FormatException("\"" + s.Substring(st, i - st) + "\" isn't a number");
                return v;
            }
            if (char.IsLetter(c))
            {
                int st = i;
                while (i < s.Length && char.IsLetter(s[i])) i++;
                string name = s.Substring(st, i - st).ToLowerInvariant();
                switch (name)
                {
                    case "pi": return Math.PI;
                    case "e": return Math.E;
                    case "ans": return ans;
                }
                double a = Power(); // sin 30 and sin(30) both work
                double r = Degrees ? Math.PI / 180 : 1;
                switch (name)
                {
                    case "sin": return Snap(Math.Sin(a * r));
                    case "cos": return Snap(Math.Cos(a * r));
                    case "tan":
                        if (Math.Abs(Math.Cos(a * r)) < 1e-12) throw new FormatException("tan is undefined there");
                        return Snap(Math.Tan(a * r));
                    case "asin": case "arcsin": return Math.Asin(a) / r;
                    case "acos": case "arccos": return Math.Acos(a) / r;
                    case "atan": case "arctan": return Math.Atan(a) / r;
                    case "ln": return Math.Log(a);
                    case "log": return Math.Log10(a);
                    case "sqrt": return Math.Sqrt(a);
                    case "abs": return Math.Abs(a);
                    case "exp": return Math.Exp(a);
                }
                throw new FormatException("unknown function \"" + name + "\"");
            }
            throw new FormatException("didn't expect \"" + c + "\"");
        }

        // sin(180) should be 0, not 1.2e-16
        static double Snap(double v) { return Math.Abs(v) < 1e-13 ? 0 : v; }
    }
}

partial class QuizWindow
{
    TextBox calcInput;
    TextBlock calcResult, calcModeText;
    Border calcBody;
    double calcAns;

    // A collapsible calculator card under the question
    UIElement CalculatorPanel()
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(14), Background = Hex("#14FFFFFF"), BorderBrush = Hex("#33C9A13B"),
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 14), Padding = new Thickness(12, 8, 12, 8)
        };
        var sp = new StackPanel();
        card.Child = sp;

        var head = new Grid { Cursor = Cursors.Hand, Background = Brushes.Transparent };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock
        {
            Text = "", FontFamily = IconFont, FontSize = 14, Foreground = Gold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        });
        var title = T("CALCULATOR ALLOWED", 12, FontWeights.Bold, Gold);
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1);
        head.Children.Add(title);
        var modeBtn = new Btn
        {
            CornerRadius = new CornerRadius(8), Background = CardBg, BorderBrush = CardStroke, BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 2, 8, 3), ToolTip = "Angle mode for sin, cos, tan and their inverses"
        };
        calcModeText = T(MathEval.Degrees ? "DEG" : "RAD", 11, FontWeights.Bold, TextMain);
        modeBtn.Child = calcModeText;
        Clickable(modeBtn, () => { MathEval.Degrees = !MathEval.Degrees; calcModeText.Text = MathEval.Degrees ? "DEG" : "RAD"; }, false);
        Grid.SetColumn(modeBtn, 2);
        head.Children.Add(modeBtn);
        head.MouseLeftButtonUp += (s, e) =>
        {
            if (e.OriginalSource is DependencyObject && IsInside((DependencyObject)e.OriginalSource, modeBtn)) return;
            ToggleCalculator();
        };
        sp.Children.Add(head);

        calcBody = new Border { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 2) };
        var body = new StackPanel();
        calcBody.Child = body;
        calcInput = new TextBox
        {
            FontFamily = CodeFont, FontSize = 17, Foreground = TextMain, CaretBrush = TextMain, Background = FieldBg,
            BorderBrush = CardStroke, BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 6),
            ToolTip = "Type an expression and press Enter, e.g. 5000(1 + 0.06/12)^48"
        };
        calcInput.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter) { CalcEquals(); e.Handled = true; }
        };
        body.Children.Add(calcInput);
        calcResult = T("Type or tap, then = (or Enter)", 15, FontWeights.SemiBold, Muted);
        calcResult.TextAlignment = TextAlignment.Right;
        calcResult.Margin = new Thickness(0, 6, 2, 8);
        calcResult.FontFamily = CodeFont;
        body.Children.Add(calcResult);

        var keys = new[]
        {
            "sin", "cos", "tan", "(", ")",
            "ln", "log", "√", "^", "e",
            "7", "8", "9", "÷", "C",
            "4", "5", "6", "×", "⌫",
            "1", "2", "3", "−", "ans",
            "0", ".", "π", "+", "="
        };
        var grid = new UniformGrid { Columns = 5 };
        foreach (var k in keys) grid.Children.Add(CalcKey(k));
        body.Children.Add(grid);
        var tip = T("Inverse trig: type asin, acos, atan. Also works: abs( ), sqrt( ), 5!, 2π, 3(4 + 1). Enter = equals.", 11, FontWeights.Normal, Muted);
        tip.Margin = new Thickness(2, 6, 0, 0);
        body.Children.Add(tip);
        sp.Children.Add(calcBody);
        return card;
    }

    static bool IsInside(DependencyObject d, DependencyObject target)
    {
        while (d != null) { if (d == target) return true; d = VisualTreeHelper.GetParent(d); }
        return false;
    }

    void ToggleCalculator()
    {
        if (calcBody == null) return;
        bool open = calcBody.Visibility != Visibility.Visible;
        calcBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open) Dispatcher.BeginInvoke(new Action(() => { calcInput.Focus(); calcInput.CaretIndex = calcInput.Text.Length; }));
    }

    Btn CalcKey(string k)
    {
        bool op = "÷×−+^".Contains(k), act = k == "=" || k == "C" || k == "⌫";
        var b = new Btn
        {
            CornerRadius = new CornerRadius(9), Margin = new Thickness(3), Padding = new Thickness(0, 7, 0, 8),
            Background = k == "=" ? Accent : op ? Hex("#22C9A13B") : CardBg, BorderBrush = CardStroke, BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = k, FontSize = k.Length > 2 ? 13 : 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = k == "=" ? Brushes.White : op || act ? Gold : TextMain, FontFamily = CodeFont
            }
        };
        var normal = b.Background;
        b.MouseEnter += (s, e) => { if (k != "=") b.Background = CardHover; };
        b.MouseLeave += (s, e) => b.Background = normal;
        return Clickable(b, () => CalcPress(k), false);
    }

    void CalcPress(string k)
    {
        if (k == "=") { CalcEquals(); }
        else if (k == "C") { calcInput.Text = ""; calcResult.Text = ""; }
        else if (k == "⌫")
        {
            int c = calcInput.CaretIndex;
            if (calcInput.SelectionLength > 0) calcInput.SelectedText = "";
            else if (c > 0) { calcInput.Text = calcInput.Text.Remove(c - 1, 1); calcInput.CaretIndex = c - 1; }
        }
        else
        {
            string ins = k == "sin" || k == "cos" || k == "tan" || k == "ln" || k == "log" ? k + "(" : k == "√" ? "√(" : k;
            int c = calcInput.CaretIndex;
            calcInput.Text = calcInput.Text.Insert(c, ins);
            calcInput.CaretIndex = c + ins.Length;
        }
        calcInput.Focus();
    }

    void CalcEquals()
    {
        string expr = calcInput.Text.Trim();
        if (expr.Length == 0) return;
        try
        {
            calcAns = MathEval.Eval(expr, calcAns);
            calcResult.Text = "= " + MathEval.Format(calcAns);
            calcResult.Foreground = TextMain;
        }
        catch (Exception ex)
        {
            calcResult.Text = "Hmm: " + ex.Message;
            calcResult.Foreground = Bad;
        }
        calcInput.SelectAll();
    }

    // For the snapshot: open the calculator with an expression already worked out
    void DemoCalculator(string expr)
    {
        if (calcBody == null) return;
        calcBody.Visibility = Visibility.Visible;
        calcInput.Text = expr;
        CalcEquals();
    }

    void SelfTestCalc(StringBuilder log, Action<string> bad)
    {
        bool deg = MathEval.Degrees;
        MathEval.Degrees = true;
        var cases = new[]
        {
            Tuple.Create("5000(1 + 0.06/12)^48", 6352.445805),
            Tuple.Create("-2^2", -4.0), Tuple.Create("2^-1", 0.5), Tuple.Create("2^3^2", 512.0),
            Tuple.Create("2π", 2 * Math.PI), Tuple.Create("3(4 + 1)", 15.0), Tuple.Create("8 - 2 × 3 + 4", 6.0),
            Tuple.Create("sin(30)", 0.5), Tuple.Create("atan(50/120)", 22.61986495), Tuple.Create("cos 60", 0.5),
            Tuple.Create("ln(20)/ln(5)", 1.861353116), Tuple.Create("log(1000)", 3.0), Tuple.Create("√(144)", 12.0),
            Tuple.Create("√2^2", 2.0), Tuple.Create("5!", 120.0), Tuple.Create("25%", 0.25), Tuple.Create("1.2E3", 1200.0),
            Tuple.Create("200(0.5)^(24/8)", 25.0), Tuple.Create("e^ln(9)", 9.0)
        };
        int ok = 0;
        foreach (var c in cases)
        {
            try
            {
                double v = MathEval.Eval(c.Item1, 0);
                if (Math.Abs(v - c.Item2) <= 1e-6 * Math.Max(1, Math.Abs(c.Item2))) ok++;
                else bad("calculator: " + c.Item1 + " gave " + v + ", expected " + c.Item2);
            }
            catch (Exception ex) { bad("calculator: " + c.Item1 + " threw " + ex.Message); }
        }
        foreach (var junk in new[] { "2 +", "(3", "foo(2)", "1/0", "ln(-1)" })
        {
            try { MathEval.Eval(junk, 0); bad("calculator: \"" + junk + "\" should be an error"); }
            catch (FormatException) { ok++; }
        }
        MathEval.Degrees = deg;
        log.AppendLine("Calculator checks passed: " + ok + "/" + (cases.Length + 5));
    }
}
