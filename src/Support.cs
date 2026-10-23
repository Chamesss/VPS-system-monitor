using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace VpsMonitor
{
    static class AppPaths
    {
        // Roaming: saved sessions + known_hosts. Local: the app window's browser profile.
        public static string DataDir => Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VPSMonitor"));
        public static string LocalDir => Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VPSMonitor"));
        public static string SessionsFile => Path.Combine(DataDir, "sessions.json");
        public static string KnownHostsFile => Path.Combine(DataDir, "known_hosts");
        static string InstanceFile => Path.Combine(LocalDir, "instance.url");

        static string Ensure(string dir) { Directory.CreateDirectory(dir); return dir; }

        public static void WriteInstanceUrl(string url) { try { File.WriteAllText(InstanceFile, url); } catch { } }
        public static void ClearInstanceUrl() { try { File.Delete(InstanceFile); } catch { } }
        public static string ReadInstanceUrl()
        {
            try { return File.Exists(InstanceFile) ? File.ReadAllText(InstanceFile).Trim() : null; } catch { return null; }
        }
    }

    // Opens the UI as a chromeless app window (Edge or Chrome), else in the default browser.
    static class Ui
    {
        public static string FindAppBrowser()
        {
            foreach (var exe in new[] { "msedge.exe", "chrome.exe" })
            {
                foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
                {
                    using (var k = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe))
                    {
                        var p = (k?.GetValue(null) as string)?.Trim('"');
                        if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
                    }
                }
            }
            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            {
                var p = Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        public static Process Launch(string url)
        {
            var browser = FindAppBrowser();
            if (browser != null)
            {
                var profile = Path.Combine(AppPaths.LocalDir, "WebView");
                var args = string.Join(" ", new[]
                {
                    "--app=" + url,
                    Cmd.Quote("--user-data-dir=" + profile),
                    "--no-first-run", "--no-default-browser-check", "--disable-sync",
                    "--disable-background-mode", "--hide-crash-restore-bubble",
                    "--disable-features=Translate,msEdgeSidebarV2,msHubApps,msUndersideButton",
                    "--window-size=1400,920",
                });
                try { return Process.Start(new ProcessStartInfo(browser, args) { UseShellExecute = false }); }
                catch { }
            }
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return null;
        }
    }

    static class Cmd
    {
        // Quotes one argument for the MSVCRT/CommandLineToArgvW rules used by ssh.exe and browsers.
        public static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char c in a)
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"') { sb.Append('\\', bs * 2 + 1).Append('"'); bs = 0; continue; }
                sb.Append('\\', bs).Append(c);
                bs = 0;
            }
            sb.Append('\\', bs * 2).Append('"');
            return sb.ToString();
        }

        public static string Join(IEnumerable<string> args)
        {
            var parts = new List<string>();
            foreach (var a in args) parts.Add(Quote(a));
            return string.Join(" ", parts);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint size);

        // 8.3 form of a path (no spaces) when the volume supports it.
        public static string ShortPath(string path)
        {
            var sb = new StringBuilder(1024);
            uint n = GetShortPathName(path, sb, (uint)sb.Capacity);
            return n > 0 && n < sb.Capacity ? sb.ToString() : path;
        }
    }

    // ssh.exe runs this exe (SSH_ASKPASS) to obtain a password or key passphrase.
    static class AskPass
    {
        public const string FlagVar = "VPSMON_ASKPASS";
        public const string SecretVar = "VPSMON_SECRET";

        public static int Run(string[] args)
        {
            string prompt = string.Join(" ", args);
            // Host keys are accepted via StrictHostKeyChecking=accept-new; never confirm anything else.
            string answer = prompt.IndexOf("yes/no", StringComparison.OrdinalIgnoreCase) >= 0
                ? "no"
                : Environment.GetEnvironmentVariable(SecretVar) ?? "";
            using (var o = Console.OpenStandardOutput())
            {
                var b = new UTF8Encoding(false).GetBytes(answer + "\n");
                o.Write(b, 0, b.Length);
            }
            return 0;
        }
    }

    // Kill-on-close job: ssh children die with the app even if it crashes.
    static class Job
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters { public ulong a, b, c, d, e, f; }

        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, ref ExtendedLimits info, uint size);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        static readonly IntPtr handle = Create();

        static IntPtr Create()
        {
            var h = CreateJobObject(IntPtr.Zero, null);
            var info = new ExtendedLimits();
            info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            SetInformationJobObject(h, 9, ref info, (uint)Marshal.SizeOf(typeof(ExtendedLimits)));
            return h;
        }

        public static void Add(Process p)
        {
            try { AssignProcessToJobObject(handle, p.Handle); } catch { }
        }
    }
}
