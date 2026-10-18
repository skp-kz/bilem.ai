using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Updates
{
    /// <summary>Релиз на GitHub: версия, заметки, ссылки на MSI и SHA256SUMS(.sig).</summary>
    public sealed class ReleaseInfo
    {
        public SemVer Version { get; set; }
        public string Tag { get; set; }
        public string Notes { get; set; }
        public bool Prerelease { get; set; }
        public string HtmlUrl { get; set; }
        public string MsiName { get; set; }
        public string MsiUrl { get; set; }
        public string SumsUrl { get; set; }
        public string SigUrl { get; set; }
    }

    /// <summary>
    /// Обновления Bilem Agent из GitHub Releases (публичный репозиторий, без токена): проверка (ETag, раз в сутки), скачивание MSI
    /// и SHA256SUMS(.sig) в ProgramData\Bilem\updates, проверка подписи Ed25519 и хеша, установка через отдельный помощник
    /// (агент закрывается, msiexec ставит, агент запускается снова), откат на предыдущий сохранённый MSI.
    /// </summary>
    public static class Updater
    {
        public const string Repo = "skp-kz/bilem.ai";
        public const string SumsName = "SHA256SUMS", SigName = "SHA256SUMS.sig";
        public static string Dir => Path.Combine(Paths.Home, "updates");
        private static string StateFile => Path.Combine(Dir, "state.json");
        private static string PendingFile => Path.Combine(Dir, "pending.json");

        /// <summary>HTTP GET: (адрес, ETag) → (код, тело, ETag). Подменяется в тестах.</summary>
        public static Func<string, string, (int Code, byte[] Body, string ETag)> Http { get; set; } = Get;

        private sealed class State
        {
            public string ETag { get; set; }
            public string Releases { get; set; }
            public DateTime? LastCheck { get; set; }
        }

        private static State Load()
        {
            try { return File.Exists(StateFile) ? JsonConvert.DeserializeObject<State>(File.ReadAllText(StateFile)) ?? new State() : new State(); }
            catch { return new State(); }
        }

        private static void Save(State s)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(StateFile, JsonConvert.SerializeObject(s, Formatting.Indented));
        }

        public static DateTime? LastCheck => Load().LastCheck;

        /// <summary>Список релизов (кэш по ETag: не изменился — GitHub отвечает 304 и не тратит лимит).</summary>
        public static List<ReleaseInfo> Releases()
        {
            var st = Load();
            var (code, body, etag) = Http($"https://api.github.com/repos/{Repo}/releases?per_page=30", st.ETag);
            string json;
            if (code == 304 && st.Releases != null) json = st.Releases;
            else if (code == 200) { json = Encoding.UTF8.GetString(body); st.ETag = etag; st.Releases = json; }
            else if (code == 404) throw new AgentException(Txt.S("UpdNoReleases"));
            else throw new AgentException(Txt.F("UpdHttpError", code));
            st.LastCheck = DateTime.Now;
            Save(st);
            return Parse(json);
        }

        internal static List<ReleaseInfo> Parse(string json)
        {
            var list = new List<ReleaseInfo>();
            foreach (JObject r in JArray.Parse(json))
            {
                if ((bool?)r["draft"] == true || !SemVer.TryParse((string)r["tag_name"], out var v)) continue;
                var assets = ((JArray)r["assets"] ?? new JArray()).OfType<JObject>().ToList();
                string Url(Func<string, bool> f) => (string)assets.FirstOrDefault(a => f((string)a["name"] ?? ""))?["browser_download_url"];
                var msi = assets.FirstOrDefault(a => ((string)a["name"] ?? "").EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
                list.Add(new ReleaseInfo
                {
                    Version = v, Tag = (string)r["tag_name"], Notes = (string)r["body"] ?? "", Prerelease = (bool?)r["prerelease"] == true || v.IsPrerelease,
                    HtmlUrl = (string)r["html_url"], MsiName = (string)msi?["name"], MsiUrl = (string)msi?["browser_download_url"],
                    SumsUrl = Url(n => n == SumsName), SigUrl = Url(n => n == SigName),
                });
            }
            return list.OrderByDescending(x => x.Version).ToList();
        }

        /// <summary>Самый новый подходящий релиз: бета — только с галочкой «Получать бета-версии»; без MSI и подписи — не предлагаем.</summary>
        public static ReleaseInfo Latest(IEnumerable<ReleaseInfo> all, bool beta) =>
            all.Where(r => (beta || !r.Prerelease) && r.MsiUrl != null && r.SumsUrl != null && r.SigUrl != null).OrderByDescending(r => r.Version).FirstOrDefault();

        /// <summary>Проверка: есть ли версия новее текущей. Сеть недоступна — исключение (окно пишет в журнал тихо).</summary>
        public static ReleaseInfo CheckNewer(bool beta, SemVer current = null)
        {
            var latest = Latest(Releases(), beta);
            current = current ?? SemVer.Current;
            return latest != null && latest.Version > current ? latest : null;
        }

        /// <summary>Скачать MSI релиза и проверить (подпись Ed25519, хеш, Authenticode). Не сошлось — файлы удаляются, отказ.</summary>
        /// <summary>Ход скачивания MSI для полосы в окне: (получено, всего; -1 — размер неизвестен). Только на потоке скачивания.</summary>
        [ThreadStatic] private static Action<long, long> _progress;

        public static string Download(ReleaseInfo r, Action<long, long> progress = null)
        {
            if (r?.MsiUrl == null || r.SumsUrl == null || r.SigUrl == null) throw new AgentException(Txt.S("UpdNoAssets"));
            var dir = Path.Combine(Dir, r.Version.ToString());
            Directory.CreateDirectory(dir);
            Secrets.RestrictDirToAdmins(Dir);
            var msi = Path.Combine(dir, r.MsiName);
            byte[] Fetch(string url)
            {
                var (code, body, _) = Http(url, null);
                if (code != 200) throw new AgentException(Txt.F("UpdHttpError", code));
                return body;
            }
            var sums = Fetch(r.SumsUrl);
            var sig = Fetch(r.SigUrl);
            _progress = progress;
            try { File.WriteAllBytes(msi, Fetch(r.MsiUrl)); }
            finally { _progress = null; }
            var why = ReleaseVerifier.Check(msi, sums, sig);
            if (why != null)
            {
                try { Directory.Delete(dir, true); } catch { }
                Log.Fail(Txt.F("UpdRejected", r.Version, why));
                throw new AgentException(Txt.F("UpdRejected", r.Version, why));
            }
            File.WriteAllBytes(Path.Combine(dir, SumsName), sums);
            File.WriteAllBytes(Path.Combine(dir, SigName), sig);
            Log.Ok(Txt.F("UpdDownloaded", r.Version));
            return msi;
        }

        /// <summary>Для отката: MSI текущей версии — скачать с GitHub, если ещё не сохранён (первая установка была вручную).</summary>
        public static void EnsureRollback(SemVer current, IEnumerable<ReleaseInfo> all)
        {
            if (Stored().Any(s => s.Version.Equals(current))) return;
            var r = all.FirstOrDefault(x => x.Version.Equals(current));
            if (r == null) { Log.Info(Txt.F("UpdNoRollbackRelease", current)); return; }
            try { Download(r); } catch (Exception e) { Log.Warn(e.Message); }
        }

        /// <summary>Сохранённые проверенные MSI (для отката): версия → путь. Перед использованием проверяются снова.</summary>
        public static List<(SemVer Version, string Msi)> Stored()
        {
            var res = new List<(SemVer, string)>();
            if (!Directory.Exists(Dir)) return res;
            foreach (var d in Directory.GetDirectories(Dir))
            {
                if (!SemVer.TryParse(Path.GetFileName(d), out var v)) continue;
                var msi = Directory.GetFiles(d, "*.msi").FirstOrDefault();
                if (msi != null && File.Exists(Path.Combine(d, SumsName)) && File.Exists(Path.Combine(d, SigName))) res.Add((v, msi));
            }
            return res.OrderByDescending(x => x.Item1).ToList();
        }

        /// <summary>Повторная проверка сохранённого MSI перед установкой (файл мог подменить кто-то с правами на папку).</summary>
        public static string Recheck(string msi)
        {
            var d = Path.GetDirectoryName(msi);
            return ReleaseVerifier.Check(msi, File.ReadAllBytes(Path.Combine(d, SumsName)), File.ReadAllBytes(Path.Combine(d, SigName)));
        }

        /// <summary>Оставить текущую версию, предыдущую (для отката) и новую; остальное удалить.</summary>
        public static void Prune(SemVer keepA, SemVer keepB)
        {
            foreach (var (v, msi) in Stored())
                if (!v.Equals(keepA) && !v.Equals(keepB))
                    try { Directory.Delete(Path.GetDirectoryName(msi), true); } catch { }
        }

        private sealed class Pending { public string From { get; set; } public string To { get; set; } public string Msi { get; set; } public string Log { get; set; } public bool Rollback { get; set; } }

        /// <summary>
        /// Поставить MSI: помощник (cmd) ждёт, пока агент закроется, запускает msiexec без окон, затем запускает агент снова.
        /// После вызова агент должен сразу закрыться.
        /// </summary>
        public static void Install(string msi, SemVer from, SemVer to, string agentExe, bool rollback = false)
        {
            var why = Recheck(msi);
            if (why != null) throw new AgentException(Txt.F("UpdRejected", to, why));
            Directory.CreateDirectory(Dir);
            var log = Path.Combine(Dir, $"msiexec-{to}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(PendingFile, JsonConvert.SerializeObject(new Pending { From = from.ToString(), To = to.ToString(), Msi = msi, Log = log, Rollback = rollback }));
            var pid = Process.GetCurrentProcess().Id;
            var cmd = Path.Combine(Dir, "install.cmd");
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine(":wait");
            sb.AppendLine($"tasklist /fi \"PID eq {pid}\" | find \"{pid}\" >nul && (ping -n 2 127.0.0.1 >nul & goto wait)");
            sb.AppendLine($"msiexec /i \"{msi}\" /qn /norestart /l*v \"{log}\"");
            // Перенаправление — впереди: «echo 0> файл» cmd читает как перенаправление потока 0
            sb.AppendLine($">\"{Path.Combine(Dir, "install.rc")}\" echo %errorlevel%");
            sb.AppendLine($"start \"\" \"{agentExe}\"");
            File.WriteAllText(cmd, sb.ToString(), new UTF8Encoding(false));
            Log.Info(Txt.F(rollback ? "UpdRollbackStarting" : "UpdInstallStarting", from, to));
            // Через оболочку: дочерний процесс не наследует дескрипторы агента (сокет MCP, файлы журнала)
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + cmd + "\"") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
        }

        /// <summary>При запуске агента: была установка — записать итог в журнал и аудит («Обновлено с X на Y»).</summary>
        public static void CompleteIfPending()
        {
            try
            {
                if (!File.Exists(PendingFile)) return;
                var p = JsonConvert.DeserializeObject<Pending>(File.ReadAllText(PendingFile));
                File.Delete(PendingFile);
                var rcFile = Path.Combine(Dir, "install.rc");
                var rc = File.Exists(rcFile) ? File.ReadAllText(rcFile).Trim() : "?";
                var now = SemVer.Current.ToString();
                if (now == p.To)
                {
                    var title = Txt.F(p.Rollback ? "UpdRolledBack" : "UpdUpdated", p.From, p.To);
                    Log.Ok(title);
                    Audit.Write(Audit.Update, null, "agent", title, p.Msi, File.Exists(p.Msi) ? File.ReadAllBytes(p.Msi) : null);
                    if (SemVer.TryParse(p.From, out var f) && SemVer.TryParse(p.To, out var t)) Prune(f, t);
                }
                else Log.Fail(Txt.F("UpdInstallFailed", p.To, rc, p.Log));
            }
            catch (Exception e) { Log.Warn(e.Message); }
        }

        private static (int, byte[], string) Get(string url, string etag)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Bilem-Agent/" + SemVer.Current;
            req.Accept = url.Contains("api.github.com") ? "application/vnd.github+json" : "*/*";
            req.Timeout = 60000;
            req.AllowAutoRedirect = true;
            if (!string.IsNullOrEmpty(etag)) req.Headers[HttpRequestHeader.IfNoneMatch] = etag;
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    var p = _progress;
                    if (p == null) s.CopyTo(ms);
                    else
                    {
                        var buf = new byte[81920];
                        var total = resp.ContentLength;
                        var last = DateTime.MinValue;
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            ms.Write(buf, 0, n);
                            if ((DateTime.Now - last).TotalMilliseconds >= 200) { last = DateTime.Now; p(ms.Length, total); }
                        }
                        p(ms.Length, total);
                    }
                    return ((int)resp.StatusCode, ms.ToArray(), resp.Headers[HttpResponseHeader.ETag]);
                }
            }
            catch (WebException e) when (e.Response is HttpWebResponse r)
            {
                using (r) return ((int)r.StatusCode, new byte[0], r.Headers[HttpResponseHeader.ETag]);
            }
            catch (WebException e) { throw new AgentException(Txt.F("UpdNetwork", e.Message)); }
        }
    }
}
