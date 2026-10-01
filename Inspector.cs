using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

// ---------- metadata reading ----------

class TagInfo
{
    public string G0, G1, Name, Value, Cat;
    public bool Removed;
}

static class Inspector
{
    static readonly Regex LineRx = new Regex(@"^\[([^\]:]*)(?::([^\]]*))?\]\s+(\S+)\s*:\s?(.*)$", RegexOptions.Compiled);
    static readonly Regex BinRx = new Regex(@"^\(Binary data (\d+) bytes", RegexOptions.Compiled);

    // The categories reuse the argument lists behind the Options switches, so what the inspector files under
    // "Location" is exactly what the Location switch removes.
    class Matcher { public string Group; public bool All; public Regex Re; }
    static Dictionary<string, List<Matcher>> matchers;
    static readonly string[] Order = { "c2pa", "location", "device", "dates", "author", "thumbs", "history" };
    public static readonly string[] DisplayOrder = { "location", "device", "dates", "author", "thumbs", "history", "c2pa", "other" };

    static void InitMatchers()
    {
        if (matchers != null) return;
        var m = new Dictionary<string, List<Matcher>>();
        foreach (var o in Opts.Items)
        {
            var list = new List<Matcher>();
            foreach (string arg in o.Args)
            {
                string a = arg.TrimStart('-').TrimEnd('=');
                string g = null, n = a; int c = a.IndexOf(':');
                if (c > 0) { g = a.Substring(0, c); n = a.Substring(c + 1); }
                var mt = new Matcher { Group = g };
                if (n.Equals("all", StringComparison.OrdinalIgnoreCase)) mt.All = true;
                else mt.Re = new Regex("^" + Regex.Escape(n).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);
                list.Add(mt);
            }
            m[o.Key] = list;
        }
        matchers = m;
    }

    static bool Eq(string a, string b) { return a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

    // category key, "other", or null for things that are not metadata (file system info, ExifTool's own composites)
    public static string Classify(string g0, string g1, string name)
    {
        if (g0 == "File" || g0 == "System" || g0 == "ExifTool" || g0 == "Composite") return null;
        InitMatchers();
        foreach (string key in Order)
            foreach (var m in matchers[key])
            {
                if (m.Group != null && !Eq(m.Group, g0) && !Eq(m.Group, g1)) continue;
                if (m.All || (m.Re != null && m.Re.IsMatch(name))) return key;
            }
        return "other";
    }

    static string Short(string v)
    {
        var b = BinRx.Match(v);
        if (b.Success)
        {
            long n = long.Parse(b.Groups[1].Value);
            return "(binary, " + (n >= 1048576 ? (n / 1048576.0).ToString("0.0") + " MB" : Math.Max(1, n / 1024) + " KB") + ")";
        }
        return v.Length > 140 ? v.Substring(0, 140) + "..." : v;
    }

    public static List<TagInfo> Read(string file)
    {
        string exe = Tools.Exe();
        if (exe == null) throw new InvalidOperationException("Could not set up exiftool");
        // arguments go through a UTF-8 argument file, same as when cleaning, so any file name works
        string argFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cleanmetadata-" + Guid.NewGuid().ToString("N") + ".args");
        File.WriteAllText(argFile, "-a\n-G0:1\n-s\n-charset\nutf8\n" + file + "\n", new UTF8Encoding(false));
        string output; var err = new StringBuilder(); int code;
        try
        {
            var psi = new ProcessStartInfo(exe, "-charset filename=utf8 -@ \"" + argFile + "\"");
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            using (var p = new Process())
            {
                p.StartInfo = psi;
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                p.Start(); p.BeginErrorReadLine();
                output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(); code = p.ExitCode;
            }
        }
        finally { try { File.Delete(argFile); } catch (Exception) { } }

        var tags = new List<TagInfo>();
        string readError = null;
        foreach (string line in output.Split('\n'))
        {
            var m = LineRx.Match(line.TrimEnd('\r'));
            if (!m.Success) continue;
            string g0 = m.Groups[1].Value, g1 = m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : g0, name = m.Groups[3].Value;
            // ExifTool reports unreadable files as an "Error" tag in its own output, usually with exit code 0
            if (g0 == "ExifTool" && name == "Error") { readError = m.Groups[4].Value.Trim(); continue; }
            string cat = Classify(g0, g1, name);
            if (cat == null) continue;
            tags.Add(new TagInfo { G0 = g0, G1 = g1, Name = name, Value = Short(m.Groups[4].Value), Cat = cat });
        }
        if (tags.Count == 0 && (readError != null || code != 0))
        {
            string e = err.ToString().Trim();
            throw new InvalidOperationException(readError ?? (e.Length > 0 ? e.Split('\n')[0].Trim().Replace("Error: ", "") : "Could not read the file"));
        }
        return tags;
    }

    // marks every tag of the original that is missing from the cleaned copy
    public static void Compare(List<TagInfo> original, List<TagInfo> cleaned)
    {
        var left = new HashSet<string>();
        foreach (var t in cleaned) left.Add(t.G1 + "|" + t.Name);
        foreach (var t in original) t.Removed = !left.Contains(t.G1 + "|" + t.Name);
    }
}

// ---------- animated number ----------

class Ticker : TextBlock
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register("Value", typeof(double), typeof(Ticker),
        new PropertyMetadata(0.0, delegate(DependencyObject d, DependencyPropertyChangedEventArgs e) { ((Ticker)d).Text = ((int)Math.Round((double)e.NewValue)).ToString(); }));
    public double Value { get { return (double)GetValue(ValueProperty); } set { SetValue(ValueProperty, value); } }
}

