using System;
using System.Collections.Generic;
using System.Linq;

namespace Bilem.Agent.Core
{
    /// <summary>Цвет значка в трее: зелёный — работает, жёлтый — подключение, красный — ошибка, серый — офлайн.</summary>
    public enum AgentHealth { Green, Yellow, Red, Grey }

    public sealed class HealthInput
    {
        public bool McpRunning { get; set; }
        /// <summary>MCP не смог запуститься (порт занят и т. п.) — текст причины.</summary>
        public string McpError { get; set; }
        public bool McpStoppedByUser { get; set; }
        public int BaseCount { get; set; }
        /// <summary>Итог короткой проверки по базам: slug → ошибка (null — в порядке).</summary>
        public IDictionary<string, string> BaseErrors { get; set; } = new Dictionary<string, string>();
        /// <summary>Идёт запуск, настройка или проверка — с этого момента.</summary>
        public DateTime? BusySince { get; set; }
        public DateTime Now { get; set; } = DateTime.Now;
    }

    public sealed class HealthState
    {
        public AgentHealth Color { get; set; }
        public string Tooltip { get; set; }
        public override string ToString() => $"{Color}: {Tooltip}";
    }

    /// <summary>Правила цвета трея (правки v0.1, пачка 1) — чистая функция, проверяется тестами.</summary>
    public static class Health
    {
        public static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(60);
        /// <summary>Подсказка значка в трее в Windows — не длиннее 63 символов.</summary>
        public const int TooltipMax = 63;

        public static HealthState Evaluate(HealthInput i)
        {
            if (i.McpStoppedByUser) return S(AgentHealth.Grey, Txt.S("TrayStopped"));
            if (!string.IsNullOrEmpty(i.McpError)) return S(AgentHealth.Red, Txt.F("TrayError", i.McpError));
            if (i.BaseCount == 0) return S(AgentHealth.Grey, Txt.S("TrayNoBases"));
            if (i.BusySince.HasValue)
            {
                if (i.Now - i.BusySince.Value > StuckAfter) return S(AgentHealth.Red, Txt.F("TrayError", Txt.S("TrayStuck")));
                return S(AgentHealth.Yellow, Txt.S("TrayConnecting"));
            }
            var bad = i.BaseErrors.Where(x => !string.IsNullOrEmpty(x.Value)).ToList();
            if (bad.Count > 0) return S(AgentHealth.Red, Txt.F("TrayError", Txt.F("TrayBaseError", bad[0].Key, bad[0].Value)));
            if (!i.McpRunning) return S(AgentHealth.Yellow, Txt.S("TrayConnecting"));
            return S(AgentHealth.Green, Txt.F("TrayOk", i.BaseCount));
        }

        private static HealthState S(AgentHealth c, string tip) =>
            new HealthState { Color = c, Tooltip = tip.Length <= TooltipMax ? tip : tip.Substring(0, TooltipMax - 1) + "…" };

        /// <summary>Короткая проверка базы для трея: пароль bilem расшифровывается и OData отвечает 200. Пусто — в порядке.</summary>
        public static string QuickCheck(BaseConfig b)
        {
            string pwd;
            try { pwd = Secrets.ReadSecretFile(b.SecretFile); }
            catch { return Txt.S("TrayNoSecret"); }
            var r = new ODataClient(b.OData, b.User, pwd).Send("GET", b.OData + "/$metadata", timeoutMs: 15000);
            return r.Code == 200 ? null : (r.Code == 0 ? Txt.S("TrayNoAnswer") : "HTTP " + r.Code);
        }
    }
}
