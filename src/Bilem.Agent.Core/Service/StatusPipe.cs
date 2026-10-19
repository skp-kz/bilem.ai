using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Service
{
    /// <summary>
    /// Именованный канал службы \\.\pipe\BilemAgent: одна строка запроса → одна строка JSON ответа.
    /// «status» — может спросить любой вошедший пользователь (окно в трее без прав администратора);
    /// команды («restart-mcp») — только администратор с повышенными правами (проверяется по учётке клиента канала).
    /// </summary>
    public sealed class StatusPipe : IDisposable
    {
        public const string Name = "BilemAgent";
        private readonly Func<JObject> _status;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        /// <summary>Команды администратора: имя → действие. Заполняет служба.</summary>
        public Action RestartMcp { get; set; }

        public StatusPipe(Func<JObject> status) { _status = status; }

        public void Start()
        {
            for (var i = 0; i < 4; i++) Task.Run(() => Loop(_cts.Token));
        }

        internal static PipeSecurity Security()
        {
            var s = new PipeSecurity();
            s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            // Вошедшие пользователи (консоль и RDP) — читать состояние; создавать экземпляры канала не могут
            s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            return s;
        }

        private async Task Loop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using (var p = new NamedPipeServerStream(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 65536, Security()))
                    {
                        await p.WaitForConnectionAsync(ct).ConfigureAwait(false);
                        var reader = new StreamReader(p, new UTF8Encoding(false), false, 1024, true);
                        var cmd = ((await reader.ReadLineAsync().ConfigureAwait(false)) ?? "").Trim().ToLowerInvariant();
                        JObject reply;
                        if (cmd == "status" || cmd.Length == 0) reply = _status();
                        else if (cmd == "restart-mcp")
                        {
                            var admin = false;
                            p.RunAsClient(() => { using (var id = WindowsIdentity.GetCurrent()) admin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); });
                            if (!admin) reply = new JObject { ["error"] = Txt.S("SvcNeedAdmin") };
                            else { RestartMcp?.Invoke(); reply = new JObject { ["ok"] = true }; }
                        }
                        else reply = new JObject { ["error"] = "unknown command" };
                        var bytes = new UTF8Encoding(false).GetBytes(reply.ToString(Newtonsoft.Json.Formatting.None) + "\n");
                        await p.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
                        p.WaitForPipeDrain();
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { Log.Info(Txt.F("SvcPipeError", e.Message)); await Task.Delay(1000).ConfigureAwait(false); }
            }
        }

        /// <summary>Клиент: запрос к службе. Служба не запущена — null.</summary>
        public static JObject Ask(string command = "status", int timeoutMs = 2000)
        {
            try
            {
                using (var c = new NamedPipeClientStream(".", Name, PipeDirection.InOut))
                {
                    c.Connect(timeoutMs);
                    var w = new StreamWriter(c, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                    w.WriteLine(command);
                    var line = new StreamReader(c, new UTF8Encoding(false)).ReadLine();
                    return string.IsNullOrEmpty(line) ? null : JObject.Parse(line);
                }
            }
            catch { return null; }
        }

        public void Dispose() => _cts.Cancel();
    }
}