// ---------- panel ----------

// Slide-over panel: a magnifier scans a pixel-art document while ExifTool reads the file, then the tags are listed by
// category. When a cleaned copy exists, every tag is marked as removed or kept.
class InspectorPanel
{
    const string Xaml = @"
<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Visibility='Collapsed'>
  <Border x:Name='Scrim' Background='#B3000000' Opacity='0'/>
  <Border x:Name='Box' HorizontalAlignment='Right' Width='430' Background='#F7101013' BorderBrush='#2E2E36' BorderThickness='1,0,0,0'>
    <Border.RenderTransform><TranslateTransform x:Name='BoxT' X='430'/></Border.RenderTransform>
    <Border.Effect><DropShadowEffect Color='#000000' BlurRadius='30' ShadowDepth='0' Opacity='.6'/></Border.Effect>
    <Grid>
      <Grid.RowDefinitions><RowDefinition Height='56'/><RowDefinition Height='*'/><RowDefinition Height='Auto'/></Grid.RowDefinitions>
      <Grid Margin='20,0,10,0'>
        <StackPanel VerticalAlignment='Center' Margin='0,0,44,0'>
          <TextBlock Text='Inspector' FontSize='16' FontWeight='SemiBold'/>
          <TextBlock x:Name='Sub' FontFamily='Cascadia Mono, Consolas' FontSize='11' Foreground='#74747F' TextTrimming='CharacterEllipsis'/>
        </StackPanel>
        <Button x:Name='CloseBtn' HorizontalAlignment='Right' VerticalAlignment='Center'/>
      </Grid>
      <ScrollViewer Grid.Row='1' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled' Focusable='False'>
        <StackPanel Margin='20,4,14,20'>
          <Border x:Name='StageBox' Height='158' CornerRadius='14' Background='#0E0E11' BorderBrush='#232329' BorderThickness='1' Margin='0,0,0,16'>
            <Grid>
              <ContentControl x:Name='StageHost' Width='240' Height='132' HorizontalAlignment='Center' VerticalAlignment='Top' Margin='0,8,0,0'/>
              <TextBlock x:Name='StageText' Text='reading metadata...' FontFamily='Cascadia Mono, Consolas' FontSize='11.5' Foreground='#74747F' HorizontalAlignment='Center' VerticalAlignment='Bottom' Margin='0,0,0,10'/>
            </Grid>
          </Border>
          <StackPanel x:Name='Summary' Orientation='Horizontal' Margin='2,0,0,18' Visibility='Collapsed'/>
          <TextBlock x:Name='ErrText' Visibility='Collapsed' Foreground='#FF5F56' FontFamily='Cascadia Mono, Consolas' FontSize='12' TextWrapping='Wrap'/>
          <StackPanel x:Name='Cats'/>
        </StackPanel>
      </ScrollViewer>
      <Border Grid.Row='2' BorderBrush='#232329' BorderThickness='0,1,0,0' Padding='20,12'>
        <StackPanel Orientation='Horizontal' HorizontalAlignment='Right'>
          <Button x:Name='RevealBtn' Content='Show clean copy' Visibility='Collapsed' Margin='0,0,8,0'/>
          <Button x:Name='CleanBtn' Content='Clean this file' Visibility='Collapsed'/>
        </StackPanel>
      </Border>
    </Grid>
  </Border>
</Grid>";

