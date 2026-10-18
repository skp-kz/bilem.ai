using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Print
{
    public sealed class InvoiceLine
    {
        public int N { get; set; }
        public string Code { get; set; }
        public string Name { get; set; }
        public decimal Qty { get; set; }
        public string Unit { get; set; }
        public decimal Price { get; set; }
        public decimal Sum { get; set; }
        public decimal Vat { get; set; }
        public int? VatRate { get; set; }
    }

    /// <summary>Счёт на оплату покупателю — всё, что нужно печатной форме (читается из 1С через OData, только чтение).</summary>
    public sealed class InvoiceData
    {
        public string Number { get; set; }
        public string PrintNumber { get; set; }
        public DateTime Date { get; set; }
        public string OrgKey { get; set; }
        public string SupplierName { get; set; }
        public string SupplierBin { get; set; }
        public string SupplierAddress { get; set; }
        public string Kbe { get; set; }
        public string Iik { get; set; }
        public string BankName { get; set; }
        public string Bik { get; set; }
        public string Knp { get; set; }
        public string BuyerKey { get; set; }
        public string BuyerName { get; set; }
        public string BuyerBin { get; set; }
        public string Contract { get; set; }
        public bool AccountVat { get; set; }
        public bool PriceIncludesVat { get; set; }
        public List<InvoiceLine> Lines { get; } = new List<InvoiceLine>();
        public decimal LinesTotal => Lines.Sum(l => l.Sum);
        public decimal VatTotal => Lines.Sum(l => l.Vat);
        /// <summary>К оплате: сумма строк, плюс НДС, если цены без НДС.</summary>
        public decimal ToPay => AccountVat && !PriceIncludesVat ? LinesTotal + VatTotal : LinesTotal;
        public int? VatRate => Lines.Where(l => l.Vat != 0).Select(l => l.VatRate).Distinct().Count() == 1 ? Lines.First(l => l.Vat != 0).VatRate : null;
    }

    /// <summary>Чтение счёта из 1С: по номеру (как на печати или как в базе) или последний.</summary>
    public static class InvoiceSource
    {
        public const string DocSet = "Document_СчетНаОплатуПокупателю";

        /// <summary>Номер на печать — как в 1С: без префикса базы/организации и ведущих нулей («ЛК00-000123» → «123»).</summary>
        public static string PrintNumber(string number)
        {
            var s = (number ?? "").Trim();
            var dash = s.LastIndexOf('-');
            var tail = dash >= 0 ? s.Substring(dash + 1) : s;
            if (tail.Length > 0 && tail.All(char.IsDigit))
            {
                var t = tail.TrimStart('0');
                return t.Length == 0 ? "0" : t;
            }
            return s;
        }

        /// <summary>Последние счета (для «Пробный счёт…»): номер, дата, покупатель, сумма.</summary>
        public static List<(string Key, string Number, DateTime Date, string Buyer, decimal Sum)> Recent(ODataClient od, int top = 20)
        {
            var rows = od.Rows(DocSet, new Dictionary<string, string>
            {
                ["$select"] = "Ref_Key,Number,Date,Контрагент_Key,СуммаДокумента",
                ["$filter"] = "DeletionMark eq false",
                ["$orderby"] = "Date desc",
                ["$top"] = top.ToString(CultureInfo.InvariantCulture),
            });
            var names = new Dictionary<string, string>();
            string Buyer(string key)
            {
                if (string.IsNullOrEmpty(key) || IsEmpty(key)) return "";
                if (!names.TryGetValue(key, out var n)) names[key] = n = (string)One(od, "Catalog_Контрагенты", key, "Description")?["Description"] ?? "";
                return n;
            }
            return rows.Select(r => ((string)r["Ref_Key"], PrintNumber((string)r["Number"]), (DateTime)r["Date"], Buyer((string)r["Контрагент_Key"]), Dec(r["СуммаДокумента"]))).ToList();
        }

        public sealed class Found
        {
            public string Key; public string Number; public DateTime Date; public string Buyer; public string BuyerBin; public decimal Sum;
        }

        /// <summary>Период: «2022», «2022-04», «2022-04-13», «2022-01-01..2022-06-30». Пусто — без ограничения.</summary>
        public static (DateTime? From, DateTime? To) ParsePeriod(string period)
        {
            var p = (period ?? "").Trim();
            if (p.Length == 0) return (null, null);
            var ci = CultureInfo.InvariantCulture;
            var parts = p.Split(new[] { ".." }, StringSplitOptions.None);
            if (parts.Length == 2)
            {
                var a = ParsePeriod(parts[0]); var b = ParsePeriod(parts[1]);
                if (a.From == null || b.To == null) throw new AgentException(Txt.F("PrintBadPeriod", period));
                return (a.From, b.To);
            }
            if (System.Text.RegularExpressions.Regex.IsMatch(p, @"^\d{4}$")) { var y = int.Parse(p, ci); return (new DateTime(y, 1, 1), new DateTime(y, 12, 31, 23, 59, 59)); }
            if (DateTime.TryParseExact(p, "yyyy-MM", ci, DateTimeStyles.None, out var m)) return (m, m.AddMonths(1).AddSeconds(-1));
            if (DateTime.TryParseExact(p, "yyyy-MM-dd", ci, DateTimeStyles.None, out var d)) return (d, d.AddDays(1).AddSeconds(-1));
            throw new AgentException(Txt.F("PrintBadPeriod", period));
        }

        /// <summary>Ключи покупателей по БИН/ИИН или части названия (как find_counterparty), не больше 20.</summary>
        public static Dictionary<string, (string Name, string Bin)> Buyers(ODataClient od, string counterparty)
        {
            var rows = od.Rows("Catalog_Контрагенты", new Dictionary<string, string>
            {
                ["$select"] = "Ref_Key,Description,ИдентификационныйКодЛичности",
                ["$filter"] = Mcp.Tools.FindFilter(counterparty),
                ["$top"] = "20",
            });
            return rows.ToDictionary(r => (string)r["Ref_Key"], r => ((string)r["Description"], (string)r["ИдентификационныйКодЛичности"]));
        }

        /// <summary>Счета по покупателю и периоду (новые сверху) — список без печати. Только чтение.</summary>
        public static (List<Found> Items, int Total) Search(ODataClient od, string counterparty, string period, int top)
        {
            var conds = new List<string> { "DeletionMark eq false" };
            Dictionary<string, (string Name, string Bin)> buyers = null;
            if (!string.IsNullOrWhiteSpace(counterparty))
            {
                buyers = Buyers(od, counterparty.Trim());
                if (buyers.Count == 0) return (new List<Found>(), 0);
                conds.Add("(" + string.Join(" or ", buyers.Keys.Select(k => $"Контрагент_Key eq guid'{k}'")) + ")");
            }
            var (from, to) = ParsePeriod(period);
            var ci = CultureInfo.InvariantCulture;
            if (from != null) conds.Add($"Date ge datetime'{from.Value.ToString("yyyy-MM-ddTHH:mm:ss", ci)}'");
            if (to != null) conds.Add($"Date le datetime'{to.Value.ToString("yyyy-MM-ddTHH:mm:ss", ci)}'");
            var (rows, total) = od.RowsCounted(DocSet, new Dictionary<string, string>
            {
                ["$select"] = "Ref_Key,Number,Date,Контрагент_Key,СуммаДокумента",
                ["$filter"] = string.Join(" and ", conds),
                ["$orderby"] = "Date desc,Number desc",
                ["$top"] = top.ToString(ci),
            });
            var names = buyers ?? new Dictionary<string, (string, string)>();
            var list = new List<Found>();
            foreach (var r in rows)
            {
                var k = (string)r["Контрагент_Key"] ?? "";
                if (!names.TryGetValue(k, out var nb))
                {
                    var c = IsEmpty(k) ? null : One(od, "Catalog_Контрагенты", k, "Description,ИдентификационныйКодЛичности");
                    nb = ((string)c?["Description"] ?? "", (string)c?["ИдентификационныйКодЛичности"]);
                    names[k] = nb;
                }
                list.Add(new Found { Key = (string)r["Ref_Key"], Number = PrintNumber((string)r["Number"]), Date = (DateTime)r["Date"], Buyer = nb.Name, BuyerBin = nb.Bin, Sum = Dec(r["СуммаДокумента"]) });
            }
            return (list, total);
        }

        /// <summary>Ключ счёта: number — как на печати или как в базе; пусто — последний. counterparty — уточнение (тот же номер у разных покупателей).</summary>
        public static string Find(ODataClient od, string number, string counterparty = null)
        {
            if (!string.IsNullOrWhiteSpace(counterparty))
            {
                var keys = new HashSet<string>(Buyers(od, counterparty.Trim()).Keys);
                if (keys.Count == 0) throw new AgentException(Txt.F("PrintNoBuyer", counterparty));
                var all = Search(od, counterparty, null, 200).Items;
                if (string.IsNullOrWhiteSpace(number)) return all.FirstOrDefault()?.Key ?? throw new AgentException(Txt.F("PrintNoInvoicesFor", counterparty));
                var nn = PrintNumber(number.Trim().TrimStart('№', ' '));
                return all.FirstOrDefault(f => f.Number == nn)?.Key ?? throw new AgentException(Txt.F("PrintInvoiceNotFound", number));
            }
            var q = new Dictionary<string, string> { ["$select"] = "Ref_Key,Number,Date", ["$orderby"] = "Date desc" };
            if (string.IsNullOrWhiteSpace(number))
            {
                // Последний — по дате, при одинаковой дате — по номеру
                q["$filter"] = "DeletionMark eq false";
                q["$orderby"] = "Date desc,Number desc";
                q["$top"] = "1";
                var last = od.Rows(DocSet, q).FirstOrDefault();
                if (last == null) throw new AgentException(Txt.S("PrintNoInvoices"));
                return (string)last["Ref_Key"];
            }
            var n = number.Trim().TrimStart('№', ' ');
            var digits = PrintNumber(n);
            q["$filter"] = $"substringof({ODataClient.Lit(digits)}, Number) and DeletionMark eq false";
            q["$top"] = "200";
            var hit = od.Rows(DocSet, q).FirstOrDefault(r =>
                string.Equals((string)r["Number"], n, StringComparison.OrdinalIgnoreCase) || PrintNumber((string)r["Number"]) == digits);
            if (hit == null) throw new AgentException(Txt.F("PrintInvoiceNotFound", n));
            return (string)hit["Ref_Key"];
        }

        public static InvoiceData Load(ODataClient od, string key, PrintSettings ps)
        {
            var d = One(od, DocSet, key, null) ?? throw new AgentException(Txt.S("PrintNoInvoices"));
            var inv = new InvoiceData
            {
                Number = (string)d["Number"],
                PrintNumber = PrintNumber((string)d["Number"]),
                Date = (DateTime)d["Date"],
                AccountVat = (bool?)d["УчитыватьНДС"] ?? false,
                PriceIncludesVat = (bool?)d["СуммаВключаетНДС"] ?? false,
                Knp = NotEmpty((string)d["КодНазначенияПлатежа"]) ?? NotEmpty(ps?.Knp) ?? "710",
            };

            var orgKey = (string)d["Организация_Key"];
            inv.OrgKey = orgKey;
            var org = One(od, "Catalog_Организации", orgKey, "Description,НаименованиеПолное,ИдентификационныйНомер,КБЕ,ОсновнойБанковскийСчет_Key");
            if (org != null)
            {
                inv.SupplierName = NotEmpty((string)org["НаименованиеПолное"]) ?? (string)org["Description"];
                inv.SupplierBin = (string)org["ИдентификационныйНомер"];
                inv.Kbe = NotEmpty((string)org["КБЕ"]);
                var acc = One(od, "Catalog_БанковскиеСчета", (string)org["ОсновнойБанковскийСчет_Key"], "НомерСчета,Банк_Key");
                if (acc != null)
                {
                    inv.Iik = (string)acc["НомерСчета"];
                    var bank = One(od, "Catalog_Банки", (string)acc["Банк_Key"], "Description,БИК,Code");
                    if (bank != null) { inv.BankName = (string)bank["Description"]; inv.Bik = NotEmpty((string)bank["БИК"]) ?? (string)bank["Code"]; }
                }
            }
            inv.Kbe = inv.Kbe ?? NotEmpty(ps?.Kbe) ?? "17";
            // Адрес — из контактной информации организации в 1С (загружается при подключении базы); поле во вкладке — переопределение
            if (!string.IsNullOrWhiteSpace(ps?.AddressOverride)) inv.SupplierAddress = ps.AddressOverride.Trim();
            else if (ps?.Addresses != null && orgKey != null && ps.Addresses.TryGetValue(orgKey, out var addr)) inv.SupplierAddress = addr;

            inv.BuyerKey = (string)d["Контрагент_Key"];
            var buyer = One(od, "Catalog_Контрагенты", inv.BuyerKey, "Description,НаименованиеПолное,ИдентификационныйКодЛичности");
            if (buyer != null)
            {
                inv.BuyerName = NotEmpty((string)buyer["НаименованиеПолное"]) ?? (string)buyer["Description"];
                inv.BuyerBin = (string)buyer["ИдентификационныйКодЛичности"];
            }
            var contract = One(od, "Catalog_ДоговорыКонтрагентов", (string)d["ДоговорКонтрагента_Key"], "Description");
            inv.Contract = NotEmpty((string)contract?["Description"]) ?? Txt.S("PrintNoContract");

            var nomCache = new Dictionary<string, JObject>();
            var unitCache = new Dictionary<string, string>();
            var rateCache = new Dictionary<string, int?>();
            JObject Nom(string k)
            {
                if (string.IsNullOrEmpty(k) || IsEmpty(k)) return null;
                if (!nomCache.TryGetValue(k, out var n)) nomCache[k] = n = One(od, "Catalog_Номенклатура", k, "Code,Description,НаименованиеПолное,БазоваяЕдиницаИзмерения_Key");
                return n;
            }
            string Unit(string k)
            {
                if (string.IsNullOrEmpty(k) || IsEmpty(k)) return "";
                if (!unitCache.TryGetValue(k, out var u)) unitCache[k] = u = (string)One(od, "Catalog_КлассификаторЕдиницИзмерения", k, "Description")?["Description"] ?? "";
                return u;
            }
            int? Rate(string k)
            {
                if (string.IsNullOrEmpty(k) || IsEmpty(k)) return null;
                if (!rateCache.TryGetValue(k, out var r)) rateCache[k] = r = (int?)One(od, "Catalog_СтавкиНДС", k, "Ставка")?["Ставка"];
                return r;
            }

            var lines = new List<(long Ln, InvoiceLine L)>();
            foreach (JObject r in (JArray)d["Товары"] ?? new JArray())
            {
                var nom = Nom((string)r["Номенклатура_Key"]);
                lines.Add(((long)r["LineNumber"], new InvoiceLine
                {
                    Code = (string)nom?["Code"], Name = (string)nom?["Description"] ?? "",
                    Qty = Dec(r["Количество"]), Unit = Unit((string)r["ЕдиницаИзмерения_Key"]) is var u && u.Length > 0 ? u : Unit((string)nom?["БазоваяЕдиницаИзмерения_Key"]),
                    Price = Dec(r["Цена"]), Sum = Dec(r["Сумма"]), Vat = Dec(r["СуммаНДС"]), VatRate = Rate((string)r["СтавкаНДС_Key"]),
                }));
            }
            foreach (JObject r in (JArray)d["Услуги"] ?? new JArray())
            {
                var nom = Nom((string)r["Номенклатура_Key"]);
                lines.Add((100000 + (long)r["LineNumber"], new InvoiceLine
                {
                    // В строке — «Содержание», если заполнено, иначе наименование номенклатуры
                    Code = (string)nom?["Code"], Name = NotEmpty((string)r["Содержание"]) ?? (string)nom?["Description"] ?? "",
                    Qty = Dec(r["Количество"]), Unit = Unit((string)nom?["БазоваяЕдиницаИзмерения_Key"]),
                    Price = Dec(r["Цена"]), Sum = Dec(r["Сумма"]), Vat = Dec(r["СуммаНДС"]), VatRate = Rate((string)r["СтавкаНДС_Key"]),
                }));
            }
            var i = 0;
            foreach (var l in lines.OrderBy(x => x.Ln)) { l.L.N = ++i; inv.Lines.Add(l.L); }
            return inv;
        }

        private static JObject One(ODataClient od, string set, string key, string select)
        {
            if (string.IsNullOrEmpty(key) || IsEmpty(key)) return null;
            od.Check(set);
            var r = od.Send("GET", $"{od.BaseUrl}/{set}(guid'{key}')?$format=json" + (select == null ? "" : "&$select=" + Uri.EscapeDataString(select)));
            if (r.Code == 404) return null;
            if (r.Code != 200) throw new AgentException(Txt.F("ODataHttpError", r.Code, ODataClient.ErrorText(r.Body)));
            return JObject.Parse(r.Body);
        }

        private static bool IsEmpty(string key) => key == "00000000-0000-0000-0000-000000000000";
        private static string NotEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        private static decimal Dec(JToken t) => t == null || t.Type == JTokenType.Null ? 0m : Convert.ToDecimal((double)t, CultureInfo.InvariantCulture);
    }
}
