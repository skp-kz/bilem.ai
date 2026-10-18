using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Bilem.Agent.Core;
using Bilem.Agent.Core.Mcp;

namespace Bilem.Agent
{
    /// <summary>CLI — то же, что кнопки окна. Код возврата: 0 — успех, 1 — любая проверка не прошла.</summary>
    internal static class Cli
    {
        public static int Run(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Log.Written += Print;
            var cmd = args[0].ToLowerInvariant();
            var a = Parse(args.Skip(1).ToArray());
            try
            {
                switch (cmd)
                {
                    case "diag": return new Onboarding().Diag(Options(a)) ? 0 : 1;
                    case "setup": return new Onboarding().Setup(Options(a)) ? 0 : 1;
                    case "verify":
                        {
                            var admin = Get(a, "admin");
                            return new Onboarding().Verify(Get(a, "slug"), admin, admin == null ? null : Password(a)) ? 0 : 1;
                        }
                    case "list": return List();
                    case "shortcut": return Shortcuts.Recreate() ? 0 : 1;
                    case "sysinfo": Console.Write(SysInfo.Collect()); return 0;
                    case "doctor":
                        {
                            var res = Diagnostics.Run();
                            Console.Write(Diagnostics.Report(res));
                            return res.Any(x => x.Status == CheckStatus.Fail) ? 1 : 0;
                        }
                    case "remove":
                        {
                            var slug = Require(a, "slug");
                            var drop = a.ContainsKey("drop-user");
                            var admin = Get(a, "admin");
                            if (drop && admin == null) { Log.Fail(Txt.S("CliDropNeedsAdmin")); return 1; }
                            return new Onboarding().Remove(slug, drop, admin, drop ? Password(a) : null) ? 0 : 1;
                        }
                    case "access": return AccessCmd(a);
                    case "print": return PrintCmd(a);
                    case "serve": return Serve();
                    case "connect":
                        {
                            var client = args.Length > 1 ? args[1] : null;
                            var cfg = AgentConfig.Load();
                            return Connectors.Connect(client, cfg.McpUrl(cfg.GetOrCreateToken())) ? 0 : 1;
                        }
                    case "url":
                        {
                            var cfg = AgentConfig.Load();
                            Console.WriteLine(cfg.McpUrl(cfg.GetOrCreateToken()));
                            return 0;
                        }
                    case "help": case "-h": case "--help": case "/?": Help(); return 0;
                    default: Log.Fail(Txt.F("CliUnknownCommand", cmd)); Help(); return 1;
                }
            }
            catch (AgentException e)
            {
                Log.Fail(e.Message);
                if (!string.IsNullOrEmpty(e.Hint)) Log.Fail(Txt.S("WhatToDo") + " " + e.Hint);
                return 1;
            }
            catch (Exception e)
            {
                Log.Fail(Txt.F("Unexpected", e.GetType().Name, e.Message));
                return 1;
            }
        }

        private static SetupOptions Options(Dictionary<string, string> a)
        {
            var o = new SetupOptions
            {
                BasePath = Require(a, "base"),
                AdminUser = Get(a, "admin"),
                Slug = Get(a, "slug"),
                EnableIis = a.ContainsKey("enable-iis"),
                ResetPassword = a.ContainsKey("reset-password"),
                PlatformVersion = Get(a, "platform"),
            };
            if (o.AdminUser != null) o.AdminPassword = Password(a);
            return o;
        }

        /// <summary>Пароль администратора 1С: --password, иначе переменная BILEM_1C_ADMIN_PWD, иначе ввод с клавиатуры (не отображается). Нигде не сохраняется.</summary>
        private static string Password(Dictionary<string, string> a)
        {
            if (a.TryGetValue("password", out var p)) return p ?? "";
            var env = Environment.GetEnvironmentVariable("BILEM_1C_ADMIN_PWD");
            if (env != null) return env;
            if (Console.IsInputRedirected) return "";
            Console.Write(Txt.S("CliPasswordPrompt"));
            var sb = new StringBuilder();
            while (true)
            {
                var k = Console.ReadKey(true);
                if (k.Key == ConsoleKey.Enter) break;
                if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
                sb.Append(k.KeyChar);
            }
            Console.WriteLine();
            return sb.ToString();
        }

