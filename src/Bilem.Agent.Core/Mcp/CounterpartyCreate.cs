using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Mcp
{
    /// <summary>
    /// Новый контрагент по названию и БИН (#100, specs/create-invoice.md, дополнение 16.11 (2)): draft_counterparty ищет такого же
    /// (по БИН, затем по названию) и ничего не пишет; create_counterparty по id подтверждённого черновика один раз делает POST
    /// в справочник «Контрагенты». Только создание: ни PATCH, ни DELETE. Адрес, банк и договор не заполняем. Каждое создание — в журнал.
    /// Справочник проводок не делает — учёт не меняется. Те же имена, поля и статусы, что в облачной ветке (minidesk 26bca7a).
    /// </summary>
    public static class CounterpartyCreate
    {
        public const string FullName = "Справочник.Контрагенты";
        public const string Set = CounterpartySearch.Set;
        public const string DraftTool = "draft_counterparty", CreateTool = "create_counterparty";
        public const string KindLegal = "ЮрЛицо", KindPerson = "ФизЛицо";
        private const int SimilarTop = 5;

        public static bool IsTool(string name) => name == DraftTool || name == CreateTool;

        /// <summary>Запись контрагентов включена «Доступом» (слой MCP) — иначе инструментов нет в списке.</summary>
        public static bool Enabled(BaseConfig b) => b?.Write != null && b.Write.Contains(FullName);

        private static JToken NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? JValue.CreateNull() : (JToken)s.Trim();

        /// <summary>БИН/ИИН: 12 цифр, пробелы и дефисы не считаются. Пусто — null; не 12 цифр — ошибка.</summary>
        internal static string Bin(string s)
        {
            var d = Regex.Replace(s ?? "", @"[\s\-]", "");
            if (d.Length == 0) return null;
            if (!Regex.IsMatch(d, @"^\d{12}$")) throw new AgentException(Txt.F("CpBadBin", (s ?? "").Trim()));
            return d;
        }

        /// <summary>ЮрЛицо / ФизЛицо. Не передан — ИП (отдельным словом в названии) считаем физлицом, остальных — юрлицом.</summary>
        internal static string Kind(string kind, string name)
        {
            var k = (kind ?? "").Trim().ToLowerInvariant().Replace(" ", "");
            if (k.Length == 0)
                return CounterpartySearch.Tokens(name).Any(t => t == "ип" || t == "ip") ? KindPerson : KindLegal;
            if (k.StartsWith("юр") || k == "legal" || k == "company" || k == "тоо" || k == "ао") return KindLegal;
            if (k.StartsWith("физ") || k == "ип" || k == "ip" || k == "person" || k == "individual") return KindPerson;
            throw new AgentException(Txt.F("CpBadKind", kind));
        }

        /// <summary>Тот же контрагент по названию: ключи без регистра, пробелов, знаков и форм собственности совпадают целиком.</summary>
        internal static bool SameName(JObject row, string name)
        {
            var q = new HashSet<string>(CounterpartySearch.Compacts(name).Where(x => x.Length > 0));
            if (q.Count == 0) return false;
            return new[] { (string)row["Description"], (string)row["НаименованиеПолное"] }
                .Where(s => !string.IsNullOrWhiteSpace(s)).SelectMany(CounterpartySearch.Compacts).Any(q.Contains);
        }

        private static JObject Option(JObject r) => new JObject
            { ["id"] = r["Ref_Key"], ["name"] = r["Description"], ["bin"] = NullIfEmpty((string)r["ИдентификационныйКодЛичности"]) };

        private static JObject ByBin(ODataClient od, string bin) => bin == null ? null : od.Rows(Set, new Dictionary<string, string>
        {
            ["$select"] = CounterpartySearch.Select,
            ["$filter"] = $"ИдентификационныйКодЛичности eq {ODataClient.Lit(bin)} and IsFolder eq false and DeletionMark eq false",
            ["$top"] = "1",
        }).OfType<JObject>().FirstOrDefault();

        /// <summary>Такой же уже есть: по БИН, затем по названию. Плюс похожие (до 5) — показать человеку рядом с черновиком.</summary>
        internal static (JObject Same, List<JObject> Similar) Existing(ODataClient od, string name, string bin)
        {
            var hit = ByBin(od, bin);
            if (hit != null) return (hit, new List<JObject>());
            var found = CounterpartySearch.Find(od, name, 20);
            var rows = found.Rows;
            // Нестрогое (по согласным) — только похожие, не «тот же»
            var same = found.Fuzzy ? null : rows.FirstOrDefault(r => SameName(r, name));
            if (same != null) return (same, new List<JObject>());
            // Похожие — только совпавшие по названию (частью или по звучанию); «ближайшие по буквам» для нового названия — шум
            return (null, rows.Take(SimilarTop).ToList());
        }

        // ---------- draft_counterparty ----------

        public static JObject MakeDraft(IReadOnlyList<BaseRuntime> all, JObject a)
        {
            var b = Tools.OneBase(all, (string)a["base"]);
            if (!Enabled(b.Config)) throw new AgentException(Txt.F("CpWriteOff", b.Config.Slug), Txt.S("CpHintWriteOff"));
            var od = b.OData;
            od.Check(Set);
            var name = Regex.Replace(((string)a["name"] ?? "").Trim(), @"\s+", " ");
            if (name.Length < 2) throw new AgentException(Txt.S("CpNeedName"));
            if (name.Length > 100) throw new AgentException(Txt.S("CpNameTooLong"));
            var fullName = Regex.Replace(((string)a["full_name"] ?? "").Trim(), @"\s+", " ");
            if (fullName.Length == 0) fullName = name;
            var bin = Bin((string)a["bin"]);
            var kind = Kind((string)a["kind"], name);

            var (same, similar) = Existing(od, name, bin);
            if (same != null)
                return new JObject
                {
                    ["status"] = "exists", ["base"] = b.Config.Slug, ["counterparty"] = Option(same),
                    ["matched_by"] = bin != null && (string)same["ИдентификационныйКодЛичности"] == bin ? "bin" : "name",
                    ["note"] = Txt.S("CpExistsNote"),
                };

            var warnings = new JArray();
            if (bin == null) warnings.Add(Txt.S("CpNoBin"));
            warnings.Add(Txt.S("CpNoDetails"));
            var body = new JObject
            {
                ["Description"] = name, ["НаименованиеПолное"] = fullName, ["ЮрФизЛицо"] = kind,
                ["ИдентификационныйКодЛичности"] = bin ?? "",
            };
            var d = InvoiceCreate.Save(b.Config.Slug, InvoiceCreate.OwnerOf(McpCaller.Current), body, name, 0m, "cp-");
            d.Kind = InvoiceCreate.KindCounterparty;
            body["Комментарий"] = Txt.F("CpComment", d.Id);
            return new JObject
            {
                ["status"] = "ready",
                ["draft_id"] = d.Id,
                ["expires_at"] = d.CreatedUtc.Add(InvoiceCreate.DraftTtl).ToLocalTime().ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
                ["base"] = b.Config.Slug,
                ["counterparty"] = new JObject { ["name"] = name, ["full_name"] = fullName, ["bin"] = NullIfEmpty(bin), ["kind"] = kind },
                ["similar"] = new JArray(similar.Select(Option)),
                ["warnings"] = warnings,
                ["note"] = Txt.S("CpDraftNote"),
            };
        }

        // ---------- create_counterparty ----------

        public static JObject Create(IReadOnlyList<BaseRuntime> all, JObject a)
        {
            var caller = McpCaller.Current;
            var d = InvoiceCreate.Take((string)a["draft_id"], InvoiceCreate.OwnerOf(caller), InvoiceCreate.KindCounterparty);
            var b = all.FirstOrDefault(x => string.Equals(x.Config.Slug, d.Slug, StringComparison.OrdinalIgnoreCase))
                ?? throw new AgentException(Txt.F("NoSuchBase", d.Slug));
            if (!string.IsNullOrWhiteSpace((string)a["base"]) && !string.Equals(((string)a["base"]).Trim(), d.Slug, StringComparison.OrdinalIgnoreCase))
                throw new AgentException(Txt.F("CpDraftOtherBase", d.Slug));
            if (!Enabled(b.Config)) throw new AgentException(Txt.F("CpWriteOff", b.Config.Slug), Txt.S("CpHintWriteOff"));
            var od = b.OData;
            od.Check(Set);
            var bin = NullIfEmpty((string)d.Body["ИдентификационныйКодЛичности"])?.ToString();
            // За 15 минут черновика такого же могли завести в 1С руками — второй не создаём
            var (same, _) = Existing(od, (string)d.Body["Description"], bin);
            if (same != null) throw new AgentException(Txt.F("CpAppeared", (string)same["Description"], (string)same["Ref_Key"]));

            // Единственная запись в справочник: POST нового контрагента (ни PATCH, ни DELETE)
            var r = od.Send("POST", $"{od.BaseUrl}/{Set}?$format=json", d.Body.ToString(Formatting.None));
            var title = Txt.F("CpAuditTitle", d.BuyerName, b.Config.Slug);
            if (!r.Ok)
            {
                var why = ODataClient.ErrorText(r.Body);
                Audit.Write(Audit.CreateFailed, b.Config.Slug, Audit.KindCounterparty, title, null, null, caller: caller,
                    extra: new JObject { ["draft_id"] = d.Id, ["name"] = d.BuyerName, ["bin"] = bin, ["http"] = r.Code, ["error"] = why });
                throw new AgentException(Txt.F("CpCreateFailed", r.Code, why), Txt.S("CpHintNewDraft"));
            }
            CounterpartySearch.Forget(od);
            var doc = JObject.Parse(r.Body);
            var code = ((string)doc["Code"] ?? "").Trim();
            Audit.Write(Audit.CounterpartyCreate, b.Config.Slug, Audit.KindCounterparty, title, null, null, caller: caller,
                extra: new JObject { ["draft_id"] = d.Id, ["ref"] = doc["Ref_Key"], ["code"] = code, ["name"] = doc["Description"], ["bin"] = bin });
            return new JObject
            {
                ["base"] = b.Config.Slug,
                ["id"] = doc["Ref_Key"],
                ["name"] = doc["Description"],
                ["code"] = NullIfEmpty(code),
                ["bin"] = NullIfEmpty((string)doc["ИдентификационныйКодЛичности"]),
                ["hint"] = Txt.F("CpCreatedHint", (string)doc["Ref_Key"]),
            };
        }
    }
}