    const string CardXaml = @"
<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        CornerRadius='12' BorderThickness='1' BorderBrush='#232329' Margin='0,0,0,8' Background='#EB141418' Opacity='0' RenderTransformOrigin='.5,.5'>
  <Border.RenderTransform><TranslateTransform x:Name='Ty' Y='10'/></Border.RenderTransform>
  <StackPanel>
    <Button x:Name='Hd' Cursor='Hand' Focusable='False'>
      <Button.Template><ControlTemplate TargetType='Button'><Border Background='Transparent' CornerRadius='12'><ContentPresenter/></Border></ControlTemplate></Button.Template>
      <Grid Margin='14,11'>
        <Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
        <Ellipse x:Name='Dot' Width='8' Height='8' Margin='0,0,11,0'/>
        <TextBlock x:Name='Title' Grid.Column='1' FontWeight='SemiBold' Foreground='#ECECEF'/>
        <TextBlock x:Name='Count' Grid.Column='2' FontFamily='Cascadia Mono, Consolas' FontSize='11' Foreground='#74747F' Margin='0,0,12,0' VerticalAlignment='Center'/>
        <Path x:Name='Chev' Grid.Column='3' Data='M3,1.5 L7,5 L3,8.5' Stroke='#74747F' StrokeThickness='1.6' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' VerticalAlignment='Center' RenderTransformOrigin='.4,.5'>
          <Path.RenderTransform><RotateTransform x:Name='ChevRot'/></Path.RenderTransform>
        </Path>
      </Grid>
    </Button>
    <StackPanel x:Name='Body' Visibility='Collapsed' Margin='14,0,14,10'>
      <StackPanel.LayoutTransform><ScaleTransform x:Name='BodyScale' ScaleY='0'/></StackPanel.LayoutTransform>
    </StackPanel>
  </StackPanel>
</Border>";

    static readonly Dictionary<string, string[]> Meta = new Dictionary<string, string[]>
    {
        { "location", new[] { "Location", "#6FF3FF" } }, { "device", new[] { "Device and camera", "#B69CFF" } },
        { "dates", new[] { "Dates and times", "#FFB454" } }, { "author", new[] { "Author and rights", "#FF7AB6" } },
        { "thumbs", new[] { "Thumbnails and previews", "#8A8A95" } }, { "history", new[] { "Edit history and software", "#D4FF4A" } },
        { "c2pa", new[] { "C2PA / Content Credentials", "#3DDC97" } }, { "other", new[] { "Other and technical", "#74747F" } }
    };

    readonly Window w;
    readonly Panel root;
    readonly Action<string> cleanFile;
    readonly FrameworkElement view, scrim, stageBox;
    readonly TranslateTransform boxT;
    readonly TextBlock sub, stageText, errText;
    readonly Panel cats, summary;
    readonly Button cleanBtn, revealBtn;
    readonly PixelStage stage = new PixelStage();
    string src, cleanPath;
    int ticket;
    public bool IsOpen { get; private set; }

