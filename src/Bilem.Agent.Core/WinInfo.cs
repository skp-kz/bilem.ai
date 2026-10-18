using Microsoft.Win32;

namespace Bilem.Agent.Core
{
    /// <summary>Сведения о Windows, которые меняют поведение мастера.</summary>
    public static class WinInfo
    {
        /// <summary>
        /// Контроллер домена: ProductType = LanmanNt (то же, что Win32_ComputerSystem.DomainRole ≥ 4).
        /// Microsoft не рекомендует ставить IIS на контроллер домена; у ApplicationPoolIdentity там нет локальной учётки.
        /// </summary>
        public static bool IsDomainController()
        {
            try
            {
                using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions"))
                    return string.Equals(k?.GetValue("ProductType") as string, "LanmanNt", System.StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
