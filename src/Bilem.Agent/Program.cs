using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Bilem.Agent.Core.Service;

namespace Bilem.Agent
{
    /// <summary>
    /// bilem-agent.exe — окно (подсистема Windows, без прав администратора); команды — bilem-agent.com.
    /// Со службой BilemAgent (часть I): без аргументов и с --tray — окно в трее (состояние службы, без UAC);
    /// --settings [вкладка] — окно настроек (UAC); --admin &lt;действие&gt; — одно действие администратора (UAC).
    /// Без службы (сборка разработчика, прежние беты) — как раньше: одно окно с MCP, с правами администратора.
    /// </summary>
    internal static class Program
    {
        /// <summary>Одно окно настроек на машину: повторный запуск показывает уже работающее.</summary>
        private const string MutexName = "Global\\Bilem.Agent.Window";
        internal const string ShowEventName = "Global\\Bilem.Agent.Show";
        /// <summary>Трей — свой у каждого вошедшего пользователя (сеанс RDP): объекты Local\, их может создать и обычный пользователь.</summary>
        internal const string TrayMutexName = "Local\\Bilem.Agent.Tray";
        internal const string TrayShowEventName = "Local\\Bilem.Agent.Tray.Show";

        [STAThread]
        private static int Main(string[] args)
        {
            // Первым делом — .NET 4.8: нет его — своё окно «установить?» вместо системной ошибки (до любого кода, которому нужен 4.8)
            var window = args.Length == 0 || args[0] == "--tray" || args[0] == "--settings";
            if (window && !NetFxCheck.EnsureOrAsk()) return 1;
            return Run(args);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int Run(string[] args)
        {
            var first = args.Length > 0 ? args[0] : "";
            switch (first)
            {
                case "--tray": return RunTray(show: false);
                case "--settings": return RunSettings(args.Skip(1).FirstOrDefault());
                case "--admin": return AdminActions.Run(args.Skip(1).ToArray());
            }
            // С аргументами — команда (обычно через bilem-agent.com); вывод — в консоль, из которой запустили
            if (args.Length > 0) { AttachConsole(-1); return Cli.Run(args); }
            return ServiceInfo.Installed ? RunTray(show: true) : RunLegacy();
        }

        /// <summary>Окно в трее без прав администратора. Запущено с правами (после обновления, «от имени администратора») — перезапуск без них.</summary>
        private static int RunTray(bool show)
        {
            if (IsElevated() && Deelevate(show ? null : "--tray")) return 0;
            using (var mutex = new Mutex(true, TrayMutexName, out var firstInstance))
            {
                if (!firstInstance)
                {
                    try { using (var ev = EventWaitHandle.OpenExisting(TrayShowEventName)) ev.Set(); } catch { }
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp(show));
                return 0;
            }
        }

        /// <summary>Окно настроек: все вкладки, как раньше; MCP — в службе. Нужны права администратора — UAC.</summary>
        private static int RunSettings(string tab)
        {
            if (!IsElevated()) return Elevate("--settings" + (string.IsNullOrEmpty(tab) ? "" : " " + tab)) ? 0 : 1;
            return RunWindow(() => new MainForm(viaService: ServiceInfo.Installed, tab: tab));
        }

        /// <summary>Без службы: окно с MCP внутри, как в прежних бетах (нужны права администратора).</summary>
        private static int RunLegacy()
        {
            if (!IsElevated()) return Elevate("") ? 0 : 1;
            return RunWindow(() => new MainForm());
        }

        /// <summary>Окно создаётся после настройки WinForms (SetCompatibleTextRenderingDefault — до первого окна).</summary>
        private static int RunWindow(Func<Form> create)
        {
            using (var mutex = new Mutex(true, MutexName, out var firstInstance))
            {
                if (!firstInstance)
                {
                    try { using (var ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set(); } catch { }
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // Пароль настроек (если задан) — до окна: без него настройки не открываются
                if (!SettingsGate.Pass(Bilem.Agent.Core.Txt.S("UiLockWhatSettings"))) return 1;
                Application.Run(create());
                return 0;
            }
        }

        internal static bool IsElevated()
        {
            using (var id = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>Запустить себя с правами администратора (UAC). Отказ в окне UAC — false, без ошибки.</summary>
        internal static bool Elevate(string arguments, bool wait = false)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(Application.ExecutablePath, arguments) { UseShellExecute = true, Verb = "runas" });
                if (!wait || p == null) return true;
                p.WaitForExit();
                return p.ExitCode == 0;   // отмена пароля настроек или ошибка — не «готово»
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return false; }   // «Нет» в окне UAC
        }

        /// <summary>
        /// Перезапуск без прав администратора: через оболочку Windows (explorer.exe запускает программу от имени вошедшего пользователя
        /// с обычными правами). Аргументы оболочка не передаёт: без аргументов агент со службой и так открывает трей.
        /// </summary>
        private static bool Deelevate(string arguments)
        {
            try
            {
                Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                    "\"" + Application.ExecutablePath + "\"") { UseShellExecute = false });
                return true;
            }
            catch { return false; }   // не вышло — работаем в трее с правами, это не опасно
        }

        [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
    }
}
