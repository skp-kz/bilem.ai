using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Cloud
{
    /// <summary>
    /// Заглушка облака по протоколу канала — для тестов и приёмки на стенде, пока облака нет (часть L).
    /// Только 127.0.0.1. «Кабинет» — страница /connect: поле для кода из агента и «Разрешить» / «Отклонить» за владельца
    /// (решение 0017, вторая редакция); управление для приёмки — POST /stub/… (код, вызов, пауза, отзыв, разрыв). Данных не хранит: всё в памяти процесса.
    /// </summary>
    public sealed class CloudStub : IDisposable
    {
        /// <summary>Заявка агента (решение 0017, вторая редакция): код → «кабинет» → «Разрешить» владельца.</summary>
        private sealed class Req
        {
            public string Id, Code, AgentId, Slug, BaseId;
            public string Status = "pending", Error, Message;
            public DateTime Expires;
            public string IssuedKey;
            public bool KeyDelivered, WithKey;
        }
        private sealed class Bind { public string BaseId, AgentId, Slug; public string State = "active", Reason; }

        private readonly HttpListener _http = new HttpListener();
        private readonly ConcurrentDictionary<string, Req> _reqs = new ConcurrentDictionary<string, Req>();   // request_id → заявка
        private readonly ConcurrentDictionary<string, string> _keys = new ConcurrentDictionary<string, string>();   // agent_id → key
        private readonly ConcurrentDictionary<string, Bind> _binds = new ConcurrentDictionary<string, Bind>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JObject>> _pending = new ConcurrentDictionary<string, TaskCompletionSource<JObject>>();
        private readonly SemaphoreSlim _send = new SemaphoreSlim(1);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private WebSocket _agent;
        private string _agentId;

        public int Port { get; }
        public string Url => $"http://127.0.0.1:{Port}";
        public const string OrgId = "stub-org";
        public const string OrgName = "Заглушка облака";
        /// <summary>Последнее hello / bases от агента — для тестов.</summary>
        public JArray LastBases { get; private set; }
        public int Hellos { get; private set; }
        public bool AgentConnected => _agent?.State == WebSocketState.Open;
        /// <summary>Пинг в welcome (тесты ставят 5 с).</summary>
        public int PingInterval { get; set; } = 25;
        public event Action<string> Logged;

        public CloudStub(int port)
        {
            Port = port;
            _http.Prefixes.Add(Url + "/");
        }

        public void Start()
        {
            _http.Start();
            Task.Run(() => Accept(_cts.Token));
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _agent?.Abort(); } catch { }
            try { _http.Stop(); _http.Close(); } catch { }
        }

        private void Say(string s) => Logged?.Invoke(s);

        // ---------------- управление (тесты и /stub/…) ----------------

        /// <summary>Лимит баз «кабинета» по тарифу (тесты ставят 1, чтобы получить `limit`).</summary>
        public int MaxBases { get; set; } = int.MaxValue;

        /// <summary>Секунд между опросами заявки (`poll_interval`; тесты ставят 1).</summary>
        public int PollInterval { get; set; } = 3;

        private Req ByCode(string code)
        {
            var n = CloudApi.NormalizeCode(code);
            return _reqs.Values.FirstOrDefault(r => CloudApi.NormalizeCode(r.Code) == n) ?? throw new StubError(400, "code_invalid", "кода нет такого");
        }

        private static bool Open(Req r) => (r.Status == "pending" || r.Status == "waiting_owner") && DateTime.UtcNow <= r.Expires;

        /// <summary>
        /// «Кабинет → Подключить базу → код»: превью и запрос владельцу на телефон (waiting_owner, 10 минут на ответ). Лимит тарифа —
        /// здесь: заявка `failed` / `limit`. Код в кабинете — один раз.
        /// </summary>
        public void Claim(string code)
        {
            var r = ByCode(code);
            if (!Open(r) || r.Status != "pending") throw new StubError(409, "code_used", "код уже введён или устарел");
            if (!_binds.ContainsKey(r.BaseId ?? "") && _binds.Count >= MaxBases)
            {
                r.Status = "failed"; r.Error = "limit"; r.Message = "лимит баз по тарифу кабинета";
                return;
            }
            r.Status = "waiting_owner";
            r.Expires = DateTime.UtcNow.AddMinutes(10);
            Say($"код {r.Code}: база {r.Slug} — ждём владельца");
        }

        /// <summary>Владелец нажал «Разрешить»: привязка базы и ключ компьютера (если его не было) — агент заберёт опросом.</summary>
        public void Approve(string code)
        {
            var r = ByCode(code);
            if (r.Status == "pending") Claim(code);
            if (r.Status == "failed") return;   // лимит — облако отказало ещё на превью
            if (r.Status != "waiting_owner" || !Open(r)) throw new StubError(409, "not_waiting", "заявка не ждёт владельца");
            var old = r.BaseId != null && _binds.TryGetValue(r.BaseId, out var o) ? o : null;
            if (old != null && old.AgentId != r.AgentId) { r.Status = "failed"; r.Error = "base_conflict"; r.Message = "база уже подключена с другого компьютера"; return; }
            // Ключ — если у компьютера его не было (заявка без Bearer: потерян или отозван); старый ключ компьютера при этом недействителен
            if (!_keys.ContainsKey(r.AgentId) || !r.WithKey) _keys[r.AgentId] = r.IssuedKey = Secrets.RandomToken();
            if (string.IsNullOrEmpty(r.BaseId)) r.BaseId = Guid.NewGuid().ToString();
            _binds[r.BaseId] = new Bind { BaseId = r.BaseId, AgentId = r.AgentId, Slug = r.Slug };
            r.Status = "approved";
            Say($"база {r.Slug} подключена (код {r.Code})");
        }

        public void Decline(string code) { var r = ByCode(code); if (Open(r)) r.Status = "declined"; }

        /// <summary>Срок вышел (код не ввели или владелец не ответил).</summary>
        public void Expire(string code) => ByCode(code).Expires = DateTime.UtcNow.AddSeconds(-1);

        private static string Show(string code) => code.Substring(0, 4) + "-" + code.Substring(4);

        public async Task<JObject> Call(string baseId, JObject rpc, int timeoutMs = 30000, string channel = "whatsapp", string user = "+70000000000")
        {
            var id = Guid.NewGuid().ToString();
            var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            await SendAgent(new JObject
            {
                ["type"] = "call", ["id"] = id, ["base_id"] = baseId, ["deadline_ms"] = Math.Min(timeoutMs, 60000),
                ["caller"] = new JObject { ["channel"] = channel, ["user"] = user }, ["rpc"] = rpc,
            }).ConfigureAwait(false);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            _pending.TryRemove(id, out _);
            if (done != tcs.Task) throw new TimeoutException("stub: нет ответа агента");
            return tcs.Task.Result;
        }

        public Task SetState(string baseId, string state, string reason)
        {
            if (_binds.TryGetValue(baseId, out var b)) { b.State = state; b.Reason = reason; }
            if (state == "revoked") _binds.TryRemove(baseId, out _);
            return SendAgent(new JObject { ["type"] = "base_state", ["base_id"] = baseId, ["state"] = state, ["reason"] = reason });
        }

        /// <summary>Отзыв ключа компьютера: закрытие 4401.</summary>
        public async Task RevokeKey()
        {
            if (_agentId != null) _keys.TryRemove(_agentId, out _);
            foreach (var b in _binds.Values.Where(x => x.AgentId == _agentId).ToList()) _binds.TryRemove(b.BaseId, out _);
            var ws = _agent;
            if (ws?.State == WebSocketState.Open) await ws.CloseOutputAsync((WebSocketCloseStatus)4401, "key_revoked", CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>Обрыв связи (как сбой сети): соединение рвётся без закрытия.</summary>
        public void Drop() { try { _agent?.Abort(); } catch { } }

        public string BaseIdOf(string slug) => _binds.Values.FirstOrDefault(b => string.Equals(b.Slug, slug, StringComparison.OrdinalIgnoreCase))?.BaseId;

        private async Task SendAgent(JObject msg)
        {
            var ws = _agent ?? throw new InvalidOperationException("stub: агент не подключён");
            var bytes = Encoding.UTF8.GetBytes(msg.ToString(Formatting.None));
            await _send.WaitAsync().ConfigureAwait(false);
            try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false); }
            finally { _send.Release(); }
        }

        // ---------------- HTTP ----------------

        private async Task Accept(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext c;
                try { c = await _http.GetContextAsync().ConfigureAwait(false); }
                catch { return; }
                var _ = Task.Run(() => Serve(c));
            }
        }

        private async Task Serve(HttpListenerContext c)
        {
            var path = c.Request.Url.AbsolutePath.TrimEnd('/');
            try
            {
                if (path == "/agent/v1/channel" && c.Request.IsWebSocketRequest) { await Channel(c).ConfigureAwait(false); return; }
                var body = ReadBody(c.Request);
                JObject res;
                var m = c.Request.HttpMethod;
                const string reqPath = "/agent/v1/connect-requests";
                if (m == "POST" && path == reqPath) { Json(c, 201, CreateRequest(body, Bearer(c))); return; }
                else if (m == "GET" && path.StartsWith(reqPath + "/")) res = PollRequest(path.Substring(reqPath.Length + 1), Bearer(c));
                else if (m == "DELETE" && path.StartsWith("/agent/v1/bases/")) res = Disconnect(path.Substring("/agent/v1/bases/".Length), Bearer(c));
                else if (m == "GET" && path == "/connect") { Html(c, CabinetPage(c.Request.QueryString["code"], c.Request.QueryString["action"])); return; }
                else if (m == "POST" && path == "/stub/claim") { Claim((string)body["code"]); res = new JObject { ["ok"] = true }; }
                else if (m == "POST" && path == "/stub/approve") { Approve((string)body["code"]); res = new JObject { ["ok"] = true }; }
                else if (m == "POST" && path == "/stub/decline") { Decline((string)body["code"]); res = new JObject { ["ok"] = true }; }
                else if (m == "POST" && path == "/stub/call") res = await Call((string)body["base_id"] ?? BaseIdOf((string)body["slug"]), (JObject)body["rpc"]).ConfigureAwait(false);
                else if (m == "POST" && path == "/stub/state") { await SetState((string)body["base_id"] ?? BaseIdOf((string)body["slug"]), (string)body["state"], (string)body["reason"]).ConfigureAwait(false); res = new JObject { ["ok"] = true }; }
                else if (m == "POST" && path == "/stub/revoke") { await RevokeKey().ConfigureAwait(false); res = new JObject { ["ok"] = true }; }
                else if (m == "POST" && path == "/stub/drop") { Drop(); res = new JObject { ["ok"] = true }; }
                else if (m == "GET" && path == "/stub/status") res = new JObject { ["agent_connected"] = AgentConnected, ["bases"] = LastBases, ["binds"] = new JArray(_binds.Values.Select(b => new JObject { ["base_id"] = b.BaseId, ["slug"] = b.Slug, ["state"] = b.State })) };
                else throw new StubError(404, "not_found", "нет такого адреса");
                Json(c, 200, res);
            }
            catch (StubError e) { Json(c, e.Http, new JObject { ["error"] = e.Code, ["message"] = e.Message }); }
            catch (Exception e) { try { Json(c, 500, new JObject { ["error"] = "stub_error", ["message"] = e.Message }); } catch { } }
        }

        private sealed class StubError : Exception
        {
            public int Http; public string Code;
            public StubError(int http, string code, string msg) : base(msg) { Http = http; Code = code; }
        }

        private static string Bearer(HttpListenerContext c)
        {
            var h = c.Request.Headers["Authorization"] ?? "";
            return h.StartsWith("Bearer ", StringComparison.Ordinal) ? h.Substring(7) : null;
        }

        private string AgentOfKey(string key) => key == null ? null : _keys.FirstOrDefault(k => k.Value == key).Key;

        /// <summary>
        /// POST /agent/v1/connect-requests (формат v1.1 — stand #77): сведения о базе → `request_id` + код подключения на 10 минут.
        /// Порядок проверок как у облака: ключ → поля → чужой компьютер.
        /// </summary>
        private JObject CreateRequest(JObject body, string key)
        {
            if (key != null && AgentOfKey(key) == null) throw new StubError(401, "key_revoked", "ключ отозван");
            var b = body["base"] as JObject ?? new JObject();
            var agentId = (string)body["agent_id"];
            var slug = (string)b["slug"];
            if (string.IsNullOrEmpty(agentId) || string.IsNullOrEmpty(slug)) throw new StubError(400, "bad_request", "нет agent_id или base.slug");
            if (key != null && AgentOfKey(key) != agentId) throw new StubError(401, "key_revoked", "ключ другого компьютера");
            var baseId = (string)b["base_id"];
            if (!string.IsNullOrEmpty(baseId) && _binds.TryGetValue(baseId, out var old) && old.AgentId != agentId)
                throw new StubError(409, "base_conflict", "база уже подключена с другого компьютера");
            var code = NewCode();
            var r = new Req
            {
                Id = Secrets.RandomToken().Substring(0, 22), Code = Show(code),
                AgentId = agentId, Slug = slug, BaseId = string.IsNullOrEmpty(baseId) ? null : baseId, Expires = DateTime.UtcNow.AddMinutes(10), WithKey = key != null,
            };
            _reqs[r.Id] = r;
            Say($"заявка: база {slug}, код {r.Code}");
            return new JObject
            {
                ["request_id"] = r.Id, ["code"] = r.Code, ["expires_at"] = r.Expires, ["poll_interval"] = PollInterval,
                ["connect_url"] = $"{Url}/connect?code={r.Code}",
            };
        }

        /// <summary>GET /agent/v1/connect-requests/{id}: всегда 200 + status; ключ — только в первом approved.</summary>
        private JObject PollRequest(string id, string key)
        {
            if (key != null && AgentOfKey(key) == null) throw new StubError(401, "key_revoked", "ключ отозван");
            if (!_reqs.TryGetValue(id, out var r)) throw new StubError(404, "request_not_found", "заявка неизвестна");
            if ((r.Status == "pending" || r.Status == "waiting_owner") && DateTime.UtcNow > r.Expires) { r.Status = "expired"; r.Message = "код устарел или владелец не ответил"; }
            var o = new JObject { ["status"] = r.Status };
            var org = new JObject { ["id"] = OrgId, ["slug"] = OrgId, ["name"] = OrgName };
            switch (r.Status)
            {
                case "pending": o["expires_at"] = r.Expires; break;
                case "waiting_owner": o["org"] = org; o["expires_at"] = r.Expires; break;
                case "approved":
                    org["mcp_url"] = $"{Url}/{OrgId}";
                    o["base_id"] = r.BaseId; o["org"] = org; o["login"] = "stub@bilem.ai";
                    if (r.IssuedKey != null && !r.KeyDelivered) { o["agent_key"] = r.IssuedKey; r.KeyDelivered = true; }
                    else o["key_delivered"] = true;
                    break;
                case "declined": o["message"] = "владелец отклонил подключение"; break;
                case "expired": o["message"] = r.Message; break;
                case "failed": o["error"] = r.Error; o["message"] = r.Message; break;
            }
            return o;
        }

        private JObject Disconnect(string baseId, string key)
        {
            var agent = AgentOfKey(key) ?? throw new StubError(401, "key_revoked", "ключ отозван");
            if (_binds.TryGetValue(baseId, out var b) && b.AgentId == agent) _binds.TryRemove(baseId, out _);
            return new JObject { ["ok"] = true };
        }

        /// <summary>«Кабинет» для приёмки: поле кода и «Разрешить» / «Отклонить» за владельца (в облаке это WhatsApp владельца).</summary>
        private string CabinetPage(string code, string action)
        {
            string note = "";
            if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrEmpty(action))
            {
                try
                {
                    if (action == "approve") Approve(code); else Decline(code);
                    note = action == "approve" ? "Разрешено — агент подключит базу сам." : "Отклонено.";
                }
                catch (StubError e) { note = e.Message; }
            }
            return "<!doctype html><meta charset=utf-8><title>Заглушка Bilem Cloud</title><body style='font-family:sans-serif;margin:40px'>" +
                "<p>Заглушка облака (тест). Код подключения из Bilem Agent (MCP → база → «Подключить…»):</p>" +
                "<form method=get action=/connect><input name=code style='font-size:24px;width:200px' value='" + WebUtility.HtmlEncode(code ?? "") + "'> " +
                "<button name=action value=approve>Разрешить</button> <button name=action value=decline>Отклонить</button></form>" +
                "<p>" + WebUtility.HtmlEncode(note) + "</p></body>";
        }

        private static string NewCode()
        {
            const string alphabet = "23456789ABCDEFGHJKMNPQRSTVWXYZ";   // Crockford без похожих (0/O, 1/I/L, U)
            var bytes = new byte[8];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
        }

        // ---------------- канал ----------------

        private async Task Channel(HttpListenerContext c)
        {
            var key = Bearer(c);
            var agentId = c.Request.Headers["X-Bilem-Agent"];
            var ctx = await c.AcceptWebSocketAsync(null).ConfigureAwait(false);
            var ws = ctx.WebSocket;
            if (key == null || AgentOfKey(key) != agentId)
            {
                await ws.CloseOutputAsync((WebSocketCloseStatus)4401, "key_revoked", CancellationToken.None).ConfigureAwait(false);
                return;
            }
            try { _agent?.Abort(); } catch { }
            _agent = ws;
            _agentId = agentId;
            Say("агент подключился");
            var buf = new byte[1024 * 1024];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    using (var ms = new MemoryStream())
                    {
                        WebSocketReceiveResult r;
                        do
                        {
                            r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token).ConfigureAwait(false);
                            if (r.MessageType == WebSocketMessageType.Close) return;
                            ms.Write(buf, 0, r.Count);
                        } while (!r.EndOfMessage);
                        var msg = JObject.Parse(Encoding.UTF8.GetString(ms.ToArray()));
                        await OnAgent(msg).ConfigureAwait(false);
                    }
                }
            }
            catch { }
            finally { if (_agent == ws) _agent = null; Say("агент отключился"); }
        }

        private async Task OnAgent(JObject msg)
        {
            switch ((string)msg["type"])
            {
                case "hello":
                    Hellos++;
                    LastBases = msg["bases"] as JArray;
                    var states = new JArray(_binds.Values.Where(b => b.AgentId == _agentId).Select(b => new JObject
                    {
                        ["base_id"] = b.BaseId, ["state"] = b.State, ["reason"] = b.Reason,
                        ["org"] = new JObject { ["id"] = OrgId, ["name"] = OrgName },
                    }));
                    await SendAgent(new JObject { ["type"] = "welcome", ["ping_interval"] = PingInterval, ["bases"] = states }).ConfigureAwait(false);
                    break;
                case "bases":
                    LastBases = msg["bases"] as JArray;
                    break;
                case "ping":
                    await SendAgent(new JObject { ["type"] = "pong", ["t"] = msg["t"] }).ConfigureAwait(false);
                    break;
                case "result":
                    if (_pending.TryGetValue((string)msg["id"] ?? "", out var tcs)) tcs.TrySetResult(msg["rpc"] as JObject);
                    break;
            }
        }

        private static JObject ReadBody(HttpListenerRequest r)
        {
            if (!r.HasEntityBody) return new JObject();
            using (var sr = new StreamReader(r.InputStream, Encoding.UTF8))
            {
                var t = sr.ReadToEnd();
                return string.IsNullOrWhiteSpace(t) ? new JObject() : JObject.Parse(t);
            }
        }

        private static void Json(HttpListenerContext c, int status, JObject o)
        {
            var b = new UTF8Encoding(false).GetBytes(o.ToString(Formatting.None));
            c.Response.StatusCode = status;
            c.Response.ContentType = "application/json; charset=utf-8";
            c.Response.ContentLength64 = b.Length;
            c.Response.OutputStream.Write(b, 0, b.Length);
            c.Response.Close();
        }

        private static void Html(HttpListenerContext c, string html)
        {
            var b = new UTF8Encoding(false).GetBytes(html);
            c.Response.ContentType = "text/html; charset=utf-8";
            c.Response.ContentLength64 = b.Length;
            c.Response.OutputStream.Write(b, 0, b.Length);
            c.Response.Close();
        }
    }
}
