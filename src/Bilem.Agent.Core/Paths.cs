using System;
using System.IO;

namespace Bilem.Agent.Core
{
    /// <summary>Где агент хранит своё состояние. Всё — в C:\ProgramData\Bilem\ (переопределяется BILEM_HOME — для тестов).</summary>
    public static class Paths
    {
        public static string Home
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("BILEM_HOME");
                return string.IsNullOrEmpty(env)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Bilem")
                    : env;
            }
        }

        public static string ConfigFile => Path.Combine(Home, "config.json");
        public static string SecretsDir => Path.Combine(Home, "secrets");
        public static string LogsDir => Path.Combine(Home, "logs");
        public static string StateDir => Path.Combine(Home, "state");

        public static string SecretFile(string slug) => Path.Combine(SecretsDir, slug + ".bilem.bin");
        public static string StateFile(string slug) => Path.Combine(StateDir, slug + ".json");

        /// <summary>Каталог, где лежит сам агент (рядом — помощники COM).</summary>
        public static string AppDir => AppDomain.CurrentDomain.BaseDirectory;

        /// <summary>Системный каталог 64-битной Windows для текущего процесса: из 32-битного System32 подменяется на SysWOW64.</summary>
        public static string NativeSystem32
        {
            get
            {
                var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                return Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess
                    ? Path.Combine(win, "Sysnative")
                    : Path.Combine(win, "System32");
            }
        }

        public static string Wow64System32
        {
            get
            {
                var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                return Environment.Is64BitOperatingSystem ? Path.Combine(win, "SysWOW64") : Path.Combine(win, "System32");
            }
        }

        public static void Ensure()
        {
            Directory.CreateDirectory(Home);
            Directory.CreateDirectory(SecretsDir);
            Directory.CreateDirectory(LogsDir);
            Directory.CreateDirectory(StateDir);
        }
    }
}
