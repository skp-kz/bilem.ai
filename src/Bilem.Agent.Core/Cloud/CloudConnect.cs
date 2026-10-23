using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Bilem.Agent.Core.Mcp;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Cloud
{
    /// <summary>
    /// Подключение базы к Bilem Cloud одним кодом (решение 0017, вторая редакция): агент показывает код, его вставляют в кабинете,
    /// владелец нажимает «Разрешить» на телефоне — агент узнаёт это опросом заявки. Отключение (раздел 5) —
    /// для окна и командной строки. Ключ компьютера сохраняется только зашифрованным в cloud.json; служба подхватывает его сама и открывает канал.
    /// </summary>
    public static class CloudConnect
    {
        /// <summary>Шаг 1: заявка в облаке → код подключения (его вставляют в кабинете: 1С-Базы → Подключить базу).</summary>
        public static ConnectRequest Begin(string slug)
        {
            var cfg = AgentConfig.Load();
            var b = cfg.Find(slug) ?? throw new AgentException(Txt.F("NoSuchBase", slug), Txt.S("HintList"));
            var store = CloudStore.Update(s => { s.EnsureAgentId(); s.EnsureBaseId(b.Slug); });
            var orgs = Organizations(b);
            ConnectRequest req;
            try { req = CloudApi.CreateRequest(cfg, store, b, orgs); }
            catch (CloudException e) when (e.Code == "key_revoked")
            {
                // Ключ отозван облаком — забыть его и подать заявку без ключа: облако выдаст новый при «Разрешить» (иначе «Подключить…» не работал бы никогда)
                Log.Warn(Txt.S("CloudKeyRevokedLog"));
                store = CloudStore.Update(s => s.SetKey(null));
                req = CloudApi.CreateRequest(cfg, store, b, orgs);
            }
            Log.Info(Txt.F("CloudRequestLog", b.Slug));
            return req;
        }

        /// <summary>
        /// Шаг 2: опрос заявки каждые `poll_interval` секунд, пока владелец не нажмёт «Разрешить» на телефоне (или не отклонит, или не истечёт срок).
        /// <paramref name="changed"/> — при каждой смене статуса (pending → waiting_owner…). Подключено — привязка и ключ сохранены;
        /// отклонено / истекло / ошибка — CloudException с текстом для человека. Нет связи и 5xx — опрос продолжается.
        /// </summary>
        public static CloudBinding Wait(string slug, ConnectRequest req, Action<ConnectStatus> changed, CancellationToken ct)
        {
            var cfg = AgentConfig.Load();
            slug = cfg.Find(slug)?.Slug ?? slug;
            string last = null;
            while (true)
            {
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(req.PollInterval));
                ct.ThrowIfCancellationRequested();
                ConnectStatus st;
                try { st = CloudApi.Poll(cfg, CloudStore.Load(), req.RequestId); }
                catch (CloudException e) when (e.Code == "key_revoked")
                {
                    // Bearer неверен — забыть ключ и повторить опрос без него
                    Log.Warn(Txt.S("CloudKeyRevokedLog"));
                    CloudStore.Update(s => s.SetKey(null));
                    continue;
                }
                catch (CloudException e) when (e.Http == 0 || e.Http >= 500)
                {
                    continue;   // нет связи или сбой облака — повторять, заявка жива до expires_at
                }
                if (st.Status != last) { last = st.Status; changed?.Invoke(st); }
                switch (st.Status)
                {
                    case "approved": return Approved(slug, st);
                    case "declined": throw new CloudException("declined", 200, Txt.S("CloudDeclined"));
                    case "expired": throw new CloudException("expired", 200, Txt.S("CloudRequestExpired"));
                    case "failed": throw new CloudException(st.Error ?? "failed", 200, CloudApi.Message(st.Error ?? "failed", st.Message));
                }
            }
        }

        /// <summary>«Разрешить» — привязка базы; ключ — только в первом ответе approved, сразу сохраняем.</summary>
        private static CloudBinding Approved(string slug, ConnectStatus r)
        {
            CloudBinding bound = null;
            var store = CloudStore.Update(s =>
            {
                if (!string.IsNullOrEmpty(r.AgentKey)) s.SetKey(r.AgentKey);
                s.BaseIds[slug] = r.BaseId ?? s.EnsureBaseId(slug);
                s.Bindings.RemoveAll(x => string.Equals(x.Slug, slug, StringComparison.OrdinalIgnoreCase));
                s.Bindings.Add(bound = new CloudBinding
                {
                    Slug = slug, BaseId = s.BaseIds[slug], OrgId = r.OrgId, OrgName = r.OrgName, OrgUrl = r.OrgUrl, Login = r.Login,
                    ConnectedAt = DateTime.Now,
                });
            });
            // Ключ выдан раньше, но до нас не дошёл (обрыв на первом approved) — канал не откроется; «Подключить…» ещё раз выдаст новый
            if (!store.HasKey) Log.Warn(Txt.F("CloudKeyLostLog", slug));
            Log.Ok(Txt.F("CloudConnectedLog", slug, r.OrgName ?? "—"));
            return bound;
        }

        /// <summary>Отключить базу: снять привязку в облаке и у себя. Облако недоступно — привязку у себя снимаем всё равно.</summary>
        public static void Disconnect(string slug)
        {
            var cfg = AgentConfig.Load();
            var store = CloudStore.Load();
            var b = store.Binding(slug);
            if (b == null) return;
            string warn = null;
            if (!b.RevokedInCabinet && store.HasKey)
            {
                try { CloudApi.Disconnect(cfg, store, b.BaseId); }
                catch (CloudException e) { warn = e.Message; }
            }
            CloudStore.Update(s => s.Bindings.RemoveAll(x => string.Equals(x.Slug, slug, StringComparison.OrdinalIgnoreCase)));
            Log.Ok(Txt.F("CloudDisconnectedLog", slug));
            if (warn != null) Log.Warn(Txt.F("CloudDisconnectOffline", warn));
        }

        /// <summary>
        /// Организации базы для заявки: наименование и БИН/ИИН (решение Славы 20.10 — облако использует только для подключения,
        /// в открытом виде не хранит). Не прочиталось — заявка без них: кабинет покажет только базу.
        /// </summary>
        public static JArray Organizations(BaseConfig b)
        {
            try
            {
                // Свой клиент без фильтра «Доступа»: заявке нужны только организации, это не ответ инструмента ИИ
                var od = new ODataClient(b.OData, b.User, Secrets.ReadSecretFile(b.SecretFile));
                var rows = od.Rows("Catalog_Организации", new Dictionary<string, string> { ["$select"] = "Description,ИдентификационныйНомер,DeletionMark" });
                return new JArray(rows.Where(o => (bool?)o["DeletionMark"] != true)
                    .Select(o => new JObject { ["name"] = (string)o["Description"], ["bin"] = (string)o["ИдентификационныйНомер"] }));
            }
            catch (Exception e)
            {
                Log.Warn(Txt.F("CloudOrgsFailed", b.Slug, e.Message));
                return new JArray();
            }
        }
    }
}