    public InspectorPanel(Window window, Panel rootGrid, UIElement before, Action<string> cleanRequest)
    {
        w = window; root = rootGrid; cleanFile = cleanRequest;
        view = (FrameworkElement)XamlReader.Parse(Xaml);
        Grid.SetRow(view, 1); Grid.SetRowSpan(view, 2);
        root.Children.Insert(root.Children.IndexOf(before), view);
        scrim = (FrameworkElement)view.FindName("Scrim"); stageBox = (FrameworkElement)view.FindName("StageBox");
        boxT = (TranslateTransform)view.FindName("BoxT");
        sub = (TextBlock)view.FindName("Sub"); stageText = (TextBlock)view.FindName("StageText"); errText = (TextBlock)view.FindName("ErrText");
        cats = (Panel)view.FindName("Cats"); summary = (Panel)view.FindName("Summary");
        cleanBtn = (Button)view.FindName("CleanBtn"); revealBtn = (Button)view.FindName("RevealBtn");
        ((ContentControl)view.FindName("StageHost")).Content = stage;

        var close = (Button)view.FindName("CloseBtn");
        close.Style = (Style)w.FindResource("Ico");
        close.Content = new System.Windows.Shapes.Path { Data = (Geometry)w.FindResource("GCross"), Stroke = Br("#A9A9B3"), StrokeThickness = 1.8, Width = 12, Height = 12, Stretch = Stretch.Uniform, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        cleanBtn.Style = (Style)w.FindResource("BtnPrimary"); revealBtn.Style = (Style)w.FindResource("Btn");
        close.Click += delegate { Close(); };
        scrim.MouseLeftButtonUp += delegate { Close(); };
        cleanBtn.Click += delegate { string f = src; Close(); if (f != null) cleanFile(f); };
        revealBtn.Click += delegate { if (cleanPath != null && File.Exists(cleanPath)) Process.Start("explorer.exe", "/select,\"" + cleanPath + "\""); };
    }

    static Brush Br(string hex) { return new SolidColorBrush(A.C(hex)); }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false; ticket++;
        stage.Stop(null);
        A.To(scrim, UIElement.OpacityProperty, 0, 220);
        A.To(boxT, TranslateTransform.XProperty, 430, 300, A.Out, 0, null, delegate { if (!IsOpen) view.Visibility = Visibility.Collapsed; });
    }

    // cleaned = path of the cleaned copy if there is one (then tags are marked removed/kept)
    public void Open(string file, string cleaned)
    {
        src = file; cleanPath = cleaned;
        int my = ++ticket;
        sub.Text = System.IO.Path.GetFileName(file); sub.ToolTip = file;
        cats.Children.Clear(); summary.Children.Clear(); summary.Visibility = Visibility.Collapsed; errText.Visibility = Visibility.Collapsed;
        cleanBtn.Visibility = Visibility.Collapsed; revealBtn.Visibility = Visibility.Collapsed;
        stageBox.Visibility = Visibility.Visible; stageBox.Height = 158; stageBox.Opacity = 1; stageText.Text = "reading metadata...";
        if (!IsOpen)
        {
            IsOpen = true;
            view.Visibility = Visibility.Visible;
            A.To(scrim, UIElement.OpacityProperty, 1, 250);
            A.To(boxT, TranslateTransform.XProperty, 0, 500, A.Spring, 0, 430, null);
        }
        stage.Play(StageMode.Scan);

        bool anim = Opts.Anim;
        ThreadPool.QueueUserWorkItem(delegate
        {
            var sw = Stopwatch.StartNew();
            List<TagInfo> orig = null; bool compared = false; string error = null;
            try
            {
                orig = Inspector.Read(file);
                if (cleaned != null && File.Exists(cleaned)) { Inspector.Compare(orig, Inspector.Read(cleaned)); compared = true; }
            }
            catch (Exception ex) { error = ex.Message; }
            while (anim && sw.ElapsedMilliseconds < 1300) Thread.Sleep(20); // let the magnifier do a few laps
            w.Dispatcher.BeginInvoke(new Action(delegate { if (my == ticket) Show(orig, compared, error); }));
        });
    }

