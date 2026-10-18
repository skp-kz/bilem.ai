using System;
using System.IO;
using System.Windows.Forms;
using Bilem.Agent.Core;

namespace Bilem.Agent
{
    /// <summary>Ярлык «Bilem Agent» на рабочем столе и в меню «Пуск» — для всех пользователей (права администратора уже есть).</summary>
    internal static class Shortcuts
    {
        public const string Name = "Bilem Agent";

        public static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), Name + ".lnk");
        public static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Name + ".lnk");

        /// <summary>
        /// Создать недостающие (при первом запуске окна) и пересоздать устаревшие: exe новее ярлыка — значит программу обновили,
        /// и Windows могла закэшировать старый значок (панель задач группирует кнопку по ярлыку из «Пуска»).
        /// </summary>
        public static void EnsureExist()
        {
            var exeTime = File.GetLastWriteTimeUtc(Application.ExecutablePath);
            var changed = false;
            foreach (var l in new[] { DesktopLink, StartMenuLink })
                if (!File.Exists(l) || File.GetLastWriteTimeUtc(l) < exeTime) changed |= TryCreate(l);
            if (changed) RefreshIcons();
        }

        /// <summary>Создать или пересоздать оба ярлыка (команда shortcut).</summary>
        public static bool Recreate()
        {
            var ok = true;
            foreach (var l in new[] { DesktopLink, StartMenuLink }) ok &= TryCreate(l);
            RefreshIcons();
            return ok;
        }

        /// <summary>Сказать оболочке Windows, что значки изменились: ярлыки, exe и кэш значков перечитываются без перезапуска проводника.</summary>
        public static void RefreshIcons()
        {
            try
            {
                foreach (var p in new[] { DesktopLink, StartMenuLink, Application.ExecutablePath })
                {
                    var ptr = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(p);
                    try { SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, ptr, IntPtr.Zero); }
                    finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr); }
                }
                SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        private const int SHCNE_UPDATEITEM = 0x00002000, SHCNE_ASSOCCHANGED = 0x08000000, SHCNF_PATHW = 0x0005;

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

        private static bool TryCreate(string lnk)
        {
            try
            {
                var exe = Application.ExecutablePath;
                var type = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(type);
                dynamic sc = shell.CreateShortcut(lnk);
                sc.TargetPath = exe;
                sc.WorkingDirectory = Path.GetDirectoryName(exe);
                sc.IconLocation = exe + ",0";
                sc.Description = Txt.S("ShortcutDescription");
                sc.Save();
                Log.Ok(Txt.F("ShortcutCreated", lnk));
                return true;
            }
            catch (Exception e)
            {
                Log.Warn(Txt.F("ShortcutFailed", lnk, e.Message));
                return false;
            }
        }
    }
}
