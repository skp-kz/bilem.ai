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

        /// <summary>regsvr32 той же разрядности, что платформа.</summary>
        public static ProcResult RegisterCom(Platform1C p)
        {
            var dir = p.Bits == 64 ? Paths.NativeSystem32 : Paths.Wow64System32;
            return Proc.Run(Path.Combine(dir, "regsvr32.exe"), "/s " + Proc.Quote(p.ComDll));
        }

        /// <summary>Помощник COM той же разрядности, что платформа: bilem-com32.exe / bilem-com64.exe рядом с агентом.</summary>
        public static string ComHelper(Platform1C p) => Path.Combine(Paths.AppDir, p.Bits == 64 ? "bilem-com64.exe" : "bilem-com32.exe");
    }
}
