using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

enum StageMode { Clean, Scan }

// Pixel-art animation stage built from the sprites in assets\sprites (embedded as resources):
//  - Clean: a brush sweeps a document, its metadata lines are wiped away and turn into dust particles.
//  - Scan:  a magnifier roams over the document and highlights the lines it passes over.
// Everything is drawn frame by frame from CompositionTarget.Rendering; sprites are scaled by a whole number of device
// pixels with nearest-neighbor filtering and every position is snapped to that grid, so the pixels stay crisp.
class PixelStage : Canvas
{
    // art pixel size of each sprite (see make-sprites.ps1)
    const int DocW = 45, DocH = 49, BrushW = 41, BrushH = 42, LupaW = 50, LupaH = 50;
    // contact point of the brush bristles and center of the magnifier lens, in sprite pixels
    const double TipX = 9.4, TipY = 40.2, LensX = 18.6, LensY = 18.6;
    // area of the document that holds the metadata lines, in doc pixels
    const double MetaTop = 17, MetaBottom = 34, MetaLeft = 8.5, MetaRight = 31;
    class Particle
    {
        public Image Img;        // sparkle sprite, or
        public Rectangle Dot;    // a single bright pixel (both are created once and reused)
        public bool Spark;
        public double X, Y, Vx, Vy, Age, Life, Phase; public bool Alive;
        public FrameworkElement El { get { return Spark ? (FrameworkElement)Img : Dot; } }
    }

    static readonly Dictionary<string, BitmapSource> Cache = new Dictionary<string, BitmapSource>();
    static readonly Random Rng = new Random();

    readonly Image doc = new Image(), clean = new Image(), brush = new Image(), lupa = new Image();
    readonly Rectangle[] bars = new Rectangle[3];
    readonly List<Particle> particles = new List<Particle>();
    readonly List<BitmapSource> dust = new List<BitmapSource>();
    readonly RotateTransform brushRot = new RotateTransform();
    readonly Stopwatch clock = new Stopwatch();

    double px = 2, docL, docT;
    StageMode mode = StageMode.Clean;
    bool running, stopping, laidOut;
    double last, stopAt, spawnAcc;
    double outX, outY;          // brush position when the stop sequence starts
    Action stopDone;

