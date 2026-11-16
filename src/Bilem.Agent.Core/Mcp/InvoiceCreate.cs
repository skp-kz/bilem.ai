using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Mcp
{
    /// <summary>
    /// Счёт на оплату покупателю по фразе руководителя (#99, specs/create-invoice.md): draft_invoice собирает черновик и ничего
    /// не пишет; create_invoice по id подтверждённого черновика один раз делает POST нового счёта. Один вид документа, только
    /// создание (ни PATCH, ни DELETE, ни пометки на удаление), справочники не создаём. Каждое создание — в журнал аудита.
    /// </summary>
    public static class InvoiceCreate
    {
        public const string DocFullName = "Документ.СчетНаОплатуПокупателю";
        public const string DocSet = "Document_СчетНаОплатуПокупателю";
        public const string DraftTool = "draft_invoice", CreateTool = "create_invoice";
        public static readonly TimeSpan DraftTtl = TimeSpan.FromMinutes(15);
        public const int MaxLines = 20;
        /// <summary>Вариант Б: услуги нет в номенклатуре — человек выбирает похожую, его слова идут в «Содержание». false — вариант А.</summary>
        public static bool AllowContent = true;
        internal const string EmptyKey = "00000000-0000-0000-0000-000000000000";
        private const int OptionsTop = 10, SimilarTop = 5;

        public const string UpdateTool = "update_invoice", CancelTool = "cancel_invoice";
        /// <summary>Свой счёт можно исправить или пометить на удаление столько времени после создания (Слава, 14.11).</summary>
        public static readonly TimeSpan OwnWindow = TimeSpan.FromHours(24);

        public static bool IsTool(string name) => name == DraftTool || name == CreateTool || name == UpdateTool || name == CancelTool;

        private static string ToolOf(string kind) => kind == KindUpdate ? UpdateTool : kind == KindCancel ? CancelTool : CreateTool;

        /// <summary>Запись счёта включена «Доступом» (слой MCP) — иначе инструментов нет в списке.</summary>
        public static bool Enabled(BaseConfig b) => b?.Write != null && b.Write.Contains(DocFullName);

        // ---------- черновики ----------

        internal sealed class Draft
        {
            public string Id, Slug, Owner;
            public DateTime CreatedUtc;
            public bool Used;
            /// <summary>Тело POST без даты (дата — в момент создания).</summary>
            public JObject Body;
            public string BuyerName;
            public decimal Total;
            /// <summary>create | update | cancel; для update/cancel — какой счёт и его версия данных на момент черновика.</summary>
            public string Kind = KindCreate, Ref, Version, Number;
        }

        internal const string KindCreate = "create", KindUpdate = "update", KindCancel = "cancel";

        private static readonly Dictionary<string, Draft> Drafts = new Dictionary<string, Draft>();
        internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;

        /// <summary>Чей черновик: собеседник облака (канал + пользователь) или локальный клиент MCP.</summary>
        internal static string OwnerOf(McpCaller c)
        {
            if (c == null) return "local";
            if (!string.IsNullOrEmpty(c.CloudUser)) return "cloud:" + c.CloudChannel + ":" + c.CloudUser;
            return "local:" + (c.TailscaleLogin ?? c.Client ?? "");
        }

        internal static string NewId()
        {
            var b = new byte[6];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            return "inv-" + string.Concat(b.Select(x => x.ToString("x2")));
        }

        internal static Draft Save(string slug, string owner, JObject body, string buyerName, decimal total)
        {
            var d = new Draft { Id = NewId(), Slug = slug, Owner = owner, CreatedUtc = UtcNow(), Body = body, BuyerName = buyerName, Total = total };
            lock (Drafts)
            {
                foreach (var k in Drafts.Where(kv => UtcNow() - kv.Value.CreatedUtc > DraftTtl + DraftTtl).Select(kv => kv.Key).ToList()) Drafts.Remove(k);
                Drafts[d.Id] = d;
            }
            return d;
        }

        /// <summary>Забрать черновик для создания: тот же собеседник, не просрочен, не использован. Помечается использованным сразу.</summary>
        internal static Draft Take(string id, string owner, string kind = KindCreate)
        {
            lock (Drafts)
            {
                if (string.IsNullOrWhiteSpace(id) || !Drafts.TryGetValue(id.Trim(), out var d)) throw new AgentException(Txt.S("InvDraftUnknown"), Txt.S("InvHintNewDraft"));
                if (d.Kind != kind) throw new AgentException(Txt.F("InvDraftWrongKind", ToolOf(d.Kind)), Txt.S("InvHintNewDraft"));
                if (d.Used) throw new AgentException(Txt.S("InvDraftUsed"), Txt.S("InvHintNewDraft"));
                if (d.Owner != owner) throw new AgentException(Txt.S("InvDraftForeign"), Txt.S("InvHintNewDraft"));
                if (UtcNow() - d.CreatedUtc > DraftTtl) throw new AgentException(Txt.F("InvDraftExpired", (int)DraftTtl.TotalMinutes), Txt.S("InvHintNewDraft"));
                d.Used = true;
                return d;
            }
        }

        internal static void ClearDrafts() { lock (Drafts) Drafts.Clear(); }

        // ---------- суммы ----------

        internal sealed class Line
        {
            public string ItemKey, ItemName, Unit, UnitKey, Content, VatRateKey;
            public bool Service;
            public decimal Qty, Price, Sum, Vat;
            public int? VatRate;
        }

        internal static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

        /// <summary>Количество, цена, сумма строки: нужна цена или сумма; обе — должны сходиться.</summary>
        internal static (decimal Qty, decimal Price, decimal Sum) Amounts(decimal? qty, decimal? price, decimal? sum)
        {
            var q = qty ?? 1m;
            if (q <= 0) throw new AgentException(Txt.S("InvBadQty"));
            if (price == null && sum == null) throw new AgentException(Txt.S("InvNeedPrice"));
            if (price != null && price <= 0 || sum != null && sum <= 0) throw new AgentException(Txt.S("InvBadSum"));
            if (price != null && sum != null)
            {
                if (Math.Abs(R2(price.Value * q) - R2(sum.Value)) > 0.01m) throw new AgentException(Txt.F("InvPriceSumMismatch", price, q, sum));
                return (q, R2(price.Value), R2(sum.Value));
            }
            if (sum != null) return (q, R2(sum.Value / q), R2(sum.Value));
            return (q, R2(price.Value), R2(price.Value * q));
        }

        /// <summary>НДС строки: «сумма включает НДС» — выделяем, иначе начисляем сверху. Не учитываем НДС или ставки нет — 0.</summary>
        internal static decimal Vat(decimal sum, int? rate, bool vatOn, bool included)
        {
            if (!vatOn || rate == null || rate <= 0) return 0m;
            return included ? R2(sum * rate.Value / (100m + rate.Value)) : R2(sum * rate.Value / 100m);
        }

        internal static decimal Total(IEnumerable<Line> lines, bool included) =>
            lines.Sum(l => l.Sum) + (included ? 0m : lines.Sum(l => l.Vat));

        /// <summary>Тело POST нового счёта (без даты). Табличные части «Товары» и «Услуги» — по признаку «Услуга» номенклатуры.</summary>
        internal static JObject Body(JObject head, IList<Line> lines, bool vatOn, bool included)
        {
            var goods = new JArray(); var services = new JArray();
            foreach (var l in lines)
            {
                var row = new JObject
                {
                    ["Номенклатура_Key"] = l.ItemKey,
                    ["Количество"] = l.Qty, ["Цена"] = l.Price, ["Сумма"] = l.Sum,
                    ["СтавкаНДС_Key"] = l.VatRateKey ?? EmptyKey, ["СуммаНДС"] = l.Vat,
                };
                if (l.Service)
                {
                    row["LineNumber"] = (services.Count + 1).ToString(CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(l.Content)) row["Содержание"] = l.Content;
                    services.Add(row);
                }
                else
                {
                    row["LineNumber"] = (goods.Count + 1).ToString(CultureInfo.InvariantCulture);
                    row["ЕдиницаИзмерения_Key"] = l.UnitKey ?? EmptyKey;
                    row["Коэффициент"] = 1;
                    goods.Add(row);
                }
            }
            var b = (JObject)head.DeepClone();
            b["УчитыватьНДС"] = vatOn;
            b["СуммаВключаетНДС"] = included;
            b["УчитыватьАкциз"] = false;
            b["СуммаВключаетАкциз"] = false;
            b["СуммаДокумента"] = Total(lines, included);
            b["Товары"] = goods;
            b["Услуги"] = services;
            return b;
        }

        // ---------- поиск в справочниках ----------

        private static bool IsEmpty(string k) => string.IsNullOrEmpty(k) || k == EmptyKey;
        private static string Guid(string k) => $"guid'{k}'";
        private static string Norm(string s) => (s ?? "").Trim().ToLowerInvariant().Replace('ё', 'е');

        /// <summary>Слова запроса для «похожих»: от 3 букв, самые длинные первыми.</summary>
        internal static List<string> Words(string q) =>
            (q ?? "").Split(new[] { ' ', ',', '.', '«', '»', '"', '-', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 3).OrderByDescending(w => w.Length).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();

        /// <summary>Один из найденных: единственный или единственный с точным совпадением названия.</summary>
        internal static JObject Pick(JArray rows, string query, string nameField = "Description")
        {
            if (rows.Count == 1) return (JObject)rows[0];
            var exact = rows.OfType<JObject>().Where(r => Norm((string)r[nameField]) == Norm(query)).ToList();
            return exact.Count == 1 ? exact[0] : null;
        }

        /// <summary>Название для сравнения дублей: без регистра, ё = е, пробелы по краям и двойные внутри не считаются.</summary>
        internal static string NameKey(string s) => string.Join(" ", Norm(s).Split(new[] { ' ', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries));

        private static string CodeKey(JObject r) { var c = ((string)r["Code"] ?? "").Trim(); return c.Length.ToString("D3", CultureInfo.InvariantCulture) + c; }

        /// <summary>
        /// Позиция номенклатуры по названию (#106): единственная → точное написание (без trim) → точное после нормализации →
        /// единственная из уже стоящих в счетах фирмы → настоящие дубли (названия совпадают после нормализации): уже использованная,
        /// иначе с меньшим кодом, Dup=true. null — позиции разные, спросить человека.
        /// </summary>
        internal static (JObject Item, bool Dup) PickItem(JArray rows, string rawQuery, Func<ISet<string>> used)
        {
            var all = rows.OfType<JObject>().ToList();
            if (all.Count == 0) return (null, false);
            if (all.Count == 1) return (all[0], false);
            var exact = all.Where(r => (string)r["Description"] == rawQuery).ToList();
            if (exact.Count == 1) return (exact[0], false);
            var same = all.Where(r => NameKey((string)r["Description"]) == NameKey(rawQuery)).ToList();
            if (same.Count == 1) return (same[0], false);
            var pool = same.Count > 0 ? same : all;
            var dup = pool.Select(r => NameKey((string)r["Description"])).Distinct().Count() == 1;
            var u = used();
            var inUse = pool.Where(r => u.Contains((string)r["Ref_Key"])).OrderBy(CodeKey, StringComparer.Ordinal).ToList();
            if (inUse.Count == 1 || dup && inUse.Count > 1) return (inUse[0], dup);
            if (dup) return (pool.OrderBy(CodeKey, StringComparer.Ordinal).First(), true);
            return (null, false);
        }

        /// <summary>Номенклатура, которая уже стоит в последних счетах фирмы.</summary>
        private static ISet<string> UsedItems(ODataClient od, string orgKey)
        {
            var res = new HashSet<string>();
            if (IsEmpty(orgKey)) return res;
            foreach (JObject d in od.Rows(DocSet, new Dictionary<string, string>
            {
                ["$filter"] = $"Организация_Key eq {Guid(orgKey)} and DeletionMark eq false",
                ["$orderby"] = "Date desc", ["$top"] = "100", ["$select"] = "Товары,Услуги",
            }))
                foreach (var r in ((JArray)d["Товары"] ?? new JArray()).Concat((JArray)d["Услуги"] ?? new JArray()))
                    if (!IsEmpty((string)r["Номенклатура_Key"])) res.Add((string)r["Номенклатура_Key"]);
            return res;
        }

        /// <summary>Счёт-образец по номеру («повтори счёт 480», #106): самый свежий с таким номером.</summary>
        private static JObject CopySource(ODataClient od, string number)
        {
            var n = number.Trim().TrimStart('№', ' ');
            var digits = Print.InvoiceSource.PrintNumber(n);
            var hit = od.Rows(DocSet, new Dictionary<string, string>
            {
                ["$filter"] = $"substringof({ODataClient.Lit(digits)}, Number) and DeletionMark eq false",
                ["$orderby"] = "Date desc", ["$top"] = "200",
                ["$select"] = "Ref_Key,Number,Date,Организация_Key,Контрагент_Key,ДоговорКонтрагента_Key,Товары,Услуги",
            }).OfType<JObject>().FirstOrDefault(r => string.Equals((string)r["Number"], n, StringComparison.OrdinalIgnoreCase) || Print.InvoiceSource.PrintNumber((string)r["Number"]) == digits);
            return hit ?? throw new AgentException(Txt.F("InvCopyNotFound", n));
        }

        /// <summary>Строки образца — как вход draft_invoice: те же ссылки на номенклатуру (item_id), количество, сумма, содержание.</summary>
        internal static JArray CopyLines(JObject src)
        {
            var res = new JArray();
            foreach (var (part, service) in new[] { ("Товары", false), ("Услуги", true) })
                foreach (JObject r in (JArray)src[part] ?? new JArray())
                {
                    var l = new JObject { ["item_id"] = r["Номенклатура_Key"], ["quantity"] = r["Количество"], ["sum"] = r["Сумма"] };
                    if (service && !string.IsNullOrWhiteSpace((string)r["Содержание"])) l["content"] = ((string)r["Содержание"]).Trim();
                    res.Add(l);
                }
            return res;
        }

        private static JObject Choose(string field, string query, IEnumerable<JObject> options, string note = null) => new JObject
        {
            ["status"] = "choose", ["field"] = field, ["query"] = query, ["options"] = new JArray(options),
            ["note"] = note ?? Txt.S("InvChooseNote"),
        };

        private static JObject NotFound(string field, string query, IEnumerable<string> similar, string note) => new JObject
        {
            ["status"] = "not_found", ["field"] = field, ["query"] = query, ["similar"] = new JArray(similar), ["note"] = note,
        };

        private static JArray Similar(ODataClient od, string set, string query, string extra, string select, string orderby = "Description")
        {
            var seen = new HashSet<string>(); var res = new JArray();
            foreach (var w in Words(query))
            {
                foreach (JObject r in od.Rows(set, new Dictionary<string, string>
                {
                    ["$select"] = select, ["$filter"] = $"substringof({ODataClient.Lit(w)}, Description) and IsFolder eq false and DeletionMark eq false" + extra,
                    ["$orderby"] = orderby, ["$top"] = SimilarTop.ToString(CultureInfo.InvariantCulture),
                }))
                {
                    if (seen.Add((string)r["Ref_Key"])) res.Add(r);
                    if (res.Count >= SimilarTop) return res;
                }
            }
            return res;
        }

        private static JObject One(ODataClient od, string set, string key, string select)
        {
            if (IsEmpty(key)) return null;
            od.Check(set);
            var r = od.Send("GET", $"{od.BaseUrl}/{set}({Guid(key)})?$format=json&$select=" + Uri.EscapeDataString(select));
            if (r.Code == 404) return null;
            if (r.Code != 200) throw new AgentException(Txt.F("ODataHttpError", r.Code, ODataClient.ErrorText(r.Body)));
            return JObject.Parse(r.Body);
        }

        private const string BuyerSelect = "Ref_Key,Description,ИдентификационныйКодЛичности,ОсновнойДоговорКонтрагента_Key";

        private static JObject BuyerOption(JObject r) => new JObject
            { ["id"] = r["Ref_Key"], ["name"] = r["Description"], ["bin"] = NullIfEmpty((string)r["ИдентификационныйКодЛичности"]) };

        private static JToken NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? JValue.CreateNull() : (JToken)s.Trim();

        // ---------- draft_invoice ----------

        public static JObject MakeDraft(IReadOnlyList<BaseRuntime> all, JObject a)
        {
            var b = Tools.OneBase(all, (string)a["base"]);
            if (!Enabled(b.Config)) throw new AgentException(Txt.F("InvWriteOff", b.Config.Slug), Txt.S("InvHintWriteOff"));
            var od = b.OData;
            od.Check(DocSet);

            // Образец «повтори счёт N» (#106): что человек назвал сам, важнее образца
            var copyQ = ((string)a["copy_number"] ?? "").Trim();
            var src = copyQ.Length > 0 ? CopySource(od, copyQ) : null;
            var srcNumber = src == null ? null : Print.InvoiceSource.PrintNumber((string)src["Number"]);
            var inLines = a["lines"] as JArray;
            if (src != null && (inLines == null || inLines.Count == 0))
            {
                inLines = CopyLines(src);
                if (inLines.Count == 0) throw new AgentException(Txt.F("InvCopyEmpty", srcNumber));
            }

            // Покупатель
            var buyerQ = ((string)a["buyer"] ?? "").Trim();
            var buyerId = ((string)a["buyer_id"] ?? "").Trim();
            if (src != null && buyerQ.Length == 0 && buyerId.Length == 0) buyerId = (string)src["Контрагент_Key"];
            JObject buyer;
            if (buyerId.Length > 0)
            {
                buyer = One(od, "Catalog_Контрагенты", buyerId, BuyerSelect + ",IsFolder,DeletionMark");
                if (buyer == null || (bool?)buyer["IsFolder"] == true || (bool?)buyer["DeletionMark"] == true) throw new AgentException(Txt.S("InvBuyerIdBad"));
            }
            else
            {
                if (buyerQ.Length < 2) throw new AgentException(Txt.S("InvNeedBuyer"));
                var found = CounterpartySearch.Find(od, buyerQ, OptionsTop + 1);
                var rows = new JArray(found.Rows);
                // Нестрогое совпадение («Ай Си Лайн» ~ «IC Line») сами не берём — человек подтверждает выбором
                buyer = found.Fuzzy ? null : Pick(rows, buyerQ);
                if (buyer == null && rows.Count == 0)
                    return NotFound("buyer", buyerQ, found.Similar.Select(r => (string)r["Description"]), Txt.S("InvBuyerNotFound"));
                if (buyer == null) return Choose("buyer", buyerQ, rows.OfType<JObject>().Take(OptionsTop).Select(BuyerOption), found.Fuzzy ? Txt.S("InvBuyerApproximate") : null);
            }
            var buyerKey = (string)buyer["Ref_Key"];

            // Фирма
            var orgQ = ((string)a["organization"] ?? "").Trim();
            if (src != null && orgQ.Length == 0 && !IsEmpty((string)src["Организация_Key"])) orgQ = (string)src["Организация_Key"];
            var orgs = od.Rows("Catalog_Организации", new Dictionary<string, string>
            {
                ["$select"] = "Ref_Key,Description,ИдентификационныйНомер,ОсновнойБанковскийСчет_Key,НомерСвидетельстваПоНДС",
                ["$filter"] = "DeletionMark eq false", ["$orderby"] = "Description",
            }).OfType<JObject>().ToList();
            if (orgQ.Length > 0) orgs = orgs.Where(o => Norm((string)o["Description"]).Contains(Norm(orgQ)) || (string)o["ИдентификационныйНомер"] == orgQ || (string)o["Ref_Key"] == orgQ).ToList();
            if (orgs.Count == 0) throw new AgentException(orgQ.Length > 0 ? Txt.F("InvOrgNotFound", orgQ) : Txt.S("InvNoOrg"));
            var exactOrg = orgs.Where(o => Norm((string)o["Description"]) == Norm(orgQ)).ToList();
            if (orgs.Count > 1 && exactOrg.Count == 1) orgs = exactOrg;
            if (orgs.Count > 1) return Choose("organization", orgQ, orgs.Take(OptionsTop).Select(o => new JObject { ["id"] = o["Ref_Key"], ["name"] = o["Description"], ["bin"] = NullIfEmpty((string)o["ИдентификационныйНомер"]) }));
            var org = orgs[0];
            var orgKey = (string)org["Ref_Key"];

            // Образец — последний счёт этой фирмы: банковский счёт, склад, валюта, НДС, КНП
            var tpl = od.Rows(DocSet, new Dictionary<string, string>
            {
                ["$filter"] = $"Организация_Key eq {Guid(orgKey)} and DeletionMark eq false",
                ["$orderby"] = "Date desc", ["$top"] = "1",
                ["$select"] = "ВалютаДокумента_Key,СтруктурнаяЕдиница,СтруктурнаяЕдиница_Type,Склад_Key,КодНазначенияПлатежа,СуммаВключаетНДС,УчитыватьНДС,КратностьВзаиморасчетов,Услуги,Товары",
            }).OfType<JObject>().FirstOrDefault();

            // Договор
            var contractQ = ((string)a["contract"] ?? "").Trim();
            var contracts = od.Rows("Catalog_ДоговорыКонтрагентов", new Dictionary<string, string>
            {
                ["$select"] = "Ref_Key,Description,НомерДоговора,ВидДоговора,ВалютаВзаиморасчетов_Key",
                ["$filter"] = $"Owner_Key eq {Guid(buyerKey)} and Организация_Key eq {Guid(orgKey)} and IsFolder eq false and DeletionMark eq false",
                ["$orderby"] = "Description",
            }).OfType<JObject>().Where(c => (string)c["ВидДоговора"] == "СПокупателем").ToList();
            // Договор образца — только при тех же покупателе и фирме и если он ещё действует; у нового покупателя — по обычным правилам
            if (src != null && contractQ.Length == 0 && (string)src["Контрагент_Key"] == buyerKey && (string)src["Организация_Key"] == orgKey
                && contracts.Any(c => (string)c["Ref_Key"] == (string)src["ДоговорКонтрагента_Key"]))
                contractQ = (string)src["ДоговорКонтрагента_Key"];
            var warnings = new JArray();
            JObject contract = null;
            if (contractQ.Length > 0)
            {
                var m = contracts.Where(c => (string)c["Ref_Key"] == contractQ || Norm((string)c["Description"]).Contains(Norm(contractQ)) || Norm((string)c["НомерДоговора"]) == Norm(contractQ)).ToList();
                if (m.Count == 0) return NotFound("contract", contractQ, contracts.Take(SimilarTop).Select(c => (string)c["Description"]), Txt.S("InvContractNotFound"));
                if (m.Count > 1) return Choose("contract", contractQ, m.Take(OptionsTop).Select(c => new JObject { ["id"] = c["Ref_Key"], ["name"] = c["Description"] }));
                contract = m[0];
            }
            else
            {
                var main = (string)buyer["ОсновнойДоговорКонтрагента_Key"];
                contract = contracts.FirstOrDefault(c => (string)c["Ref_Key"] == main) ?? (contracts.Count == 1 ? contracts[0] : null);
                if (contract == null && contracts.Count > 1) return Choose("contract", "", contracts.Take(OptionsTop).Select(c => new JObject { ["id"] = c["Ref_Key"], ["name"] = c["Description"] }));
                if (contract == null) warnings.Add(Txt.S("InvNoContract"));
            }

            // Валюта: договора, образца или тенге; валютные счета — не в этой версии
            var currencyKey = !IsEmpty((string)contract?["ВалютаВзаиморасчетов_Key"]) ? (string)contract["ВалютаВзаиморасчетов_Key"] : (string)tpl?["ВалютаДокумента_Key"];
            var cur = IsEmpty(currencyKey)
                ? od.Rows("Catalog_Валюты", new Dictionary<string, string> { ["$select"] = "Ref_Key,Code,Description", ["$filter"] = "Code eq '398'", ["$top"] = "1" }).OfType<JObject>().FirstOrDefault()
                : One(od, "Catalog_Валюты", currencyKey, "Ref_Key,Code,Description");
            if (cur == null) throw new AgentException(Txt.S("InvNoCurrency"));
            if (((string)cur["Code"] ?? "").Trim() != "398") throw new AgentException(Txt.F("InvForeignCurrency", (string)cur["Description"]));

            var vatOn = (bool?)tpl?["УчитыватьНДС"] ?? !string.IsNullOrWhiteSpace((string)org["НомерСвидетельстваПоНДС"]);
            var included = (bool?)tpl?["СуммаВключаетНДС"] ?? true;
            var tplRate = ((JArray)tpl?["Услуги"] ?? new JArray()).Concat((JArray)tpl?["Товары"] ?? new JArray()).Select(r => (string)r["СтавкаНДС_Key"]).FirstOrDefault(k => !IsEmpty(k));

            // Строки
            var (lines, stop) = ResolveLines(od, inLines, vatOn, included, tplRate, warnings, orgKey);
            if (stop != null) return stop;

            // Шапка (без даты — она ставится при создании)
            var head = new JObject
            {
                ["Организация_Key"] = orgKey, ["Контрагент_Key"] = buyerKey, ["ДоговорКонтрагента_Key"] = (string)contract?["Ref_Key"] ?? EmptyKey,
                ["ВалютаДокумента_Key"] = (string)cur["Ref_Key"], ["КурсВзаиморасчетов"] = 1, ["КратностьВзаиморасчетов"] = (string)tpl?["КратностьВзаиморасчетов"] ?? "1",
            };
            if (tpl != null && !IsEmpty((string)tpl["СтруктурнаяЕдиница"]))
            {
                head["СтруктурнаяЕдиница"] = tpl["СтруктурнаяЕдиница"];
                head["СтруктурнаяЕдиница_Type"] = tpl["СтруктурнаяЕдиница_Type"];
            }
            else if (!IsEmpty((string)org["ОсновнойБанковскийСчет_Key"]))
            {
                head["СтруктурнаяЕдиница"] = org["ОсновнойБанковскийСчет_Key"];
                head["СтруктурнаяЕдиница_Type"] = "StandardODATA.Catalog_БанковскиеСчета";
            }
            else warnings.Add(Txt.S("InvNoBankAccount"));
            if (tpl != null && !IsEmpty((string)tpl["Склад_Key"]) && lines.Any(l => !l.Service)) head["Склад_Key"] = tpl["Склад_Key"];
            if (!string.IsNullOrWhiteSpace((string)tpl?["КодНазначенияПлатежа"])) head["КодНазначенияПлатежа"] = tpl["КодНазначенияПлатежа"];
            if (tpl == null) warnings.Add(Txt.S("InvNoTemplate"));

            var body = Body(head, lines, vatOn, included);
            var total = Total(lines, included);
            var draft = Save(b.Config.Slug, OwnerOf(McpCaller.Current), body, (string)buyer["Description"], total);
            body["Комментарий"] = Txt.F("InvComment", draft.Id);
            return new JObject
            {
                ["status"] = "ready",
                ["draft_id"] = draft.Id,
                ["expires_at"] = draft.CreatedUtc.Add(DraftTtl).ToLocalTime().ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
                ["base"] = b.Config.Slug,
                ["organization"] = new JObject { ["name"] = org["Description"], ["bin"] = NullIfEmpty((string)org["ИдентификационныйНомер"]) },
                ["buyer"] = new JObject { ["name"] = buyer["Description"], ["bin"] = NullIfEmpty((string)buyer["ИдентификационныйКодЛичности"]) },
                ["contract"] = contract == null ? JValue.CreateNull() : (JToken)(string)contract["Description"],
                ["lines"] = LinesJson(lines),
                ["vat_on"] = vatOn,
                ["vat_included"] = included,
                ["vat"] = Tools.Money((double)lines.Sum(l => l.Vat)),
                ["total"] = Tools.Money((double)total),
                ["currency"] = "KZT",
                ["warnings"] = warnings,
                ["note"] = Txt.S("InvDraftNote"),
            }.Also(o => { if (srcNumber != null) o["copied_from"] = srcNumber; });
        }

        /// <summary>Строки счёта по словам человека: номенклатура, количество, цена/сумма, НДС. Не нашли или несколько — ответ choose/not_found.</summary>
        private static (List<Line> Lines, JObject Stop) ResolveLines(ODataClient od, JArray input, bool vatOn, bool included, string tplRate, JArray warnings, string orgKey)
        {
            // Строки (вход — как в draft_invoice)
            var inLines = input?.OfType<JObject>().ToList() ?? new List<JObject>();
            if (inLines.Count == 0) throw new AgentException(Txt.S("InvNeedLines"));
            if (inLines.Count > MaxLines) throw new AgentException(Txt.F("InvTooManyLines", MaxLines));
            var rates = new Dictionary<string, int?>();
            int? Rate(string k)
            {
                if (IsEmpty(k)) return null;
                if (!rates.TryGetValue(k, out var r)) rates[k] = r = (int?)One(od, "Catalog_СтавкиНДС", k, "Ставка")?["Ставка"];
                return r;
            }
            var units = new Dictionary<string, string>();
            string Unit(string k)
            {
                if (IsEmpty(k)) return "";
                if (!units.TryGetValue(k, out var u)) units[k] = u = (string)One(od, "Catalog_КлассификаторЕдиницИзмерения", k, "Description")?["Description"] ?? "";
                return u;
            }
            const string itemSelect = "Ref_Key,Code,Description,Услуга,СтавкаНДС_Key,БазоваяЕдиницаИзмерения_Key";
            ISet<string> used = null;
            ISet<string> Used() => used ?? (used = UsedItems(od, orgKey));
            var lines = new List<Line>();
            for (var i = 0; i < inLines.Count; i++)
            {
                var li = inLines[i];
                var field = $"lines[{i}]";
                var itemId = ((string)li["item_id"] ?? "").Trim();
                var rawQ = (string)li["item"] ?? "";
                var itemQ = rawQ.Trim();
                JObject item;
                if (itemId.Length > 0)
                {
                    // Позиция выбрана по id из options или взята из образца — по названию не ищем
                    item = One(od, "Catalog_Номенклатура", itemId, itemSelect + ",IsFolder,DeletionMark");
                    if (item == null || (bool?)item["IsFolder"] == true || (bool?)item["DeletionMark"] == true) throw new AgentException(Txt.F("InvItemIdBad", i + 1));
                }
                else
                {
                    if (itemQ.Length < 2) throw new AgentException(Txt.F("InvNeedItem", i + 1));
                    var found = od.Rows("Catalog_Номенклатура", new Dictionary<string, string>
                    {
                        ["$select"] = itemSelect,
                        ["$filter"] = $"substringof({ODataClient.Lit(itemQ)}, Description) and IsFolder eq false and DeletionMark eq false",
                        ["$orderby"] = "Description", ["$top"] = (OptionsTop + 1).ToString(CultureInfo.InvariantCulture),
                    });
                    if (found.Count == 0)
                    {
                        var sim = Similar(od, "Catalog_Номенклатура", itemQ, "", itemSelect, "Услуга desc,Description").OfType<JObject>().Select(r => (string)r["Description"]);
                        return (null, NotFound(field, itemQ, sim, AllowContent ? Txt.S("InvItemNotFoundContent") : Txt.S("InvItemNotFound")));
                    }
                    var (pick, dup) = PickItem(found, rawQ, Used);
                    if (pick == null) return (null, Choose(field, itemQ, found.OfType<JObject>().Take(OptionsTop).Select(r => new JObject
                        { ["id"] = r["Ref_Key"], ["code"] = NullIfEmpty((string)r["Code"]), ["name"] = r["Description"], ["service"] = r["Услуга"] }), Txt.S("InvChooseItemNote")));
                    item = pick;
                    if (dup) warnings.Add(Txt.F("InvItemDuplicates", ((string)item["Description"] ?? "").Trim(), ((string)item["Code"] ?? "").Trim()));
                }
                var (q, p, s) = Amounts(Dec(li["quantity"]), Dec(li["price"]), Dec(li["sum"]));
                var service = (bool?)item["Услуга"] == true;
                var content = ((string)li["content"] ?? "").Trim();
                if (content.Length > 0 && (!AllowContent || !service)) { warnings.Add(Txt.F(service ? "InvContentOff" : "InvContentGoods", i + 1)); content = ""; }
                if (content.Length > 1000) content = content.Substring(0, 1000);
                var rateKey = !IsEmpty((string)item["СтавкаНДС_Key"]) ? (string)item["СтавкаНДС_Key"] : tplRate;
                var rate = Rate(rateKey);
                lines.Add(new Line
                {
                    ItemKey = (string)item["Ref_Key"], ItemName = (string)item["Description"], Service = service, Content = content.Length > 0 ? content : null,
                    UnitKey = (string)item["БазоваяЕдиницаИзмерения_Key"], Unit = Unit((string)item["БазоваяЕдиницаИзмерения_Key"]),
                    Qty = q, Price = p, Sum = s, VatRateKey = IsEmpty(rateKey) ? null : rateKey, VatRate = rate, Vat = Vat(s, rate, vatOn, included),
                });
            }
            return (lines, null);
        }

        private static decimal? Dec(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return (decimal)(double)t;
            var s = ((string)t ?? "").Replace(" ", "").Replace(" ", "").Replace(',', '.');
            if (s.Length == 0) return null;
            if (!decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v)) throw new AgentException(Txt.F("InvBadNumber", (string)t));
            return v;
        }

        // ---------- create_invoice ----------

        public static JObject Create(IReadOnlyList<BaseRuntime> all, JObject a)
        {
            var caller = McpCaller.Current;
            var d = Take((string)a["draft_id"], OwnerOf(caller));
            var b = all.FirstOrDefault(x => string.Equals(x.Config.Slug, d.Slug, StringComparison.OrdinalIgnoreCase))
                ?? throw new AgentException(Txt.F("NoSuchBase", d.Slug));
            if (!Enabled(b.Config)) throw new AgentException(Txt.F("InvWriteOff", b.Config.Slug), Txt.S("InvHintWriteOff"));
            var od = b.OData;
            od.Check(DocSet);
            var body = (JObject)d.Body.DeepClone();
            body["Date"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            // Единственная запись в 1С во всём агенте: POST нового счёта (ни PATCH, ни DELETE)
            var r = od.Send("POST", $"{od.BaseUrl}/{DocSet}?$format=json", body.ToString(Formatting.None));
            var title = Txt.F("InvAuditDraft", d.Id, b.Config.Slug);
            if (!r.Ok)
            {
                var why = ODataClient.ErrorText(r.Body);
                Audit.Write(Audit.CreateFailed, b.Config.Slug, Mail.MailService.DocInvoice, title, null, null, caller: caller,
                    extra: new JObject { ["draft_id"] = d.Id, ["sum"] = d.Total, ["http"] = r.Code, ["error"] = why });
                throw new AgentException(Txt.F("InvCreateFailed", r.Code, why), Txt.S("InvHintNewDraft"));
            }
            var doc = JObject.Parse(r.Body);
            var number = Print.InvoiceSource.PrintNumber((string)doc["Number"]);
            var date = (DateTime)doc["Date"];
            var total = doc["СуммаДокумента"] != null ? (decimal)(double)doc["СуммаДокумента"] : d.Total;
            Audit.Write(Audit.Create, b.Config.Slug, Mail.MailService.DocInvoice,
                Txt.F("MailDocInvoice", number, date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), b.Config.Slug), null, null, caller: caller,
                extra: new JObject { ["draft_id"] = d.Id, ["number"] = number, ["ref"] = doc["Ref_Key"], ["version"] = doc["DataVersion"], ["sum"] = total, ["buyer"] = d.BuyerName });
            return new JObject
            {
                ["base"] = b.Config.Slug,
                ["number"] = number,
                ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["buyer"] = d.BuyerName,
                ["total"] = Tools.Money((double)total),
                ["currency"] = "KZT",
                ["note"] = Txt.S("InvCreatedNote"),
            };
        }

        internal static JArray LinesJson(IEnumerable<Line> lines) => new JArray(lines.Select((l, i) => new JObject
        {
            ["n"] = i + 1, ["item"] = l.ItemName, ["content"] = NullIfEmpty(l.Content), ["quantity"] = Tools.Money((double)l.Qty), ["unit"] = NullIfEmpty(l.Unit),
            ["price"] = Tools.Money((double)l.Price), ["sum"] = Tools.Money((double)l.Sum),
            ["vat_rate"] = l.VatRate == null ? JValue.CreateNull() : (JToken)l.VatRate, ["vat"] = Tools.Money((double)l.Vat),
        }));

        // ---------- update_invoice / cancel_invoice: только свои счета ----------

        /// <summary>Свой счёт по журналу создания: ref, номер, версия данных после последней записи Bilem, когда создан.</summary>
        internal sealed class Own
        {
            public string Ref, Number, Version, Buyer;
            public DateTime Created;
        }

        /// <summary>Владелец записи журнала — так же, как OwnerOf для собеседника.</summary>
        internal static string OwnerOfEntry(JObject requestedBy)
        {
            if (requestedBy == null) return "local";
            if (!string.IsNullOrEmpty((string)requestedBy["cloud_user"])) return "cloud:" + (string)requestedBy["cloud_channel"] + ":" + (string)requestedBy["cloud_user"];
            if ((string)requestedBy["client"] == "bilem-agent") return "local";
            return "local:" + ((string)requestedBy["tailscale_login"] ?? (string)requestedBy["client"] ?? "");
        }

        /// <summary>
        /// Найти свой счёт в журнале аудита (этот и прошлый месяц): создан Bilem в этой базе тем же собеседником не раньше чем OwnWindow назад.
        /// Версия — из последней записи Bilem по этому счёту (создание или исправление); пометка на удаление закрывает счёт.
        /// </summary>
        internal static Own FindOwn(IEnumerable<string> journalLines, string slug, string number, string owner, DateTime now)
        {
            var want = Print.InvoiceSource.PrintNumber((number ?? "").Trim().TrimStart('№', ' '));
            Own own = null;
            var marked = false;
            foreach (var line in journalLines)
            {
                JObject o;
                try { o = JsonConvert.DeserializeObject<JObject>(line, new JsonSerializerSettings { DateParseHandling = DateParseHandling.None }); } catch { continue; }
                if (o == null) continue;
                if (!string.Equals((string)o["base"], slug, StringComparison.OrdinalIgnoreCase) || !(o["created"] is JObject c)) continue;
                var action = (string)o["action"];
                if (action == Audit.Create && Print.InvoiceSource.PrintNumber((string)c["number"]) == want && OwnerOfEntry((JObject)o["requested_by"]) == owner)
                    own = new Own { Ref = (string)c["ref"], Number = (string)c["number"], Version = (string)c["version"], Buyer = (string)c["buyer"],
                        Created = DateTimeOffset.Parse((string)o["time"], CultureInfo.InvariantCulture).UtcDateTime };
                else if (own != null && (string)c["ref"] == own.Ref && action == Audit.InvoiceUpdate) own.Version = (string)c["version"];
                else if (own != null && (string)c["ref"] == own.Ref && action == Audit.InvoiceCancel) { own = null; marked = true; }
            }
            if (own == null && marked) throw new AgentException(Txt.F("InvOwnMarked", number));
            if (own == null) throw new AgentException(Txt.F("InvNotOwn", number), Txt.S("InvHintNotOwn"));
            if (now - own.Created > OwnWindow) throw new AgentException(Txt.F("InvOwnTooOld", number, (int)OwnWindow.TotalHours), Txt.S("InvHintNotOwn"));
            return own;
        }

        private static IEnumerable<string> JournalLines(DateTime now)
        {
            foreach (var f in new[] { Audit.FileFor(now.AddMonths(-1)), Audit.FileFor(now) }.Distinct())
            {
                if (!System.IO.File.Exists(f)) continue;
                string[] lines;
                using (var s = new System.IO.FileStream(f, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                using (var r = new System.IO.StreamReader(s, System.Text.Encoding.UTF8))
                    lines = r.ReadToEnd().Split('\n');
                foreach (var l in lines) if (l.Trim().Length > 0) yield return l;
            }
        }

        private const string DocSelect = "Ref_Key,DataVersion,DeletionMark,Number,Date,Организация_Key,Контрагент_Key,ДоговорКонтрагента_Key,СуммаВключаетНДС,УчитыватьНДС,СуммаДокумента,Товары,Услуги";

        /// <summary>Счёт из 1С для своего: на месте, не помечен, версия та же, что после записи Bilem — иначе его меняли руками.</summary>
        private static JObject OwnDoc(ODataClient od, Own own)
        {
            var doc = One(od, DocSet, own.Ref, DocSelect) ?? throw new AgentException(Txt.F("InvOwnGone", own.Number));
            if ((bool?)doc["DeletionMark"] == true) throw new AgentException(Txt.F("InvOwnMarked", own.Number));
            if (!string.IsNullOrEmpty(own.Version) && (string)doc["DataVersion"] != own.Version) throw new AgentException(Txt.F("InvOwnChanged", own.Number), Txt.S("InvHintOwnChanged"));
            return doc;
        }

        /// <summary>Строки счёта из 1С — для «было».</summary>
        private static List<Line> DocLines(ODataClient od, JObject doc)
        {
            var res = new List<Line>();
            var rates = new Dictionary<string, int?>();
            foreach (var (part, service) in new[] { ("Товары", false), ("Услуги", true) })
                foreach (JObject r in (JArray)doc[part] ?? new JArray())
                {
                    var nom = One(od, "Catalog_Номенклатура", (string)r["Номенклатура_Key"], "Description");
                    var rk = (string)r["СтавкаНДС_Key"];
                    if (!IsEmpty(rk) && !rates.ContainsKey(rk)) rates[rk] = (int?)One(od, "Catalog_СтавкиНДС", rk, "Ставка")?["Ставка"];
                    res.Add(new Line
                    {
                        ItemName = (string)nom?["Description"] ?? "", Service = service, Content = service ? NullIfEmpty((string)r["Содержание"])?.ToString() : null,
                        Qty = Dc(r["Количество"]), Price = Dc(r["Цена"]), Sum = Dc(r["Сумма"]), Vat = Dc(r["СуммаНДС"]), VatRate = IsEmpty(rk) ? null : rates[rk],
                    });
                }
            return res;
        }

        private static decimal Dc(JToken t) => t == null || t.Type == JTokenType.Null ? 0m : (decimal)(double)t;

        private static JObject Side(IList<Line> lines, decimal total) => new JObject
            { ["lines"] = LinesJson(lines), ["vat"] = Tools.Money((double)lines.Sum(l => l.Vat)), ["total"] = Tools.Money((double)total) };

        public static JObject Update(IReadOnlyList<BaseRuntime> all, JObject a)
        {
            if (!string.IsNullOrWhiteSpace((string)a["draft_id"])) return Apply(all, (string)a["draft_id"], KindUpdate);
            var b = Tools.OneBase(all, (string)a["base"]);
            if (!Enabled(b.Config)) throw new AgentException(Txt.F("InvWriteOff", b.Config.Slug), Txt.S("InvHintWriteOff"));
            var number = ((string)a["number"] ?? "").Trim();
            if (number.Length == 0) throw new AgentException(Txt.S("InvNeedNumber"));
            var owner = OwnerOf(McpCaller.Current);
            var own = FindOwn(JournalLines(DateTime.Now), b.Config.Slug, number, owner, UtcNow());
            var od = b.OData;
            var doc = OwnDoc(od, own);
            if (a["lines"] == null && a["contract"] == null) throw new AgentException(Txt.S("InvNothingToChange"));
            var vatOn = (bool?)doc["УчитыватьНДС"] ?? true;
            var included = (bool?)doc["СуммаВключаетНДС"] ?? true;
            var before = DocLines(od, doc);
            var warnings = new JArray();
            var patch = new JObject();
            var changes = new JArray();
            var after = before;
            if (a["lines"] != null)
            {
                var docRate = ((JArray)doc["Услуги"] ?? new JArray()).Concat((JArray)doc["Товары"] ?? new JArray()).Select(r => (string)r["СтавкаНДС_Key"]).FirstOrDefault(k => !IsEmpty(k));
                var (lines, stop) = ResolveLines(od, a["lines"] as JArray, vatOn, included, docRate, warnings, (string)doc["Организация_Key"]);
                if (stop != null) return stop;
                var body = Body(new JObject(), lines, vatOn, included);
                patch["Товары"] = body["Товары"]; patch["Услуги"] = body["Услуги"]; patch["СуммаДокумента"] = body["СуммаДокумента"];
                after = lines;
                changes.Add(Txt.S("InvChangeLines"));
            }
            var contractQ = ((string)a["contract"] ?? "").Trim();
            JToken contractName = null;
            if (contractQ.Length > 0)
            {
                var contracts = od.Rows("Catalog_ДоговорыКонтрагентов", new Dictionary<string, string>
                {
                    ["$select"] = "Ref_Key,Description,НомерДоговора,ВидДоговора",
                    ["$filter"] = $"Owner_Key eq {Guid((string)doc["Контрагент_Key"])} and Организация_Key eq {Guid((string)doc["Организация_Key"])} and IsFolder eq false and DeletionMark eq false",
                }).OfType<JObject>().Where(c => (string)c["ВидДоговора"] == "СПокупателем").ToList();
                var m = contracts.Where(c => (string)c["Ref_Key"] == contractQ || Norm((string)c["Description"]).Contains(Norm(contractQ)) || Norm((string)c["НомерДоговора"]) == Norm(contractQ)).ToList();
                if (m.Count == 0) return NotFound("contract", contractQ, contracts.Take(SimilarTop).Select(c => (string)c["Description"]), Txt.S("InvContractNotFound"));
                if (m.Count > 1) return Choose("contract", contractQ, m.Take(OptionsTop).Select(c => new JObject { ["id"] = c["Ref_Key"], ["name"] = c["Description"] }));
                patch["ДоговорКонтрагента_Key"] = m[0]["Ref_Key"];
                contractName = m[0]["Description"];
                changes.Add(Txt.F("InvChangeContract", (string)contractName));
            }
            var totalBefore = Dc(doc["СуммаДокумента"]);
            var totalAfter = patch["СуммаДокумента"] != null ? (decimal)patch["СуммаДокумента"] : totalBefore;
            var d = Save(b.Config.Slug, owner, patch, own.Buyer, totalAfter);
            d.Kind = KindUpdate; d.Ref = own.Ref; d.Version = (string)doc["DataVersion"]; d.Number = own.Number;
            return new JObject
            {
                ["status"] = "ready", ["draft_id"] = d.Id,
                ["expires_at"] = d.CreatedUtc.Add(DraftTtl).ToLocalTime().ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
                ["base"] = b.Config.Slug, ["number"] = Print.InvoiceSource.PrintNumber(own.Number), ["buyer"] = own.Buyer,
                ["before"] = Side(before, totalBefore), ["after"] = Side(after, totalAfter).Also(o => { if (contractName != null) o["contract"] = contractName; }),
                ["changes"] = changes, ["vat_included"] = included, ["currency"] = "KZT", ["warnings"] = warnings,
                ["note"] = Txt.S("InvUpdateDraftNote"),
            };
        }

        public static JObject Cancel(IReadOnlyList<BaseRuntime> all, JObject a)
        {
            if (!string.IsNullOrWhiteSpace((string)a["draft_id"])) return Apply(all, (string)a["draft_id"], KindCancel);
            var b = Tools.OneBase(all, (string)a["base"]);
            if (!Enabled(b.Config)) throw new AgentException(Txt.F("InvWriteOff", b.Config.Slug), Txt.S("InvHintWriteOff"));
            var number = ((string)a["number"] ?? "").Trim();
            if (number.Length == 0) throw new AgentException(Txt.S("InvNeedNumber"));
            var owner = OwnerOf(McpCaller.Current);
            var own = FindOwn(JournalLines(DateTime.Now), b.Config.Slug, number, owner, UtcNow());
            var doc = OwnDoc(b.OData, own);
            var total = Dc(doc["СуммаДокумента"]);
            var d = Save(b.Config.Slug, owner, new JObject { ["DeletionMark"] = true }, own.Buyer, total);
            d.Kind = KindCancel; d.Ref = own.Ref; d.Version = (string)doc["DataVersion"]; d.Number = own.Number;
            return new JObject
            {
                ["status"] = "ready", ["draft_id"] = d.Id,
                ["expires_at"] = d.CreatedUtc.Add(DraftTtl).ToLocalTime().ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
                ["base"] = b.Config.Slug, ["number"] = Print.InvoiceSource.PrintNumber(own.Number),
                ["date"] = ((DateTime)doc["Date"]).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["buyer"] = own.Buyer, ["total"] = Tools.Money((double)total), ["currency"] = "KZT",
                ["note"] = Txt.S("InvCancelDraftNote"),
            };
        }

        /// <summary>Исправление или пометка своего счёта по подтверждённому черновику: версия та же — один PATCH этого счёта.</summary>
        private static JObject Apply(IReadOnlyList<BaseRuntime> all, string draftId, string kind)
        {
            var caller = McpCaller.Current;
            var d = Take(draftId, OwnerOf(caller), kind);
            var b = all.FirstOrDefault(x => string.Equals(x.Config.Slug, d.Slug, StringComparison.OrdinalIgnoreCase))
                ?? throw new AgentException(Txt.F("NoSuchBase", d.Slug));
            if (!Enabled(b.Config)) throw new AgentException(Txt.F("InvWriteOff", b.Config.Slug), Txt.S("InvHintWriteOff"));
            var od = b.OData;
            var now = One(od, DocSet, d.Ref, "DataVersion,DeletionMark") ?? throw new AgentException(Txt.F("InvOwnGone", d.Number));
            if ((string)now["DataVersion"] != d.Version) throw new AgentException(Txt.F("InvOwnChanged", d.Number), Txt.S("InvHintOwnChanged"));
            if ((bool?)now["DeletionMark"] == true) throw new AgentException(Txt.F("InvOwnMarked", d.Number));
            // Только этот счёт и только поля черновика: строки/сумма/договор или пометка на удаление
            var r = od.Send("PATCH", $"{od.BaseUrl}/{DocSet}({Guid(d.Ref)})?$format=json", d.Body.ToString(Formatting.None));
            var action = kind == KindCancel ? Audit.InvoiceCancel : Audit.InvoiceUpdate;
            var number = Print.InvoiceSource.PrintNumber(d.Number);
            if (!r.Ok)
            {
                var why = ODataClient.ErrorText(r.Body);
                Audit.Write(Audit.CreateFailed, b.Config.Slug, Mail.MailService.DocInvoice, Txt.F("InvAuditDraft", d.Id, b.Config.Slug), null, null, caller: caller,
                    extra: new JObject { ["draft_id"] = d.Id, ["ref"] = d.Ref, ["kind"] = kind, ["http"] = r.Code, ["error"] = why });
                throw new AgentException(Txt.F(kind == KindCancel ? "InvCancelFailed" : "InvUpdateFailed", r.Code, why), Txt.S("InvHintNewDraft"));
            }
            var doc = r.Body.Trim().Length > 0 ? JObject.Parse(r.Body) : One(od, DocSet, d.Ref, DocSelect);
            var date = (DateTime)doc["Date"];
            var total = doc["СуммаДокумента"] != null ? Dc(doc["СуммаДокумента"]) : d.Total;
            Audit.Write(action, b.Config.Slug, Mail.MailService.DocInvoice,
                Txt.F("MailDocInvoice", number, date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), b.Config.Slug), null, null, caller: caller,
                extra: new JObject { ["draft_id"] = d.Id, ["number"] = d.Number, ["ref"] = d.Ref, ["version"] = doc["DataVersion"], ["sum"] = total, ["buyer"] = d.BuyerName });
            return new JObject
            {
                ["base"] = b.Config.Slug, ["number"] = number, ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["buyer"] = d.BuyerName, ["total"] = Tools.Money((double)total), ["currency"] = "KZT",
                ["note"] = Txt.S(kind == KindCancel ? "InvCancelledNote" : "InvUpdatedNote"),
            };
        }

        private static JObject Also(this JObject o, Action<JObject> a) { a(o); return o; }
    }
}
