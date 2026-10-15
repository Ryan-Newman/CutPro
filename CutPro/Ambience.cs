using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace CutPro;

/// <summary>
/// Ambient life for the material themes, drawn on a click-through overlay above the panels (and a grass layer behind the
/// timeline): Nature — grass swaying in travelling wind gusts, ladybugs and an ant crawling along panel edges (one visits the
/// selected clip), now and then a butterfly; Frosted Glass — light glinting across the panes, droplets sliding down the glass;
/// Stone — sparkles twinkling in the sapphire, lapis, quartz and obsidian; Metals — a bright sheen sweeping across the plates.
/// Transforms and opacity animate on the GPU; only the crawling creatures move per frame (a few transforms).
/// </summary>
public sealed class Ambience
{
    readonly Canvas layer;                         // over everything, IsHitTestVisible = false
    readonly Func<IReadOnlyList<Card>> cards;
    readonly Card panelCard;                       // the timeline card: the grass grows inside it, behind the tracks
    readonly Func<Rect?> selectedClipRect;          // the selected clip on the timeline, in layer coordinates
    readonly Random rnd = new(7);
    Canvas? grass;
    string? kind;
    readonly List<Critter> critters = new();
    readonly List<DispatcherTimerLite> timers = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    double lastT;
    bool hooked;

    /// <summary>While video plays the creatures hold still and no gusts / glints / butterflies start: separate moving things all
    /// over the window make WPF repaint (almost) all of it every frame (its dirty regions merge), which slowed playback.</summary>
    public Func<bool> Playing = () => false;

    public Ambience(Canvas layer, Func<IReadOnlyList<Card>> cards, Card panelCard, Func<Rect?> selectedClipRect)
    {
        this.layer = layer; this.cards = cards; this.panelCard = panelCard; this.selectedClipRect = selectedClipRect;
        panelCard.SizeChanged += (_, _) => { if (kind == "nature") RebuildGrassSoon(); };
    }

    /// <summary>Starts the theme's ambience (null / disabled = none).</summary>
    public void Apply(string themeName, bool enabled)
    {
        Clear();
        kind = !enabled ? null : themeName switch { "Frosted Glass" => "glass", "Stone" => "stone", "Metals" => "metal", "Nature" => "nature", _ => null };
        if (kind == null) return;
        switch (kind)
        {
            case "nature":
                if (Part("grass")) RebuildGrassSoon();
                if (Part("gust")) Every(3.5, 7, Gust);
                if (Part("bugEditor") || Part("bugs")) critters.Add(new Critter(Ladybug(0), Ladybug(1), 26, () => Region("Editor")));
                if (Part("bugSide") || Part("bugs")) critters.Add(new Critter(Ant(0), Ant(1), 34, () => Region("Side")) { S = 400 });
                if (Part("bugVisitor") || Part("bugs")) critters.Add(new Critter(Ladybug(0), Ladybug(1), 20, () => selectedClipRect() is { } r ? r : Region("Insp"), visitor: true) { S = 200 });
                if (Part("bugPanel") || Part("bugs")) critters.Add(new Critter(Ant(0), Ant(1), 30, () => Region("Panel")) { S = 900, Dir = -1 });
                if (!Part("bugs") && !Part("bugEditor") && !Part("bugSide") && !Part("bugVisitor") && !Part("bugPanel")) critters.Clear();
                foreach (var c in critters) layer.Children.Add(c.El);
                Every(28, 45, Butterfly);
                break;
            case "glass":
                Every(5, 9, () => Sweep(diagonal: true, width: 140, peak: 0x2E));
                for (int i = 0; i < 5; i++) { string reg = i % 2 == 0 ? "Side" : "Insp"; critters.Add(new Critter(Droplet(), Droplet(), 0, () => Region(reg)) { Drop = true, X = 0.15 + rnd.NextDouble() * 0.7, S = rnd.NextDouble() * 400 }); }
                if (!Part("bugs")) critters.Clear();
                foreach (var c in critters) layer.Children.Add(c.El);
                break;
            case "stone":
                for (int i = 0; i < 14; i++) Twinkle(i);
                break;
            case "metal":
                Every(6, 10, () => Sweep(diagonal: false, width: 70, peak: 0x40));
                break;
        }
        if (critters.Count > 0 && !hooked) { CompositionTarget.Rendering += Tick; hooked = true; }
    }

