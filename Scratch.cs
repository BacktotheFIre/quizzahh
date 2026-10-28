using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

// Scratch paper: a sheet that pulls down over the middle of the screen so you can work problems out
// by hand with the mouse, a pen (pressure works) or touch. Every question gets a fresh page and the
// old pages are kept like a little notebook, so you can flip back. Pages are saved between runs.
partial class QuizWindow
{
    Grid scratchSheet;
    Border scratchTab;
    TranslateTransform scratchSlide;
    InkCanvas ink;
    Border inkFrame;
    Grid paperGrid;
    Rectangle marginLine;
    TextBlock pageLabel, scratchHint;
    bool scratchOpen, scratchLoaded;
    string scratchTool = "pen", paperStyle = "grid";
    Color scratchColor = Color.FromRgb(0xF2, 0xEB, 0xDF);
    double scratchSize = 3.5;
    readonly List<StrokeCollection> pages = new List<StrokeCollection>();
    int pageIdx;
    readonly Dictionary<StrokeCollection, Stack<StrokeCollection[]>> undoStacks = new Dictionary<StrokeCollection, Stack<StrokeCollection[]>>();
    readonly Dictionary<StrokeCollection, Stack<StrokeCollection[]>> redoStacks = new Dictionary<StrokeCollection, Stack<StrokeCollection[]>>();
    bool applyingHistory;
    readonly Dictionary<string, Btn> toolButtons = new Dictionary<string, Btn>();
    readonly List<KeyValuePair<Color, Border>> swatches = new List<KeyValuePair<Color, Border>>();
    readonly List<KeyValuePair<double, Border>> sizeDots = new List<KeyValuePair<double, Border>>();
    Btn paperButton;
    Point shapeStart;
    Stroke shapeStroke;

    const int MaxPages = 40;
    static readonly string ScratchFile = System.IO.Path.Combine(DataDir, "scratch", "pages.ink");

    static readonly Color[] InkColors =
    {
        Color.FromRgb(0xF2, 0xEB, 0xDF), Color.FromRgb(0xE0, 0x4B, 0x3F), Color.FromRgb(0xF0, 0x9A, 0x3E), Color.FromRgb(0xF2, 0xD5, 0x4C),
        Color.FromRgb(0x7F, 0xC9, 0x8A), Color.FromRgb(0x5F, 0xC4, 0xE0), Color.FromRgb(0x7A, 0x9C, 0xF5), Color.FromRgb(0xC5, 0x8B, 0xF2)
    };

