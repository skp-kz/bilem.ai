using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core
{
    public sealed class VerifyResult
    {
        public bool Ok => Failures == 0;
        public int Failures { get; set; }
        public int ReadOk { get; set; }
        public int ReadTotal { get; set; }
        public bool WriteDenied { get; set; }
        public int Sets { get; set; }
        /// <summary>Код ответа на $metadata (0 — обрыв) и падение веб-сервера из журнала событий — для переключения на Apache (J5).</summary>
        public int MetadataCode { get; set; } = 200;
        public string Crash { get; set; }
    }

    /// <summary>
    /// Проверки подключённой базы (шаг 10 спайка): чтение каждого объекта состава, запрет записи, 401 без пароля, 403 по адресам машины.
    /// В журнал — только коды ответов и счётчики, без данных.
    /// </summary>
    public static class Verifier
    {
        public static VerifyResult Run(BaseConfig b, string password)
        {
            var r = new VerifyResult();
            var od = new ODataClient(b.OData, b.User, password);

            var started = DateTime.Now;
            var meta = od.Send("GET", b.OData + "/$metadata");
            r.MetadataCode = meta.Code;
            if (meta.Code != 200)
            {
                Log.Fail(Txt.F("VerifyMetadataFailed", meta.Code, ODataClient.ErrorText(meta.Body)));
                // Лицензия 1С не видна учётке веб-сервера (J6) — понятная причина вместо общего «HTTP 500»
                if ((meta.Body ?? "").IndexOf("лиценз", StringComparison.OrdinalIgnoreCase) >= 0) Log.Fail(Txt.S("J6License"));
                // Обрыв соединения или 5xx — часто это упавший рабочий процесс IIS: причина в журнале событий Windows
                var crash = meta.Code == 0 || meta.Code >= 500 ? Crashes.IisExplain(started) : null;
                r.Crash = crash;
                if (crash != null) { Log.Fail(crash); Log.Fail(Txt.S("WhatToDo") + " " + Txt.S("HintIisCrash")); }
                else Log.Fail(Txt.S("WhatToDo") + " " + HintForCode(meta.Code));
                r.Failures++;
                return r;
            }
            var sets = ODataClient.EntitySets(meta.Body);
            r.Sets = sets.Count;
            Log.Ok(Txt.F("VerifyMetadataOk", sets.Count));

            // (1) Чтение: каждый объект — 200, и не пусто там, где при онбординге были записи
            foreach (var fn in b.ODataObjects)
            {
                r.ReadTotal++;
                var set = ODataClient.SetName(fn);
                if (!sets.Contains(set)) { Log.Fail(Txt.F("VerifySetMissing", set)); r.Failures++; continue; }
                var g = od.Get(set, new Dictionary<string, string> { ["$top"] = "1" });
                if (g.Code != 200) { Log.Fail(Txt.F("VerifyReadHttp", set, g.Code, ODataClient.ErrorText(g.Body))); r.Failures++; continue; }
                var n = ((JArray)JObject.Parse(g.Body)["value"])?.Count ?? 0;
                if (n == 0 && b.NonEmpty.Contains(fn)) { Log.Fail(Txt.F("VerifyReadEmpty", set)); r.Failures++; continue; }
                r.ReadOk++;
            }
            Log.Write(r.ReadOk == r.ReadTotal ? LogLevel.Ok : LogLevel.Warn, Txt.F("VerifyReadSummary", r.ReadOk, r.ReadTotal, b.NonEmpty.Count));

            // Выключенное «Доступом» в составе OData не видно
            var off = Access.CatalogOf(b).Items.Select(i => i.FullName).Where(fn => !b.ODataObjects.Contains(fn)).ToList();
            var leaked = off.Where(fn => sets.Contains(ODataClient.SetName(fn))).ToList();
            if (leaked.Count > 0) { Log.Fail(Txt.F("VerifyOffVisible", string.Join(", ", leaked.Select(ODataClient.SetName)))); r.Failures++; }
            else if (off.Count > 0) Log.Ok(Txt.F("VerifyOffHidden", off.Count));

            // (2) Запись запрещена: отказ 1С — HTTP 500 «Нарушение прав доступа» (known-issues 9), поэтому судим по результату
            var writeFails = 0;
            // Проверяем на объектах, где запись выключена «Доступом»: POST «{}» туда, где она разрешена, создал бы пустой документ
            var docSet = WriteTestSet(b, "Документ.", "Документ.СчетНаОплатуПокупателю", "Документ.РеализацияТоваровУслуг");
            if (docSet != null && sets.Contains(docSet))
            {
                var before = Count(od, docSet);
                var post = od.Send("POST", $"{b.OData}/{docSet}?$format=json", "{}");
                var after = Count(od, docSet);
                if (post.Ok || after != before) { Log.Fail(Txt.F("VerifyPostAllowed", docSet, post.Code, before, after)); writeFails++; }
                else Log.Ok(Txt.F("VerifyPostDenied", docSet, post.Code, ODataClient.ErrorText(post.Body), before, after));
            }
            else Log.Warn(Txt.F("VerifyPostSkipped", docSet));

            var catSet = WriteTestSet(b, "Справочник.", "Справочник.Контрагенты", "Справочник.Номенклатура");
            var cat = catSet == null ? new HttpReply { Code = 0 } : od.Get(catSet, new Dictionary<string, string> { ["$top"] = "1", ["$select"] = "Ref_Key,Description,DataVersion" });
            var item = cat.Code == 200 ? ((JArray)JObject.Parse(cat.Body)["value"])?.FirstOrDefault() as JObject : null;
            if (item != null)
            {
                // PATCH тем же наименованием: даже при разрешённой записи данные не поменялись бы
                var one = $"{b.OData}/{catSet}(guid'{item["Ref_Key"]}')";
                var patch = od.Send("PATCH", one + "?$format=json", new JObject { ["Description"] = item["Description"] }.ToString(Newtonsoft.Json.Formatting.None));
                var ver2 = od.Send("GET", one + "?$select=DataVersion&$format=json");
                var changed = ver2.Code == 200 && (string)JObject.Parse(ver2.Body)["DataVersion"] != (string)item["DataVersion"];
                if (patch.Ok || changed) { Log.Fail(Txt.F("VerifyPatchAllowed", catSet, patch.Code)); writeFails++; }
                else Log.Ok(Txt.F("VerifyPatchDenied", catSet, patch.Code, ODataClient.ErrorText(patch.Body)));
            }
            else Log.Warn(Txt.F("VerifyPatchSkipped", catSet, cat.Code));
            r.WriteDenied = writeFails == 0;
            r.Failures += writeFails;

            // (3) Периметр
            var anon = new ODataClient(b.OData, null, null).Send("GET", b.OData + "/$metadata", withAuth: false);
            if (anon.Code == 401) Log.Ok(Txt.S("VerifyNoPassword401")); else { Log.Fail(Txt.F("VerifyNoPasswordCode", anon.Code)); r.Failures++; }
            var ips = MachineAddresses();
            foreach (var ip in ips)
            {
                var ext = od.Send("GET", b.OData.Replace("127.0.0.1", ip) + "/$metadata", timeoutMs: 10000);
                var shown = MaskIp(ip);
                // IIS отвечает 403 (ограничения по IP); свой Apache по адресу машины вообще не слушает — соединение не устанавливается
                if (ext.Code == 403) Log.Ok(Txt.F("VerifyExternal403", shown));
                else if (ext.Code == 0 && Web.WebServerKind.Of(b) == Web.WebServerKind.Apache) Log.Ok(Txt.F("VerifyExternalClosed", shown));
                else { Log.Fail(Txt.F("VerifyExternalCode", shown, ext.Code)); r.Failures++; }
            }
            if (ips.Count == 0) Log.Warn(Txt.S("VerifyNoExternalIp"));
            return r;
        }

        /// <summary>Набор для проверки запрета записи: первый из предпочтительных, иначе любой того же вида — в составе и без записи.</summary>
        internal static string WriteTestSet(BaseConfig b, string kind, params string[] preferred)
        {
            var write = new HashSet<string>(b.Write ?? new List<string>());
            var pool = b.ODataObjects ?? new List<string>();
            var fn = preferred.Where(pool.Contains).FirstOrDefault(x => !write.Contains(x))
                ?? pool.FirstOrDefault(x => x.StartsWith(kind, StringComparison.Ordinal) && !write.Contains(x));
            return fn == null ? null : ODataClient.SetName(fn);
        }

        private static string Count(ODataClient od, string set) => od.Send("GET", $"{od.BaseUrl}/{set}/$count").Body?.Trim();

        public static string HintForCode(int code)
        {
            switch (code)
            {
                case 401: return Txt.S("Hint401");
                case 403: return Txt.S("Hint403");
                case 404: return Txt.S("Hint404");
                case 500: return Txt.S("Hint500");
                case 503: return Txt.S("Hint503");
                default: return Txt.S("HintSendLog");
            }
        }

        /// <summary>IPv4-адреса машины, кроме 127.* и 169.254.*.</summary>
        public static List<string> MachineAddresses() =>
            NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(a => a.ToString()).Distinct().ToList();

        public static string MaskIp(string ip)
        {
            var p = ip.Split('.');
            return p.Length == 4 ? $"{p[0]}.{p[1]}.x.x" : ip;
        }
    }
}
