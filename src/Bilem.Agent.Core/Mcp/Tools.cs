using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Mcp
{
    /// <summary>Подключённая база во время работы MCP: OData-клиент с паролем bilem, профиль конфигурации, кэш справочников.</summary>
    public sealed class BaseRuntime
    {
        public BaseConfig Config { get; }
        public ConfigProfile Profile { get; }
        public ODataClient OData { get; }
        private readonly Dictionary<string, (DateTime At, object Value)> _cache = new Dictionary<string, (DateTime, object)>();
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

        public BaseRuntime(BaseConfig cfg, string password)
        {
            Config = cfg;
            Profile = Profiles.For(cfg.Configuration);
            OData = new ODataClient(cfg.OData, cfg.User, password);
            var allowed = new HashSet<string>(cfg.ReadEffective.Select(ODataClient.SetName));
            var cat = Access.CatalogOf(cfg);
            var titles = cat.Items.ToDictionary(i => ODataClient.SetName(i.FullName), i => i.Title);
            OData.Deny = set => allowed.Contains(set) ? null
                : Txt.F("ToolObjectOff", titles.TryGetValue(set, out var t) ? t : set, cfg.Slug);
        }

        public static BaseRuntime FromConfig(BaseConfig cfg) => new BaseRuntime(cfg, Secrets.ReadSecretFile(cfg.SecretFile));

        /// <summary>Справочники, которые меняются редко (организации, план счетов), — кэш на 10 минут.</summary>
        public T Cached<T>(string key, Func<T> load)
        {
            lock (_cache)
            {
                if (_cache.TryGetValue(key, out var c) && DateTime.UtcNow - c.At < CacheTtl) return (T)c.Value;
            }
            var v = load();
            lock (_cache) _cache[key] = (DateTime.UtcNow, v);
            return v;
        }
    }

    /// <summary>
    /// Инструменты MCP (только чтение) — перенос прототипа захода 4 с тем же форматом ответа.
    /// Новое: если баз несколько и base не указан — ответ по всем базам сразу, у каждой строки — подпись базы.
    /// </summary>
    public static class Tools
    {
        public const int DefaultMaxRows = 200;

        /// <summary>Выписка старше стольких рабочих дней — предупреждение о давности (вкладка «MCP»).</summary>
        public static int StaleWorkdays { get; set; } = 3;

        public static List<McpTool> Create(Func<IReadOnlyList<BaseRuntime>> bases, int maxRows = DefaultMaxRows)
        {
            var tools = CreateRaw(bases);
            // Защитный максимум строк в ответе любого инструмента — обрезка с пометкой «показаны первые N из M»
            foreach (var t in tools) { var h = t.Handler; t.Handler = a => Limit(h(a), maxRows); }
            return tools;
        }

        /// <summary>Массивы ответа (bases, organizations, items — и вложенные) не длиннее max; обрезали — пометка note.</summary>
        internal static JObject Limit(JObject o, int max)
        {
            if (o == null) return o;
            foreach (var p in o.Properties().Where(p => p.Name.StartsWith("_", StringComparison.Ordinal)).ToList()) p.Remove();
            if (max <= 0) return o;
            foreach (var key in new[] { "bases", "organizations", "items" })
            {
                if (!(o[key] is JArray arr)) continue;
                foreach (var child in arr.OfType<JObject>()) Limit(child, max);
                if (arr.Count <= max) continue;
                var total = arr.Count;
                while (arr.Count > max) arr.RemoveAt(arr.Count - 1);
                if (o["note"] == null) o["note"] = Txt.F("ToolTruncated", max, total);
                if (key == "items" && o["found"] != null) o["found"] = max;
            }
            return o;
        }

        private static List<McpTool> CreateRaw(Func<IReadOnlyList<BaseRuntime>> bases)
        {
            var baseProp = new JObject { ["type"] = "string", ["description"] = Txt.S("ToolParamBase") };
            return new List<McpTool>
            {
                new McpTool
                {
                    Name = "list_bases", Title = Txt.S("ToolListBasesTitle"), Description = Txt.S("ToolListBasesDesc"),
                    InputSchema = Schema(new JObject()),
                    Handler = a => new JObject { ["bases"] = new JArray(bases().Select(b => new JObject
                        { ["base"] = b.Config.Slug, ["configuration"] = b.Config.DisplayName, ["version"] = b.Config.Version })) },
                },
                new McpTool
                {
                    Name = "whoami", Title = Txt.S("ToolWhoamiTitle"), Description = Txt.S("ToolWhoamiDesc"),
                    InputSchema = Schema(new JObject { ["base"] = baseProp }),
                    Handler = a => PerBase(bases(), (string)a["base"], Whoami, rows => new JObject { ["bases"] = new JArray(rows) }),
                },
                new McpTool
                {
                    Name = "find_counterparty", Title = Txt.S("ToolFindTitle"), Description = Txt.S("ToolFindDesc"),
                    InputSchema = Schema(new JObject
                    {
                        ["query"] = new JObject { ["type"] = "string", ["minLength"] = 2, ["description"] = Txt.S("ToolFindParamQuery") },
                        ["base"] = baseProp,
                    }, "query"),
                    Handler = a =>
                    {
                        var q = ((string)a["query"] ?? "").Trim();
                        if (q.Length < 2) throw new AgentException(Txt.S("ToolFindTooShort"));
                        return PerBase(bases(), (string)a["base"], b => Find(b, q), rows => MergeFind(rows));
                    },
                },
                new McpTool
                {
                    Name = "cash_balance", Title = Txt.S("ToolCashTitle"), Description = Txt.S("ToolCashDesc") + Txt.S("ToolCashDescExtra"),
                    InputSchema = Schema(new JObject
                    {
                        ["date"] = new JObject { ["type"] = "string", ["pattern"] = @"^\d{4}-\d{2}-\d{2}$", ["description"] = Txt.S("ToolCashParamDate") },
                        ["base"] = baseProp,
                    }),
                    Handler = a =>
                    {
                        var d = (string)a["date"];
                        if (string.IsNullOrEmpty(d)) d = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        else if (!Regex.IsMatch(d, @"^\d{4}-\d{2}-\d{2}$") || !DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                            throw new AgentException(Txt.S("ToolCashBadDate"));
                        return PerBase(bases(), (string)a["base"], b => Cash(b, d), rows => MergeCash(rows, d));
                    },
                },
                new McpTool
                {
                    Name = "print_invoice", Title = Txt.S("ToolPrintTitle"), Description = Txt.S("ToolPrintDesc"),
                    InputSchema = Schema(new JObject
                    {
                        ["number"] = new JObject { ["type"] = "string", ["description"] = Txt.S("ToolPrintParamNumber") },
                        ["last"] = new JObject { ["type"] = "boolean", ["description"] = Txt.S("ToolPrintParamLast") },
                        ["counterparty"] = new JObject { ["type"] = "string", ["description"] = Txt.S("ToolPrintParamCounterparty") },
                        ["period"] = new JObject { ["type"] = "string", ["description"] = Txt.S("ToolPrintParamPeriod") },
                        ["base"] = baseProp,
                    }),
                    Handler = a =>
                    {
                        var number = (string)a["number"]; var last = (bool?)a["last"] ?? false;
                        // (печать или поиск — см. описание инструмента)
                        var cp = (string)a["counterparty"]; var period = (string)a["period"];
                        // Без номера и «последнего» — поиск: список счетов без печати
                        if (string.IsNullOrWhiteSpace(number) && !last)
                        {
                            if (string.IsNullOrWhiteSpace(cp) && string.IsNullOrWhiteSpace(period)) throw new AgentException(Txt.S("ToolPrintNeedNumber"));
                            return PerBase(bases(), (string)a["base"], b => SearchInvoices(b, cp, period), rows => MergeFind(rows));
                        }
                        return PrintInvoice(bases(), (string)a["base"], number, last, cp);
                    },
                },
                new McpTool
                {
                    Name = "print_power_of_attorney", Title = Txt.S("ToolPoaTitle"), Description = Txt.S("ToolPoaDesc"),
                    InputSchema = Schema(new JObject
                    {
                        ["number"] = new JObject { ["type"] = "string", ["description"] = Txt.S("ToolPoaParamNumber") },
                        ["last"] = new JObject { ["type"] = "boolean", ["description"] = Txt.S("ToolPoaParamLast") },
                        ["employee"] = new JObject { ["type"] = "string", ["description"] = Txt.S("ToolPoaParamEmployee") },
                        ["period"] = new JObject { ["type"] = "string", ["description"] = Txt.S("ToolPrintParamPeriod") },
                        ["base"] = baseProp,
                    }),
                    Handler = a =>
                    {
                        var number = (string)a["number"]; var last = (bool?)a["last"] ?? false;
                        var emp = (string)a["employee"]; var period = (string)a["period"];
                        // Без номера и «последней» — поиск: список доверенностей без печати
                        if (string.IsNullOrWhiteSpace(number) && !last)
                        {
                            if (string.IsNullOrWhiteSpace(emp) && string.IsNullOrWhiteSpace(period)) throw new AgentException(Txt.S("ToolPoaNeedNumber"));
                            return PerBase(bases(), (string)a["base"], b => SearchPoa(b, emp, period), rows => MergeFind(rows));
                        }
                        return PrintPoa(OneBase(bases(), (string)a["base"]), number, last, emp);
                    },
                },
                new McpTool
                {
                    Name = "print_reconciliation", Title = Txt.S("ToolActTitle"), Description = Txt.S("ToolActDesc"),
                    InputSchema = Schema(new JObject
                    {
                        ["counterparty"] = new JObject { ["type"] = "string", ["description"] = Txt.S("ToolActParamCounterparty") },
                        ["period_from"] = new JObject { ["type"] = "string", ["pattern"] = @"^\d{4}-\d{2}-\d{2}$", ["description"] = Txt.S("ToolActParamFrom") },
                        ["period_to"] = new JObject { ["type"] = "string", ["pattern"] = @"^\d{4}-\d{2}-\d{2}$", ["description"] = Txt.S("ToolActParamTo") },
                        ["contract"] = new JObject { ["type"] = "string", ["description"] = Txt.S("ToolActParamContract") },
                        ["organization"] = new JObject { ["type"] = "string", ["description"] = Txt.S("ToolActParamOrganization") },
                        ["base"] = baseProp,
                    }, "counterparty"),
                    Handler = a => PrintAct(OneBase(bases(), (string)a["base"]), a),
                },
                DebtTool("receivables", DebtKind.Receivables, bases, baseProp),
                DebtTool("payables", DebtKind.Payables, bases, baseProp),
                new McpTool
                {
                    Name = "send_document", Title = Txt.S("ToolSendTitle"), Description = Txt.S("ToolSendDesc"),
                    InputSchema = Schema(new JObject
                    {
                        ["document"] = new JObject { ["type"] = "string", ["enum"] = new JArray("invoice", "reconciliation", "power_of_attorney"), ["description"] = Txt.S("ToolSendParamDocument") },
                        ["number"] = new JObject { ["type"] = "string", ["description"] = Txt.S("ToolPrintParamNumber") },
                        ["last"] = new JObject { ["type"] = "boolean", ["description"] = Txt.S("ToolPrintParamLast") },
                        ["counterparty"] = new JObject { ["type"] = "string", ["description"] = Txt.S("ToolSendParamCounterparty") },
                        ["employee"] = new JObject { ["type"] = "string", ["description"] = Txt.S("ToolPoaParamEmployee") },
                        ["period_from"] = new JObject { ["type"] = "string", ["description"] = Txt.S("ToolActParamFrom") },
                        ["period_to"] = new JObject { ["type"] = "string", ["description"] = Txt.S("ToolActParamTo") },
                        ["contract"] = new JObject { ["type"] = "string", ["description"] = Txt.S("ToolActParamContract") },
                        ["organization"] = new JObject { ["type"] = "string", ["description"] = Txt.S("ToolActParamOrganization") },
                        ["to"] = new JObject { ["type"] = "string", ["description"] = Txt.S("ToolSendParamTo") },
                        ["note"] = new JObject { ["type"] = "string", ["maxLength"] = 300, ["description"] = Txt.S("ToolSendParamNote") },
                        ["base"] = baseProp,
                    }, "document"),
                    Handler = a => SendDocument(bases(), a),
                },
            };
        }

        // ---------- receivables / payables ----------

        /// <summary>«Кто нам должен» / «кому должны мы» — одна схема параметров, зеркальные счета (часть G).</summary>
        private static McpTool DebtTool(string name, DebtKind kind, Func<IReadOnlyList<BaseRuntime>> bases, JObject baseProp)
        {
            var p = kind == DebtKind.Receivables ? "ToolRecv" : "ToolPay";
            var date = new JObject { ["type"] = "string", ["pattern"] = @"^\d{4}-\d{2}-\d{2}$" };
            return new McpTool
            {
                Name = name, Title = Txt.S(p + "Title"), Description = Txt.S(p + "Desc") + Txt.S("ToolDebtDescCommon"),
                InputSchema = Schema(new JObject
                {
                    ["as_of_date"] = new JObject(date) { ["description"] = Txt.S("ToolDebtParamAsOf") },
                    ["min_days"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = Txt.S("ToolDebtParamMinDays") },
                    ["issued_before"] = new JObject(date) { ["description"] = Txt.S("ToolDebtParamIssuedBefore") },
                    ["min_amount"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["description"] = Txt.S("ToolDebtParamMinAmount") },
                    ["counterparty"] = new JObject { ["type"] = "string", ["description"] = Txt.S("ToolDebtParamCounterparty") },
                    ["organization"] = new JObject { ["type"] = "string", ["description"] = Txt.S("ToolActParamOrganization") },
                    ["top"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 200, ["description"] = Txt.S("ToolDebtParamTop") },
                    ["base"] = baseProp,
                }),
                Handler = a => PerBase(bases(), (string)a["base"], b => Debt(b, kind, a), rows => MergeDebt(rows, kind)),
            };
        }

        private static DateTime? ParseDate(JObject a, string name)
        {
            var s = ((string)a[name] ?? "").Trim();
            if (s.Length == 0) return null;
            if (!DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) throw new AgentException(Txt.F("ActBadDate", s));
            return d;
        }

        internal static JObject Debt(BaseRuntime b, DebtKind kind, JObject a)
        {
            var q = new Debts.Request
            {
                Kind = kind,
                AsOf = ParseDate(a, "as_of_date") ?? DateTime.Today,
                IssuedBefore = ParseDate(a, "issued_before"),
                MinDays = Math.Max(0, (int?)a["min_days"] ?? 0),
                MinAmount = Math.Max(0m, (decimal?)a["min_amount"] ?? 0m),
                Counterparty = (string)a["counterparty"],
                Organization = (string)a["organization"],
                Top = Math.Min(200, Math.Max(1, (int?)a["top"] ?? 20)),
            };
            var r = Debts.Load(b, q);
            return Debts.ToJson(b, q, r, Freshness.AsOf(b, StaleWorkdays));
        }

        private static JObject MergeDebt(List<JObject> rows, DebtKind kind)
        {
            var key = kind == DebtKind.Receivables ? "debtors" : "creditors";
            var ok = rows.Where(r => r["error"] == null).ToList();
            var errors = new JArray(rows.Where(r => r["error"] != null));
            var res = new JObject { ["kind"] = kind == DebtKind.Receivables ? "receivables" : "payables", ["currency"] = "KZT", ["bases"] = new JArray(ok) };
            if (errors.Count > 0) res["errors"] = errors;
            return res;
        }

        private static JObject Schema(JObject props, params string[] required)
        {
            var s = new JObject { ["type"] = "object", ["properties"] = props };
            if (required.Length > 0) s["required"] = new JArray(required);
            return s;
        }

        /// <summary>base указан — ответ по одной базе (формат прототипа); не указан и база одна — так же; несколько — по всем.</summary>
        internal static JObject PerBase(IReadOnlyList<BaseRuntime> all, string slug, Func<BaseRuntime, JObject> one, Func<List<JObject>, JObject> merge)
        {
            if (!string.IsNullOrEmpty(slug))
            {
                var b = all.FirstOrDefault(x => string.Equals(x.Config.Slug, slug, StringComparison.OrdinalIgnoreCase));
                if (b == null) throw new AgentException(Txt.F("ToolNoSuchBase", slug, all.Count == 0 ? Txt.S("ToolNone") : string.Join(", ", all.Select(x => x.Config.Slug))));
                return one(b);
            }
            if (all.Count == 0) throw new AgentException(Txt.S("ToolNoBases"));
            if (all.Count == 1) return one(all[0]);
            var rows = new List<JObject>();
            foreach (var b in all)
            {
                try { rows.Add(one(b)); }
                catch (AgentException e) { rows.Add(new JObject { ["base"] = b.Config.Slug, ["error"] = e.Message }); }
                catch (Exception e) { rows.Add(new JObject { ["base"] = b.Config.Slug, ["error"] = e.Message }); }
            }
            return merge(rows);
        }

        // ---------- send_document ----------

        /// <summary>Готовый документ агента для письма и аудита.</summary>
        internal sealed class AgentDoc
        {
            public string Kind; public string Title; public string Path; public byte[] Pdf;
            /// <summary>Контрагент документа — его e-mail из 1С, если адресат не назван.</summary>
            public string CounterpartyKey;
            public Dictionary<string, string> Vars = new Dictionary<string, string>();
        }

        /// <summary>base указан — он; не указан и база одна — она; иначе — просим назвать.</summary>
        internal static BaseRuntime OneBase(IReadOnlyList<BaseRuntime> all, string slug)
        {
            if (all.Count == 0) throw new AgentException(Txt.S("ToolNoBases"));
            if (!string.IsNullOrEmpty(slug))
                return all.FirstOrDefault(x => string.Equals(x.Config.Slug, slug, StringComparison.OrdinalIgnoreCase))
                    ?? throw new AgentException(Txt.F("ToolNoSuchBase", slug, string.Join(", ", all.Select(x => x.Config.Slug))));
            if (all.Count == 1) return all[0];
            throw new AgentException(Txt.F("ToolPrintWhichBase", string.Join(", ", all.Select(x => x.Config.Slug))));
        }

        /// <summary>
        /// Документ агента → письмо сразу (исправление Славы 18.10 вечер: окна подтверждения нет). Счёт и акт сверки — на любой e-mail
        /// (не назван — e-mail контрагента из 1С; несколько — список, ИИ спрашивает пользователя); доверенность — только
        /// «Список адресов для отправки доверенностей». Ответ — «отправлено на …, копия на …» или ошибка SMTP.
        /// </summary>
        internal static JObject SendDocument(IReadOnlyList<BaseRuntime> all, JObject a)
        {
            var b = OneBase(all, (string)a["base"]);
            var kind = ((string)a["document"] ?? Mail.MailService.DocInvoice).Trim().ToLowerInvariant();
            var cfg = AgentConfig.Load();
            var to = ((string)a["to"] ?? "").Trim();
            // Доверенность — только «Список адресов для отправки доверенностей»: проверяем адрес до всего остального
            if (kind == Mail.MailService.DocPowerOfAttorney)
            {
                if (to.Length == 0) throw new AgentException(Txt.S("MailPoaNeedTo"));
                Mail.MailService.CheckRecipient(kind, to, cfg.Smtp?.Trusted);
            }
            var note = ((string)a["note"] ?? "").Trim();
            if (note.Length > 300) throw new AgentException(Txt.S("MailNoteTooLong"));
            var bc = cfg.Find(b.Config.Slug) ?? b.Config;
            var acc = Mail.MailService.Ready(cfg);
            var ms = bc.Mail ?? new Mail.MailSettings();

            var doc = MakeDoc(b, bc, kind, a);
            if (to.Length == 0)
            {
                var emails = Mail.ContactEmails.Of(bc, b.OData, doc.CounterpartyKey);
                if (emails.Count == 0) throw new AgentException(Txt.S("MailNoCounterpartyEmail"));
                // Несколько e-mail у контрагента — не угадываем: список, ИИ спрашивает пользователя и вызывает снова с to
                if (emails.Count > 1)
                    return new JObject
                    {
                        ["status"] = "choose", ["document"] = doc.Title, ["candidates"] = new JArray(emails),
                        ["note"] = Txt.F("MailChoiceNote", emails.Count),
                    };
                to = emails[0];
            }
            else to = Mail.MailService.CheckRecipient(kind, to, acc.Trusted);

            var vars = new Dictionary<string, string>(doc.Vars) { ["приписка"] = note, ["подпись"] = ms.Signature ?? "" };
            var (subjectT, bodyT) = ms.Templates(kind);
            var subject = Mail.MailService.Fill(subjectT, vars).Replace("\n", " ");
            var body = Mail.MailService.Fill(bodyT, vars);
            Mail.SentMail sent;
            try { sent = Mail.MailService.Send(bc, cfg, doc.Title, to, subject, body, System.IO.Path.GetFileName(doc.Path), doc.Pdf); }
            catch (AgentException e)
            {
                Audit.Write(Audit.SendFailed, bc.Slug, kind, doc.Title, doc.Path, doc.Pdf, to, string.IsNullOrWhiteSpace(acc.Bcc) ? null : acc.Bcc.Trim(), e.Message);
                throw;
            }
            Audit.Write(Audit.Send, bc.Slug, kind, doc.Title, doc.Path, doc.Pdf, sent.To, sent.Bcc, "ok");
            return new JObject
            {
                ["status"] = "sent",
                ["status_text"] = sent.Bcc == null ? Txt.F("MailSentText", sent.To) : Txt.F("MailSentTextBcc", sent.To, sent.Bcc),
                ["document"] = doc.Title, ["to"] = sent.To, ["bcc"] = sent.Bcc, ["subject"] = subject,
                ["at"] = sent.At.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            };
        }

        /// <summary>Документ для письма — печать тем же путём, что print_* (с записью аудита печати).</summary>
        internal static AgentDoc MakeDoc(BaseRuntime b, BaseConfig bc, string kind, JObject a)
        {
            if (kind == Mail.MailService.DocInvoice)
            {
                var number = (string)a["number"]; var last = (bool?)a["last"] ?? false;
                if (string.IsNullOrWhiteSpace(number) && !last) throw new AgentException(Txt.S("ToolPrintNeedNumber"));
                var r = Print.PrintService.Invoice(bc, b.OData, last ? null : number, (string)a["counterparty"]);
                var inv = r.Invoice;
                var doc = new AgentDoc
                {
                    Kind = kind, Path = r.Path, Pdf = r.Pdf, CounterpartyKey = inv.BuyerKey,
                    Title = Txt.F("MailDocInvoice", inv.PrintNumber, inv.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), bc.Slug),
                };
                doc.Vars["номер"] = inv.PrintNumber;
                doc.Vars["дата"] = inv.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
                doc.Vars["сумма"] = Print.Words.Money(inv.ToPay);
                doc.Vars["организация"] = Print.PrintService.ShortBuyer(inv.SupplierName);
                doc.Vars["покупатель"] = Print.PrintService.ShortBuyer(inv.BuyerName);
                Audit.Write(Audit.Print, bc.Slug, kind, doc.Title, doc.Path, doc.Pdf);
                return doc;
            }
            if (kind == Mail.MailService.DocPowerOfAttorney)
            {
                var number = (string)a["number"]; var last = (bool?)a["last"] ?? false;
                if (string.IsNullOrWhiteSpace(number) && !last) throw new AgentException(Txt.S("ToolPoaNeedNumber"));
                var r = Print.PrintService.PowerOfAttorney(bc, b.OData, last ? null : number, (string)a["employee"]);
                var doc = new AgentDoc { Kind = kind, Path = r.Path, Pdf = r.Pdf, CounterpartyKey = null, Title = PoaTitle(r.Poa, bc.Slug) };
                doc.Vars["номер"] = r.Poa.PrintNumber;
                doc.Vars["дата"] = r.Poa.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
                doc.Vars["организация"] = Print.PrintService.ShortBuyer(r.Poa.Org.ShortName ?? r.Poa.Org.Name);
                Audit.Write(Audit.Print, bc.Slug, kind, doc.Title, doc.Path, doc.Pdf);
                return doc;
            }
            if (kind == Mail.MailService.DocReconciliation)
            {
                var (from, to) = ActPeriod(bc, a);
                var r = Print.PrintService.Reconciliation(bc, b.OData, RequireCounterparty(a), from, to, (string)a["contract"], (string)a["organization"]);
                var doc = new AgentDoc { Kind = kind, Path = r.Path, Pdf = r.Pdf, CounterpartyKey = r.Act.CounterpartyKey, Title = ActTitle(r.Act, bc.Slug) };
                doc.Vars["период"] = Print.ReconciliationSource.Period(r.Act.From, r.Act.To);
                doc.Vars["организация"] = Print.PrintService.ShortBuyer(r.Act.Org.ShortName ?? r.Act.Org.Name);
                doc.Vars["контрагент"] = Print.PrintService.ShortBuyer(r.Act.CounterpartyShort);
                Audit.Write(Audit.Print, bc.Slug, kind, doc.Title, doc.Path, doc.Pdf);
                return doc;
            }
            throw new AgentException(Txt.S("MailOnlyInvoice"));
        }

        // ---------- print_invoice ----------

        /// <summary>Поиск счетов: номер, дата, покупатель, БИН, сумма — до 20 на базу, новые сверху.</summary>
        internal static JObject SearchInvoices(BaseRuntime b, string counterparty, string period)
        {
            var (found, total) = Print.InvoiceSource.Search(b.OData, counterparty, period, SearchTop);
            var items = new JArray(found.Select(f => new JObject
            {
                ["number"] = f.Number, ["date"] = f.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["buyer"] = f.Buyer, ["bin"] = NullIfEmpty(f.BuyerBin), ["sum"] = Money((double)f.Sum), ["currency"] = "KZT",
            }));
            var o = new JObject { ["base"] = b.Config.Slug, ["found"] = items.Count, ["more"] = total > items.Count, ["items"] = items };
            if (total > items.Count) o["note"] = Txt.F("ToolTruncated", items.Count, total);
            o["_total"] = total;
            return o;
        }

        public const int SearchTop = 20;

        /// <summary>PDF — ресурсом (клиент на другой машине получает файл), первая страница — картинкой (для клиентов без показа PDF).</summary>
        internal static JArray PdfContent(string path, byte[] pdf)
        {
            var extra = new JArray(new JObject
            {
                ["type"] = "resource",
                ["resource"] = new JObject
                {
                    ["uri"] = "file:///" + Uri.EscapeDataString(System.IO.Path.GetFileName(path)),
                    ["name"] = System.IO.Path.GetFileName(path),
                    ["mimeType"] = "application/pdf",
                    ["blob"] = Convert.ToBase64String(pdf),
                },
            });
            var png = Print.PdfPreview.FirstPagePng(pdf);
            if (png != null) extra.Add(new JObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(png) });
            return extra;
        }

        /// <summary>Счёт на оплату (уже есть в 1С) → PDF с логотипом, печатью и подписью этой базы. Только чтение.</summary>
        internal static JObject PrintInvoice(IReadOnlyList<BaseRuntime> all, string slug, string number, bool last, string counterparty = null)
        {
            var b = OneBase(all, slug);
            if (string.IsNullOrWhiteSpace(number) && !last) throw new AgentException(Txt.S("ToolPrintNeedNumber"));
            var r = Print.PrintService.Invoice(b.Config, b.OData, last ? null : number, counterparty);
            var inv = r.Invoice;
            Audit.Write(Audit.Print, b.Config.Slug, Mail.MailService.DocInvoice,
                Txt.F("MailDocInvoice", inv.PrintNumber, inv.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), b.Config.Slug), r.Path, r.Pdf);
            return new JObject
            {
                [McpProtocol.ExtraContent] = PdfContent(r.Path, r.Pdf),
                ["file"] = System.IO.Path.GetFileName(r.Path),
                ["base"] = b.Config.Slug,
                ["path"] = r.Path,
                ["number"] = inv.PrintNumber,
                ["date"] = inv.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["buyer"] = inv.BuyerName,
                ["sum"] = Money((double)inv.ToPay),
                ["currency"] = "KZT",
                ["note"] = Txt.S("ToolPrintNote"),
            }.Also(o =>
            {
                // Форма не настроена — печатаем, но не молчим
                var miss = Print.PrintStore.Missing(b.Config.Slug);
                if (miss != null) o["warning"] = Txt.F("PrintNotConfigured", b.Config.Slug, miss);
            });
        }

        // ---------- print_power_of_attorney / print_reconciliation ----------

        internal static string PoaTitle(Print.PoaData p, string slug) =>
            Txt.F("MailDocPoa", p.PrintNumber, p.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), slug);

        internal static string ActTitle(Print.ActData act, string slug) =>
            Txt.F("MailDocAct", Print.PrintService.ShortBuyer(act.CounterpartyShort), Print.ReconciliationSource.Period(act.From, act.To), slug);

        /// <summary>Доверенности: номер, дата, действительна по, кому (ФИО), от кого — без удостоверения личности.</summary>
        internal static JObject SearchPoa(BaseRuntime b, string employee, string period)
        {
            var (found, total) = Print.PoaSource.Search(b.OData, employee, period, SearchTop);
            var items = new JArray(found.Select(f => new JObject
            {
                ["number"] = f.Number, ["date"] = f.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["valid_to"] = f.ValidTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["employee"] = f.Person, ["from"] = f.From,
            }));
            var o = new JObject { ["base"] = b.Config.Slug, ["found"] = items.Count, ["more"] = total > items.Count, ["items"] = items };
            if (total > items.Count) o["note"] = Txt.F("ToolTruncated", items.Count, total);
            o["_total"] = total;
            return o;
        }

        /// <summary>Доверенность (уже есть в 1С) → PDF формы Д-1. Удостоверение личности — только внутри PDF, в ответе его нет.</summary>
        internal static JObject PrintPoa(BaseRuntime b, string number, bool last, string employee)
        {
            if (string.IsNullOrWhiteSpace(number) && !last) throw new AgentException(Txt.S("ToolPoaNeedNumber"));
            var r = Print.PrintService.PowerOfAttorney(b.Config, b.OData, last ? null : number, employee);
            var p = r.Poa;
            Audit.Write(Audit.Print, b.Config.Slug, Mail.MailService.DocPowerOfAttorney, PoaTitle(p, b.Config.Slug), r.Path, r.Pdf);
            return new JObject
            {
                [McpProtocol.ExtraContent] = PdfContent(r.Path, r.Pdf),
                ["file"] = System.IO.Path.GetFileName(r.Path),
                ["base"] = b.Config.Slug,
                ["path"] = r.Path,
                ["number"] = p.PrintNumber,
                ["date"] = p.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["valid_to"] = p.ValidTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["employee"] = p.PersonName,
                ["from"] = p.From,
                ["items"] = p.Lines.Count,
                ["note"] = Txt.S("ToolPoaNote"),
            }.Also(o =>
            {
                var miss = Print.PrintStore.Missing(b.Config.Slug);
                if (miss != null) o["warning"] = Txt.F("PrintNotConfigured", b.Config.Slug, miss);
            });
        }

        private static string RequireCounterparty(JObject a)
        {
            var cp = ((string)a["counterparty"] ?? "").Trim();
            if (cp.Length < 2) throw new AgentException(Txt.S("ToolActNeedCounterparty"));
            return cp;
        }

        /// <summary>Период акта: period_from / period_to; не указан — по настройке «Акта сверки» (прошлый квартал по умолчанию).</summary>
        internal static (DateTime From, DateTime To) ActPeriod(BaseConfig bc, JObject a)
        {
            var def = Print.ReconciliationSource.DefaultPeriod(bc.Print?.ActPeriod, DateTime.Now);
            DateTime Parse(string s, DateTime fallback)
            {
                if (string.IsNullOrWhiteSpace(s)) return fallback;
                if (!DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) throw new AgentException(Txt.F("ActBadDate", s));
                return d;
            }
            return (Parse((string)a["period_from"], def.From), Parse((string)a["period_to"], def.To));
        }

        /// <summary>Акт сверки по проведённым документам 1С → PDF; в ответе сальдо и обороты (наши данные) и as_of.</summary>
        internal static JObject PrintAct(BaseRuntime b, JObject a)
        {
            var (from, to) = ActPeriod(b.Config, a);
            var r = Print.PrintService.Reconciliation(b.Config, b.OData, RequireCounterparty(a), from, to, (string)a["contract"], (string)a["organization"]);
            var act = r.Act;
            Audit.Write(Audit.Print, b.Config.Slug, Mail.MailService.DocReconciliation, ActTitle(act, b.Config.Slug), r.Path, r.Pdf);
            var org = act.Org.ShortName ?? act.Org.Name;
            return new JObject
            {
                [McpProtocol.ExtraContent] = PdfContent(r.Path, r.Pdf),
                ["file"] = System.IO.Path.GetFileName(r.Path),
                ["base"] = b.Config.Slug,
                ["path"] = r.Path,
                ["organization"] = org,
                ["counterparty"] = act.CounterpartyShort,
                ["bin"] = NullIfEmpty(act.CounterpartyBin),
                ["contract"] = NullIfEmpty(act.Contract),
                ["period_from"] = act.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["period_to"] = act.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["opening_balance"] = Money((double)act.Open),
                ["debit"] = Money((double)act.TurnDr),
                ["credit"] = Money((double)act.TurnCr),
                ["closing_balance"] = Money((double)act.Close),
                ["debt_in_favor_of"] = act.Close == 0 ? JValue.CreateNull() : (JToken)(act.Close > 0 ? org : act.CounterpartyShort),
                ["documents"] = act.RowCount,
                ["contracts"] = act.ByContracts ? new JArray(act.Sections.Select(s => new JObject
                {
                    ["contract"] = s.Contract, ["opening_balance"] = Money((double)s.Open), ["debit"] = Money((double)s.TurnDr),
                    ["credit"] = Money((double)s.TurnCr), ["closing_balance"] = Money((double)s.Close),
                })) : null,
                ["currency"] = "KZT",
                ["counterparty_half"] = (b.Config.Print?.ActMirror ?? false) ? "mirror" : "empty",
                ["note"] = Txt.S("ToolActNote"),
                ["as_of"] = Freshness.AsOf(b, StaleWorkdays),
            }.Also(o =>
            {
                if (o["contracts"]?.Type == JTokenType.Null) o.Remove("contracts");
                var miss = Print.PrintStore.Missing(b.Config.Slug);
                if (miss != null) o["warning"] = Txt.F("PrintNotConfigured", b.Config.Slug, miss);
            });
        }

        // ---------- whoami ----------

        private static JArray Organizations(BaseRuntime b) => b.Cached("orgs", () => b.OData.Rows("Catalog_Организации", new Dictionary<string, string>
        {
            ["$select"] = "Ref_Key,Description,ИдентификационныйНомер",
            ["$filter"] = "DeletionMark eq false",
        }));

        internal static JObject Whoami(BaseRuntime b)
        {
            var orgs = Organizations(b);
            return new JObject
            {
                ["base"] = b.Config.Slug,
                ["configuration"] = b.Config.DisplayName,
                ["version"] = b.Config.Version,
                ["user"] = b.Config.User,
                ["organizations"] = new JArray(orgs.Select(o => new JObject
                {
                    ["name"] = o["Description"],
                    ["bin"] = NullIfEmpty((string)o["ИдентификационныйНомер"]),
                })),
            };
        }

        // ---------- find_counterparty ----------

        internal static string FindFilter(string query)
        {
            var s = query.Trim();
            string cond;
            if (Regex.IsMatch(s, @"^\d+$"))
                cond = s.Length == 12 ? $"ИдентификационныйКодЛичности eq {ODataClient.Lit(s)}" : $"substringof({ODataClient.Lit(s)}, ИдентификационныйКодЛичности)";
            else
                cond = $"substringof({ODataClient.Lit(s)}, Description)";
            return cond + " and IsFolder eq false and DeletionMark eq false";
        }

        internal static JObject Find(BaseRuntime b, string query)
        {
            // Пробел внутри $filter — %20 (Uri.EscapeDataString): «+» 1С не декодирует и отвечает 400 (known-issues 14)
            var (rows, total) = b.OData.RowsCounted("Catalog_Контрагенты", new Dictionary<string, string>
            {
                ["$select"] = "Description,НаименованиеПолное,ИдентификационныйКодЛичности",
                ["$filter"] = FindFilter(query),
                ["$orderby"] = "Description",
                ["$top"] = FindTop.ToString(CultureInfo.InvariantCulture),
            });
            var items = new JArray();
            foreach (var r in rows.Take(FindTop))
            {
                var it = new JObject { ["name"] = r["Description"] };
                var full = (string)r["НаименованиеПолное"];
                if (!string.IsNullOrEmpty(full)) it["fullName"] = full;
                it["bin"] = NullIfEmpty((string)r["ИдентификационныйКодЛичности"]);
                items.Add(it);
            }
            var o = new JObject { ["base"] = b.Config.Slug, ["found"] = items.Count, ["more"] = total > items.Count, ["items"] = items };
            if (total > items.Count) o["note"] = Txt.F("ToolTruncated", items.Count, total);
            o["_total"] = total; // служебное: для объединения по базам; Limit() убирает поля «_…» из ответа
            return o;
        }

        /// <summary>Поиск контрагента — не больше 10 строк на базу.</summary>
        public const int FindTop = 10;

        private static JObject MergeFind(List<JObject> rows)
        {
            var items = new JArray();
            var errors = new JArray();
            var more = false;
            var total = 0;
            foreach (var r in rows)
            {
                if (r["error"] != null) { errors.Add(r); continue; }
                more |= (bool)r["more"];
                total += (int?)r["_total"] ?? ((JArray)r["items"]).Count;
                foreach (JObject it in (JArray)r["items"])
                {
                    var x = new JObject { ["base"] = r["base"] };
                    foreach (var p in it.Properties()) x[p.Name] = p.Value;
                    items.Add(x);
                }
            }
            var o = new JObject { ["found"] = items.Count, ["more"] = more, ["items"] = items };
            if (more) o["note"] = Txt.F("ToolTruncated", items.Count, total);
            if (errors.Count > 0) o["errors"] = errors;
            return o;
        }

        // ---------- cash_balance ----------

        private sealed class MoneyAccount { public string Key; public string Code; public CashClass Cls; }

        private static List<MoneyAccount> MoneyAccounts(BaseRuntime b) => b.Cached("accounts", () =>
            b.OData.Rows("ChartOfAccounts_" + b.Profile.ChartOfAccounts, new Dictionary<string, string> { ["$select"] = "Ref_Key,Code" })
                .Select(a => new { Key = (string)a["Ref_Key"], Code = (string)a["Code"] })
                .Where(a => b.Profile.IsMoneyAccount(a.Code))
                .Select(a => new MoneyAccount { Key = a.Key, Code = a.Code, Cls = b.Profile.ClassifyMoney(a.Code) })
                .ToList());

        internal static JObject Cash(BaseRuntime b, string date)
        {
            var accounts = MoneyAccounts(b);
            var byKey = accounts.ToDictionary(a => a.Key, StringComparer.OrdinalIgnoreCase);
            // Условие по счетам — в $filter (строка запроса), а не в AccountCondition: длинное условие в сегменте пути
            // упирается в лимит http.sys на длину сегмента (260 символов) и получает HTML-ошибку IIS (known-issues 10)
            var rows = b.OData.Rows($"AccountingRegister_{b.Profile.AccountingRegister}/Balance(Period=datetime'{date}T23:59:59')", new Dictionary<string, string>
            {
                ["$filter"] = string.Join(" or ", accounts.Select(a => $"Account_Key eq guid'{a.Key}'")),
            });
            var orgNames = Organizations(b).ToDictionary(o => (string)o["Ref_Key"], o => (string)o["Description"], StringComparer.OrdinalIgnoreCase);
            var per = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var r in rows)
            {
                if (!byKey.TryGetValue((string)r["Account_Key"] ?? "", out var acc)) continue;
                var org = (string)r["Организация_Key"] ?? "";
                if (!per.TryGetValue(org, out var sums)) { sums = new double[3]; per[org] = sums; order.Add(org); }
                sums[(int)acc.Cls] += (double?)r["СуммаBalance"] ?? 0;
            }
            var list = new JArray();
            foreach (var org in order)
            {
                var s = per[org];
                list.Add(new JObject
                {
                    ["organization"] = orgNames.TryGetValue(org, out var n) ? n : org,
                    ["cash"] = Money(s[0]), ["bank"] = Money(s[1]), ["other"] = Money(s[2]), ["total"] = Money(s[0] + s[1] + s[2]),
                });
            }
            // «Данные на …» — дата последней выписки по каждому счёту и последнего документа; модель обязана её назвать
            return new JObject { ["base"] = b.Config.Slug, ["date"] = date, ["currency"] = "KZT", ["organizations"] = list, ["as_of"] = Freshness.AsOf(b, StaleWorkdays) };
        }

        private static JObject MergeCash(List<JObject> rows, string date)
        {
            var list = new JArray();
            var errors = new JArray();
            foreach (var r in rows)
            {
                if (r["error"] != null) { errors.Add(r); continue; }
                foreach (JObject o in (JArray)r["organizations"])
                {
                    var x = new JObject { ["base"] = r["base"] };
                    foreach (var p in o.Properties()) x[p.Name] = p.Value;
                    list.Add(x);
                }
            }
            var res = new JObject { ["date"] = date, ["currency"] = "KZT", ["organizations"] = list };
            var asOf = new JObject();
            foreach (var r in rows.Where(r => r["error"] == null && r["as_of"] != null)) asOf[(string)r["base"]] = r["as_of"];
            if (asOf.Count > 0) res["as_of"] = asOf;
            if (errors.Count > 0) res["errors"] = errors;
            return res;
        }

        /// <summary>Сумма как в прототипе (JS: Math.round(n*100)/100) и в том же виде в JSON: целое — без «.0».</summary>
        internal static JToken Money(double v)
        {
            var r = Math.Floor(v * 100 + 0.5) / 100;
            if (Math.Abs(r) < 1e15 && r == Math.Floor(r)) return new JRaw(((long)r).ToString(CultureInfo.InvariantCulture));
            return new JRaw(r.ToString("R", CultureInfo.InvariantCulture));
        }

        private static JObject Also(this JObject o, Action<JObject> a) { a(o); return o; }

        private static JToken NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? JValue.CreateNull() : (JToken)s;
    }
}
