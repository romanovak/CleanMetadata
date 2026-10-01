using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

// CleanMetadata: drag-and-drop window (WPF) that strips metadata, including C2PA / Content Credentials, using exiftool.
// Writes a "_clean" copy next to the original; the original is never touched. exiftool is embedded in the exe.

[assembly: AssemblyTitle("CleanMetadata - GUI for ExifTool")]
[assembly: AssemblyDescription("Drag-and-drop metadata and C2PA remover")]
[assembly: AssemblyProduct("CleanMetadata")]
[assembly: AssemblyCompany("CleanMetadata project")]
[assembly: AssemblyCopyright("Copyright (c) 2026 CleanMetadata contributors. MIT License.")]
[assembly: AssemblyInformationalVersion("1.0.0")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
static class Native
{
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
}

// ---------- embedded exiftool ----------

static class Tools
{
    static readonly object L = new object();
    static string exe;

    // Unpacks the embedded ExifTool once per app version and returns the path to exiftool.exe (null on failure).
    public static string Exe()
    {
        lock (L)
        {
            if (exe != null) return exe;
            try
            {
                string ver = Assembly.GetExecutingAssembly().GetName().Version.ToString();
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanMetadata", "exiftool-" + ver);
                string path = System.IO.Path.Combine(dir, "exiftool.exe");
                // a named mutex keeps two running instances from unpacking at the same time
                using (var m = new Mutex(false, "CleanMetadata.Unpack"))
                {
                    try { m.WaitOne(); } catch (AbandonedMutexException) { }
                    try { if (!File.Exists(path)) Unpack(dir); }
                    finally { m.ReleaseMutex(); }
                }
                exe = path;
            }
            catch (Exception) { }
            return exe;
        }
    }

    // Version string of the bundled ExifTool, for the About dialog.
    public static string Version()
    {
        string x = Exe();
        if (x == null) return "?";
        try
        {
            var psi = new ProcessStartInfo(x, "-ver") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                return o.Length > 0 ? o : "?";
            }
        }
        catch (Exception) { return "?"; }
    }
    // Extracts into a temp folder first and renames it at the end, so a crash never leaves a half-unpacked copy in place.
    static void Unpack(string dir)
    {
        string tmp = dir + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        string root = System.IO.Path.GetFullPath(tmp) + "\\";
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("tools.zip"))
        using (var zip = new ZipArchive(s))
            foreach (var en in zip.Entries)
            {
                string dest = System.IO.Path.GetFullPath(System.IO.Path.Combine(tmp, en.FullName.Replace('/', '\\')));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // never write outside the target folder
                if (en.Name.Length == 0) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest));
                using (var input = en.Open())
                using (var o = File.Create(dest)) input.CopyTo(o);
            }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.Move(tmp, dir);
    }
}
enum State { Queued, Working, Done, Warn, Error }

class Job
{
    public string Src, Out, Note;
    public State State = State.Queued;
    public RowView Row;
    public List<string> Args;   // ExifTool arguments, snapshotted when the file is added
    public bool CheckC2pa;
}

// ---------- options ----------

class Option
{
    public string Key, Title, Desc;
    public string[] Args;
    public bool On;
}

// What to remove. "all" strips everything with -all= (keeping color profile / orientation on request);
// the other presets delete only the selected categories. Settings persist in %LOCALAPPDATA%\CleanMetadata\settings.ini.
static class Opts
{
    public static readonly Option[] Items =
    {
        new Option { Key = "location", Title = "Location", Desc = "GPS coordinates, city, country",
            Args = new[] { "-GPS:all=", "-GPS*=", "-City=", "-State=", "-Country=", "-Location=", "-Sub-location=", "-Province-State=", "-Country-PrimaryLocationName=", "-CountryCode=" } },
        new Option { Key = "device", Title = "Device and camera", Desc = "make, model, lens, serial numbers, maker notes",
            Args = new[] { "-Make=", "-Model=", "-LensMake=", "-LensModel=", "-LensInfo=", "-LensID=", "-SerialNumber=", "-InternalSerialNumber=", "-LensSerialNumber=", "-BodySerialNumber=", "-MakerNotes:all=" } },
        new Option { Key = "dates", Title = "Dates and times", Desc = "capture, creation and modification dates",
            Args = new[] { "-AllDates=", "-CreateDate=", "-ModifyDate=", "-DateTimeOriginal=", "-DateTimeDigitized=", "-DateCreated=", "-TimeCreated=", "-MediaCreateDate=", "-MediaModifyDate=", "-TrackCreateDate=", "-TrackModifyDate=", "-MetadataDate=" } },
        new Option { Key = "author", Title = "Author and rights", Desc = "artist, copyright, captions, keywords (EXIF, IPTC, XMP)",
            Args = new[] { "-IPTC:all=", "-Artist=", "-Copyright=", "-Creator=", "-Rights=", "-Author=", "-By-line=", "-Credit=", "-Source=", "-ImageDescription=", "-Caption-Abstract=", "-Description=", "-Title=", "-Subject=", "-Keywords=", "-Comment=", "-UserComment=", "-XPAuthor=", "-XPComment=", "-XPKeywords=", "-XPTitle=", "-XPSubject=", "-OwnerName=", "-Publisher=", "-Headline=" } },
        new Option { Key = "thumbs", Title = "Thumbnails and previews", Desc = "embedded preview images",
            Args = new[] { "-ThumbnailImage=", "-PreviewImage=", "-JpgFromRaw=", "-OtherImage=", "-PhotoshopThumbnail=", "-IFD1:all=" } },
        new Option { Key = "history", Title = "Edit history and software", Desc = "editing software, XMP document IDs and history",
            Args = new[] { "-Software=", "-ProcessingSoftware=", "-CreatorTool=", "-History*=", "-DerivedFrom*=", "-DocumentID=", "-InstanceID=", "-OriginalDocumentID=", "-XMP-xmpMM:all=", "-Encoder=" } },
        new Option { Key = "c2pa", Title = "C2PA / Content Credentials", Desc = "provenance manifests (AI and edit labels)",
            Args = new[] { "-JUMBF:all=" } }
    };

    static readonly string[] PrivacyKeys = { "location", "device", "dates", "author", "thumbs" };
    static readonly string[] ProvenanceKeys = { "c2pa", "history" };
    static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif", ".tif", ".tiff" };

    public static string Preset = "all"; // all | privacy | provenance | custom
    public static bool KeepIcc = true, KeepOri = true;
    public static bool Anim = true; // play the cleaning animation (adds about a second per run)
    public static string TagText = "";

    static Opts() { Apply("all"); } // defaults: everything on

    static string FilePath()
    {
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanMetadata", "settings.ini");
    }

    public static bool IsImage(string file) { return Array.IndexOf(ImageExt, System.IO.Path.GetExtension(file).ToLowerInvariant()) >= 0; }

    public static void Apply(string preset)
    {
        Preset = preset;
        if (preset == "custom") return;
        foreach (var o in Items)
            o.On = preset == "all"
                || (preset == "privacy" && Array.IndexOf(PrivacyKeys, o.Key) >= 0)
                || (preset == "provenance" && Array.IndexOf(ProvenanceKeys, o.Key) >= 0);
    }

    public static void Reset() { Apply("all"); KeepIcc = true; KeepOri = true; Anim = true; TagText = ""; }

    // tag names the user typed; anything that isn't a plain ExifTool tag name is reported back instead of being passed on
    public static List<string> Tags(out List<string> rejected)
    {
        var ok = new List<string>(); rejected = new List<string>();
        foreach (string t in TagText.Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Za-z0-9_*:\-]+$") && !t.StartsWith("-")) ok.Add(t);
            else rejected.Add(t);
        }
        return ok;
    }

    public static List<string> Build(bool image)
    {
        var a = new List<string>();
        if (Preset == "all")
        {
            a.Add("-all=");
            if (image && (KeepIcc || KeepOri))
            {
                a.Add("-tagsfromfile"); a.Add("@");
                if (KeepIcc) a.Add("-ColorSpaceTags");
                if (KeepOri) a.Add("-Orientation");
            }
            return a;
        }
        foreach (var o in Items) if (o.On) a.AddRange(o.Args);
        List<string> bad;
        foreach (string t in Tags(out bad)) a.Add("-" + t + "=");
        return a;
    }

    public static bool WantsC2pa()
    {
        if (Preset == "all") return true;
        foreach (var o in Items) if (o.Key == "c2pa" && o.On) return true;
        return false;
    }

    public static void Load()
    {
        try
        {
            string p = FilePath();
            if (!File.Exists(p)) return;
            var kv = new Dictionary<string, string>();
            foreach (string line in File.ReadAllLines(p))
            {
                int i = line.IndexOf('=');
                if (i > 0) kv[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
            string v;
            if (kv.TryGetValue("preset", out v) && (v == "all" || v == "privacy" || v == "provenance" || v == "custom")) Preset = v;
            if (kv.TryGetValue("keepIcc", out v)) KeepIcc = v == "1";
            if (kv.TryGetValue("keepOri", out v)) KeepOri = v == "1";
            if (kv.TryGetValue("anim", out v)) Anim = v != "0";
            if (kv.TryGetValue("tags", out v)) TagText = v;
            Apply(Preset);
            if (Preset == "custom") foreach (var o in Items) if (kv.TryGetValue("opt." + o.Key, out v)) o.On = v == "1";
        }
        catch (Exception) { }
    }

    public static void Save()
    {
        try
        {
            string p = FilePath();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
            var lines = new List<string> { "preset=" + Preset, "keepIcc=" + (KeepIcc ? "1" : "0"), "keepOri=" + (KeepOri ? "1" : "0"), "anim=" + (Anim ? "1" : "0"), "tags=" + TagText.Replace("\r", " ").Replace("\n", " ") };
            foreach (var o in Items) lines.Add("opt." + o.Key + "=" + (o.On ? "1" : "0"));
            File.WriteAllLines(p, lines.ToArray());
        }
        catch (Exception) { }
    }
}
// ---------- cleaning ----------

static class Cleaner
{
    static readonly object Sync = new object();
    static Process running;
    public static volatile bool Cancelled;

    // Stops the current exiftool run (window closing) so no half-written copy is left behind.
    public static void Cancel()
    {
        Cancelled = true;
        lock (Sync)
        {
            if (running != null) { try { running.Kill(); } catch (Exception) { } }
        }
    }

    static string ShortErr(string msg)
    {
        if (msg.Length == 0) return "Could not create the copy";
        string s = msg.Split('\n')[0].Trim();
        if (s.StartsWith("Error: ")) s = s.Substring(7);
        int i = s.IndexOf(" - ");
        return i > 0 ? s.Substring(0, i) : s;
    }

    static void Fail(Job j, string note) { j.State = State.Error; j.Note = note; }

    static void DeleteQuiet(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }

    public static void Clean(Job j)
    {
        string exiftool = Tools.Exe();
        if (exiftool == null) { Fail(j, "Could not set up exiftool"); return; }

        string f = j.Src;
        j.Out = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f),
            System.IO.Path.GetFileNameWithoutExtension(f) + "_clean" + System.IO.Path.GetExtension(f));
        if (File.Exists(j.Out)) File.Delete(j.Out);

        if (j.Args == null || j.Args.Count == 0) { Fail(j, "Nothing selected in Options"); return; }
        var a = new List<string>(j.Args);
        a.AddRange(new[] { "-o", j.Out, f });

        // Arguments go through a UTF-8 argument file: plain command-line arguments break on file names with
        // characters outside the ANSI code page (CJK, emoji, ...).
        string argFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cleanmetadata-" + Guid.NewGuid().ToString("N") + ".args");
        File.WriteAllText(argFile, string.Join("\n", a.ToArray()) + "\n", new UTF8Encoding(false));

        int code = -1;
        var msg = new StringBuilder();
        try
        {
            var psi = new ProcessStartInfo(exiftool, "-charset filename=utf8 -@ \"" + argFile + "\"");
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using (var p = new Process())
            {
                p.StartInfo = psi;
                DataReceivedEventHandler grab = delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (msg) msg.AppendLine(e.Data); };
                p.OutputDataReceived += grab; p.ErrorDataReceived += grab;
                lock (Sync)
                {
                    if (Cancelled) { Fail(j, "Cancelled"); return; }
                    p.Start(); running = p;
                }
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                p.WaitForExit();
                code = p.ExitCode;
                lock (Sync) running = null;
            }
        }
        finally { try { File.Delete(argFile); } catch (Exception) { } }

        if (Cancelled || code != 0 || !File.Exists(j.Out))
        {
            DeleteQuiet(j.Out);
            Fail(j, Cancelled ? "Cancelled" : ShortErr(msg.ToString()));
            return;
        }

        // the C2PA manifest sits at the start of the file (at the end in some mp4s); check both ends
        var latin = Encoding.GetEncoding("iso-8859-1");
        string head, tail;
        using (var fs = File.OpenRead(j.Out))
        {
            byte[] b = new byte[4 * 1024 * 1024];
            int n = fs.Read(b, 0, b.Length);
            head = latin.GetString(b, 0, n);
            long from = Math.Max(0, fs.Length - 1024 * 1024);
            fs.Seek(from, SeekOrigin.Begin);
            n = fs.Read(b, 0, (int)Math.Min(b.Length, fs.Length - from));
            tail = latin.GetString(b, 0, n);
        }
        string both = head + tail;
        if (j.CheckC2pa && (both.Contains("c2pa") || both.Contains("jumb") || both.Contains("trainedAlgorithmicMedia")))
        { j.State = State.Warn; j.Note = "C2PA still detected in the copy"; }
        else j.State = State.Done;
    }
}
// ---------- animation ----------

