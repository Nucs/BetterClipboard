#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
#:property OutputType=WinExe
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────
// Paste target for end-to-end tests: a window titled "BC-PasteTarget" with a focused multi-line TextBox
// (a real Win32 caret, so flyout-near-caret placement is exercised). Every 300 ms the TextBox content is
// mirrored to the file args[0]; the window closes after args[1] seconds.
// Build once:  dotnet build tools/e2e/PasteTarget.cs   (then run the produced PasteTarget.dll with dotnet)
// Use a UNIQUE output file per run: two live instances writing one file crash each other.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────
var output = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "bc-paste-target.txt");
var lifetime = TimeSpan.FromSeconds(args.Length > 1 ? double.Parse(args[1]) : 60);
// Top-level statements run on an MTA thread; WPF needs STA.
var ui = new Thread(() => Run(output, lifetime));
ui.SetApartmentState(ApartmentState.STA);
ui.Start();
ui.Join();

static void Run(string output, TimeSpan lifetime)
{
var app = new Application();
var box = new TextBox { AcceptsReturn = true, FontSize = 16, Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
var window = new Window
{
    Title = "BC-PasteTarget",
    Width = 640,
    Height = 360,
    Left = 700,
    Top = 300,
    Content = box,
    WindowStartupLocation = WindowStartupLocation.Manual,
};
window.Loaded += (_, _) => { window.Activate(); box.Focus(); };
var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
var started = DateTime.UtcNow;
timer.Tick += (_, _) =>
{
    File.WriteAllText(output, box.Text);
    if (DateTime.UtcNow - started > lifetime)
    {
        window.Close();
    }
};
timer.Start();
app.Run(window);
}
