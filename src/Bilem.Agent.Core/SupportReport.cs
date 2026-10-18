using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core
{
    /// <summary>Отчёт для поддержки: zip с журналами, конфигом без токена и сведениями о системе. Паролей и токенов нет.</summary>
    public static class SupportReport
    {
        public static string Create(string targetDir = null)
        {
            targetDir = targetDir ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var zipPath = Path.Combine(targetDir, $"bilem-support-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                if (Directory.Exists(Paths.LogsDir))
                    foreach (var f in Directory.GetFiles(Paths.LogsDir, "*.log").OrderByDescending(File.GetLastWriteTime).Take(14))
                        Add(zip, "logs/" + Path.GetFileName(f), Connectors.Mask(ReadShared(f)));
                if (File.Exists(Paths.ConfigFile))
                {
                    var cfg = JObject.Parse(File.ReadAllText(Paths.ConfigFile, Encoding.UTF8));
                    cfg.Remove("TokenProtected");
                    Add(zip, "config.json", cfg.ToString());
                }
                if (Directory.Exists(Paths.StateDir))
                    foreach (var f in Directory.GetFiles(Paths.StateDir, "*.json"))
                        Add(zip, "state/" + Path.GetFileName(f), File.ReadAllText(f, Encoding.UTF8));
                try { Add(zip, "sysinfo.txt", SysInfo.Collect()); }
                catch (Exception e) { Add(zip, "sysinfo.txt", e.ToString()); }
                try { Add(zip, "diagnostics.txt", Diagnostics.Report(Diagnostics.Run())); }
                catch (Exception e) { Add(zip, "diagnostics.txt", e.ToString()); }
            }
            return zipPath;
        }

        private static string ReadShared(string f)
        {
            using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8)) return sr.ReadToEnd();
        }

        private static void Add(ZipArchive zip, string name, string text)
        {
            var e = zip.CreateEntry(name);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(text);
        }
    }
}