static class A
{
    // ease-out (expo) and spring (back) curves
    public static readonly IEasingFunction Out = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
    public static readonly IEasingFunction Spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
    public static readonly IEasingFunction Sine = new SineEase { EasingMode = EasingMode.EaseInOut };

    public static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }

    public static void To(IAnimatable t, DependencyProperty p, double to, int ms)
    { To(t, p, to, ms, Out, 0, null, null); }

    public static void To(IAnimatable t, DependencyProperty p, double to, int ms, IEasingFunction e, int delay, double? from, Action done)
    {
        var a = new DoubleAnimation();
        a.To = to;
        if (from.HasValue) a.From = from.Value;
        a.Duration = TimeSpan.FromMilliseconds(Math.Max(1, ms));
        a.EasingFunction = e;
        a.BeginTime = TimeSpan.FromMilliseconds(delay);
        if (done != null) a.Completed += delegate { done(); };
        t.BeginAnimation(p, a);
    }

    public static void Loop(IAnimatable t, DependencyProperty p, double from, double to, int ms, IEasingFunction e, bool reverse)
    {
        var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms));
        a.EasingFunction = e;
        a.AutoReverse = reverse;
        a.RepeatBehavior = RepeatBehavior.Forever;
        t.BeginAnimation(p, a);
    }

    public static void Tint(SolidColorBrush b, Color to, int ms)
    {
        var a = new ColorAnimation(); a.To = to; a.Duration = TimeSpan.FromMilliseconds(ms); a.EasingFunction = Out;
        b.BeginAnimation(SolidColorBrush.ColorProperty, a);
    }

    public static void Tint(SolidColorBrush b, Color from, Color to, int ms)
    {
        var a = new ColorAnimation(); a.From = from; a.To = to; a.Duration = TimeSpan.FromMilliseconds(ms); a.EasingFunction = Out;
        b.BeginAnimation(SolidColorBrush.ColorProperty, a);
    }

    // new text slides in from below with a fade
    public static void Swap(TextBlock tb, TranslateTransform tt, string text)
    {
        tb.Text = text;
        To(tb, UIElement.OpacityProperty, 1, 380, Out, 0, 0, null);
        To(tt, TranslateTransform.YProperty, 0, 380, Out, 0, 7, null);
    }
}

// ---------- row ----------

class RowView
{
    const string Xaml = @"
<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        x:Name='Card' CornerRadius='14' BorderThickness='1' Margin='0,0,0,8' Padding='12,10,8,10' Opacity='0' RenderTransformOrigin='.5,.5' ClipToBounds='True'>
  <Border.BorderBrush><SolidColorBrush x:Name='Bb' Color='#232329'/></Border.BorderBrush>
  <Border.Background>
    <LinearGradientBrush StartPoint='0,0' EndPoint='0,1'><GradientStop Color='#EB18181C' Offset='0'/><GradientStop Color='#EB101013' Offset='1'/></LinearGradientBrush>
  </Border.Background>
  <Border.RenderTransform>
    <TransformGroup><ScaleTransform x:Name='Sc' ScaleX='.98' ScaleY='.98'/><TranslateTransform x:Name='Tx' Y='12'/></TransformGroup>
  </Border.RenderTransform>
  <Grid>
    <Border x:Name='Shim' CornerRadius='14' Margin='-12,-10,-8,-10' IsHitTestVisible='False' Opacity='0'>
      <Border.Background>
        <LinearGradientBrush StartPoint='0,0' EndPoint='1,0'>
          <LinearGradientBrush.RelativeTransform><TranslateTransform x:Name='ShimT' X='-1'/></LinearGradientBrush.RelativeTransform>
          <GradientStop Color='#00D4FF4A' Offset='.3'/><GradientStop Color='#16D4FF4A' Offset='.5'/><GradientStop Color='#00D4FF4A' Offset='.7'/>
        </LinearGradientBrush>
      </Border.Background>
    </Border>
    <Grid>
      <Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
      <Border x:Name='Tile' Width='34' Height='34' CornerRadius='10' Background='#1FD4FF4A' VerticalAlignment='Center' RenderTransformOrigin='.5,.5'>
        <Border.RenderTransform><TransformGroup><ScaleTransform x:Name='TileSc'/><RotateTransform x:Name='TileRot'/></TransformGroup></Border.RenderTransform>
        <TextBlock x:Name='Ext' FontFamily='Cascadia Mono, Consolas' FontSize='10.5' FontWeight='SemiBold' Foreground='#D4FF4A' HorizontalAlignment='Center' VerticalAlignment='Center'/>
      </Border>
      <StackPanel Grid.Column='1' Margin='11,0,8,0' VerticalAlignment='Center'>
        <TextBlock x:Name='Name' FontSize='13' FontWeight='SemiBold' Foreground='#ECECEF' TextTrimming='CharacterEllipsis'/>
        <TextBlock x:Name='Sub' FontFamily='Cascadia Mono, Consolas' FontSize='11' Foreground='#74747F' TextTrimming='CharacterEllipsis' Margin='0,2,0,0'>
          <TextBlock.RenderTransform><TranslateTransform x:Name='SubT'/></TextBlock.RenderTransform>
        </TextBlock>
      </StackPanel>
      <StackPanel Grid.Column='2' Orientation='Horizontal' VerticalAlignment='Center' Margin='0,0,4,0'>
        <Grid x:Name='IconHost' Width='16' Height='16' RenderTransformOrigin='.5,.5'>
          <Grid.RenderTransform><ScaleTransform x:Name='IconSc'/></Grid.RenderTransform>
          <Ellipse x:Name='IQueued' Width='12' Height='12' Stroke='#74747F' StrokeThickness='1.5'/>
          <Path x:Name='ISpin' Visibility='Collapsed' Data='M8,1.5 A6.5,6.5 0 1 1 1.5,8' StrokeThickness='2' StrokeStartLineCap='Round' StrokeEndLineCap='Round' RenderTransformOrigin='.5,.5'>
            <Path.Stroke><LinearGradientBrush StartPoint='0,0' EndPoint='1,1'><GradientStop Color='#D4FF4A' Offset='0'/><GradientStop Color='#6FF3FF' Offset='1'/></LinearGradientBrush></Path.Stroke>
            <Path.RenderTransform><RotateTransform x:Name='SpinRot'/></Path.RenderTransform>
          </Path>
          <Path x:Name='ICheck' Visibility='Collapsed' Data='M3.5,8.5 L6.8,11.8 L12.5,4.8' Stroke='#3DDC97' StrokeThickness='2' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' StrokeDashArray='7 7' StrokeDashOffset='7'/>
          <Path x:Name='ICross' Visibility='Collapsed' Data='M4.5,4.5 L11.5,11.5 M11.5,4.5 L4.5,11.5' Stroke='#FF5F56' StrokeThickness='2' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>
          <Path x:Name='IWarn' Visibility='Collapsed' Data='M8,3.5 L8,9 M8,12 L8,12.2' Stroke='#FFB454' StrokeThickness='2' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>
        </Grid>
        <TextBlock x:Name='Label' FontFamily='Cascadia Mono, Consolas' FontSize='11.5' FontWeight='SemiBold' Foreground='#74747F' VerticalAlignment='Center' Margin='8,0,0,0'/>
      </StackPanel>
      <Button x:Name='Insp' Grid.Column='3' Margin='4,0,0,0' ToolTip='Inspect metadata'/>
      <Button x:Name='Open' Grid.Column='4' Visibility='Collapsed' Opacity='0' Margin='2,0,0,0' ToolTip='Show in folder'/>
    </Grid>
  </Grid>
</Border>";

    static readonly Color Line = A.C("#232329"), Line2 = A.C("#2E2E36");

    public readonly FrameworkElement Card;
    readonly Job job;
    readonly SolidColorBrush bb;
    readonly ScaleTransform sc, tileSc, iconSc;
    readonly TranslateTransform tx, subT, shimT;
    readonly RotateTransform tileRot, spinRot;
    readonly FrameworkElement shim, tile, iSpin, iCheck, iCross, iWarn, iQueued;
    readonly TextBlock sub, label;
    readonly Button open, insp;
    public event Action<Job> InspectRequested;
    readonly Window win;
    bool hover;

    public RowView(Job j, Window w)
    {
        job = j; win = w;
        Card = (FrameworkElement)XamlReader.Parse(Xaml);
        bb = (SolidColorBrush)Card.FindName("Bb");
        sc = (ScaleTransform)Card.FindName("Sc"); tx = (TranslateTransform)Card.FindName("Tx");
        tileSc = (ScaleTransform)Card.FindName("TileSc"); tileRot = (RotateTransform)Card.FindName("TileRot");
        iconSc = (ScaleTransform)Card.FindName("IconSc"); spinRot = (RotateTransform)Card.FindName("SpinRot");
        subT = (TranslateTransform)Card.FindName("SubT"); shimT = (TranslateTransform)Card.FindName("ShimT");
        shim = (FrameworkElement)Card.FindName("Shim"); tile = (FrameworkElement)Card.FindName("Tile");
        iSpin = (FrameworkElement)Card.FindName("ISpin"); iCheck = (FrameworkElement)Card.FindName("ICheck");
        iCross = (FrameworkElement)Card.FindName("ICross"); iWarn = (FrameworkElement)Card.FindName("IWarn");
        iQueued = (FrameworkElement)Card.FindName("IQueued");
        sub = (TextBlock)Card.FindName("Sub"); label = (TextBlock)Card.FindName("Label");
        open = (Button)Card.FindName("Open"); insp = (Button)Card.FindName("Insp");

        string ext = System.IO.Path.GetExtension(j.Src).TrimStart('.').ToUpperInvariant();
        ((TextBlock)Card.FindName("Ext")).Text = ext.Length > 4 ? ext.Substring(0, 4) : ext;
        var name = (TextBlock)Card.FindName("Name");
        name.Text = System.IO.Path.GetFileName(j.Src);
        name.ToolTip = j.Src;
        sub.Text = System.IO.Path.GetDirectoryName(j.Src);
        label.Text = "Queued";

        open.Style = (Style)w.FindResource("Ico");
        open.Content = new System.Windows.Shapes.Path
        {
            Data = (Geometry)w.FindResource("GFolder"),
            Stroke = (Brush)new BrushConverter().ConvertFromString("#A9A9B3"),
            StrokeThickness = 1.6, Width = 14, Height = 14, Stretch = Stretch.Uniform,
            StrokeLineJoin = PenLineJoin.Round
        };
        open.Click += delegate { Reveal(); };
        insp.Style = (Style)w.FindResource("Ico");
        insp.Content = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M10.5,4 A6.5,6.5 0 1 0 10.5,17 A6.5,6.5 0 1 0 10.5,4 Z M15.5,15.5 L20,20"),
            Stroke = (Brush)new BrushConverter().ConvertFromString("#A9A9B3"), StrokeThickness = 1.7, Width = 14, Height = 14, Stretch = Stretch.Uniform,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        };
        insp.Click += delegate { if (InspectRequested != null) InspectRequested(job); };

