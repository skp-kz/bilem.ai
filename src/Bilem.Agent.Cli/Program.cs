using System.Diagnostics;
using System.IO;

namespace Bilem.AgentCli
{
    /// <summary>
    /// bilem-agent.com: команды командной строки (setup, verify, serve, url, …) — с консолью и кодом возврата.
    /// Без аргументов — открыть окно (bilem-agent.exe рядом), как раньше при запуске «bilem-agent».
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length > 0) return Bilem.Agent.Cli.Run(args);
            var exe = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? ".", "bilem-agent.exe");
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return 0;
        }
    }
}
