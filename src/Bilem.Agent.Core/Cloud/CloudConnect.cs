using System;
using System.Collections.Generic;
using System.Linq;
using Bilem.Agent.Core.Mcp;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Cloud
{
    /// <summary>
    /// Подключение базы к Bilem Cloud кодом (часть L, протокол разделы 1–3) и отключение (раздел 5) — для окна и командной строки.
    /// Ключ компьютера сохраняется только зашифрованным в cloud.json; служба подхватывает его сама и открывает канал.
    /// </summary>
    public static class CloudConnect
    {
        /// <summary>Шаг 1: заявка в облаке. Возвращает адрес кабинета — его открывают в браузере.</summary>
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
                // Ключ отозван облаком — забыть его и подать заявку без ключа: обмен кода выдаст новый (иначе «Подключить…» не работал бы никогда)
                Log.Warn(Txt.S("CloudKeyRevokedLog"));
                store = CloudStore.Update(s => s.SetKey(null));
                req = CloudApi.CreateRequest(cfg, store, b, orgs);
            }
            Log.Info(Txt.F("CloudRequestLog", b.Slug));
            return req;
        }

        /// <summary>Шаг 3: код из кабинета → привязка базы (и ключ компьютера, если его ещё не было).</summary>
        public static CloudBinding Finish(string slug, ConnectRequest req, string code)
        {
            var cfg = AgentConfig.Load();
            var store = CloudStore.Load();
            ExchangeResult r;
            try { r = CloudApi.Exchange(cfg, store, req.RequestId, code); }
            catch (CloudException e) when (e.Code == "key_revoked")
            {
                CloudStore.Update(s => s.SetKey(null));
                throw;
            }
            CloudBinding bound = null;
            CloudStore.Update(s =>
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