        Card.MouseEnter += delegate { hover = true; Hover(true); };
        Card.MouseLeave += delegate { hover = false; Hover(false); };
        Card.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { if (e.ClickCount == 2) Reveal(); };
    }

    void Reveal()
    {
        string t = (job.Out != null && File.Exists(job.Out)) ? job.Out : job.Src;
        Process.Start("explorer.exe", "/select,\"" + t + "\"");
    }

    // slides up, scales in and unblurs
    public void In(int delay)
    {
        var blur = new BlurEffect { Radius = 3 };
        Card.Effect = blur;
        A.To(Card, UIElement.OpacityProperty, 1, 600, A.Out, delay, 0, null);
        A.To(sc, ScaleTransform.ScaleXProperty, 1, 600, A.Out, delay, .98, null);
        A.To(sc, ScaleTransform.ScaleYProperty, 1, 600, A.Out, delay, .98, null);
        A.To(tx, TranslateTransform.YProperty, 0, 600, A.Out, delay, 12, null);
        A.To(blur, BlurEffect.RadiusProperty, 0, 600, A.Out, delay, 3, delegate { Card.Effect = null; });
    }

    public void Out(int delay, Action done)
    {
        A.To(Card, UIElement.OpacityProperty, 0, 200, A.Out, delay, null, null);
        A.To(sc, ScaleTransform.ScaleXProperty, .96, 200, A.Out, delay, null, null);
        A.To(sc, ScaleTransform.ScaleYProperty, .96, 200, A.Out, delay, null, done);
    }

    // hover: the tile tilts and grows
    void Hover(bool on)
    {
        A.To(tileRot, RotateTransform.AngleProperty, on ? -8 : 0, 450, A.Spring, 0, null, null);
        A.To(tileSc, ScaleTransform.ScaleXProperty, on ? 1.08 : 1, 450, A.Spring, 0, null, null);
        A.To(tileSc, ScaleTransform.ScaleYProperty, on ? 1.08 : 1, 450, A.Spring, 0, null, null);
        if (job.State == State.Done || job.State == State.Queued)
            A.Tint(bb, on ? Line2 : Line, 250);
    }

    void ShowIcon(FrameworkElement which)
    {
        foreach (var i in new[] { iQueued, iSpin, iCheck, iCross, iWarn })
            i.Visibility = i == which ? Visibility.Visible : Visibility.Collapsed;
        A.To(iconSc, ScaleTransform.ScaleXProperty, 1, 450, A.Spring, 0, .4, null);
        A.To(iconSc, ScaleTransform.ScaleYProperty, 1, 450, A.Spring, 0, .4, null);
    }

    void Status(string text, string color)
    {
        label.Foreground = new SolidColorBrush(A.C(color));
        label.Text = text;
    }

    public void SetState(State s)
    {
        switch (s)
        {
            case State.Working:
                ShowIcon(iSpin);
                A.Loop(spinRot, RotateTransform.AngleProperty, 0, 360, 800, null, false);
                Status("Cleaning", "#D4FF4A");
                A.Tint(bb, A.C("#4DD4FF4A"), 300);
                A.To(shim, UIElement.OpacityProperty, 1, 300);
                A.Loop(shimT, TranslateTransform.XProperty, -1, 1, 1400, null, false);
                break;

            case State.Done:
                StopWork();
                ShowIcon(iCheck);
                // the check mark draws itself
                A.To(iCheck, Shape.StrokeDashOffsetProperty, 0, 550, A.Out, 50, 7, null);
                Status("Clean", "#3DDC97");
                A.Tint(bb, A.C("#99D4FF4A"), hover ? Line2 : Line, 800);
                A.Swap(sub, subT, "→ " + System.IO.Path.GetFileName(job.Out) + "  ·  " + Size(job.Out));
                ShowOpen();
                break;

            case State.Warn:
                StopWork();
                ShowIcon(iWarn);
                Status("Review", "#FFB454");
                A.Tint(bb, A.C("#59FFB454"), 300);
                sub.Foreground = new SolidColorBrush(A.C("#FFB454"));
                A.Swap(sub, subT, job.Note);
                ShowOpen();
                break;

            case State.Error:
                StopWork();
                ShowIcon(iCross);
                Status("Error", "#FF5F56");
                A.Tint(bb, A.C("#59FF5F56"), 300);
                sub.Foreground = new SolidColorBrush(A.C("#FF5F56"));
                A.Swap(sub, subT, job.Note);
                Shake();
                break;
        }
    }

    void StopWork()
    {
        A.To(shim, UIElement.OpacityProperty, 0, 250);
        A.To(shimT, TranslateTransform.XProperty, -1, 1, null, 0, null, null);
        A.To(spinRot, RotateTransform.AngleProperty, 0, 1, null, 0, null, null);
    }

    void ShowOpen()
    {
        open.Visibility = Visibility.Visible;
        A.To(open, UIElement.OpacityProperty, 1, 350, A.Out, 150, 0, null);
    }

    // shake
    void Shake()
    {
        var k = new DoubleAnimationUsingKeyFrames();
        double[] v = { 0, -4, 4, -2, 2, 0 };
        for (int i = 0; i < v.Length; i++) k.KeyFrames.Add(new LinearDoubleKeyFrame(v[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * 80))));
        tx.BeginAnimation(TranslateTransform.XProperty, k);
    }

    static string Size(string path)
    {
        try
        {
            long b = new FileInfo(path).Length;
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.0") + " GB";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0.0") + " MB";
            return Math.Max(1, b / 1024) + " KB";
        }
        catch (Exception) { return ""; }
    }
}

// ---------- window ----------

class MainWin
{
    const string WinXaml = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        xmlns:shell='clr-namespace:System.Windows.Shell;assembly=PresentationFramework'
        Title='Clean Metadata' Width='520' Height='720' MinWidth='500' MinHeight='580'
        WindowStyle='None' ResizeMode='CanResize' Background='#0B0B0D' AllowDrop='True'
        FontFamily='Segoe UI Variable Text, Segoe UI' FontSize='13' Foreground='#ECECEF'
        UseLayoutRounding='True' SnapsToDevicePixels='True' WindowStartupLocation='CenterScreen'>
  <shell:WindowChrome.WindowChrome>
    <shell:WindowChrome CaptionHeight='42' ResizeBorderThickness='6' GlassFrameThickness='0' CornerRadius='0' UseAeroCaptionButtons='False'/>
  </shell:WindowChrome.WindowChrome>

  <Window.Resources>
    <LinearGradientBrush x:Key='Grad' StartPoint='0,0' EndPoint='1,1'><GradientStop Color='#D4FF4A' Offset='0'/><GradientStop Color='#6FF3FF' Offset='1'/></LinearGradientBrush>
    <ExponentialEase x:Key='Out' EasingMode='EaseOut' Exponent='6'/>
    <BackEase x:Key='Spring' EasingMode='EaseOut' Amplitude='0.6'/>
    <StreamGeometry x:Key='GArrow'>M12,3.5 V14.5 M7.5,10 L12,14.5 L16.5,10 M5,19.5 H19</StreamGeometry>
    <StreamGeometry x:Key='GPlus'>M12,5 V19 M5,12 H19</StreamGeometry>
    <StreamGeometry x:Key='GFolder'>M3,7 A2,2 0 0 1 5,5 H9 L11,7.5 H19 A2,2 0 0 1 21,9.5 V17 A2,2 0 0 1 19,19 H5 A2,2 0 0 1 3,17 Z</StreamGeometry>
    <StreamGeometry x:Key='GTrash'>M4,7 H20 M9,7 V5 H15 V7 M6,7 L7,19 H17 L18,7 M10,11 V16 M14,11 V16</StreamGeometry>
    <StreamGeometry x:Key='GCheck'>M5,12.5 L10,17.5 L19,7</StreamGeometry>
    <StreamGeometry x:Key='GCross'>M6,6 L18,18 M18,6 L6,18</StreamGeometry>

    <Style x:Key='Nothing' TargetType='RepeatButton'>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style TargetType='ScrollBar'>
      <Setter Property='Width' Value='10'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='ScrollBar'>
          <Grid Background='Transparent'>
            <Track x:Name='PART_Track' IsDirectionReversed='True'>
              <Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Style='{StaticResource Nothing}'/></Track.DecreaseRepeatButton>
              <Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Style='{StaticResource Nothing}'/></Track.IncreaseRepeatButton>
              <Track.Thumb>
                <Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'>
                  <Border x:Name='T' Background='#2E2E36' CornerRadius='3' Margin='2,0'/>
                  <ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='T' Property='Background' Value='#3C3C46'/></Trigger></ControlTemplate.Triggers>
                </ControlTemplate></Thumb.Template></Thumb>
              </Track.Thumb>
            </Track>
          </Grid>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>

    <Style x:Key='Cap' TargetType='Button'>
      <Setter Property='Width' Value='46'/><Setter Property='Height' Value='42'/>
      <Setter Property='Foreground' Value='#A9A9B3'/><Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border><Border.Background><SolidColorBrush x:Name='Bb' Color='#001D1D22'/></Border.Background>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter Property='Foreground' Value='#ECECEF'/>
              <Trigger.EnterActions><BeginStoryboard><Storyboard><ColorAnimation Storyboard.TargetName='Bb' Storyboard.TargetProperty='Color' To='#FF1D1D22' Duration='0:0:.15'/></Storyboard></BeginStoryboard></Trigger.EnterActions>
              <Trigger.ExitActions><BeginStoryboard><Storyboard><ColorAnimation Storyboard.TargetName='Bb' Storyboard.TargetProperty='Color' To='#001D1D22' Duration='0:0:.25'/></Storyboard></BeginStoryboard></Trigger.ExitActions>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
    <Style x:Key='CapClose' TargetType='Button' BasedOn='{StaticResource Cap}'>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border><Border.Background><SolidColorBrush x:Name='Bb' Color='#00FF5F56'/></Border.Background>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter Property='Foreground' Value='#FFFFFF'/>
              <Trigger.EnterActions><BeginStoryboard><Storyboard><ColorAnimation Storyboard.TargetName='Bb' Storyboard.TargetProperty='Color' To='#E6FF5F56' Duration='0:0:.15'/></Storyboard></BeginStoryboard></Trigger.EnterActions>
              <Trigger.ExitActions><BeginStoryboard><Storyboard><ColorAnimation Storyboard.TargetName='Bb' Storyboard.TargetProperty='Color' To='#00FF5F56' Duration='0:0:.25'/></Storyboard></BeginStoryboard></Trigger.ExitActions>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>

    <Style x:Key='Ico' TargetType='Button'>
      <Setter Property='Width' Value='28'/><Setter Property='Height' Value='28'/>
      <Setter Property='Cursor' Value='Hand'/><Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border CornerRadius='8' RenderTransformOrigin='.5,.5'>
            <Border.Background><SolidColorBrush x:Name='Bb' Color='#001D1D22'/></Border.Background>
            <Border.RenderTransform><ScaleTransform x:Name='Sc'/></Border.RenderTransform>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Trigger.EnterActions><BeginStoryboard><Storyboard><ColorAnimation Storyboard.TargetName='Bb' Storyboard.TargetProperty='Color' To='#FF1D1D22' Duration='0:0:.15'/></Storyboard></BeginStoryboard></Trigger.EnterActions>
              <Trigger.ExitActions><BeginStoryboard><Storyboard><ColorAnimation Storyboard.TargetName='Bb' Storyboard.TargetProperty='Color' To='#001D1D22' Duration='0:0:.25'/></Storyboard></BeginStoryboard></Trigger.ExitActions>
            </Trigger>
            <Trigger Property='IsPressed' Value='True'>
              <Trigger.EnterActions><BeginStoryboard><Storyboard>
                <DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleX' To='.88' Duration='0:0:.08'/>
                <DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleY' To='.88' Duration='0:0:.08'/>
              </Storyboard></BeginStoryboard></Trigger.EnterActions>
              <Trigger.ExitActions><BeginStoryboard><Storyboard>
                <DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleX' To='1' Duration='0:0:.35' EasingFunction='{StaticResource Spring}'/>
                <DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleY' To='1' Duration='0:0:.35' EasingFunction='{StaticResource Spring}'/>
              </Storyboard></BeginStoryboard></Trigger.ExitActions>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>

    <Style x:Key='Pill' TargetType='Button'>
      <Setter Property='Height' Value='32'/><Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Foreground' Value='#0B0B0D'/><Setter Property='FontWeight' Value='SemiBold'/>
      <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Grid RenderTransformOrigin='.5,.5'>
            <Grid.RenderTransform><TransformGroup><ScaleTransform x:Name='Sc'/><TranslateTransform x:Name='Tr'/></TransformGroup></Grid.RenderTransform>
            <Border CornerRadius='16' Background='{StaticResource Grad}' Padding='14,0,16,0'>
              <Border.Effect><DropShadowEffect x:Name='Sh' Color='#D4FF4A' BlurRadius='18' ShadowDepth='0' Opacity='0'/></Border.Effect>
              <Grid>
                <Canvas x:Name='Rip' IsHitTestVisible='False'/>
                <StackPanel Orientation='Horizontal' HorizontalAlignment='Center' VerticalAlignment='Center'>
                  <Path Data='{StaticResource GPlus}' Stroke='#0B0B0D' StrokeThickness='2' StrokeStartLineCap='Round' StrokeEndLineCap='Round' Width='12' Height='12' Stretch='Uniform' Margin='0,0,8,0'/>
                  <ContentPresenter VerticalAlignment='Center'/>
                </StackPanel>
              </Grid>
            </Border>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Trigger.EnterActions><BeginStoryboard><Storyboard>
                <DoubleAnimation Storyboard.TargetName='Tr' Storyboard.TargetProperty='Y' To='-1' Duration='0:0:.3' EasingFunction='{StaticResource Spring}'/>
                <DoubleAnimation Storyboard.TargetName='Sh' Storyboard.TargetProperty='Opacity' To='.55' Duration='0:0:.3'/>
              </Storyboard></BeginStoryboard></Trigger.EnterActions>
              <Trigger.ExitActions><BeginStoryboard><Storyboard>
                <DoubleAnimation Storyboard.TargetName='Tr' Storyboard.TargetProperty='Y' To='0' Duration='0:0:.3' EasingFunction='{StaticResource Spring}'/>
                <DoubleAnimation Storyboard.TargetName='Sh' Storyboard.TargetProperty='Opacity' To='0' Duration='0:0:.3'/>
              </Storyboard></BeginStoryboard></Trigger.ExitActions>
            </Trigger>
            <Trigger Property='IsPressed' Value='True'>
              <Trigger.EnterActions><BeginStoryboard><Storyboard>
                <DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleX' To='.96' Duration='0:0:.08'/>
                <DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleY' To='.96' Duration='0:0:.08'/>
              </Storyboard></BeginStoryboard></Trigger.EnterActions>
              <Trigger.ExitActions><BeginStoryboard><Storyboard>
                <DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleX' To='1' Duration='0:0:.35' EasingFunction='{StaticResource Spring}'/>
                <DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleY' To='1' Duration='0:0:.35' EasingFunction='{StaticResource Spring}'/>
              </Storyboard></BeginStoryboard></Trigger.ExitActions>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
    <Style x:Key='MenuItem' TargetType='Button'>
      <Setter Property='Foreground' Value='#ECECEF'/><Setter Property='Cursor' Value='Hand'/><Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border CornerRadius='8' Padding='12,7'>
            <Border.Background><SolidColorBrush x:Name='Bb' Color='#001D1D22'/></Border.Background>
            <ContentPresenter HorizontalAlignment='Left' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Trigger.EnterActions><BeginStoryboard><Storyboard><ColorAnimation Storyboard.TargetName='Bb' Storyboard.TargetProperty='Color' To='#FF1D1D22' Duration='0:0:.12'/></Storyboard></BeginStoryboard></Trigger.EnterActions>
              <Trigger.ExitActions><BeginStoryboard><Storyboard><ColorAnimation Storyboard.TargetName='Bb' Storyboard.TargetProperty='Color' To='#001D1D22' Duration='0:0:.2'/></Storyboard></BeginStoryboard></Trigger.ExitActions>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>

