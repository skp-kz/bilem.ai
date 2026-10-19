using System;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Bilem.Agent.Core;
using Bilem.Agent.Core.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    public class McpProtocolTests
    {
        private static McpProtocol Proto() => new McpProtocol(new[]
        {
            new McpTool { Name = "echo", Title = "e", Description = "d", InputSchema = new JObject { ["type"] = "object" }, Handler = a => new JObject { ["got"] = a["x"] } },
            new McpTool { Name = "boom", Title = "b", Description = "d", InputSchema = new JObject { ["type"] = "object" }, Handler = a => throw new AgentException("нет базы «x»") },
        }, "0.1.0");

        private static JObject Call(McpProtocol p, string json) => JObject.Parse(p.HandleRaw(json));

        [Fact]
        public void ListChanged_CapabilityAndVisibility()
        {
            var p = Proto();
            var init = Call(p, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}");
            Assert.True((bool)init["result"]["capabilities"]["tools"]["listChanged"]);
            Assert.Equal("echo,boom", p.ToolsSignature());
            p.Visible = t => t.Name != "boom";
            var list = (JArray)Call(p, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}")["result"]["tools"];
            Assert.Equal(new[] { "echo" }, list.Select(x => (string)x["name"]));
            Assert.Equal("echo", p.ToolsSignature());
        }

        /// <summary>GET text/event-stream — поток уведомлений; Notify доставляет notifications/tools/list_changed.</summary>
        [Fact]
        public void Sse_DeliversListChanged()
        {
            var port = McpHttpTests.FreePortForTests();
            using (var srv = new McpHttpServer(port, () => "tok", new McpProtocol(new McpTool[0], "0")))
            {
                srv.Start();
                using (var c = new System.Net.Sockets.TcpClient("127.0.0.1", port))
                {
                    var st = c.GetStream();
                    var req = System.Text.Encoding.ASCII.GetBytes("GET /mcp/tok HTTP/1.1\r\nHost: 127.0.0.1\r\nAccept: text/event-stream\r\n\r\n");
                    st.Write(req, 0, req.Length);
                    for (var i = 0; i < 50 && srv.StreamCount == 0; i++) System.Threading.Thread.Sleep(50);
                    Assert.Equal(1, srv.StreamCount);
                    srv.Notify(McpProtocol.ListChangedNotification);
                    st.ReadTimeout = 3000;
                    var buf = new byte[4096]; var got = new System.Text.StringBuilder();
                    while (!got.ToString().Contains("list_changed")) { var n = st.Read(buf, 0, buf.Length); if (n <= 0) break; got.Append(System.Text.Encoding.UTF8.GetString(buf, 0, n)); }
                    Assert.Contains("text/event-stream", got.ToString());
                    Assert.Contains("notifications/tools/list_changed", got.ToString());
                }
            }
        }

        /// <summary>print_invoice: PDF ресурсом и картинка — отдельными элементами content после текста, не внутри JSON.</summary>
        [Fact]
        public void ExtraContentGoesAfterText()
        {
            var p = new McpProtocol(new[]
            {
                new McpTool { Name = "file", Title = "f", Description = "d", InputSchema = new JObject { ["type"] = "object" }, Handler = a => new JObject
                {
                    ["ok"] = true,
                    [McpProtocol.ExtraContent] = new JArray(
                        new JObject { ["type"] = "resource", ["resource"] = new JObject { ["uri"] = "file:///a.pdf", ["mimeType"] = "application/pdf", ["blob"] = "JVBERg==" } },
                        new JObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = "iVBO" }),
                } },
            }, "0.1.0");
            var c = (JArray)Call(p, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"file\",\"arguments\":{}}}")["result"]["content"];
            Assert.Equal(new[] { "text", "resource", "image" }, c.Select(x => (string)x["type"]));
            Assert.Null(JObject.Parse((string)c[0]["text"])[McpProtocol.ExtraContent]);
            Assert.Equal("application/pdf", (string)c[1]["resource"]["mimeType"]);
        }

        [Fact]
        public void InitializeEchoesSupportedVersion()
        {
            var r = Call(Proto(), "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\"}}");
            Assert.Equal("2025-03-26", (string)r["result"]["protocolVersion"]);
            Assert.NotNull(r["result"]["capabilities"]["tools"]);
            Assert.Equal("bilem-agent", (string)r["result"]["serverInfo"]["name"]);
            var r2 = Call(Proto(), "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"1999-01-01\"}}");
            Assert.Equal(McpProtocol.SupportedVersions[0], (string)r2["result"]["protocolVersion"]);
        }

        [Fact]
        public void NotificationsHaveNoReply()
        {
            Assert.Null(Proto().HandleRaw("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"));
        }

        [Fact]
        public void PingAndUnknownMethod()
        {
            Assert.NotNull(Call(Proto(), "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\"}")["result"]);
            Assert.Equal(-32601, (int)Call(Proto(), "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"nope\"}")["error"]["code"]);
            Assert.Equal(-32700, (int)Call(Proto(), "{not json")["error"]["code"]);
        }

        [Fact]
        public void ToolsListAndCall()
        {
            var p = Proto();
            var list = Call(p, "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/list\"}");
            Assert.Equal(2, ((JArray)list["result"]["tools"]).Count);
            var ok = Call(p, "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\",\"params\":{\"name\":\"echo\",\"arguments\":{\"x\":7}}}");
            var payload = JObject.Parse((string)ok["result"]["content"][0]["text"]);
            Assert.Equal(7, (int)payload["got"]);
            Assert.NotNull(payload["ms"]);
            var err = Call(p, "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"boom\",\"arguments\":{}}}");
            Assert.True((bool)err["result"]["isError"]);
            Assert.Equal("нет базы «x»", (string)err["result"]["content"][0]["text"]);
        }

        [Fact]
        public void RealToolsHaveRussianDescriptionsAndSchemas()
        {
            var tools = Tools.Create(() => new List<BaseRuntime>());
            Assert.Equal(new[] { "list_bases", "whoami", "find_counterparty", "cash_balance", "print_invoice", "print_power_of_attorney", "print_reconciliation", "receivables", "payables", "send_document" }, tools.ConvertAll(t => t.Name));
            foreach (var t in tools)
            {
                Assert.False(string.IsNullOrEmpty(t.Description));
                Assert.DoesNotContain("[", t.Description.Substring(0, 1));
                Assert.Equal("object", (string)t.InputSchema["type"]);
            }
        }
    }

    public class ToolsLogicTests
    {
        private static BaseRuntime Fake(string slug) => new BaseRuntime(new BaseConfig { Slug = slug, Configuration = "БухгалтерияДляКазахстана", OData = "http://127.0.0.1/x", Version = "3.0" }, "p");

        [Fact]
        public void BaseGivenUnknownIsError()
        {
            var all = new List<BaseRuntime> { Fake("a"), Fake("b") };
            var e = Assert.Throws<AgentException>(() => Tools.PerBase(all, "zzz", b => new JObject(), rows => new JObject()));
            Assert.Contains("a, b", e.Message);
        }

        [Fact]
        public void NoBaseAndSeveralMeansAll()
        {
            var all = new List<BaseRuntime> { Fake("a"), Fake("b") };
            var r = Tools.PerBase(all, null, b => new JObject { ["base"] = b.Config.Slug }, rows => new JObject { ["n"] = rows.Count });
            Assert.Equal(2, (int)r["n"]);
            var single = Tools.PerBase(new List<BaseRuntime> { Fake("a") }, null, b => new JObject { ["base"] = b.Config.Slug }, rows => new JObject { ["n"] = rows.Count });
            Assert.Equal("a", (string)single["base"]);
        }

        [Fact]
        public void OneBaseFailingDoesNotHideOthers()
        {
            var all = new List<BaseRuntime> { Fake("a"), Fake("b") };
            var r = Tools.PerBase(all, null, b => b.Config.Slug == "a" ? throw new AgentException("недоступна") : new JObject { ["base"] = "b" },
                rows => new JObject { ["rows"] = new JArray(rows) });
            Assert.Equal("недоступна", (string)r["rows"][0]["error"]);
            Assert.Equal("b", (string)r["rows"][1]["base"]);
        }

        [Theory]
        [InlineData(0.0, "0")]
        [InlineData(-45000.0, "-45000")]
        [InlineData(71708999.18, "71708999.18")]
        [InlineData(52329464.255, "52329464.26")]
        [InlineData(0.1 + 0.2, "0.3")]
        public void MoneyLikeJs(double v, string json) => Assert.Equal(json, Tools.Money(v).ToString());

        [Fact]
        public void FindFilterByNameAndBin()
        {
            Assert.StartsWith("substringof('Мост', Description)", Tools.FindFilter("Мост"));
            Assert.StartsWith("ИдентификационныйКодЛичности eq '030540008810'", Tools.FindFilter("030540008810"));
            Assert.StartsWith("substringof('0540', ИдентификационныйКодЛичности)", Tools.FindFilter("0540"));
            Assert.Contains("O''Brien", Tools.FindFilter("O'Brien"));
            Assert.EndsWith("and IsFolder eq false and DeletionMark eq false", Tools.FindFilter("x1"));
        }
    }

    public class McpHttpTests
    {
        internal static int FreePortForTests() => FreePort();

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static HttpReply Send(string method, string url, string body = null, string origin = null)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Proxy = null;
            if (origin != null) req.Headers["Origin"] = origin;
            if (body != null)
            {
                var b = System.Text.Encoding.UTF8.GetBytes(body);
                req.ContentType = "application/json";
                req.ContentLength = b.Length;
                using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            try { using (var r = (HttpWebResponse)req.GetResponse()) using (var sr = new System.IO.StreamReader(r.GetResponseStream())) return new HttpReply { Code = (int)r.StatusCode, Body = sr.ReadToEnd() }; }
            catch (WebException e) when (e.Response is HttpWebResponse r) { return new HttpReply { Code = (int)r.StatusCode }; }
        }

        [Fact]
        public void TokenOriginAndMethodChecks()
        {
            var port = McpHttpTests.FreePortForTests();
            var proto = new McpProtocol(new McpTool[0], "0.1.0");
            using (var srv = new McpHttpServer(port, () => "tok123", proto))
            {
                srv.Start();
                var init = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}";
                Assert.Equal(401, Send("POST", $"http://127.0.0.1:{port}/mcp", init).Code);
                Assert.Equal(401, Send("POST", $"http://127.0.0.1:{port}/mcp/wrong", init).Code);
                Assert.Equal(403, Send("POST", $"http://127.0.0.1:{port}/mcp/tok123", init, "http://evil.example").Code);
                Assert.Equal(405, Send("GET", $"http://127.0.0.1:{port}/mcp/tok123").Code);
                Assert.Equal(404, Send("POST", $"http://127.0.0.1:{port}/other", init).Code);
                var ok = Send("POST", $"http://127.0.0.1:{port}/mcp/tok123", init);
                Assert.Equal(200, ok.Code);
                Assert.Equal("bilem-agent", (string)JObject.Parse(ok.Body)["result"]["serverInfo"]["name"]);
                Assert.Equal(202, Send("POST", $"http://127.0.0.1:{port}/mcp/tok123", "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}").Code);
            }
        }

        [Fact]
        public void ListensOnLoopbackOnly()
        {
            var port = McpHttpTests.FreePortForTests();
            using (var srv = new McpHttpServer(port, () => "t", new McpProtocol(new McpTool[0], "0")))
            {
                srv.Start();
                foreach (var ip in Verifier.MachineAddresses())
                {
                    using (var c = new TcpClient())
                    {
                        var ar = c.BeginConnect(IPAddress.Parse(ip), port, null, null);
                        var connected = ar.AsyncWaitHandle.WaitOne(2000) && c.Connected;
                        Assert.False(connected, "сервер доступен по адресу машины");
                    }
                }
            }
        }
    }
}
