using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace VpsMonitor
{
    class ConnectResult
    {
        public bool Ok;
        public string Error;
        public string Detail;
        public Dictionary<string, string> Info;
        public Process Process;
    }

    // Owns the single live ssh process: connects, streams snapshots, reconnects on drops.
    class SshMonitor
    {
        public event Action<string, object> Event;

        readonly object gate = new object();
        Process proc;      // live, authenticated stream
        Process pending;   // connection attempt in progress
        int gen;           // bumps on every Connect/Stop so stale callbacks are ignored
        bool wanted;
        Session session;
        string secret;
        string state = "idle", message;
        Dictionary<string, string> info;

        static readonly string remoteCommand = BuildRemoteCommand();

        public Dictionary<string, object> StateObject()
        {
            lock (gate)
            {
                return new Dictionary<string, object>
                {
                    ["state"] = state, ["message"] = message,
                    ["session"] = session?.Public(), ["info"] = info,
                };
            }
        }

        public ConnectResult Connect(Session s, string secretValue)
        {
            Stop();
            int myGen;
            lock (gate) { myGen = ++gen; session = s; }
            SetState("connecting", "Connecting to " + s.Target + "...");

            var r = RunOnce(s, secretValue, myGen);
            lock (gate)
            {
                if (myGen != gen) return new ConnectResult { Error = "Cancelled." };
                if (r.Ok) { wanted = true; secret = secretValue; info = r.Info; }
                else session = null;
            }
            if (r.Ok)
            {
                SetState("live", null);
                Emit("info", r.Info);
                if (!Attach(myGen, r.Process))
                    new Thread(() => ReconnectLoop(myGen)) { IsBackground = true }.Start();
            }
            else SetState("idle", r.Error);
            return r;
        }

        public void Stop()
        {
            Process a, b;
            lock (gate)
            {
                gen++;
                wanted = false;
                a = proc; b = pending;
                proc = pending = null;
                session = null; secret = null; info = null;
            }
            Kill(a);
            Kill(b);
            SetState("idle", null);
        }

        ConnectResult RunOnce(Session s, string secretValue, int myGen)
        {
            var ssh = Tools.SshPath;
            if (ssh == null) return new ConnectResult { Error = "OpenSSH client (ssh.exe) not found." };

            var p = new Process { StartInfo = BuildStartInfo(ssh, s, secretValue), EnableRaisingEvents = true };
            var ready = new ManualResetEvent(false);
            var errLines = new List<string>();
            string remoteErr = null;
            Dictionary<string, string> gotInfo = null, building = null;
            StringBuilder frame = null;

            p.OutputDataReceived += (o, e) =>
            {
                var line = e.Data;
                if (line == null) return;
                if (line == "@@INFO") { building = new Dictionary<string, string>(); return; }
                if (line == "@@END" && building != null) { gotInfo = building; building = null; ready.Set(); return; }
                if (line.StartsWith("@@ERR ")) { remoteErr = line.Substring(6); return; }
                if (line == "@@FRAME") { frame = new StringBuilder(64 * 1024); return; }
                if (line == "@@EOF")
                {
                    if (frame != null && IsCurrent(myGen, p)) Emit("frame", frame.ToString());
                    frame = null;
                    return;
                }
                if (building != null)
                {
                    int i = line.IndexOf('=');
                    if (i > 0) building[line.Substring(0, i)] = line.Substring(i + 1);
                    return;
                }
                frame?.Append(line).Append('\n');
            };
            p.ErrorDataReceived += (o, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data) || e.Data.StartsWith("Warning: Permanently added")) return;
                lock (errLines) { errLines.Add(e.Data); if (errLines.Count > 40) errLines.RemoveAt(0); }
            };
            p.Exited += (o, e) => { ready.Set(); OnExited(myGen, p); };

            try { p.Start(); }
            catch (Exception ex) { return new ConnectResult { Error = "Could not start ssh.exe: " + ex.Message }; }
            Job.Add(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            lock (gate)
            {
                if (myGen != gen) { Kill(p); return new ConnectResult { Error = "Cancelled." }; }
                pending = p;
            }

            bool signaled = ready.WaitOne(45000);
            if (gotInfo != null && !p.HasExited)
            {
                lock (gate) { if (pending == p) pending = null; }
                return new ConnectResult { Ok = true, Info = gotInfo, Process = p };
            }

            Kill(p);
            if (p.WaitForExit(3000)) p.WaitForExit(); // the no-arg overload also waits for stderr to drain
            lock (gate) { if (pending == p) pending = null; }
            string stderr;
            lock (errLines) stderr = string.Join("\n", errLines);
            return new ConnectResult { Error = Explain(remoteErr, stderr, signaled), Detail = stderr };
        }

        bool IsCurrent(int myGen, Process p)
        {
            lock (gate) return myGen == gen && (proc == p || pending == p || proc == null);
        }

        void OnExited(int myGen, Process p)
        {
            lock (gate)
            {
                if (myGen != gen || proc != p) return;
                proc = null;
                if (!wanted) return;
            }
            SetState("reconnecting", "Connection lost - reconnecting...");
            new Thread(() => ReconnectLoop(myGen)) { IsBackground = true }.Start();
        }

        void ReconnectLoop(int myGen)
        {
            int delay = 2000;
            for (int attempt = 1; ; attempt++)
            {
                Thread.Sleep(delay);
                Session s; string sec;
                lock (gate) { if (myGen != gen || !wanted) return; s = session; sec = secret; }

                SetState("reconnecting", "Reconnecting (attempt " + attempt + ")...");
                var r = RunOnce(s, sec, myGen);
                if (r.Ok)
                {
                    lock (gate) { if (myGen != gen) { Kill(r.Process); return; } info = r.Info; }
                    SetState("live", null);
                    Emit("info", r.Info);
                    if (Attach(myGen, r.Process)) return;
                    continue;
                }
                SetState("reconnecting", r.Error + " Retrying...");
                delay = Math.Min(delay * 2, 30000);
            }
        }

        // Makes p the live process. Returns false if it already died (caller keeps retrying).
        bool Attach(int myGen, Process p)
        {
            lock (gate)
            {
                if (myGen != gen) { Kill(p); return true; }
                proc = p;
                if (!p.HasExited) return true;
                proc = null;
            }
            SetState("reconnecting", "Connection lost - reconnecting...");
            return false;
        }

        static ProcessStartInfo BuildStartInfo(string ssh, Session s, string secretValue)
        {
            var known = AppPaths.KnownHostsFile.Replace('\\', '/');
            var args = new List<string>
            {
                "-T", "-n", "-C",
                "-o", "ConnectTimeout=15",
                "-o", "ServerAliveInterval=10",
                "-o", "ServerAliveCountMax=3",
                // New hosts are trusted on first use; changed keys are rejected.
                "-o", "StrictHostKeyChecking=accept-new",
                "-o", "UserKnownHostsFile=\"" + known + "\" ~/.ssh/known_hosts",
            };
            if (s.Port != 22) { args.Add("-p"); args.Add(s.Port.ToString()); }
            switch (s.Auth)
            {
                case "password":
                    args.AddRange(new[]
                    {
                        "-o", "PreferredAuthentications=password,keyboard-interactive",
                        "-o", "PubkeyAuthentication=no",
                        "-o", "NumberOfPasswordPrompts=1",
                    });
                    break;
                case "key":
                    args.AddRange(new[]
                    {
                        "-i", s.KeyPath,
                        "-o", "IdentitiesOnly=yes",
                        "-o", "PreferredAuthentications=publickey",
                        "-o", "NumberOfPasswordPrompts=1",
                    });
                    break;
                default: // agent / default keys / ~/.ssh/config
                    args.AddRange(new[] { "-o", "BatchMode=yes" });
                    break;
            }
            args.Add(s.Target);
            args.Add(remoteCommand);

            var psi = new ProcessStartInfo(ssh, Cmd.Join(args))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["SSH_ASKPASS"] = Cmd.ShortPath(Application.ExecutablePath);
            psi.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "force";
            if (string.IsNullOrEmpty(psi.EnvironmentVariables["DISPLAY"])) psi.EnvironmentVariables["DISPLAY"] = "localhost:0";
            psi.EnvironmentVariables[AskPass.FlagVar] = "1";
            psi.EnvironmentVariables[AskPass.SecretVar] = secretValue ?? "";
            return psi;
        }

        // The remote script travels base64-encoded so no shell quoting can mangle it.
        static string BuildRemoteCommand()
        {
            using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("remote.sh"))
            using (var rd = new StreamReader(st))
            {
                var script = rd.ReadToEnd().Replace("\r\n", "\n");
                var b64 = Convert.ToBase64String(new UTF8Encoding(false).GetBytes(script));
                return "echo " + b64 + "|base64 -d|sh";
            }
        }

        static string Explain(string remoteErr, string stderr, bool exited)
        {
            if (!string.IsNullOrEmpty(remoteErr)) return remoteErr;
            if (!exited) return "Timed out waiting for the server to answer.";
            string s = stderr ?? "";
            bool Has(string x) => s.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0;
            if (Has("REMOTE HOST IDENTIFICATION HAS CHANGED") || Has("Host key verification failed"))
                return "The server's host key does not match the one saved earlier. If the server was reinstalled, " +
                       "remove its line from " + AppPaths.KnownHostsFile + " (or ~/.ssh/known_hosts) and try again.";
            if (Has("UNPROTECTED PRIVATE KEY FILE") || Has("bad permissions"))
                return "Windows OpenSSH refused the key because other users can read it. Move it into your own " +
                       "user folder (for example %USERPROFILE%\\.ssh) and try again.";
            if (Has("incorrect passphrase") || Has("bad passphrase"))
                return "Wrong passphrase for the private key.";
            if (Has("invalid format") || Has("Load key"))
                return "The selected file is not a usable private key.";
            if (Has("Bad owner or permissions"))
                return "OpenSSH refused your ~/.ssh/config because of its file permissions.";
            if (Has("Permission denied"))
                return "Authentication failed - check the username and password / key.";
            if (Has("Could not resolve hostname"))
                return "Host not found - check the address.";
            if (Has("Connection refused"))
                return "Connection refused - is SSH running on that port?";
            if (Has("timed out") || Has("Network is unreachable") || Has("No route to host"))
                return "Could not reach the server (timed out). Check the address, port and firewall.";
            if (Has("base64: not found") || Has("base64: command not found"))
                return "The server is missing the 'base64' command (coreutils).";
            var lines = s.Split('\n');
            var last = lines.Length > 0 ? lines[lines.Length - 1].Trim() : "";
            return last.Length > 0 ? "SSH failed: " + last : "SSH closed the connection unexpectedly.";
        }

        void SetState(string st, string msg)
        {
            lock (gate) { state = st; message = msg; }
            Emit("status", StateObject());
        }

        void Emit(string name, object payload)
        {
            try { Event?.Invoke(name, payload); } catch { }
        }

        static void Kill(Process p)
        {
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(); } catch { }
        }
    }
}