    <Style x:Key='Btn' TargetType='Button'>
      <Setter Property='Height' Value='32'/><Setter Property='Foreground' Value='#ECECEF'/><Setter Property='FontWeight' Value='SemiBold'/>
      <Setter Property='Cursor' Value='Hand'/><Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border CornerRadius='16' Padding='18,0' RenderTransformOrigin='.5,.5'>
            <Border.Background><SolidColorBrush x:Name='Bb' Color='#FF1D1D22'/></Border.Background>
            <Border.RenderTransform><ScaleTransform x:Name='Sc'/></Border.RenderTransform>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Trigger.EnterActions><BeginStoryboard><Storyboard><ColorAnimation Storyboard.TargetName='Bb' Storyboard.TargetProperty='Color' To='#FF2A2A31' Duration='0:0:.15'/></Storyboard></BeginStoryboard></Trigger.EnterActions>
              <Trigger.ExitActions><BeginStoryboard><Storyboard><ColorAnimation Storyboard.TargetName='Bb' Storyboard.TargetProperty='Color' To='#FF1D1D22' Duration='0:0:.25'/></Storyboard></BeginStoryboard></Trigger.ExitActions>
            </Trigger>
            <Trigger Property='IsPressed' Value='True'>
              <Trigger.EnterActions><BeginStoryboard><Storyboard>
                <DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleX' To='.96' Duration='0:0:.08'/>
                <DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleY' To='.96' Duration='0:0:.08'/>
              </Storyboard></BeginStoryboard></Trigger.EnterActions>
              <Trigger.ExitActions><BeginStoryboard><Storyboard>
                <DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleX' To='1' Duration='0:0:.35' EasingFunction='{StaticResource Spring}'/>
                <DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleY' To='1' Duration='0:0:.35' EasingFunction='{StaticResource Spring}'/>
              </Storyboard></BeginStoryboard></Trigger.ExitActions>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
    <Style x:Key='BtnDanger' TargetType='Button' BasedOn='{StaticResource Btn}'>
      <Setter Property='Foreground' Value='#0B0B0D'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border CornerRadius='16' Padding='18,0' RenderTransformOrigin='.5,.5'>
            <Border.Background><SolidColorBrush x:Name='Bb' Color='#FFFF5F56'/></Border.Background>
            <Border.RenderTransform><ScaleTransform x:Name='Sc'/></Border.RenderTransform>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Trigger.EnterActions><BeginStoryboard><Storyboard><ColorAnimation Storyboard.TargetName='Bb' Storyboard.TargetProperty='Color' To='#FFFF7B73' Duration='0:0:.15'/></Storyboard></BeginStoryboard></Trigger.EnterActions>
              <Trigger.ExitActions><BeginStoryboard><Storyboard><ColorAnimation Storyboard.TargetName='Bb' Storyboard.TargetProperty='Color' To='#FFFF5F56' Duration='0:0:.25'/></Storyboard></BeginStoryboard></Trigger.ExitActions>
            </Trigger>
            <Trigger Property='IsPressed' Value='True'>
              <Trigger.EnterActions><BeginStoryboard><Storyboard>
                <DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleX' To='.96' Duration='0:0:.08'/>
                <DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleY' To='.96' Duration='0:0:.08'/>
              </Storyboard></BeginStoryboard></Trigger.EnterActions>
              <Trigger.ExitActions><BeginStoryboard><Storyboard>
                <DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleX' To='1' Duration='0:0:.35' EasingFunction='{StaticResource Spring}'/>
                <DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleY' To='1' Duration='0:0:.35' EasingFunction='{StaticResource Spring}'/>
              </Storyboard></BeginStoryboard></Trigger.ExitActions>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
    <Style x:Key='BtnPrimary' TargetType='Button' BasedOn='{StaticResource Btn}'>
      <Setter Property='Foreground' Value='#0B0B0D'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border CornerRadius='16' Padding='18,0' Background='{StaticResource Grad}' RenderTransformOrigin='.5,.5'>
            <Border.RenderTransform><ScaleTransform x:Name='Sc'/></Border.RenderTransform>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsPressed' Value='True'>
              <Trigger.EnterActions><BeginStoryboard><Storyboard>
                <DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleX' To='.96' Duration='0:0:.08'/>
                <DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleY' To='.96' Duration='0:0:.08'/>
              </Storyboard></BeginStoryboard></Trigger.EnterActions>
              <Trigger.ExitActions><BeginStoryboard><Storyboard>
                <DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleX' To='1' Duration='0:0:.35' EasingFunction='{StaticResource Spring}'/>
                <DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleY' To='1' Duration='0:0:.35' EasingFunction='{StaticResource Spring}'/>
              </Storyboard></BeginStoryboard></Trigger.ExitActions>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>    <Style x:Key='Switch' TargetType='ToggleButton'>
      <Setter Property='Cursor' Value='Hand'/><Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='ToggleButton'>
          <Grid Width='36' Height='22' Background='Transparent'>
            <Border CornerRadius='11'><Border.Background><SolidColorBrush x:Name='Tb' Color='#FF2E2E36'/></Border.Background></Border>
            <Ellipse Width='16' Height='16' HorizontalAlignment='Left' Margin='3,0,0,0'>
              <Ellipse.Fill><SolidColorBrush x:Name='Kb' Color='#FFA9A9B3'/></Ellipse.Fill>
              <Ellipse.RenderTransform><TranslateTransform x:Name='Kt'/></Ellipse.RenderTransform>
            </Ellipse>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsChecked' Value='True'>
              <Trigger.EnterActions><BeginStoryboard><Storyboard>
                <DoubleAnimation Storyboard.TargetName='Kt' Storyboard.TargetProperty='X' To='14' Duration='0:0:.45' EasingFunction='{StaticResource Spring}'/>
                <ColorAnimation Storyboard.TargetName='Tb' Storyboard.TargetProperty='Color' To='#4DD4FF4A' Duration='0:0:.25'/>
                <ColorAnimation Storyboard.TargetName='Kb' Storyboard.TargetProperty='Color' To='#FFD4FF4A' Duration='0:0:.25'/>
              </Storyboard></BeginStoryboard></Trigger.EnterActions>
              <Trigger.ExitActions><BeginStoryboard><Storyboard>
                <DoubleAnimation Storyboard.TargetName='Kt' Storyboard.TargetProperty='X' To='0' Duration='0:0:.45' EasingFunction='{StaticResource Spring}'/>
                <ColorAnimation Storyboard.TargetName='Tb' Storyboard.TargetProperty='Color' To='#FF2E2E36' Duration='0:0:.25'/>
                <ColorAnimation Storyboard.TargetName='Kb' Storyboard.TargetProperty='Color' To='#FFA9A9B3' Duration='0:0:.25'/>
              </Storyboard></BeginStoryboard></Trigger.ExitActions>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>

    <Style x:Key='SegBtn' TargetType='Button'>
      <Setter Property='FontWeight' Value='SemiBold'/><Setter Property='FontSize' Value='12'/><Setter Property='Cursor' Value='Hand'/><Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='Button'><Border Background='Transparent'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border></ControlTemplate>
      </Setter.Value></Setter>
    </Style>

    <Style x:Key='Input' TargetType='TextBox'>
      <Setter Property='Foreground' Value='#ECECEF'/><Setter Property='CaretBrush' Value='#D4FF4A'/>
      <Setter Property='FontFamily' Value='Cascadia Mono, Consolas'/><Setter Property='FontSize' Value='12'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='TextBox'>
          <Border CornerRadius='10' BorderThickness='1' Background='#0B0B0D' Padding='10,8'>
            <Border.BorderBrush><SolidColorBrush x:Name='Bb' Color='#FF2E2E36'/></Border.BorderBrush>
            <ScrollViewer x:Name='PART_ContentHost' Focusable='False' HorizontalScrollBarVisibility='Hidden' VerticalScrollBarVisibility='Hidden'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsKeyboardFocused' Value='True'>
              <Trigger.EnterActions><BeginStoryboard><Storyboard><ColorAnimation Storyboard.TargetName='Bb' Storyboard.TargetProperty='Color' To='#66D4FF4A' Duration='0:0:.2'/></Storyboard></BeginStoryboard></Trigger.EnterActions>
              <Trigger.ExitActions><BeginStoryboard><Storyboard><ColorAnimation Storyboard.TargetName='Bb' Storyboard.TargetProperty='Color' To='#FF2E2E36' Duration='0:0:.25'/></Storyboard></BeginStoryboard></Trigger.ExitActions>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
    <StreamGeometry x:Key='GSliders'>M4,7 H11 M17,7 H20 M4,17 H7 M13,17 H20 M11,7 A3,3 0 1 0 17,7 A3,3 0 1 0 11,7 Z M7,17 A3,3 0 1 0 13,17 A3,3 0 1 0 7,17 Z</StreamGeometry>  </Window.Resources>

  <Grid x:Name='Root'>
    <Grid.RowDefinitions><RowDefinition Height='42'/><RowDefinition Height='*'/><RowDefinition Height='32'/></Grid.RowDefinitions>

    <!-- background: dot grid + cursor spotlight -->
    <Rectangle x:Name='Bg' Grid.RowSpan='3' IsHitTestVisible='False'>
      <Rectangle.Fill>
        <DrawingBrush TileMode='Tile' Viewport='0,0,18,18' ViewportUnits='Absolute' Viewbox='0,0,18,18' ViewboxUnits='Absolute'>
          <DrawingBrush.Drawing><GeometryDrawing Brush='#0AFFFFFF'><GeometryDrawing.Geometry><EllipseGeometry Center='9,9' RadiusX='.6' RadiusY='.6'/></GeometryDrawing.Geometry></GeometryDrawing></DrawingBrush.Drawing>
        </DrawingBrush>
      </Rectangle.Fill>
    </Rectangle>
    <Rectangle x:Name='SpotRect' Grid.RowSpan='3' IsHitTestVisible='False' Opacity='.4'>
      <Rectangle.Fill>
        <DrawingBrush TileMode='Tile' Viewport='0,0,18,18' ViewportUnits='Absolute' Viewbox='0,0,18,18' ViewboxUnits='Absolute'>
          <DrawingBrush.Drawing><GeometryDrawing Brush='#D4FF4A'><GeometryDrawing.Geometry><EllipseGeometry Center='9,9' RadiusX='.75' RadiusY='.75'/></GeometryDrawing.Geometry></GeometryDrawing></DrawingBrush.Drawing>
        </DrawingBrush>
      </Rectangle.Fill>
      <Rectangle.OpacityMask>
        <RadialGradientBrush x:Name='Spot' MappingMode='Absolute' Center='-300,-300' GradientOrigin='-300,-300' RadiusX='130' RadiusY='130'>
          <GradientStop Color='#E6000000' Offset='0'/><GradientStop Color='#00000000' Offset='.8'/>
        </RadialGradientBrush>
      </Rectangle.OpacityMask>
    </Rectangle>

    <!-- header -->
    <Border Grid.Row='0' Background='#D9121215' BorderBrush='#232329' BorderThickness='0,0,0,1'>
      <Grid>
        <Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
        <StackPanel Orientation='Horizontal' VerticalAlignment='Center' Margin='12,0,0,0'>
          <Grid Width='26' Height='26' x:Name='LogoHost'>
            <Border x:Name='LogoRing' CornerRadius='9' BorderBrush='#D4FF4A' BorderThickness='1' Margin='-3' Opacity='0' RenderTransformOrigin='.5,.5'>
              <Border.RenderTransform><ScaleTransform x:Name='RingSc'/></Border.RenderTransform>
            </Border>
            <Image x:Name='LogoImg' RenderOptions.BitmapScalingMode='HighQuality' RenderTransformOrigin='.5,.5'>
              <Image.RenderTransform><TransformGroup><ScaleTransform x:Name='SparkSc'/><RotateTransform x:Name='SparkRot'/></TransformGroup></Image.RenderTransform>
            </Image>
          </Grid>
          <TextBlock Text='Clean Metadata' FontWeight='SemiBold' Margin='9,0,0,0' VerticalAlignment='Center'/>
        </StackPanel>

