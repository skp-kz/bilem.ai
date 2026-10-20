using System;
using System.Collections.Generic;
using System.Threading;

namespace Bilem.Agent.Core.Mcp
{
    /// <summary>
    /// Кто вызвал инструмент: программа из MCP initialize (clientInfo — «claude-code 2.1»), адрес источника,
    /// пользователь сети Tailscale (заголовки Tailscale-User-Login / Tailscale-User-Name от «tailscale serve»).
    /// Сервер без состояния: initialize выдаёт Mcp-Session-Id, клиент присылает его в следующих запросах.
    /// </summary>
    public sealed class McpCaller
    {
        public string Client { get; set; }
        public string Remote { get; set; }
        public string TailscaleLogin { get; set; }
        public string TailscaleName { get; set; }
        public string Session { get; set; }
        /// <summary>Сессия, выданная этим запросом (initialize) — сервер вернёт её заголовком Mcp-Session-Id.</summary>
        public string NewSession { get; set; }
        /// <summary>Вызов из Bilem Cloud (часть L): база этого вызова — инструменты видят только её.</summary>
        public string CloudBase { get; set; }
        /// <summary>Канал облака (whatsapp, telegram, chatgpt, claude…) и кто спросил — для журнала и аудита.</summary>
        public string CloudChannel { get; set; }
        public string CloudUser { get; set; }

        private static readonly AsyncLocal<McpCaller> Cur = new AsyncLocal<McpCaller>();
        private static readonly Dictionary<string, string> Sessions = new Dictionary<string, string>();
        private static readonly Queue<string> Order = new Queue<string>();
        private static string _lastClient;
        private const int MaxSessions = 500;

        /// <summary>Вызов текущего запроса (HTTP-сервер ставит перед разбором JSON-RPC); null — вызов не из MCP.</summary>
        public static McpCaller Current { get => Cur.Value; set => Cur.Value = value; }

        /// <summary>initialize: запомнить программу клиента, выдать сессию.</summary>
        public static void Initialized(string name, string version)
        {
            var client = string.IsNullOrWhiteSpace(name) ? null : (name.Trim() + (string.IsNullOrWhiteSpace(version) ? "" : " " + version.Trim()));
            var id = Guid.NewGuid().ToString("N");
            lock (Sessions)
            {
                Sessions[id] = client;
                Order.Enqueue(id);
                while (Order.Count > MaxSessions) Sessions.Remove(Order.Dequeue());
                _lastClient = client;
            }
            var c = Current;
            if (c != null) { c.NewSession = id; c.Session = id; c.Client = client; }
        }

        /// <summary>Программа по сессии; клиент без сессии — последняя, что делала initialize.</summary>
        public static string ClientOf(string session)
        {
            lock (Sessions)
                return !string.IsNullOrEmpty(session) && Sessions.TryGetValue(session, out var c) ? c : _lastClient;
        }

        /// <summary>«claude-code 2.1 (127.0.0.1, Tailscale: user@example.com)» — для журнала.</summary>
        public override string ToString()
        {
            var who = Client ?? "?";
            var net = Remote ?? "?";
            var ts = TailscaleLogin ?? TailscaleName;
            if (!string.IsNullOrEmpty(ts)) net += ", Tailscale: " + ts;
            if (!string.IsNullOrEmpty(CloudChannel)) net += ", Bilem Cloud: " + CloudChannel + (string.IsNullOrEmpty(CloudUser) ? "" : " " + CloudUser);
            return $"{who} ({net})";
        }

        internal static void ClearForTests() { lock (Sessions) { Sessions.Clear(); Order.Clear(); _lastClient = null; } }
    }
}
