using System;
using System.ServiceProcess;
using Microsoft.Win32;

namespace Bilem.Agent.Core.Service
{
    /// <summary>Служба BilemAgent: установлена ли, в каком состоянии. Читать может и обычный пользователь.</summary>
    public static class ServiceInfo
    {
        public static bool Installed
        {
            get
            {
                try
                {
                    using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                        .OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + AgentServer.ServiceName))
                        return k != null;
                }
                catch { return false; }
            }
        }

        /// <summary>Состояние службы; не установлена или нет доступа — null.</summary>
        public static ServiceControllerStatus? Status
        {
            get
            {
                try { using (var sc = new ServiceController(AgentServer.ServiceName)) return sc.Status; }
                catch { return null; }
            }
        }

        /// <summary>Запустить службу (нужны права администратора) и подождать до 30 с.</summary>
        public static void Start()
        {
            using (var sc = new ServiceController(AgentServer.ServiceName))
            {
                if (sc.Status == ServiceControllerStatus.Running) return;
                if (sc.Status != ServiceControllerStatus.StartPending) sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
        }
    }
}