        <Border x:Name='Pill' Grid.Column='2' Height='24' CornerRadius='12' BorderThickness='1' Padding='9,0,10,0' Margin='0,0,6,0' VerticalAlignment='Center' RenderTransformOrigin='.5,.5'>
          <Border.BorderBrush><SolidColorBrush x:Name='PillBb' Color='#2E2E36'/></Border.BorderBrush>
          <Border.RenderTransform><ScaleTransform x:Name='PillSc'/></Border.RenderTransform>
          <StackPanel Orientation='Horizontal' VerticalAlignment='Center'>
            <Ellipse x:Name='PillLed' Width='6' Height='6' Margin='0,0,7,0'><Ellipse.Fill><SolidColorBrush x:Name='PillLedB' Color='#3C3C46'/></Ellipse.Fill></Ellipse>
            <TextBlock x:Name='PillText' FontFamily='Cascadia Mono, Consolas' FontSize='11.5' Foreground='#A9A9B3' Text='no files'/>
          </StackPanel>
        </Border>
        <Button x:Name='ClearBtn' Grid.Column='3' Style='{StaticResource Ico}' shell:WindowChrome.IsHitTestVisibleInChrome='True' Opacity='0' IsHitTestVisible='False' ToolTip='Clear list' Margin='0,0,6,0'>
          <Path Data='{StaticResource GTrash}' Stroke='#A9A9B3' StrokeThickness='1.6' StrokeLineJoin='Round' StrokeStartLineCap='Round' StrokeEndLineCap='Round' Width='14' Height='14' Stretch='Uniform'/>
        </Button>
        <Button x:Name='OptBtn' Grid.Column='4' Style='{StaticResource Ico}' shell:WindowChrome.IsHitTestVisibleInChrome='True' ToolTip='Options' Margin='0,0,2,0'>
          <Path Data='{StaticResource GSliders}' Stroke='#A9A9B3' StrokeThickness='1.6' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' Width='15' Height='15' Stretch='Uniform'/>
        </Button>
        <Button x:Name='MenuBtn' Grid.Column='5' Style='{StaticResource Ico}' shell:WindowChrome.IsHitTestVisibleInChrome='True' ToolTip='Menu' Margin='0,0,6,0'>
          <Path Data='M5,12 L5.01,12 M12,12 L12.01,12 M19,12 L19.01,12' Stroke='#A9A9B3' StrokeThickness='2.6' StrokeStartLineCap='Round' StrokeEndLineCap='Round' Width='14' Height='14' Stretch='Uniform'/>
        </Button>
        <Popup x:Name='MenuPop' Placement='Bottom' StaysOpen='False' AllowsTransparency='True' HorizontalOffset='-176' VerticalOffset='2'>
          <Border x:Name='MenuBox' Margin='14' Width='190' CornerRadius='12' Background='#F2141418' BorderBrush='#2E2E36' BorderThickness='1' Padding='5' Opacity='0' RenderTransformOrigin='.5,0'>
            <Border.RenderTransform><TransformGroup><ScaleTransform x:Name='MenuSc' ScaleX='.96' ScaleY='.96'/><TranslateTransform x:Name='MenuT' Y='-6'/></TransformGroup></Border.RenderTransform>
            <Border.Effect><DropShadowEffect Color='#000000' BlurRadius='24' ShadowDepth='6' Opacity='.6'/></Border.Effect>
            <StackPanel>
              <Button x:Name='AboutItem' Style='{StaticResource MenuItem}' Content='About'/>
              <Border Height='1' Background='#232329' Margin='6,4'/>
              <Button x:Name='UninstallItem' Style='{StaticResource MenuItem}' Content='Uninstall...' Foreground='#FF5F56'/>
            </StackPanel>
          </Border>
        </Popup>        <StackPanel Grid.Column='6' Orientation='Horizontal'>
          <Button x:Name='MinBtn' Style='{StaticResource Cap}' shell:WindowChrome.IsHitTestVisibleInChrome='True'>
            <Path Data='M0,5 H10' Stroke='{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}' StrokeThickness='1' Width='10' Height='10'/>
          </Button>
          <Button x:Name='CloseBtn' Style='{StaticResource CapClose}' shell:WindowChrome.IsHitTestVisibleInChrome='True'>
            <Path Data='M0,0 L10,10 M10,0 L0,10' Stroke='{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}' StrokeThickness='1' Width='10' Height='10'/>
          </Button>
        </StackPanel>
      </Grid>
    </Border>

    <!-- content -->
    <Grid Grid.Row='1' Margin='14,12,14,8'>
      <Grid.RowDefinitions><RowDefinition Height='Auto'/><RowDefinition Height='*'/></Grid.RowDefinitions>

      <Border x:Name='Zone' Height='190' CornerRadius='16' BorderThickness='1' Background='#A6121215' Cursor='Hand' Opacity='0'>
        <Border.BorderBrush><SolidColorBrush x:Name='ZoneBb' Color='#2E2E36'/></Border.BorderBrush>
        <Grid>
          <Border x:Name='ZoneSweep' CornerRadius='16' BorderThickness='1.5' Opacity='0' IsHitTestVisible='False' Margin='-1'>
            <Border.BorderBrush>
              <LinearGradientBrush StartPoint='0,0' EndPoint='1,1'>
                <LinearGradientBrush.RelativeTransform><RotateTransform x:Name='SweepRot' CenterX='.5' CenterY='.5'/></LinearGradientBrush.RelativeTransform>
                <GradientStop Color='#00D4FF4A' Offset='0'/><GradientStop Color='#00D4FF4A' Offset='.45'/><GradientStop Color='#D4FF4A' Offset='.72'/><GradientStop Color='#6FF3FF' Offset='.88'/><GradientStop Color='#006FF3FF' Offset='1'/>
              </LinearGradientBrush>
            </Border.BorderBrush>
          </Border>
          <StackPanel x:Name='ZoneContent' VerticalAlignment='Center' HorizontalAlignment='Center'>
            <Grid Width='58' Height='58' HorizontalAlignment='Center' Margin='0,0,0,12'>
              <Rectangle Width='58' Height='58' RadiusX='19' RadiusY='19' Stroke='#4DD4FF4A' StrokeThickness='1' StrokeDashArray='3 3' RenderTransformOrigin='.5,.5'>
                <Rectangle.RenderTransform><RotateTransform x:Name='DashRot'/></Rectangle.RenderTransform>
              </Rectangle>
              <Border Width='42' Height='42' CornerRadius='13' Background='#1FD4FF4A'>
                <Path Data='{StaticResource GArrow}' Stroke='{StaticResource Grad}' StrokeThickness='1.8' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' Width='20' Height='20' Stretch='Uniform'>
                  <Path.RenderTransform><TranslateTransform x:Name='ArrowT'/></Path.RenderTransform>
                </Path>
              </Border>
            </Grid>
            <TextBlock Text='Drop your files here' FontSize='17' FontWeight='SemiBold' HorizontalAlignment='Center'/>
            <TextBlock Text='mp4 · mov · jpg · png · webp · heic' FontFamily='Cascadia Mono, Consolas' FontSize='11.5' Foreground='#8A8A95' HorizontalAlignment='Center' Margin='0,3,0,14'/>
            <StackPanel Orientation='Horizontal' HorizontalAlignment='Center'>
              <Button x:Name='PickBtn' Style='{StaticResource Pill}' Content='Choose files'/>
              <Button x:Name='InspectBtn' Style='{StaticResource Btn}' Content='Inspect...' Margin='8,0,0,0'/>
            </StackPanel>
          </StackPanel>
          <Grid x:Name='SceneHost' Opacity='0' IsHitTestVisible='False' VerticalAlignment='Center' HorizontalAlignment='Center'>
            <StackPanel>
              <ContentControl x:Name='StageHost' Width='240' Height='132' HorizontalAlignment='Center'/>
              <TextBlock x:Name='SceneText' FontFamily='Cascadia Mono, Consolas' FontSize='11.5' Foreground='#A9A9B3' HorizontalAlignment='Center' Margin='0,-2,0,0'/>
            </StackPanel>
          </Grid>
        </Grid>
      </Border>

      <Grid Grid.Row='1' Margin='0,12,0,0'>
        <TextBlock x:Name='Empty' Text='your files will show up here' FontFamily='Cascadia Mono, Consolas' FontSize='11.5' Foreground='#4A4A54' HorizontalAlignment='Center' VerticalAlignment='Center' Margin='0,0,0,40'/>
        <ScrollViewer x:Name='Scroll' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled' Focusable='False'>
          <StackPanel x:Name='Rows' Margin='0,0,2,0'/>
        </ScrollViewer>
      </Grid>
    </Grid>

    <!-- footer -->
    <Border Grid.Row='2' Background='#D9121215' BorderBrush='#232329' BorderThickness='0,1,0,0'>
      <Grid Margin='14,0'>
        <StackPanel Orientation='Horizontal' VerticalAlignment='Center'>
          <Ellipse x:Name='Led' Width='6' Height='6' Margin='0,0,8,0'>
            <Ellipse.Fill><SolidColorBrush x:Name='LedB' Color='#3C3C46'/></Ellipse.Fill>
            <Ellipse.Effect><DropShadowEffect x:Name='LedFx' Color='#D4FF4A' BlurRadius='8' ShadowDepth='0' Opacity='0'/></Ellipse.Effect>
          </Ellipse>
          <TextBlock x:Name='StatusText' FontFamily='Cascadia Mono, Consolas' FontSize='11.5' Foreground='#74747F' Text='ready'>
            <TextBlock.RenderTransform><TranslateTransform x:Name='StatusT'/></TextBlock.RenderTransform>
          </TextBlock>
        </StackPanel>
        <StackPanel Orientation='Horizontal' HorizontalAlignment='Right' VerticalAlignment='Center'>
          <Border BorderBrush='#2E2E36' BorderThickness='1,1,1,2' CornerRadius='4' Padding='5,0'><TextBlock Text='Ctrl' FontFamily='Cascadia Mono, Consolas' FontSize='10' Foreground='#74747F'/></Border>
          <Border BorderBrush='#2E2E36' BorderThickness='1,1,1,2' CornerRadius='4' Padding='5,0' Margin='4,0,0,0'><TextBlock Text='O' FontFamily='Cascadia Mono, Consolas' FontSize='10' Foreground='#74747F'/></Border>
          <TextBlock Text='choose' FontFamily='Cascadia Mono, Consolas' FontSize='11' Foreground='#74747F' Margin='7,0,0,0'/>
        </StackPanel>
      </Grid>
    </Border>

    <!-- options drawer -->
    <Grid x:Name='Drawer' Grid.Row='1' Grid.RowSpan='2' Visibility='Collapsed'>
      <Border x:Name='Scrim' Background='#B3000000' Opacity='0'/>
      <Border x:Name='DrawerBox' HorizontalAlignment='Right' Width='390' Background='#F7101013' BorderBrush='#2E2E36' BorderThickness='1,0,0,0'>
        <Border.RenderTransform><TranslateTransform x:Name='DrawerT' X='390'/></Border.RenderTransform>
        <Border.Effect><DropShadowEffect Color='#000000' BlurRadius='30' ShadowDepth='0' Opacity='.6'/></Border.Effect>
        <Grid>
          <Grid.RowDefinitions><RowDefinition Height='54'/><RowDefinition Height='*'/></Grid.RowDefinitions>
          <Grid Margin='20,0,10,0'>
            <StackPanel VerticalAlignment='Center'>
              <TextBlock Text='Options' FontSize='16' FontWeight='SemiBold'/>
              <TextBlock Text='choose what gets removed' FontFamily='Cascadia Mono, Consolas' FontSize='11' Foreground='#74747F'/>
            </StackPanel>
            <Button x:Name='DrawerClose' Style='{StaticResource Ico}' HorizontalAlignment='Right' VerticalAlignment='Center'>
              <Path Data='{StaticResource GCross}' Stroke='#A9A9B3' StrokeThickness='1.8' StrokeStartLineCap='Round' StrokeEndLineCap='Round' Width='12' Height='12' Stretch='Uniform'/>
            </Button>
          </Grid>
          <ScrollViewer Grid.Row='1' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled' Focusable='False'>
            <StackPanel Margin='20,4,14,24'>
              <TextBlock Text='PRESET' FontFamily='Cascadia Mono, Consolas' FontSize='10.5' Foreground='#74747F'/>
              <Border Height='36' CornerRadius='11' Background='#0B0B0D' BorderBrush='#232329' BorderThickness='1' Margin='0,8,0,22'>
                <Grid x:Name='Seg' Margin='3'>
                  <Border x:Name='SegPill' HorizontalAlignment='Left' CornerRadius='8' Background='{StaticResource Grad}'>
                    <Border.RenderTransform><TranslateTransform x:Name='SegPillT'/></Border.RenderTransform>
                  </Border>
                  <UniformGrid x:Name='SegBtns' Rows='1'/>
                </Grid>
              </Border>

              <TextBlock Text='REMOVE' FontFamily='Cascadia Mono, Consolas' FontSize='10.5' Foreground='#74747F' Margin='0,0,0,12'/>
              <StackPanel x:Name='RemoveRows'/>

              <TextBlock x:Name='KeepLabel' Text='KEEP (ALL METADATA PRESET)' FontFamily='Cascadia Mono, Consolas' FontSize='10.5' Foreground='#74747F' Margin='0,8,0,12'/>
              <StackPanel x:Name='KeepRows'/>

              <TextBlock Text='INTERFACE' FontFamily='Cascadia Mono, Consolas' FontSize='10.5' Foreground='#74747F' Margin='0,8,0,12'/>
              <StackPanel x:Name='UiRows'/>

              <TextBlock Text='ALSO REMOVE THESE TAGS' FontFamily='Cascadia Mono, Consolas' FontSize='10.5' Foreground='#74747F' Margin='0,8,0,8'/>
              <Grid>
                <TextBox x:Name='TagsBox' Style='{StaticResource Input}' AcceptsReturn='False'/>
                <TextBlock x:Name='TagsHint' Text='e.g. XMP-xmp:CreatorTool, EXIF:Software' IsHitTestVisible='False' FontFamily='Cascadia Mono, Consolas' FontSize='12' Foreground='#4A4A54' Margin='11,9,0,0'/>
              </Grid>
              <TextBlock x:Name='TagsNote' FontFamily='Cascadia Mono, Consolas' FontSize='10.5' Foreground='#74747F' TextWrapping='Wrap' Margin='0,6,0,0'/>

