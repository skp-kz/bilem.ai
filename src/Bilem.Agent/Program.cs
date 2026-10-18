using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Bilem.Agent
{
    /// <summary>bilem-agent.exe — окно (подсистема Windows); команды — bilem-agent.com, с аргументами exe тоже выполняет команду.</summary>
    internal static class Program
    {
        /// <summary>Одно окно агента на машину: повторный запуск показывает уже работающее.</summary>
        private const string MutexName = "Global\\Bilem.Agent.Window";
        internal const string ShowEventName = "Global\\Bilem.Agent.Show";

        [STAThread]
        private static int Main(string[] args)
        {
            // С аргументами — команда (обычно через bilem-agent.com); вывод — в консоль, из которой запустили
            if (args.Length > 0) { AttachConsole(-1); return Cli.Run(args); }

            using (var mutex = new Mutex(true, MutexName, out var first))
            {
                if (!first)
                {
                    // Агент уже работает — просим его показать окно и выходим
                    try { using (var ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set(); } catch { }
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                return 0;
            }
        }

        [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
    }
}
