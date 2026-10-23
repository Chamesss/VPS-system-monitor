using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace VpsMonitor
{
    static class Program
    {
        public const string AppName = "VPS Monitor";

        static Form invoker;
        static readonly System.Collections.Generic.List<Process> windows = new System.Collections.Generic.List<Process>();

        [STAThread]
        static int Main(string[] args)
        {
            // ssh.exe re-launches this exe as its askpass helper.
            if (Environment.GetEnvironmentVariable(AskPass.FlagVar) == "1")
                return AskPass.Run(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Fatal(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Fatal(e.ExceptionObject as Exception);

            using (var mutex = new Mutex(true, @"Local\VPSMonitor.SingleInstance", out bool first))
            {
                if (!first)
                {
                    // Already running: just bring up another window on the existing instance.
                    string url = AppPaths.ReadInstanceUrl();
                    if (url != null) Ui.Launch(url);
                    return 0;
                }

                invoker = new Form();
                var _ = invoker.Handle; // bind the marshalling control to this (STA) thread

                var monitor = new SshMonitor();
                var server = new WebServer(monitor);
                server.Start();
                AppPaths.WriteInstanceUrl(server.Url);

                using (var tray = CreateTray(server))
                {
                    WatchBrowser(Ui.Launch(server.Url));
                    Application.Run();
                    tray.Visible = false;
                }

                monitor.Stop();
                server.Stop();
                CloseWindows();
                AppPaths.ClearInstanceUrl();
            }
            return 0;
        }

        // When the app window is closed, the app exits (and its ssh session with it).
        static void WatchBrowser(Process browser)
        {
            if (browser == null) return;
            lock (windows) windows.Add(browser);
            var started = DateTime.UtcNow;
            browser.EnableRaisingEvents = true;
            browser.Exited += (s, e) =>
            {
                // A browser that exits right away handed the window to an existing process;
                // keep running in the tray in that case.
                if ((DateTime.UtcNow - started).TotalSeconds < 5) return;
                Quit();
            };
        }

        static void CloseWindows()
        {
            lock (windows)
            {
                foreach (var w in windows)
                {
                    try
                    {
                        if (w.HasExited) continue;
                        w.CloseMainWindow();
                        if (!w.WaitForExit(2000)) w.Kill();
                    }
                    catch { }
                }
            }
        }

        static NotifyIcon CreateTray(WebServer server)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open window", null, (s, e) => WatchBrowser(Ui.Launch(server.Url)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => Quit());
            var tray = new NotifyIcon
            {
                Text = AppName,
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
                ContextMenuStrip = menu,
                Visible = true,
            };
            tray.DoubleClick += (s, e) => WatchBrowser(Ui.Launch(server.Url));
            return tray;
        }

        public static void Quit()
        {
            if (invoker.InvokeRequired) invoker.BeginInvoke(new Action(Application.Exit));
            else Application.Exit();
        }

        public static T OnUiThread<T>(Func<T> f)
        {
            return invoker.InvokeRequired ? (T)invoker.Invoke(f) : f();
        }

        static void Fatal(Exception ex)
        {
            MessageBox.Show(ex?.ToString() ?? "Unknown error", AppName + " - unexpected error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
