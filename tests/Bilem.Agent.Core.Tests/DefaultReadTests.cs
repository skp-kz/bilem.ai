using System.Collections.Generic;
using System.Linq;
using Bilem.Agent.Core.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>#104: «ГТД по импорту» — чтение по умолчанию, плашка «Обновить права Bilem», понятный ответ customs_declarations.</summary>
    public class DefaultReadTests
    {
        private static AccessCatalog Cat(bool withGtd) => Access.FromMeta(Profiles.BpKz, JObject.Parse(@"{
          subsystems: [ { name: 'Покупка', synonym: 'Покупка' }, { name: 'Продажа', synonym: 'Продажа' } ],
          objects: [
            { fullName: 'Справочник.Контрагенты', exists: true, synonym: 'Контрагенты', sections: ['Покупка', 'Продажа'] },
            { fullName: 'Документ.ГТДИмпорт', exists: " + (withGtd ? "true" : "false") + @", synonym: 'ГТД по импорту', sections: ['Покупка'] } ]
        }"));

        private static BaseConfig Base(bool withGtd, IEnumerable<string> odata, IEnumerable<string> seen = null) => new BaseConfig
        {
            Slug = "inv-demo", Configuration = "БухгалтерияДляКазахстана", Catalog = Cat(withGtd),
            ODataObjects = odata.ToList(), ReadDefaults = (seen ?? new string[0]).ToList(),
        };

        [Fact]
        public void DefaultRead_HasGtdImport_AndIsNotPersonalOrWrite()
        {
            Assert.Contains("Документ.ГТДИмпорт", Access.DefaultRead);
            Assert.DoesNotContain(Access.DefaultRead, Profiles.PersonalDataObjects.Contains);
            Assert.DoesNotContain(Access.DefaultRead, Access.WriteWhitelist.Contains);
            Assert.All(Access.DefaultRead, x => Assert.Contains(x, Profiles.BpKz.CatalogObjects));
        }

        [Fact]
        public void Missing_OldComposition_ShowsRightsButton()
        {
            var b = Base(true, new[] { "Справочник.Контрагенты" });
            Assert.Equal(new[] { "Документ.ГТДИмпорт" }, Access.MissingDefaultRead(b));
        }

        [Fact]
        public void Missing_CatalogLoadedBeforeBeta17_StillShowsButton()
        {
            var b = Base(false, new[] { "Справочник.Контрагенты" });
            b.Catalog.Asked = null;   // каталог из старой версии: про «ГТД по импорту» 1С не спрашивали
            Assert.Equal(new[] { "Документ.ГТДИмпорт" }, Access.MissingDefaultRead(b));
            Assert.Contains("«Обновить права Bilem»", Customs.WhyOff(b));
        }

        [Fact]
        public void Missing_NotInConfiguration_Silent()
        {
            var b = Base(false, new[] { "Справочник.Контрагенты" });
            Assert.Empty(Access.MissingDefaultRead(b));
            Assert.Equal(Txt.F("CustomsMissing", "inv-demo"), Customs.WhyOff(b));
        }

        [Fact]
        public void Missing_AlreadyInComposition_OrTurnedOffByHuman_NoButton()
        {
            Assert.Empty(Access.MissingDefaultRead(Base(true, new[] { "Справочник.Контрагенты", "Документ.ГТДИмпорт" })));
            var off = Base(true, new[] { "Справочник.Контрагенты" }, seen: new[] { "Документ.ГТДИмпорт" });
            Assert.Empty(Access.MissingDefaultRead(off));
            Assert.Equal(Txt.F("CustomsOff", "inv-demo"), Customs.WhyOff(off));
        }

        [Fact]
        public void WhyOff_OldComposition_SaysPressRefreshRights()
        {
            var say = Customs.WhyOff(Base(true, new[] { "Справочник.Контрагенты" }));
            Assert.Contains("«Доступ» → «Обновить права Bilem»", say);
        }

        [Fact]
        public void MarkReadDefaults_RecordsOnlyWhatIsInComposition()
        {
            var b = Base(true, new[] { "Справочник.Контрагенты" });
            Access.MarkReadDefaults(b);
            Assert.Empty(b.ReadDefaults);
            b.ODataObjects.Add("Документ.ГТДИмпорт");
            Access.MarkReadDefaults(b);
            Access.MarkReadDefaults(b);
            Assert.Equal(new[] { "Документ.ГТДИмпорт" }, b.ReadDefaults);
        }
    }
}
