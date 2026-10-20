using System;
using System.Windows.Forms;
using Bilem.Agent.Core;
using Bilem.Agent.Core.Service;

namespace Bilem.Agent
{
    /// <summary>
    /// Разовые действия администратора из окна в трее: «bilem-agent.exe --admin &lt;действие&gt;» запускается с UAC, делает одно дело и выходит.
    /// copy-url — адрес MCP с токеном в буфер обмена (токен читают только администраторы); restart-mcp — перезапуск MCP в службе;
    /// start-service — запустить службу BilemAgent.
    /// </summary>
    internal static class AdminActions
    {
        public static int Run(string[] args)
        {
            var action = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            Application.EnableVisualStyles();
            if (!Program.IsElevated())
            {
                MessageBox.Show(Txt.S("SvcNeedAdmin"), "Bilem Agent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
            try
            {
                switch (action)
                {
                    case "copy-url":
                        // Адрес с токеном — ключ к данным 1С: под паролем настроек, если он задан
                        if (!SettingsGate.Pass(Txt.S("UiLockWhatCopy"))) return 1;
                        var cfg = AgentConfig.Load();
                        Clipboard.SetDataObject(cfg.McpUrl(cfg.GetOrCreateToken()), true);
                        return 0;
                    case "restart-mcp":
                        var r = StatusPipe.Ask("restart-mcp", 5000);
                        if (r == null) throw new AgentException(Txt.S("UiSvcNotRunning"));
                        if (r["error"] != null) throw new AgentException((string)r["error"]);
                        return 0;
                    case "start-service":
                        ServiceInfo.Start();
                        return 0;
                    default:
                        throw new AgentException(Txt.F("CliUnknownCommand", action));
                }
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "Bilem Agent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }
    }
}
