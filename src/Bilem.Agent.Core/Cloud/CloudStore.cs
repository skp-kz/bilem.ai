using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace Bilem.Agent.Core.Cloud
{
    /// <summary>Привязка базы к организации в облаке (протокол канала, «Сущности»). Без данных 1С.</summary>
    public sealed class CloudBinding
    {
        public string Slug { get; set; }
        public string BaseId { get; set; }
        public string OrgId { get; set; }
        public string OrgName { get; set; }
        public string OrgUrl { get; set; }
        /// <summary>E-mail, кто подтвердил подключение в кабинете.</summary>
        public string Login { get; set; }
        public DateTime? ConnectedAt { get; set; }
        /// <summary>Отключена в кабинете (base_state revoked) — показываем, пока человек не подключит заново или не уберёт.</summary>
        public bool RevokedInCabinet { get; set; }
    }

    /// <summary>
    /// Bilem Cloud на этом компьютере (часть L): номер установки, ключ компьютера (DPAPI), привязки баз.
    /// Отдельный файл cloud.json рядом с config.json: его пишут и окно (подключить / отключить), и служба (отключена в кабинете,
    /// ключ отозван) — с замком и перечитыванием перед записью, чтобы не затирать друг друга.
    /// Ключ — только зашифрованным, наружу (журнал, sysinfo, отчёт) не выходит.
    /// </summary>
    public sealed class CloudStore
    {
        public string AgentId { get; set; }
        public string KeyProtected { get; set; }
        public List<CloudBinding> Bindings { get; set; } = new List<CloudBinding>();
        /// <summary>base_id баз, для которых уже делали заявку, — один номер базы навсегда (slug → base_id).</summary>
        public Dictionary<string, string> BaseIds { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string FilePath => Path.Combine(Paths.Home, "cloud.json");

        private static readonly JsonSerializerSettings Js = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DateFormatString = "yyyy-MM-ddTHH:mm:ss",
        };

        public static CloudStore Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new CloudStore();
                var s = JsonConvert.DeserializeObject<CloudStore>(File.ReadAllText(FilePath, Encoding.UTF8), Js) ?? new CloudStore();
                s.BaseIds = new Dictionary<string, string>(s.BaseIds ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                s.Bindings = s.Bindings ?? new List<CloudBinding>();
                return s;
            }
            catch (Exception e) { Log.Warn(Txt.F("CloudStoreBroken", e.Message)); return new CloudStore(); }
        }

        /// <summary>Перечитать, изменить, записать — под замком машины (окно и служба пишут файл оба).</summary>
        public static CloudStore Update(Action<CloudStore> change)
        {
            using (var m = Mutex())
            {
                var locked = false;
                try { locked = m.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (System.Threading.AbandonedMutexException) { locked = true; }
                try
                {
                    var s = Load();
                    change(s);
                    s.Save();
                    return s;
                }
                finally { if (locked) m.ReleaseMutex(); }
            }
        }

        private void Save()
        {
            Paths.Ensure();
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(this, Js), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
            Secrets.RestrictToAdmins(FilePath);
        }

        [JsonIgnore] public bool HasKey => !string.IsNullOrEmpty(KeyProtected);

        /// <summary>Ключ компьютера; null — нет или не расшифровывается (данные с другой машины).</summary>
        public string Key()
        {
            if (!HasKey) return null;
            try { return Secrets.Unprotect(KeyProtected); } catch { return null; }
        }

        public void SetKey(string key) => KeyProtected = string.IsNullOrEmpty(key) ? null : Secrets.Protect(key);

        public string EnsureAgentId() => AgentId = string.IsNullOrEmpty(AgentId) ? Guid.NewGuid().ToString() : AgentId;

        public string EnsureBaseId(string slug)
        {
            if (!BaseIds.TryGetValue(slug, out var id) || string.IsNullOrEmpty(id)) BaseIds[slug] = id = Guid.NewGuid().ToString();
            return id;
        }

        public CloudBinding Binding(string slug) => Bindings.FirstOrDefault(b => string.Equals(b.Slug, slug, StringComparison.OrdinalIgnoreCase));
        public CloudBinding ByBaseId(string baseId) => Bindings.FirstOrDefault(b => string.Equals(b.BaseId, baseId, StringComparison.OrdinalIgnoreCase));

        /// <summary>Привязки, по которым идут вызовы: подключены и не отключены в кабинете, и база ещё есть в агенте.</summary>
        public List<CloudBinding> Active(AgentConfig cfg) =>
            Bindings.Where(b => !b.RevokedInCabinet && cfg.Find(b.Slug) != null).ToList();

        private static System.Threading.Mutex Mutex()
        {
            string name;
            using (var h = System.Security.Cryptography.SHA256.Create())
                name = "BilemAgentCloud-" + BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(Paths.Home.ToLowerInvariant())), 0, 8).Replace("-", "");
            try { return new System.Threading.Mutex(false, "Global\\" + name); }
            catch (UnauthorizedAccessException) { return new System.Threading.Mutex(false, "Local\\" + name); }
        }
    }
}
