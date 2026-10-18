using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Bilem.Agent.Core.Mail;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>v0.3, часть C: SMTP и send_document.</summary>
    public class MailTests
    {
        private const string Secret = "app-pass-QWERTY-123";

        /// <summary>Заглушка SMTP без TLS: EHLO, AUTH PLAIN, MAIL/RCPT/DATA; копит всё, что пришло.</summary>
        private sealed class FakeSmtp : IDisposable
        {
            private readonly TcpListener _l = new TcpListener(IPAddress.Loopback, 0);
            public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;
            public readonly StringBuilder Log = new StringBuilder();
            public readonly List<string> Rcpt = new List<string>();
            public string Data;
            private readonly Thread _t;

            public FakeSmtp()
            {
                _l.Start();
                _t = new Thread(Serve) { IsBackground = true };
                _t.Start();
            }

            private void Serve()
            {
                using (var c = _l.AcceptTcpClient())
                using (var s = c.GetStream())
                using (var r = new StreamReader(s, Encoding.ASCII))
                using (var w = new StreamWriter(s, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true })
                {
                    w.WriteLine("220 fake ESMTP");
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        Log.AppendLine(line);
                        if (line.StartsWith("EHLO")) { w.WriteLine("250-fake"); w.WriteLine("250 AUTH PLAIN LOGIN"); }
                        else if (line.StartsWith("AUTH PLAIN")) w.WriteLine("235 ok");
                        else if (line.StartsWith("MAIL FROM")) w.WriteLine("250 ok");
                        else if (line.StartsWith("RCPT TO")) { Rcpt.Add(line.Substring(9).Trim('<', '>', ' ')); w.WriteLine("250 ok"); }
                        else if (line == "DATA")
                        {
                            w.WriteLine("354 go");
                            var sb = new StringBuilder();
                            while ((line = r.ReadLine()) != null && line != ".") sb.AppendLine(line);
                            Data = sb.ToString();
                            w.WriteLine("250 queued");
                        }
                        else if (line == "QUIT") { w.WriteLine("221 bye"); break; }
                        else w.WriteLine("500 ?");
                    }
                }
            }

            public void Dispose() { try { _l.Stop(); } catch { } }
        }

        [Fact]
        public void Smtp_SendsPdfAndBcc()
        {
            using (var srv = new FakeSmtp())
            {
                var pdf = Encoding.ASCII.GetBytes("%PDF-1.7 test");
                new SmtpSender { Host = "127.0.0.1", Port = srv.Port, Security = SmtpSecurity.None, Login = "me@example.com", Password = Secret }
                    .Send(new MailMessageData { From = "me@example.com", FromName = "Бухгалтерия", To = "client@example.com", Bcc = "me@example.com", Subject = "Счёт на оплату №4", Body = "Здравствуйте!", AttachmentName = "Счёт №4 (demo).pdf", Attachment = pdf });
                Thread.Sleep(200);
                Assert.Equal(new[] { "client@example.com", "me@example.com" }, srv.Rcpt);
                Assert.Contains("application/pdf", srv.Data);
                Assert.Contains(Convert.ToBase64String(pdf), srv.Data);
                Assert.Contains("=?utf-8?B?", srv.Data);              // кириллица в заголовках — RFC 2047
                Assert.DoesNotContain(Secret, srv.Log.ToString());    // пароль открытым текстом по сети не идёт (только base64 в AUTH)
            }
        }

        /// <summary>Исправление Славы 18.10: счёт и акт сверки — на любой e-mail; доверенность — только «Список адресов для отправки доверенностей».</summary>
        [Fact]
        public void Recipient_InvoiceAnyAddress_PoaOnlyOwn()
        {
            var own = new[] { "slava@example.com" };
            Assert.Equal("anyone@client.kz", MailService.CheckRecipient(MailService.DocInvoice, "anyone@client.kz", own));
            Assert.Equal("anyone@client.kz", MailService.CheckRecipient(MailService.DocReconciliation, "anyone@client.kz", own));
            Assert.Equal("slava@example.com", MailService.CheckRecipient(MailService.DocPowerOfAttorney, "SLAVA@example.com".ToLowerInvariant(), own));
            var e = Assert.Throws<AgentException>(() => MailService.CheckRecipient(MailService.DocPowerOfAttorney, "stranger@evil.example", own));
            Assert.Equal("Доверенность отправляется только на адреса из поля «Список адресов для отправки доверенностей» во вкладке SMTP.", e.Message);
            Assert.Throws<AgentException>(() => MailService.CheckRecipient(MailService.DocInvoice, "не адрес", own));
        }

        [Fact]
        public void Limits_HourAndDay()
        {
            var acc = new SmtpAccount { LimitHour = 2, LimitDay = 3 };
            var now = new DateTime(2026, 10, 18, 15, 0, 0);
            MailService.CheckLimits(acc, now, new[] { now.AddMinutes(-10) });
            Assert.Throws<AgentException>(() => MailService.CheckLimits(acc, now, new[] { now.AddMinutes(-10), now.AddMinutes(-20) }));
            Assert.Throws<AgentException>(() => MailService.CheckLimits(acc, now, new[] { now.AddHours(-2), now.AddHours(-3), now.AddHours(-5) }));
        }

        private static AgentConfig Cfg(bool enabled, int port = 1)
        {
            var acc = new SmtpAccount { Enabled = enabled, Host = "127.0.0.1", Port = port, Security = SmtpSecurity.None, Login = "me@example.com", Bcc = "copy@example.com", LimitHour = 0, LimitDay = 0 };
            acc.SetPassword(Secret);
            var cfg = new AgentConfig { Smtp = acc };
            cfg.Bases.Add(new BaseConfig { Slug = "t", Mail = new MailSettings { FromName = "ТОО Тест" } });
            return cfg;
        }

        /// <summary>Исправление Славы 18.10 (вечер): окна подтверждения нет — письмо уходит сразу; рубильник и ненастроенный ящик — отказ.</summary>
        [Fact]
        public void Send_ImmediatelyWithBcc_SwitchOffAndNotConfigured()
        {
            var off = Cfg(enabled: false);
            Assert.Contains("выключен", Assert.Throws<AgentException>(() => MailService.Send(off.Bases[0], off, "doc", "a@b.kz", "s", "t", "f.pdf", new byte[1])).Message);
            var empty = new AgentConfig { Smtp = new SmtpAccount { Enabled = true } };
            Assert.Contains("не настроен", Assert.Throws<AgentException>(() => MailService.Send(off.Bases[0], empty, "doc", "a@b.kz", "s", "t", "f.pdf", new byte[1])).Message);
            using (var srv = new FakeSmtp())
            {
                var cfg = Cfg(enabled: true, port: srv.Port);
                var sent = MailService.Send(cfg.Bases[0], cfg, "doc", "client@example.com", "Счёт", "Текст", "f.pdf", Encoding.ASCII.GetBytes("%PDF-1.7"));
                Thread.Sleep(200);
                Assert.Equal("client@example.com", sent.To);
                Assert.Equal("copy@example.com", sent.Bcc);
                Assert.Equal(new[] { "client@example.com", "copy@example.com" }, srv.Rcpt);
            }
        }

        /// <summary>Ошибка SMTP — отказ с её текстом, не «отправлено».</summary>
        [Fact]
        public void Send_SmtpErrorIsReported()
        {
            var cfg = Cfg(enabled: true, port: 1);
            var e = Assert.Throws<AgentException>(() => MailService.Send(cfg.Bases[0], cfg, "doc", "a@b.kz", "s", "t", "f.pdf", new byte[1]));
            Assert.StartsWith("Письмо не отправлено — ошибка SMTP", e.Message);
            Assert.DoesNotContain(Secret, e.Message);
        }

        /// <summary>Пароль не виден нигде: ни в конфиге открытым текстом, ни в журнале, ни в ответе MCP.</summary>
        [Fact]
        public void Password_NeverInPlainText()
        {
            var lines = new List<string>();
            Action<LogEntry> onLog = en => lines.Add(en.ToString());
            Log.Written += onLog;
            try
            {
                var cfg = Cfg(enabled: true);
                cfg.Save();
                Assert.DoesNotContain(Secret, File.ReadAllText(Paths.ConfigFile));
                Assert.Equal(Secret, AgentConfig.Load().Smtp.Sender().Password);   // расшифровывается только на этой машине
                try { new SmtpSender { Host = "127.0.0.1", Port = 1, Login = "me", Password = Secret, TimeoutMs = 2000 }.Send(new MailMessageData { From = "a@b.kz", To = "a@b.kz" }); }
                catch (Exception e) { Assert.DoesNotContain(Secret, e.Message); }
                using (var srv = new FakeSmtp())
                {
                    var c2 = Cfg(enabled: true, port: srv.Port);
                    MailService.Send(c2.Bases[0], c2, "doc", "a@b.kz", "s", "t", "f.pdf", new byte[1]);
                    Thread.Sleep(200);
                    Assert.DoesNotContain(Secret, srv.Data ?? "");
                }
            }
            finally { Log.Written -= onLog; }
            Assert.DoesNotContain(lines, l => l.Contains(Secret));
        }

        /// <summary>Аудит: строка JSON — кто запросил (программа, адрес, Tailscale), путь и SHA-256 PDF, адреса письма; дописывание.</summary>
        [Fact]
        public void Audit_WritesCallerHashAndMail()
        {
            var pdf = Encoding.ASCII.GetBytes("%PDF-1.7 audit");
            var caller = new Mcp.McpCaller { Client = "claude-code 2.1", Remote = "127.0.0.1", TailscaleLogin = "slava@example.com" };
            var f = Audit.FileFor(DateTime.Now);
            var before = File.Exists(f) ? File.ReadAllLines(f).Length : 0;
            Audit.Write(Audit.Print, "t", MailService.DocInvoice, "Счёт №4 от 13.04.2022 (t)", @"C:\x\Счёт.pdf", pdf, caller: caller);
            Audit.Write(Audit.Send, "t", MailService.DocInvoice, "Счёт №4 от 13.04.2022 (t)", @"C:\x\Счёт.pdf", pdf, "client@example.com", "copy@example.com", "ok", caller);
            var lines = File.ReadAllLines(f);
            Assert.Equal(before + 2, lines.Length);
            var o = Newtonsoft.Json.Linq.JObject.Parse(lines[lines.Length - 1]);
            Assert.Equal("send", (string)o["action"]);
            Assert.Equal("claude-code 2.1", (string)o["requested_by"]["client"]);
            Assert.Equal("slava@example.com", (string)o["requested_by"]["tailscale_login"]);
            Assert.Equal(Audit.Sha256(pdf), (string)o["pdf"]["sha256"]);
            Assert.Equal(64, ((string)o["pdf"]["sha256"]).Length);
            Assert.Equal("copy@example.com", (string)o["mail"]["bcc"]);
        }

        /// <summary>initialize → Mcp-Session-Id; по сессии — программа клиента.</summary>
        [Fact]
        public void Caller_SessionRemembersClient()
        {
            var c = new Mcp.McpCaller { Remote = "127.0.0.1" };
            Mcp.McpCaller.Current = c;
            try { Mcp.McpCaller.Initialized("claude-code", "2.1.3"); }
            finally { Mcp.McpCaller.Current = null; }
            Assert.NotNull(c.NewSession);
            Assert.Equal("claude-code 2.1.3", Mcp.McpCaller.ClientOf(c.NewSession));
            Assert.Equal("claude-code 2.1.3 (127.0.0.1)", c.ToString());
        }

        [Fact]
        public void TrustedList_Parse()
        {
            Assert.Equal(new[] { "slava@example.com", "buh@example.kz" }, SmtpAccount.ParseList("slava@example.com, buh@example.kz; не-адрес, SLAVA@example.com"));
        }

        [Fact]
        public void Presets_HaveIcloud()
        {
            Assert.Contains(MailPresets.All, p => p.Name == "iCloud" && p.Host == "smtp.mail.me.com" && p.Port == 587);
        }
    }
}