    /// <summary>Diagnostics: CUTPRO_AMBPART=grass,gust,bugs limits the ambience to those parts (measuring their cost).</summary>
    static bool Part(string p) => Environment.GetEnvironmentVariable("CUTPRO_AMBPART") is not { Length: > 0 } v || v.Split(',').Contains(p);

    public void Clear()
    {
        foreach (var t in timers) t.Stop();
        timers.Clear();
        critters.Clear();
        layer.Children.Clear();
        if (grass != null) { (grass.Parent as Panel)?.Children.Remove(grass); grass = null; }
        if (hooked) { CompositionTarget.Rendering -= Tick; hooked = false; }
    }

    Rect? Region(string region)
    {
        var c = cards().FirstOrDefault(x => x.Region == region);
        if (c == null || !c.IsVisible || c.ActualWidth < 30 || c.ActualHeight < 20) return null;
        try { return c.TransformToVisual(layer).TransformBounds(new Rect(0, 0, c.ActualWidth, c.ActualHeight)); } catch { return null; }
    }
    Card? CardOf(string region) => cards().FirstOrDefault(x => x.Region == region);

    // ---------------- timers (random intervals) ----------------
    sealed class DispatcherTimerLite
    {
        readonly System.Windows.Threading.DispatcherTimer t = new();
        public DispatcherTimerLite(Func<TimeSpan> next, Action a, Func<bool> skip) { t.Interval = next(); t.Tick += (_, _) => { if (!skip()) a(); t.Interval = next(); }; t.Start(); }
        public void Stop() => t.Stop();
    }
    void Every(double minS, double maxS, Action a) => timers.Add(new DispatcherTimerLite(() => TimeSpan.FromSeconds(minS + rnd.NextDouble() * (maxS - minS)), a, Playing));

    // ---------------- per-frame creatures ----------------
    sealed class Critter
    {
        public readonly Image El = new() { IsHitTestVisible = false, Stretch = Stretch.Fill };
        double w, h;                                // drawn size (logical px)
        readonly ImageSource f0, f1;
        public readonly double Speed;
        readonly Func<Rect?> where;
        public bool Visitor, Drop;
        public double S, Dir = 1, X, PauseLeft, WalkLeft = 3, Frame;
        public Critter(ImageSource a, ImageSource b, double speed, Func<Rect?> where, bool visitor = false)
        {
            // frames pre-rendered to bitmaps: moving / turning them is then a texture transform (vector art re-rendered every frame cost ~7 ms)
            double k = speed > 0 ? 1.35 : 1;
            w = a.Width * k; h = a.Height * k;
            f0 = Bake(a, k); f1 = ReferenceEquals(a, b) ? f0 : Bake(b, k);
            Speed = speed; this.where = where; Visitor = visitor;
            El.Source = f0; El.Width = w; El.Height = h;
            El.RenderTransform = new TransformGroup { Children = { new RotateTransform(), new TranslateTransform() } };
        }
        static ImageSource Bake(ImageSource src, double k)
        {
            const double over = 2;                     // supersampled for sharp edges at high DPI
            int pw = (int)Math.Ceiling(src.Width * k * over), ph = (int)Math.Ceiling(src.Height * k * over);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawImage(src, new Rect(0, 0, pw, ph));
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32); rtb.Render(dv); rtb.Freeze();
            return rtb;
        }

