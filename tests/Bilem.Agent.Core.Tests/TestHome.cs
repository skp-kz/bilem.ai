using System;
using System.IO;

namespace System.Runtime.CompilerServices
{
    // В .NET Framework 4.8 нет атрибута инициализатора модуля — компилятору достаточно, чтобы тип существовал
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}

namespace Bilem.Agent.Core.Tests
{
    internal static class TestHome
    {
        /// <summary>Тесты не трогают настоящий C:\ProgramData\Bilem: журналы и конфиг — во временной папке.</summary>
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Init()
        {
            var dir = Path.Combine(Path.GetTempPath(), "bilem-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("BILEM_HOME", dir);
        }
    }
}
