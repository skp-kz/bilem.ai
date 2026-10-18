using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Bilem.Agent.Core;
using Bilem.Agent.Core.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    public class FormsTests
    {
        /// <summary>Все окна и диалоги — от BilemForm: у всех иконка приложения, а не стандартная иконка Windows.</summary>
        [Fact]
        public void EveryFormDerivesFromBilemForm()
        {
            var forms = typeof(Bilem.Agent.BilemForm).Assembly.GetTypes()
                .Where(t => typeof(Form).IsAssignableFrom(t) && t != typeof(Bilem.Agent.BilemForm)).ToList();
            Assert.NotEmpty(forms);
            foreach (var f in forms) Assert.True(typeof(Bilem.Agent.BilemForm).IsAssignableFrom(f), f.Name + " не наследуется от BilemForm");
        }

        [Fact]
        public void AppIconComesFromResourceWithAllSizes()
        {
            using (var s = typeof(Bilem.Agent.BilemForm).Assembly.GetManifestResourceStream("bilem-agent.ico"))
            {
                Assert.NotNull(s);
                var head = new byte[6];
                s.Read(head, 0, 6);
                Assert.True(BitConverter.ToUInt16(head, 4) >= 9, "в иконке приложения мало размеров");
            }
            using (var f = new Bilem.Agent.BilemForm())
            {
                Assert.NotSame(SystemIcons.Application, f.Icon);
                Assert.Equal(AutoScaleMode.Dpi, f.AutoScaleMode);
            }
            foreach (var t in new[] { "tray-green.ico", "tray-yellow.ico", "tray-red.ico", "tray-grey.ico" })
                Assert.NotNull(typeof(Bilem.Agent.BilemForm).Assembly.GetManifestResourceStream(t));
        }
    }

    public class FilterEncodingTests
    {
        /// <summary>Пробел внутри $filter — %20: «+» 1С не декодирует и отвечает 400 (known-issues 14).</summary>
        [Fact]
        public void SpaceIsPercent20NotPlus()
        {
            var q = ODataClient.Query(new Dictionary<string, string> { ["$filter"] = Tools.FindFilter("Мост отряд") });
            Assert.Contains("%20", q);
            Assert.DoesNotContain("+", q);
            Assert.Contains(Uri.EscapeDataString("'Мост отряд'"), q);
        }
    }

    public class LimitTests
    {
        private static JObject WithItems(int n) => new JObject { ["found"] = n, ["items"] = new JArray(Enumerable.Range(1, n).Select(i => new JObject { ["name"] = "x" + i })) };

        [Fact]
        public void TruncatesWithNote()
        {
            var o = Tools.Limit(WithItems(250), 200);
            Assert.Equal(200, ((JArray)o["items"]).Count);
            Assert.Equal(200, (int)o["found"]);
            Assert.Equal(Txt.F("ToolTruncated", 200, 250), (string)o["note"]);
        }

        [Fact]
        public void LeavesSmallAnswersAlone()
        {
            var o = Tools.Limit(WithItems(5), 200);
            Assert.Equal(5, ((JArray)o["items"]).Count);
            Assert.Null(o["note"]);
        }

        [Fact]
        public void NestedArraysAndServiceFields()
        {
            var o = new JObject
            {
                ["_total"] = 7,
                ["bases"] = new JArray(new JObject { ["base"] = "a", ["_total"] = 3, ["organizations"] = new JArray(Enumerable.Range(1, 5).Select(i => new JObject { ["name"] = i })) }),
            };
            Tools.Limit(o, 2);
            Assert.Null(o["_total"]);
            var b = (JObject)o["bases"][0];
            Assert.Null(b["_total"]);
            Assert.Equal(2, ((JArray)b["organizations"]).Count);
            Assert.Equal(Txt.F("ToolTruncated", 2, 5), (string)b["note"]);
        }

        [Fact]
        public void EveryToolIsWrapped()
        {
            var tools = Tools.Create(() => new List<BaseRuntime>(), 1);
            var lb = tools.First(t => t.Name == "list_bases").Handler(new JObject());
            Assert.NotNull(lb["bases"]);
        }
    }
}