    void Show(List<TagInfo> tags, bool compared, string error)
    {
        stage.Stop(null);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Opts.Anim ? 420 : 1) };
        int my = ticket;
        t.Tick += delegate
        {
            t.Stop();
            if (my != ticket) return;
            A.To(stageBox, UIElement.OpacityProperty, 0, 200);
            A.To(stageBox, FrameworkElement.HeightProperty, 0, 380, A.Out, 0, null, delegate { stageBox.Visibility = Visibility.Collapsed; });
            if (error != null)
            {
                errText.Text = error; errText.Visibility = Visibility.Visible;
                return;
            }
            Build(tags, compared);
        };
        t.Start();
    }

    void Build(List<TagInfo> tags, bool compared)
    {
        int removed = 0; foreach (var x in tags) if (x.Removed) removed++;
        summary.Visibility = Visibility.Visible;
        if (compared) { AddStat(removed, "removed", "#FFB454", 0); AddStat(tags.Count - removed, "kept", "#3DDC97", 1); }
        else AddStat(tags.Count, tags.Count == 1 ? "tag found" : "tags found", "#D4FF4A", 0);

        cleanBtn.Visibility = !compared ? Visibility.Visible : Visibility.Collapsed;
        revealBtn.Visibility = compared ? Visibility.Visible : Visibility.Collapsed;

        if (tags.Count == 0)
        {
            var none = new TextBlock { Text = "no metadata found", Foreground = Br("#74747F"), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Margin = new Thickness(2, 6, 0, 0) };
            cats.Children.Add(none); return;
        }
        int i = 0;
        foreach (string key in Inspector.DisplayOrder)
        {
            var list = tags.FindAll(delegate(TagInfo x) { return x.Cat == key; });
            if (list.Count == 0) continue;
            AddCard(key, list, compared, i++);
        }
    }

    void AddStat(int value, string label, string color, int index)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 28, 0), Opacity = 0 };
        var n = new Ticker { FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = Br(color), Text = "0" };
        sp.Children.Add(n);
        sp.Children.Add(new TextBlock { Text = label, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5, Foreground = Br("#74747F") });
        summary.Children.Add(sp);
        A.To(sp, UIElement.OpacityProperty, 1, 300, A.Out, 80 + index * 90, 0, null);
        // count up
        var anim = new DoubleAnimation(0, value, TimeSpan.FromMilliseconds(Opts.Anim ? 700 : 1)) { EasingFunction = A.Out, BeginTime = TimeSpan.FromMilliseconds(80 + index * 90) };
        n.BeginAnimation(Ticker.ValueProperty, anim);
    }

    void AddCard(string key, List<TagInfo> list, bool compared, int index)
    {
        var card = (FrameworkElement)XamlReader.Parse(CardXaml);
        string[] meta = Meta[key];
        ((System.Windows.Shapes.Ellipse)card.FindName("Dot")).Fill = Br(meta[1]);
        ((TextBlock)card.FindName("Title")).Text = meta[0];
        int rem = 0; foreach (var x in list) if (x.Removed) rem++;
        ((TextBlock)card.FindName("Count")).Text = compared ? rem + " removed, " + (list.Count - rem) + " kept" : list.Count.ToString();
        var body = (Panel)card.FindName("Body");
        foreach (var x in list) body.Children.Add(Row(x, compared));
        var scale = (ScaleTransform)card.FindName("BodyScale"); var rot = (RotateTransform)card.FindName("ChevRot");
        bool open = false;
        ((Button)card.FindName("Hd")).Click += delegate
        {
            open = !open;
            A.To(rot, RotateTransform.AngleProperty, open ? 90 : 0, 400, A.Spring, 0, null, null);
            if (open) { body.Visibility = Visibility.Visible; A.To(scale, ScaleTransform.ScaleYProperty, 1, 380, A.Out, 0, 0, null); }
            else A.To(scale, ScaleTransform.ScaleYProperty, 0, 220, A.Out, 0, null, delegate { if (!open) body.Visibility = Visibility.Collapsed; });
        };
        cats.Children.Add(card);
        int delay = Math.Min(index, 8) * 60;
        A.To(card, UIElement.OpacityProperty, 1, 500, A.Out, delay, 0, null);
        A.To((TranslateTransform)card.FindName("Ty"), TranslateTransform.YProperty, 0, 500, A.Out, delay, 10, null);
    }

    static FrameworkElement Row(TagInfo t, bool compared)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var mono = new FontFamily("Cascadia Mono, Consolas");
        var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center,
            Fill = Br(compared ? (t.Removed ? "#FFB454" : "#3DDC97") : "#3C3C46") };
        var name = new TextBlock { Text = t.Name, FontFamily = mono, FontSize = 11, Foreground = Br("#ECECEF"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = t.G1 + ":" + t.Name, Margin = new Thickness(0, 0, 8, 0) };
        var val = new TextBlock { Text = t.Value, FontFamily = mono, FontSize = 11, Foreground = Br(t.Removed ? "#FFB454" : "#8A8A95"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = t.Value };
        if (t.Removed) val.TextDecorations = TextDecorations.Strikethrough;
        Grid.SetColumn(name, 1); Grid.SetColumn(val, 2);
        g.Children.Add(dot); g.Children.Add(name); g.Children.Add(val);
        return g;
    }
}
