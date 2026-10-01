using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

// ---------- inline view ----------

// The inspector, shown inside a row of the file list instead of in a separate panel: a magnifier scans a pixel-art
// document while ExifTool reads the file, then the tags are listed by category. When a cleaned copy exists, every tag
// is marked as removed or kept. For a file that has not been cleaned it offers "Clean this file" (current Options) and
// "Custom", which lists only the categories the file actually has so you can pick what to remove.
class InspectView
{
    const string Xaml = @"
<StackPanel xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Border x:Name='StageBox' Height='150' CornerRadius='12' Background='#0E0E11' BorderBrush='#232329' BorderThickness='1' Margin='0,0,0,12'>
    <Grid>
      <ContentControl x:Name='StageHost' Width='224' Height='132' HorizontalAlignment='Center' VerticalAlignment='Top' Margin='0,6,0,0'/>
      <TextBlock x:Name='StageText' Text='reading metadata...' FontFamily='Cascadia Mono, Consolas' FontSize='11.5' Foreground='#74747F' HorizontalAlignment='Center' VerticalAlignment='Bottom' Margin='0,0,0,8'/>
    </Grid>
  </Border>
  <StackPanel x:Name='Summary' Orientation='Horizontal' Margin='2,0,0,14' Visibility='Collapsed'/>
  <TextBlock x:Name='ErrText' Visibility='Collapsed' Foreground='#FF5F56' FontFamily='Cascadia Mono, Consolas' FontSize='12' TextWrapping='Wrap'/>
  <StackPanel x:Name='Actions' Orientation='Horizontal' Margin='0,0,0,12' Visibility='Collapsed'>
    <Button x:Name='CleanBtn' Content='Clean this file'/>
    <Button x:Name='CustomBtn' Content='Custom' Margin='8,0,0,0'/>
    <Button x:Name='RevealBtn' Content='Show clean copy'/>
  </StackPanel>
  <Border x:Name='CustomBox' Visibility='Collapsed' CornerRadius='12' BorderThickness='1' BorderBrush='#2E2E36' Background='#EB0E0E11' Padding='14,12,14,12' Margin='0,0,0,12'>
    <Border.LayoutTransform><ScaleTransform x:Name='CustomSc' ScaleY='0'/></Border.LayoutTransform>
    <StackPanel>
      <TextBlock Text='REMOVE ONLY' FontFamily='Cascadia Mono, Consolas' FontSize='10.5' Foreground='#74747F' Margin='0,0,0,12'/>
      <StackPanel x:Name='CustomRows'/>
      <StackPanel Orientation='Horizontal' HorizontalAlignment='Right' Margin='0,4,0,0'>
        <Button x:Name='CancelBtn' Content='Cancel' Margin='0,0,8,0'/>
        <Button x:Name='GoBtn' Content='Clean selected'/>
      </StackPanel>
    </StackPanel>
  </Border>
  <StackPanel x:Name='Cats'/>
</StackPanel>";

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

    const string SwitchRowXaml = @"
<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Margin='0,0,0,12'>
  <Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
  <StackPanel Margin='0,0,14,0'>
    <TextBlock x:Name='T' FontSize='13' FontWeight='SemiBold'/>
    <TextBlock x:Name='D' FontFamily='Cascadia Mono, Consolas' FontSize='10.5' Foreground='#74747F' TextWrapping='Wrap' Margin='0,3,0,0'/>
  </StackPanel>
  <ToggleButton x:Name='Sw' Grid.Column='1' VerticalAlignment='Top' Margin='0,1,0,0'/>
</Grid>";

    static readonly Dictionary<string, string[]> Meta = new Dictionary<string, string[]>
    {
        { "location", new[] { "Location", "#6FF3FF" } }, { "device", new[] { "Device and camera", "#B69CFF" } },
        { "dates", new[] { "Dates and times", "#FFB454" } }, { "author", new[] { "Author and rights", "#FF7AB6" } },
        { "thumbs", new[] { "Thumbnails and previews", "#8A8A95" } }, { "history", new[] { "Edit history and software", "#D4FF4A" } },
        { "c2pa", new[] { "C2PA / Content Credentials", "#3DDC97" } }, { "other", new[] { "Other and technical", "#74747F" } }
    };

    readonly Window w;
    readonly Job job;
    readonly Action<string[], bool> cleanRequest;
    public readonly FrameworkElement Root;
    readonly FrameworkElement stageBox, customBox;
    readonly ScaleTransform customSc;
    readonly TextBlock stageText, errText;
    readonly Panel cats, summary, actions, customRows;
    readonly Button cleanBtn, customBtn, revealBtn, goBtn, cancelBtn;
    readonly PixelStage stage = new PixelStage();
    readonly List<ToggleButton> catSwitches = new List<ToggleButton>();
    readonly List<string> catKeys = new List<string>();
    ToggleButton restSwitch;
    List<TagInfo> tags;
    int ticket;
    bool loaded, customOpen;
    public bool Stale;   // the file changed (it was cleaned): reload the next time the view is opened

