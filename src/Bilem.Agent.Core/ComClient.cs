using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core
{
    /// <summary>Вызов помощника COM (bilem-com32/64.exe) — отдельный процесс нужной разрядности.</summary>
    public sealed class ComClient
    {
        private readonly Platform1C _platform;

        public ComClient(Platform1C platform) { _platform = platform; }

        /// <summary>Строка соединения файловой базы с кавычками по правилам 1С (кавычка внутри значения удваивается).</summary>
        public static string FileConnection(string basePath, string user, string password)
        {
            string q(string s) => (s ?? "").Replace("\"", "\"\"");
            var cs = $"File=\"{q(basePath)}\";";
            if (!string.IsNullOrEmpty(user)) cs += $"Usr=\"{q(user)}\";" + "Pwd" + $"=\"{q(password)}\";";
            return cs;
        }

        public List<JObject> Run(string connection, params ComOp[] ops)
        {
            var exe = Platform.ComHelper(_platform);
            if (!File.Exists(exe)) throw new AgentException(Txt.F("ComHelperMissing", Path.GetFileName(exe)), Txt.S("HintReinstall"));
            var job = new ComJob { Connection = connection, Ops = new List<ComOp>(ops) };
            var res = Proc.Run(exe, "", JsonConvert.SerializeObject(job, ComReply.Js), oem: false, timeoutMs: 600000);
            if (res.TimedOut) throw new AgentException(Txt.S("ComTimeout"), Txt.S("HintBaseBusy"));
            ComReply reply;
            try { reply = JsonConvert.DeserializeObject<ComReply>(res.Out); }
            catch { reply = null; }
            if (reply == null) throw new AgentException(Txt.F("ComCrashed", res.ExitCode, Trim(res.Err)), Txt.S("HintSendLog"));
            if (!reply.Ok) throw new AgentException(Txt.F("ComError", reply.Error), HintFor(reply.Error));
            return reply.Results;
        }

        private static string Trim(string s) => string.IsNullOrEmpty(s) ? "—" : (s.Length > 300 ? s.Substring(0, 300) : s);

        /// <summary>Подсказка по тексту ошибки подключения — как в спайке.</summary>
        public static string HintFor(string err)
        {
            var m = (err ?? "").ToLowerInvariant();
            if (m.Contains("заблокир") || m.Contains("монопол") || m.Contains("exclusive") || m.Contains("lock")) return Txt.S("HintBaseBusy");
            if (m.Contains("обновлен") || m.Contains("версии") || m.Contains("version")) return Txt.S("HintBaseNeedsUpdate");
            if (m.Contains("лиценз") || m.Contains("licen")) return Txt.S("HintNoLicense");
            if (m.Contains("идентификац") || m.Contains("пароль") || m.Contains("authent")) return Txt.S("HintAdminLogin");
            return Txt.S("HintSendLog");
        }
    }

    /// <summary>Ошибка для человека: «что случилось → что делать».</summary>
    public sealed class AgentException : Exception
    {
        public string Hint { get; }
        /// <summary>Коротко — для подсказки значка в трее (63 символа).</summary>
        public string Short { get; set; }
        public AgentException(string message, string hint = null) : base(message) { Hint = hint; }
    }
}
