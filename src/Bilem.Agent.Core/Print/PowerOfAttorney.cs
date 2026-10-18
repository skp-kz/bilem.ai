using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Print
{
    /// <summary>Организация в шапке печатной формы: наименование, БИН, адрес, банковский счёт.</summary>
    public sealed class OrgHeader
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string ShortName { get; set; }
        public string Bin { get; set; }
        public string Address { get; set; }
        public string Iik { get; set; }
        public string BankName { get; set; }
        public string Bik { get; set; }
        public string Kbe { get; set; }

        /// <summary>Организация из 1С; счёт — указанный (accountKey) или основной; адрес — своё значение или из контактной информации.</summary>
        internal static OrgHeader Load(ODataClient od, string orgKey, PrintSettings ps, string accountKey = null)
        {
            var h = new OrgHeader { Key = orgKey };
            var org = Od.One(od, "Catalog_Организации", orgKey, "Description,НаименованиеПолное,ИдентификационныйНомер,КБЕ,ОсновнойБанковскийСчет_Key");
            if (org != null)
            {
                h.ShortName = Od.NotEmpty((string)org["Description"]);
                h.Name = Od.NotEmpty((string)org["НаименованиеПолное"]) ?? h.ShortName;
                h.Bin = (string)org["ИдентификационныйНомер"];
                h.Kbe = Od.NotEmpty((string)org["КБЕ"]);
                var acc = Od.One(od, "Catalog_БанковскиеСчета", Od.IsEmpty(accountKey) ? (string)org["ОсновнойБанковскийСчет_Key"] : accountKey, "НомерСчета,Банк_Key");
                if (acc != null)
                {
                    h.Iik = (string)acc["НомерСчета"];
                    var bank = Od.One(od, "Catalog_Банки", (string)acc["Банк_Key"], "Description,БИК,Code");
                    if (bank != null) { h.BankName = (string)bank["Description"]; h.Bik = Od.NotEmpty((string)bank["БИК"]) ?? (string)bank["Code"]; }
                }
            }
            if (!string.IsNullOrWhiteSpace(ps?.AddressOverride)) h.Address = ps.AddressOverride.Trim();
            else if (ps?.Addresses != null && orgKey != null && ps.Addresses.TryGetValue(orgKey, out var addr)) h.Address = addr;
            return h;
        }
    }

    public sealed class PoaLine
    {
        public int N { get; set; }
        public string Name { get; set; }
        public string Unit { get; set; }
        public decimal Qty { get; set; }
    }

    /// <summary>Доверенность (форма Д-1) — всё для печатной формы. Удостоверение личности — только в PDF, наружу MCP не отдаётся.</summary>
    public sealed class PoaData
    {
        public string Number { get; set; }
        public string PrintNumber { get; set; }
        public DateTime Date { get; set; }
        public DateTime ValidTo { get; set; }
        public OrgHeader Org { get; set; }
        public string PersonName { get; set; }
        public string PersonTitle { get; set; }
        /// <summary>Вид документа («Удостоверение личности») и текст «№ … от …, выдано …»; null — нет в 1С или выключено.</summary>
        public string IdKind { get; set; }
        public string IdText { get; set; }
        public string From { get; set; }
        public string FromKey { get; set; }
        public string ByDocument { get; set; }
        public List<PoaLine> Lines { get; } = new List<PoaLine>();
    }

    /// <summary>Чтение доверенности из 1С: по номеру, последняя, поиск по сотруднику и периоду. Только чтение.</summary>
    public static class PoaSource
    {
        public const string DocSet = "Document_Доверенность";
        private const string PersonSet = "Catalog_ФизическиеЛица";

        public sealed class Found
        {
            public string Key; public string Number; public DateTime Date; public DateTime? ValidTo; public string Person; public string From;
        }

        /// <summary>Физлица по части ФИО (служебный справочник, только ключи).</summary>
        private static List<string> People(ODataClient od, string employee)
        {
            var rows = Od.Service(od, PersonSet, new Dictionary<string, string>
            {
                ["$select"] = "Ref_Key",
                ["$filter"] = $"substringof({ODataClient.Lit(employee.Trim())}, Description) and IsFolder eq false",
                ["$top"] = "50",
            });
            return rows.Select(r => (string)r["Ref_Key"]).ToList();
        }

        /// <summary>Доверенности по сотруднику (часть ФИО) и периоду, новые сверху: номер, дата, кому, от кого.</summary>
        public static (List<Found> Items, int Total) Search(ODataClient od, string employee, string period, int top)
        {
            var conds = new List<string> { "DeletionMark eq false" };
            if (!string.IsNullOrWhiteSpace(employee))
            {
                var keys = People(od, employee);
                if (keys.Count == 0) return (new List<Found>(), 0);
                conds.Add("(" + string.Join(" or ", keys.Select(k => $"ФизЛицо_Key eq guid'{k}'")) + ")");
            }
            var (from, to) = InvoiceSource.ParsePeriod(period);
            if (from != null) conds.Add($"Date ge datetime'{Od.D(from.Value)}'");
            if (to != null) conds.Add($"Date le datetime'{Od.D(to.Value)}'");
            var (rows, total) = od.RowsCounted(DocSet, new Dictionary<string, string>
            {
                ["$select"] = "Ref_Key,Number,Date,ДатаДействия,ФизЛицо_Key,НаПолучениеОт,Контрагент_Key",
                ["$filter"] = string.Join(" and ", conds),
                ["$orderby"] = "Date desc,Number desc",
                ["$top"] = top.ToString(CultureInfo.InvariantCulture),
            });
            var names = new Od.Names(od);
            return (rows.Select(r => new Found
            {
                Key = (string)r["Ref_Key"], Number = InvoiceSource.PrintNumber((string)r["Number"]), Date = (DateTime)r["Date"],
                ValidTo = Od.HasDate(r["ДатаДействия"]) ? (DateTime?)(DateTime)r["ДатаДействия"] : null,
                Person = PersonName(od, (string)r["ФизЛицо_Key"]),
                From = Od.NotEmpty((string)r["НаПолучениеОт"]) ?? names.Of("Catalog_Контрагенты", (string)r["Контрагент_Key"]),
            }).ToList(), total);
        }

        /// <summary>Ключ доверенности: номер (как на печати или как в базе), пусто — последняя; employee — уточнение.</summary>
        public static string Find(ODataClient od, string number, string employee = null)
        {
            if (!string.IsNullOrWhiteSpace(employee))
            {
                var all = Search(od, employee, null, 200).Items;
                if (all.Count == 0) throw new AgentException(Txt.F("PoaNoneFor", employee));
                if (string.IsNullOrWhiteSpace(number)) return all[0].Key;
                var nn = InvoiceSource.PrintNumber(number.Trim().TrimStart('№', ' '));
                return all.FirstOrDefault(f => f.Number == nn)?.Key ?? throw new AgentException(Txt.F("PoaNotFound", number));
            }
            var q = new Dictionary<string, string> { ["$select"] = "Ref_Key,Number,Date", ["$orderby"] = "Date desc,Number desc" };
            if (string.IsNullOrWhiteSpace(number))
            {
                q["$filter"] = "DeletionMark eq false";
                q["$top"] = "1";
                var last = od.Rows(DocSet, q).FirstOrDefault() ?? throw new AgentException(Txt.S("PoaNone"));
                return (string)last["Ref_Key"];
            }
            var n = number.Trim().TrimStart('№', ' ');
            var digits = InvoiceSource.PrintNumber(n);
            q["$filter"] = $"substringof({ODataClient.Lit(digits)}, Number) and DeletionMark eq false";
            q["$top"] = "200";
            var hit = od.Rows(DocSet, q).FirstOrDefault(r =>
                string.Equals((string)r["Number"], n, StringComparison.OrdinalIgnoreCase) || InvoiceSource.PrintNumber((string)r["Number"]) == digits);
            return (string)hit?["Ref_Key"] ?? throw new AgentException(Txt.F("PoaNotFound", n));
        }

        /// <summary>ФИО: регистр «ФИО физлиц» (последняя запись), иначе наименование физлица. Служебные объекты.</summary>
        internal static string PersonName(ODataClient od, string personKey)
        {
            if (Od.IsEmpty(personKey)) return null;
            var fio = Od.Service(od, "InformationRegister_ФИОФизЛиц", new Dictionary<string, string>
            {
                ["$filter"] = $"ФизЛицо eq cast(guid'{personKey}', 'Catalog_ФизическиеЛица')",
                ["$orderby"] = "Period desc", ["$top"] = "1",
            }).FirstOrDefault();
            if (fio != null)
            {
                var s = string.Join(" ", new[] { (string)fio["Фамилия"], (string)fio["Имя"], (string)fio["Отчество"] }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
                if (s.Length > 0) return s;
            }
            return Od.NotEmpty((string)Od.One(od, PersonSet, personKey, "Description", check: false)?["Description"]);
        }

        public static PoaData Load(ODataClient od, string key, PrintSettings ps)
        {
            ps = ps ?? new PrintSettings();
            var d = Od.One(od, DocSet, key, null) ?? throw new AgentException(Txt.S("PoaNone"));
            var date = (DateTime)d["Date"];
            var p = new PoaData
            {
                Number = (string)d["Number"],
                PrintNumber = InvoiceSource.PrintNumber((string)d["Number"]),
                Date = date,
                ValidTo = Od.HasDate(d["ДатаДействия"]) ? (DateTime)d["ДатаДействия"] : date.Date.AddDays(Math.Max(1, ps.PoaValidDays)),
                ByDocument = Od.NotEmpty((string)d["ПоДокументу"]),
            };
            var orgKey = (string)d["Организация_Key"];
            p.Org = OrgHeader.Load(od, orgKey, ps, (string)d["СтруктурнаяЕдиница_Key"]);
            var names = new Od.Names(od);
            p.FromKey = Od.IsEmpty((string)d["Контрагент_Key"]) ? null : (string)d["Контрагент_Key"];
            p.From = Od.NotEmpty((string)d["НаПолучениеОт"]) ?? names.Of("Catalog_Контрагенты", p.FromKey);

            // Кому выдана: ФИО, должность (сотрудник этой организации), удостоверение — служебные объекты, только в PDF
            var person = (string)d["ФизЛицо_Key"];
            p.PersonName = PersonName(od, person);
            if (!Od.IsEmpty(person))
            {
                var emp = Od.Service(od, "Catalog_СотрудникиОрганизаций", new Dictionary<string, string>
                {
                    ["$select"] = "ТекущаяДолжностьОрганизации_Key,Организация_Key,Актуальность",
                    ["$filter"] = $"Физлицо_Key eq guid'{person}' and DeletionMark eq false",
                }).OrderByDescending(e => (string)e["Организация_Key"] == orgKey).ThenByDescending(e => (bool?)e["Актуальность"] == true).FirstOrDefault();
                if (emp != null) p.PersonTitle = names.Of("Catalog_ДолжностиОрганизаций", (string)emp["ТекущаяДолжностьОрганизации_Key"], check: false);
                if (ps.PoaIdentity)
                {
                    var pass = Od.Service(od, "InformationRegister_ПаспортныеДанныеФизЛиц", new Dictionary<string, string>
                    {
                        ["$filter"] = $"ФизЛицо_Key eq guid'{person}' and Period le datetime'{Od.D(date)}'",
                        ["$orderby"] = "Period desc", ["$top"] = "1",
                    }).FirstOrDefault();
                    if (pass != null) (p.IdKind, p.IdText) = Identity(pass, names);
                }
            }

            var unitNames = new Dictionary<string, string>();
            foreach (JObject r in ((JArray)d["Товары"] ?? new JArray()).OrderBy(x => (long)x["LineNumber"]))
            {
                var t = (string)r["НаименованиеТовара_Type"] ?? "";
                string name;
                if (t.EndsWith("Catalog_Номенклатура", StringComparison.Ordinal)) name = names.Of("Catalog_Номенклатура", (string)r["НаименованиеТовара"]);
                else if (t == "Edm.String" || t.Length == 0) name = Od.NotEmpty((string)r["НаименованиеТовара"]);
                else name = null;   // ссылка на объект вне состава OData
                p.Lines.Add(new PoaLine
                {
                    N = p.Lines.Count + 1, Name = name ?? "",
                    Unit = names.Of("Catalog_КлассификаторЕдиницИзмерения", (string)r["ЕдиницаПоКлассификатору_Key"]) ?? "",
                    Qty = Od.Dec(r["Количество"]),
                });
            }
            return p;
        }

        /// <summary>«Удостоверение личности», «№ 000000000 от 01.01.2020, выдано МВД РК».</summary>
        internal static (string Kind, string Text) Identity(JToken pass, Od.Names names)
        {
            var kind = names.Of("Catalog_ДокументыУдостоверяющиеЛичность", (string)pass["ДокументВид_Key"], check: false) ?? "Документ, удостоверяющий личность";
            var num = string.Join(" ", new[] { (string)pass["ДокументСерия"], (string)pass["ДокументНомер"] }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
            if (num.Length == 0) return (null, null);
            var text = "№ " + num;
            if (Od.HasDate(pass["ДокументДатаВыдачи"])) text += " от " + ((DateTime)pass["ДокументДатаВыдачи"]).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            var by = Od.NotEmpty((string)pass["ДокументКемВыдан"]);
            if (by != null) text += ", выдано " + by;
            return (kind, text);
        }
    }
}