        /// <summary>
        /// access --slug X — дерево доступа; --load --admin A — разделы из подсистем 1С;
        /// --read-on/--read-off/--write-on/--write-off «Документ.X,Справочник.Y» — изменить (слой MCP сразу, с --admin — и 1С).
        /// </summary>
        private static int AccessCmd(Dictionary<string, string> a)
        {
            var slug = Require(a, "slug");
            var cfg = AgentConfig.Load();
            var b = cfg.Find(slug);
            if (b == null) { Log.Fail(Txt.F("NoSuchBase", slug)); return 1; }
            var admin = Get(a, "admin");
            if (a.ContainsKey("load"))
            {
                if (admin == null) { Log.Fail(Txt.S("CliAccessNeedsAdmin")); return 1; }
                if (!new Onboarding().LoadCatalog(slug, admin, Password(a))) return 1;
                cfg = AgentConfig.Load(); b = cfg.Find(slug);
            }
            var cat = Access.CatalogOf(b);
            List<string> L(string k) => (Get(a, k) ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
            var read = new HashSet<string>(b.ReadEffective);
            var write = new HashSet<string>(b.Write ?? new List<string>());
            read.UnionWith(L("read-on")); write.UnionWith(L("write-on"));
            foreach (var x in L("write-on").Where(x => !Access.WriteWhitelist.Contains(x)))
                Log.Warn(Txt.F("AccessWriteNotAllowed", cat.TitleOf(x)) + " " + Txt.S("UiAccessWriteLockTip"));
            foreach (var x in L("read-off")) { read.Remove(x); write.Remove(x); }
            foreach (var x in L("write-off")) write.Remove(x);
            var n = Access.Normalize(cat, read, write);
            var c = Access.Diff(b, n.Read, n.Write);
            if (c.Any)
            {
                b.Read = n.Read.ToList(); b.Write = n.Write.ToList();
                cfg.Upsert(b); cfg.Save();
                Log.Info(Txt.F("AccessChangedLog", b.Slug, Environment.UserName, c.ReadOn.Count, c.ReadOff.Count, c.WriteOn.Count, c.WriteOff.Count,
                    string.Join(", ", c.ReadOn.Concat(c.ReadOff).Concat(c.WriteOn).Concat(c.WriteOff).Distinct().Select(cat.TitleOf))));
            }
            // --apply — применить к 1С и без изменений (после обновления агента: новые служебные объекты, роли)
            if (a.ContainsKey("apply") || Access.NeedsOneC(b, n.Read, n.Write))
            {
                if (admin == null) Log.Warn(Txt.S("UiAccessPending1C"));
                else if (!new Onboarding().ApplyAccess(slug, admin, Password(a), n.Read, n.Write)) return 1;
                cfg = AgentConfig.Load(); b = cfg.Find(slug); cat = Access.CatalogOf(b);
            }
            foreach (var s in cat.Sections)
            {
                var items = cat.Items.Where(i => i.Section == s.Key).OrderBy(i => i.Title, StringComparer.CurrentCulture).ToList();
                Console.WriteLine((s.Locked ? "[замок] " : "") + s.Title);
                foreach (var i in items)
                    Console.WriteLine("  {0} {1} {2}  ({3})", b.ReadEffective.Contains(i.FullName) ? "Ч" : "·", (b.Write ?? new List<string>()).Contains(i.FullName) ? "З" : "·", i.Title, i.FullName);
            }
            return 0;
        }

        /// <summary>
        /// print --slug X [--number N] — PDF счёта на оплату (без номера — последний) в «Документы\Bilem\Счета»;
        /// настройки: --logo/--stamp/--sign «файл», --signer-name, --signer-title; --details --admin A — адрес из 1С.
        /// </summary>
        private static int PrintCmd(Dictionary<string, string> a)
        {
            var slug = Require(a, "slug");
            var cfg = AgentConfig.Load();
            var b = cfg.Find(slug);
            if (b == null) { Log.Fail(Txt.F("NoSuchBase", slug)); return 1; }
            if (a.ContainsKey("details"))
            {
                var admin = Get(a, "admin");
                if (admin == null) { Log.Fail(Txt.S("CliAccessNeedsAdmin")); return 1; }
                if (!new Onboarding().LoadPrintDetails(slug, admin, Password(a))) return 1;
                cfg = AgentConfig.Load(); b = cfg.Find(slug);
            }
            var changed = false;
            var from = Get(a, "copy-from");
            if (from != null)
            {
                var src = cfg.Find(from);
                if (src == null) { Log.Fail(Txt.F("NoSuchBase", from)); return 1; }
                Bilem.Agent.Core.Print.PrintStore.Copy(src, b);
                Log.Ok(Txt.F("UiPrintCopied", from, b.Slug));
                changed = true;
            }
            foreach (var (key, kind) in new[] { ("logo", Bilem.Agent.Core.Print.PrintImage.Logo), ("stamp", Bilem.Agent.Core.Print.PrintImage.Stamp), ("sign", Bilem.Agent.Core.Print.PrintImage.Sign) })
            {
                var f = Get(a, key);
                if (f == null) continue;
                var note = Bilem.Agent.Core.Print.PrintStore.Save(slug, kind, f);
                Log.Ok(Txt.F("PrintImageSaved", key) + (note == null ? "" : " " + note));
            }
            b.Print = b.Print ?? new Bilem.Agent.Core.Print.PrintSettings();
            if (Get(a, "issued-by") != null) { b.Print.IssuedBy = Get(a, "issued-by"); changed = true; }
            if (changed) { cfg.Upsert(b); cfg.Save(); }
            var rt = BaseRuntime.FromConfig(b);
            var res = Bilem.Agent.Core.Print.PrintService.Invoice(b, rt.OData, Get(a, "number"));
            Log.Ok(Txt.F("PrintDone", res.Path));
            var miss = Bilem.Agent.Core.Print.PrintStore.Missing(b.Slug);
            if (miss != null) Log.Warn(Txt.F("PrintNotConfigured", b.Slug, miss));
            Console.WriteLine(res.Path);
            return 0;
        }

        private static int List()
        {
            var cfg = AgentConfig.Load();
            if (cfg.Bases.Count == 0) { Console.WriteLine(Txt.S("NoBases")); return 0; }
            foreach (var b in cfg.Bases)
                Console.WriteLine(Txt.F("CliListRow", b.Slug, b.DisplayName, b.Version, b.Path, b.OData, b.Pool,
                    b.VerifiedAt?.ToString("dd.MM.yyyy HH:mm") ?? "—", b.WriteDeniedAt?.ToString("dd.MM.yyyy") ?? "—"));
            return 0;
        }

        private static int Serve()
        {
            using (var host = new McpHost())
            {
                host.Start();
                Log.Info(Txt.S("CliServeStop"));
                Bilem.Agent.Core.Updates.Updater.CompleteIfPending();
                // Режим без окна: обновления только проверяем и сообщаем в журнал; ставит человек кнопкой в окне
                using (new Timer(_ =>
                {
                    try
                    {
                        var last = Bilem.Agent.Core.Updates.Updater.LastCheck;
                        if (last != null && DateTime.Now - last.Value < TimeSpan.FromDays(1)) return;
                        var r = Bilem.Agent.Core.Updates.Updater.CheckNewer(AgentConfig.Load().UpdateBeta);
                        if (r != null) Log.Info(Txt.F("UpdAvailableLog", r.Version));
                    }
                    catch (Exception e) { Log.Info(Txt.F("UpdCheckFailedQuiet", e.Message)); }
                }, null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1)))
                {
                    var done = new ManualResetEvent(false);
                    Console.CancelKeyPress += (s, e) => { e.Cancel = true; done.Set(); };
                    done.WaitOne();
                }
            }
            return 0;
        }

        private static void Help() => Console.WriteLine(Txt.S("CliHelp"));

        private static void Print(LogEntry e)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = e.Level == LogLevel.Ok ? ConsoleColor.Green : e.Level == LogLevel.Plan ? ConsoleColor.Cyan
                : e.Level == LogLevel.Warn ? ConsoleColor.Yellow : e.Level == LogLevel.Fail ? ConsoleColor.Red : ConsoleColor.Gray;
            Console.WriteLine(e.ToString());
            Console.ForegroundColor = old;
        }

        /// <summary>--ключ значение / --флаг.</summary>
        internal static Dictionary<string, string> Parse(string[] args)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--")) continue;
                var key = args[i].Substring(2);
                string val = null;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) val = args[++i];
                d[key] = val;
            }
            return d;
        }

        private static string Get(Dictionary<string, string> a, string k) => a.TryGetValue(k, out var v) ? v : null;

        private static string Require(Dictionary<string, string> a, string k)
        {
            var v = Get(a, k);
            if (string.IsNullOrEmpty(v)) throw new AgentException(Txt.F("CliMissingArg", k), Txt.S("CliSeeHelp"));
            return v;
        }
    }
}