    public InspectView(Window window, Job j, Action<string[], bool> request)
    {
        w = window; job = j; cleanRequest = request;
        Root = (FrameworkElement)XamlReader.Parse(Xaml);
        stageBox = (FrameworkElement)Root.FindName("StageBox"); customBox = (FrameworkElement)Root.FindName("CustomBox");
        customSc = (ScaleTransform)Root.FindName("CustomSc");
        stageText = (TextBlock)Root.FindName("StageText"); errText = (TextBlock)Root.FindName("ErrText");
        cats = (Panel)Root.FindName("Cats"); summary = (Panel)Root.FindName("Summary");
        actions = (Panel)Root.FindName("Actions"); customRows = (Panel)Root.FindName("CustomRows");
        cleanBtn = (Button)Root.FindName("CleanBtn"); customBtn = (Button)Root.FindName("CustomBtn"); revealBtn = (Button)Root.FindName("RevealBtn");
        goBtn = (Button)Root.FindName("GoBtn"); cancelBtn = (Button)Root.FindName("CancelBtn");
        ((ContentControl)Root.FindName("StageHost")).Content = stage;

        cleanBtn.Style = (Style)w.FindResource("BtnPrimary"); goBtn.Style = (Style)w.FindResource("BtnPrimary");
        customBtn.Style = (Style)w.FindResource("Btn"); revealBtn.Style = (Style)w.FindResource("Btn"); cancelBtn.Style = (Style)w.FindResource("Btn");
        cleanBtn.Click += delegate { cleanRequest(null, false); };
        customBtn.Click += delegate { SetCustom(!customOpen); };
        cancelBtn.Click += delegate { SetCustom(false); };
        goBtn.Click += delegate
        {
            var keys = new List<string>();
            for (int i = 0; i < catSwitches.Count; i++) if (catSwitches[i].IsChecked == true) keys.Add(catKeys[i]);
            bool rest = restSwitch != null && restSwitch.IsChecked == true;
            SetCustom(false);
            cleanRequest(keys.ToArray(), rest);
        };
        revealBtn.Click += delegate
        {
            if (job.Out != null && File.Exists(job.Out)) Process.Start("explorer.exe", "/select,\"" + job.Out + "\"");
        };
    }

    static Brush Br(string hex) { return new SolidColorBrush(A.C(hex)); }

    public void EnsureLoaded() { if (!loaded || Stale) Load(); }

    // the file is being cleaned: no actions until the result is back
    public void CleaningStarted()
    {
        Stale = true;
        actions.Visibility = Visibility.Collapsed;
        SetCustom(false);
    }

    void Load()
    {
        loaded = true; Stale = false;
        int my = ++ticket;
        cats.Children.Clear(); summary.Children.Clear(); summary.Visibility = Visibility.Collapsed;
        errText.Visibility = Visibility.Collapsed; actions.Visibility = Visibility.Collapsed;
        SetCustom(false);
        stageBox.Visibility = Visibility.Visible; stageBox.Height = 150; stageBox.Opacity = 1; stageText.Text = "reading metadata...";
        stage.Play(StageMode.Scan);

        string src = job.Src;
        string cleaned = (job.State == State.Done || job.State == State.Warn) && job.Out != null && !job.Replaced ? job.Out : null;
        bool anim = Opts.Anim;
        ThreadPool.QueueUserWorkItem(delegate
        {
            var sw = Stopwatch.StartNew();
            List<TagInfo> orig = null; bool compared = false; string error = null;
            try
            {
                orig = Inspector.Read(src);
                if (cleaned != null && File.Exists(cleaned)) { Inspector.Compare(orig, Inspector.Read(cleaned)); compared = true; }
            }
            catch (Exception ex) { error = ex.Message; }
            while (anim && sw.ElapsedMilliseconds < 1300) Thread.Sleep(20); // let the magnifier do a few laps
            w.Dispatcher.BeginInvoke(new Action(delegate { if (my == ticket) Show(orig, compared, error); }));
        });
    }

