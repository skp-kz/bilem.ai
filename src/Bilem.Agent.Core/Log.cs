using System;
using System.IO;
using System.Text;

namespace Bilem.Agent.Core
{
    public enum LogLevel { Info, Ok, Plan, Warn, Fail }

    public sealed class LogEntry
    {
        public DateTime Time { get; set; }
        public LogLevel Level { get; set; }
        public string Text { get; set; }
        public override string ToString() => $"{Time:HH:mm:ss} [{Level.ToString().ToUpperInvariant()}] {Text}";
    }

    /// <summary>
    /// Журнал агента: файл по дням в C:\ProgramData\Bilem\logs\ + событие для окна и консоли.
    /// Пароли, токены и данные из 1С сюда не пишутся — только шаги, коды и счётчики.
    /// </summary>
    public static class Log
    {
        private static readonly object Sync = new object();
        public static event Action<LogEntry> Written;

        public static string CurrentFile(string prefix = "agent") =>
            Path.Combine(Paths.LogsDir, $"{prefix}-{DateTime.Now:yyyyMMdd}.log");

        public static void Write(LogLevel level, string text)
        {
            var e = new LogEntry { Time = DateTime.Now, Level = level, Text = text };
            Append("agent", e.ToString());
            Written?.Invoke(e);
        }

        public static void Info(string t) => Write(LogLevel.Info, t);
        public static void Ok(string t) => Write(LogLevel.Ok, t);
        public static void Plan(string t) => Write(LogLevel.Plan, t);
        public static void Warn(string t) => Write(LogLevel.Warn, t);
        public static void Fail(string t) => Write(LogLevel.Fail, t);

        /// <summary>Журнал запросов MCP: время, база, инструмент, длительность, итог — без данных ответа.</summary>
        public static event Action<string> McpWritten;

        public static void Mcp(string tool, string baseSlug, long ms, string result)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{baseSlug ?? "-"}\t{tool}\t{ms} ms\t{result}";
            Append("mcp", line);
            McpWritten?.Invoke(line);
        }

        public static void Append(string prefix, string line)
        {
            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(Paths.LogsDir);
                    File.AppendAllText(CurrentFile(prefix), line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch
                {
                    // Журнал не должен ронять агента (например, нет прав в тестах)
                }
            }
        }
    }
}
