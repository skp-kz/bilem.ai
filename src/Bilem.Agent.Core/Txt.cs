using System.Globalization;
using System.Resources;

namespace Bilem.Agent.Core
{
    /// <summary>Все тексты для человека — в одном файле ресурсов Resources\Strings.resx (казахский — потом, отдельным файлом).</summary>
    public static class Txt
    {
        private static readonly ResourceManager Rm =
            new ResourceManager("Bilem.Agent.Core.Resources.Strings", typeof(Txt).Assembly);

        public static string S(string key)
        {
            var s = Rm.GetString(key, CultureInfo.CurrentUICulture);
            return s ?? "[" + key + "]";
        }

        public static string F(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, S(key), args);

        /// <summary>Есть ли ключ в ресурсах (для теста «все ключи на месте»).</summary>
        public static bool Has(string key) => Rm.GetString(key, CultureInfo.InvariantCulture) != null;
    }
}
