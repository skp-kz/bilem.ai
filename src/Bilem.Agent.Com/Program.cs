using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Bilem.Agent.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Com
{
    /// <summary>
    /// Помощник COM к 1С. Читает ComJob (JSON) из stdin, подключается к базе через V83.COMConnector,
    /// выполняет операции, пишет ComReply (JSON) в stdout. Код возврата: 0 — все операции выполнены, 1 — ошибка.
    /// Отдельный процесс нужной разрядности: COM-коннектор 1С работает только в процессе той же разрядности, что платформа.
    /// </summary>
    internal static class Program
    {
        private static int Main()
        {
            var reply = new ComReply();
            try
            {
                string input;
                using (var r = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false))) input = r.ReadToEnd();
                var job = JsonConvert.DeserializeObject<ComJob>(input);
                Run(job, reply);
                reply.Ok = string.IsNullOrEmpty(reply.Error);
            }
            catch (Exception e)
            {
                reply.Ok = false;
                reply.Error = Clean(e);
            }
            using (var w = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)))
                w.Write(JsonConvert.SerializeObject(reply, ComReply.Js));
            // Ответ записан — выходим без выгрузки библиотек: comcntr.dll 8.3.24 при выгрузке на выходе процесса падал в ucrtbase
            // с 0xc0000409 (Server 2016, боевой сервер 19.10) — запись в журнале событий на каждый вызов, хотя работа уже сделана.
            TerminateProcess(GetCurrentProcess(), reply.Ok ? 0u : 1u);
            return reply.Ok ? 0 : 1;
        }

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        private static string Clean(Exception e)
        {
            while (e.InnerException != null && (e is System.Reflection.TargetInvocationException || e is AggregateException)) e = e.InnerException;
            return (e.Message ?? e.GetType().Name).Trim();
        }

        private static void Run(ComJob job, ComReply reply)
        {
            var type = Type.GetTypeFromProgID("V83.COMConnector");
            if (type == null) throw new InvalidOperationException("V83.COMConnector не зарегистрирован в этой разрядности.");
            dynamic connector = Activator.CreateInstance(type);
            dynamic c;
            try { c = connector.Connect(job.Connection); }
            catch (Exception e) { throw new InvalidOperationException("Не удалось подключиться: " + Clean(e)); }

            try
            {
                foreach (var op in job.Ops)
                {
                    try
                    {
                        reply.Results.Add(Do(c, op));
                    }
                    catch (Exception e)
                    {
                        reply.Error = $"{op.Op}: {Clean(e)}";
                        return;
                    }
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(c);
                Marshal.FinalReleaseComObject(connector);
            }
        }

        private static JObject Do(dynamic c, ComOp op)
        {
            switch (op.Op)
            {
                case "inspect": return Inspect(c);
                case "listRoles": return ListRoles(c);
                case "userInfo": return UserInfo(c, op);
                case "ensureUser": return EnsureUser(c, op);
                case "dropUser": return DropUser(c, op);
                case "ensureOData": return EnsureOData(c, op);
                case "dataFlags": return DataFlags(c, op);
                case "metaCatalog": return MetaCatalog(c, op);
                case "rightsMap": return RightsMap(c, op);
                case "setOData": return SetOData(c, op);
                case "orgAddresses": return OrgAddresses(c);
                case "describe": return Describe(c, op);
                case "orgSigners": return OrgSigners(c);
                default: throw new ArgumentException("неизвестная операция " + op.Op);
            }
        }

        private static JObject Inspect(dynamic c)
        {
            dynamic md = c.Metadata;
            return new JObject
            {
                ["op"] = "inspect",
                ["name"] = (string)md.Name,
                ["synonym"] = (string)md.Synonym,
                ["version"] = (string)md.Version,
                ["hasBsp"] = md.Catalogs.Find("Пользователи") != null,
            };
        }

        private static IEnumerable<dynamic> Items(dynamic collection)
        {
            int n = collection.Count();
            for (int i = 0; i < n; i++) yield return collection.Get(i);
        }

        private static JObject ListRoles(dynamic c)
        {
            var arr = new JArray();
            foreach (var r in Items(c.Metadata.Roles)) arr.Add(new JObject { ["name"] = (string)r.Name, ["synonym"] = (string)r.Synonym });
            return new JObject { ["op"] = "listRoles", ["roles"] = arr };
        }

        /// <summary>Роли пользователя ИБ — через Contains по всем ролям конфигурации (перечисление ролей пользователя по COM ненадёжно).</summary>
        private static List<string> RolesOf(dynamic c, dynamic user)
        {
            var have = new List<string>();
            dynamic ur = user.Roles;
            foreach (var r in Items(c.Metadata.Roles)) if ((bool)ur.Contains(r)) have.Add((string)r.Name);
            return have;
        }

        private static JObject UserInfo(dynamic c, ComOp op)
        {
            dynamic u = c.InfoBaseUsers.FindByName(op.User);
            var o = new JObject { ["op"] = "userInfo", ["exists"] = u != null };
            if (u != null) o["roles"] = new JArray(RolesOf(c, u));
            return o;
        }

        private static JObject EnsureUser(dynamic c, ComOp op)
        {
            dynamic md = c.Metadata;
            var wanted = new List<dynamic>();
            var missing = new List<string>();
            foreach (var name in op.Roles ?? new List<string>())
            {
                dynamic r = md.Roles.Find(name);
                if (r == null) missing.Add(name); else wanted.Add(r);
            }
            if (wanted.Count == 0) throw new InvalidOperationException("ни одной роли из списка нет в конфигурации");

            dynamic users = c.InfoBaseUsers;
            dynamic u = users.FindByName(op.User);
            var created = u == null;
            // Явный тип: вызов с dynamic-аргументами возвращает dynamic, а у dynamic нет методов-расширений LINQ (Except)
            List<string> before = created ? new List<string>() : (List<string>)RolesOf(c, u);
            List<string> wantNames = wanted.Select(r => (string)r.Name).ToList();
            var rolesDiffer = before.Count != wantNames.Count || before.Except(wantNames).Any();

            if (created) { u = users.CreateUser(); u.Name = op.User; }
            var needWrite = created || rolesDiffer || !string.IsNullOrEmpty(op.Password);
            if (needWrite)
            {
                u.FullName = op.FullName ?? op.User;
                u.StandardAuthentication = true;
                u.OSAuthentication = false;
                u.ShowInList = false;
                if (!string.IsNullOrEmpty(op.Password)) u.Password = op.Password;
                dynamic ur = u.Roles;
                ur.Clear();
                foreach (var r in wanted) ur.Add(r);
                u.Write();
            }

            // БСП: элемент справочника «Пользователи», связанный с пользователем ИБ (обмен данными — без проверок БСП)
            var bspLinked = false;
            if (md.Catalogs.Find("Пользователи") != null)
            {
                dynamic cat = c.Catalogs.Пользователи;
                dynamic item = cat.FindByDescription(op.FullName ?? op.User, true);
                dynamic obj = (item != null && !(bool)item.IsEmpty()) ? item.GetObject() : cat.CreateItem();
                obj.Description = op.FullName ?? op.User;
                obj.ИдентификаторПользователяИБ = u.UUID;
                obj.DeletionMark = false;
                obj.DataExchange.Load = true;
                obj.Write();
                bspLinked = true;
            }

            return new JObject
            {
                ["op"] = "ensureUser",
                ["created"] = created,
                ["written"] = needWrite,
                ["passwordSet"] = !string.IsNullOrEmpty(op.Password),
                ["rolesBefore"] = new JArray(before),
                ["rolesAfter"] = new JArray(RolesOf(c, users.FindByName(op.User))),
                ["missingRoles"] = new JArray(missing),
                ["bspLinked"] = bspLinked,
            };
        }

        private static JObject DropUser(dynamic c, ComOp op)
        {
            dynamic u = c.InfoBaseUsers.FindByName(op.User);
            var existed = u != null;
            if (existed) u.Delete();
            if (c.Metadata.Catalogs.Find("Пользователи") != null)
            {
                dynamic item = c.Catalogs.Пользователи.FindByDescription(op.FullName ?? op.User, true);
                if (item != null && !(bool)item.IsEmpty())
                {
                    dynamic obj = item.GetObject();
                    obj.DeletionMark = true;
                    obj.DataExchange.Load = true;
                    obj.Write();
                }
            }
            return new JObject { ["op"] = "dropUser", ["existed"] = existed };
        }

        private static JObject EnsureOData(dynamic c, ComOp op)
        {
            dynamic current = c.GetStandardODataInterfaceContent();
            var have = new HashSet<string>();
            var currentList = new List<dynamic>();
            foreach (var m in Items(current)) { currentList.Add(m); have.Add((string)m.FullName()); }
            var added = new List<string>(); var already = new List<string>(); var missing = new List<string>();
            var toAdd = new List<dynamic>();
            foreach (var fn in op.Objects ?? new List<string>())
            {
                dynamic mo = c.Metadata.FindByFullName(fn);
                if (mo == null) { missing.Add(fn); continue; }
                if (have.Contains((string)mo.FullName())) already.Add(fn); else { toAdd.Add(mo); added.Add(fn); }
            }
            var total = currentList.Count;
            if (toAdd.Count > 0)
            {
                dynamic arr = c.NewObject("Array");
                foreach (var m in currentList) arr.Add(m);
                foreach (var m in toAdd) arr.Add(m);
                c.SetStandardODataInterfaceContent(arr);
                total = (int)arr.Count();
            }
            return new JObject
            {
                ["op"] = "ensureOData",
                ["added"] = new JArray(added), ["already"] = new JArray(already), ["missing"] = new JArray(missing), ["total"] = total,
            };
        }

        /// <summary>
        /// Разделы и имена для вкладки «Доступ»: подсистемы верхнего уровня (как в левом меню 1С) и для каждого объекта —
        /// есть ли он, синоним и в каких разделах лежит (с учётом вложенных подсистем). Только метаданные, без данных.
        /// </summary>
        private static JObject MetaCatalog(dynamic c, ComOp op)
        {
            dynamic md = c.Metadata;
            var subs = new JArray();
            var where = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in Items(md.Subsystems))
            {
                bool inMenu = true;
                try { inMenu = (bool)s.IncludeInCommandInterface; } catch { }
                if (!inMenu) continue;
                var name = (string)s.Name;
                subs.Add(new JObject { ["name"] = name, ["synonym"] = (string)s.Synonym });
                Collect(s, name, where);
            }
            var objs = new JArray();
            foreach (var fn in op.Objects ?? new List<string>())
            {
                dynamic mo = md.FindByFullName(fn);
                var o = new JObject { ["fullName"] = fn, ["exists"] = mo != null };
                if (mo != null)
                {
                    o["synonym"] = (string)mo.Synonym;
                    o["sections"] = new JArray(where.TryGetValue((string)mo.FullName(), out var l) ? l : new List<string>());
                }
                objs.Add(o);
            }
            return new JObject { ["op"] = "metaCatalog", ["subsystems"] = subs, ["objects"] = objs };
        }

        private static void Collect(dynamic subsystem, string top, Dictionary<string, List<string>> where)
        {
            // «Состав» подсистемы — коллекция без Get(i): обходим перечислением COM
            foreach (dynamic m in (System.Collections.IEnumerable)subsystem.Content)
            {
                var fn = (string)m.FullName();
                if (!where.TryGetValue(fn, out var l)) { l = new List<string>(); where[fn] = l; }
                if (!l.Contains(top)) l.Add(top);
            }
            foreach (var s in Items(subsystem.Subsystems)) Collect(s, top, where);
        }

        /// <summary>
        /// Права ролей на объекты (ПравоДоступа): для каждого объекта — роли с чтением и роли с добавлением+изменением;
        /// для ролей-кандидатов — на скольких объектах из Scope у них есть запись (чтобы выбрать минимальную).
        /// </summary>
        private static JObject RightsMap(dynamic c, ComOp op)
        {
            dynamic md = c.Metadata;
            List<dynamic> roles = Enumerable.ToList<dynamic>(Items(md.Roles));
            var res = new JObject();
            var candidates = new HashSet<string>();
            foreach (var fn in op.Objects ?? new List<string>())
            {
                dynamic mo = md.FindByFullName(fn);
                if (mo == null) continue;
                var read = new JArray(); var write = new JArray();
                foreach (var r in roles)
                {
                    var rn = (string)r.Name;
                    if (Right(c, "Чтение", mo, r)) { read.Add(rn); candidates.Add(rn); }
                    if (Right(c, "Добавление", mo, r) && Right(c, "Изменение", mo, r)) { write.Add(rn); candidates.Add(rn); }
                }
                res[fn] = new JObject { ["read"] = read, ["write"] = write };
            }
            // Широта ролей-кандидатов: на сколько объектов области они дают запись
            var breadth = new JObject();
            var scope = op.Scope ?? new List<string>();
            foreach (var rn in candidates)
            {
                dynamic r = md.Roles.Find(rn);
                var n = 0;
                foreach (var fn in scope)
                {
                    dynamic mo = md.FindByFullName(fn);
                    if (mo != null && Right(c, "Добавление", mo, r)) n++;
                }
                breadth[rn] = n;
            }
            return new JObject { ["op"] = "rightsMap", ["objects"] = res, ["breadth"] = breadth };
        }

        private static bool Right(dynamic c, string right, dynamic mo, dynamic role)
        {
            try { return (bool)c.AccessRight(right, mo, role); }
            catch { return false; }
        }

        /// <summary>
        /// Юридические адреса организаций — для шапки счёта (в составе OData контактной информации нет: там и данные физлиц).
        /// Только организации, только адрес; ключ — Ref_Key организации.
        /// </summary>
        /// <summary>Описание объектов метаданных (измерения, ресурсы, реквизиты, типы) — для диагностики; данных не читает.</summary>
        private static JObject Describe(dynamic c, ComOp op)
        {
            var o = new JObject();
            foreach (var fn in op.Objects ?? new List<string>())
            {
                // «~шаблон» — имена регистров сведений и справочников, подходящие под регулярное выражение
                if (fn.StartsWith("~"))
                {
                    var rx = new System.Text.RegularExpressions.Regex(fn.Substring(1));
                    var names = new JArray();
                    foreach (var x in Items(c.Metadata.InformationRegisters)) if (rx.IsMatch((string)x.Name)) names.Add("РегистрСведений." + (string)x.Name);
                    foreach (var x in Items(c.Metadata.Catalogs)) if (rx.IsMatch((string)x.Name)) names.Add("Справочник." + (string)x.Name);
                    foreach (var x in Items(c.Metadata.Enums)) if (rx.IsMatch((string)x.Name)) names.Add("Перечисление." + (string)x.Name);
                    o[fn] = names;
                    continue;
                }
                dynamic mo = c.Metadata.FindByFullName(fn);
                if (mo == null) { o[fn] = null; continue; }
                var d = new JObject();
                foreach (var part in new[] { "Dimensions", "Resources", "Attributes" })
                {
                    dynamic coll;
                    try { coll = mo.GetType().InvokeMember(part, System.Reflection.BindingFlags.GetProperty, null, mo, null); } catch { continue; }
                    var arr = new JArray();
                    foreach (var x in Items(coll)) arr.Add((string)x.Name + ": " + (string)c.String(x.Type));
                    d[part] = arr;
                }
                o[fn] = d;
            }
            return new JObject { ["op"] = "describe", ["objects"] = o };
        }

        /// <summary>
        /// Руководитель и главный бухгалтер организаций — «Ответственные лица организаций» (срез последних): ФИО и должность
        /// для подписей печатных форм. Только чтение, вход администратора.
        /// </summary>
        private static JObject OrgSigners(dynamic c)
        {
            const string fields = "О.СтруктурнаяЕдиница КАК Орг, О.ОтветственноеЛицо КАК Кто, О.ФизическоеЛицо.Наименование КАК ФИО, "
                + "ВЫРАЗИТЬ(О.Должность.Наименование КАК СТРОКА(150)) КАК Должность";
            var o = new JObject();
            if (c.Metadata.FindByFullName("РегистрСведений.ОтветственныеЛицаОрганизаций") == null) return new JObject { ["op"] = "orgSigners", ["signers"] = o };
            dynamic sel;
            try
            {
                dynamic q = c.NewObject("Query");
                q.Text = "ВЫБРАТЬ " + fields + " ИЗ РегистрСведений.ОтветственныеЛицаОрганизаций.СрезПоследних КАК О ГДЕ О.СтруктурнаяЕдиница ССЫЛКА Справочник.Организации";
                sel = q.Execute().Select();
            }
            catch
            {
                // Регистр непериодический — без среза
                dynamic q = c.NewObject("Query");
                q.Text = "ВЫБРАТЬ " + fields + " ИЗ РегистрСведений.ОтветственныеЛицаОрганизаций КАК О ГДЕ О.СтруктурнаяЕдиница ССЫЛКА Справочник.Организации";
                sel = q.Execute().Select();
            }
            var rows = 0; var kinds = new JArray();
            while ((bool)sel.Next())
            {
                rows++;
                var org = (string)c.XMLString(sel.Орг);
                var who = (string)c.XMLString(sel.Кто);
                if (!kinds.Any(k => (string)k == who)) kinds.Add(who);
                var key = who == "Руководитель" ? "director" : who == "ГлавныйБухгалтер" ? "chief_accountant" : null;
                if (key == null) continue;
                var fio = ((string)sel.ФИО ?? "").Trim();
                if (fio.Length == 0) continue;
                if (!(o[org] is JObject e)) o[org] = e = new JObject();
                e[key] = new JObject { ["name"] = fio, ["title"] = ((string)sel.Должность ?? "").Trim() };
            }
            // Для журнала: сколько записей в регистре всего (ответственные лица могут быть заданы на подразделения, а не на организацию)
            dynamic qc = c.NewObject("Query");
            qc.Text = "ВЫБРАТЬ КОЛИЧЕСТВО(*) КАК Всего ИЗ РегистрСведений.ОтветственныеЛицаОрганизаций";
            dynamic sc = qc.Execute().Select();
            var total = (bool)sc.Next() ? (int)sc.Всего : 0;
            return new JObject { ["op"] = "orgSigners", ["signers"] = o, ["rows"] = rows, ["total"] = total, ["kinds"] = kinds };
        }

        private static JObject OrgAddresses(dynamic c)
        {
            dynamic q = c.NewObject("Query");
            q.Text = "ВЫБРАТЬ КИ.Объект КАК Объект, КИ.Представление КАК Представление, КИ.Вид.Наименование КАК ВидИмя "
                   + "ИЗ РегистрСведений.КонтактнаяИнформация КАК КИ "
                   + "ГДЕ КИ.Объект ССЫЛКА Справочник.Организации И КИ.Тип = ЗНАЧЕНИЕ(Перечисление.ТипыКонтактнойИнформации.Адрес)";
            dynamic sel = q.Execute().Select();
            var best = new Dictionary<string, (int Rank, string Text)>();
            while ((bool)sel.Next())
            {
                var key = (string)c.XMLString(sel.Объект);
                var text = ((string)sel.Представление ?? "").Trim();
                if (text.Length == 0) continue;
                var kind = ((string)sel.ВидИмя ?? "").ToLowerInvariant();
                var rank = kind.Contains("юрид") ? 0 : kind.Contains("факт") ? 1 : 2;
                if (!best.TryGetValue(key, out var cur) || rank < cur.Rank) best[key] = (rank, text);
            }
            var o = new JObject();
            foreach (var kv in best) o[kv.Key] = kv.Value.Text;
            return new JObject { ["op"] = "orgAddresses", ["addresses"] = o };
        }

        /// <summary>Состав OData — ровно этот список (объектов, которых нет в конфигурации, пропускаем).</summary>
        private static JObject SetOData(dynamic c, ComOp op)
        {
            dynamic arr = c.NewObject("Array");
            var set = new List<string>(); var missing = new List<string>();
            foreach (var fn in op.Objects ?? new List<string>())
            {
                dynamic mo = c.Metadata.FindByFullName(fn);
                if (mo == null) { missing.Add(fn); continue; }
                arr.Add(mo); set.Add(fn);
            }
            c.SetStandardODataInterfaceContent(arr);
            return new JObject { ["op"] = "setOData", ["set"] = new JArray(set), ["missing"] = new JArray(missing), ["total"] = set.Count };
        }

        /// <summary>Есть ли записи в каждом объекте (только признак). Псевдоним — не зарезервированное слово (known-issues 8).</summary>
        private static JObject DataFlags(dynamic c, ComOp op)
        {
            var flags = new JObject();
            var errors = new JObject();
            foreach (var fn in op.Objects ?? new List<string>())
            {
                if (c.Metadata.FindByFullName(fn) == null) continue;
                try
                {
                    dynamic q = c.NewObject("Query");
                    q.Text = $"ВЫБРАТЬ ПЕРВЫЕ 1 ИСТИНА КАК Найдено ИЗ {fn} КАК Т";
                    flags[fn] = !(bool)q.Execute().IsEmpty();
                }
                catch (Exception e)
                {
                    flags[fn] = JValue.CreateNull();
                    errors[fn] = Clean(e);
                }
            }
            return new JObject { ["op"] = "dataFlags", ["flags"] = flags, ["errors"] = errors };
        }
    }
}
