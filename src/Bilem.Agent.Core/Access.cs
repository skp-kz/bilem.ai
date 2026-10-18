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
            var cat = new AccessCatalog { FromConfiguration = true, LoadedAt = DateTime.Now };
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

            void Ensure(string fn, string kind, string preferPrefix)
            {
                var have = ((JArray)objs[fn]?[kind] ?? new JArray()).Select(x => (string)x).ToList();
                if (have.Any(plan.Roles.Contains)) return;
                var pick = have.Where(r => !Forbidden(r))
                    .OrderBy(Breadth)
                    .ThenBy(r => r.StartsWith(preferPrefix, StringComparison.Ordinal) ? 0 : 1)
                    .ThenBy(r => r.Length)
                    .FirstOrDefault();
                if (pick == null) { plan.NoRole.Add(fn); return; }
                plan.Roles.Add(pick);
                plan.Extra.Add(pick);
                plan.Why[fn] = pick;
            }

            foreach (var fn in needRead ?? Enumerable.Empty<string>()) Ensure(fn, "read", "Чтение");
            foreach (var fn in needWrite ?? Enumerable.Empty<string>()) Ensure(fn, "write", "ДобавлениеИзменение");
            return plan;
        }
    }
}