    static BitmapSource Sprite(string name)
    {
        BitmapSource s;
        if (Cache.TryGetValue(name, out s)) return s;
        using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("sprite." + name + ".png"))
        {
            if (st == null) return null;
            var bi = new BitmapImage();
            bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = st; bi.EndInit(); bi.Freeze();
            Cache[name] = bi;
            return bi;
        }
    }

    public PixelStage()
    {
        Width = 224; Height = 132;
        ClipToBounds = true; IsHitTestVisible = false; Opacity = 0;
        SnapsToDevicePixels = true;
        foreach (var im in new[] { doc, clean, brush, lupa }) RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.NearestNeighbor);
        doc.Source = Sprite("doc"); clean.Source = Sprite("doc_clean");
        brush.Source = Sprite("brush"); lupa.Source = Sprite("lupa");
        foreach (string n in new[] { "spark3a", "spark3b", "spark3a", "spark3b", "spark5a", "spark5b" }) { var s = Sprite(n); if (s != null) dust.Add(s); }

        for (int i = 0; i < bars.Length; i++)
        {
            bars[i] = new Rectangle { Fill = new SolidColorBrush(Color.FromRgb(212, 255, 74)), Opacity = 0, RadiusX = 1, RadiusY = 1 };
        }
        brush.RenderTransform = brushRot;
        brush.Visibility = Visibility.Collapsed; lupa.Visibility = Visibility.Collapsed;

        Children.Add(doc); Children.Add(clean);
        foreach (var b in bars) Children.Add(b);
        Children.Add(brush); Children.Add(lupa);

        Loaded += delegate
        {
            // use a whole number of device pixels per art pixel so nearest-neighbor scaling stays even
            var src = PresentationSource.FromVisual(this);
            double k = src != null ? src.CompositionTarget.TransformToDevice.M11 : 1;
            px = Math.Max(2, Math.Round(2 * k)) / k;
            Layout();
        };
        Layout();
    }

    void Layout()
    {
        docL = Math.Round((Width - DocW * px) / 2 / px) * px;
        docT = Math.Round((Height - DocH * px) / 2 / px) * px + 2 * px;
        Place(doc, DocW, DocH, docL, docT); Place(clean, DocW, DocH, docL, docT);
        Place(brush, BrushW, BrushH, 0, 0); Place(lupa, LupaW, LupaH, 0, 0);
        brush.RenderTransformOrigin = new Point(TipX / BrushW, TipY / BrushH);
        // highlight bars sit on the three metadata lines of the document sprite
        double[] rows = { 18, 24, 30 }, heights = { 3, 3, 3 };
        for (int i = 0; i < bars.Length; i++)
        {
            bars[i].Width = (MetaRight - MetaLeft + 1) * px; bars[i].Height = heights[i] * px;
            SetLeft(bars[i], docL + MetaLeft * px); SetTop(bars[i], docT + rows[i] * px);
        }
        laidOut = true;
    }

    void Place(Image im, int w, int h, double x, double y)
    {
        im.Width = w * px; im.Height = h * px; SetLeft(im, x); SetTop(im, y);
    }

    double Snap(double v) { return Math.Round(v / px) * px; }

    public void Play(StageMode m)
    {
        if (!laidOut) Layout();
        mode = m; stopping = false; stopDone = null;
        foreach (var p in particles)
        {
            p.Alive = false;
            if (p.Img != null) p.Img.Visibility = Visibility.Collapsed;
            if (p.Dot != null) p.Dot.Visibility = Visibility.Collapsed;
        }
        brush.Visibility = m == StageMode.Clean ? Visibility.Visible : Visibility.Collapsed;
        lupa.Visibility = m == StageMode.Scan ? Visibility.Visible : Visibility.Collapsed;
        clean.Visibility = m == StageMode.Clean ? Visibility.Visible : Visibility.Collapsed;
        clean.Opacity = 0; clean.Clip = null;
        foreach (var b in bars) b.Opacity = 0;
        clock.Restart(); last = 0; spawnAcc = 0;
        if (!running) { running = true; CompositionTarget.Rendering += OnFrame; }
        A.To(this, OpacityProperty, 1, 250);
    }

    // Plays the finishing flourish (everything wiped, one last puff of dust) and then fades out; done runs at the end.
    public void Stop(Action done)
    {
        if (!running) { if (done != null) done(); return; }
        stopping = true; stopDone = done; stopAt = clock.Elapsed.TotalSeconds;
        outX = GetLeft(brush) + TipX * px; outY = GetTop(brush) + TipY * px;
        if (mode == StageMode.Clean) { Emit(docL + DocW * px / 2, docT + 24 * px, 0, 10, 1); }
    }

    void End()
    {
        running = false; CompositionTarget.Rendering -= OnFrame;
        var d = stopDone; stopDone = null; stopping = false;
        if (d != null) d();
    }

    // ----- particles -----

    // Dust is a mix of single bright pixels and tiny plus-shaped sparkles. It drifts up slowly, sways a little and
    // fades in steps; most of it is one art pixel big.
    void Emit(double x, double y, double push, int count, double speed)
    {
        for (int n = 0; n < count; n++)
        {
            Particle p = null;
            foreach (var q in particles) if (!q.Alive) { p = q; break; }
            if (p == null)
            {
                if (particles.Count >= 64) return;
                p = new Particle();
                particles.Add(p);
            }
            bool sparkle = dust.Count > 0 && Rng.NextDouble() < .22;
            if (p.Img != null) p.Img.Visibility = Visibility.Collapsed;
            if (p.Dot != null) p.Dot.Visibility = Visibility.Collapsed;
            p.Spark = sparkle;
            if (sparkle)
            {
                if (p.Img == null) { p.Img = new Image(); RenderOptions.SetBitmapScalingMode(p.Img, BitmapScalingMode.NearestNeighbor); Children.Add(p.Img); }
                var spr = dust[Rng.NextDouble() < .8 ? Rng.Next(4) % dust.Count : Math.Min(4 + Rng.Next(2), dust.Count - 1)];
                p.Img.Source = spr; p.Img.Width = spr.PixelWidth * px; p.Img.Height = spr.PixelHeight * px;
            }
            else
            {
                if (p.Dot == null) { p.Dot = new Rectangle(); Children.Add(p.Dot); }
                double s = (Rng.NextDouble() < .8 ? 1 : 2) * px;
                p.Dot.Width = s; p.Dot.Height = s;
                double t = Rng.NextDouble();
                bool glint = Rng.NextDouble() < .12;
                p.Dot.Fill = new SolidColorBrush(glint ? Color.FromRgb(232, 255, 248) : Color.FromRgb((byte)(212 + (111 - 212) * t), (byte)(255 + (243 - 255) * t), (byte)(74 + (255 - 74) * t)));
            }
            p.X = x + (Rng.NextDouble() - .5) * 9 * px; p.Y = y + (Rng.NextDouble() - .5) * 3 * px;
            p.Vx = ((Rng.NextDouble() - .5) * 34 + push * .3) * speed; p.Vy = (-8 - Rng.NextDouble() * 22) * speed;
            p.Age = 0; p.Life = 1.5 + Rng.NextDouble() * 1.0; p.Phase = Rng.NextDouble() * 6.28; p.Alive = true;
            p.El.Visibility = Visibility.Visible; p.El.Opacity = 1;
            SetLeft(p.El, Snap(p.X)); SetTop(p.El, Snap(p.Y));
        }
    }

    void StepParticles(double dt)
    {
        foreach (var p in particles)
        {
            if (!p.Alive) continue;
            p.Age += dt;
            if (p.Age >= p.Life) { p.Alive = false; p.El.Visibility = Visibility.Collapsed; continue; }
            p.Vy += 3 * dt;                    // almost weightless
            p.Vx *= (1 - 1.1 * dt);            // air drag
            p.Vy *= (1 - .7 * dt);
            p.X += (p.Vx + Math.Sin(p.Age * 2.6 + p.Phase) * 7) * dt; p.Y += p.Vy * dt;
            double f = p.Age / p.Life;
            // stepped fade (pixel-art style) instead of a smooth one
            p.El.Opacity = f < .5 ? 1 : (f < .68 ? .8 : (f < .82 ? .55 : (f < .93 ? .3 : .15)));
            SetLeft(p.El, Snap(p.X)); SetTop(p.El, Snap(p.Y));
        }
    }
    // ----- frame loop -----

    const double Period = 1.55;

    void OnFrame(object s, EventArgs e)
    {
        double t = clock.Elapsed.TotalSeconds, dt = Math.Min(.05, t - last); last = t;
        Advance(t, dt);
        if (stopping && t - stopAt > 1.3) { stopping = false; A.To(this, OpacityProperty, 0, 220, A.Out, 0, null, End); }
    }

    void Advance(double t, double dt)
    {
        if (mode == StageMode.Clean) UpdateClean(t, dt); else UpdateScan(t, dt);
        StepParticles(dt);
    }

    // Draws the scene as it looks `seconds` into the loop without running the frame loop (used by the visual tests).
    public void Preview(StageMode m, double seconds)
    {
        if (!laidOut) Layout();
        Play(m);
        running = false; CompositionTarget.Rendering -= OnFrame; // Play subscribed; drive the clock by hand instead
        BeginAnimation(OpacityProperty, null); Opacity = 1;
        double t = 0, step = 1.0 / 60;
        last = 0;
        while (t < seconds) { t += step; Advance(t, step); }
    }

    static double Ease(double u) { return 1 - Math.Pow(1 - u, 3); }

    void UpdateClean(double t, double dt)
    {
        double docW = DocW * px, docH = DocH * px;
        double offX = Width + 12 * px, offY = -10 * px; // parking spot outside the stage, top right
        double tipX, tipY, angle = 0; bool sweeping = false; double reveal;

        if (stopping)
        {
            // wipe finished: brush flies off, document stays clean
            double u = Math.Min(1, (t - stopAt) / .35);
            tipX = outX + (offX - outX) * Ease(u); tipY = outY + (offY - outY) * Ease(u);
            reveal = docH; clean.Opacity = 1;
        }
        else
        {
            double ct = t % Period; int cycle = (int)(t / Period);
            const double tin = .14, tsweep = 1.22;
            double startX = docL + (MetaLeft + MetaRight) / 2 * px + (MetaRight - MetaLeft) / 2 * px * .92, startY = docT + MetaTop * px;
            if (ct < tin)
            {
                double u = Ease(ct / tin);
                tipX = offX + (startX - offX) * u; tipY = offY + (startY - offY) * u;
                reveal = cycle == 0 ? 0 : docH;
                // the lines come back while the next brush enters
                clean.Opacity = cycle == 0 ? 0 : 1 - ct / tin;
            }
            else if (ct < tsweep)
            {
                double u = (ct - tin) / (tsweep - tin);
                double cx = docL + (MetaLeft + MetaRight) / 2 * px, amp = (MetaRight - MetaLeft) / 2 * px * .92;
                tipX = cx + amp * Math.Cos(3 * Math.PI * u);
                tipY = docT + (MetaTop + (MetaBottom - MetaTop) * u) * px;
                angle = -13 * Math.Sin(3 * Math.PI * u);
                reveal = (tipY - docT) + 2 * px;
                clean.Opacity = 1; sweeping = true;
            }
            else
            {
                double u = Ease((ct - tsweep) / (Period - tsweep));
                double endX = docL + MetaLeft * px, endY = docT + MetaBottom * px;
                tipX = endX + (offX - endX) * u; tipY = endY + (offY - endY) * u;
                reveal = docH; clean.Opacity = 1;
                if (ct - tsweep < dt * 2) Emit(docL + docW / 2, docT + 24 * px, 0, 8, 1); // a last soft puff when the wipe ends
            }
        }

        brushRot.Angle = angle;
        SetLeft(brush, Snap(tipX - TipX * px)); SetTop(brush, Snap(tipY - TipY * px));
        clean.Clip = new RectangleGeometry(new Rect(0, 0, docW, Math.Max(0, Math.Min(docH, reveal - 0))));
        // reveal is measured from the top of the document; the clip is in the image's own coordinates
        if (sweeping)
        {
            spawnAcc += dt * 30;
            int n = (int)spawnAcc; spawnAcc -= n;
            double dir = -Math.Sin(3 * Math.PI * ((t % Period - .14) / 1.08)); // push the dust the way the brush moves
            if (n > 0) Emit(tipX, tipY - 2 * px, dir * 70, n, 1);
        }
    }

    void UpdateScan(double t, double dt)
    {
        double docW = DocW * px, docH = DocH * px;
        double cx = docL + docW / 2, cy = docT + docH * .58;
        double x = cx + docW * .42 * Math.Sin(2 * Math.PI * t / 2.3);
        double y = cy + docH * .26 * Math.Sin(2 * Math.PI * t / 1.55 + .6);
        double ex = stopping ? Math.Min(1, (t - stopAt) / .3) : 0;
        if (stopping) { x = x + (cx + docW * .62 - x) * Ease(ex); y = y + (docT - 6 * px - y) * Ease(ex); }
        SetLeft(lupa, Snap(x - LensX * px)); SetTop(lupa, Snap(y - LensY * px));

        double[] rowsMid = { 19.8, 25.5, 31.4 };
        for (int i = 0; i < bars.Length; i++)
        {
            double dy = Math.Abs(y - (docT + rowsMid[i] * px)) / px;
            double dx = Math.Abs(x - (docL + (MetaLeft + MetaRight) / 2 * px)) / px;
            double a = Math.Max(0, 1 - dy / 4.5) * Math.Max(0, 1 - dx / 16);
            bars[i].Opacity = stopping ? 0 : Math.Round(a * 3) / 3 * .55; // stepped glow
        }
        spawnAcc += dt * 5;
        int n = (int)spawnAcc; spawnAcc -= n;
        if (n > 0 && !stopping) Emit(x, y, 0, n, .5);
    }
}
