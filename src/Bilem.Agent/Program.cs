using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Bilem.Agent
{
    /// <summary>Один exe: без аргументов — окно, с аргументами — CLI.</summary>
    internal static class Program
    {
        /// <summary>Одно окно агента на машину: повторный запуск показывает уже работающее.</summary>
        private const string MutexName = "Global\\Bilem.Agent.Window";
        internal const string ShowEventName = "Global\\Bilem.Agent.Show";

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0) return Cli.Run(args);

            // Запуск двойным щелчком: консоль принадлежит только нам — прячем её и открываем окно
            HideOwnConsole();
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

        private static void HideOwnConsole()
        {
            var ids = new uint[2];
            if (GetConsoleProcessList(ids, 2) <= 1)
            {
                var h = GetConsoleWindow();
                if (h != IntPtr.Zero) ShowWindow(h, 0);
                FreeConsole();
            }
        }

        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll")] private static extern uint GetConsoleProcessList(uint[] list, uint count);
        [DllImport("kernel32.dll")] private static extern bool FreeConsole();
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    }
}
