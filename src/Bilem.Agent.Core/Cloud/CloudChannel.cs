using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bilem.Agent.Core.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Cloud
{
    public enum CloudState { Off, NoKey, Connecting, Working, NoConnection, KeyRevoked, Replaced }

    /// <summary>
    /// Канал службы к Bilem Cloud (протокол stand/cloud-channel-protocol, раздел 4): одно исходящее соединение WebSocket по TLS,
    /// вызовы инструментов MCP только к привязанным базам, ответ — как у локального MCP. Входящих портов нет.
    /// Переподключение — само, с паузами; ключ отозван (4401) — не переподключаемся, пока не появится новый.
    /// </summary>
    public sealed class CloudChannel : IDisposable
    {
        public const int MaxConcurrent = 4;
        public const int MaxResultBytes = 8 * 1024 * 1024;
        private const int MaxIncomingBytes = 4 * 1024 * 1024;
        public const int DefaultDeadlineMs = 60000;
        internal static int[] Backoff = { 1, 2, 5, 10, 30, 60 };
        internal static int BackoffLongSec = 300;
        internal static int ReplacedPauseSec = 300;

        private readonly McpHost _host;
        private readonly SemaphoreSlim _calls = new SemaphoreSlim(MaxConcurrent);
        private readonly SemaphoreSlim _send = new SemaphoreSlim(1);
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new ConcurrentDictionary<string, CancellationTokenSource>();
        private readonly Dictionary<string, (string State, string Reason)> _baseStates = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource _stop;
        private Task _loop;
        private ClientWebSocket _ws;
        private string _sentBases = "";
        private DateTime _lastPong;
        private volatile bool _welcomed;

        public CloudState State { get; private set; } = CloudState.Off;
        public DateTime? LastContact { get; private set; }
        public string LastError { get; private set; }
        public int PingInterval { get; private set; } = 25;
        public bool Running => _loop != null && !_loop.IsCompleted;

        public CloudChannel(McpHost host)
        {
            _host = host;
            _host.ListChanged += () => { var _ = SendBasesIfChanged(true); };
        }

        public void Start()
        {
            if (Running) return;
            _stop = new CancellationTokenSource();
            State = CloudState.Connecting;
            var ct = _stop.Token;
            _loop = Task.Run(() => Loop(ct));
            Log.Info(Txt.F("CloudChannelStarting", AgentConfig.Load().Cloud.EffectiveUrl));
        }

        /// <summary>Сняли галочку Bilem Cloud: закрыть канал (1000, mode_off); ключ и привязки остаются.</summary>
        public void Stop()
        {
            if (_stop == null) return;
            var ws = _ws;
            try { if (ws?.State == WebSocketState.Open) ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "mode_off", CancellationToken.None).Wait(3000); } catch { }
            _stop.Cancel();
            try { _loop?.Wait(5000); } catch { }
            _stop = null; _loop = null; _ws = null;
            State = CloudState.Off;
            Log.Info(Txt.S("CloudChannelStopped"));
        }

        public void Dispose() => Stop();

        // ---------------- соединение ----------------

        private async Task Loop(CancellationToken ct)
        {
            var attempt = 0;
            while (!ct.IsCancellationRequested)
            {
                var store = CloudStore.Load();
                var key = store.Key();
                if (key == null)
                {
                    // Ключа нет (ещё не подключали базу или отозван) — ждём, пока окно подключит базу кодом
                    if (State != CloudState.KeyRevoked) State = CloudState.NoKey;
                    await Delay(3, ct).ConfigureAwait(false);
                    continue;
                }
                if (State == CloudState.KeyRevoked) State = CloudState.Connecting;
                int? pause = null;
                _welcomed = false;
                try
                {
                    pause = await Session(store, key, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception e)
                {
                    LastError = (e.InnerException ?? e).Message;
                    if (State != CloudState.NoConnection) Log.Warn(Txt.F("CloudNoConnectionLog", LastError));
                    State = CloudState.NoConnection;
                }
                if (State == CloudState.KeyRevoked) continue;
                // Сессия дошла до welcome — связь была: после обрыва (деплой облака, сеть) переподключаемся сразу, без накопленной паузы
                if (_welcomed) attempt = 0;
                var wait = pause ?? (attempt < Backoff.Length ? Backoff[attempt] : BackoffLongSec);
                attempt++;
                await Delay(wait + Jitter(wait), ct).ConfigureAwait(false);
            }
        }

        private static readonly Random Rnd = new Random();
        private static double Jitter(int sec) { lock (Rnd) return Rnd.NextDouble() * Math.Min(5, sec * 0.2); }

        private static async Task Delay(double sec, CancellationToken ct)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(sec), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        /// <summary>Одно соединение: hello → welcome → вызовы и пинги до разрыва. Возвращает паузу до переподключения (или null — по умолчанию).</summary>
        private async Task<int?> Session(CloudStore store, string key, CancellationToken ct)
        {
            var cfg = AgentConfig.Load();
            var uri = CloudApi.ChannelUri(cfg.Cloud.EffectiveUrl);
            using (var ws = new ClientWebSocket())
            {
                ws.Options.SetRequestHeader("Authorization", "Bearer " + key);
                ws.Options.SetRequestHeader("X-Bilem-Agent", store.EnsureAgentId());
                ws.Options.KeepAliveInterval = TimeSpan.Zero;   // свой ping/pong по протоколу
                if (!uri.IsLoopback) ws.Options.Proxy = CloudApi.SystemProxy();
                State = CloudState.Connecting;
                using (var connectCt = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectCt.CancelAfter(TimeSpan.FromSeconds(30));
                    await ws.ConnectAsync(uri, connectCt.Token).ConfigureAwait(false);
                }
                _ws = ws;
                _lastPong = DateTime.UtcNow;
                using (var sessionCt = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    var pinger = Task.Run(() => PingLoop(ws, sessionCt.Token));
                    var watcher = Task.Run(() => WatchBases(sessionCt.Token));
                    try
                    {
                        try { await Send(Hello(store, cfg), ct).ConfigureAwait(false); }
                        catch (Exception) when (!ct.IsCancellationRequested) { /* облако уже закрывает (например, 4401) — код прочитаем ниже */ }
                        while (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                        {
                            if (ct.IsCancellationRequested) break;
                            var msg = await Receive(ws, sessionCt.Token).ConfigureAwait(false);
                            if (msg == null) break;
                            LastContact = DateTime.Now;
                            Handle(msg, ct);
                        }
                    }
                    catch (Exception) when (ws.CloseStatus.HasValue && !ct.IsCancellationRequested) { /* закрыто облаком — разбираем код */ }
                    finally
                    {
                        sessionCt.Cancel();
                        foreach (var c in _running.Values) { try { c.Cancel(); } catch { } }
                        _ws = null;
                    }
                }
                return Closed(ws.CloseStatus, ws.CloseStatusDescription);
            }
        }

        /// <summary>Коды закрытия облаком (протокол, раздел 4).</summary>
        private int? Closed(WebSocketCloseStatus? status, string why)
        {
            var code = (int?)status;
            switch (code)
            {
                case 4401:
                    // Ключ неверен или отозван: удалить ключ, не переподключаться (статус «Ключ отозван — подключите базы заново»)
                    CloudStore.Update(s => { s.SetKey(null); foreach (var b in s.Bindings) b.RevokedInCabinet = true; });
                    State = CloudState.KeyRevoked;
                    Log.Warn(Txt.S("CloudKeyRevokedLog"));
                    return null;
                case 4409:
                    State = CloudState.Replaced;
                    Log.Warn(Txt.S("CloudReplacedLog"));
                    return ReplacedPauseSec;
                case 4429:
                    State = CloudState.NoConnection;
                    return BackoffLongSec;
                default:
                    if (State == CloudState.Working) Log.Info(Txt.F("CloudClosedLog", code?.ToString() ?? "—", why ?? ""));
                    State = CloudState.NoConnection;
                    return null;
            }
        }

        private JObject Hello(CloudStore store, AgentConfig cfg) => new JObject
        {
            ["type"] = "hello",
            ["agent_id"] = store.AgentId,
            ["agent_version"] = McpHost.Version,
            ["machine"] = Environment.MachineName,
            ["bases"] = BasesArray(store, cfg),
        };

        private JArray BasesArray(CloudStore store, AgentConfig cfg)
        {
            var tools = new JArray((_host.Protocol?.Tools ?? new List<McpTool>()).Where(t => _host.Protocol.Visible(t)).Select(t => t.Name));
            var arr = new JArray(store.Active(cfg).OrderBy(b => b.Slug).Select(b => new JObject { ["base_id"] = b.BaseId, ["slug"] = b.Slug, ["tools"] = tools.DeepClone() }));
            _sentBases = arr.ToString(Formatting.None);
            return arr;
        }

        /// <summary>Подключили / отключили / удалили базу — облаку полный список (сообщение bases).</summary>
        private async Task SendBasesIfChanged(bool force = false)
        {
            if (_ws?.State != WebSocketState.Open) return;
            try
            {
                var store = CloudStore.Load();
                var cfg = AgentConfig.Load();
                var before = _sentBases;
                var arr = BasesArray(store, cfg);
                if (!force && arr.ToString(Formatting.None) == before) return;
                await Send(new JObject { ["type"] = "bases", ["bases"] = arr }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) { Log.Warn(e.Message); }
        }

        private async Task WatchBases(CancellationToken ct)
        {
            DateTime stamp = DateTime.MinValue, cfgStamp = DateTime.MinValue;
            while (!ct.IsCancellationRequested)
            {
                await Delay(2, ct).ConfigureAwait(false);
                try
                {
                    var s = File.Exists(CloudStore.FilePath) ? File.GetLastWriteTimeUtc(CloudStore.FilePath) : DateTime.MinValue;
                    var c = File.Exists(Paths.ConfigFile) ? File.GetLastWriteTimeUtc(Paths.ConfigFile) : DateTime.MinValue;
                    if (s == stamp && c == cfgStamp) continue;
                    stamp = s; cfgStamp = c;
                    await SendBasesIfChanged().ConfigureAwait(false);
                }
                catch { }
            }
        }

        private async Task PingLoop(ClientWebSocket ws, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Delay(PingInterval, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                // Нет pong 3 интервала подряд — соединение мёртвое: обрываем, цикл переподключится
                if ((DateTime.UtcNow - _lastPong).TotalSeconds > PingInterval * 3) { try { ws.Abort(); } catch { } return; }
                try { await Send(new JObject { ["type"] = "ping", ["t"] = DateTime.UtcNow.ToString("o") }, ct).ConfigureAwait(false); } catch { }
            }
        }

        private async Task Send(JObject msg, CancellationToken ct)
        {
            var ws = _ws;
            if (ws == null) return;
            var bytes = new UTF8Encoding(false).GetBytes(msg.ToString(Formatting.None));
            await _send.WaitAsync(ct).ConfigureAwait(false);
            try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
            finally { _send.Release(); }
        }

        private static async Task<JObject> Receive(ClientWebSocket ws, CancellationToken ct)
        {
            var buf = new byte[64 * 1024];
            using (var ms = new MemoryStream())
            {
                while (true)
                {
                    var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false); } catch { }
                        return null;
                    }
                    ms.Write(buf, 0, r.Count);
                    if (ms.Length > MaxIncomingBytes) throw new InvalidDataException(Txt.S("CloudMessageTooBig"));
                    if (r.EndOfMessage) break;
                }
                try { return JObject.Parse(Encoding.UTF8.GetString(ms.ToArray())); }
                catch { return new JObject(); }   // мусор — пропускаем, соединение не рвём
            }
        }

        // ---------------- сообщения ----------------

        private void Handle(JObject msg, CancellationToken ct)
        {
            switch ((string)msg["type"])
            {
                case "welcome":
                    PingInterval = Math.Max(5, (int?)msg["ping_interval"] ?? 25);
                    ApplyStates(msg["bases"] as JArray);
                    if (State != CloudState.Working) Log.Ok(Txt.F("CloudWorkingLog", ActiveCount()));
                    State = CloudState.Working;
                    _welcomed = true;
                    LastError = null;
                    break;
                case "base_state":
                    ApplyStates(new JArray(msg));
                    break;
                case "ping":
                    var _ = Send(new JObject { ["type"] = "pong", ["t"] = msg["t"] }, ct);
                    break;
                case "pong":
                    _lastPong = DateTime.UtcNow;
                    break;
                case "call":
                    var id = (string)msg["id"];
                    if (string.IsNullOrEmpty(id)) return;
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    _running[id] = cts;
                    Task.Run(() => RunCall(msg, cts.Token)).ContinueWith(t => { _running.TryRemove(id, out var c); c?.Dispose(); });
                    break;
                case "cancel":
                    if (_running.TryGetValue((string)msg["id"] ?? "", out var cc)) { try { cc.Cancel(); } catch { } }
                    break;
            }
        }

        private int ActiveCount()
        {
            try { return CloudStore.Load().Active(AgentConfig.Load()).Count(b => StateOf(b.BaseId).State == "active"); } catch { return 0; }
        }

        private (string State, string Reason) StateOf(string baseId)
        {
            lock (_baseStates) return _baseStates.TryGetValue(baseId ?? "", out var s) ? s : ("active", null);
        }

        private void ApplyStates(JArray bases)
        {
            if (bases == null) return;
            var revoked = new List<string>();
            lock (_baseStates)
                foreach (var b in bases.OfType<JObject>())
                {
                    var id = (string)b["base_id"];
                    if (string.IsNullOrEmpty(id)) continue;
                    var st = (string)b["state"] ?? "active";
                    _baseStates[id] = (st, (string)b["reason"]);
                    if (st == "revoked") revoked.Add(id);
                }
            if (revoked.Count == 0) return;
            // Отключена в кабинете — снимаем у себя привязку (протокол, «Состояния базы»)
            CloudStore.Update(s => { foreach (var id in revoked) { var x = s.ByBaseId(id); if (x != null && !x.RevokedInCabinet) { x.RevokedInCabinet = true; Log.Warn(Txt.F("CloudRevokedInCabinetLog", x.Slug)); } } });
        }

        /// <summary>Вызов инструмента из облака — по правилам агента, даже если облако ошиблось (протокол, раздел 4).</summary>
        private async Task RunCall(JObject msg, CancellationToken ct)
        {
            var id = (string)msg["id"];
            var rpc = msg["rpc"] as JObject;
            JObject reply;
            await _calls.WaitAsync(ct).ConfigureAwait(false);
            try { reply = await Execute(msg, rpc, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }   // cancel или разрыв — результат никому не нужен
            finally { _calls.Release(); }
            if (ct.IsCancellationRequested) return;
            var text = reply.ToString(Formatting.None);
            if (Encoding.UTF8.GetByteCount(text) > MaxResultBytes) reply = RpcError(rpc?["id"], -32002, Txt.S("CloudResultTooBig"));
            try { await Send(new JObject { ["type"] = "result", ["id"] = id, ["rpc"] = reply }, ct).ConfigureAwait(false); }
            catch (Exception e) { Log.Warn(e.Message); }
        }

        internal async Task<JObject> Execute(JObject msg, JObject rpc, CancellationToken ct)
        {
            var baseId = (string)msg["base_id"];
            var method = (string)rpc?["method"];
            var cfg = AgentConfig.Load();
            var binding = CloudStore.Load().Active(cfg).FirstOrDefault(b => string.Equals(b.BaseId, baseId, StringComparison.OrdinalIgnoreCase));
            if (binding == null)
            {
                Log.Warn(Txt.F("CloudCallRefusedLog", baseId ?? "—"));
                return RpcError(rpc?["id"], -32001, Txt.S("CloudBaseNotConnected"));
            }
            if (StateOf(baseId).State == "paused") return RpcError(rpc?["id"], -32003, Txt.S("CloudBasePaused"));
            if (method != "tools/list" && method != "tools/call") return RpcError(rpc?["id"], -32601, "Method not found: " + method);
            if (method == "tools/call")
            {
                // Базу ставит агент по base_id; что прислало облако в base — не в счёт
                var p = rpc["params"] as JObject ?? new JObject();
                var args = p["arguments"] as JObject ?? new JObject();
                args["base"] = binding.Slug;
                p["arguments"] = args;
                rpc["params"] = p;
            }
            var caller = new McpCaller
            {
                Client = "bilem-cloud",
                Remote = new Uri(cfg.Cloud.EffectiveUrl).Host,
                CloudBase = binding.Slug,
                CloudChannel = (string)msg["caller"]?["channel"] ?? "cloud",
                CloudUser = (string)msg["caller"]?["user"],
            };
            var deadline = Math.Max(1000, (int?)msg["deadline_ms"] ?? DefaultDeadlineMs);
            var work = Task.Run(() =>
            {
                McpCaller.Current = caller;
                try { return _host.Protocol.Handle(rpc); }
                finally { McpCaller.Current = null; }
            });
            var done = await Task.WhenAny(work, Task.Delay(deadline, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (done != work) return RpcError(rpc["id"], -32004, Txt.S("CloudDeadline"));
            return await work.ConfigureAwait(false) ?? RpcError(rpc["id"], -32603, "no result");
        }

        private static JObject RpcError(JToken id, int code, string message) =>
            new JObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JObject { ["code"] = code, ["message"] = message } };

        // ---------------- состояние для окна и sysinfo ----------------

        /// <summary>Без ключа и данных 1С: состояние, время связи, базы с состояниями и адресами организаций.</summary>
        public JObject Status()
        {
            var cfg = AgentConfig.Load();
            var store = CloudStore.Load();
            var bases = new JArray(store.Bindings.OrderBy(b => b.Slug).Select(b =>
            {
                var st = b.RevokedInCabinet ? ("revoked", "cabinet") : StateOf(b.BaseId);
                return new JObject
                {
                    ["slug"] = b.Slug, ["base_id"] = b.BaseId, ["org"] = b.OrgName, ["org_url"] = b.OrgUrl, ["login"] = b.Login,
                    ["state"] = cfg.Find(b.Slug) == null ? "deleted" : st.Item1, ["reason"] = st.Item2,
                };
            }));
            return new JObject
            {
                ["enabled"] = cfg.Cloud.Enabled,
                ["url"] = cfg.Cloud.EffectiveUrl,
                ["state"] = State.ToString(),
                ["last_contact"] = LastContact,
                ["error"] = LastError,
                ["has_key"] = store.HasKey,
                ["bases"] = bases,
            };
        }
    }
}