              <TextBlock Text='EQUIVALENT EXIFTOOL COMMAND' FontFamily='Cascadia Mono, Consolas' FontSize='10.5' Foreground='#74747F' Margin='0,20,0,8'/>
              <Border CornerRadius='10' Background='#0B0B0D' BorderBrush='#232329' BorderThickness='1' Padding='10,8'>
                <TextBlock x:Name='CmdText' FontFamily='Cascadia Mono, Consolas' FontSize='10.5' Foreground='#A9A9B3' TextWrapping='Wrap'/>
              </Border>
              <StackPanel Orientation='Horizontal' Margin='0,12,0,0'>
                <Button x:Name='CopyBtn' Style='{StaticResource Btn}' Content='Copy command' Margin='0,0,8,0'/>
                <Button x:Name='ResetBtn' Style='{StaticResource Btn}' Content='Reset'/>
              </StackPanel>
            </StackPanel>
          </ScrollViewer>
        </Grid>
      </Border>
    </Grid>
    <!-- toast -->
    <Border x:Name='Toast' Grid.RowSpan='3' VerticalAlignment='Top' HorizontalAlignment='Center' Margin='0,54,0,0' Padding='12,7' CornerRadius='16'
            Background='#F2181A1C' BorderBrush='#2E2E36' BorderThickness='1' Opacity='0' IsHitTestVisible='False' RenderTransformOrigin='.5,.5'>
      <Border.RenderTransform><TransformGroup><ScaleTransform x:Name='ToastSc' ScaleX='.95' ScaleY='.95'/><TranslateTransform x:Name='ToastT' Y='-8'/></TransformGroup></Border.RenderTransform>
      <Border.Effect><DropShadowEffect Color='#000000' BlurRadius='24' ShadowDepth='6' Opacity='.6'/></Border.Effect>
      <StackPanel Orientation='Horizontal'>
        <Path x:Name='ToastIc' Width='12' Height='12' Stretch='Uniform' StrokeThickness='2' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' VerticalAlignment='Center'/>
        <TextBlock x:Name='ToastTx' FontFamily='Cascadia Mono, Consolas' FontSize='11.5' FontWeight='SemiBold' Margin='8,0,0,0' VerticalAlignment='Center'/>
      </StackPanel>
    </Border>

    <!-- drag overlay: marching ants -->
    <Grid x:Name='Overlay' Grid.RowSpan='3' IsHitTestVisible='False' Opacity='0'>
      <Grid.Background><SolidColorBrush Color='#EB0B0B0D'/></Grid.Background>
      <Ellipse Width='420' Height='420' IsHitTestVisible='False'>
        <Ellipse.Fill><RadialGradientBrush><GradientStop Color='#2ED4FF4A' Offset='0'/><GradientStop Color='#00D4FF4A' Offset='1'/></RadialGradientBrush></Ellipse.Fill>
      </Ellipse>
      <Rectangle x:Name='Ants' Margin='10' RadiusX='16' RadiusY='16' StrokeThickness='1.5' StrokeDashArray='5 4.5' Stroke='{StaticResource Grad}'/>
      <StackPanel VerticalAlignment='Center' HorizontalAlignment='Center' RenderTransformOrigin='.5,.5'>
        <StackPanel.RenderTransform><ScaleTransform x:Name='OvSc' ScaleX='.94' ScaleY='.94'/></StackPanel.RenderTransform>
        <Border Width='64' Height='64' CornerRadius='20' Background='#26D4FF4A' HorizontalAlignment='Center' Margin='0,0,0,16'>
          <Path Data='{StaticResource GArrow}' Stroke='{StaticResource Grad}' StrokeThickness='1.8' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' Width='30' Height='30' Stretch='Uniform'>
            <Path.RenderTransform><TranslateTransform x:Name='OvArrowT'/></Path.RenderTransform>
          </Path>
        </Border>
        <TextBlock Text='Drop to clean' FontSize='20' FontWeight='SemiBold' HorizontalAlignment='Center'/>
        <TextBlock Text='a _clean copy is created, the original is never touched' FontFamily='Cascadia Mono, Consolas' FontSize='11.5' Foreground='#A9A9B3' HorizontalAlignment='Center' Margin='0,5,0,0'/>
      </StackPanel>
    </Grid>
  </Grid>
</Window>";

    readonly Window w;
    readonly List<Job> jobs = new List<Job>();
    static readonly object Gate = new object();

    T G<T>(string n) where T : class
    {
        object o = w.FindName(n);
        if (o == null) throw new InvalidOperationException("missing element " + n);
        return (T)o;
    }

    readonly Panel rows;
    readonly UIElement empty, overlay, toast, clearBtn, zone, spotRect;
    readonly TextBlock pillText, statusText, toastTx;
    readonly SolidColorBrush pillBb, pillLedB, ledB, zoneBb;
    readonly DropShadowEffect ledFx;
    readonly ScaleTransform pillSc, ovSc, toastSc, sparkSc, ringSc;
    readonly TranslateTransform toastT, statusT, arrowT, ovArrowT;
    readonly RotateTransform sparkRot, dashRot, sweepRot;
    readonly System.Windows.Shapes.Path toastIc;
    readonly RadialGradientBrush spot;
    readonly DispatcherTimer dragTimer = new DispatcherTimer(), toastTimer = new DispatcherTimer();
    readonly FrameworkElement logoRing, zoneSweep, root;
    readonly ScrollViewer scroll;
    int lastDrag, lastCount = -1, lastMenuClose;
    bool busy, overlayOn;

    public Window Window { get { return w; } }

    public MainWin(string[] args)
    {
        w = (Window)XamlReader.Parse(WinXaml);
        root = G<FrameworkElement>("Root");
        rows = G<Panel>("Rows"); empty = G<UIElement>("Empty"); overlay = G<UIElement>("Overlay");
        toast = G<UIElement>("Toast"); clearBtn = G<UIElement>("ClearBtn"); zone = G<UIElement>("Zone"); spotRect = G<UIElement>("SpotRect");
        scroll = G<ScrollViewer>("Scroll");
        pillText = G<TextBlock>("PillText"); statusText = G<TextBlock>("StatusText"); toastTx = G<TextBlock>("ToastTx");
        pillBb = G<SolidColorBrush>("PillBb"); pillLedB = G<SolidColorBrush>("PillLedB"); ledB = G<SolidColorBrush>("LedB"); zoneBb = G<SolidColorBrush>("ZoneBb");
        ledFx = G<DropShadowEffect>("LedFx");
        pillSc = G<ScaleTransform>("PillSc"); ovSc = G<ScaleTransform>("OvSc"); toastSc = G<ScaleTransform>("ToastSc");
        sparkSc = G<ScaleTransform>("SparkSc"); ringSc = G<ScaleTransform>("RingSc");
        toastT = G<TranslateTransform>("ToastT"); statusT = G<TranslateTransform>("StatusT"); arrowT = G<TranslateTransform>("ArrowT"); ovArrowT = G<TranslateTransform>("OvArrowT");
        sparkRot = G<RotateTransform>("SparkRot"); dashRot = G<RotateTransform>("DashRot"); sweepRot = G<RotateTransform>("SweepRot");
        toastIc = G<System.Windows.Shapes.Path>("ToastIc");
        spot = G<RadialGradientBrush>("Spot");
        using (var ls = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png"))
        {
            var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = ls; bi.EndInit(); bi.Freeze();
            G<Image>("LogoImg").Source = bi;
        }        logoRing = G<FrameworkElement>("LogoRing"); zoneSweep = G<FrameworkElement>("ZoneSweep");

        G<Button>("MinBtn").Click += delegate { w.WindowState = WindowState.Minimized; };
        G<Button>("CloseBtn").Click += delegate { w.Close(); };
        G<Button>("PickBtn").Click += delegate { Pick(); };

        var menuPop = G<System.Windows.Controls.Primitives.Popup>("MenuPop");
        menuPop.PlacementTarget = G<UIElement>("MenuBtn");
        var menuBox = G<UIElement>("MenuBox");
        var menuSc = G<ScaleTransform>("MenuSc"); var menuT = G<TranslateTransform>("MenuT");
        // StaysOpen=false closes the popup on the mouse-down that also triggers Click, so ignore a re-open right after closing
        G<Button>("MenuBtn").Click += delegate { if (Environment.TickCount - lastMenuClose > 250) menuPop.IsOpen = true; };
        menuPop.Closed += delegate { lastMenuClose = Environment.TickCount; A.To(menuBox, UIElement.OpacityProperty, 0, 1); };
        menuPop.Opened += delegate
        {
            A.To(menuBox, UIElement.OpacityProperty, 1, 180);
            A.To(menuT, TranslateTransform.YProperty, 0, 380, A.Spring, 0, -6, null);
            A.To(menuSc, ScaleTransform.ScaleXProperty, 1, 380, A.Spring, 0, .96, null);
            A.To(menuSc, ScaleTransform.ScaleYProperty, 1, 380, A.Spring, 0, .96, null);
        };
        G<Button>("AboutItem").Click += delegate { menuPop.IsOpen = false; About(); };
        G<Button>("UninstallItem").Click += delegate { menuPop.IsOpen = false; UninstallFlow(); };
        G<Button>("ClearBtn").Click += delegate { ClearList(); };
        zone.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { if (!(e.OriginalSource is DependencyObject && IsInButton((DependencyObject)e.OriginalSource))) Pick(); };
        InitOptions();
        InitStage();
        zone.MouseEnter += delegate { A.Tint(zoneBb, A.C("#80D4FF4A"), 250); };
        zone.MouseLeave += delegate { A.Tint(zoneBb, A.C("#2E2E36"), 300); };

        // ripple on the lime buttons
        EventManager.RegisterClassHandler(typeof(Button), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Ripple));

        w.PreviewMouseMove += delegate(object s, MouseEventArgs e)
        {
            Point p = e.GetPosition(root);
            spot.Center = p; spot.GradientOrigin = p;
        };
        w.PreviewKeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.Key == Key.O && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { Pick(); e.Handled = true; }
            else if (e.Key == Key.Escape && inspector != null && inspector.IsOpen) { inspector.Close(); e.Handled = true; }
            else if (e.Key == Key.Escape && drawerOpen) { ToggleDrawer(false); e.Handled = true; }
        };

        // drag and drop anywhere on the window
        w.DragEnter += OnDrag; w.DragOver += OnDrag;
        w.Drop += delegate(object s, DragEventArgs e)
        {
            ShowOverlay(false);
            string[] f = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (f != null) AddPaths(f);
            e.Handled = true;
        };
        dragTimer.Interval = TimeSpan.FromMilliseconds(120);
        dragTimer.Tick += delegate { if (overlayOn && Environment.TickCount - lastDrag > 260) ShowOverlay(false); };
        dragTimer.Start();

        toastTimer.Tick += delegate { toastTimer.Stop(); HideToast(); };

        w.SourceInitialized += delegate
        {
            IntPtr h = new WindowInteropHelper(w).Handle;
            int on = 1, round = 2, border = 0x00292923; // border #232329 (BGR)
            Native.DwmSetWindowAttribute(h, 20, ref on, 4);      // dark title bar
            Native.DwmSetWindowAttribute(h, 33, ref round, 4);   // rounded corners (Win11)
            Native.DwmSetWindowAttribute(h, 34, ref border, 4);  // border color
            HwndSource.FromHwnd(h).AddHook(delegate(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
            {
                if (msg == 0x00A3) handled = true; // double-click on the title bar: no maximize
                return IntPtr.Zero;
            });
        };
        w.StateChanged += delegate { if (w.WindowState == WindowState.Maximized) w.WindowState = WindowState.Normal; };
        w.Closing += delegate { Cleaner.Cancel(); };

        w.Loaded += delegate
        {
            // idle loops
            A.Loop(dashRot, RotateTransform.AngleProperty, 0, 360, 14000, null, false);
            A.Loop(arrowT, TranslateTransform.YProperty, 0, -3, 1800, A.Sine, true);
            A.Loop(ovArrowT, TranslateTransform.YProperty, 0, 6, 700, A.Sine, true);
            A.Loop((IAnimatable)G<Rectangle>("Ants"), Shape.StrokeDashOffsetProperty, 0, -9.5, 1000, null, false);
            A.To(zone, UIElement.OpacityProperty, 1, 600, A.Out, 0, 0, null);
            Refresh();
            if (args.Length > 0) AddPaths(args);
        };
    }

    static bool IsInButton(DependencyObject d)
    {
        while (d != null) { if (d is Button) return true; d = (d is Visual || d is System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d); }
        return false;
    }

    // ----- ripple -----

    void Ripple(object sender, MouseButtonEventArgs e)
    {
        var b = sender as Button;
        if (b == null || b.Template == null) return;
        var rip = b.Template.FindName("Rip", b) as Canvas;
        if (rip == null) return;
        Point p = e.GetPosition(rip);
        double d = Math.Max(b.ActualWidth, b.ActualHeight) * 2.4;
        rip.Clip = new RectangleGeometry(new Rect(0, 0, rip.ActualWidth, rip.ActualHeight), 16, 16);
        var el = new Ellipse { Width = d, Height = d, Fill = new SolidColorBrush(Color.FromArgb(130, 255, 255, 255)), RenderTransformOrigin = new Point(.5, .5) };
        var st = new ScaleTransform(0, 0);
        el.RenderTransform = st;
        Canvas.SetLeft(el, p.X - d / 2); Canvas.SetTop(el, p.Y - d / 2);
        rip.Children.Add(el);
        A.To(st, ScaleTransform.ScaleXProperty, 1, 600, A.Out, 0, 0, null);
        A.To(st, ScaleTransform.ScaleYProperty, 1, 600, A.Out, 0, 0, null);
        A.To(el, UIElement.OpacityProperty, 0, 600, A.Out, 0, 1, delegate { rip.Children.Remove(el); });
    }

    // ----- drag -----

    void OnDrag(object s, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; ShowOverlay(true); lastDrag = Environment.TickCount; }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    void ShowOverlay(bool on)
    {
        if (on == overlayOn) return;
        overlayOn = on;
        A.To(overlay, UIElement.OpacityProperty, on ? 1 : 0, on ? 220 : 180);
        A.To(ovSc, ScaleTransform.ScaleXProperty, on ? 1 : .94, 380, A.Spring, 0, null, null);
        A.To(ovSc, ScaleTransform.ScaleYProperty, on ? 1 : .94, 380, A.Spring, 0, null, null);
        if (on) lastDrag = Environment.TickCount;
    }

