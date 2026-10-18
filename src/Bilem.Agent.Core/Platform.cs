using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Bilem.Agent.Core
{
    public sealed class Platform1C
    {
        public Version Version { get; set; }
        public int Bits { get; set; }
        public string Bin { get; set; }
        public string ComDll => Path.Combine(Bin, "comcntr.dll");
        public string Webinst => Path.Combine(Bin, "webinst.exe");
        public override string ToString() => $"{Version} ({Bits}-бит)";
    }

    /// <summary>Платформа 1С: поиск установленных версий, разрядность, регистрация COM-коннектора.</summary>
    public static class Platform
    {
        /// <summary>
        /// Корни Program Files — по разрядности ОС, а не процесса (known-issues 1): в 32-битном процессе
        /// ProgramFiles указывает на «Program Files (x86)», 64-битная папка — в ProgramW6432.
        /// </summary>
        public static IEnumerable<(string Root, int Bits)> Roots()
        {
            if (Environment.Is64BitOperatingSystem)
            {
                var pf64 = Environment.GetEnvironmentVariable("ProgramW6432");
                if (string.IsNullOrEmpty(pf64)) pf64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                yield return (Path.Combine(pf64, "1cv8"), 64);
                yield return (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "1cv8"), 32);
            }
            else
            {
                yield return (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "1cv8"), 32);
            }
        }

        public static List<Platform1C> Find()
        {
            var list = new List<Platform1C>();
            foreach (var (root, bits) in Roots())
            {
                if (!Directory.Exists(root)) continue;
                foreach (var d in Directory.GetDirectories(root))
                {
                    var name = Path.GetFileName(d);
                    if (!Regex.IsMatch(name, @"^8\.3\.\d+\.\d+$")) continue;
                    var bin = Path.Combine(d, "bin");
                    if (!File.Exists(Path.Combine(bin, "comcntr.dll"))) continue;
                    list.Add(new Platform1C { Version = new Version(name), Bits = bits, Bin = bin });
                }
            }
            return list;
        }

        /// <summary>Самая новая; при равных версиях — 64-битная.</summary>
        public static Platform1C Choose(List<Platform1C> found, string wantVersion = null)
        {
            if (!string.IsNullOrEmpty(wantVersion))
                return found.Where(p => p.Version == new Version(wantVersion)).OrderByDescending(p => p.Bits).FirstOrDefault();
            return found.OrderByDescending(p => p.Version).ThenByDescending(p => p.Bits).FirstOrDefault();
        }

        /// <summary>
        /// Проверка COM-коннектора в ветке реестра нужной разрядности: ProgID, путь к comcntr.dll этой платформы
        /// и библиотека типов (known-issues 2: ProgID без TypeLib — TYPE_E_LIBNOTREGISTERED). Пусто — всё в порядке.
        /// </summary>
        public static string ComProblem(Platform1C p)
        {
            var view = p.Bits == 64 ? RegistryView.Registry64 : RegistryView.Registry32;
            using (var hkcr = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view))
            {
                var clsid = hkcr.OpenSubKey(@"V83.COMConnector\CLSID")?.GetValue("") as string;
                if (string.IsNullOrEmpty(clsid)) return Txt.S("ComNoProgId");
                var dll = hkcr.OpenSubKey($@"CLSID\{clsid}\InprocServer32")?.GetValue("") as string;
                if (!string.Equals(Path.GetFullPath(dll ?? "x:\\"), Path.GetFullPath(p.ComDll), StringComparison.OrdinalIgnoreCase))
                    return Txt.F("ComOtherDll", dll ?? "—");
                var tl = hkcr.OpenSubKey($@"CLSID\{clsid}\TypeLib")?.GetValue("") as string;
                if (string.IsNullOrEmpty(tl) || hkcr.OpenSubKey($@"TypeLib\{tl}") == null) return Txt.S("ComNoTypeLib");
            }
            return "";
        }

        /// <summary>Путь к comcntr.dll, зарегистрированному сейчас как V83.COMConnector в этой разрядности; null — не зарегистрирован.</summary>
        public static string RegisteredComDll(int bits)
        {
            var view = bits == 64 ? RegistryView.Registry64 : RegistryView.Registry32;
            using (var hkcr = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view))
            {
                var clsid = hkcr.OpenSubKey(@"V83.COMConnector\CLSID")?.GetValue("") as string;
                if (string.IsNullOrEmpty(clsid)) return null;
                var dll = hkcr.OpenSubKey($@"CLSID\{clsid}\InprocServer32")?.GetValue("") as string;
                return string.IsNullOrEmpty(dll) ? null : dll;
            }
        }

        /// <summary>
        /// Перерегистрация меняет то, чем уже пользуются: зарегистрирован коннектор другой платформы (файл на месте).
        /// Тогда — только с явного «да» человека. Не зарегистрирован никакой или файл удалён — менять нечего, регистрируем сами.
        /// </summary>
        public static string ComInUse(Platform1C p)
        {
            var dll = RegisteredComDll(p.Bits);
            if (dll == null || !File.Exists(dll)) return null;
            return string.Equals(Path.GetFullPath(dll), Path.GetFullPath(p.ComDll), StringComparison.OrdinalIgnoreCase) ? null : dll;
        }

        /// <summary>regsvr32 той же разрядности, что платформа.</summary>
        public static ProcResult RegisterCom(Platform1C p) => Regsvr(p.ComDll, p.Bits, false);

        private static ProcResult Regsvr(string dll, int bits, bool unregister)
        {
            var dir = bits == 64 ? Paths.NativeSystem32 : Paths.Wow64System32;
            return Proc.Run(Path.Combine(dir, "regsvr32.exe"), (unregister ? "/s /u " : "/s ") + Proc.Quote(dll));
        }

        /// <summary>Перед перерегистрацией: запомнить, что было (только первый раз — иначе «прежней» станет наша же).</summary>
        public static void RememberCom(AgentConfig cfg, Platform1C p)
        {
            var key = p.Bits.ToString();
            if (cfg.ComBefore.ContainsKey(key)) { cfg.ComBefore[key].Ours = p.ComDll; return; }
            cfg.ComBefore[key] = new ComRegistration { Previous = RegisteredComDll(p.Bits) ?? "", Ours = p.ComDll, At = DateTime.Now };
        }

        /// <summary>
        /// Вернуть регистрацию, которая была до агента (при удалении последней базы этой разрядности или агента).
        /// Только если сейчас зарегистрирован наш коннектор: если после нас регистрацию сменил кто-то ещё — не трогаем.
        /// Возвращает строку для журнала или null — нечего возвращать.
        /// </summary>
        public static string RestoreCom(AgentConfig cfg, int bits)
        {
            var key = bits.ToString();
            if (!cfg.ComBefore.TryGetValue(key, out var reg)) return null;
            cfg.ComBefore.Remove(key);
            var now = RegisteredComDll(bits);
            if (now == null || !string.Equals(Path.GetFullPath(now), Path.GetFullPath(reg.Ours ?? "x:\\"), StringComparison.OrdinalIgnoreCase))
                return Txt.F("ComRestoreSkipped", now ?? "—");
            if (string.IsNullOrEmpty(reg.Previous))
            {
                Regsvr(reg.Ours, bits, true);
                return Txt.F("ComRestoredNone", reg.Ours);
            }
            if (!File.Exists(reg.Previous)) return Txt.F("ComRestoreMissing", reg.Previous);
            var r = Regsvr(reg.Previous, bits, false);
            return r.ExitCode == 0 ? Txt.F("ComRestored", reg.Previous) : Txt.F("ComRestoreFailed", reg.Previous, r.ExitCode);
        }

        /// <summary>Помощник COM той же разрядности, что платформа: bilem-com32.exe / bilem-com64.exe рядом с агентом.</summary>
        public static string ComHelper(Platform1C p) => Path.Combine(Paths.AppDir, p.Bits == 64 ? "bilem-com64.exe" : "bilem-com32.exe");
    }
}
