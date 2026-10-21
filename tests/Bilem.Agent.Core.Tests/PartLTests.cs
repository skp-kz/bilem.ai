using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Bilem.Agent.Core.Cloud;
using Bilem.Agent.Core.Service;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>
    /// Часть L: режимы «бесплатно / Bilem Cloud» взаимоисключающие, подключение базы кодом, канал службы на заглушке облака,
    /// отказ по неподключённой базе, пауза, отключение в кабинете, обрыв и возврат, отзыв ключа.
    /// Пишут config.json и cloud.json — не параллельно с другими.
    /// </summary>
    [Collection("config-exclusive")]
    public class PartLTests : IDisposable
    {
        private readonly CloudStub _stub;
        private readonly int _mcpPort;
        private readonly AgentServer _server;
        private readonly int[] _backoff = CloudChannel.Backoff;

        public PartLTests()
        {
            CloudChannel.Backoff = new[] { 0, 0, 1 };
            _stub = new CloudStub(FreePort()) { PingInterval = 5 };
            _stub.Start();
            _mcpPort = FreePort();
            var cfg = AgentConfig.Load();
            cfg.Port = _mcpPort;
            cfg.Cloud = new CloudSettings { Enabled = false, Url = _stub.Url };
            cfg.Bases.Clear();
            foreach (var slug in new[] { "alpha", "beta" })
            {
                var secret = System.IO.Path.Combine(Paths.Home, "secrets", slug + ".bin");
                Secrets.WriteSecretFile(secret, "x");
                cfg.Bases.Add(new BaseConfig { Slug = slug, Configuration = "Тест", OData = "http://127.0.0.1:1/" + slug + "/odata/standard.odata", SecretFile = secret });
            }
            cfg.Save();
            CloudStore.Update(s => { s.Bindings.Clear(); s.SetKey(null); s.BaseIds.Clear(); });
            _server = new AgentServer { WithPipe = false };
            _server.Start();
        }

        public void Dispose()
        {
            _server.Dispose();
            _stub.Dispose();
            CloudChannel.Backoff = _backoff;
            var cfg = AgentConfig.Load();
            cfg.Cloud = new CloudSettings();
            cfg.Bases.Clear();
            cfg.Save();
            CloudStore.Update(s => { s.Bindings.Clear(); s.SetKey(null); });
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        private static bool Listening(int port)
        {
            try { using (var c = new TcpClient()) { c.Connect(IPAddress.Loopback, port); return true; } }
            catch { return false; }
        }

        private static void Until(Func<bool> cond, string what, int ms = 15000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { if (cond()) return; Thread.Sleep(100); }
            Assert.True(cond(), "не дождались: " + what);
        }

        private static void Cloud(bool on)
        {
            var cfg = AgentConfig.Load();
            cfg.Cloud.Enabled = on;
            cfg.Save();
        }

        private CloudBinding Connect(string slug)
        {
            var req = CloudConnect.Begin(slug);
            Assert.StartsWith(_stub.Url + "/connect?r=", req.ConnectUrl);
            return CloudConnect.Finish(slug, req, _stub.CodeOf(req.RequestId).Insert(4, "-").ToLowerInvariant());
        }

        private static JObject ListBases() => new JObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JObject { ["name"] = "list_bases", ["arguments"] = new JObject { ["base"] = "beta" } },
        };

        [Fact]
        public void Modes_AreExclusive_BothWays()
        {
            Until(() => Listening(_mcpPort), "локальный MCP в бесплатном режиме");
            Cloud(true);
            Until(() => _server.CloudMode && !Listening(_mcpPort), "в Bilem Cloud локальный порт закрыт");
            Assert.False(_server.Host.Running);
            Cloud(false);
            Until(() => !_server.CloudMode && Listening(_mcpPort), "сняли галочку — локальный MCP снова слушает");
            Assert.False(_server.Cloud.Running);
        }

        [Fact]
        public void ConnectByCode_CallOnlyBoundBase_RefuseOthers()
        {
            Cloud(true);
            Until(() => _server.CloudMode, "режим облака");
            var b = Connect("alpha");
            Assert.Equal(CloudStub.OrgName, b.OrgName);
            var store = CloudStore.Load();
            Assert.True(store.HasKey);
            Assert.DoesNotContain(store.Key(), System.IO.File.ReadAllText(CloudStore.FilePath));   // ключ — только зашифрованным
            Until(() => _stub.AgentConnected && _server.Cloud.State == CloudState.Working, "канал открыт");
            Until(() => _stub.LastBases?.Count == 1, "hello со списком баз");
            Assert.Equal(b.BaseId, (string)_stub.LastBases[0]["base_id"]);

            // В аргументах — чужая база, агент ставит свою по base_id: видна только alpha
            var r = _stub.Call(b.BaseId, ListBases()).Result;
            var text = (string)r["result"]["content"][0]["text"];
            Assert.Contains("alpha", text);
            Assert.DoesNotContain("beta", text);

            // Вызов к неподключённой базе — отказ агента, даже если облако «ошиблось»
            var no = _stub.Call(Guid.NewGuid().ToString(), ListBases()).Result;
            Assert.Equal(-32001, (int)no["error"]["code"]);
            // Только tools/list и tools/call
            var init = _stub.Call(b.BaseId, new JObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "initialize" }).Result;
            Assert.Equal(-32601, (int)init["error"]["code"]);
            var list = _stub.Call(b.BaseId, new JObject { ["jsonrpc"] = "2.0", ["id"] = 3, ["method"] = "tools/list" }).Result;
            Assert.True(((JArray)list["result"]["tools"]).Count > 3);

            // Вторая база: ключ тот же, привязок две
            var keyBefore = CloudStore.Load().Key();
            Connect("beta");
            Assert.Equal(keyBefore, CloudStore.Load().Key());
            Until(() => _stub.LastBases?.Count == 2, "bases после второй базы");

            // Отключили в агенте — привязка снята, облаку ушёл новый список
            CloudConnect.Disconnect("beta");
            Assert.Null(CloudStore.Load().Binding("beta"));
            Until(() => _stub.LastBases?.Count == 1, "bases после отключения");
        }

        [Fact]
        public void Codes_Invalid_Expired_Used_TooMany()
        {
            var req = CloudConnect.Begin("alpha");
            var ex = Assert.Throws<CloudException>(() => CloudConnect.Finish("alpha", req, "ZZZZ-ZZZZ"));
            Assert.Equal("code_invalid", ex.Code);
            CloudConnect.Finish("alpha", req, _stub.CodeOf(req.RequestId));
            ex = Assert.Throws<CloudException>(() => CloudConnect.Finish("alpha", req, _stub.CodeOf(req.RequestId)));
            Assert.Equal("code_used", ex.Code);

            var old = CloudConnect.Begin("beta");
            _stub.Expire(old.RequestId);
            ex = Assert.Throws<CloudException>(() => CloudConnect.Finish("beta", old, _stub.CodeOf(old.RequestId)));
            Assert.Equal("code_expired", ex.Code);

            var many = CloudConnect.Begin("beta");
            for (var i = 0; i < 4; i++) Assert.Equal("code_invalid", Assert.Throws<CloudException>(() => CloudConnect.Finish("beta", many, "WRONG000")).Code);
            Assert.Equal("too_many_attempts", Assert.Throws<CloudException>(() => CloudConnect.Finish("beta", many, "WRONG000")).Code);
            Assert.Equal("too_many_attempts", Assert.Throws<CloudException>(() => CloudConnect.Finish("beta", many, _stub.CodeOf(many.RequestId))).Code);
        }

        [Fact]
        public void Pause_Revoke_Drop_KeyRevoked()
        {
            Cloud(true);
            var b = Connect("alpha");
            Until(() => _server.Cloud.State == CloudState.Working, "канал открыт");

            _stub.SetState(b.BaseId, "paused", "unpaid").Wait();
            Until(() => (string)_server.Cloud.Status()["bases"][0]["state"] == "paused", "пауза в статусе");
            Assert.Equal(-32003, (int)_stub.Call(b.BaseId, ListBases()).Result["error"]["code"]);
            _stub.SetState(b.BaseId, "active", null).Wait();
            Until(() => (string)_server.Cloud.Status()["bases"][0]["state"] == "active", "снова работает");

            // Обрыв связи — агент переподключается сам
            var hellos = _stub.Hellos;
            _stub.Drop();
            Until(() => _stub.Hellos > hellos && _stub.AgentConnected, "переподключение после обрыва", 20000);
            Assert.Null(_stub.Call(b.BaseId, ListBases()).Result["error"]);

            // Отключили в кабинете — привязка снята у агента, вызовы — отказ
            _stub.SetState(b.BaseId, "revoked", "cabinet").Wait();
            Until(() => CloudStore.Load().Binding("alpha")?.RevokedInCabinet == true, "отключена в кабинете");
            Assert.Equal(-32001, (int)_stub.Call(b.BaseId, ListBases()).Result["error"]["code"]);

            // Отзыв ключа (4401) — ключ удалён, не переподключаемся
            _stub.RevokeKey().Wait();
            Until(() => _server.Cloud.State == CloudState.KeyRevoked && !CloudStore.Load().HasKey, "ключ отозван");
            Thread.Sleep(1500);
            Assert.False(_stub.AgentConnected);
        }

        [Fact]
        public void StaleKey_ForgottenOnConnect_NewKeyIssued()
        {
            // Ключ, которого облако не знает (отозван, другое облако): заявка — без него, обмен выдаёт новый
            CloudStore.Update(s => { s.EnsureAgentId(); s.SetKey("stale-key"); });
            var b = Connect("alpha");
            Assert.NotNull(b);
            var key = CloudStore.Load().Key();
            Assert.False(string.IsNullOrEmpty(key));
            Assert.NotEqual("stale-key", key);
        }

        [Fact]
        public void Url_HttpsOrLocalOnly_AndStatusHasNoKey()
        {
            Assert.Throws<AgentException>(() => CloudApi.CheckUrl("http://mcp.bilem.ai"));
            Assert.Equal("wss://mcp.bilem.ai/agent/v1/channel", CloudApi.ChannelUri("https://mcp.bilem.ai").ToString());
            Assert.Equal("ws://127.0.0.1:18790/agent/v1/channel", CloudApi.ChannelUri("http://127.0.0.1:18790").ToString());
            Assert.Equal("ABCD2345", CloudApi.NormalizeCode(" abcd-2345 "));
            Cloud(true);
            Connect("alpha");
            Until(() => _server.Cloud.State == CloudState.Working, "канал открыт");
            var st = _server.Status().ToString();
            Assert.DoesNotContain(CloudStore.Load().Key(), st);
            Assert.Contains("\"mode\": \"cloud\"", st);
        }
    }
}
