using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core
{
    public sealed class SetupOptions
    {
        public string BasePath { get; set; }
        public string AdminUser { get; set; }
        public string AdminPassword { get; set; }
        public string Slug { get; set; }
        public bool EnableIis { get; set; }
        public bool ResetPassword { get; set; }
        public string PlatformVersion { get; set; }
        /// <summary>Человек ответил «да» на предупреждение «это контроллер домена» (IIS на DC Microsoft не рекомендует).</summary>
        public bool AllowDomainController { get; set; }
        /// <summary>Человек разрешил сменить системную регистрацию COM-коннектора с другой платформы на выбранную.</summary>
        public bool AllowComRegister { get; set; }
    }

    public enum StepStatus { Pending, Running, Ok, Warn, Fail, Skipped }

    public sealed class Step
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public StepStatus Status { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>
    /// Подключение базы 1С: перенос setup-spike.ps1 без потерь (находки known-issues 1–10 — см. журнал задачи).
    /// Каждый шаг: проверить → сделать → перепроверить. Повторный запуск ничего не ломает и продолжает с упавшего шага.
    /// </summary>
    public sealed class Onboarding
    {
        public const string BilemUser = "bilem";
        public const string BilemFullName = "Bilem (ИИ-помощник)";
        public static readonly Regex SlugRx = new Regex("^[a-z0-9_]{1,40}$");

        public List<Step> Steps { get; } = new List<Step>();
        public event Action<Step> StepChanged;

        private Step _cur;

        private void Begin(string id)
        {
            _cur = new Step { Id = id, Title = Txt.S("Step_" + id), Status = StepStatus.Running };
            Steps.Add(_cur);
            Log.Info(Txt.F("StepHeader", Steps.Count, _cur.Title));
            StepChanged?.Invoke(_cur);
        }

        private void End(StepStatus st, string detail = null)
        {
            _cur.Status = st;
            _cur.Detail = detail;
            StepChanged?.Invoke(_cur);
            SaveState();
        }

        private string _stateSlug;
        private void SaveState()
        {
            if (_stateSlug == null) return;
            try
            {
                Paths.Ensure();
                var s = new JObject { ["slug"] = _stateSlug, ["time"] = DateTime.Now, ["steps"] = JArray.FromObject(Steps.Select(x => new { x.Id, Status = x.Status.ToString() })) };
                File.WriteAllText(Paths.StateFile(_stateSlug), s.ToString(Formatting.Indented));
            }
            catch { }
        }

        public static bool IsAdmin()
        {
            using (var id = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        // ---------------- Диагностика ----------------

        /// <summary>Диагностика и план, ничего не меняет.</summary>
        public bool Diag(SetupOptions o) => Run(o, apply: false);

        /// <summary>Подключить базу: пользователь bilem, состав OData, публикация только на 127.0.0.1, проверки.</summary>
        public bool Setup(SetupOptions o) => Run(o, apply: true);

        private bool Run(SetupOptions o, bool apply)
        {
            try
            {
                Log.Info(Txt.F(apply ? "ModeApply" : "ModeDiag", Environment.OSVersion.Version, Environment.Is64BitProcess ? 64 : 32));

                // 0. Права администратора Windows
                Begin("admin");
                if (!IsAdmin()) { End(StepStatus.Fail); return Fail(Txt.S("NeedAdmin"), Txt.S("HintRunAsAdmin")); }
                End(StepStatus.Ok);

                // 1. База
                Begin("base");
                if (string.IsNullOrEmpty(o.BasePath) || !File.Exists(Path.Combine(o.BasePath, "1Cv8.1CD")))
                { End(StepStatus.Fail); return Fail(Txt.F("NoBaseFile", o.BasePath), Txt.S("HintBaseFolder")); }
                var basePath = Path.GetFullPath(o.BasePath).TrimEnd('\\');
                var slug = string.IsNullOrEmpty(o.Slug) ? BaseDiscovery.SlugFromPath(basePath) : o.Slug.ToLowerInvariant();
                if (!SlugRx.IsMatch(slug)) { End(StepStatus.Fail); return Fail(Txt.F("BadSlug", slug), Txt.S("HintSlug")); }
                _stateSlug = slug;
                var cfg = AgentConfig.Load();
                var existing = cfg.Find(slug);
                if (existing != null && !string.Equals(existing.Path.TrimEnd('\\'), basePath, StringComparison.OrdinalIgnoreCase))
                { End(StepStatus.Fail); return Fail(Txt.F("SlugTaken", slug, existing.Path), Txt.S("HintSlugOther")); }
                var pub = "bilem_" + slug;
                var pool = "Bilem_" + slug;
                ReportPrevious(slug);
                Log.Ok(Txt.F("BaseFound", basePath, slug, pub, pool));
                End(StepStatus.Ok, slug);

                // 2. Платформа и COM-коннектор
                Begin("platform");
                var found = Platform.Find();
                if (found.Count == 0) { End(StepStatus.Fail); return Fail(Txt.S("NoPlatform"), Txt.S("HintInstallPlatform")); }
                foreach (var f in found) Log.Info(Txt.F("PlatformFound", f));
                var plat = Platform.Choose(found, o.PlatformVersion);
                if (plat == null) { End(StepStatus.Fail); return Fail(Txt.F("PlatformNotFound", o.PlatformVersion), Txt.S("HintInstallPlatform")); }
                Log.Ok(Txt.F("PlatformChosen", plat, plat.Bin));
                var com = Platform.ComProblem(plat);
                if (com.Length > 0)
                {
                    // Зарегистрирован коннектор другой платформы — им могут пользоваться обмены и обработки: только с «да» человека
                    var inUse = Platform.ComInUse(plat);
                    if (!apply) { Log.Plan(Txt.F("PlanRegisterCom", com, plat.ComDll)); if (inUse != null) Log.Plan(Txt.F("ComAsk", inUse, plat.Version)); End(StepStatus.Warn, com); return PlanDone(); }
                    if (inUse != null && !o.AllowComRegister) { End(StepStatus.Fail); return Fail(Txt.F("ComAsk", inUse, plat.Version), Txt.S("HintComAsk")); }
                    Log.Warn(Txt.F("RegisteringCom", com));
                    Platform.RememberCom(cfg, plat);
                    cfg.Save();
                    Platform.RegisterCom(plat);
                    com = Platform.ComProblem(plat);
                    if (com.Length > 0) { End(StepStatus.Fail); return Fail(Txt.F("ComRegisterFailed", com), Txt.F("HintRegsvr", plat.ComDll)); }
                    if (inUse != null) Log.Ok(Txt.F("ComReregistered", inUse, plat.ComDll));
                }
                Log.Ok(Txt.S("ComOk"));
                End(StepStatus.Ok, plat.ToString());

                // 3. Вход в базу и конфигурация
                Begin("connect");
                var client = new ComClient(plat);
                var conn = ComClient.FileConnection(basePath, o.AdminUser, o.AdminPassword);
                var r1 = client.Run(conn, new ComOp { Op = "inspect" }, new ComOp { Op = "userInfo", User = BilemUser });
                var info = r1[0];
                var userInfo = r1[1];
                var cfgName = (string)info["name"];
                var profile = Profiles.For(cfgName);
                Log.Ok(Txt.F("ConfigInfo", (string)info["synonym"], cfgName, (string)info["version"]));
                Log.Info(Txt.F("BspInfo", (bool)info["hasBsp"] ? Txt.S("Yes") : Txt.S("No")));
                if (!profile.Tested) Log.Warn(Txt.F("ProfileUntested", cfgName));
                End(StepStatus.Ok, (string)info["version"]);

                var b = existing ?? new BaseConfig();
                b.Slug = slug; b.Path = basePath; b.Configuration = cfgName; b.Synonym = (string)info["synonym"]; b.Version = (string)info["version"];
                b.HasBsp = (bool)info["hasBsp"]; b.PlatformVersion = plat.Version.ToString(); b.PlatformBits = plat.Bits;
                b.Publication = pub; b.Pool = pool; b.OData = $"http://127.0.0.1/{pub}/odata/standard.odata"; b.User = BilemUser;
                b.SecretFile = Paths.SecretFile(slug);

                // 4. Пользователь bilem, 5. Состав OData — одним подключением
                Begin("user");
                var userExists = (bool)userInfo["exists"];
                var haveSecret = File.Exists(b.SecretFile);
                if (userExists)
                {
                    var have = ((JArray)userInfo["roles"]).Select(x => (string)x).ToList();
                    // Роли, добавленные «Доступом» и для служебных объектов, — не «лишние»
                    var expected = profile.Roles.Concat(b.ExtraRoles ?? new List<string>()).ToList();
                    var missing = expected.Except(have).ToList();
                    var extra = have.Except(expected).ToList();
                    Log.Write(missing.Count + extra.Count == 0 ? LogLevel.Ok : LogLevel.Warn, Txt.F("UserRolesNow", BilemUser, have.Count, string.Join(", ", have)));
                    if (missing.Count > 0) Log.Warn(Txt.F("UserRolesMissing", string.Join(", ", missing)));
                    if (extra.Count > 0) Log.Warn(Txt.F("UserRolesExtra", string.Join(", ", extra)));
                }
                var newPassword = !userExists || !haveSecret || o.ResetPassword;
                if (!apply)
                {
                    Log.Plan(Txt.F(userExists ? "PlanUserUpdate" : "PlanUserCreate", BilemUser, string.Join(", ", profile.Roles)));
                    if (newPassword) Log.Plan(Txt.F("PlanUserPassword", b.SecretFile));
                    if (b.HasBsp) Log.Plan(Txt.S("PlanBspLink"));
                    End(StepStatus.Skipped);
                    Begin("odata");
                    Log.Plan(Txt.F("PlanOData", profile.ODataObjects.Length));
                    End(StepStatus.Skipped);
                }
                else
                {
                    string pwd = null, pwdProtected = null;
                    if (newPassword)
                    {
                        pwd = Secrets.RandomPassword();
                        // Шифруем до записи пользователя: если DPAPI упадёт, пароль bilem в базе не сменится впустую (known-issues 5)
                        pwdProtected = Secrets.Protect(pwd);
                    }
                    // Повторная настройка не сбрасывает «Доступ»: роли записи и выбранный состав чтения сохраняются
                    var wantRoles = profile.Roles.Concat(b.ExtraRoles ?? new List<string>()).Distinct().ToList();
                    // Служебные объекты (e-mail контрагента, данные для доверенности) — роль чтения подбираем, если ролей профиля мало
                    var serviceRoles = ServiceRoles(client, conn, profile, wantRoles);
                    wantRoles = wantRoles.Concat(serviceRoles).Distinct().ToList();
                    b.ExtraRoles = (b.ExtraRoles ?? new List<string>()).Concat(serviceRoles).Distinct().ToList();
                    var wantOData = (b.Read ?? profile.ODataObjects.ToList()).Concat(Profiles.ServiceODataObjects).Distinct().ToList();
                    var r2 = client.Run(conn,
                        new ComOp { Op = "ensureUser", User = BilemUser, FullName = BilemFullName, Password = pwd, Roles = wantRoles },
                        b.Read != null ? new ComOp { Op = "setOData", Objects = wantOData } : new ComOp { Op = "ensureOData", Objects = wantOData },
                        new ComOp { Op = "dataFlags", Objects = wantOData },
                        new ComOp { Op = "metaCatalog", Objects = profile.CatalogObjects.ToList() },
                        new ComOp { Op = "orgAddresses" },
                        new ComOp { Op = "orgSigners" });
                    pwd = null;
                    if (pwdProtected != null)
                    {
                        Directory.CreateDirectory(Paths.SecretsDir);
                        File.WriteAllText(b.SecretFile, pwdProtected);
                        Secrets.RestrictToAdmins(b.SecretFile);
                    }
                    var u = r2[0];
                    foreach (var m in (JArray)u["missingRoles"]) Log.Warn(Txt.F("RoleNotInConfig", (string)m));
                    b.Roles = ((JArray)u["rolesAfter"]).Select(x => (string)x).ToList();
                    Log.Ok(Txt.F((bool)u["created"] ? "UserCreated" : ((bool)u["written"] ? "UserUpdated" : "UserUnchanged"), BilemUser, string.Join(", ", b.Roles)));
                    if (pwdProtected != null) Log.Ok(Txt.F("UserPasswordSaved", b.SecretFile));
                    if ((bool)u["bspLinked"]) Log.Ok(Txt.S("BspLinked"));
                    End(StepStatus.Ok, string.Join(", ", b.Roles));

                    Begin("odata");
                    var od = r2[1];
                    foreach (var m in (JArray)od["missing"]) Log.Info(Txt.F("ODataNotInConfig", (string)m));
                    if (b.Read != null)
                    {
                        var keepRead = b.Read;
                        b.ODataObjects = ((JArray)od["set"]).Select(x => (string)x).ToList();
                        b.Read = b.ODataObjects.Where(x => !Profiles.IsService(x) || keepRead.Contains(x)).ToList();
                        Log.Ok(Txt.F("AccessODataKept", b.ODataObjects.Count));
                    }
                    else
                    {
                        var present = ((JArray)od["added"]).Concat((JArray)od["already"]).Select(x => (string)x).ToList();
                        b.ODataObjects = profile.ODataObjects.Concat(Profiles.ServiceODataObjects).Where(present.Contains).ToList();
                        Log.Ok(Txt.F("ODataComposition", ((JArray)od["added"]).Count, ((JArray)od["already"]).Count, (int)od["total"]));
                    }
                    b.Catalog = Access.FromMeta(profile, r2[3]);
                    // Адрес поставщика для счёта — сразу при подключении: новая база печатается с адресом из 1С
                    b.Print = b.Print ?? new Print.PrintSettings();
                    b.Print.Addresses = ((JObject)r2[4]["addresses"]).Properties().ToDictionary(p => p.Name, p => (string)p.Value);
                    b.Print.Signers = Print.OrgSigner.Parse((JObject)r2[5]["signers"]);
                    Log.Ok(Txt.F("PrintDetailsLoaded", b.Print.Addresses.Count));
                    Log.Ok(Txt.F("AccessCatalogLoaded", b.Catalog.Sections.Count, b.Catalog.Items.Count));
                    var flags = (JObject)r2[2]["flags"];
                    foreach (var e in (JObject)r2[2]["errors"]) Log.Warn(Txt.F("DataFlagError", e.Key, (string)e.Value));
                    b.NonEmpty = flags.Properties().Where(p => p.Value.Type == JTokenType.Boolean && (bool)p.Value).Select(p => p.Name).ToList();
                    Log.Info(Txt.F("DataFlags", b.NonEmpty.Count, flags.Count));
                    End(StepStatus.Ok, b.ODataObjects.Count.ToString());
                }

                // 6. IIS
                Begin("iis");
                var dc = WinInfo.IsDomainController();
                if (dc) Log.Warn(Txt.S("DcWarning"));
                if (!Iis.Installed)
                {
                    if (dc && apply && o.EnableIis && !o.AllowDomainController) { End(StepStatus.Fail); return Fail(Txt.S("DcNoIis"), Txt.S("HintDc")); }
                    if (!apply || !o.EnableIis)
                    {
                        Log.Plan(Txt.S("PlanEnableIis"));
                        if (apply) { End(StepStatus.Fail); return Fail(Txt.S("NoIis"), Txt.S("HintEnableIis")); }
                    }
                    else
                    {
                        var d = Iis.EnableFeatures(Iis.WebServerFeatures);
                        if (d.ExitCode != 0 && d.ExitCode != 3010) { End(StepStatus.Fail); return Fail(Txt.F("DismFailed", d.ExitCode, d.Combined), Txt.S("HintIisManual")); }
                        if (d.ExitCode == 3010) Log.Warn(Txt.S("RebootRequired"));
                        if (!Iis.Installed) { End(StepStatus.Fail); return Fail(Txt.S("NoIis"), Txt.S("HintIisManual")); }
                        Log.Ok(Txt.S("IisEnabled"));
                    }
                }
                else Log.Ok(Txt.S("IisInstalled"));
                if (Iis.Installed && !Iis.IpSecurityInstalled)
                {
                    if (!apply) Log.Plan(Txt.S("PlanIpSecurity"));
                    else if (o.EnableIis)
                    {
                        var d = Iis.EnableFeatures("IIS-IPSecurity");
                        if (d.ExitCode != 0 && d.ExitCode != 3010) { End(StepStatus.Fail); return Fail(Txt.F("DismFailed", d.ExitCode, d.Combined), Txt.S("HintIisManual")); }
                        Log.Ok(Txt.S("IpSecurityEnabled"));
                    }
                    // Без «Ограничений по IP» публикация была бы открыта наружу — не публикуем
                    else { End(StepStatus.Fail); return Fail(Txt.S("NoIpSecurity"), Txt.S("HintEnableIis")); }
                }
                End(apply || Iis.Installed ? StepStatus.Ok : StepStatus.Skipped);

                // 7. Публикация (только 127.0.0.1), свой пул на базу
                Begin("publish");
                var dir = Path.Combine(Iis.WwwRoot, pub);
                var vrd = Path.Combine(dir, "default.vrd");
                if (!apply)
                {
                    Log.Plan(Txt.F("PlanPublish", pub, dir, pool, plat.Bits));
                    End(StepStatus.Skipped);
                    return PlanDone();
                }
                if (!Iis.AppExists(pub) || !File.Exists(vrd))
                {
                    var w = Iis.Publish(plat, pub, dir, basePath);
                    Log.Info(Txt.F("WebinstResult", w.ExitCode, w.Combined));
                    if (!File.Exists(vrd)) { End(StepStatus.Fail); return Fail(Txt.S("WebinstNoVrd"), Txt.S("HintWebinst")); }
                }
                else Log.Ok(Txt.F("PublicationExists", pub));
                if (!Iis.ODataEnabled(vrd)) Iis.EnableOData(vrd);
                Iis.EnableOData(vrd); // пул сессий — всегда в нужном виде
                Log.Ok(Txt.S("VrdOData"));
                Iis.EnsurePool(pool, plat.Bits);
                if (!string.Equals(Iis.AppPoolOf(pub), pool, StringComparison.OrdinalIgnoreCase)) Iis.SetAppPool(pub, pool);
                Log.Ok(Txt.F("PoolReady", pool, plat.Bits));
                if (!Iis.AnonymousIsPool(pub)) Iis.AnonymousAsPool(pub);
                Log.Ok(Txt.F("AnonymousAsPool", pool));
                Iis.GrantPool(basePath, pool);
                Log.Ok(Txt.F("FolderRights", basePath, pool));
                if (!Iis.LocalhostOnly(pub)) Iis.RestrictToLocalhost(pub);
                if (!Iis.LocalhostOnly(pub)) { End(StepStatus.Fail); return Fail(Txt.S("LocalhostOnlyFailed"), Txt.S("HintIisManual")); }
                Log.Ok(Txt.S("LocalhostOnly"));
                Iis.Recycle(pool);
                b.SetupAt = DateTime.Now;
                cfg.Upsert(b);
                cfg.Save();
                End(StepStatus.Ok, pub);

                // 8. Проверки
                return VerifyStep(cfg, b);
            }
            catch (AgentException e)
            {
                if (_cur != null && _cur.Status == StepStatus.Running) End(StepStatus.Fail, e.Message);
                return Fail(e.Message, e.Hint);
            }
            catch (Exception e)
            {
                if (_cur != null && _cur.Status == StepStatus.Running) End(StepStatus.Fail, e.Message);
                return Fail(Txt.F("Unexpected", e.GetType().Name, e.Message), Txt.S("HintSendLog"));
            }
        }

        private bool VerifyStep(AgentConfig cfg, BaseConfig b)
        {
            Begin("verify");
            Health.Resume(b.Slug);
            var pwd = File.Exists(b.SecretFile) ? Secrets.ReadSecretFile(b.SecretFile) : null;
            if (pwd == null) { End(StepStatus.Fail); return Fail(Txt.F("NoSecret", b.SecretFile), Txt.S("HintResetPassword")); }
            var vr = Verifier.Run(b, pwd);
            var now = DateTime.Now;
            b.VerifiedAt = now;
            b.LastCheckOk = vr.Ok;
            if (vr.WriteDenied) b.WriteDeniedAt = now;
            cfg.Upsert(b);
            cfg.Save();
            if (!vr.Ok) { End(StepStatus.Fail, Txt.F("ChecksFailed", vr.Failures)); return Fail(Txt.F("ChecksFailed", vr.Failures), Txt.S("HintSeeFail")); }
            End(StepStatus.Ok);
            var writes = b.WriteInOneC?.Count ?? 0;
            Log.Ok(writes > 0 ? Txt.F("DoneWithWrite", b.Slug, b.OData, writes) : Txt.F("Done", b.Slug, b.OData));
            return true;
        }

        // ---------------- Проверка ----------------

        /// <summary>Проверки уже подключённой базы. С логином администратора — ещё и роли bilem по COM.</summary>
        public bool Verify(string slug, string adminUser = null, string adminPassword = null)
        {
            try
            {
                var cfg = AgentConfig.Load();
                var bases = string.IsNullOrEmpty(slug) ? cfg.Bases.ToList() : new[] { cfg.Find(slug) }.Where(x => x != null).ToList();
                if (bases.Count == 0) return Fail(string.IsNullOrEmpty(slug) ? Txt.S("NoBases") : Txt.F("NoSuchBase", slug), Txt.S("HintSetupFirst"));
                var ok = true;
                foreach (var b in bases)
                {
                    _stateSlug = null;
                    Log.Info(Txt.F("VerifyBase", b.Slug, b.DisplayName));
                    if (!string.IsNullOrEmpty(adminUser))
                    {
                        Begin("roles");
                        var plat = Platform.Choose(Platform.Find(), b.PlatformVersion) ?? Platform.Choose(Platform.Find());
                        var ui = new ComClient(plat).Run(ComClient.FileConnection(b.Path, adminUser, adminPassword), new ComOp { Op = "userInfo", User = BilemUser })[0];
                        var have = (bool)ui["exists"] ? ((JArray)ui["roles"]).Select(x => (string)x).ToList() : new List<string>();
                        var want = ExpectedRoles(b);
                        var same = have.Count == want.Count && !want.Except(have).Any();
                        Log.Write(same ? LogLevel.Ok : LogLevel.Fail, Txt.F("UserRolesNow", BilemUser, have.Count, string.Join(", ", have)));
                        End(same ? StepStatus.Ok : StepStatus.Fail);
                        if (!same) ok = false;
                    }
                    ok &= VerifyStep(cfg, b);
                }
                return ok;
            }
            catch (AgentException e) { return Fail(e.Message, e.Hint); }
            catch (Exception e) { return Fail(Txt.F("Unexpected", e.GetType().Name, e.Message), Txt.S("HintSendLog")); }
        }

        // ---------------- Доступ ----------------

        /// <summary>
        /// «Доступ» → «Применить», слой 1С: разделы из подсистем, роли bilem (подбор по ПравоДоступа), состав OData — ровно выбранное
        /// чтение, затем автоматическая проверка. Слой MCP (Read/Write) уже сохранён окном. Проведения и удаления в коде нет.
        /// </summary>
        public bool ApplyAccess(string slug, string adminUser, string adminPassword, ICollection<string> read, ICollection<string> write)
        {
            try
            {
                _stateSlug = null;
                var cfg = AgentConfig.Load();
                var b = cfg.Find(slug);
                if (b == null) return Fail(Txt.F("NoSuchBase", slug), Txt.S("HintList"));
                var profile = Profiles.For(b.Configuration);

                Begin("access_connect");
                var plat = Platform.Choose(Platform.Find(), b.PlatformVersion) ?? Platform.Choose(Platform.Find());
                if (plat == null) { End(StepStatus.Fail); return Fail(Txt.S("NoPlatform"), Txt.S("HintInstallPlatform")); }
                var client = new ComClient(plat);
                var conn = ComClient.FileConnection(b.Path, adminUser, adminPassword);
                // Объекты состава профиля читают роли профиля; роль чтения подбираем только для остальных
                var needRead = read.Where(x => !profile.ODataObjects.Contains(x)).ToList();
                var needService = Profiles.ServiceODataObjects.Where(x => !profile.ODataObjects.Contains(x) && !needRead.Contains(x)).ToList();
                var r1 = client.Run(conn,
                    new ComOp { Op = "metaCatalog", Objects = profile.CatalogObjects.ToList() },
                    new ComOp { Op = "rightsMap", Objects = needRead.Concat(needService).Concat(write).Distinct().ToList(), Scope = profile.CatalogObjects.ToList() },
                    new ComOp { Op = "listRoles" });
                b.Catalog = Access.FromMeta(profile, r1[0]);
                Log.Ok(Txt.F("AccessCatalogLoaded", b.Catalog.Sections.Count, b.Catalog.Items.Count));
                End(StepStatus.Ok, Txt.F("AccessCatalogShort", b.Catalog.Sections.Count));

                Begin("access_roles");
                var allRoles = new HashSet<string>(((JArray)r1[2]["roles"]).Select(x => (string)x["name"]));
                var baseRoles = profile.Roles.Where(allRoles.Contains).ToList();
                var plan = Access.PlanRoles(profile, baseRoles, needRead.Concat(needService), write, r1[1]);
                // Служебного объекта нет в конфигурации или роли на него нет — не ошибка «Доступа», агент без него обойдётся
                foreach (var s in plan.NoRole.Where(needService.Contains).ToList()) { plan.NoRole.Remove(s); Log.Info(Txt.F("ServiceNoRole", s)); }
                if (plan.NoRole.Count > 0)
                {
                    End(StepStatus.Fail);
                    return Fail(Txt.F("AccessNoRole", string.Join(", ", plan.NoRole.Select(b.Catalog.TitleOf))), Txt.S("HintAccessNoRole"));
                }
                foreach (var kv in plan.Why) Log.Info(Txt.F("AccessRoleFor", kv.Value, b.Catalog.TitleOf(kv.Key)));
                var before = b.Roles ?? new List<string>();
                var added = plan.Roles.Except(before).ToList();
                var removed = before.Except(plan.Roles).ToList();
                var u = client.Run(conn, new ComOp { Op = "ensureUser", User = BilemUser, FullName = BilemFullName, Roles = plan.Roles })[0];
                b.Roles = ((JArray)u["rolesAfter"]).Select(x => (string)x).ToList();
                b.ExtraRoles = plan.Extra.Distinct().ToList();
                if (added.Count > 0) Log.Ok(Txt.F("AccessRolesAdded", string.Join(", ", added)));
                if (removed.Count > 0) Log.Ok(Txt.F("AccessRolesRemoved", string.Join(", ", removed)));
                if (added.Count + removed.Count == 0) Log.Ok(Txt.S("AccessRolesSame"));
                End(StepStatus.Ok, string.Join(", ", b.Roles));

                Begin("access_odata");
                var r2 = client.Run(conn,
                    new ComOp { Op = "setOData", Objects = read.Concat(Profiles.ServiceODataObjects).Distinct().ToList() },
                    new ComOp { Op = "dataFlags", Objects = read.ToList() });
                var set = ((JArray)r2[0]["set"]).Select(x => (string)x).ToList();
                var wasOData = b.ODataObjects ?? new List<string>();
                b.ODataObjects = set;
                // Служебный объект, включённый на «Доступе» (Физические лица), — остаётся в чтении ИИ
                b.Read = set.Where(x => !Profiles.IsService(x) || read.Contains(x)).ToList();
                b.Write = write.Where(set.Contains).ToList();
                b.WriteInOneC = b.Write.ToList();
                var flags = (JObject)r2[1]["flags"];
                b.NonEmpty = flags.Properties().Where(p => p.Value.Type == JTokenType.Boolean && (bool)p.Value).Select(p => p.Name).ToList();
                Log.Ok(Txt.F("AccessODataSet", set.Count, set.Except(wasOData).Count(), wasOData.Except(set).Count()));
                cfg.Upsert(b);
                cfg.Save();
                End(StepStatus.Ok, set.Count.ToString());

                return VerifyStep(cfg, b);
            }
            catch (AgentException e)
            {
                if (_cur != null && _cur.Status == StepStatus.Running) End(StepStatus.Fail, e.Message);
                return Fail(e.Message, e.Hint);
            }
            catch (Exception e)
            {
                if (_cur != null && _cur.Status == StepStatus.Running) End(StepStatus.Fail, e.Message);
                return Fail(Txt.F("Unexpected", e.GetType().Name, e.Message), Txt.S("HintSendLog"));
            }
        }

        /// <summary>Только разделы из подсистем 1С (без изменений в базе) — для баз, подключённых до v0.2.</summary>
        public bool LoadCatalog(string slug, string adminUser, string adminPassword)
        {
            try
            {
                _stateSlug = null;
                var cfg = AgentConfig.Load();
                var b = cfg.Find(slug);
                if (b == null) return Fail(Txt.F("NoSuchBase", slug), Txt.S("HintList"));
                var profile = Profiles.For(b.Configuration);
                Begin("access_connect");
                var plat = Platform.Choose(Platform.Find(), b.PlatformVersion) ?? Platform.Choose(Platform.Find());
                if (plat == null) { End(StepStatus.Fail); return Fail(Txt.S("NoPlatform"), Txt.S("HintInstallPlatform")); }
                var r = new ComClient(plat).Run(ComClient.FileConnection(b.Path, adminUser, adminPassword),
                    new ComOp { Op = "metaCatalog", Objects = profile.CatalogObjects.ToList() });
                b.Catalog = Access.FromMeta(profile, r[0]);
                cfg.Upsert(b);
                cfg.Save();
                Log.Ok(Txt.F("AccessCatalogLoaded", b.Catalog.Sections.Count, b.Catalog.Items.Count));
                End(StepStatus.Ok, Txt.F("AccessCatalogShort", b.Catalog.Sections.Count));
                return true;
            }
            catch (AgentException e) { if (_cur?.Status == StepStatus.Running) End(StepStatus.Fail); return Fail(e.Message, e.Hint); }
            catch (Exception e) { if (_cur?.Status == StepStatus.Running) End(StepStatus.Fail); return Fail(Txt.F("Unexpected", e.GetType().Name, e.Message), Txt.S("HintSendLog")); }
        }

        /// <summary>«Реквизиты из 1С…» для печатных форм: юридические адреса организаций (вход администратора, в базе ничего не меняется).</summary>
        public bool LoadPrintDetails(string slug, string adminUser, string adminPassword)
        {
            try
            {
                _stateSlug = null;
                var cfg = AgentConfig.Load();
                var b = cfg.Find(slug);
                if (b == null) return Fail(Txt.F("NoSuchBase", slug), Txt.S("HintList"));
                Begin("print_details");
                var plat = Platform.Choose(Platform.Find(), b.PlatformVersion) ?? Platform.Choose(Platform.Find());
                if (plat == null) { End(StepStatus.Fail); return Fail(Txt.S("NoPlatform"), Txt.S("HintInstallPlatform")); }
                var r = new ComClient(plat).Run(ComClient.FileConnection(b.Path, adminUser, adminPassword), new ComOp { Op = "orgAddresses" }, new ComOp { Op = "orgSigners" });
                b.Print = b.Print ?? new Print.PrintSettings();
                b.Print.Addresses = ((JObject)r[0]["addresses"]).Properties().ToDictionary(p => p.Name, p => (string)p.Value);
                b.Print.Signers = Print.OrgSigner.Parse((JObject)r[1]["signers"]);
                cfg.Upsert(b);
                cfg.Save();
                Log.Ok(Txt.F("PrintDetailsLoaded", b.Print.Addresses.Count));
                End(StepStatus.Ok, b.Print.Addresses.Count.ToString());
                return true;
            }
            catch (AgentException e) { if (_cur?.Status == StepStatus.Running) End(StepStatus.Fail); return Fail(e.Message, e.Hint); }
            catch (Exception e) { if (_cur?.Status == StepStatus.Running) End(StepStatus.Fail); return Fail(Txt.F("Unexpected", e.GetType().Name, e.Message), Txt.S("HintSendLog")); }
        }

        /// <summary>Роли для чтения служебных объектов, которых не дают роли профиля (нет объекта или роли — пропускаем, в журнал).</summary>
        private static List<string> ServiceRoles(ComClient client, string conn, ConfigProfile profile, IEnumerable<string> have)
        {
            var need = Profiles.ServiceODataObjects.Where(x => !profile.ODataObjects.Contains(x)).ToList();
            if (need.Count == 0) return new List<string>();
            var r = client.Run(conn,
                new ComOp { Op = "rightsMap", Objects = need, Scope = profile.CatalogObjects.ToList() },
                new ComOp { Op = "listRoles" });
            var all = new HashSet<string>(((JArray)r[1]["roles"]).Select(x => (string)x["name"]));
            var plan = Access.PlanRoles(profile, have.Where(all.Contains), need, new string[0], r[0]);
            foreach (var s in plan.NoRole) Log.Info(Txt.F("ServiceNoRole", s));
            foreach (var kv in plan.Why) Log.Info(Txt.F("AccessRoleFor", kv.Value, kv.Key));
            return plan.Extra.Distinct().ToList();
        }

        /// <summary>Роли, которые должны быть у bilem: роли профиля + добавленные «Доступом».</summary>
        public static List<string> ExpectedRoles(BaseConfig b) =>
            Profiles.For(b.Configuration).Roles.Concat(b.ExtraRoles ?? new List<string>()).Distinct().ToList();

        // ---------------- Удаление ----------------

        /// <summary>Убрать базу: публикация, пул, права пула на папку, пароль; пользователя bilem — по флагу (нужен логин администратора 1С).</summary>
        public bool Remove(string slug, bool dropUser, string adminUser = null, string adminPassword = null)
        {
            try
            {
                Begin("remove");
                var cfg = AgentConfig.Load();
                var b = cfg.Find(slug);
                if (b == null) { End(StepStatus.Fail); return Fail(Txt.F("NoSuchBase", slug), Txt.S("HintList")); }
                if (dropUser)
                {
                    var plat = Platform.Choose(Platform.Find(), b.PlatformVersion) ?? Platform.Choose(Platform.Find());
                    new ComClient(plat).Run(ComClient.FileConnection(b.Path, adminUser, adminPassword), new ComOp { Op = "dropUser", User = BilemUser, FullName = BilemFullName });
                    Log.Ok(Txt.F("UserDropped", BilemUser));
                }
                Iis.DeleteApp(b.Publication);
                Iis.DeletePool(b.Pool);
                var dir = Path.Combine(Iis.WwwRoot, b.Publication);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Iis.RevokePool(b.Path, b.Pool);
                Log.Ok(Txt.F("FolderRightsRevoked", b.Path, b.Pool));
                if (File.Exists(b.SecretFile)) File.Delete(b.SecretFile);
                if (File.Exists(Paths.StateFile(slug))) File.Delete(Paths.StateFile(slug));
                cfg.Bases.Remove(b);
                // Последняя база этой разрядности — вернуть системную регистрацию COM, которая была до агента
                if (!cfg.Bases.Any(x => x.PlatformBits == b.PlatformBits))
                {
                    var restored = Platform.RestoreCom(cfg, b.PlatformBits);
                    if (restored != null) Log.Ok(restored);
                }
                cfg.Save();
                Log.Ok(Txt.F("Removed", slug, b.Publication, b.Pool));
                if (!dropUser) Log.Info(Txt.S("RemoveUserKept"));
                Log.Info(Txt.S("RemoveODataKept"));
                End(StepStatus.Ok);
                return true;
            }
            catch (AgentException e) { if (_cur?.Status == StepStatus.Running) End(StepStatus.Fail); return Fail(e.Message, e.Hint); }
            catch (Exception e) { if (_cur?.Status == StepStatus.Running) End(StepStatus.Fail); return Fail(Txt.F("Unexpected", e.GetType().Name, e.Message), Txt.S("HintSendLog")); }
        }

        // ---------------- Вспомогательное ----------------

        private void ReportPrevious(string slug)
        {
            try
            {
                var f = Paths.StateFile(slug);
                if (!File.Exists(f)) return;
                var s = JObject.Parse(File.ReadAllText(f));
                var failed = ((JArray)s["steps"]).FirstOrDefault(x => (string)x["Status"] == "Fail");
                if (failed != null) Log.Info(Txt.F("PreviousFailedAt", Txt.S("Step_" + (string)failed["Id"]), (DateTime)s["time"]));
            }
            catch { }
        }

        private static bool PlanDone()
        {
            Log.Info(Txt.S("PlanReady"));
            return true;
        }

        private static bool Fail(string message, string hint)
        {
            Log.Fail(message);
            if (!string.IsNullOrEmpty(hint)) Log.Fail(Txt.S("WhatToDo") + " " + hint);
            return false;
        }
    }
}
