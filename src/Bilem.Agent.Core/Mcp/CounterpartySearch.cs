using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Mcp
{
    /// <summary>
    /// Поиск контрагента по тому, как его назвал человек (#101): «Protax Company» → «Pro Tax &amp; Accounting», «Протакс» → «Pro Tax»,
    /// «Ай Си Лайн» → «IC Line». Сначала прежний OData-фильтр по подстроке; пусто — справочник в память (кэш 5 мин на базу) и сравнение
    /// ключей: без регистра, пробелов и знаков, без форм собственности, в латинице. Второй уровень (нестрогий) — по согласным.
    /// Ничего — до 5 ближайших в Similar. Одна логика для find_counterparty, покупателя в draft_invoice, print_invoice и акта сверки.
    /// </summary>
    public static class CounterpartySearch
    {
        public const string Set = "Catalog_Контрагенты";
        public const string Select = "Ref_Key,Description,НаименованиеПолное,ИдентификационныйКодЛичности,ОсновнойДоговорКонтрагента_Key";
        public const int SimilarTop = 5;
        public static TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

        public sealed class Result
        {
            public List<JObject> Rows = new List<JObject>();
            public int Total;
            /// <summary>Найдено только нестрого (по согласным) — переспросить человека, самим не выбирать.</summary>
            public bool Fuzzy;
            public List<JObject> Similar = new List<JObject>();
        }

        public static Result Find(ODataClient od, string query, int top)
        {
            var q = (query ?? "").Trim();
            var (rows, total) = od.RowsCounted(Set, new Dictionary<string, string>
            {
                ["$select"] = Select, ["$filter"] = Tools.FindFilter(q), ["$orderby"] = "Description",
                ["$top"] = top.ToString(CultureInfo.InvariantCulture),
            });
            if (rows.Count > 0 || Regex.IsMatch(q, @"^\d+$")) return new Result { Rows = rows.OfType<JObject>().ToList(), Total = total };
            return Match(Catalog(od), q, top);
        }

        // ---------- справочник в памяти ----------

        private sealed class Cached { public DateTime At; public List<Entry> Entries; }
        private static readonly ConcurrentDictionary<string, Cached> Cache = new ConcurrentDictionary<string, Cached>();

        internal sealed class Entry
        {
            public JObject Row;
            public string[] Compact, Skeleton;
        }

        /// <summary>Забыть справочник базы — после create_counterparty новый контрагент находится по названию сразу.</summary>
        internal static void Forget(ODataClient od) => Cache.TryRemove(od.BaseUrl, out _);

        private static List<Entry> Catalog(ODataClient od)
        {
            if (Cache.TryGetValue(od.BaseUrl, out var c) && DateTime.UtcNow - c.At < CacheTtl) return c.Entries;
            var rows = od.Rows(Set, new Dictionary<string, string>
            {
                ["$select"] = Select, ["$filter"] = "IsFolder eq false and DeletionMark eq false", ["$orderby"] = "Description",
            });
            var list = rows.OfType<JObject>().Select(Index).ToList();
            Cache[od.BaseUrl] = new Cached { At = DateTime.UtcNow, Entries = list };
            return list;
        }

        internal static Entry Index(JObject r)
        {
            var names = new[] { (string)r["Description"], (string)r["НаименованиеПолное"] }.Where(s => !string.IsNullOrWhiteSpace(s));
            var compact = names.SelectMany(Compacts).Distinct().ToArray();
            return new Entry { Row = r, Compact = compact, Skeleton = compact.Select(Skeleton).Distinct().ToArray() };
        }

        internal static Result Match(IEnumerable<JObject> rows, string query, int top) => Match(rows.Select(Index).ToList(), query, top);

        private static Result Match(List<Entry> all, string query, int top)
        {
            var qTokens = Meaningful(Tokens(query)).Select(t => Compact(new[] { t })).Where(t => t.Length >= 2).Distinct().ToList();
            var qc = Compacts(query).Where(s => s.Length >= 2).ToList();
            var res = new Result();
            if (qc.Count == 0) return res;

            // 1) Ключ без пробелов и знаков — подстрока имени; или каждое слово запроса — подстрока имени
            var hit = all.Where(e => qc.Any(q => e.Compact.Any(n => n.Contains(q)))
                                  || (qTokens.Count > 1 && e.Compact.Any(n => qTokens.All(n.Contains)))).ToList();
            if (hit.Count == 0)
            {
                // 2) По согласным: «Протакс» ~ «Pro Tax», «Ай Си Лайн» ~ «IC Line»
                var qs = qc.Select(Skeleton).Where(s => s.Length >= 3).Distinct().ToList();
                hit = all.Where(e => qs.Any(q => e.Skeleton.Any(n => n.Contains(q)))).ToList();
                res.Fuzzy = hit.Count > 0;
            }
            if (hit.Count > 0)
            {
                res.Total = hit.Count;
                res.Rows = hit.OrderBy(e => e.Compact.Min(n => n.Length)).ThenBy(e => (string)e.Row["Description"], StringComparer.CurrentCultureIgnoreCase)
                    .Take(top).Select(e => e.Row).ToList();
                return res;
            }

            // 3) Ничего — ближайшие: хотя бы одно слово запроса (от 4 букв) похоже на имя по парам букв
            var words = qTokens.Where(t => t.Length >= 4).ToList();
            res.Similar = all.Select(e => (e, s: Score(words, e)))
                .Where(x => x.s >= 0.6).OrderByDescending(x => x.s).ThenByDescending(x => qc.Max(q => x.e.Compact.Max(n => Overlap(q, n))))
                .ThenBy(x => x.e.Compact.Min(n => n.Length))
                .Take(SimilarTop).Select(x => x.e.Row).ToList();
            return res;
        }

        /// <summary>Лучшее слово запроса: доля общих пар букв, если начало слова (первые две буквы) есть в имени.</summary>
        private static double Score(List<string> words, Entry e) =>
            words.Count == 0 ? 0 : words.Max(w => e.Compact.Max(n => n.Contains(w.Substring(0, 2)) ? Overlap(w, n) : 0));

        /// <summary>Доля пар букв запроса, которые есть в имени.</summary>
        internal static double Overlap(string q, string n)
        {
            if (q.Length < 2 || n.Length < 2) return 0;
            var have = new HashSet<string>();
            for (var i = 0; i + 1 < n.Length; i++) have.Add(n.Substring(i, 2));
            int all = 0, hit = 0;
            for (var i = 0; i + 1 < q.Length; i++) { all++; if (have.Contains(q.Substring(i, 2))) hit++; }
            return (double)hit / all;
        }

        // ---------- ключи ----------

        /// <summary>Формы собственности и «компания»: из запроса и имени убираем, если без них что-то остаётся.</summary>
        internal static readonly HashSet<string> LegalForms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "тоо", "ип", "ао", "оао", "зао", "пао", "ооо", "чп", "кх", "фх", "гкп", "ргп", "кгп", "кгу", "гу", "пк", "оо", "фирма",
            "компания", "компани", "компаниясы", "company", "co", "llp", "llc", "ltd", "inc", "corp", "jsc", "gmbh", "too", "ip",
        };

        /// <summary>Буквы, продиктованные по-русски: «Ай Си» → IC. Только отдельным словом.</summary>
        private static readonly Dictionary<string, string> LetterNames = new Dictionary<string, string>
        {
            ["эй"] = "a", ["би"] = "b", ["си"] = "c", ["ди"] = "d",["эф"] = "f", ["джи"] = "g", ["эйч"] = "h",
            ["ай"] = "i", ["джей"] = "j", ["кей"] = "k", ["кэй"] = "k", ["эль"] = "l", ["эл"] = "l", ["эм"] = "m", ["эн"] = "n",
            ["оу"] = "o", ["пи"] = "p", ["кью"] = "q", ["ар"] = "r", ["эс"] = "s", ["ти"] = "t", ["ви"] = "v",
["икс"] = "x", ["уай"] = "y", ["зет"] = "z", ["зед"] = "z",
        };

        internal static List<string> Tokens(string s) =>
            Regex.Split((s ?? "").ToLowerInvariant().Replace('ё', 'е'), @"[^\p{L}\p{Nd}]+").Where(t => t.Length > 0).ToList();

        private static List<string> Meaningful(List<string> tokens)
        {
            var rest = tokens.Where(t => !LegalForms.Contains(t)).ToList();
            return rest.Any(t => t.Length >= 2) ? rest : tokens;
        }

        /// <summary>Ключи строки: латиница без пробелов и знаков; вариант с буквами по-русски («ай си» → ic), если отличается.</summary>
        internal static List<string> Compacts(string s)
        {
            var t = Meaningful(Tokens(s));
            var plain = Compact(t);
            var lettered = Compact(t.Select(x => LetterNames.TryGetValue(x, out var l) ? l : x));
            return plain == lettered ? new List<string> { plain } : new List<string> { plain, lettered };
        }

        internal static string Compact(IEnumerable<string> tokens)
        {
            var s = Translit(string.Concat(tokens));
            return s.Replace("dzh", "j").Replace("kh", "h").Replace("ks", "x");
        }

        /// <summary>Согласные ключа: c/q → k, ph → f, w → v, без гласных, y и h, без удвоений.</summary>
        internal static string Skeleton(string compact)
        {
            var s = compact.Replace("ph", "f").Replace('c', 'k').Replace('q', 'k').Replace('w', 'v');
            var sb = new StringBuilder();
            foreach (var ch in s)
            {
                if ("aeiouyh".IndexOf(ch) >= 0) continue;
                if (sb.Length > 0 && sb[sb.Length - 1] == ch) continue;
                sb.Append(ch);
            }
            return sb.ToString();
        }

        private const string Ru = "абвгдеёжзийклмнопрстуфхцчшщъыьэюяәғқңөұүһі";
        private static readonly string[] La = { "a","b","v","g","d","e","e","zh","z","i","y","k","l","m","n","o","p","r","s","t","u","f","h","ts","ch","sh","sch","","y","","e","yu","ya","a","g","k","n","o","u","u","h","i" };

        internal static string Translit(string s)
        {
            var sb = new StringBuilder();
            foreach (var ch in s ?? "")
            {
                var i = Ru.IndexOf(ch);
                if (i >= 0) sb.Append(La[i]); else sb.Append(ch);
            }
            return sb.ToString();
        }
    }
}
