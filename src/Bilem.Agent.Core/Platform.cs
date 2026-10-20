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
        /// <summary>Установлена «для текущего пользователя» (в профиле): веб-серверу нужно выдать чтение папки платформы.</summary>
        public bool PerUser { get; set; }
        public override string ToString() => $"{Version} ({Bits}-бит)";
    }

    /// <summary>Найденная установка 1С — любая, с причиной, почему не годится (часть K). Годная — полная 8.3 с COM-коннектором.</summary>
    public sealed class PlatformInstall
    {
        public Version Version { get; set; }
        public int Bits { get; set; }
        public string Bin { get; set; }
        public bool Training { get; set; }
        public bool PerUser { get; set; }
        public bool Com { get; set; }
        public bool WebIsapi { get; set; }
        public bool WebApache { get; set; }

        public bool Is83 => Version.Major == 8 && Version.Minor == 3;
        public bool Usable => !Training && Is83 && Com;
        /// <summary>Путь для журнала и sysinfo: профиль пользователя — как %LOCALAPPDATA%, без имени.</summary>
        public string ShownPath => Platform.MaskProfile(Path.GetDirectoryName(Bin.TrimEnd('\\')));
        public Platform1C ToPlatform() => new Platform1C { Version = Version, Bits = Bits, Bin = Bin, PerUser = PerUser };

        public string Line() => Txt.F("PlatformSurveyLine", Version, Bits,
            Training ? Txt.S("PlatformKindTraining") : Txt.S("PlatformKindFull"),
            Com ? Txt.S("Yes") : Txt.S("No"),
            WebIsapi || WebApache ? Txt.S("Yes") : Txt.S("No"),
            ShownPath);
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

        /// <summary>Годные платформы: полная (не учебная) 8.3 с COM-коннектором — в Program Files или в профиле пользователя.</summary>
        public static List<Platform1C> Find() => Survey().Where(i => i.Usable).Select(i => i.ToPlatform()).ToList();

        /// <summary>Где ищем (часть K): корень с папками версий, разрядность по умолчанию, «для пользователя», «учебная».</summary>
        public sealed class SearchRoot
        {
            public string Path; public int Bits; public bool PerUser; public bool Training;
        }

        public static List<SearchRoot> SearchRoots()
        {
            var list = new List<SearchRoot>();
            foreach (var (root, bits) in Roots())
            {
                list.Add(new SearchRoot { Path = root, Bits = bits });
                list.Add(new SearchRoot { Path = root + "t", Bits = bits, Training = true });   // 1cv8t — учебная версия
            }
            foreach (var la in LocalAppDataDirs())
            {
                list.Add(new SearchRoot { Path = System.IO.Path.Combine(la, "Programs", "1cv8"), PerUser = true });
                list.Add(new SearchRoot { Path = System.IO.Path.Combine(la, "Programs", "1cv8t"), PerUser = true, Training = true });
            }
            return list;
        }

        /// <summary>
        /// Все найденные установки 1С — годные и нет: папки Program Files (1cv8, 1cv8t), профили пользователей (Programs\1cv8, 1cv8t)
        /// и, для нестандартных папок, список установленных программ Windows.
        /// </summary>
        public static List<PlatformInstall> Survey(IEnumerable<SearchRoot> roots = null, bool registry = true)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<PlatformInstall>();
            void AddVersion(string dir, int bits, bool perUser, bool training)
            {
                var name = Path.GetFileName(dir.TrimEnd('\\'));
                if (!Regex.IsMatch(name, @"^8\.\d+\.\d+\.\d+$")) return;
                var bin = Path.Combine(dir, "bin");
                if (!Directory.Exists(bin) || !seen.Add(Path.GetFullPath(bin))) return;
                var main = Path.Combine(bin, "1cv8.exe");
                var tr = Path.Combine(bin, "1cv8t.exe");
                training = training || (File.Exists(tr) && !File.Exists(main));
                var exe = new[] { main, tr, Path.Combine(bin, "1cv8c.exe"), Path.Combine(bin, "1cv8ct.exe") }.FirstOrDefault(File.Exists);
                var pe = exe == null ? 0 : PeBits(exe);
                list.Add(new PlatformInstall
                {
                    Version = new Version(name),
                    Bits = pe != 0 ? pe : bits != 0 ? bits : 32,
                    Bin = bin,
                    Training = training,
                    PerUser = perUser,
                    Com = File.Exists(Path.Combine(bin, "comcntr.dll")),
                    WebIsapi = File.Exists(Path.Combine(bin, "wsisapi.dll")),
                    WebApache = File.Exists(Path.Combine(bin, "wsap24.dll")),
                });
            }
            void Root(string root, int bits, bool perUser, bool training)
            {
                try { if (Directory.Exists(root)) foreach (var d in Directory.GetDirectories(root)) AddVersion(d, bits, perUser, training); }
                catch (Exception e) { Log.Warn(Txt.F("PlatformSearchError", MaskProfile(root), e.Message)); }
            }
            foreach (var r in roots ?? SearchRoots()) Root(r.Path, r.Bits, r.PerUser, r.Training);
            if (registry)
                foreach (var (loc, training) in UninstallEntries())
                {
                    // InstallLocation — папка версии (…\1cv8\8.3.25.1374) или корень с папками версий
                    var perUser = loc.IndexOf(@"\AppData\", StringComparison.OrdinalIgnoreCase) >= 0;
                    var t = training || loc.TrimEnd('\\').Split('\\').Any(s => s.Equals("1cv8t", StringComparison.OrdinalIgnoreCase));
                    try { if (Directory.Exists(Path.Combine(loc, "bin"))) AddVersion(loc, 0, perUser, t); else Root(loc, 0, perUser, t); } catch { }
                }
            return list;
        }

        /// <summary>Ничего годного: самая близкая находка (полная 8.3 без COM, затем другая версия, затем учебная) и сообщение по ней.</summary>
        public static string Explain(List<PlatformInstall> survey)
        {
            var best = survey.Where(i => !i.Training && i.Is83).OrderByDescending(i => i.Version).FirstOrDefault();
            if (best != null) return Txt.F("PlatformNoCom", best.Version, best.ShownPath);
            best = survey.Where(i => !i.Training).OrderByDescending(i => i.Version).FirstOrDefault();
            if (best != null) return Txt.F("PlatformNot83", best.Version, best.ShownPath);
            best = survey.OrderByDescending(i => i.Version).FirstOrDefault();
            if (best != null) return Txt.F("PlatformTraining", best.Version, best.ShownPath);
            return Txt.S("NoPlatform") + " " + Txt.F("PlatformSearched", string.Join("; ", SearchRoots().Select(r => MaskProfile(r.Path)).Distinct()));
        }

        /// <summary>Подсказка к Explain.</summary>
        public static string ExplainHint(List<PlatformInstall> survey) =>
            survey.Any(i => !i.Training && i.Is83) ? Txt.S("HintPlatformAddCom")
            : survey.Count > 0 ? Txt.S("HintPlatformFull83")
            : Txt.S("HintInstallPlatform");

        /// <summary>%LOCALAPPDATA% всех профилей (список профилей Windows) и текущего пользователя.</summary>
        private static IEnumerable<string> LocalAppDataDirs()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mine = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(mine)) set.Add(mine);
            try
            {
                using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"))
                    if (k != null)
                        foreach (var sid in k.GetSubKeyNames())
                        {
                            if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal)) continue;   // только обычные учётки, не службы
                            var p = k.OpenSubKey(sid)?.GetValue("ProfileImagePath") as string;
                            if (!string.IsNullOrEmpty(p)) set.Add(Path.Combine(Environment.ExpandEnvironmentVariables(p), "AppData", "Local"));
                        }
            }
            catch { }
            return set;
        }

        /// <summary>Установленные программы «1С:Предприятие» / «1C:Enterprise» (64 и 32): папка установки и признак учебной.</summary>
        private static IEnumerable<(string Location, bool Training)> UninstallEntries()
        {
            var res = new List<(string, bool)>();
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                    try
                    {
                        using (var k = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                            if (k != null)
                                foreach (var n in k.GetSubKeyNames())
                                    using (var s = k.OpenSubKey(n))
                                    {
                                        var name = s?.GetValue("DisplayName") as string ?? "";
                                        var loc = s?.GetValue("InstallLocation") as string;
                                        if (string.IsNullOrEmpty(loc) || !IsOneCName(name)) continue;
                                        res.Add((loc, name.IndexOf("учебн", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Training", StringComparison.OrdinalIgnoreCase) >= 0));
                                    }
                    }
                    catch { }
            return res;
        }

        internal static bool IsOneCName(string name) =>
            name.IndexOf("1С:Предприятие", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("1C:Предприятие", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("1C:Enterprise", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Разрядность exe по PE-заголовку: 64, 32 или 0 — не разобрать.</summary>
        internal static int PeBits(string exe)
        {
            try
            {
                using (var f = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var r = new BinaryReader(f))
                {
                    if (f.Length < 0x40 || r.ReadUInt16() != 0x5A4D) return 0;
                    f.Position = 0x3C;
                    var pe = r.ReadInt32();
                    if (pe <= 0 || pe + 6 > f.Length) return 0;
                    f.Position = pe;
                    if (r.ReadUInt32() != 0x00004550) return 0;
                    var machine = r.ReadUInt16();
                    return machine == 0x8664 || machine == 0xAA64 ? 64 : machine == 0x014C ? 32 : 0;
                }
            }
            catch { return 0; }
        }

        /// <summary>Путь внутри профиля пользователя — как %LOCALAPPDATA%\… / %USERPROFILE%\…, без имени учётки.</summary>
        public static string MaskProfile(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var m = Regex.Match(path, @"^[A-Za-z]:\\Users\\[^\\]+\\AppData\\Local(\\.*)?$", RegexOptions.IgnoreCase);
            if (m.Success) return "%LOCALAPPDATA%" + m.Groups[1].Value;
            m = Regex.Match(path, @"^[A-Za-z]:\\Users\\[^\\]+(\\.*)?$", RegexOptions.IgnoreCase);
            return m.Success ? "%USERPROFILE%" + m.Groups[1].Value : path;
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

        /// <summary>
        /// Платформа «для пользователя» лежит в его профиле, куда веб-серверу хода нет: выдать учётке веб-сервера чтение папки
        /// этой версии 1С (только чтение программ; при удалении базы не снимаем — папкой могут пользоваться другие базы).
        /// </summary>
        public static void GrantRead(Platform1C p, string account)
        {
            if (!p.PerUser) return;
            var dir = Path.GetDirectoryName(p.Bin.TrimEnd('\\'));
            var r = Proc.Run(Path.Combine(Paths.NativeSystem32, "icacls.exe"), $"{Proc.Quote(dir)} /grant \"{account}:(OI)(CI)RX\" /T /Q");
            if (r.ExitCode != 0) throw new AgentException(Txt.F("PlatformPerUserGrantFailed", MaskProfile(dir), r.Combined), Txt.S("HintPlatformMachineWide"));
            Log.Ok(Txt.F("PlatformPerUserGranted", account, MaskProfile(dir)));
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
