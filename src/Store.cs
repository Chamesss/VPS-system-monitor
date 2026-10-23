using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace VpsMonitor
{
    class Session
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Host { get; set; }
        public int Port { get; set; } = 22;
        public string User { get; set; }
        public string Auth { get; set; } // password | key | agent
        public string KeyPath { get; set; }
        public string Secret { get; set; } // DPAPI-protected password / passphrase, base64
        public string Created { get; set; }
        public string LastUsed { get; set; }

        public string Target => string.IsNullOrEmpty(User) ? Host : User + "@" + Host;

        public Dictionary<string, object> Public() => new Dictionary<string, object>
        {
            ["id"] = Id, ["name"] = Name, ["host"] = Host, ["port"] = Port, ["user"] = User,
            ["auth"] = Auth, ["keyPath"] = KeyPath, ["hasSecret"] = !string.IsNullOrEmpty(Secret),
            ["lastUsed"] = LastUsed, ["target"] = Target,
        };
    }

    // Sessions live in %APPDATA%\VPSMonitor\sessions.json. Secrets are encrypted with
    // Windows DPAPI, so only this Windows account on this PC can read them.
    static class Store
    {
        static readonly object gate = new object();
        static readonly JavaScriptSerializer json = new JavaScriptSerializer();
        static readonly byte[] entropy = Encoding.UTF8.GetBytes("VPSMonitor.session.v1");

        public static List<Session> All()
        {
            lock (gate)
            {
                try
                {
                    if (!File.Exists(AppPaths.SessionsFile)) return new List<Session>();
                    return json.Deserialize<List<Session>>(File.ReadAllText(AppPaths.SessionsFile)) ?? new List<Session>();
                }
                catch { return new List<Session>(); }
            }
        }

        public static Session Get(string id) => All().FirstOrDefault(s => s.Id == id);

        // Inserts or updates; a session with the same destination and auth method is replaced.
        public static Session Upsert(Session s)
        {
            lock (gate)
            {
                var list = All();
                var existing = list.FirstOrDefault(x => x.Id == s.Id)
                    ?? list.FirstOrDefault(x => Same(x.Host, s.Host) && x.Port == s.Port && Same(x.User, s.User) && x.Auth == s.Auth);
                if (existing != null)
                {
                    s.Id = existing.Id;
                    s.Created = existing.Created;
                    list.Remove(existing);
                }
                if (string.IsNullOrEmpty(s.Id)) s.Id = Guid.NewGuid().ToString("N").Substring(0, 12);
                if (string.IsNullOrEmpty(s.Created)) s.Created = DateTime.UtcNow.ToString("o");
                list.Insert(0, s);
                Save(list);
                return s;
            }
        }

        public static void Delete(string id)
        {
            lock (gate)
            {
                var list = All();
                list.RemoveAll(s => s.Id == id);
                Save(list);
            }
        }

        static bool Same(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

        static void Save(List<Session> list)
        {
            var tmp = AppPaths.SessionsFile + ".tmp";
            File.WriteAllText(tmp, json.Serialize(list), new UTF8Encoding(false));
            if (File.Exists(AppPaths.SessionsFile)) File.Replace(tmp, AppPaths.SessionsFile, null);
            else File.Move(tmp, AppPaths.SessionsFile);
        }

        public static string Protect(string secret)
        {
            if (string.IsNullOrEmpty(secret)) return null;
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(data);
        }

        public static string Unprotect(string blob)
        {
            if (string.IsNullOrEmpty(blob)) return null;
            try
            {
                var data = ProtectedData.Unprotect(Convert.FromBase64String(blob), entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch { return null; }
        }
    }
}
