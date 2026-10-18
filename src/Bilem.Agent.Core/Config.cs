using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace Bilem.Agent.Core
{
    /// <summary>Подключённая база. Без данных из 1С: только адреса, имена объектов, версии и признаки проверок.</summary>
    public sealed class BaseConfig
    {
        public string Slug { get; set; }
        public string Path { get; set; }
        public string Configuration { get; set; }
        public string Synonym { get; set; }
        public string Version { get; set; }
        public bool HasBsp { get; set; }
        public string PlatformVersion { get; set; }
        public int PlatformBits { get; set; }
        public string Publication { get; set; }
        public string Pool { get; set; }
        public string OData { get; set; }
        public string User { get; set; } = "bilem";
        public string SecretFile { get; set; }
        public List<string> Roles { get; set; } = new List<string>();
        public List<string> ODataObjects { get; set; } = new List<string>();
        /// <summary>Объекты состава OData, в которых при онбординге были записи (только имена — для проверки «bilem видит»).</summary>
        public List<string> NonEmpty { get; set; } = new List<string>();
        public DateTime? SetupAt { get; set; }
        public DateTime? VerifiedAt { get; set; }
        /// <summary>Когда последний раз проверено, что запись через OData запрещена.</summary>
        public DateTime? WriteDeniedAt { get; set; }
        /// <summary>Итог последней проверки: все проверки прошли.</summary>
        public bool? LastCheckOk { get; set; }

        // ----- Вкладка «Доступ» (только имена объектов метаданных) -----
        /// <summary>Разделы и человеческие имена объектов — из подсистем самой конфигурации (или запасная раскладка профиля).</summary>
        public AccessCatalog Catalog { get; set; }
        /// <summary>Слой MCP: какие объекты видят инструменты. Пусто (null) — весь состав OData.</summary>
        public List<string> Read { get; set; }
        /// <summary>Слой MCP: для каких объектов разрешены черновики (инструменты записи — v0.3).</summary>
        public List<string> Write { get; set; } = new List<string>();
        /// <summary>Слой 1С: для каких объектов у bilem есть роль записи (применено «Применить» с паролем администратора).</summary>
        public List<string> WriteInOneC { get; set; } = new List<string>();
        /// <summary>Роли bilem сверх ролей профиля — добавлены «Доступом» (запись или чтение, которого не хватало).</summary>
        public List<string> ExtraRoles { get; set; } = new List<string>();

        /// <summary>Что видят инструменты ИИ: выбранное на «Доступе» или весь состав OData — без служебных объектов.</summary>
        /// <summary>Что видят инструменты ИИ: выбор «Доступа»; не выбирали — состав OData без служебных объектов.</summary>
        [JsonIgnore] public List<string> ReadEffective => Read != null ? Read.ToList() : (ODataObjects ?? new List<string>()).Where(x => !Profiles.IsService(x)).ToList();

        /// <summary>Письмо от базы: имя отправителя, шаблоны, подпись (ящик — общий, AgentConfig.Smtp).</summary>
        public Bilem.Agent.Core.Mail.MailSettings Mail { get; set; }

        /// <summary>Печатные формы базы (картинки — файлами в bases&lt;имя&gt;print).</summary>
        public Bilem.Agent.Core.Print.PrintSettings Print { get; set; }

        [JsonIgnore] public string DisplayName => string.IsNullOrEmpty(Synonym) ? Configuration : Synonym;
    }

    public sealed class AgentConfig
    {
        public int Port { get; set; } = 8796;
        /// <summary>Защитный максимум строк в ответе любого инструмента MCP; больше — обрезается с пометкой.</summary>
        public int MaxRows { get; set; } = 200;
        /// <summary>Выписка по счёту старше стольких рабочих дней — предупреждение «остаток и должники могут быть неточны».</summary>
        public int StaleWorkdays { get; set; } = 3;
        /// <summary>«Сервис» → «Обновления»: получать бета-версии (пререлизы GitHub).</summary>
        public bool UpdateBeta { get; set; }
        /// <summary>Токен MCP, DPAPI LocalMachine.</summary>
        public string TokenProtected { get; set; }
        public List<BaseConfig> Bases { get; set; } = new List<BaseConfig>();
        /// <summary>SMTP-клиент — один ящик на все базы; тумблер «SMTP-клиент включён» — Smtp.Enabled.</summary>
        public Bilem.Agent.Core.Mail.SmtpAccount Smtp { get; set; } = new Bilem.Agent.Core.Mail.SmtpAccount();
        /// <summary>Подсказку «Bilem Agent работает в трее» уже показали — больше не показываем.</summary>
        public bool TrayHintShown { get; set; }

        private static readonly JsonSerializerSettings Js = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DateFormatString = "yyyy-MM-ddTHH:mm:ss",
        };

        public static AgentConfig Load()
        {
            var f = Paths.ConfigFile;
            if (!File.Exists(f)) return new AgentConfig();
            return JsonConvert.DeserializeObject<AgentConfig>(File.ReadAllText(f, Encoding.UTF8), Js) ?? new AgentConfig();
        }

        public void Save()
        {
            Paths.Ensure();
            var f = Paths.ConfigFile;
            var tmp = f + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(this, Js), new UTF8Encoding(false));
            if (File.Exists(f)) File.Replace(tmp, f, null); else File.Move(tmp, f);
            Secrets.RestrictToAdmins(f);
        }

        public BaseConfig Find(string slug) =>
            Bases.FirstOrDefault(b => string.Equals(b.Slug, slug, StringComparison.OrdinalIgnoreCase));

        public void Upsert(BaseConfig b)
        {
            var i = Bases.FindIndex(x => string.Equals(x.Slug, b.Slug, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) Bases[i] = b; else Bases.Add(b);
            Bases.Sort((x, y) => string.Compare(x.Slug, y.Slug, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Токен MCP: создаётся при первом обращении.</summary>
        public string GetOrCreateToken()
        {
            if (!string.IsNullOrEmpty(TokenProtected))
            {
                try { return Secrets.Unprotect(TokenProtected); }
                catch { /* ключ машины сменился — выпускаем новый */ }
            }
            // Токена нет: выпускаем под общим замком машины и перечитываем конфиг — другой процесс (serve, окно, команда url)
            // мог выпустить его только что. Иначе на новой установке два процесса выпускали разные токены → 401 (v0.3.0-beta.1).
            using (var m = TokenMutex())
            {
                var locked = false;
                try { locked = m.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (System.Threading.AbandonedMutexException) { locked = true; }
                try
                {
                    var disk = Load();
                    if (!string.IsNullOrEmpty(disk.TokenProtected))
                    {
                        try
                        {
                            var t = Secrets.Unprotect(disk.TokenProtected);
                            TokenProtected = disk.TokenProtected;
                            return t;
                        }
                        catch { /* в файле токен другой машины — выпускаем новый */ }
                    }
                    var fresh = Secrets.RandomToken();
                    disk.TokenProtected = TokenProtected = Secrets.Protect(fresh);
                    disk.Save();
                    return fresh;
                }
                finally { if (locked) m.ReleaseMutex(); }
            }
        }

        private static System.Threading.Mutex TokenMutex()
        {
            // Один замок на папку данных (у тестов — своя папка)
            string name;
            using (var h = System.Security.Cryptography.SHA256.Create())
                name = "BilemAgentToken-" + BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(Paths.Home.ToLowerInvariant())), 0, 8).Replace("-", "");
            try { return new System.Threading.Mutex(false, "Global\\" + name); }
            catch (UnauthorizedAccessException) { return new System.Threading.Mutex(false, "Local\\" + name); }
        }

        public string RotateToken()
        {
            var t = Secrets.RandomToken();
            TokenProtected = Secrets.Protect(t);
            Save();
            return t;
        }

        public string McpUrl(string token) => $"http://127.0.0.1:{Port}/mcp/{token}";
    }
}