    // The tab at the top of the screen plus the sheet itself (built hidden, above Heather)
    void BuildScratch(Grid host, double H, double stageW)
    {
        double sheetW = Math.Max(420, stageW - 24);

        scratchTab = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(0, 0, 14, 14), Background = Hex("#EE160F0E"),
            BorderBrush = Hex("#66C23B30"), BorderThickness = new Thickness(1, 0, 1, 1),
            Padding = new Thickness(18, 7, 20, 9), Cursor = Cursors.Hand, ToolTip = "Draw it out: scratch paper for working problems by hand",
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.5, Color = Colors.Black }
        };
        var tabRow = new StackPanel { Orientation = Orientation.Horizontal };
        tabRow.Children.Add(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 13, Foreground = TitleRed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 9, 0) });
        tabRow.Children.Add(new TextBlock { Text = "SCRATCH PAPER", FontFamily = TitleFont, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = TextMain, VerticalAlignment = VerticalAlignment.Center });
        scratchTab.Child = tabRow;
        scratchTab.MouseEnter += (s, e) => scratchTab.Background = Hex("#F2241615");
        scratchTab.MouseLeave += (s, e) => scratchTab.Background = Hex("#EE160F0E");
        scratchTab.MouseLeftButtonUp += (s, e) => { Sfx.Play(Sfx.Click); ToggleScratch(); };
        Panel.SetZIndex(scratchTab, 41);
        host.Children.Add(scratchTab);

        scratchSheet = new Grid
        {
            Width = sheetW, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 16),
            Visibility = Visibility.Collapsed
        };
        scratchSlide = new TranslateTransform(0, -H);
        scratchSheet.RenderTransform = scratchSlide;
        Panel.SetZIndex(scratchSheet, 42);
        var card = new Border
        {
            CornerRadius = new CornerRadius(22), Background = Hex("#F7140E0D"), BorderBrush = Hex("#55C23B30"), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 34, ShadowDepth = 5, Opacity = 0.6, Color = Colors.Black }
        };
        scratchSheet.Children.Add(card);
        var dp = new DockPanel { Margin = new Thickness(16, 12, 16, 16) };
        card.Child = dp;

        // ----- Toolbar -----
        var bar = new Grid { Margin = new Thickness(2, 0, 2, 10) };
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        DockPanel.SetDock(bar, Dock.Top);
        dp.Children.Add(bar);
        var tools = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(tools);

        tools.Children.Add(ToolButton("pen", "", null, "Pen (P)"));
        tools.Children.Add(ToolButton("marker", "", null, "Highlighter (H)"));
        tools.Children.Add(ToolButton("line", null, LineIcon(), "Straight line (L). Hold Shift to snap to 45°"));
        tools.Children.Add(ToolButton("box", "", null, "Box (B). Hold Shift for a square"));
        tools.Children.Add(ToolButton("circle", "", null, "Circle (C). Hold Shift for a perfect circle"));
        tools.Children.Add(ToolButton("eraser", "", null, "Eraser (E)"));
        tools.Children.Add(ToolButton("select", "", null, "Select and move (S): circle some ink, then drag it"));
        tools.Children.Add(Gap());
        foreach (var c in InkColors) tools.Children.Add(Swatch(c));
        tools.Children.Add(Gap());
        foreach (var sz in new[] { 2.0, 3.5, 7.0 }) tools.Children.Add(SizeDot(sz));
        tools.Children.Add(Gap());
        tools.Children.Add(SmallIcon("", "Undo (Ctrl+Z)", ScratchUndo));
        tools.Children.Add(SmallIcon("", "Redo (Ctrl+Y)", ScratchRedo));
        tools.Children.Add(SmallIcon("", "Clear this page", ScratchClear));
        paperButton = SmallIcon("", "Paper: graph / lined / blank", CyclePaper);
        tools.Children.Add(paperButton);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(right, 1);
        right.Children.Add(SmallIcon("", "Previous page", () => GoPage(pageIdx - 1)));
        pageLabel = new TextBlock { FontSize = 13, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, MinWidth = 44, TextAlignment = TextAlignment.Center };
        right.Children.Add(pageLabel);
        right.Children.Add(SmallIcon("", "Next page", () => GoPage(pageIdx + 1)));
        right.Children.Add(SmallIcon("", "New page", NewScratchPage));
        right.Children.Add(new Border { Width = 1, Height = 20, Background = CardStroke, Margin = new Thickness(8, 0, 6, 0) });
        right.Children.Add(SmallIcon("", "Put the scratch paper away (Esc)", CloseScratch));
        bar.Children.Add(right);

        // ----- Paper -----
        inkFrame = new Border { CornerRadius = new CornerRadius(14), BorderBrush = CardStroke, BorderThickness = new Thickness(1), ClipToBounds = true };
        var paper = new Grid();
        paperGrid = paper;
        inkFrame.Child = paper;
        dp.Children.Add(inkFrame);
        marginLine = new Rectangle
        {
            Width = 1.5, Fill = new SolidColorBrush(Color.FromArgb(0x60, 0xC2, 0x3B, 0x30)), HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(64, 0, 0, 0), IsHitTestVisible = false, Visibility = Visibility.Collapsed
        };
        paper.Children.Add(marginLine);
        ink = new InkCanvas { Background = Brushes.Transparent, Cursor = Cursors.Pen };
        paper.Children.Add(ink);
        ApplyPaper();
        scratchHint = new TextBlock
        {
            Text = "Draw with your mouse, pen or finger.\nWork the problem out here, then pick your answer.",
            FontSize = 15, Foreground = Muted, Opacity = 0.75, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };
        paper.Children.Add(scratchHint);
        // Round the paper's corners
        paper.SizeChanged += (s, e) => paper.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 13, 13);

        ink.PreviewMouseLeftButtonDown += ShapeDown;
        ink.PreviewMouseMove += ShapeMove;
        ink.PreviewMouseLeftButtonUp += ShapeUp;
        ink.LostMouseCapture += (s, e) => shapeStroke = null;

        host.Children.Add(scratchSheet);
        SetScratchTool("pen");
        SetScratchColor(scratchColor);
        SetScratchSize(scratchSize);
    }

    // ----- Toolbar pieces -----
    static Border Gap() { return new Border { Width = 1, Height = 22, Background = CardStroke, Margin = new Thickness(8, 6, 8, 6) }; }

    Btn ToolButton(string tool, string glyph, UIElement icon, string tip)
    {
        var b = new Btn
        {
            Width = 36, Height = 34, CornerRadius = new CornerRadius(9), Background = Brushes.Transparent, ToolTip = tip,
            Margin = new Thickness(1, 2, 1, 2), BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent,
            Child = icon ?? new TextBlock
            {
                Text = glyph, FontFamily = IconFont, FontSize = 15, Foreground = Muted,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        b.MouseEnter += (s, e) => { if (scratchTool != tool) b.Background = CardHover; };
        b.MouseLeave += (s, e) => { if (scratchTool != tool) b.Background = Brushes.Transparent; };
        toolButtons[tool] = b;
        return Clickable(b, () => SetScratchTool(tool), false);
    }

    static UIElement LineIcon()
    {
        return new Line
        {
            X1 = 2, Y1 = 14, X2 = 14, Y2 = 2, Stroke = Muted, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
    }

    Btn SmallIcon(string glyph, string tip, Action click)
    {
        var b = IconButton(glyph, tip, click);
        b.Margin = new Thickness(1, 2, 1, 2);
        ((TextBlock)b.Child).FontSize = 14;
        return b;
    }

    Border Swatch(Color c)
    {
        var dot = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(c),
            BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Margin = new Thickness(3, 2, 3, 2),
            Cursor = Cursors.Hand, ToolTip = "Ink color", VerticalAlignment = VerticalAlignment.Center
        };
        dot.MouseLeftButtonUp += (s, e) =>
        {
            Sfx.Play(Sfx.Click);
            SetScratchColor(c);
            if (scratchTool != "pen" && scratchTool != "marker" && !IsShapeTool) SetScratchTool("pen");
        };
        swatches.Add(new KeyValuePair<Color, Border>(c, dot));
        return dot;
    }

    Border SizeDot(double size)
    {
        double d = 4 + size * 1.4;
        var hit = new Border
        {
            Width = 26, Height = 30, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Line thickness",
            Margin = new Thickness(1, 2, 1, 2), CornerRadius = new CornerRadius(8),
            Child = new Ellipse { Width = d, Height = d, Fill = Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        hit.MouseLeftButtonUp += (s, e) => { Sfx.Play(Sfx.Click); SetScratchSize(size); };
        sizeDots.Add(new KeyValuePair<double, Border>(size, hit));
        return hit;
    }

    bool IsShapeTool { get { return scratchTool == "line" || scratchTool == "box" || scratchTool == "circle"; } }

    void SetScratchTool(string tool)
    {
        scratchTool = tool;
        foreach (var kv in toolButtons)
        {
            bool on = kv.Key == tool;
            kv.Value.Background = on ? Hex("#33C23B30") : Brushes.Transparent;
            kv.Value.BorderBrush = on ? Hex("#66C23B30") : Brushes.Transparent;
            var tb = kv.Value.Child as TextBlock;
            if (tb != null) tb.Foreground = on ? TextMain : Muted;
            var ln = kv.Value.Child as Line;
            if (ln != null) ln.Stroke = on ? TextMain : Muted;
        }
        if (ink == null) return;
        switch (tool)
        {
            case "eraser":
                ink.EditingMode = InkCanvasEditingMode.EraseByPoint;
                ink.EraserShape = new EllipseStylusShape(8 + scratchSize * 4, 8 + scratchSize * 4);
                break;
            case "select": ink.EditingMode = InkCanvasEditingMode.Select; break;
            case "line": case "box": case "circle": ink.EditingMode = InkCanvasEditingMode.None; ink.Cursor = Cursors.Cross; break;
            default: ink.EditingMode = InkCanvasEditingMode.Ink; break;
        }
        if (!IsShapeTool) ink.Cursor = tool == "select" ? Cursors.Arrow : Cursors.Pen;
        ink.DefaultDrawingAttributes = MakeAttributes();
    }

    void SetScratchColor(Color c)
    {
        scratchColor = c;
        foreach (var kv in swatches) kv.Value.BorderBrush = kv.Key == c ? Brushes.White : Brushes.Transparent;
        if (ink != null) ink.DefaultDrawingAttributes = MakeAttributes();
    }

    void SetScratchSize(double size)
    {
        scratchSize = size;
        foreach (var kv in sizeDots)
        {
            kv.Value.Background = kv.Key == size ? CardHover : Brushes.Transparent;
            ((Ellipse)kv.Value.Child).Fill = kv.Key == size ? TextMain : Muted;
        }
        if (ink == null) return;
        ink.DefaultDrawingAttributes = MakeAttributes();
        if (scratchTool == "eraser") ink.EraserShape = new EllipseStylusShape(8 + size * 4, 8 + size * 4);
    }

    DrawingAttributes MakeAttributes()
    {
        if (scratchTool == "marker")
        {
            return new DrawingAttributes
            {
                Color = scratchColor == InkColors[0] ? Color.FromRgb(0xF2, 0xD5, 0x4C) : scratchColor,
                IsHighlighter = true, StylusTip = StylusTip.Rectangle, Width = 6 + scratchSize * 2.5, Height = 4 + scratchSize * 3.5,
                IgnorePressure = true, FitToCurve = true
            };
        }
        return new DrawingAttributes
        {
            Color = scratchColor, Width = scratchSize, Height = scratchSize, FitToCurve = !IsShapeTool,
            StylusTip = StylusTip.Ellipse, IgnorePressure = IsShapeTool
        };
    }

    // ----- Paper backgrounds -----
    static Brush PaperBrush(string style)
    {
        var bg = Color.FromRgb(0x17, 0x12, 0x12);
        var dg = new DrawingGroup();
        if (style == "grid")
        {
            // Graph paper: faint 24px squares, slightly brighter every 5th line
            const double cell = 24, tile = cell * 5;
            dg.Children.Add(new GeometryDrawing(new SolidColorBrush(bg), null, new RectangleGeometry(new Rect(0, 0, tile, tile))));
            var minor = new Pen(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), 1);
            var major = new Pen(new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF)), 1);
            for (int i = 0; i < 5; i++)
            {
                var pen = i == 0 ? major : minor;
                dg.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(i * cell + 0.5, 0), new Point(i * cell + 0.5, tile))));
                dg.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(0, i * cell + 0.5), new Point(tile, i * cell + 0.5))));
            }
            var b = new DrawingBrush(dg) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, tile, tile), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
            b.Freeze();
            return b;
        }
        if (style == "lined")
        {
            // Notebook paper: ruled lines (the red margin line is its own element)
            const double gap = 34;
            dg.Children.Add(new GeometryDrawing(new SolidColorBrush(bg), null, new RectangleGeometry(new Rect(0, 0, 400, gap))));
            dg.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromArgb(0x2C, 0x8F, 0xB4, 0xFF)), 1), new LineGeometry(new Point(0, gap - 0.5), new Point(400, gap - 0.5))));
            var b = new DrawingBrush(dg) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 400, gap), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
            b.Freeze();
            return b;
        }
        var plain = new SolidColorBrush(bg);
        plain.Freeze();
        return plain;
    }

    void CyclePaper()
    {
        paperStyle = paperStyle == "grid" ? "lined" : paperStyle == "lined" ? "blank" : "grid";
        ApplyPaper();
        stats.ScratchPaper = paperStyle;
        stats.Save();
    }

    void ApplyPaper()
    {
        paperGrid.Background = PaperBrush(paperStyle);
        marginLine.Visibility = paperStyle == "lined" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----- Shapes: line, box and circle, previewed while you drag -----
    void ShapeDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsShapeTool) return;
        shapeStart = e.GetPosition(ink);
        shapeStroke = new Stroke(ShapePoints(shapeStart, shapeStart, false), MakeAttributes());
        ink.Strokes.Add(shapeStroke);
        ink.CaptureMouse();
        e.Handled = true;
    }

    void ShapeMove(object sender, MouseEventArgs e)
    {
        if (shapeStroke == null || !IsShapeTool) return;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        shapeStroke.StylusPoints = ShapePoints(shapeStart, e.GetPosition(ink), shift);
        e.Handled = true;
    }

    void ShapeUp(object sender, MouseButtonEventArgs e)
    {
        if (shapeStroke == null) return;
        var s = shapeStroke;
        shapeStroke = null;
        ink.ReleaseMouseCapture();
        // A click without a drag leaves nothing behind
        var b = s.GetBounds();
        if (b.Width < 3 && b.Height < 3) ink.Strokes.Remove(s);
        e.Handled = true;
    }

    StylusPointCollection ShapePoints(Point a, Point b, bool shift)
    {
        var pts = new StylusPointCollection();
        if (scratchTool == "line")
        {
            if (shift)
            {
                // snap to the nearest 45 degrees
                double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
                double ang = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
                b = new Point(a.X + len * Math.Cos(ang), a.Y + len * Math.Sin(ang));
            }
            pts.Add(new StylusPoint(a.X, a.Y));
            pts.Add(new StylusPoint(b.X, b.Y));
            return pts;
        }
        double w = b.X - a.X, h = b.Y - a.Y;
        if (shift)
        {
            double m = Math.Max(Math.Abs(w), Math.Abs(h));
            w = Math.Sign(w == 0 ? 1 : w) * m;
            h = Math.Sign(h == 0 ? 1 : h) * m;
        }
        if (scratchTool == "box")
        {
            pts.Add(new StylusPoint(a.X, a.Y));
            pts.Add(new StylusPoint(a.X + w, a.Y));
            pts.Add(new StylusPoint(a.X + w, a.Y + h));
            pts.Add(new StylusPoint(a.X, a.Y + h));
            pts.Add(new StylusPoint(a.X, a.Y));
            return pts;
        }
        double cx = a.X + w / 2, cy = a.Y + h / 2, rx = Math.Abs(w) / 2, ry = Math.Abs(h) / 2;
        for (int i = 0; i <= 72; i++)
        {
            double t = i * Math.PI * 2 / 72;
            pts.Add(new StylusPoint(cx + rx * Math.Cos(t), cy + ry * Math.Sin(t)));
        }
        return pts;
    }

    // ----- Pages, undo and saving -----
    void EnsureScratchLoaded()
    {
        if (scratchLoaded) return;
        scratchLoaded = true;
        paperStyle = stats.ScratchPaper ?? "grid";
        if (paperStyle != "grid" && paperStyle != "lined" && paperStyle != "blank") paperStyle = "grid";
        ApplyPaper();
        try
        {
            if (File.Exists(ScratchFile))
                using (var r = new BinaryReader(File.OpenRead(ScratchFile)))
                {
                    int n = r.ReadInt32();
                    for (int i = 0; i < n && i < MaxPages; i++)
                    {
                        int len = r.ReadInt32();
                        var bytes = r.ReadBytes(len);
                        using (var ms = new MemoryStream(bytes)) pages.Add(new StrokeCollection(ms));
                    }
                    pageIdx = Math.Max(0, Math.Min(pages.Count - 1, r.ReadInt32()));
                }
        }
        catch { pages.Clear(); pageIdx = 0; }
        if (pages.Count == 0) pages.Add(new StrokeCollection());
        ShowPage(pageIdx);
    }

    void SaveScratch()
    {
        if (!scratchLoaded || Stats.ReadOnly) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ScratchFile));
            // Keep pages that have something on them (and the one you're on)
            var keep = pages.Where((p, i) => p.Count > 0 || i == pageIdx).ToList();
            int at = keep.IndexOf(pages[pageIdx]);
            string tmp = ScratchFile + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp)))
            {
                w.Write(keep.Count);
                foreach (var p in keep)
                {
                    using (var ms = new MemoryStream())
                    {
                        p.Save(ms, true);
                        var bytes = ms.ToArray();
                        w.Write(bytes.Length);
                        w.Write(bytes);
                    }
                }
                w.Write(Math.Max(0, at));
            }
            if (File.Exists(ScratchFile)) File.Delete(ScratchFile);
            File.Move(tmp, ScratchFile);
        }
        catch { }
    }

    void ShowPage(int i)
    {
        if (ink.Strokes != null) ink.Strokes.StrokesChanged -= OnStrokesChanged;
        pageIdx = i;
        ink.Strokes = pages[i];
        ink.Strokes.StrokesChanged += OnStrokesChanged;
        pageLabel.Text = (i + 1) + " / " + pages.Count;
        scratchHint.Visibility = ink.Strokes.Count == 0 && pages.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    void GoPage(int i)
    {
        if (i < 0 || i >= pages.Count) return;
        // Leaving an empty page behind? Drop it.
        if (pages[pageIdx].Count == 0 && pages.Count > 1 && i != pageIdx)
        {
            pages.RemoveAt(pageIdx);
            if (i > pageIdx) i--;
        }
        ShowPage(i);
    }

    void NewScratchPage()
    {
        if (pages[pageIdx].Count == 0) { ShowPage(pageIdx); return; }
        pages.Add(new StrokeCollection());
        while (pages.Count > MaxPages) pages.RemoveAt(0);
        ShowPage(pages.Count - 1);
    }

    // A new question gets a fresh page (old pages stay one click back)
    void ScratchNewQuestion()
    {
        if (!scratchLoaded) return;
        if (pages[pageIdx].Count > 0) NewScratchPage();
    }

    void OnStrokesChanged(object sender, StrokeCollectionChangedEventArgs e)
    {
        scratchHint.Visibility = Visibility.Collapsed;
        if (applyingHistory) return;
        var page = ink.Strokes;
        Stack<StrokeCollection[]> u, r;
        if (!undoStacks.TryGetValue(page, out u)) undoStacks[page] = u = new Stack<StrokeCollection[]>();
        if (!redoStacks.TryGetValue(page, out r)) redoStacks[page] = r = new Stack<StrokeCollection[]>();
        u.Push(new[] { new StrokeCollection(e.Added), new StrokeCollection(e.Removed) });
        r.Clear();
    }

    void ScratchUndo() { History(true); }
    void ScratchRedo() { History(false); }

    void History(bool back)
    {
        if (ink == null) return;
        var page = ink.Strokes;
        Stack<StrokeCollection[]> from, to;
        if (!(back ? undoStacks : redoStacks).TryGetValue(page, out from) || from.Count == 0) return;
        if (!(back ? redoStacks : undoStacks).TryGetValue(page, out to)) (back ? redoStacks : undoStacks)[page] = to = new Stack<StrokeCollection[]>();
        var step = from.Pop();
        applyingHistory = true;
        try
        {
            var add = back ? step[1] : step[0];
            var remove = back ? step[0] : step[1];
            foreach (var s in remove) if (page.Contains(s)) page.Remove(s);
            foreach (var s in add) if (!page.Contains(s)) page.Add(s);
        }
        finally { applyingHistory = false; }
        to.Push(step);
    }

    void ScratchClear()
    {
        if (ink.Strokes.Count == 0) return;
        ink.Strokes.Clear(); // one undo step brings it all back
    }

    // ----- Opening and closing -----
    void ToggleScratch() { if (scratchOpen) CloseScratch(); else OpenScratch(); }

    void OpenScratch()
    {
        if (scratchOpen || scratchSheet == null) return;
        EnsureScratchLoaded();
        scratchOpen = true;
        scratchSheet.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slide = new DoubleAnimation(0, TimeSpan.FromMilliseconds(360)) { EasingFunction = ease };
        // Once it covers her, Heather can rest (no point drawing her behind the paper)
        slide.Completed += (s, e) => { if (scratchOpen && heatherView != null) heatherView.Visibility = Visibility.Hidden; };
        scratchSlide.BeginAnimation(TranslateTransform.YProperty, slide);
        scratchTab.BeginAnimation(OpacityProperty, Anim(0, 200));
        scratchTab.IsHitTestVisible = false;
        ink.Focus();
    }

    void CloseScratch()
    {
        if (!scratchOpen) return;
        scratchOpen = false;
        if (heatherView != null) heatherView.Visibility = Visibility.Visible;
        var slide = new DoubleAnimation(-Height, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        slide.Completed += (s, e) => { if (!scratchOpen) scratchSheet.Visibility = Visibility.Collapsed; };
        scratchSlide.BeginAnimation(TranslateTransform.YProperty, slide);
        scratchTab.BeginAnimation(OpacityProperty, Anim(1, 300));
        scratchTab.IsHitTestVisible = true;
        SaveScratch();
    }

    // For the snapshot screenshots: a worked problem drawn with every tool
    void DemoScratch()
    {
        Func<double, double, double, double, Stroke> line = (x1, y1, x2, y2) =>
            new Stroke(new StylusPointCollection(new[] { new StylusPoint(x1, y1), new StylusPoint(x2, y2) }), MakeAttributes());
        ink.Strokes.Clear();
        SetScratchColor(InkColors[0]);
        SetScratchTool("line");
        // axes
        ink.Strokes.Add(line(60, 380, 460, 380));
        ink.Strokes.Add(line(100, 80, 100, 420));
        // a parabola
        SetScratchColor(InkColors[5]);
        SetScratchTool("pen");
        var curve = new StylusPointCollection();
        for (double x = -3; x <= 3.001; x += 0.1) curve.Add(new StylusPoint(260 + x * 50, 380 - x * x * 30));
        ink.Strokes.Add(new Stroke(curve, MakeAttributes()));
        // circled vertex
        SetScratchColor(InkColors[1]);
        SetScratchTool("circle");
        ink.Strokes.Add(new Stroke(ShapePoints(new Point(240, 360), new Point(280, 400), false), MakeAttributes()));
        // boxed answer
        SetScratchColor(InkColors[3]);
        SetScratchTool("box");
        ink.Strokes.Add(new Stroke(ShapePoints(new Point(520, 120), new Point(700, 190), false), MakeAttributes()));
        SetScratchTool("marker");
        ink.Strokes.Add(new Stroke(new StylusPointCollection(new[] { new StylusPoint(530, 230), new StylusPoint(690, 230) }), MakeAttributes()));
        SetScratchColor(InkColors[0]);
        SetScratchTool("pen");
    }

    // Keys while the paper is out. Returns true when the key was used.
    bool ScratchKey(KeyEventArgs e)
    {
        if (!scratchOpen) return false;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Escape) { CloseScratch(); return true; }
        if (ctrl && e.Key == Key.Z) { if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ScratchRedo(); else ScratchUndo(); return true; }
        if (ctrl && e.Key == Key.Y) { ScratchRedo(); return true; }
        if (ctrl) return false;
        switch (e.Key)
        {
            case Key.P: SetScratchTool("pen"); return true;
            case Key.H: SetScratchTool("marker"); return true;
            case Key.L: SetScratchTool("line"); return true;
            case Key.B: SetScratchTool("box"); return true;
            case Key.C: SetScratchTool("circle"); return true;
            case Key.E: SetScratchTool("eraser"); return true;
            case Key.S: SetScratchTool("select"); return true;
            case Key.Delete: ScratchClear(); return true;
        }
        return false;
    }
}
