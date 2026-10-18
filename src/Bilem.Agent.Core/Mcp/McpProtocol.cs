using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Mcp
{
    public sealed class McpTool
    {
        public string Name { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public JObject InputSchema { get; set; }
        /// <summary>Возвращает объект ответа; исключение AgentException — ответ isError с текстом для модели.</summary>
        public Func<JObject, JObject> Handler { get; set; }
    }

    /// <summary>
    /// Минимальная реализация MCP по спецификации (JSON-RPC 2.0): initialize, notifications/*, ping, tools/list, tools/call.
    /// Официальный C# SDK рассчитан на .NET 8+ и ASP.NET Core — на net48 не встаёт (журнал задачи).
    /// </summary>
    public sealed class McpProtocol
    {
        public static readonly string[] SupportedVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };
        private readonly List<McpTool> _tools;
        private readonly string _version;

        public McpProtocol(IEnumerable<McpTool> tools, string version)
        {
            _tools = tools.ToList();
            _version = version;
        }

        public IReadOnlyList<McpTool> Tools => _tools;

        /// <summary>Какие инструменты сейчас в списке (например, почта — только при включённом SMTP-клиенте).</summary>
        public Func<McpTool, bool> Visible { get; set; } = t => true;

        /// <summary>Отпечаток набора инструментов — сменился, значит клиентам уходит notifications/tools/list_changed.</summary>
        public string ToolsSignature() => string.Join(",", _tools.Where(Visible).Select(t => t.Name));

        public static readonly string ListChangedNotification =
            new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/tools/list_changed" }.ToString(Formatting.None);

        /// <summary>Тело запроса → тело ответа; null — ответа нет (только уведомления), HTTP 202.</summary>
        public string HandleRaw(string body)
        {
            JToken req;
            try { req = JToken.Parse(body); }
            catch { return Error(null, -32700, "Parse error").ToString(Formatting.None); }
            if (req is JArray batch)
            {
                var replies = new JArray(batch.Select(x => Handle(x as JObject)).Where(x => x != null));
                return replies.Count == 0 ? null : replies.ToString(Formatting.None);
            }
            return Handle(req as JObject)?.ToString(Formatting.None);
        }

        public JObject Handle(JObject req)
        {
            if (req == null) return Error(null, -32600, "Invalid Request");
            var id = req["id"];
            var method = (string)req["method"];
            var isNotification = id == null;
            if (method == null) return Error(id, -32600, "Invalid Request");
            if (method.StartsWith("notifications/", StringComparison.Ordinal)) return null;
            try
            {
                switch (method)
                {
                    case "initialize": return Result(id, Initialize(req["params"] as JObject));
                    case "ping": return Result(id, new JObject());
                    case "tools/list": return Result(id, new JObject { ["tools"] = new JArray(_tools.Where(Visible).Select(Describe)) });
                    case "tools/call": return Result(id, Call(req["params"] as JObject));
                    default: return isNotification ? null : Error(id, -32601, "Method not found: " + method);
                }
            }
            catch (Exception e)
            {
                return Error(id, -32603, e.Message);
            }
        }

        private JObject Initialize(JObject p)
        {
            var asked = (string)p?["protocolVersion"];
            var ver = SupportedVersions.Contains(asked) ? asked : SupportedVersions[0];
            // Кто подключился (claude-code, Claude Desktop…) — для журнала и аудита
            McpCaller.Initialized((string)p?["clientInfo"]?["name"], (string)p?["clientInfo"]?["version"]);
            return new JObject
            {
                ["protocolVersion"] = ver,
                // Набор инструментов меняется (почта вкл/выкл, «Доступ», обновление) — сервер шлёт notifications/tools/list_changed
                ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = true } },
                ["serverInfo"] = new JObject { ["name"] = "bilem-agent", ["title"] = "Bilem Agent", ["version"] = _version },
                // Правило для модели: называть дату данных 1С (as_of) — учёт отстаёт от жизни
                ["instructions"] = Txt.S("McpInstructions"),
            };
        }

        private static JObject Describe(McpTool t) => new JObject
        {
            ["name"] = t.Name,
            ["title"] = t.Title,
            ["description"] = t.Description,
            ["inputSchema"] = t.InputSchema,
        };

        private JObject Call(JObject p)
        {
            var name = (string)p?["name"];
            var tool = _tools.FirstOrDefault(t => t.Name == name);
            if (tool == null) throw new InvalidOperationException("Unknown tool: " + name);
            var args = p["arguments"] as JObject ?? new JObject();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var outObj = tool.Handler(args);
                // Дополнительное содержимое (файл ресурсом, картинка) — отдельными элементами content, не в JSON-тексте
                var extra = outObj[ExtraContent] as JArray;
                outObj.Remove(ExtraContent);
                outObj["ms"] = sw.ElapsedMilliseconds;
                Log.Mcp(name, (string)args["base"], sw.ElapsedMilliseconds, "ok");
                var res = Text(outObj.ToString(Formatting.None), false);
                if (extra != null) foreach (var c in extra) ((JArray)res["content"]).Add(c);
                return res;
            }
            catch (AgentException e)
            {
                Log.Mcp(name, (string)args["base"], sw.ElapsedMilliseconds, "error");
                return Text(e.Message, true);
            }
            catch (Exception e)
            {
                Log.Mcp(name, (string)args["base"], sw.ElapsedMilliseconds, "error " + e.GetType().Name);
                return Text(Txt.F("ToolFailed", e.Message), true);
            }
        }

        /// <summary>Ключ ответа инструмента: массив элементов content (resource, image), который идёт после текста.</summary>
        public const string ExtraContent = "@content";

        private static JObject Text(string text, bool isError)
        {
            var o = new JObject { ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }) };
            if (isError) o["isError"] = true;
            return o;
        }

        private static JObject Result(JToken id, JObject result) => new JObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };

        private static JObject Error(JToken id, int code, string message) =>
            new JObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JObject { ["code"] = code, ["message"] = message } };
    }
}
