using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Bilem.Agent.Core.Updates
{
    /// <summary>Версия SemVer «X.Y.Z» или «X.Y.Z-beta.N» (тег — с «v» впереди). Выпуск старше любой беты той же X.Y.Z.</summary>
    public sealed class SemVer : IComparable<SemVer>
    {
        private static readonly Regex Rx = new Regex(@"^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+.*)?$");

        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }
        /// <summary>«beta.2» или null — выпуск.</summary>
        public string Pre { get; }
        public bool IsPrerelease => Pre != null;

        private SemVer(int a, int b, int c, string pre) { Major = a; Minor = b; Patch = c; Pre = string.IsNullOrEmpty(pre) ? null : pre; }

        public static SemVer Parse(string s) => TryParse(s, out var v) ? v : throw new FormatException("не версия: " + s);

        public static bool TryParse(string s, out SemVer v)
        {
            v = null;
            var m = Rx.Match((s ?? "").Trim());
            if (!m.Success) return false;
            v = new SemVer(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), m.Groups[4].Success ? m.Groups[4].Value : null);
            return true;
        }

        public int CompareTo(SemVer o)
        {
            if (o == null) return 1;
            var c = Major.CompareTo(o.Major); if (c != 0) return c;
            c = Minor.CompareTo(o.Minor); if (c != 0) return c;
            c = Patch.CompareTo(o.Patch); if (c != 0) return c;
            if (Pre == null) return o.Pre == null ? 0 : 1;
            if (o.Pre == null) return -1;
            // Части пререлиза: числа — как числа, строки — по алфавиту, короче — младше
            var a = Pre.Split('.'); var b = o.Pre.Split('.');
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                var an = int.TryParse(a[i], out var ai); var bn = int.TryParse(b[i], out var bi);
                c = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
                if (c != 0) return c;
            }
            return a.Length.CompareTo(b.Length);
        }

        public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Pre == null ? "" : "-" + Pre);
        public override bool Equals(object obj) => obj is SemVer v && CompareTo(v) == 0;
        public override int GetHashCode() => ToString().GetHashCode();
        public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
        public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;

        /// <summary>Версия этой сборки агента (InformationalVersion: «0.3.0-beta.1»).</summary>
        public static SemVer Current
        {
            get
            {
                var asm = typeof(SemVer).Assembly;
                var info = asm.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false).OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
                return TryParse(info, out var v) ? v : Parse(asm.GetName().Version.ToString(3));
            }
        }
    }
}
