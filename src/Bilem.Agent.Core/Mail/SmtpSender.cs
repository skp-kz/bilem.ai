using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace Bilem.Agent.Core.Mail
{
    public enum SmtpSecurity { SslOnConnect, StartTls, None }

    public sealed class MailMessageData
    {
        public string FromName { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        public string Bcc { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public string AttachmentName { get; set; }
        public byte[] Attachment { get; set; }
    }

    /// <summary>Ошибка SMTP — текст сервера для человека (без пароля).</summary>
    public sealed class SmtpException : Exception
    {
        public SmtpException(string message) : base(message) { }
    }

    /// <summary>
    /// Минимальный SMTP-клиент: SSL сразу (465) или STARTTLS (587), AUTH PLAIN/LOGIN, одно письмо: текст + PDF.
    /// System.Net.Mail не умеет SSL сразу при подключении (465 — Mail.ru, Яндекс). Пароль в журнал и в текст ошибки не попадает.
    /// </summary>
    public sealed class SmtpSender
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public SmtpSecurity Security { get; set; }
        public string Login { get; set; }
        public string Password { get; set; }
        public int TimeoutMs { get; set; } = 30000;
        /// <summary>Для тестов с локальным сервером без настоящего сертификата.</summary>
        public bool AcceptAnyCertificate { get; set; }

        private Stream _s;
        private StreamReader _r;

        public void Send(MailMessageData m)
        {
            using (var tcp = new TcpClient())
            {
                tcp.ReceiveTimeout = tcp.SendTimeout = TimeoutMs;
                var connect = tcp.ConnectAsync(Host, Port);
                if (!connect.Wait(TimeoutMs)) throw new SmtpException(Txt.F("SmtpConnectTimeout", Host, Port));
                if (connect.IsFaulted) throw new SmtpException(Txt.F("SmtpConnectFailed", Host, Port, connect.Exception?.GetBaseException().Message));
                _s = tcp.GetStream();
                if (Security == SmtpSecurity.SslOnConnect) Tls();
                Bind();
                Expect(220);
                var ehlo = Ehlo();
                if (Security == SmtpSecurity.StartTls)
                {
                    if (!ehlo.Any(x => x.StartsWith("STARTTLS", StringComparison.OrdinalIgnoreCase))) throw new SmtpException(Txt.S("SmtpNoStartTls"));
                    Cmd("STARTTLS", 220);
                    Tls();
                    Bind();
                    ehlo = Ehlo();
                }
                if (!string.IsNullOrEmpty(Login)) Auth(ehlo);
                Cmd($"MAIL FROM:<{m.From}>", 250);
                Cmd($"RCPT TO:<{m.To}>", 250, 251);
                if (!string.IsNullOrEmpty(m.Bcc) && !string.Equals(m.Bcc, m.To, StringComparison.OrdinalIgnoreCase)) Cmd($"RCPT TO:<{m.Bcc}>", 250, 251);
                Cmd("DATA", 354);
                var data = Mime(m);
                var bytes = Encoding.ASCII.GetBytes(data + "\r\n.\r\n");
                _s.Write(bytes, 0, bytes.Length);
                Expect(250);
                try { Cmd("QUIT", 221); } catch { }
            }
        }

        private void Tls()
        {
            var ssl = new SslStream(_s, false, (a, b, c, e) => AcceptAnyCertificate || e == SslPolicyErrors.None);
            try { ssl.AuthenticateAsClient(Host, null, SslProtocols.Tls12 | (SslProtocols)12288 /* Tls13 */, true); }
            catch (Exception e) { throw new SmtpException(Txt.F("SmtpTlsFailed", e.GetBaseException().Message)); }
            _s = ssl;
        }

        private void Bind() => _r = new StreamReader(_s, Encoding.ASCII, false, 1024, true);

        private List<string> Ehlo()
        {
            Write("EHLO bilem-agent");
            return Expect(250).Select(l => l.Length > 4 ? l.Substring(4) : "").ToList();
        }

        private void Auth(List<string> ehlo)
        {
            var auth = ehlo.FirstOrDefault(x => x.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase)) ?? "AUTH PLAIN LOGIN";
            if (auth.IndexOf("PLAIN", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0" + Login + "\0" + Password));
                Write("AUTH PLAIN " + token, secret: true);
                Expect(235);
                return;
            }
            Cmd("AUTH LOGIN", 334);
            Write(Convert.ToBase64String(Encoding.UTF8.GetBytes(Login)));
            Expect(334);
            Write(Convert.ToBase64String(Encoding.UTF8.GetBytes(Password)), secret: true);
            Expect(235);
        }

        private void Cmd(string line, params int[] ok) { Write(line); Expect(ok); }

        private string _last;
        private void Write(string line, bool secret = false)
        {
            _last = secret ? line.Split(' ')[0] + " ***" : line;
            var b = Encoding.ASCII.GetBytes(line + "\r\n");
            _s.Write(b, 0, b.Length);
            _s.Flush();
        }

        /// <summary>Ответ сервера (многострочный); не тот код — ошибка с текстом сервера.</summary>
        private List<string> Expect(params int[] ok)
        {
            var lines = new List<string>();
            while (true)
            {
                var l = _r.ReadLine() ?? throw new SmtpException(Txt.S("SmtpClosed"));
                lines.Add(l);
                if (l.Length < 4 || l[3] != '-') break;
            }
            var code = int.TryParse(lines.Last().Substring(0, Math.Min(3, lines.Last().Length)), out var c) ? c : 0;
            if (!ok.Contains(code))
            {
                var text = string.Join(" ", lines.Select(x => x.Length > 4 ? x.Substring(4) : x)).Trim();
                throw new SmtpException(Txt.F("SmtpRejected", code, text));
            }
            return lines;
        }

        // ---------- MIME ----------

        internal static string Mime(MailMessageData m)
        {
            var boundary = "bilem-" + Guid.NewGuid().ToString("N");
            var sb = new StringBuilder();
            void H(string s) => sb.Append(s).Append("\r\n");
            H("From: " + Address(m.FromName, m.From));
            H("To: <" + m.To + ">");
            H("Subject: " + Header(m.Subject));
            H("Date: " + DateTime.Now.ToString("ddd, dd MMM yyyy HH:mm:ss ", System.Globalization.CultureInfo.InvariantCulture) + DateTimeOffset.Now.ToString("zzz").Replace(":", ""));
            H("Message-ID: <" + Guid.NewGuid().ToString("N") + "@bilem-agent>");
            H("MIME-Version: 1.0");
            H("X-Mailer: Bilem Agent");
            if (m.Attachment == null)
            {
                H("Content-Type: text/plain; charset=utf-8");
                H("Content-Transfer-Encoding: base64");
                H("");
                Wrap76(sb, Convert.ToBase64String(Encoding.UTF8.GetBytes(m.Body ?? "")));
                return sb.ToString().TrimEnd('\r', '\n');
            }
            H($"Content-Type: multipart/mixed; boundary=\"{boundary}\"");
            H("");
            H("--" + boundary);
            H("Content-Type: text/plain; charset=utf-8");
            H("Content-Transfer-Encoding: base64");
            H("");
            Wrap76(sb, Convert.ToBase64String(Encoding.UTF8.GetBytes(m.Body ?? "")));
            H("--" + boundary);
            var name = Header(m.AttachmentName);
            H($"Content-Type: application/pdf; name=\"{name}\"");
            H("Content-Transfer-Encoding: base64");
            H($"Content-Disposition: attachment; filename=\"{name}\"");
            H("");
            Wrap76(sb, Convert.ToBase64String(m.Attachment));
            H("--" + boundary + "--");
            return sb.ToString().TrimEnd('\r', '\n');
        }

        private static void Wrap76(StringBuilder sb, string b64)
        {
            for (var i = 0; i < b64.Length; i += 76) sb.Append(b64, i, Math.Min(76, b64.Length - i)).Append("\r\n");
        }

        /// <summary>Заголовок не ASCII — RFC 2047 (=?utf-8?B?…?=), кусками, чтобы строка не была длиннее 76.</summary>
        internal static string Header(string s)
        {
            s = s ?? "";
            if (s.All(ch => ch >= 32 && ch < 127)) return s;
            var parts = new List<string>();
            var bytes = Encoding.UTF8.GetBytes(s);
            var chunk = new List<byte>();
            var chars = s.ToCharArray();
            var cur = new StringBuilder();
            foreach (var ch in s)
            {
                cur.Append(ch);
                if (Encoding.UTF8.GetByteCount(cur.ToString()) > 42) { parts.Add(cur.ToString(0, cur.Length - 1)); cur.Remove(0, cur.Length - 1); }
            }
            if (cur.Length > 0) parts.Add(cur.ToString());
            return string.Join("\r\n ", parts.Select(p => "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(p)) + "?="));
        }

        private static string Address(string name, string addr) =>
            string.IsNullOrWhiteSpace(name) ? "<" + addr + ">" : Header(name) + " <" + addr + ">";
    }
}