        public void Step(double dt, Random rnd)
        {
            if (where() is not { } r) { El.Visibility = Visibility.Collapsed; return; }
            El.Visibility = Visibility.Visible;
            var g = (TransformGroup)El.RenderTransform;
            var rot = (RotateTransform)g.Children[0]; var tr = (TranslateTransform)g.Children[1];

            if (Drop)
            {
                // a droplet sliding down the glass in fits and starts
                if (PauseLeft > 0) PauseLeft -= dt;
                else { S += dt * (8 + 10 * Math.Abs(Math.Sin(S * 0.05))); if (rnd.NextDouble() < dt * 0.4) PauseLeft = 0.6 + rnd.NextDouble() * 2.5; }
                double y = r.Top + 30 + S % Math.Max(1, r.Height - 50);
                if (y < r.Top + 32) X = 0.15 + rnd.NextDouble() * 0.7;
                tr.X = r.Left + r.Width * X - w / 2; tr.Y = y - h / 2; rot.Angle = 0;
                return;
            }
            // crawl along the rectangle's edge (clockwise), stop now and then, sometimes turn round
            if (PauseLeft > 0) { PauseLeft -= dt; }
            else
            {
                S += Dir * Speed * dt;
                WalkLeft -= dt;
                if (WalkLeft <= 0) { PauseLeft = 0.4 + rnd.NextDouble() * 1.8; WalkLeft = 2 + rnd.NextDouble() * 6; if (rnd.NextDouble() < 0.3) Dir = -Dir; }
                Frame += dt;
                El.Source = (int)(Frame / 0.11) % 2 == 0 ? f0 : f1;
            }
            double per = 2 * (r.Width + r.Height), s = ((S % per) + per) % per;
            Point p; double ang;
            if (s < r.Width) { p = new(r.Left + s, r.Top); ang = 0; }
            else if (s < r.Width + r.Height) { p = new(r.Right, r.Top + s - r.Width); ang = 90; }
            else if (s < 2 * r.Width + r.Height) { p = new(r.Right - (s - r.Width - r.Height), r.Bottom); ang = 180; }
            else { p = new(r.Left, r.Bottom - (s - 2 * r.Width - r.Height)); ang = 270; }
            if (Dir < 0) ang += 180;
            rot.CenterX = w / 2; rot.CenterY = h / 2; rot.Angle = ang;
            tr.X = p.X - w / 2; tr.Y = p.Y - h / 2;
        }
    }

    void Tick(object? s, EventArgs e)
    {
        double t = clock.Elapsed.TotalSeconds, dt = Math.Clamp(t - lastT, 0, 0.1); lastT = t;
        if (Playing()) return;
        foreach (var c in critters) c.Step(dt, rnd);
    }

    // ---------------- nature: grass in the wind ----------------
    System.Windows.Threading.DispatcherTimer? grassTimer;
    void RebuildGrassSoon()
    {
        grassTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        grassTimer.Stop();
        grassTimer.Tick -= GrassTick; grassTimer.Tick += GrassTick;
        grassTimer.Start();
    }
    void GrassTick(object? s, EventArgs e) { grassTimer!.Stop(); if (kind == "nature") BuildGrass(); }

    /// <summary>Clumps of blades over the grass texture, each swaying from its root; a phase that runs left to right makes the
    /// sway travel across the field like gusts of wind.</summary>
    void BuildGrass()
    {
        if (panelCard.Child is not Grid host) return;
        if (grass != null) host.Children.Remove(grass);
        grass = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        Grid.SetRowSpan(grass, Math.Max(1, host.RowDefinitions.Count));
        host.Children.Insert(0, grass);
        double W = panelCard.ActualWidth, H = panelCard.ActualHeight;
        if (W < 50 || H < 50) return;
        var clumps = Enumerable.Range(0, 10).Select(_ => Clump(rnd)).ToArray();
        Color[] tips = { Color.FromRgb(108, 150, 58), Color.FromRgb(90, 134, 48), Color.FromRgb(126, 160, 66), Color.FromRgb(78, 120, 44) };
        // a fringe of tall blades along the bottom edge sways (two staggered rows); the field above gets passing gusts
        foreach (double y in new[] { H - 4, H + 8 })
            for (double x = -10; x < W + 10; x += 16 + rnd.NextDouble() * 10)
            {
                var geo = clumps[rnd.Next(clumps.Length)];
                var tip = tips[rnd.Next(tips.Length)];
                var fill = new LinearGradientBrush(Color.FromArgb(210, 22, 44, 14), Color.FromArgb(225, tip.R, tip.G, tip.B), new Point(0, 1), new Point(0, 0)); fill.Freeze();
                double hh = geo.Bounds.Height, ww = geo.Bounds.Width;
                var skew = new SkewTransform(0, 0, ww / 2, hh);
                // cached as a bitmap: the sway is then a GPU transform of a texture (skewing vector paths re-tessellates them every frame)
                var path = new Path { Data = geo, Fill = fill, RenderTransform = skew, IsHitTestVisible = false, CacheMode = new BitmapCache { SnapsToDevicePixels = false } };
                Canvas.SetLeft(path, x + (rnd.NextDouble() - 0.5) * 8); Canvas.SetTop(path, y - hh);
                grass.Children.Add(path);
                double amp = 4 + rnd.NextDouble() * 5, period = 1.9 + rnd.NextDouble() * 0.7;
                var a = new DoubleAnimation(-amp * 0.3, amp, TimeSpan.FromSeconds(period))
                {
                    AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    BeginTime = TimeSpan.FromSeconds(-(x / 260.0) - rnd.NextDouble() * 0.25),     // the gust travels left → right
                };
                Timeline.SetDesiredFrameRate(a, 30);
                skew.BeginAnimation(SkewTransform.AngleXProperty, a);
            }
    }


    /// <summary>Wind passing over the field: a soft lighter band rolling across the grass.</summary>
    void Gust()
    {
        if (grass == null || grass.ActualWidth < 50) return;
        double W = grass.ActualWidth, H = grass.ActualHeight, bw = 160 + rnd.NextDouble() * 120;
        var band = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        band.GradientStops.Add(new GradientStop(Color.FromArgb(0, 200, 230, 150), 0));
        band.GradientStops.Add(new GradientStop(Color.FromArgb(34, 200, 230, 150), 0.5));
        band.GradientStops.Add(new GradientStop(Color.FromArgb(0, 200, 230, 150), 1));
        var r = new Rectangle { Width = bw, Height = H + 40, Fill = band, IsHitTestVisible = false };
        var move = new TranslateTransform(-bw - 40, -20);
        r.RenderTransform = new TransformGroup { Children = { new SkewTransform(-18, 0, bw / 2, H / 2), move } };
        grass.Children.Add(r);
        var a = new DoubleAnimation(move.X, W + bw + 40, TimeSpan.FromSeconds((W + bw) / (170 + rnd.NextDouble() * 90)));
        Timeline.SetDesiredFrameRate(a, 30);
        a.Completed += (_, _) => grass?.Children.Remove(r);
        move.BeginAnimation(TranslateTransform.XProperty, a);
    }
    static Geometry Clump(Random rnd)
    {
        var g = new StreamGeometry();
        using (var sc = g.Open())
        {
            int n = 4 + rnd.Next(4);
            for (int i = 0; i < n; i++)
            {
                double bx = rnd.NextDouble() * 14, h = 22 + rnd.NextDouble() * 30, w = 1.8 + rnd.NextDouble() * 1.6, bend = (rnd.NextDouble() - 0.4) * 10;
                sc.BeginFigure(new Point(bx - w / 2, 56), true, true);
                sc.QuadraticBezierTo(new Point(bx + bend * 0.3, 56 - h * 0.6), new Point(bx + bend, 56 - h), true, false);
                sc.QuadraticBezierTo(new Point(bx + bend * 0.3 + w * 0.4, 56 - h * 0.55), new Point(bx + w / 2, 56), true, false);
            }
        }
        g.Freeze();
        return g;
    }

    // ---------------- nature: creatures ----------------
    static DrawingImage Art(double w, double h, Action<DrawingContext> draw)
    {
        var g = new DrawingGroup();
        using (var dc = g.Open()) { dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); draw(dc); }
        g.Freeze(); var i = new DrawingImage(g); i.Freeze(); return i;
    }
    static Pen Leg(double w = 0.7) { var p = new Pen(new SolidColorBrush(Color.FromRgb(20, 20, 20)), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; p.Freeze(); return p; }

    /// <summary>A ladybug walking right (two leg positions).</summary>
    static DrawingImage Ladybug(int frame) => Art(16, 14, dc =>
    {
        var leg = Leg();
        double s = frame == 0 ? 1 : -1;
        for (int i = 0; i < 3; i++)
        {
            double x = 5 + i * 3;
            dc.DrawLine(leg, new Point(x, 4.5), new Point(x + s * (i == 1 ? -1.5 : 1.5), 1.5));
            dc.DrawLine(leg, new Point(x, 9.5), new Point(x - s * (i == 1 ? -1.5 : 1.5), 12.5));
        }
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(18, 18, 18)), null, new Point(13, 7), 2.6, 2.4);
        var shell = new RadialGradientBrush(Color.FromRgb(240, 70, 50), Color.FromRgb(170, 20, 18)) { GradientOrigin = new Point(0.35, 0.3) };
        dc.DrawEllipse(shell, new Pen(Brushes.Black, 0.5), new Point(7.5, 7), 5.6, 4.6);
        dc.DrawLine(new Pen(Brushes.Black, 0.6), new Point(2, 7), new Point(13, 7));
        var spot = new SolidColorBrush(Color.FromRgb(15, 15, 15));
        foreach (var (x, y) in new[] { (5.0, 4.6), (9.0, 4.4), (6.0, 9.4), (9.6, 9.3), (3.4, 7.0) }) dc.DrawEllipse(spot, null, new Point(x, y), 0.95, 0.95);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), null, new Point(6, 4.2), 1.4, 0.6);
        dc.DrawLine(Leg(0.5), new Point(15, 6), new Point(16, 4.5)); dc.DrawLine(Leg(0.5), new Point(15, 8), new Point(16, 9.5));
    });

    /// <summary>An ant walking right.</summary>
    static DrawingImage Ant(int frame) => Art(18, 12, dc =>
    {
        var leg = Leg(0.6); double s = frame == 0 ? 1 : -1;
        foreach (var (x, i) in new[] { (7.5, 0), (9.0, 1), (10.5, 2) })
        {
            dc.DrawLine(leg, new Point(x, 6), new Point(x - 2.5 + s * (i - 1) * 1.6, 1.5));
            dc.DrawLine(leg, new Point(x, 6), new Point(x - 2.5 - s * (i - 1) * 1.6, 10.5));
        }
        var body = new SolidColorBrush(Color.FromRgb(36, 24, 20));
        dc.DrawEllipse(body, null, new Point(4, 6), 3.4, 2.4);
        dc.DrawEllipse(body, null, new Point(9, 6), 1.9, 1.4);
        dc.DrawEllipse(body, null, new Point(13, 6), 2.2, 2);
        dc.DrawLine(Leg(0.5), new Point(14.5, 5), new Point(17.5, 2.5)); dc.DrawLine(Leg(0.5), new Point(14.5, 7), new Point(17.5, 9.5));
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), null, new Point(3.4, 5), 1.2, 0.5);
    });

    /// <summary>A butterfly drifts across the window on a wavy path, flapping.</summary>
    void Butterfly()
    {
        double W = layer.ActualWidth, H = layer.ActualHeight;
        if (W < 200) return;
        var wings = new Path
        {
            Data = Geometry.Parse("M 0,0 C -9,-11 -16,-5 -12,1 C -16,6 -8,11 0,2 C 8,11 16,6 12,1 C 16,-5 9,-11 0,0 Z"),
            Fill = new LinearGradientBrush(Color.FromRgb(255, 176, 59), Color.FromRgb(230, 92, 40), 90), Stroke = new SolidColorBrush(Color.FromRgb(60, 30, 10)), StrokeThickness = 0.8,
            IsHitTestVisible = false,
        };
        var flap = new ScaleTransform(1, 1);
        var pos = new TranslateTransform();
        wings.RenderTransform = new TransformGroup { Children = { flap, new RotateTransform(rnd.NextDouble() * 30 - 15), pos } };
        layer.Children.Add(wings);
        flap.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.13)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        bool ltr = rnd.NextDouble() < 0.5;
        double y0 = H * (0.15 + rnd.NextDouble() * 0.5), dur = 9 + rnd.NextDouble() * 5;
        var path = new PathGeometry();
        var fig = new PathFigure { StartPoint = new Point(ltr ? -30 : W + 30, y0) };
        for (int i = 1; i <= 6; i++)
        {
            double x = ltr ? -30 + (W + 60) * i / 6.0 : W + 30 - (W + 60) * i / 6.0;
            fig.Segments.Add(new QuadraticBezierSegment(new Point(x - (ltr ? 1 : -1) * (W / 12), y0 + (rnd.NextDouble() - 0.5) * 160), new Point(x, y0 + (rnd.NextDouble() - 0.5) * 80), true));
        }
        path.Figures.Add(fig); path.Freeze();
        var ax = new DoubleAnimationUsingPath { PathGeometry = path, Source = PathAnimationSource.X, Duration = TimeSpan.FromSeconds(dur) };
        var ay = new DoubleAnimationUsingPath { PathGeometry = path, Source = PathAnimationSource.Y, Duration = TimeSpan.FromSeconds(dur) };
        ax.Completed += (_, _) => layer.Children.Remove(wings);
        pos.BeginAnimation(TranslateTransform.XProperty, ax);
        pos.BeginAnimation(TranslateTransform.YProperty, ay);
    }

    // ---------------- glass / metal: light sweeping across a panel ----------------
    void Sweep(bool diagonal, double width, byte peak)
    {
        var names = cards().Where(c => c.IsVisible && c.ActualWidth > 80 && c.ActualHeight > 30).ToList();
        if (names.Count == 0) return;
        var card = names[rnd.Next(names.Count)];
        if (Region(card.Region) is not { } r) return;
        double radius = card.Radius;
        var host = new Canvas { Width = r.Width, Height = r.Height, ClipToBounds = false, IsHitTestVisible = false,
                                Clip = new RectangleGeometry(new Rect(0, 0, r.Width, r.Height), radius, radius) };
        Canvas.SetLeft(host, r.Left); Canvas.SetTop(host, r.Top);
        var band = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        band.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0));
        band.GradientStops.Add(new GradientStop(Color.FromArgb(peak, 255, 255, 255), 0.5));
        band.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
        double h = diagonal ? r.Height * 2 + 200 : r.Height;
        var rect = new Rectangle { Width = width, Height = h, Fill = band, IsHitTestVisible = false };
        var move = new TranslateTransform(-width - (diagonal ? h * 0.47 : 0), diagonal ? -100 : 0);
        rect.RenderTransform = new TransformGroup { Children = { new RotateTransform(diagonal ? 25 : 0, width / 2, h / 2), move } };
        host.Children.Add(rect);
        layer.Children.Add(host);
        var a = new DoubleAnimation(move.X, r.Width + width + (diagonal ? h * 0.47 : 0), TimeSpan.FromSeconds(diagonal ? 1.6 : 2.2)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        a.Completed += (_, _) => layer.Children.Remove(host);
        move.BeginAnimation(TranslateTransform.XProperty, a);
    }

    static DrawingImage Droplet() => Art(8, 10, dc =>
    {
        var body = new RadialGradientBrush(Color.FromArgb(60, 255, 255, 255), Color.FromArgb(150, 190, 225, 255)) { GradientOrigin = new Point(0.4, 0.35) };
        dc.DrawGeometry(body, new Pen(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), 0.5), Geometry.Parse("M 4,0.5 C 6.5,4 7.5,5.5 7.5,6.8 C 7.5,8.7 5.9,9.6 4,9.6 C 2.1,9.6 0.5,8.7 0.5,6.8 C 0.5,5.5 1.5,4 4,0.5 Z"));
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), null, new Point(2.9, 6), 0.9, 1.2);
    });

    // ---------------- stone: sparkles in the crystals ----------------
    static readonly string[] Sparkly = { "Activity", "Status", "Insp", "Title" };
    void Twinkle(int i)
    {
        var star = new Path
        {
            Data = Geometry.Parse("M 0,-6 L 1.1,-1.1 L 6,0 L 1.1,1.1 L 0,6 L -1.1,1.1 L -6,0 L -1.1,-1.1 Z"),
            Fill = new RadialGradientBrush(Colors.White, Color.FromArgb(0, 170, 200, 255)), IsHitTestVisible = false, Opacity = 0,
        };
        var sc = new ScaleTransform(0.3, 0.3); var pos = new TranslateTransform();
        star.RenderTransform = new TransformGroup { Children = { sc, new RotateTransform(rnd.Next(0, 45)), pos } };
        layer.Children.Add(star);
        void Next()
        {
            var region = Sparkly[rnd.Next(Sparkly.Length)];
            if (Region(region) is { } r) { pos.X = r.Left + 6 + rnd.NextDouble() * (r.Width - 12); pos.Y = r.Top + 6 + rnd.NextDouble() * (r.Height - 12); }
            var begin = TimeSpan.FromSeconds(rnd.NextDouble() * 6);
            var op = new DoubleAnimationUsingKeyFrames { BeginTime = begin };
            op.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.Zero)); op.KeyFrames.Add(new EasingDoubleKeyFrame(1, TimeSpan.FromSeconds(0.35)));
            op.KeyFrames.Add(new EasingDoubleKeyFrame(0, TimeSpan.FromSeconds(1.0)));
            op.Completed += (_, _) => { if (kind == "stone" && star.Parent != null) Next(); };
            var grow = new DoubleAnimation(0.3, 1, TimeSpan.FromSeconds(0.5)) { BeginTime = begin, AutoReverse = true };
            star.BeginAnimation(UIElement.OpacityProperty, op);
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, grow); sc.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
        Next();
    }
}
