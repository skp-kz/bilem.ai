using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core
{
    /// <summary>
    /// Задание помощнику COM (bilem-com32/64.exe): строка соединения и список операций — одно подключение к базе
    /// на весь список. Передаётся через stdin (пароль администратора не попадает в командную строку).
    /// </summary>
    public sealed class ComJob
    {
        public string Connection { get; set; }
        public List<ComOp> Ops { get; set; } = new List<ComOp>();
    }

    public sealed class ComOp
    {
        /// <summary>inspect | listRoles | userInfo | ensureUser | dropUser | ensureOData | dataFlags | metaCatalog | rightsMap | setOData</summary>
        public string Op { get; set; }
        public string User { get; set; }
        public string FullName { get; set; }
        /// <summary>Новый пароль пользователя ИБ; пусто — пароль не менять.</summary>
        public string Password { get; set; }
        public List<string> Roles { get; set; }
        public List<string> Objects { get; set; }
        /// <summary>rightsMap: объекты, по которым считать широту ролей-кандидатов.</summary>
        public List<string> Scope { get; set; }
    }

    public sealed class ComReply
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public List<JObject> Results { get; set; } = new List<JObject>();

        public static readonly JsonSerializerSettings Js = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
    }
}
