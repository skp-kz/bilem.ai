using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;

namespace Bilem.Agent.Core
{
    public sealed class CrashRecord
    {
        public DateTime Time { get; set; }
        public string App { get; set; }
        public string AppVersion { get; set; }
        public string Module { get; set; }
        public string ModuleVersion { get; set; }
        public string Code { get; set; }
        public string ModulePath { get; set; }
    }

    /// <summary>
    /// Падения процессов из журнала событий Windows (Application, «Application Error», 1000) — чтобы вместо «HTTP 0»
    /// сказать, что упало: рабочий процесс IIS на модуле 1С (wsisapi.dll) и т. п. Только чтение журнала, без дампов.
    /// </summary>
    public static class Crashes
    {
        public static List<CrashRecord> Since(DateTime from, params string[] apps)
        {
            var list = new List<CrashRecord>();
            try
            {
                var ms = Math.Max(1000, (long)(DateTime.Now - from).TotalMilliseconds);
                var q = new EventLogQuery("Application", PathType.LogName,
                    $"*[System[Provider[@Name='Application Error'] and (EventID=1000) and TimeCreated[timediff(@SystemTime) <= {ms}]]]");
                using (var rd = new EventLogReader(q))
                    for (var e = rd.ReadEvent(); e != null; e = rd.ReadEvent())
                        using (e)
                        {
                            var p = e.Properties.Select(x => x.Value?.ToString() ?? "").ToList();
                            if (p.Count < 7) continue;
                            if (apps.Length > 0 && !apps.Any(a => string.Equals(a, p[0], StringComparison.OrdinalIgnoreCase))) continue;
                            list.Add(new CrashRecord
                            {
                                Time = e.TimeCreated ?? DateTime.Now, App = p[0], AppVersion = p[1], Module = p[3], ModuleVersion = p[4],
                                Code = "0x" + p[6].Replace("0x", ""), ModulePath = p.Count > 11 ? p[11] : "",
                            });
                        }
            }
            catch { }   // журнал недоступен — без подробностей, как раньше
            return list.OrderBy(x => x.Time).ToList();
        }

        /// <summary>Падения w3wp после начала запроса — текст для шага проверки (FAIL) и отчёта; пусто — падений нет.</summary>
        public static string IisExplain(DateTime from)
        {
            var c = Since(from.AddMinutes(-1), "w3wp.exe");
            if (c.Count == 0) return null;
            var last = c[c.Count - 1];
            var times = string.Join(", ", c.Select(x => x.Time.ToString("HH:mm:ss")));
            var oneC = last.Module.StartsWith("wsisapi", StringComparison.OrdinalIgnoreCase) || last.ModulePath.IndexOf("1cv8", StringComparison.OrdinalIgnoreCase) >= 0;
            return Txt.F(oneC ? "IisCrash1C" : "IisCrashOther", last.Module, last.ModuleVersion, last.Code, c.Count, times);
        }
    }
}
