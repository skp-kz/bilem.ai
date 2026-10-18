using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Bilem.Agent.Core;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    public class BaseDiscoveryTests
    {
        [Fact]
        public void ParsesFileServerAndWebBases()
        {
            var v8i = "\uFEFF[Демо]\r\nConnect=File=\"C:\\Bases\\Demo\";\r\nID=1\r\n[Сервер]\r\nConnect=Srvr=\"srv1\";Ref=\"buh\";\r\n[Веб]\r\nConnect=ws=\"http://x/y\";\r\n";
            var list = BaseDiscovery.Parse(v8i);
            Assert.Equal(3, list.Count);
            Assert.Equal(BaseKind.File, list[0].Kind);
            Assert.Equal("C:\\Bases\\Demo", list[0].Path);
            Assert.Equal("Демо", list[0].Name);
            Assert.Equal(BaseKind.Server, list[1].Kind);
            Assert.Equal("srv1", list[1].Server);
            Assert.Equal("buh", list[1].Ref);
            Assert.Equal(BaseKind.Web, list[2].Kind);
        }

        [Fact]
        public void ConnParamsHandleDoubledQuotes()
        {
            var p = BaseDiscovery.ConnParams("File=\"C:\\a\"\"b\";Usr=\"x\";");
            Assert.Equal("C:\\a\"b", p["file"]);
            Assert.Equal("x", p["usr"]);
        }

        [Theory]
        [InlineData("C:\\Bases\\Demo", "demo")]
        [InlineData("C:\\Bases\\Бухгалтерия 2024\\", "buhgalteriya_2024")]
        [InlineData("C:\\x\\demo", "demo")]
        [InlineData("C:\\x\\!!!", "base")]
        public void SlugFromPath(string path, string slug)
        {
            Assert.Equal(slug, BaseDiscovery.SlugFromPath(path));
            Assert.Matches(Onboarding.SlugRx, BaseDiscovery.SlugFromPath(path));
        }
    }

    public class ComClientTests
    {
        [Fact]
        public void FileConnectionQuotesValues()
        {
            Assert.Equal("File=\"C:\\b\";", ComClient.FileConnection("C:\\b", null, null));
            var cs = ComClient.FileConnection("C:\\a\"b", "Адм", "p\"w");
            // Имя параметра пароля — отдельно от значения (проверка перед push ищет «пароль=» в тексте)
            Assert.Equal("File=\"C:\\a\"\"b\";Usr=\"Адм\";" + "Pwd" + "=\"p\"\"w\";", cs);
        }

        [Fact]
        public void HintsByErrorText()
        {
            Assert.Equal(Txt.S("HintBaseBusy"), ComClient.HintFor("База заблокирована"));
            Assert.Equal(Txt.S("HintNoLicense"), ComClient.HintFor("не обнаружено свободной лицензии"));
        }
    }

    public class ProfileTests
    {
        [Theory]
        [InlineData("1010", CashClass.Cash)]
        [InlineData("1030", CashClass.Bank)]
        [InlineData("1040", CashClass.Bank)]
        [InlineData("1050", CashClass.Bank)]
        [InlineData("1020", CashClass.Other)]
        [InlineData("1080", CashClass.Other)]
        [InlineData("1090", CashClass.Other)]
        public void MoneyClasses(string code, CashClass cls) => Assert.Equal(cls, Profiles.BpKz.ClassifyMoney(code));

        [Theory]
        [InlineData("1010", true)]
        [InlineData("1022", true)]
        [InlineData("1000", false)]
        [InlineData("1210", false)]
        [InlineData("3350", false)]
        [InlineData("10", false)]
        public void MoneyAccounts(string code, bool money) => Assert.Equal(money, Profiles.BpKz.IsMoneyAccount(code));

        [Fact]
        public void PostingPolicyHidesPayrollAndPersons()
        {
            var p = Profiles.BpKz;
            Assert.False(PostingPolicy.CanExpose(p, new[] { "1030", "3350" }, null));
            Assert.False(PostingPolicy.CanExpose(p, new[] { "1251" }, null));
            Assert.False(PostingPolicy.CanExpose(p, new[] { "1210", "6010" }, new[] { "StandardODATA.Catalog_ФизическиеЛица" }));
            Assert.True(PostingPolicy.CanExpose(p, new[] { "1030", "1210" }, new[] { "StandardODATA.Catalog_Контрагенты" }));
        }

        [Fact]
        public void ReadOnlyRolesOnly()
        {
            Assert.DoesNotContain(Profiles.BpKz.Roles, r => r.StartsWith("ДобавлениеИзменение") || r == "ПолныеПрава" || r.StartsWith("Администр"));
            Assert.Equal(6, Profiles.BpKz.Roles.Length);
        }

        [Fact]
        public void UnknownConfigurationFallsBackUntested()
        {
            var p = Profiles.For("НеизвестнаяКонфигурация");
            Assert.False(p.Tested);
            Assert.Equal(Profiles.BpKz.Roles, p.Roles);
        }
    }

    public class SecretsTests
    {
        [Fact]
        public void ConstantTimeEquals()
        {
            Assert.True(Secrets.ConstantTimeEquals("abc", "abc"));
            Assert.False(Secrets.ConstantTimeEquals("abc", "abd"));
            Assert.False(Secrets.ConstantTimeEquals("abc", "abcd"));
            Assert.False(Secrets.ConstantTimeEquals(null, "a"));
        }

        [Fact]
        public void RandomValuesLookRight()
        {
            var p = Secrets.RandomPassword();
            Assert.Equal(24, p.Length);
            Assert.Matches("^[A-Za-z2-9]+$", p);
            Assert.Matches("^[A-Za-z0-9_-]{43}$", Secrets.RandomToken());
            Assert.NotEqual(Secrets.RandomToken(), Secrets.RandomToken());
        }

        [Fact]
        public void DpapiRoundTrip()
        {
            var s = Secrets.Protect("секрет-123");
            Assert.DoesNotContain("секрет", s);
            Assert.Equal("секрет-123", Secrets.Unprotect(s));
        }
    }

    public class ODataTests
    {
        [Theory]
        [InlineData("Справочник.Контрагенты", "Catalog_Контрагенты")]
        [InlineData("Документ.ЧекККМ", "Document_ЧекККМ")]
        [InlineData("РегистрБухгалтерии.Типовой", "AccountingRegister_Типовой")]
        [InlineData("РегистрСведений.КурсыВалют", "InformationRegister_КурсыВалют")]
        [InlineData("ПланСчетов.Типовой", "ChartOfAccounts_Типовой")]
        public void SetNames(string full, string set) => Assert.Equal(set, ODataClient.SetName(full));

        [Fact]
        public void LiteralEscapesQuotes() => Assert.Equal("'O''Brien'", ODataClient.Lit("O'Brien"));

        [Fact]
        public void QueryAddsFormatAndEncodes()
        {
            var q = ODataClient.Query(new System.Collections.Generic.Dictionary<string, string> { ["$filter"] = "a eq 'б'" });
            Assert.Contains("$format=json", q);
            Assert.Contains("$filter=a%20eq%20%27%D0%B1%27", q);
        }

        [Fact]
        public void ErrorTextFromODataAndHtml()
        {
            Assert.Equal("Нарушение прав доступа!", ODataClient.ErrorText("{\"odata.error\":{\"code\":\"\",\"message\":{\"lang\":\"ru\",\"value\":\"Нарушение прав доступа!\"}}}"));
            Assert.Equal("Bad Request", ODataClient.ErrorText("<html><body><h1>Bad Request</h1></body></html>"));
        }

        [Fact]
        public void EntitySetsFromMetadata()
        {
            var sets = ODataClient.EntitySets("<EntitySet Name=\"Catalog_A\" EntityType=\"x\"/><EntitySet Name=\"Document_B\" EntityType=\"y\"/>");
            Assert.Equal(new[] { "Catalog_A", "Document_B" }, sets);
        }
    }

    public class PlatformTests
    {
        [Fact]
        public void RootsByOsBitnessNotProcess()
        {
            var roots = Platform.Roots().ToList();
            if (Environment.Is64BitOperatingSystem)
            {
                Assert.Contains(roots, r => r.Bits == 64 && !r.Root.Contains("(x86)"));
                Assert.Contains(roots, r => r.Bits == 32 && r.Root.Contains("(x86)"));
            }
        }

        [Fact]
        public void ChoosesNewestThen64()
        {
            var list = new System.Collections.Generic.List<Platform1C>
            {
                new Platform1C { Version = new Version("8.3.25.1"), Bits = 64 },
                new Platform1C { Version = new Version("8.3.27.2342"), Bits = 32 },
                new Platform1C { Version = new Version("8.3.27.2342"), Bits = 64 },
            };
            var p = Platform.Choose(list);
            Assert.Equal(new Version("8.3.27.2342"), p.Version);
            Assert.Equal(64, p.Bits);
            Assert.Equal(32, Platform.Choose(list.Where(x => x.Bits == 32).ToList()).Bits);
        }
    }

    public class HealthTests
    {
        private static HealthInput Ok() => new HealthInput { McpRunning = true, BaseCount = 2, BaseErrors = new System.Collections.Generic.Dictionary<string, string> { ["a"] = null, ["b"] = null } };

        [Fact]
        public void GreenWhenAllGood()
        {
            var h = Health.Evaluate(Ok());
            Assert.Equal(AgentHealth.Green, h.Color);
            Assert.Equal("Bilem Agent — работает · баз: 2", h.Tooltip);
        }

        [Fact]
        public void GreyWhenNoBasesOrStopped()
        {
            var i = Ok(); i.BaseCount = 0;
            Assert.Equal(AgentHealth.Grey, Health.Evaluate(i).Color);
            Assert.Contains("базы не подключены", Health.Evaluate(i).Tooltip);
            var s = Ok(); s.McpStoppedByUser = true; s.McpRunning = false;
            Assert.Equal(AgentHealth.Grey, Health.Evaluate(s).Color);
            Assert.Contains("MCP остановлен", Health.Evaluate(s).Tooltip);
        }

        [Fact]
        public void RedWhenMcpFailsOrBaseBroken()
        {
            var i = Ok(); i.McpRunning = false; i.McpError = "порт 8796 занят";
            Assert.Equal(AgentHealth.Red, Health.Evaluate(i).Color);
            var b = Ok(); b.BaseErrors["b"] = "HTTP 503";
            var h = Health.Evaluate(b);
            Assert.Equal(AgentHealth.Red, h.Color);
            Assert.Equal("Bilem Agent — ошибка: база b: HTTP 503", h.Tooltip);
        }

        [Fact]
        public void YellowWhileBusyThenRedAfter60s()
        {
            var now = new DateTime(2026, 10, 18, 12, 0, 0);
            var i = Ok(); i.Now = now; i.BusySince = now.AddSeconds(-30);
            Assert.Equal(AgentHealth.Yellow, Health.Evaluate(i).Color);
            i.BusySince = now.AddSeconds(-61);
            var h = Health.Evaluate(i);
            Assert.Equal(AgentHealth.Red, h.Color);
            Assert.Contains("долго нет ответа", h.Tooltip);
        }

        [Fact]
        public void TooltipNeverLongerThanWindowsLimit()
        {
            var i = Ok(); i.BaseErrors["очень_длинное_имя_базы_для_проверки_подсказки"] = "1С ответила HTTP 500: Ошибка при выполнении операции с информационной базой";
            Assert.True(Health.Evaluate(i).Tooltip.Length <= Health.TooltipMax);
        }
    }

    public class DiagnosticsTests
    {
        [Theory]
        [InlineData(533320, "4.8.1")]
        [InlineData(528040, "4.8")]
        [InlineData(528049, "4.8")]
        [InlineData(461808, "4.7.2")]
        [InlineData(379893, "4.5.2")]
        public void NetVersionByRelease(int release, string name) => Assert.Equal(name, Diagnostics.NetName(release));

        [Fact]
        public void ThisMachineHasNet48() => Assert.True(Diagnostics.NetRelease() >= Diagnostics.Net48Release);

        [Fact]
        public void ReportMarksAndHintsAndMasksToken()
        {
            var res = new System.Collections.Generic.List<CheckResult>
            {
                new CheckResult { Group = "Система", Name = ".NET", Status = CheckStatus.Ok, Detail = "4.8" },
                new CheckResult { Group = "Система", Name = "IIS", Status = CheckStatus.Fail, Detail = "нет", Hint = "включите" },
                new CheckResult { Group = "Агент", Name = "MCP", Status = CheckStatus.Info, Detail = "http://127.0.0.1:8796/mcp/SECRETtoken123" },
            };
            var text = Diagnostics.Report(res);
            Assert.Contains("[OK]   .NET: 4.8", text);
            Assert.Contains("[FAIL] IIS: нет", text);
            Assert.Contains("→ включите", text);
            Assert.DoesNotContain("SECRETtoken123", text);
            Assert.Contains(Txt.F("DiagSummary", 1, 0, 1), text);
        }
    }

    public class ResourcesTests
    {
        /// <summary>Каждый ключ Txt.S/Txt.F из исходников есть в Strings.resx (текст на экране не превратится в [ключ]).</summary>
        [Fact]
        public void AllKeysPresent()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Bilem.Agent.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var keys = Directory.GetFiles(Path.Combine(dir.FullName, "src"), "*.cs", SearchOption.AllDirectories)
                .SelectMany(f => Regex.Matches(File.ReadAllText(f), "Txt\\.(?:S|F)\\(\"([A-Za-z0-9_]+)\"\\s*[,)]").Cast<Match>().Select(m => m.Groups[1].Value))
                .Distinct().ToList();
            Assert.NotEmpty(keys);
            var missing = keys.Where(k => !Txt.Has(k)).ToList();
            Assert.True(missing.Count == 0, "Нет в Strings.resx: " + string.Join(", ", missing));
            foreach (var step in new[] { "admin", "base", "platform", "connect", "user", "odata", "iis", "publish", "verify", "remove", "roles" })
                Assert.True(Txt.Has("Step_" + step), "Step_" + step);
            // Ключи, которые собираются из частей в окне
            foreach (var st in Enum.GetNames(typeof(StepStatus))) Assert.True(Txt.Has("UiStep_" + st), "UiStep_" + st);
            foreach (var c in new[] { "claudecode", "claudedesktop", "cursor" }) Assert.True(Txt.Has("UiConnectDone_" + c), "UiConnectDone_" + c);
            foreach (var k in new[] { "ModeApply", "ModeDiag", "PlanUserCreate", "PlanUserUpdate", "UserCreated", "UserUpdated", "UserUnchanged" }) Assert.True(Txt.Has(k), k);
        }
    }
}
