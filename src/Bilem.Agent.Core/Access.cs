using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core
{
    public sealed class AccessSection
    {
        public string Key { get; set; }
        public string Title { get; set; }
        /// <summary>Закрытый раздел (зарплата и кадры): галочки недоступны.</summary>
        public bool Locked { get; set; }
    }

    public sealed class AccessItem
    {
        public string FullName { get; set; }
        public string Title { get; set; }
        public string Section { get; set; }
        public bool Locked { get; set; }

        /// <summary>Запись — только белый список (решение 0013), не из настроек.</summary>
        [JsonIgnore] public bool CanWrite => !Locked && Access.WriteWhitelist.Contains(FullName);
    }

    /// <summary>Дерево вкладки «Доступ»: разделы как в левом меню 1С и объекты профиля с человеческими именами.</summary>
    public sealed class AccessCatalog
    {
        public List<AccessSection> Sections { get; set; } = new List<AccessSection>();
        public List<AccessItem> Items { get; set; } = new List<AccessItem>();
        /// <summary>Разделы — из подсистем самой конфигурации (иначе — запасная раскладка профиля).</summary>
        public bool FromConfiguration { get; set; }
        public DateTime? LoadedAt { get; set; }
        /// <summary>Какие объекты спрашивали у 1С при загрузке (есть они или нет). null — каталог загружен до beta.17: про новые объекты не знает.</summary>
        public List<string> Asked { get; set; }

        /// <summary>Объект у 1С спрашивали, и его в конфигурации нет.</summary>
        public bool KnownAbsent(string fullName) => FromConfiguration && Asked != null && Asked.Contains(fullName) && Find(fullName) == null;

        public AccessItem Find(string fullName) => Items.FirstOrDefault(i => i.FullName == fullName);
        public string TitleOf(string fullName) => Find(fullName)?.Title ?? fullName;
    }

    /// <summary>Что изменится при «Применить».</summary>
    public sealed class AccessChange
    {
        public List<string> ReadOn { get; } = new List<string>();
        public List<string> ReadOff { get; } = new List<string>();
        public List<string> WriteOn { get; } = new List<string>();
        public List<string> WriteOff { get; } = new List<string>();
        public bool Any => ReadOn.Count + ReadOff.Count + WriteOn.Count + WriteOff.Count > 0;
    }

    /// <summary>Роли bilem для выбранного доступа (подбор по ПравоДоступа: минимальный набор).</summary>
    public sealed class RolePlan
    {
        public List<string> Roles { get; } = new List<string>();
        public List<string> Extra { get; } = new List<string>();
        /// <summary>Объект → роль, которую добавили ради него (для журнала).</summary>
        public Dictionary<string, string> Why { get; } = new Dictionary<string, string>();
        /// <summary>Объекты, для которых в конфигурации нет подходящей роли.</summary>
        public List<string> NoRole { get; } = new List<string>();
    }

    public static class Access
    {
        /// <summary>
        /// Белый список записи (решение 0013): Bilem не создаёт документы, которые меняют бухгалтерский учёт.
        /// Только счёт покупателю, доверенность, акт сверки и карточка контрагента (банковский счёт, договор) — черновиками.
        /// Список — в коде, не в config.json; расширять — только решением.
        /// </summary>
        public static readonly IReadOnlyCollection<string> WriteWhitelist = new HashSet<string>
        {
            "Документ.СчетНаОплатуПокупателю", "Документ.Доверенность", "Документ.АктСверкиВзаиморасчетов",
            "Справочник.Контрагенты", "Справочник.БанковскиеСчета", "Справочник.ДоговорыКонтрагентов",
        };

        /// <summary>
        /// Запись, которую bilem получает сразу при настройке базы (Слава, 14.11, #99): документы без проводок, у каждого в БП КЗ
        /// своя узкая роль. Контрагенты (#100, 16.11) — тоже: справочник проводок не делает, draft_counterparty / create_counterparty
        /// только добавляют карточку; уже подключённые базы получают роль по «Обновить права Bilem». Банковские счета и договоры —
        /// только галочкой на «Доступе».
        /// </summary>
        public static readonly IReadOnlyList<string> DefaultWrite = new[]
        {
            "Документ.СчетНаОплатуПокупателю", "Документ.АктСверкиВзаиморасчетов", "Документ.Доверенность", "Справочник.Контрагенты",
        };

        /// <summary>
        /// Чтение по умолчанию сверх состава профиля (Слава, 14.11, #104): объекты, которые нужны новым инструментам, — включены и при
        /// настройке базы, и по «Обновить права Bilem». Правило: новый документ или регистр для чтения нового инструмента добавляется сюда.
        /// Выключенными по умолчанию остаются только зарплата и кадры (закрыты намеренно) и запись сверх DefaultWrite.
        /// Нет объекта в конфигурации — пропускаем молча. Выключил человек на «Доступе» — снова не включаем (BaseConfig.ReadDefaults).
        /// </summary>
        public static readonly IReadOnlyList<string> DefaultRead = new[]
        {
            "Документ.ГТДИмпорт",
        };

        /// <summary>
        /// Чтение по умолчанию, которого у базы ещё нет в составе OData и которое базе ещё не предлагали. Каталог старый и про объект
        /// не знает — тоже считаем (узнает «Обновить права Bilem»); известно, что в конфигурации его нет, — нет.
        /// </summary>
        public static List<string> MissingDefaultRead(BaseConfig b)
        {
            var cat = CatalogOf(b);
            var have = new HashSet<string>(b.ODataObjects ?? new List<string>());
            var seen = new HashSet<string>(b.ReadDefaults ?? new List<string>());
            var profile = Profiles.For(b.Configuration);
            return DefaultRead.Where(x => profile.CatalogObjects.Contains(x) && !cat.KnownAbsent(x) && !have.Contains(x) && !seen.Contains(x)).ToList();
        }

        /// <summary>Запись по умолчанию (счёт, акт сверки, доверенность, контрагенты), которой у базы ещё нет в 1С.</summary>
        public static List<string> MissingDefaultWrite(BaseConfig b)
        {
            var cat = CatalogOf(b);
            var have = new HashSet<string>(b.WriteInOneC ?? new List<string>());
            return DefaultWrite.Where(x => cat.Find(x) != null && !have.Contains(x)).ToList();
        }

        /// <summary>Чтение по умолчанию, попавшее в состав OData, — запомнить как предложенное (дальше решает человек на «Доступе»).</summary>
        public static void MarkReadDefaults(BaseConfig b) =>
            b.ReadDefaults = (b.ReadDefaults ?? new List<string>()).Concat(DefaultRead.Where((b.ODataObjects ?? new List<string>()).Contains)).Distinct().ToList();

        public const string LockedKey = "ЗарплатаИКадры";
        public const string OtherKey = "Прочее";

        /// <summary>Каталог по ответу помощника COM metaCatalog (подсистемы конфигурации).</summary>
        public static AccessCatalog FromMeta(ConfigProfile p, JObject meta)
        {
            var subs = ((JArray)meta["subsystems"]).Select(s => new { Name = (string)s["name"], Title = ((string)s["synonym"] ?? "").Trim() }).ToList();
            if (subs.Count == 0) return Fallback(p);
            var titleOf = subs.ToDictionary(s => s.Name, s => string.IsNullOrEmpty(s.Title) ? s.Name : s.Title);
            int Rank(string sub)
            {
                var i = Array.IndexOf(p.SectionOrder ?? new string[0], sub);
                return i >= 0 ? i : 1000;
            }
            var cat = new AccessCatalog
            {
                FromConfiguration = true, LoadedAt = DateTime.Now,
                Asked = ((JArray)meta["objects"]).OfType<JObject>().Select(o => (string)o["fullName"]).ToList(),
            };
            foreach (var o in ((JArray)meta["objects"]).OfType<JObject>())
            {
                if (o["exists"] == null || !(bool)o["exists"]) continue;
                var fn = (string)o["fullName"];
                var secs = ((JArray)o["sections"] ?? new JArray()).Select(x => (string)x).ToList();
                var real = secs.Where(s => !p.ServiceSubsystems.Contains(s)).ToList();
                var locked = p.PersonalData.Contains(fn) || (real.Count > 0 && real.All(s => p.LockedSubsystems.Contains(s)));
                string section;
                if (locked) section = LockedKey;
                else if (p.SectionOverrides != null && p.SectionOverrides.TryGetValue(fn, out var ov) && titleOf.ContainsKey(ov)) section = ov;
                else
                {
                    var open = real.Where(s => !p.LockedSubsystems.Contains(s)).ToList();
                    section = open.OrderBy(Rank).ThenBy(s => titleOf.TryGetValue(s, out var t) ? t : s, StringComparer.CurrentCulture).FirstOrDefault() ?? OtherKey;
                }
                var title = ((string)o["synonym"] ?? "").Trim();
                cat.Items.Add(new AccessItem { FullName = fn, Title = string.IsNullOrEmpty(title) ? fn : title, Section = section, Locked = locked });
            }
            var used = cat.Items.Select(i => i.Section).Distinct().ToList();
            foreach (var s in used.Where(k => k != LockedKey && k != OtherKey).OrderBy(Rank).ThenBy(k => titleOf.TryGetValue(k, out var t) ? t : k, StringComparer.CurrentCulture))
                cat.Sections.Add(new AccessSection { Key = s, Title = titleOf.TryGetValue(s, out var t) ? t : s });
            if (used.Contains(LockedKey)) cat.Sections.Add(new AccessSection { Key = LockedKey, Title = Txt.S("AccessLockedSection"), Locked = true });
            if (used.Contains(OtherKey)) cat.Sections.Add(new AccessSection { Key = OtherKey, Title = Txt.S("AccessOtherSection") });
            return cat;
        }

        /// <summary>Запасная раскладка профиля: разделы из таблицы ТЗ, имена — синонимы БП КЗ.</summary>
        public static AccessCatalog Fallback(ConfigProfile p, IEnumerable<string> present = null)
        {
            var only = present == null ? null : new HashSet<string>(present);
            var cat = new AccessCatalog();
            foreach (var fn in p.CatalogObjects)
            {
                var locked = p.PersonalData.Contains(fn);
                if (only != null && !only.Contains(fn) && !locked) continue;
                string section = locked ? LockedKey : (p.FallbackSections.TryGetValue(fn, out var s) ? s : OtherKey);
                cat.Items.Add(new AccessItem { FullName = fn, Title = p.FallbackTitles.TryGetValue(fn, out var t) ? t : fn, Section = section, Locked = locked });
            }
            foreach (var s in cat.Items.Select(i => i.Section).Distinct().Where(k => k != LockedKey && k != OtherKey))
                cat.Sections.Add(new AccessSection { Key = s, Title = s });
            if (cat.Items.Any(i => i.Section == LockedKey)) cat.Sections.Add(new AccessSection { Key = LockedKey, Title = Txt.S("AccessLockedSection"), Locked = true });
            if (cat.Items.Any(i => i.Section == OtherKey)) cat.Sections.Add(new AccessSection { Key = OtherKey, Title = Txt.S("AccessOtherSection") });
            return cat;
        }

        /// <summary>Каталог базы: загруженный из 1С или запасной (объекты, которых нет в базе, запасной не знает — показывает только состав OData).</summary>
        public static AccessCatalog CatalogOf(BaseConfig b)
        {
            if (b.Catalog != null && b.Catalog.Items.Count > 0) return b.Catalog;
            return Fallback(Profiles.For(b.Configuration), b.ODataObjects);
        }

        /// <summary>Правила галочек: запись без чтения невозможна; закрытое и «только чтение» — без записи; только объекты каталога.</summary>
        public static (HashSet<string> Read, HashSet<string> Write) Normalize(AccessCatalog cat, IEnumerable<string> read, IEnumerable<string> write)
        {
            var known = cat.Items.Where(i => !i.Locked).ToDictionary(i => i.FullName);
            var w = new HashSet<string>((write ?? Enumerable.Empty<string>()).Where(x => known.TryGetValue(x, out var i) && i.CanWrite));
            var r = new HashSet<string>((read ?? Enumerable.Empty<string>()).Where(known.ContainsKey));
            r.UnionWith(w);
            return (r, w);
        }

        public static AccessChange Diff(BaseConfig b, ISet<string> read, ISet<string> write)
        {
            var c = new AccessChange();
            var r0 = new HashSet<string>(b.ReadEffective);
            var w0 = new HashSet<string>(b.Write ?? new List<string>());
            c.ReadOn.AddRange(read.Where(x => !r0.Contains(x)));
            c.ReadOff.AddRange(r0.Where(x => !read.Contains(x)));
            c.WriteOn.AddRange(write.Where(x => !w0.Contains(x)));
            c.WriteOff.AddRange(w0.Where(x => !write.Contains(x)));
            return c;
        }

        /// <summary>Нужен ли слой 1С (пароль администратора): состав OData или роли записи отличаются от выбранного.</summary>
        public static bool NeedsOneC(BaseConfig b, ISet<string> read, ISet<string> write) =>
            !read.SetEquals((b.ODataObjects ?? new List<string>()).Where(x => !Profiles.IsService(x) || read.Contains(x))) || !write.SetEquals(b.WriteInOneC ?? new List<string>());

        /// <summary>Роли, которые не даём никогда: полные права, администрирование, устаревшие («Удалить…»).</summary>
        public static bool Forbidden(string role) =>
            role.IndexOf("ПолныеПрава", StringComparison.OrdinalIgnoreCase) >= 0
            || role.IndexOf("Администр", StringComparison.OrdinalIgnoreCase) >= 0
            || role.StartsWith("Удалить", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Подбор ролей (ответ помощника COM rightsMap): роли профиля + для каждого объекта, которому их не хватает, —
        /// роль с самым узким правом (меньше всего объектов с добавлением), «Чтение…» / «ДобавлениеИзменение…» — при равенстве.
        /// </summary>
        public static RolePlan PlanRoles(ConfigProfile p, IEnumerable<string> availableBaseRoles, IEnumerable<string> needRead, IEnumerable<string> needWrite, JObject rights)
        {
            var plan = new RolePlan();
            plan.Roles.AddRange(availableBaseRoles);
            var objs = (JObject)rights["objects"] ?? new JObject();
            var breadth = (JObject)rights["breadth"] ?? new JObject();
            int Breadth(string role) => (int?)breadth[role] ?? 1000;

            // soft — чтение плана обмена: узкой роли нет (только полные права) — пропускаем, запись объекта не запрещаем
            bool Ensure(string fn, string kind, string preferPrefix, bool soft = false)
            {
                var have = ((JArray)objs[fn]?[kind] ?? new JArray()).Select(x => (string)x).ToList();
                if (have.Any(plan.Roles.Contains)) return true;
                // Для плана обмена — только роли «Чтение…»: «ДобавлениеИзменениеПлановОбмена» читает их тоже, но и пишет
                var pick = have.Where(r => !Forbidden(r) && (!soft || r.StartsWith("Чтение", StringComparison.Ordinal)))
                    .OrderBy(Breadth)
                    .ThenBy(r => r.StartsWith(preferPrefix, StringComparison.Ordinal) ? 0 : 1)
                    .ThenBy(r => r.Length)
                    .FirstOrDefault();
                if (pick == null) { if (!soft) plan.NoRole.Add(fn); return false; }
                plan.Roles.Add(pick);
                plan.Extra.Add(pick);
                plan.Why[fn] = pick;
                return true;
            }

            foreach (var fn in needRead ?? Enumerable.Empty<string>()) Ensure(fn, "read", "Чтение");
            foreach (var fn in needWrite ?? Enumerable.Empty<string>())
            {
                if (!Ensure(fn, "write", "ДобавлениеИзменение")) continue;
                // Запись объекта из состава плана обмена регистрирует изменение: 1С читает узлы плана (#100, контрагенты в БП КЗ).
                // Планы, которые читают только полные права (АвтономнаяРабота, МиграцияПриложений), счёту не мешали — их пропускаем
                foreach (var xp in ((JArray)objs[fn]?["exchangePlans"] ?? new JArray()).Select(x => (string)x))
                    Ensure(xp, "read", "Чтение", soft: true);
            }
            return plan;
        }
    }
}
