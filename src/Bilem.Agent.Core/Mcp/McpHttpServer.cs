using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bilem.Agent.Core.Mcp
{
    /// <summary>
    /// HTTP для MCP (Streamable HTTP без сессий): POST /mcp/&lt;токен&gt; с JSON-RPC, ответ — application/json.
    /// Слушаем TcpListener только на 127.0.0.1 — с другого адреса машины сервер недоступен физически
    /// (HttpListener идёт через http.sys, а тот по умолчанию принимает на всех адресах и смотрит только на Host).
    /// Без токена или с неверным — 401; запросы из браузера (есть Origin) — 403.
    /// GET с Accept: text/event-stream — поток уведомлений сервера (SSE): notifications/tools/list_changed.
    /// </summary>
    public sealed class McpHttpServer : IDisposable
    {
        private static readonly Regex PathRx = new Regex(@"^/mcp(?:/([^/?]*))?/?(?:\?.*)?$");
        private const int MaxHeader = 64 * 1024;
        private const int MaxBody = 4 * 1024 * 1024;

        private readonly int _port;
        private readonly Func<string> _token;
        private readonly McpProtocol _protocol;
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private readonly List<Stream> _streams = new List<Stream>();

        /// <summary>Открытых потоков уведомлений (SSE) — для журнала и тестов.</summary>
        public int StreamCount { get { lock (_streams) return _streams.Count; } }

        /// <summary>Уведомление всем открытым потокам (SSE); закрытые — выкидываем.</summary>
        public void Notify(string json)
        {
            var b = Encoding.UTF8.GetBytes("event: message\ndata: " + json + "\n\n");
            lock (_streams)
                foreach (var st in _streams.ToArray())
                {
                    try { st.Write(b, 0, b.Length); st.Flush(); }
                    catch { _streams.Remove(st); }
                }
        }

        public bool Running => _listener != null;
        public int Port => _port;

        public McpHttpServer(int port, Func<string> token, McpProtocol protocol)
        {
            _port = port;
            _token = token;
            _protocol = protocol;
        }

        public void Start()
        {
            if (_listener != null) return;
            _cts = new CancellationTokenSource();
            var l = new TcpListener(IPAddress.Loopback, _port);
            try { l.Start(); }
            catch (SocketException e) { throw new AgentException(Txt.F("McpPortBusy", _port, e.Message), Txt.S("HintMcpPort")) { Short = Txt.F("TrayPortBusy", _port) }; }
            // Сокет не наследуется дочерними процессами: иначе помощник установки обновления (cmd → msiexec) уносил копию
            // и держал порт после выхода агента — новая версия не могла запустить MCP (боевой сервер 19.10, beta.5 → beta.6)
            NoInherit(l.Server.Handle);
            _listener = l;
            Task.Run(() => AcceptLoop(l, _cts.Token));
            Log.Ok(Txt.F("McpStarted", _port));
        }

        public void Stop()
        {
            if (_listener == null) return;
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            _listener = null;
            Log.Info(Txt.S("McpStopped"));
        }

        public void Dispose() => Stop();

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
        internal static void NoInherit(IntPtr handle) => SetHandleInformation(handle, 1 /* HANDLE_FLAG_INHERIT */, 0);

        private async Task AcceptLoop(TcpListener l, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await l.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch { if (ct.IsCancellationRequested) return; continue; }
                NoInherit(c.Client.Handle);
                _ = Task.Run(() => Handle(c));
            }
        }

        private void Handle(TcpClient client)
        {
            using (client)
            {
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;
                var s = client.GetStream();
                try
                {
                    // Дополнительная защита: принимаем только с петлевого адреса
                    if (!IPAddress.IsLoopback(((IPEndPoint)client.Client.RemoteEndPoint).Address)) { Write(s, 403, "{\"error\":\"forbidden\"}"); return; }
                    if (!ReadRequest(s, out var method, out var path, out var headers, out var body)) { Write(s, 400, "{\"error\":\"bad_request\"}"); return; }
                    var m = PathRx.Match(path);
                    if (!m.Success) { Write(s, 404, "{\"error\":\"not_found\"}"); return; }
                    if (!Secrets.ConstantTimeEquals(Uri.UnescapeDataString(m.Groups[1].Value), _token())) { Log.Mcp("-", null, 0, "401"); Write(s, 401, "{\"error\":\"unauthorized\"}"); return; }
                    if (headers.ContainsKey("origin")) { Write(s, 403, "{\"error\":\"origin_not_allowed\"}"); return; }
                    if (method == "GET" && headers.TryGetValue("accept", out var acc) && acc.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Stream(client, s);
                        return;
                    }
                    if (method != "POST") { Write(s, 405, "{\"error\":\"method_not_allowed\"}", "Allow: POST\r\n"); return; }
                    // Кто вызывает: адрес, Tailscale (tailscale serve добавляет заголовки), сессия → программа из initialize
                    headers.TryGetValue("mcp-session-id", out var session);
                    headers.TryGetValue("tailscale-user-login", out var tsLogin);
                    headers.TryGetValue("tailscale-user-name", out var tsName);
                    var caller = new McpCaller
                    {
                        Remote = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString(), Session = session,
                        Client = McpCaller.ClientOf(session), TailscaleLogin = tsLogin, TailscaleName = tsName,
                    };
                    McpCaller.Current = caller;
                    string reply;
                    try { reply = _protocol.HandleRaw(Encoding.UTF8.GetString(body)); }
                    finally { McpCaller.Current = null; }
                    var extra = caller.NewSession == null ? "" : "Mcp-Session-Id: " + caller.NewSession + "\r\n";
                    if (reply == null) Write(s, 202, null, extra);
                    else Write(s, 200, reply, extra);
                }
                catch (IOException) { }
                catch (Exception e)
                {
                    Log.Warn(Txt.F("McpInternalError", e.GetType().Name, e.Message));
                    try { Write(s, 500, "{\"error\":\"internal_error\"}"); } catch { }
                }
            }
        }

        /// <summary>Поток уведомлений: держим соединение, раз в 25 с — комментарий-«пульс», пока клиент не закроет.</summary>
        private void Stream(TcpClient client, Stream s)
        {
            client.SendTimeout = 10000;
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: keep-alive\r\n\r\n");
            s.Write(head, 0, head.Length);
            s.Flush();
            lock (_streams) _streams.Add(s);
            var ping = Encoding.ASCII.GetBytes(": ping\n\n");
            try
            {
                while (_cts != null && !_cts.IsCancellationRequested)
                {
                    if (_cts.Token.WaitHandle.WaitOne(25000)) break;
                    lock (_streams) { s.Write(ping, 0, ping.Length); s.Flush(); }
                }
            }
            catch { }
            finally { lock (_streams) _streams.Remove(s); }
        }

        private static bool ReadRequest(Stream s, out string method, out string path, out Dictionary<string, string> headers, out byte[] body)
        {
            method = path = null; headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); body = new byte[0];
            var buf = new List<byte>(4096);
            var one = new byte[1];
            // Заголовки — до пустой строки
            while (true)
            {
                if (s.Read(one, 0, 1) != 1) return false;
                buf.Add(one[0]);
                var n = buf.Count;
                if (n >= 4 && buf[n - 4] == '\r' && buf[n - 3] == '\n' && buf[n - 2] == '\r' && buf[n - 1] == '\n') break;
                if (n > MaxHeader) return false;
            }
            var lines = Encoding.ASCII.GetString(buf.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var rl = lines[0].Split(' ');
            if (rl.Length < 2) return false;
            method = rl[0].ToUpperInvariant();
            path = rl[1];
            for (int i = 1; i < lines.Length; i++)
            {
                var p = lines[i].IndexOf(':');
                if (p > 0) headers[lines[i].Substring(0, p).Trim()] = lines[i].Substring(p + 1).Trim();
            }
            if (headers.TryGetValue("transfer-encoding", out var te) && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                body = ReadChunked(s);
                return body != null;
            }
            if (headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var len) && len > 0)
            {
                if (len > MaxBody) return false;
                body = new byte[len];
                var got = 0;
                while (got < len) { var r = s.Read(body, got, len - got); if (r <= 0) return false; got += r; }
            }
            return true;
        }

        private static byte[] ReadChunked(Stream s)
        {
            var ms = new MemoryStream();
            while (true)
            {
                var sizeLine = ReadLine(s);
                if (sizeLine == null) return null;
                var semi = sizeLine.IndexOf(';');
                if (semi >= 0) sizeLine = sizeLine.Substring(0, semi);
                if (!int.TryParse(sizeLine.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var size)) return null;
                if (size == 0) { ReadLine(s); return ms.ToArray(); }
                if (ms.Length + size > MaxBody) return null;
                var chunk = new byte[size];
                var got = 0;
                while (got < size) { var r = s.Read(chunk, got, size - got); if (r <= 0) return null; got += r; }
                ms.Write(chunk, 0, size);
                ReadLine(s);
            }
        }

        private static string ReadLine(Stream s)
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            while (true)
            {
                if (s.Read(one, 0, 1) != 1) return null;
                if (one[0] == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append((char)one[0]);
                if (sb.Length > 1024) return null;
            }
        }

        private static void Write(Stream s, int code, string json, string extraHeaders = "")
        {
            var body = json == null ? new byte[0] : new UTF8Encoding(false).GetBytes(json);
            var reason = code == 200 ? "OK" : code == 202 ? "Accepted" : code == 400 ? "Bad Request" : code == 401 ? "Unauthorized"
                : code == 403 ? "Forbidden" : code == 404 ? "Not Found" : code == 405 ? "Method Not Allowed" : "Internal Server Error";
            var head = $"HTTP/1.1 {code} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n{extraHeaders}\r\n";
            var hb = Encoding.ASCII.GetBytes(head);
            s.Write(hb, 0, hb.Length);
            if (body.Length > 0) s.Write(body, 0, body.Length);
            s.Flush();
        }
    }
}
