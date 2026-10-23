using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace VpsMonitor
{
    class ToolInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool Required { get; set; }
        public bool Found { get; set; }
        public string Path { get; set; }
        public string Version { get; set; }
        public string Note { get; set; }
    }

    // Detects what the app needs on this PC before the connection form is enabled.
    static class Tools
    {
        static string sshPath;

        public static string SshPath => sshPath ?? FindSsh();

        public static Dictionary<string, object> Detect()
        {
            var ssh = DetectSsh();
            var browser = Ui.FindAppBrowser();
            var tools = new List<ToolInfo>
            {
                ssh,
                new ToolInfo
                {
                    Id = "window", Name = "App window (Microsoft Edge / Chrome)", Required = false,
                    Found = browser != null, Path = browser,
                    Note = browser != null ? null : "Not found - the dashboard will open in your default browser instead.",
                },
            };
            return new Dictionary<string, object>
            {
                ["ok"] = ssh.Found,
                ["tools"] = tools,
                ["dataDir"] = AppPaths.DataDir,
                ["version"] = AppVersion,
            };
        }

        public static string AppVersion
        {
            get
            {
                var v = typeof(Tools).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
                int plus = v.IndexOf('+'); // drop the "+<commit>" suffix the SDK appends
                return plus >= 0 ? v.Substring(0, plus) : v;
            }
        }

        static ToolInfo DetectSsh()
        {
            var t = new ToolInfo { Id = "ssh", Name = "OpenSSH Client (ssh.exe)", Required = true };
            // VPSMON_FAKE_NO_SSH=1 previews the "missing tools" screen.
            var path = Environment.GetEnvironmentVariable("VPSMON_FAKE_NO_SSH") == "1" ? null : FindSsh();
            if (path == null)
            {
                t.Note = "Not installed. VPS Monitor connects through the OpenSSH client that ships with Windows.";
                return t;
            }
            t.Found = true;
            t.Path = path;
            t.Version = ReadVersion(path);
            var m = Regex.Match(t.Version ?? "", @"OpenSSH\S*?_(\d+)\.(\d+)");
            if (m.Success)
            {
                var v = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
                if (v < new Version(8, 4))
                    t.Note = "This OpenSSH is older than 8.4: password and key-passphrase logins may not work " +
                             "(unencrypted keys and the SSH agent will). Update with: winget install Microsoft.OpenSSH.Preview";
            }
            return t;
        }

        static string FindSsh()
        {
            var candidates = new List<string>();
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                if (dir.Trim().Length > 0) candidates.Add(System.IO.Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"')), "ssh.exe"));
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            candidates.Add(System.IO.Path.Combine(win, @"System32\OpenSSH\ssh.exe"));
            candidates.Add(System.IO.Path.Combine(win, @"Sysnative\OpenSSH\ssh.exe"));
            candidates.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"OpenSSH\ssh.exe"));
            candidates.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Git\usr\bin\ssh.exe"));
            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) return sshPath = c; } catch { }
            }
            return sshPath = null;
        }

        static string ReadVersion(string path)
        {
            try
            {
                var psi = new ProcessStartInfo(path, "-V")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardError = true, RedirectStandardOutput = true,
                };
                using (var p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    return err.Trim();
                }
            }
            catch { return null; }
        }
    }
}
