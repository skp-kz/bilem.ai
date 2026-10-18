using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core
{
    /// <summary>Прописать MCP Bilem в ИИ-клиента: Claude Code, Claude Desktop, Cursor. Перед правкой файла — копия рядом.</summary>
    public static class Connectors
    {
        public const string ServerName = "bilem";
        public const string McpRemote = "mcp-remote@0.14.3";

        public static bool Connect(string client, string url)
        {
            switch ((client ?? "").ToLowerInvariant())
            {
                case "claude-code": return ClaudeCode(url);
                case "claude-desktop": return ClaudeDesktop(url);
                case "cursor": return Cursor(url);
                default: Log.Fail(Txt.F("ConnectUnknown", client)); return false;
            }
        }

        /// <summary>Claude Code: claude mcp add --transport http --scope user bilem &lt;адрес&gt;.</summary>
        public static bool ClaudeCode(string url)
        {
            var claude = FindOnPath("claude.cmd") ?? FindOnPath("claude.exe") ?? FindOnPath("claude");
            if (claude == null)
            {
                Log.Warn(Txt.S("ConnectClaudeNotFound"));
                Log.Info(Txt.S("ConnectClaudeManual"));
                return false;
            }
            Proc.Run(claude, $"mcp remove {ServerName} --scope user", oem: false, timeoutMs: 60000);
            var r = Proc.Run(claude, $"mcp add --transport http --scope user {ServerName} {url}", oem: false, timeoutMs: 60000);
            if (r.ExitCode != 0) { Log.Fail(Txt.F("ConnectClaudeFailed", r.ExitCode, Mask(r.Combined))); return false; }
            Log.Ok(Txt.S("ConnectClaudeOk"));
            return true;
        }

        /// <summary>Claude Desktop: локальный HTTP-сервер — через мост mcp-remote (нужен Node.js).</summary>
        public static bool ClaudeDesktop(string url)
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json");
            var json = Load(file);
            var servers = json["mcpServers"] as JObject ?? new JObject();
            servers[ServerName] = new JObject { ["command"] = "npx", ["args"] = new JArray("-y", McpRemote, url) };
            json["mcpServers"] = servers;
            Save(file, json);
            if (FindOnPath("npx.cmd") == null) Log.Warn(Txt.S("ConnectNoNode"));
            Log.Ok(Txt.F("ConnectDesktopOk", file));
            return true;
        }

        /// <summary>Cursor: ~/.cursor/mcp.json, mcpServers.bilem.url.</summary>
        public static bool Cursor(string url)
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "mcp.json");
            var json = Load(file);
            var servers = json["mcpServers"] as JObject ?? new JObject();
            servers[ServerName] = new JObject { ["url"] = url };
            json["mcpServers"] = servers;
            Save(file, json);
            Log.Ok(Txt.F("ConnectCursorOk", file));
            return true;
        }

        private static JObject Load(string file)
        {
            if (!File.Exists(file)) return new JObject();
            var text = File.ReadAllText(file, Encoding.UTF8);
            return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
        }

        private static void Save(string file, JObject json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            if (File.Exists(file)) File.Copy(file, file + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
            File.WriteAllText(file, json.ToString(Formatting.Indented), new UTF8Encoding(false));
        }

        public static string FindOnPath(string exe)
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    var p = Path.Combine(dir.Trim(), exe);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }

        /// <summary>Токен в адресе — не в журнал.</summary>
        public static string Mask(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", "/mcp/[A-Za-z0-9_\\-]+", "/mcp/<токен>");
    }
}
