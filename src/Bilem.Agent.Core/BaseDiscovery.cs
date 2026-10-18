using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bilem.Agent.Core
{
    public enum BaseKind { File, Server, Web, Unknown }

    public sealed class FoundBase
    {
        public string Name { get; set; }
        public BaseKind Kind { get; set; }
        public string Path { get; set; }
        public string Server { get; set; }
        public string Ref { get; set; }
        public string Source { get; set; }
        public bool Exists => Kind == BaseKind.File && File.Exists(System.IO.Path.Combine(Path ?? "", "1Cv8.1CD"));
        public override string ToString() => Kind == BaseKind.File ? $"{Name} — {Path}" : $"{Name} — {Server}\\{Ref}";
    }

    /// <summary>Поиск баз 1С: списки баз всех профилей Windows (C:\Users\*\AppData\Roaming\1C\1CEStart\ibases.v8i).</summary>
    public static class BaseDiscovery
    {
        public static List<FoundBase> FindAll()
        {
            var result = new List<FoundBase>();
            var usersRoot = System.IO.Path.Combine(System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)), "Users");
            IEnumerable<string> profiles;
            try { profiles = Directory.GetDirectories(usersRoot); } catch { profiles = Enumerable.Empty<string>(); }
            foreach (var prof in profiles)
            {
                var f = System.IO.Path.Combine(prof, "AppData", "Roaming", "1C", "1CEStart", "ibases.v8i");
                try { if (File.Exists(f)) result.AddRange(Parse(File.ReadAllText(f, Encoding.UTF8), f)); }
                catch { /* нет доступа к профилю — пропускаем */ }
            }
            // Одна и та же база может быть в нескольких профилях — оставляем первую
            return result
                .GroupBy(b => b.Kind == BaseKind.File ? "F:" + Norm(b.Path) : "S:" + (b.Server + "\\" + b.Ref).ToLowerInvariant())
                .Select(g => g.First())
                .OrderBy(b => b.Kind).ThenBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static string Norm(string p) => (p ?? "").Trim().TrimEnd('\\').ToLowerInvariant();

        /// <summary>Разбор ibases.v8i: секции [Имя] и строка Connect=File="…"; / Srvr="…";Ref="…"; / ws="…";.</summary>
        public static List<FoundBase> Parse(string text, string source = null)
        {
            var list = new List<FoundBase>();
            string name = null;
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.Trim().TrimStart('\uFEFF');
                if (line.StartsWith("[") && line.EndsWith("]")) { name = line.Substring(1, line.Length - 2); continue; }
                if (name == null || !line.StartsWith("Connect=", StringComparison.OrdinalIgnoreCase)) continue;
                var conn = line.Substring("Connect=".Length);
                var p = ConnParams(conn);
                var b = new FoundBase { Name = name, Source = source };
                if (p.TryGetValue("file", out var file)) { b.Kind = BaseKind.File; b.Path = file; }
                else if (p.TryGetValue("srvr", out var srv)) { b.Kind = BaseKind.Server; b.Server = srv; p.TryGetValue("ref", out var rf); b.Ref = rf; }
                else if (p.ContainsKey("ws")) { b.Kind = BaseKind.Web; b.Server = p["ws"]; }
                else b.Kind = BaseKind.Unknown;
                list.Add(b);
                name = null;
            }
            return list;
        }

        /// <summary>Параметры строки соединения 1С: Имя="значение"; с удвоенными кавычками внутри.</summary>
        public static Dictionary<string, string> ConnParams(string conn)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(conn ?? "", "([A-Za-z]+)\\s*=\\s*(?:\"((?:[^\"]|\"\")*)\"|([^;]*))\\s*;?"))
            {
                var v = m.Groups[2].Success ? m.Groups[2].Value.Replace("\"\"", "\"") : m.Groups[3].Value;
                d[m.Groups[1].Value.ToLowerInvariant()] = v;
            }
            return d;
        }

        /// <summary>Короткое имя базы: латиница, цифры, «_»; из имени папки.</summary>
        public static string SlugFromPath(string path)
        {
            var leaf = System.IO.Path.GetFileName((path ?? "").TrimEnd('\\', '/'));
            var s = Regex.Replace(Translit(leaf).ToLowerInvariant(), "[^a-z0-9_]+", "_").Trim('_');
            return string.IsNullOrEmpty(s) ? "base" : (s.Length > 40 ? s.Substring(0, 40) : s);
        }

        private static string Translit(string s)
        {
            const string ru = "абвгдеёжзийклмнопрстуфхцчшщъыьэюяәғқңөұүһі";
            string[] la = { "a","b","v","g","d","e","e","zh","z","i","y","k","l","m","n","o","p","r","s","t","u","f","h","ts","ch","sh","sch","","y","","e","yu","ya","a","g","k","n","o","u","u","h","i" };
            var sb = new StringBuilder();
            foreach (var ch in s ?? "")
            {
                var lower = char.ToLowerInvariant(ch);
                var i = ru.IndexOf(lower);
                sb.Append(i >= 0 ? la[i] : ch.ToString());
            }
            return sb.ToString();
        }
    }
}
