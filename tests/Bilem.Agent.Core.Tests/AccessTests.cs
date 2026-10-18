using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>v0.2, часть 1: вкладка «Доступ».</summary>
    public class AccessTests
    {
        private static JObject Meta() => JObject.Parse(@"{
          subsystems: [
            { name: 'Администрирование', synonym: 'Администрирование' }, { name: 'БанкИКасса', synonym: 'Банк и касса' },
            { name: 'КадровыйУчет', synonym: 'Кадровый учет' }, { name: 'Покупка', synonym: 'Покупка' },
            { name: 'Предприятие', synonym: 'Предприятие' }, { name: 'Продажа', synonym: 'Продажа' } ],
          objects: [
            { fullName: 'Справочник.Организации', exists: true, synonym: 'Организации', sections: ['Администрирование', 'Предприятие'] },
            { fullName: 'Справочник.Контрагенты', exists: true, synonym: 'Контрагенты', sections: ['Покупка', 'Продажа'] },
            { fullName: 'Документ.АктСверкиВзаиморасчетов', exists: true, synonym: 'Акт сверки', sections: ['Покупка', 'Продажа'] },
            { fullName: 'Документ.СчетНаОплатуПокупателю', exists: true, synonym: 'Счет на оплату покупателю', sections: ['Продажа'] },
            { fullName: 'Справочник.Валюты', exists: true, synonym: 'Валюты', sections: ['Администрирование', 'БанкИКасса'] },
            { fullName: 'Справочник.ФизическиеЛица', exists: true, synonym: 'Физические лица', sections: ['КадровыйУчет'] },
            { fullName: 'Документ.АвансовыйОтчет', exists: true, synonym: 'Авансовый отчет', sections: ['БанкИКасса'] },
            { fullName: 'РегистрСведений.КурсыВалют', exists: true, synonym: 'Курсы валют', sections: [] },
            { fullName: 'РегистрБухгалтерии.Типовой', exists: true, synonym: 'Журнал проводок', sections: ['Продажа'] },
            { fullName: 'Документ.НачислениеЗарплаты', exists: false } ]
        }");

        private static AccessCatalog Cat() => Access.FromMeta(Profiles.BpKz, Meta());

        [Fact]
        public void Catalog_SectionsFromSubsystems_MenuOrder_OverridesAndService()
        {
            var c = Cat();
            Assert.True(c.FromConfiguration);
            Assert.Equal(new[] { "Банк и касса", "Продажа", "Предприятие", "Зарплата и кадры", "Прочее" }, c.Sections.Select(s => s.Title));
            Assert.Equal("Предприятие", c.Find("Справочник.Организации").Section);   // служебная «Администрирование» — не раздел
            Assert.Equal("Предприятие", c.Find("Справочник.Контрагенты").Section);   // основной раздел по профилю
            Assert.Equal("Продажа", c.Find("Документ.АктСверкиВзаиморасчетов").Section); // первый по порядку меню 1С
            Assert.Equal("БанкИКасса", c.Find("Справочник.Валюты").Section);
            Assert.Equal(Access.OtherKey, c.Find("РегистрСведений.КурсыВалют").Section);
            Assert.Null(c.Find("Документ.НачислениеЗарплаты"));                       // нет в конфигурации — не показываем
        }

        [Fact]
        public void Catalog_PersonalData_IsLocked()
        {
            var c = Cat();
            Assert.True(c.Sections.Single(s => s.Key == Access.LockedKey).Locked);
            foreach (var fn in new[] { "Справочник.ФизическиеЛица", "Документ.АвансовыйОтчет" })
            {
                Assert.True(c.Find(fn).Locked);
                Assert.Equal(Access.LockedKey, c.Find(fn).Section);
                Assert.False(c.Find(fn).CanWrite);
            }
        }

        [Fact]
        public void Normalize_WriteImpliesRead_NoWriteForRegistersAndLocked()
        {
            var (r, w) = Access.Normalize(Cat(),
                new[] { "Справочник.Организации", "Справочник.ФизическиеЛица", "Нет.Такого" },
                new[] { "Документ.СчетНаОплатуПокупателю", "РегистрБухгалтерии.Типовой", "Документ.АвансовыйОтчет" });
            Assert.Equal(new[] { "Документ.СчетНаОплатуПокупателю" }, w);
            Assert.Equal(new[] { "Документ.СчетНаОплатуПокупателю", "Справочник.Организации" }, r.OrderBy(x => x, StringComparer.Ordinal));
        }

        /// <summary>Решение 0013: запись — только белый список; реализация, платёжки, регистры — никогда.</summary>
        [Fact]
        public void Write_OnlyWhitelist()
        {
            Assert.Equal(new[] { "Документ.АктСверкиВзаиморасчетов", "Документ.Доверенность", "Документ.СчетНаОплатуПокупателю",
                "Справочник.БанковскиеСчета", "Справочник.ДоговорыКонтрагентов", "Справочник.Контрагенты" },
                Access.WriteWhitelist.OrderBy(x => x, StringComparer.Ordinal));
            var cat = Access.Fallback(Profiles.BpKz);
            Assert.All(cat.Items, i => Assert.Equal(!i.Locked && Access.WriteWhitelist.Contains(i.FullName), i.CanWrite));
            var all = cat.Items.Select(i => i.FullName).ToList();
            var (_, w) = Access.Normalize(cat, all, all);
            Assert.True(w.SetEquals(Access.WriteWhitelist.Where(all.Contains)));
            Assert.DoesNotContain("Документ.РеализацияТоваровУслуг", w);
            Assert.DoesNotContain("Документ.ПлатежноеПоручениеИсходящее", w);
        }

        [Fact]
        public void Diff_AndNeedsOneC()
        {
            var b = new BaseConfig { ODataObjects = new List<string> { "Справочник.Организации", "Справочник.Контрагенты" } };
            var read = new HashSet<string> { "Справочник.Организации", "Документ.СчетНаОплатуПокупателю" };
            var write = new HashSet<string> { "Документ.СчетНаОплатуПокупателю" };
            var c = Access.Diff(b, read, write);
            Assert.Equal(new[] { "Документ.СчетНаОплатуПокупателю" }, c.ReadOn);
            Assert.Equal(new[] { "Справочник.Контрагенты" }, c.ReadOff);
            Assert.Equal(new[] { "Документ.СчетНаОплатуПокупателю" }, c.WriteOn);
            Assert.True(Access.NeedsOneC(b, read, write));
            b.ODataObjects = read.ToList(); b.WriteInOneC = write.ToList(); b.Read = read.ToList(); b.Write = write.ToList();
            Assert.False(Access.Diff(b, read, write).Any);
            Assert.False(Access.NeedsOneC(b, read, write));
        }

        [Fact]
        public void PlanRoles_PicksNarrowest_NeverFullRights()
        {
            var rights = JObject.Parse(@"{
              objects: {
                'Документ.СчетНаОплатуПокупателю': { read: ['ЧтениеДанныхБухгалтерии'], write: ['ПолныеПрава', 'ДобавлениеИзменениеДанныхБухгалтерии', 'ДобавлениеИзменениеСчетовПокупателям', 'УдалитьБухгалтер'] },
                'Справочник.ОсновныеСредства': { read: ['ПолныеПрава', 'ДобавлениеИзменениеДанныхБухгалтерии', 'ЧтениеОС'], write: [] },
                'Документ.Х': { read: [], write: ['ПолныеПрава', 'АдминистраторСистемы'] } },
              breadth: { 'ПолныеПрава': 49, 'ДобавлениеИзменениеДанныхБухгалтерии': 44, 'ДобавлениеИзменениеСчетовПокупателям': 1, 'УдалитьБухгалтер': 0, 'ЧтениеОС': 0, 'ЧтениеДанныхБухгалтерии': 0 }
            }");
            var p = Access.PlanRoles(Profiles.BpKz, Profiles.BpKz.Roles, new[] { "Справочник.ОсновныеСредства" },
                new[] { "Документ.СчетНаОплатуПокупателю", "Документ.Х" }, rights);
            Assert.Equal("ДобавлениеИзменениеСчетовПокупателям", p.Why["Документ.СчетНаОплатуПокупателю"]);
            Assert.Equal("ЧтениеОС", p.Why["Справочник.ОсновныеСредства"]);
            Assert.Equal(new[] { "Документ.Х" }, p.NoRole);
            Assert.DoesNotContain(p.Roles, Access.Forbidden);
        }

        [Fact]
        public void Verifier_WriteTest_AvoidsObjectsWithWrite()
        {
            var b = new BaseConfig { ODataObjects = new List<string> { "Документ.СчетНаОплатуПокупателю", "Документ.РеализацияТоваровУслуг", "Справочник.Контрагенты", "Справочник.Номенклатура" } };
            Assert.Equal("Document_СчетНаОплатуПокупателю", Verifier.WriteTestSet(b, "Документ.", "Документ.СчетНаОплатуПокупателю", "Документ.РеализацияТоваровУслуг"));
            b.Write = new List<string> { "Документ.СчетНаОплатуПокупателю", "Справочник.Контрагенты" };
            Assert.Equal("Document_РеализацияТоваровУслуг", Verifier.WriteTestSet(b, "Документ.", "Документ.СчетНаОплатуПокупателю", "Документ.РеализацияТоваровУслуг"));
            Assert.Equal("Catalog_Номенклатура", Verifier.WriteTestSet(b, "Справочник.", "Справочник.Контрагенты", "Справочник.Номенклатура"));
        }

        [Fact]
        public void Mcp_ReadOff_DeniedBeforeRequest()
        {
            var b = new BaseConfig
            {
                Slug = "t", Configuration = "БухгалтерияДляКазахстана", OData = "http://127.0.0.1:1/none",
                ODataObjects = new List<string> { "Справочник.Организации", "Справочник.Контрагенты" },
                Read = new List<string> { "Справочник.Организации" },
            };
            var rt = new Mcp.BaseRuntime(b, "x");
            var e = Assert.Throws<AgentException>(() => rt.OData.Rows("Catalog_Контрагенты"));
            Assert.Contains("Контрагенты", e.Message);
            Assert.Contains("«Доступ»", e.Message);
            Assert.Null(rt.OData.Deny("Catalog_Организации"));
        }

        /// <summary>Решение 0002: проведения, отмены проведения, удаления и пометки на удаление в коде агента нет.</summary>
        [Fact]
        public void Code_HasNoPostingOrDeletion()
        {
            var root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "Bilem.Agent.sln"))) root = Path.GetDirectoryName(root);
            Assert.NotNull(root);
            var bad = new Regex(@"""DELETE""|/Post\b|/Unpost\b|SetDeletionMark|УстановитьПометкуУдаления|\.Post\(|\.Unpost\(|Провести\(|ОтменаПроведения", RegexOptions.CultureInvariant);
            var hits = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, i, l)))
                .Where(x => bad.IsMatch(x.l))
                .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
                .ToList();
            Assert.Empty(hits);
        }
    }
}
