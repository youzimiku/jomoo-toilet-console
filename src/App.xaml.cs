using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace JomooRemote
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            string shot = null;
            foreach (var a in e.Args)
                if (a.StartsWith("--shot=", StringComparison.OrdinalIgnoreCase))
                    shot = a.Substring(7).Trim('"');

            string stateArg = null;
            foreach (var a in e.Args)
                if (a.StartsWith("--demo=", StringComparison.OrdinalIgnoreCase))
                    stateArg = a.Substring(7);

            var w = new MainWindow();
            MainWindow = w;

            foreach (var a in e.Args)
                if (a.Equals("--selftest=reconnect", StringComparison.OrdinalIgnoreCase))
                {
                    w.SelfTestReconnect = true;
                    w.WindowStartupLocation = WindowStartupLocation.Manual;
                    w.Left = -4000;
                    w.Top = 0;
                }

            if (shot != null)
            {
                w.NoConnect = true;
                int demo = 0;
                int.TryParse(stateArg, out demo);
                w.DemoState = demo;
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Left = -4000;
                w.Top = 0;
            }

            w.Show();

            if (shot != null) Capture(w, shot);

            DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show(args.Exception.Message, "九牧智控中心 · 异常");
                args.Handled = true;
            };
        }

        void Capture(Window w, string path)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1500) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                try
                {
                    w.UpdateLayout();
                    var dpi = VisualTreeHelper.GetDpi(w);
                    int px = (int)Math.Round(w.ActualWidth * dpi.DpiScaleX);
                    int py = (int)Math.Round(w.ActualHeight * dpi.DpiScaleY);

                    var rtb = new RenderTargetBitmap(px, py, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                    rtb.Render(w);

                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));

                    var full = Path.GetFullPath(path);
                    var dir = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    using (var fs = File.Create(full)) enc.Save(fs);

                    Shutdown(0);
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(path + ".err.txt", ex.ToString()); } catch { }
                    Shutdown(3);
                }
            };
            timer.Start();
        }
    }
}