    void Show(List<TagInfo> found, bool compared, string error)
    {
        stage.Stop(null);
        int my = ticket;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Opts.Anim ? 420 : 1) };
        t.Tick += delegate
        {
            t.Stop();
            if (my != ticket) return;
            A.To(stageBox, UIElement.OpacityProperty, 0, 200);
            A.To(stageBox, FrameworkElement.HeightProperty, 0, 380, A.Out, 0, null, delegate { stageBox.Visibility = Visibility.Collapsed; });
            if (error != null) { errText.Text = error; errText.Visibility = Visibility.Visible; ShowActions(false, false); return; }
            Build(found, compared);
        };
        t.Start();
    }

    void ShowActions(bool compared, bool hasTags)
    {
        bool canClean = job.State == State.Idle || job.State == State.Error;
        cleanBtn.Visibility = canClean ? Visibility.Visible : Visibility.Collapsed;
        customBtn.Visibility = canClean && hasTags ? Visibility.Visible : Visibility.Collapsed;
        revealBtn.Visibility = compared ? Visibility.Visible : Visibility.Collapsed;
        actions.Visibility = canClean || compared ? Visibility.Visible : Visibility.Collapsed;
    }

    void Build(List<TagInfo> found, bool compared)
    {
        tags = found;
        int removed = 0; foreach (var x in found) if (x.Removed) removed++;
        summary.Visibility = Visibility.Visible;
        if (compared) { AddStat(removed, "removed", "#FFB454", 0); AddStat(found.Count - removed, "kept", "#3DDC97", 1); }
        else AddStat(found.Count, found.Count == 1 ? "tag found" : "tags found", "#D4FF4A", 0);

        if (found.Count == 0)
        {
            cats.Children.Add(new TextBlock { Text = "no metadata found", Foreground = Br("#74747F"), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Margin = new Thickness(2, 6, 0, 8) });
        }
        else
        {
            int i = 0;
            foreach (string key in Inspector.DisplayOrder)
            {
                var list = found.FindAll(delegate(TagInfo x) { return x.Cat == key; });
                if (list.Count == 0) continue;
                AddCard(key, list, compared, i++);
            }
        }
        BuildCustom();
        ShowActions(compared, found.Count > 0);
    }

    // one switch per category the file really has, plus "everything else"
    void BuildCustom()
    {
        customRows.Children.Clear(); catSwitches.Clear(); catKeys.Clear(); restSwitch = null;
        foreach (string key in Inspector.DisplayOrder)
        {
            if (key == "other") continue;
            int n = tags.FindAll(delegate(TagInfo x) { return x.Cat == key; }).Count;
            if (n == 0) continue;
            var sw = AddSwitchRow(Meta[key][0], n + (n == 1 ? " tag" : " tags"), true);
            catSwitches.Add(sw); catKeys.Add(key);
        }
        int others = tags.FindAll(delegate(TagInfo x) { return x.Cat == "other"; }).Count;
        restSwitch = AddSwitchRow("Everything else", others + " other tags and anything not listed above (like the All preset)", false);
        restSwitch.Click += delegate
        {
            bool all = restSwitch.IsChecked == true;
            foreach (var s in catSwitches) { s.IsChecked = all ? true : s.IsChecked; s.IsEnabled = !all; }
            UpdateGo();
        };
        foreach (var s in catSwitches) s.Click += delegate { UpdateGo(); };
        UpdateGo();
    }

    ToggleButton AddSwitchRow(string title, string desc, bool on)
    {
        var row = (FrameworkElement)XamlReader.Parse(SwitchRowXaml);
        ((TextBlock)row.FindName("T")).Text = title; ((TextBlock)row.FindName("D")).Text = desc;
        var sw = (ToggleButton)row.FindName("Sw"); sw.Style = (Style)w.FindResource("Switch"); sw.IsChecked = on;
        customRows.Children.Add(row);
        return sw;
    }

    void UpdateGo()
    {
        bool any = restSwitch != null && restSwitch.IsChecked == true;
        foreach (var s in catSwitches) if (s.IsChecked == true) any = true;
        goBtn.IsEnabled = any;
        A.To(goBtn, UIElement.OpacityProperty, any ? 1 : .4, 200);
    }

    void SetCustom(bool on)
    {
        if (on == customOpen) return;
        customOpen = on;
        if (on)
        {
            customBox.Visibility = Visibility.Visible;
            A.To(customSc, ScaleTransform.ScaleYProperty, 1, 380, A.Out, 0, 0, null);
            Root.Dispatcher.BeginInvoke(new Action(delegate { customBox.BringIntoView(); }), DispatcherPriority.Background);
        }
        else A.To(customSc, ScaleTransform.ScaleYProperty, 0, 220, A.Out, 0, null, delegate { if (!customOpen) customBox.Visibility = Visibility.Collapsed; });
    }

    void AddStat(int value, string label, string color, int index)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 28, 0), Opacity = 0 };
        var n = new Ticker { FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = Br(color), Text = "0" };
        sp.Children.Add(n);
        sp.Children.Add(new TextBlock { Text = label, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5, Foreground = Br("#74747F") });
        summary.Children.Add(sp);
        A.To(sp, UIElement.OpacityProperty, 1, 300, A.Out, 80 + index * 90, 0, null);
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