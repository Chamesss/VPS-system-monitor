using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace VpsMonitor
{
    // Local-only HTTP server for the UI. Every API call must carry the per-launch token,
    // so other websites and local programs cannot drive the app.
    class WebServer
    {
        readonly HttpListener listener = new HttpListener();
        readonly SshMonitor monitor;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        readonly List<EventClient> clients = new List<EventClient>();
        readonly byte[] page;
        System.Threading.Timer keepAlive;

        public int Port { get; private set; }
        public string Token { get; private set; }
        public string Url => "http://localhost:" + Port + "/?t=" + Token;

        public WebServer(SshMonitor monitor)
        {
            this.monitor = monitor;
            using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("ui.html"))
            using (var ms = new MemoryStream()) { st.CopyTo(ms); page = ms.ToArray(); }

            monitor.Event += (name, payload) =>
                Broadcast(name, payload is string s ? json.Serialize(s) : json.Serialize(payload));
        }

        public void Start()
        {
            var bytes = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            Token = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

            for (int attempt = 0; ; attempt++)
            {
                Port = FreePort();
                listener.Prefixes.Clear();
                listener.Prefixes.Add("http://localhost:" + Port + "/");
                try { listener.Start(); break; }
                catch (HttpListenerException) when (attempt < 5) { }
            }
            new Thread(Loop) { IsBackground = true, Name = "http" }.Start();
            keepAlive = new System.Threading.Timer(_ => Broadcast(null, null), null, 15000, 15000);
        }

        public void Stop()
        {
            keepAlive?.Dispose();
            try { listener.Stop(); } catch { }
        }

        static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        void Loop()
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); } catch { return; }
                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                var path = req.Url.AbsolutePath;

                if (path == "/" || path == "/index.html")
                {
                    ctx.Response.Headers["Cache-Control"] = "no-store";
                    ctx.Response.Headers["Content-Security-Policy"] =
                        "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self' data:; connect-src 'self'";
                    Reply(ctx, 200, "text/html; charset=utf-8", page);
                    return;
                }
                if (!path.StartsWith("/api/")) { Reply(ctx, 404, "text/plain", Encoding.UTF8.GetBytes("Not found")); return; }

                var token = req.Headers["X-Token"] ?? req.QueryString["t"];
                var origin = req.Headers["Origin"];
                if (token != Token || (origin != null && origin != "http://localhost:" + Port))
                {
                    Reply(ctx, 403, "text/plain", Encoding.UTF8.GetBytes("Forbidden"));
                    return;
                }

                switch (path)
                {
                    case "/api/events": OpenEvents(ctx); return;
                    case "/api/env": Json(ctx, Tools.Detect()); return;
                    case "/api/state": Json(ctx, monitor.StateObject()); return;
                    case "/api/sessions": Json(ctx, ListSessions()); return;
                }

                if (req.HttpMethod != "POST") { Reply(ctx, 405, "text/plain", Encoding.UTF8.GetBytes("POST only")); return; }
                var body = ReadBody(req);
                switch (path)
                {
                    case "/api/connect": Json(ctx, Connect(body)); return;
                    case "/api/disconnect": monitor.Stop(); Json(ctx, Ok()); return;
                    case "/api/sessions/delete": Store.Delete(Str(body, "id")); Json(ctx, Ok()); return;
                    case "/api/browse-key": Json(ctx, new Dictionary<string, object> { ["path"] = Program.OnUiThread(BrowseKey) }); return;
                    case "/api/open-features":
                        Process.Start(new ProcessStartInfo("ms-settings:optionalfeatures") { UseShellExecute = true });
                        Json(ctx, Ok());
                        return;
                    case "/api/quit": Json(ctx, Ok()); Program.Quit(); return;
                }
                Reply(ctx, 404, "text/plain", Encoding.UTF8.GetBytes("Not found"));
            }
            catch (Exception ex)
            {
                try { Json(ctx, new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.Message }, 500); } catch { }
            }
        }

        object ListSessions()
        {
            var list = new List<object>();
            foreach (var s in Store.All()) list.Add(s.Public());
            return list;
        }

        Dictionary<string, object> Connect(Dictionary<string, object> d)
        {
            string id = Str(d, "id");
            string secret = Str(d, "secret");
            bool remember = !d.ContainsKey("remember") || d["remember"] is bool b && b;
            Session s;

            if (!string.IsNullOrEmpty(id))
            {
                s = Store.Get(id);
                if (s == null) return Fail("That saved session no longer exists.");
                if (string.IsNullOrEmpty(secret))
                {
                    secret = Store.Unprotect(s.Secret);
                    remember = s.Secret != null && secret != null;
                    if (secret == null && s.Auth == "password")
                        return new Dictionary<string, object> { ["ok"] = false, ["needSecret"] = true, ["session"] = s.Public() };
                }
            }
            else
            {
                s = new Session
                {
                    Name = Str(d, "name"),
                    Host = Str(d, "host"),
                    User = Str(d, "user"),
                    Auth = Str(d, "auth") ?? "password",
                    KeyPath = Str(d, "keyPath"),
                };
                int.TryParse(Str(d, "port") ?? "22", out int port);
                s.Port = port == 0 ? 22 : port;
            }

            var problem = Validate(s);
            if (problem != null) return Fail(problem);
            if (s.Auth == "password" && string.IsNullOrEmpty(secret)) return Fail("Enter the password.");
            if (string.IsNullOrEmpty(s.Name)) s.Name = s.Target;

            var r = monitor.Connect(s, secret);
            if (!r.Ok) return new Dictionary<string, object> { ["ok"] = false, ["error"] = r.Error, ["detail"] = r.Detail };

            // Saved only after a successful login.
            s.Secret = remember && !string.IsNullOrEmpty(secret) ? Store.Protect(secret) : null;
            s.LastUsed = DateTime.UtcNow.ToString("o");
            s = Store.Upsert(s);
            return new Dictionary<string, object> { ["ok"] = true, ["session"] = s.Public(), ["info"] = r.Info };
        }

        static string Validate(Session s)
        {
            if (string.IsNullOrWhiteSpace(s.Host)) return "Enter the server address.";
            s.Host = s.Host.Trim();
            s.User = (s.User ?? "").Trim();
            if (s.Host.StartsWith("-") || !System.Text.RegularExpressions.Regex.IsMatch(s.Host, @"^[A-Za-z0-9.\-_:\[\]%]+$"))
                return "The server address contains invalid characters.";
            if (s.User.StartsWith("-") || !System.Text.RegularExpressions.Regex.IsMatch(s.User, @"^[A-Za-z0-9.\-_\\$]*$"))
                return "The username contains invalid characters.";
            if (s.Port < 1 || s.Port > 65535) return "Port must be between 1 and 65535.";
            if (s.Auth != "password" && s.Auth != "key" && s.Auth != "agent") return "Unknown authentication method.";
            if (s.Auth == "key")
            {
                if (string.IsNullOrWhiteSpace(s.KeyPath)) return "Choose a private key file.";
                s.KeyPath = Environment.ExpandEnvironmentVariables(s.KeyPath.Trim().Trim('"'));
                if (!File.Exists(s.KeyPath)) return "Private key file not found: " + s.KeyPath;
            }
            else s.KeyPath = null;
            return null;
        }

        static string BrowseKey()
        {
            using (var owner = new Form
            {
                TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(1, 1),
            })
            using (var dlg = new OpenFileDialog
            {
                Title = "Select your SSH private key",
                Filter = "Private keys|*.*",
                CheckFileExists = true,
            })
            {
                var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
                if (Directory.Exists(sshDir)) dlg.InitialDirectory = sshDir;
                owner.Show();
                owner.Activate();
                return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.FileName : null;
            }
        }

        // ---- Server-sent events -------------------------------------------------------

        class EventClient
        {
            public HttpListenerResponse Res;
            public bool Alive = true;

            public void Send(byte[] data)
            {
                lock (this)
                {
                    if (!Alive) return;
                    try { Res.OutputStream.Write(data, 0, data.Length); Res.OutputStream.Flush(); }
                    catch { Alive = false; try { Res.Abort(); } catch { } }
                }
            }
        }

        void OpenEvents(HttpListenerContext ctx)
        {
            var res = ctx.Response;
            res.ContentType = "text/event-stream";
            res.Headers["Cache-Control"] = "no-store";
            res.SendChunked = true;
            var c = new EventClient { Res = res };
            c.Send(Encode("status", json.Serialize(monitor.StateObject())));
            lock (clients) clients.Add(c);
        }

        static byte[] Encode(string name, string data) =>
            Encoding.UTF8.GetBytes(name == null ? ": ping\n\n" : "event: " + name + "\ndata: " + data + "\n\n");

        void Broadcast(string name, string data)
        {
            var bytes = Encode(name, data);
            EventClient[] list;
            lock (clients) { clients.RemoveAll(c => !c.Alive); list = clients.ToArray(); }
            foreach (var c in list) c.Send(bytes);
        }

        // ---- helpers ------------------------------------------------------------------

        Dictionary<string, object> ReadBody(HttpListenerRequest req)
        {
            using (var rd = new StreamReader(req.InputStream, Encoding.UTF8))
            {
                var text = rd.ReadToEnd();
                if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, object>();
                return json.Deserialize<Dictionary<string, object>>(text) ?? new Dictionary<string, object>();
            }
        }

        static string Str(Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && v != null ? Convert.ToString(v) : null;

        static Dictionary<string, object> Ok() => new Dictionary<string, object> { ["ok"] = true };
        static Dictionary<string, object> Fail(string msg) => new Dictionary<string, object> { ["ok"] = false, ["error"] = msg };

        void Json(HttpListenerContext ctx, object o, int code = 200) =>
            Reply(ctx, code, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json.Serialize(o)));

        static void Reply(HttpListenerContext ctx, int code, string type, byte[] body)
        {
            var res = ctx.Response;
            res.StatusCode = code;
            res.ContentType = type;
            res.ContentLength64 = body.Length;
            res.OutputStream.Write(body, 0, body.Length);
            res.Close();
        }
    }
}
