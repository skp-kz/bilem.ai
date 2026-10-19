using System;
using System.ServiceProcess;
using Bilem.Agent.Core;
using Bilem.Agent.Core.Service;

namespace Bilem.Agent.Service
{
    /// <summary>Служба BilemAgent: старт — AgentServer (MCP, проверки, канал состояния), стоп — освобождает порт MCP.</summary>
    internal sealed class AgentService : ServiceBase
    {
        private AgentServer _server;

        public AgentService()
        {
            ServiceName = AgentServer.ServiceName;
            CanStop = true;
            CanShutdown = true;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Fail(Txt.F("SvcCrashed", (e.ExceptionObject as Exception)?.Message));
            _server = new AgentServer();
            _server.Start();
        }

        protected override void OnStop() { _server?.Dispose(); _server = null; }

        protected override void OnShutdown() => OnStop();

        private static void Main(string[] args)
        {
            // Запуск из консоли для отладки: bilem-agent-svc.exe --console (как «bilem-agent serve»)
            if (Environment.UserInteractive && args.Length > 0 && args[0] == "--console")
            {
                using (var s = new AgentServer())
                {
                    s.Start();
                    Console.WriteLine("Bilem Agent: служба в консоли, Enter — выход");
                    Console.ReadLine();
                }
                return;
            }
            ServiceBase.Run(new AgentService());
        }
    }
}
