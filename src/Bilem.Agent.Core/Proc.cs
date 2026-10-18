using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Bilem.Agent.Core
{
    public sealed class ProcResult
    {
        public int ExitCode { get; set; }
        public string Out { get; set; }
        public string Err { get; set; }
        public bool TimedOut { get; set; }
        public string Combined => ((Out ?? "") + " " + (Err ?? "")).Trim();
    }

    /// <summary>Запуск внешних программ (appcmd, icacls, dism, webinst, regsvr32, помощник COM) с захватом вывода.</summary>
    public static class Proc
    {
        /// <param name="oem">Вывод консольных утилит Windows — в OEM-кодировке (cp866), иначе — UTF-8.</param>
        public static ProcResult Run(string exe, string args, string stdin = null, bool oem = true, int timeoutMs = 300000)
        {
            var enc = oem ? Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage) : new UTF8Encoding(false);
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin != null,
                StandardOutputEncoding = enc,
                StandardErrorEncoding = enc,
            };
            using (var p = new Process { StartInfo = psi })
            {
                var sbOut = new StringBuilder();
                var sbErr = new StringBuilder();
                p.OutputDataReceived += (s, e) => { if (e.Data != null) sbOut.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) sbErr.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (stdin != null)
                {
                    // StandardInputEncoding в .NET Framework нет — пишем байты UTF-8 сами
                    var bytes = new UTF8Encoding(false).GetBytes(stdin);
                    p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                    p.StandardInput.BaseStream.Flush();
                    p.StandardInput.Close();
                }
                var done = p.WaitForExit(timeoutMs);
                if (!done) { try { p.Kill(); } catch { } }
                else p.WaitForExit();
                return new ProcResult
                {
                    ExitCode = done ? p.ExitCode : -1,
                    Out = sbOut.ToString().Trim(),
                    Err = sbErr.ToString().Trim(),
                    TimedOut = !done,
                };
            }
        }

        public static string Quote(string s) => "\"" + (s ?? "").Replace("\"", "\\\"") + "\"";

        public static bool Exists(string path) => !string.IsNullOrEmpty(path) && File.Exists(path);
    }
}
