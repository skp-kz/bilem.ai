using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bilem.Agent.Core;
using Bilem.Agent.Core.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>
    /// Проверка на стенде (#99): настоящая подключённая база из config.json, вызовы через протокол MCP. Включается только
    /// переменной BILEM_STAND_BASE (имя тестовой базы) и сценарием в BILEM_STAND_SCRIPT (JSON: [{tool, args}]); в CI не идёт.
    /// </summary>
    public class InvoiceStandTests
    {
        private readonly ITestOutputHelper _out;
        public InvoiceStandTests(ITestOutputHelper o) { _out = o; }

        [Fact]
        public void StandScript()
        {
            var slug = Environment.GetEnvironmentVariable("BILEM_STAND_BASE");
            var script = Environment.GetEnvironmentVariable("BILEM_STAND_SCRIPT");
            if (string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(script)) return;
            // Настоящий C:\ProgramData\Bilem: config.json, пароль bilem и журнал аудита стенда
            var home = Environment.GetEnvironmentVariable("BILEM_HOME");
            Environment.SetEnvironmentVariable("BILEM_HOME", Environment.GetEnvironmentVariable("BILEM_STAND_HOME") ?? @"C:\ProgramData\Bilem");
            try { Run(slug, script); }
            finally { Environment.SetEnvironmentVariable("BILEM_HOME", home); }
        }

        private void Run(string slug, string script)
        {
            var cfg = AgentConfig.Load();
            var b = cfg.Find(slug) ?? throw new Exception("нет базы " + slug);
            var rt = new List<BaseRuntime> { BaseRuntime.FromConfig(b) };
            var proto = new McpProtocol(Tools.Create(() => rt), "stand");
            McpCaller.Current = new McpCaller { Client = "stand-test", CloudChannel = "whatsapp", CloudUser = Environment.GetEnvironmentVariable("BILEM_STAND_USER") ?? "u1" };
            var outFile = Environment.GetEnvironmentVariable("BILEM_STAND_OUT");
            var log = new List<string>();
            JObject last = null;
            string draft = null, number = null;
            foreach (JObject step in JArray.Parse(File.ReadAllText(script)))
            {
                var args = (JObject)step["args"] ?? new JObject();
                // "$draft" / "$number" — draft_id и номер счёта из последнего ответа, где они были
                foreach (var p in args.Properties().ToList())
                {
                    if (p.Value.Type == JTokenType.String && (string)p.Value == "$draft") p.Value = draft;
                    if (p.Value.Type == JTokenType.String && (string)p.Value == "$number") p.Value = number;
                }
                if (step["user"] != null) McpCaller.Current.CloudUser = (string)step["user"];
                var req = new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JObject { ["name"] = step["tool"], ["arguments"] = args } };
                var res = JObject.Parse(proto.HandleRaw(req.ToString()));
                var text = (string)res["result"]?["content"]?[0]?["text"];
                // Картинка первой страницы PDF — файлом рядом с журналом сценария
                var png = ((JArray)res["result"]?["content"])?.OfType<JObject>().FirstOrDefault(c => (string)c["type"] == "image");
                if (png != null && outFile != null) File.WriteAllBytes(Path.ChangeExtension(outFile, ".png"), Convert.FromBase64String((string)png["data"]));
                try { last = JObject.Parse(text ?? "{}"); } catch { last = new JObject { ["text"] = text }; }
                if (last["draft_id"] != null) draft = (string)last["draft_id"];
                if (last["number"] != null && (string)step["tool"] != "print_invoice") number = (string)last["number"];
                var line = $"== {step["tool"]} {args.ToString(Newtonsoft.Json.Formatting.None)}\nisError={res["result"]?["isError"]}\n{text}";
                log.Add(line);
                _out.WriteLine(line);
            }
            McpCaller.Current = null;
            if (outFile != null) File.WriteAllText(outFile, string.Join("\n\n", log), new System.Text.UTF8Encoding(false));
        }
    }
}