    // ----- files -----

    void Pick()
    {
        var d = new OpenFileDialog();
        d.Multiselect = true;
        d.Title = "Choose the files to clean";
        d.Filter = "Videos and photos|*.mp4;*.mov;*.m4v;*.3gp;*.jpg;*.jpeg;*.png;*.webp;*.heic;*.heif;*.tif;*.tiff;*.gif|All files|*.*";
        if (d.ShowDialog(w) == true) AddPaths(d.FileNames);
    }

    static readonly string[] MediaExt = { ".mp4", ".mov", ".m4v", ".3gp", ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif", ".tif", ".tiff", ".gif" };

    // collects media files below a folder, skipping folders we can't read and symlink/junction loops
    static void Walk(string dir, List<string> files)
    {
        try
        {
            foreach (string f in Directory.GetFiles(dir))
                if (Array.IndexOf(MediaExt, System.IO.Path.GetExtension(f).ToLowerInvariant()) >= 0) files.Add(f);
            foreach (string d in Directory.GetDirectories(dir))
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0) Walk(d, files);
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    void AddPaths(string[] paths)
    {
        var files = new List<string>();
        foreach (string raw in paths)
        {
            string p;
            try { p = System.IO.Path.GetFullPath(raw); } catch (Exception) { continue; }
            if (Directory.Exists(p)) Walk(p, files);
            else if (File.Exists(p)) files.Add(p);
        }
        // skip files that are already in the list (or repeated in this drop)
        var fresh = new List<string>();
        foreach (string f in files)
        {
            string ff = f;
            bool known = jobs.Exists(delegate(Job x) { return string.Equals(x.Src, ff, StringComparison.OrdinalIgnoreCase); })
                || fresh.Exists(delegate(string x) { return string.Equals(x, ff, StringComparison.OrdinalIgnoreCase); });
            if (!known) fresh.Add(f);
        }
        files = fresh;
        if (files.Count == 0) return;
        var batch = new List<Job>();
        int i = 0;
        foreach (string f in files)
        {
            var j = new Job { Src = f, Args = Opts.Build(Opts.IsImage(f)), CheckC2pa = Opts.WantsC2pa() };
            j.Row = new RowView(j, w);
            j.Row.InspectRequested += OnInspect;
            rows.Children.Add(j.Row.Card);
            j.Row.In(Math.Min(i, 8) * 70);
            jobs.Add(j); batch.Add(j); i++;
        }
        scroll.ScrollToBottom();
        Refresh();

        // With the animation on, each file takes at least a moment (about 3 s per batch in total) so the cleaning can be seen.
        int minMs = Opts.Anim ? Math.Max(250, Math.Min(1500, 3000 / batch.Count)) : 0;

        // one batch at a time, on a worker thread so the window never freezes
        ThreadPool.QueueUserWorkItem(delegate
        {
            lock (Gate)
            {
                foreach (Job j in batch)
                {
                    if (Cleaner.Cancelled) break;
                    Job jj = j;
                    w.Dispatcher.BeginInvoke(new Action(delegate { jj.State = State.Working; jj.Row.SetState(State.Working); Refresh(); }));
                    var sw = Stopwatch.StartNew();
                    try { Cleaner.Clean(jj); }
                    catch (Exception ex) { jj.State = State.Error; jj.Note = ex.Message; }
                    while (!Cleaner.Cancelled && sw.ElapsedMilliseconds < minMs) Thread.Sleep(20);
                    w.Dispatcher.BeginInvoke(new Action(delegate { jj.Row.SetState(jj.State); Refresh(); }));
                }
            }
        });
    }

    void ClearList()
    {
        if (busy || jobs.Count == 0) return;
        var old = new List<Job>(jobs);
        jobs.Clear();
        int i = 0;
        foreach (Job j in old)
        {
            Job jj = j;
            j.Row.Out(Math.Min(i, 8) * 25, delegate { rows.Children.Remove(jj.Row.Card); });
            i++;
        }
        Refresh();
    }

    // ----- global state -----

    void Refresh()
    {
        int ok = 0, warn = 0, err = 0, pend = 0;
        foreach (Job j in jobs)
        {
            switch (j.State)
            {
                case State.Done: ok++; break;
                case State.Warn: warn++; break;
                case State.Error: err++; break;
                default: pend++; break;
            }
        }
        int total = jobs.Count;
        bool nowBusy = pend > 0;

        pillText.Text = total == 0 ? "no files" : total + (total == 1 ? " file" : " files");
        if (lastCount >= 0 && total != lastCount && total > 0)
        {   // counter bump
            A.To(pillSc, ScaleTransform.ScaleXProperty, 1.18, 120, A.Out, 0, null, delegate { A.To(pillSc, ScaleTransform.ScaleXProperty, 1, 380, A.Spring, 0, null, null); });
            A.To(pillSc, ScaleTransform.ScaleYProperty, 1.18, 120, A.Out, 0, null, delegate { A.To(pillSc, ScaleTransform.ScaleYProperty, 1, 380, A.Spring, 0, null, null); });
        }
        lastCount = total;
        A.Tint(pillBb, total > 0 ? A.C("#40D4FF4A") : A.C("#2E2E36"), 300);
        A.Tint(pillLedB, total > 0 ? A.C("#D4FF4A") : A.C("#3C3C46"), 300);

        empty.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool canClear = total > 0 && !nowBusy;
        clearBtn.IsHitTestVisible = canClear;
        A.To(clearBtn, UIElement.OpacityProperty, canClear ? 1 : 0, 250);

        string text, led; bool pulse = false; double glow = 0;
        if (nowBusy)
        {
            int done = ok + warn + err;
            text = "cleaning " + Math.Min(done + 1, total) + " of " + total;
            led = "#D4FF4A"; pulse = true; glow = .7;
        }
        else if (total == 0) { text = "ready"; led = "#3C3C46"; }
        else
        {
            var parts = new List<string>();
            if (ok > 0) parts.Add(ok + " clean");
            if (warn > 0) parts.Add(warn + " to review");
            if (err > 0) parts.Add(err + (err == 1 ? " error" : " errors"));
            text = string.Join(" · ", parts.ToArray());
            led = err > 0 ? "#FF5F56" : (warn > 0 ? "#FFB454" : "#3DDC97"); glow = .6;
        }
        if (statusText.Text != text) A.Swap(statusText, statusT, text);
        if (nowBusy) sceneText.Text = text;
        statusText.Foreground = new SolidColorBrush(A.C(nowBusy ? "#A9A9B3" : "#74747F"));
        A.Tint(ledB, A.C(led), 300);
        ledFx.Color = A.C(led);
        A.To(ledFx, DropShadowEffect.OpacityProperty, glow, 300);
        if (pulse) A.Loop(ledB, Brush.OpacityProperty, 1, .35, 800, A.Sine, true);
        else A.To(ledB, Brush.OpacityProperty, 1, 200);

        if (nowBusy != busy)
        {
            bool wasBusy = busy;
            busy = nowBusy;
            SetBusy(busy);
            if (wasBusy && !busy) Finished(ok, warn, err);
        }
    }

    // busy state: the logo wiggles with a pulsing ring and the drop zone gets a rotating gradient border
    void SetBusy(bool on)
    {
        StageBusy(on);
        if (on)
        {
            A.Loop(sparkRot, RotateTransform.AngleProperty, -6, 8, 700, A.Sine, true);
            A.Loop(sparkSc, ScaleTransform.ScaleXProperty, 1, 1.18, 700, A.Sine, true);
            A.Loop(sparkSc, ScaleTransform.ScaleYProperty, 1, 1.18, 700, A.Sine, true);
            A.Loop(ringSc, ScaleTransform.ScaleXProperty, .85, 1.25, 1600, A.Out, false);
            A.Loop(ringSc, ScaleTransform.ScaleYProperty, .85, 1.25, 1600, A.Out, false);
            A.Loop(logoRing, UIElement.OpacityProperty, .7, 0, 1600, A.Out, false);
            A.To(zoneSweep, UIElement.OpacityProperty, 1, 400);
            A.Loop(sweepRot, RotateTransform.AngleProperty, 0, 360, 2200, null, false);
            A.To(spotRect, UIElement.OpacityProperty, .65, 400);
        }
        else
        {
            A.To(sparkRot, RotateTransform.AngleProperty, 0, 1, null, 0, null, null);
            A.To(sparkSc, ScaleTransform.ScaleXProperty, 1, 300);
            A.To(sparkSc, ScaleTransform.ScaleYProperty, 1, 300);
            A.To(logoRing, UIElement.OpacityProperty, 0, 200);
            A.To(zoneSweep, UIElement.OpacityProperty, 0, 400);
            A.To(sweepRot, RotateTransform.AngleProperty, 0, 1, null, 0, null, null);
            A.To(spotRect, UIElement.OpacityProperty, .4, 400);
        }
    }

    void Finished(int ok, int warn, int err)
    {
        if (err == 0 && warn == 0)
            ShowToast(ok + (ok == 1 ? " file cleaned" : " files cleaned"), "#3DDC97", "GCheck");
        else if (ok == 0)
            ShowToast(err > 0 ? "could not clean" : "check the files", "#FF5F56", "GCross");
        else
            ShowToast(ok + " cleaned, " + (warn + err) + " with problems", "#FFB454", "GCheck");
    }

    // ----- options drawer -----

    const string OptRowXaml = @"
<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Margin='0,0,0,14'>
  <Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
  <StackPanel Margin='0,0,14,0'>
    <TextBlock x:Name='T' FontSize='13' FontWeight='SemiBold'/>
    <TextBlock x:Name='D' FontFamily='Cascadia Mono, Consolas' FontSize='10.5' Foreground='#74747F' TextWrapping='Wrap' Margin='0,3,0,0'/>
  </StackPanel>
  <ToggleButton x:Name='Sw' Grid.Column='1' VerticalAlignment='Top' Margin='0,1,0,0'/>
</Grid>";

    static readonly string[][] Presets = { new[] { "all", "All" }, new[] { "privacy", "Privacy" }, new[] { "provenance", "Provenance" }, new[] { "custom", "Custom" } };

    FrameworkElement drawer, scrim, segPill;
    TranslateTransform drawerT, segPillT;
    Panel removeRows, keepRows;
    UIElement[] removeRowEls, keepRowEls;
    ToggleButton[] removeSw, keepSw;
    Button[] segBtns;
    TextBox tagsBox;
    TextBlock tagsHint, tagsNote, cmdText, keepLabel;
    bool drawerOpen, syncing;
    ToggleButton animSw;
    PixelStage mainStage;
    FrameworkElement zoneContent, sceneHost;
    TextBlock sceneText;
    InspectorPanel inspector;
    double segW;

    void InitOptions()
    {
        drawer = G<FrameworkElement>("Drawer"); scrim = G<FrameworkElement>("Scrim");
        segPill = G<FrameworkElement>("SegPill"); drawerT = G<TranslateTransform>("DrawerT"); segPillT = G<TranslateTransform>("SegPillT");
        removeRows = G<Panel>("RemoveRows"); keepRows = G<Panel>("KeepRows");
        tagsBox = G<TextBox>("TagsBox"); tagsHint = G<TextBlock>("TagsHint"); tagsNote = G<TextBlock>("TagsNote");
        cmdText = G<TextBlock>("CmdText"); keepLabel = G<TextBlock>("KeepLabel");

        Opts.Load();

        // segmented preset control
        var segHost = G<Panel>("SegBtns");
        segBtns = new Button[Presets.Length];
        for (int i = 0; i < Presets.Length; i++)
        {
            string key = Presets[i][0];
            var b = new Button { Content = Presets[i][1], Style = (Style)w.FindResource("SegBtn") };
            b.Click += delegate { Opts.Apply(key); Opts.Save(); SyncPanel(); };
            segBtns[i] = b; segHost.Children.Add(b);
        }
        G<FrameworkElement>("Seg").SizeChanged += delegate(object s, SizeChangedEventArgs e)
        {
            segW = (e.NewSize.Width) / Presets.Length;
            segPill.Width = segW;
            MovePill(false);
        };

        // one switch per category
        removeSw = new ToggleButton[Opts.Items.Length]; removeRowEls = new UIElement[Opts.Items.Length];
        for (int i = 0; i < Opts.Items.Length; i++)
        {
            Option o = Opts.Items[i];
            var row = (FrameworkElement)XamlReader.Parse(OptRowXaml);
            ((TextBlock)row.FindName("T")).Text = o.Title; ((TextBlock)row.FindName("D")).Text = o.Desc;
            var sw = (ToggleButton)row.FindName("Sw"); sw.Style = (Style)w.FindResource("Switch");
            sw.Click += delegate { if (syncing) return; o.On = sw.IsChecked == true; Opts.Apply("custom"); Opts.Save(); SyncPanel(); };
            removeSw[i] = sw; removeRowEls[i] = row; removeRows.Children.Add(row);
        }
        keepSw = new ToggleButton[2]; keepRowEls = new UIElement[2];
        string[][] keeps = { new[] { "Color profile", "keep the ICC profile so colors don't shift" }, new[] { "Orientation", "keep EXIF rotation so photos stay upright" } };
        for (int i = 0; i < 2; i++)
        {
            int idx = i;
            var row = (FrameworkElement)XamlReader.Parse(OptRowXaml);
            ((TextBlock)row.FindName("T")).Text = keeps[i][0]; ((TextBlock)row.FindName("D")).Text = keeps[i][1];
            var sw = (ToggleButton)row.FindName("Sw"); sw.Style = (Style)w.FindResource("Switch");
            sw.Click += delegate { if (syncing) return; if (idx == 0) Opts.KeepIcc = sw.IsChecked == true; else Opts.KeepOri = sw.IsChecked == true; Opts.Save(); SyncPanel(); };
            keepSw[i] = sw; keepRowEls[i] = row; keepRows.Children.Add(row);
        }

        tagsBox.Text = Opts.TagText;
        tagsBox.TextChanged += delegate { if (syncing) return; Opts.TagText = tagsBox.Text; Opts.Save(); SyncPanel(); };

        G<Button>("CopyBtn").Click += delegate
        {
            try { Clipboard.SetText(FullCommand()); ShowToast("command copied", "#3DDC97", "GCheck"); } catch (Exception) { }
        };
        G<Button>("ResetBtn").Click += delegate { Opts.Reset(); Opts.Save(); SyncPanel(); };
        G<Button>("DrawerClose").Click += delegate { ToggleDrawer(false); };
        scrim.MouseLeftButtonUp += delegate { ToggleDrawer(false); };
        G<Button>("OptBtn").Click += delegate { ToggleDrawer(!drawerOpen); };
        SyncPanel();
    }

    void MovePill(bool animate)
    {
        int idx = 0;
        for (int i = 0; i < Presets.Length; i++) if (Presets[i][0] == Opts.Preset) idx = i;
        if (animate) A.To(segPillT, TranslateTransform.XProperty, idx * segW, 450, A.Spring, 0, null, null);
        else segPillT.BeginAnimation(TranslateTransform.XProperty, null);
        if (!animate) segPillT.X = idx * segW;
        for (int i = 0; i < segBtns.Length; i++) segBtns[i].Foreground = new SolidColorBrush(A.C(i == idx ? "#0B0B0D" : "#A9A9B3"));
    }

    // pushes the current settings into every control
    void SyncPanel()
    {
        syncing = true;
        bool all = Opts.Preset == "all";
        for (int i = 0; i < Opts.Items.Length; i++)
        {
            removeSw[i].IsChecked = Opts.Items[i].On;
            removeSw[i].IsEnabled = !all;
            A.To(removeRowEls[i], UIElement.OpacityProperty, all ? .5 : 1, 250);
        }
        keepSw[0].IsChecked = Opts.KeepIcc; keepSw[1].IsChecked = Opts.KeepOri;
        if (animSw != null) animSw.IsChecked = Opts.Anim;
        for (int i = 0; i < 2; i++) { keepSw[i].IsEnabled = all; A.To(keepRowEls[i], UIElement.OpacityProperty, all ? 1 : .35, 250); }
        A.To(keepLabel, UIElement.OpacityProperty, all ? 1 : .35, 250);
        if (tagsBox.Text != Opts.TagText) tagsBox.Text = Opts.TagText;
        tagsBox.IsEnabled = !all;
        A.To(tagsBox, UIElement.OpacityProperty, all ? .4 : 1, 250);
        tagsHint.Visibility = tagsBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        syncing = false;

        List<string> bad; Opts.Tags(out bad);
        tagsNote.Foreground = new SolidColorBrush(A.C(bad.Count > 0 ? "#FFB454" : "#74747F"));
        tagsNote.Text = bad.Count > 0 ? "ignored (not a tag name): " + string.Join(", ", bad.ToArray())
            : (all ? "not needed: the preset already removes everything" : "comma-separated, wildcards allowed (GPS*)");

        MovePill(true);
        UpdateCommand();
    }

    string FullCommand()
    {
        var a = Opts.Build(true);
        if (a.Count == 0) return "";
        return "exiftool " + string.Join(" ", a.ToArray()) + " -o file_clean.jpg file.jpg";
    }

    void UpdateCommand()
    {
        string full = FullCommand();
        if (full.Length == 0) { cmdText.Foreground = new SolidColorBrush(A.C("#FFB454")); cmdText.Text = "nothing selected: turn on at least one option"; return; }
        cmdText.Foreground = new SolidColorBrush(A.C("#A9A9B3"));
        cmdText.Text = full.Length > 190 ? full.Substring(0, 190) + " ..." : full;
    }

    void ToggleDrawer(bool open)
    {
        if (open == drawerOpen) return;
        drawerOpen = open;
        if (open)
        {
            drawer.Visibility = Visibility.Visible;
            A.To(scrim, UIElement.OpacityProperty, 1, 250);
            A.To(drawerT, TranslateTransform.XProperty, 0, 500, A.Spring, 0, 390, null);
        }
        else
        {
            A.To(scrim, UIElement.OpacityProperty, 0, 220);
            A.To(drawerT, TranslateTransform.XProperty, 390, 300, A.Out, 0, null, delegate { if (!drawerOpen) drawer.Visibility = Visibility.Collapsed; });
        }
    }
    // ----- pixel-art scenes -----

    void InitStage()
    {
        zoneContent = G<FrameworkElement>("ZoneContent"); sceneHost = G<FrameworkElement>("SceneHost"); sceneText = G<TextBlock>("SceneText");
        mainStage = new PixelStage();
        G<ContentControl>("StageHost").Content = mainStage;
        inspector = new InspectorPanel(w, (Panel)root, G<UIElement>("Toast"), delegate(string f) { AddPaths(new[] { f }); });

        G<Button>("InspectBtn").Click += delegate
        {
            var d = new OpenFileDialog();
            d.Title = "Choose a file to inspect";
            d.Filter = "Videos and photos|*.mp4;*.mov;*.m4v;*.3gp;*.jpg;*.jpeg;*.png;*.webp;*.heic;*.heif;*.tif;*.tiff;*.gif|All files|*.*";
            if (d.ShowDialog(w) == true) inspector.Open(d.FileName, null);
        };

        // the animation switch lives in the Options panel
        var row = (FrameworkElement)XamlReader.Parse(OptRowXaml);
        ((TextBlock)row.FindName("T")).Text = "Cleaning animation";
        ((TextBlock)row.FindName("D")).Text = "brush and dust while cleaning; adds about a second";
        animSw = (ToggleButton)row.FindName("Sw"); animSw.Style = (Style)w.FindResource("Switch");
        animSw.IsChecked = Opts.Anim;
        animSw.Click += delegate { if (syncing) return; Opts.Anim = animSw.IsChecked == true; Opts.Save(); };
        G<Panel>("UiRows").Children.Add(row);
    }

    void OnInspect(Job j)
    {
        if (j.State == State.Working) return;
        inspector.Open(j.Src, (j.State == State.Done || j.State == State.Warn) && j.Out != null ? j.Out : null);
    }

    // while files are being cleaned the drop zone turns into the brush-and-document scene
    void StageBusy(bool on)
    {
        if (on)
        {
            if (!Opts.Anim) return;
            A.To(zoneContent, UIElement.OpacityProperty, 0, 220);
            A.To(sceneHost, UIElement.OpacityProperty, 1, 250);
            mainStage.Play(StageMode.Clean);
        }
        else
        {
            mainStage.Stop(delegate
            {
                A.To(sceneHost, UIElement.OpacityProperty, 0, 220);
                A.To(zoneContent, UIElement.OpacityProperty, 1, 300);
            });
        }
    }
    // ----- about / uninstall -----

    void About()
    {
        string ver = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        Dlg.Show(w, "CleanMetadata " + ver,
            "A simple GUI for ExifTool.\nExifTool " + Tools.Version() + ", by Phil Harvey, does all the metadata work.\n\nMIT licensed.\nhttps://github.com/romanovak/CleanMetadata",
            "OK", null, false);
    }

    void UninstallFlow()
    {
        if (busy) { Dlg.Show(w, "Still working", "Wait until the current files are cleaned, then try again.", "OK", null, false); return; }
        if (!Dlg.Show(w, "Uninstall CleanMetadata?",
            "This deletes the ExifTool files and settings stored in %LOCALAPPDATA%\\CleanMetadata. Then Explorer opens with CleanMetadata.exe selected so you can delete it.\n\nYour original and _clean files are not touched.",
            "Uninstall", "Cancel", true)) return;
        Uninstaller.Run();
        Uninstaller.RevealExe();
        w.Close();
    }
    // ----- toast -----

    void ShowToast(string text, string color, string geometry)
    {
        toastTx.Text = text;
        toastTx.Foreground = new SolidColorBrush(A.C("#ECECEF"));
        toastIc.Data = (Geometry)w.FindResource(geometry);
        toastIc.Stroke = new SolidColorBrush(A.C(color));
        A.To(toast, UIElement.OpacityProperty, 1, 200);
        A.To(toastT, TranslateTransform.YProperty, 0, 450, A.Spring, 0, null, null);
        A.To(toastSc, ScaleTransform.ScaleXProperty, 1, 450, A.Spring, 0, null, null);
        A.To(toastSc, ScaleTransform.ScaleYProperty, 1, 450, A.Spring, 0, null, null);
        toastTimer.Interval = TimeSpan.FromMilliseconds(2600);
        toastTimer.Stop(); toastTimer.Start();
    }

    void HideToast()
    {
        A.To(toast, UIElement.OpacityProperty, 0, 250);
        A.To(toastT, TranslateTransform.YProperty, -8, 300, A.Out, 0, null, null);
        A.To(toastSc, ScaleTransform.ScaleXProperty, .95, 300, A.Out, 0, null, null);
        A.To(toastSc, ScaleTransform.ScaleYProperty, .95, 300, A.Out, 0, null, null);
    }
}

// ---------- uninstall ----------

static class Uninstaller
{
    // Removes everything the app writes: the unpacked ExifTool and the settings file. It deliberately does not delete
    // the exe itself. A program that launches a hidden shell to delete its own file is exactly what antivirus heuristics
    // look for, so the user removes the exe and Explorer opens with it selected.
    public static void Run()
    {
        try
        {
            string data = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanMetadata");
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
        catch (Exception) { }
    }

    public static void RevealExe()
    {
        try { Process.Start("explorer.exe", "/select,\"" + Process.GetCurrentProcess().MainModule.FileName + "\""); }
        catch (Exception) { }
    }
}
// ---------- dialog ----------

// Small modal dialog in the app's own style (the stock MessageBox is light and clashes with the dark GUI).
static class Dlg
{
    const string Xaml = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        WindowStyle='None' ResizeMode='NoResize' SizeToContent='Height' Width='400' Background='#0B0B0D' ShowInTaskbar='False'
        WindowStartupLocation='CenterOwner' FontFamily='Segoe UI Variable Text, Segoe UI' FontSize='13' Foreground='#ECECEF' UseLayoutRounding='True'>
  <Border BorderBrush='#2E2E36' BorderThickness='1' Opacity='0' RenderTransformOrigin='.5,.5' x:Name='Box'>
    <Border.RenderTransform><ScaleTransform x:Name='Sc' ScaleX='.96' ScaleY='.96'/></Border.RenderTransform>
    <StackPanel Margin='24,22,24,20'>
      <TextBlock x:Name='Title' FontSize='16' FontWeight='SemiBold' TextWrapping='Wrap'/>
      <TextBlock x:Name='Msg' TextWrapping='Wrap' Foreground='#A9A9B3' Margin='0,10,0,20' LineHeight='20'/>
      <StackPanel Orientation='Horizontal' HorizontalAlignment='Right'>
        <Button x:Name='CancelBtn' Margin='0,0,8,0'/>
        <Button x:Name='OkBtn'/>
      </StackPanel>
    </StackPanel>
  </Border>
</Window>";

    public static bool Show(Window owner, string title, string message, string ok, string cancel, bool danger)
    {
        var d = (Window)XamlReader.Parse(Xaml);
        d.Owner = owner;
        ((TextBlock)d.FindName("Title")).Text = title;
        ((TextBlock)d.FindName("Msg")).Text = message;
        var okB = (Button)d.FindName("OkBtn");
        var cancelB = (Button)d.FindName("CancelBtn");
        okB.Content = ok;
        okB.Style = (Style)owner.FindResource(danger ? "BtnDanger" : "BtnPrimary");
        if (cancel == null) cancelB.Visibility = Visibility.Collapsed;
        else { cancelB.Content = cancel; cancelB.Style = (Style)owner.FindResource("Btn"); }

        bool result = false;
        okB.Click += delegate { result = true; d.Close(); };
        cancelB.Click += delegate { d.Close(); };
        d.KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) d.Close();
            else if (e.Key == Key.Enter) { result = true; d.Close(); }
        };
        d.MouseLeftButtonDown += delegate { try { d.DragMove(); } catch (InvalidOperationException) { } };
        d.SourceInitialized += delegate
        {
            IntPtr h = new WindowInteropHelper(d).Handle;
            int round = 2, border = 0x00292923;
            Native.DwmSetWindowAttribute(h, 33, ref round, 4);
            Native.DwmSetWindowAttribute(h, 34, ref border, 4);
        };
        d.Loaded += delegate
        {
            var box = (UIElement)d.FindName("Box");
            var sc = (ScaleTransform)d.FindName("Sc");
            A.To(box, UIElement.OpacityProperty, 1, 200);
            A.To(sc, ScaleTransform.ScaleXProperty, 1, 380, A.Spring, 0, .96, null);
            A.To(sc, ScaleTransform.ScaleYProperty, 1, 380, A.Spring, 0, .96, null);
            okB.Focus();
        };
        d.ShowDialog();
        return result;
    }
}
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--uninstall") { Uninstaller.Run(); return 0; }
        try
        {
            var app = new Application();
            app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                MessageBox.Show(e.Exception.Message, "CleanMetadata");
                e.Handled = true;
            };
            var m = new MainWin(args);
            return app.Run(m.Window);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "CleanMetadata");
            return 1;
        }
    }
}