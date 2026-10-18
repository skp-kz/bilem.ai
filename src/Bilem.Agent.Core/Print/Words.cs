using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Bilem.Agent.Core.Print
{
    /// <summary>Сумма и дата прописью для печатных форм (русский, тенге/тиын).</summary>
    public static class Words
    {
        private static readonly string[] Months = { "января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря" };
        private static readonly string[] OnesM = { "", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять" };
        private static readonly string[] OnesF = { "", "одна", "две", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять" };
        private static readonly string[] Teens = { "десять", "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать", "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать" };
        private static readonly string[] Tens = { "", "", "двадцать", "тридцать", "сорок", "пятьдесят", "шестьдесят", "семьдесят", "восемьдесят", "девяносто" };
        private static readonly string[] Hundreds = { "", "сто", "двести", "триста", "четыреста", "пятьсот", "шестьсот", "семьсот", "восемьсот", "девятьсот" };
        // (одна, две-четыре, пять+), род
        private static readonly (string[] Forms, bool Fem)[] Scales =
        {
            (new[] { "", "", "" }, false),
            (new[] { "тысяча", "тысячи", "тысяч" }, true),
            (new[] { "миллион", "миллиона", "миллионов" }, false),
            (new[] { "миллиард", "миллиарда", "миллиардов" }, false),
            (new[] { "триллион", "триллиона", "триллионов" }, false),
        };

        /// <summary>18 октября 2026 г.</summary>
        public static string Date(DateTime d) => $"{d.Day} {Months[d.Month - 1]} {d.Year} г.";

        /// <summary>«Пятьдесят тысяч тенге 00 тиын» — с заглавной буквы.</summary>
        public static string Tenge(decimal sum)
        {
            sum = Math.Round(Math.Abs(sum), 2, MidpointRounding.AwayFromZero);
            var whole = (long)Math.Floor(sum);
            var tiyn = (int)Math.Round((sum - whole) * 100);
            var text = Number(whole) + " тенге " + tiyn.ToString("00", CultureInfo.InvariantCulture) + " тиын";
            return char.ToUpper(text[0], new CultureInfo("ru-RU")) + text.Substring(1);
        }

        public static string Number(long n)
        {
            if (n == 0) return "ноль";
            var parts = new List<string>();
            for (var scale = 0; n > 0 && scale < Scales.Length; scale++, n /= 1000)
            {
                var tri = (int)(n % 1000);
                if (tri == 0) continue;
                var words = Triple(tri, Scales[scale].Fem);
                var form = Scales[scale].Forms[Plural(tri)];
                parts.Insert(0, string.IsNullOrEmpty(form) ? words : words + " " + form);
            }
            return string.Join(" ", parts);
        }

        private static string Triple(int n, bool fem)
        {
            var w = new List<string>();
            if (n >= 100) w.Add(Hundreds[n / 100]);
            var r = n % 100;
            if (r >= 10 && r < 20) w.Add(Teens[r - 10]);
            else
            {
                if (r >= 20) w.Add(Tens[r / 10]);
                if (r % 10 > 0) w.Add((fem ? OnesF : OnesM)[r % 10]);
            }
            return string.Join(" ", w.Where(x => x.Length > 0));
        }

        /// <summary>0 — одна, 1 — две-четыре, 2 — пять и больше.</summary>
        private static int Plural(int n)
        {
            var r100 = n % 100;
            if (r100 >= 11 && r100 <= 14) return 2;
            var r = n % 10;
            return r == 1 ? 0 : (r >= 2 && r <= 4 ? 1 : 2);
        }

        /// <summary>Количество прописью для доверенности: целое — «Два»; дробное — цифрами «2,5».</summary>
        public static string QtyWords(decimal v)
        {
            if (v == Math.Floor(v) && v >= 0 && v < 1_000_000_000_000m)
            {
                var t = Number((long)v);
                return char.ToUpper(t[0], new CultureInfo("ru-RU")) + t.Substring(1);
            }
            return Qty(v);
        }

        /// <summary>«Иванов Иван Иванович» → «Иванов И.И.»; уже короткое или одно слово — как есть.</summary>
        public static string ShortName(string fio)
        {
            var p = (fio ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2 || p.Skip(1).Any(x => x.Contains("."))) return string.Join(" ", p);
            return p[0] + " " + string.Concat(p.Skip(1).Take(2).Select(x => char.ToUpper(x[0], new CultureInfo("ru-RU")) + "."));
        }

        /// <summary>50 000,00 — пробел между тысячами, запятая.</summary>
        public static string Money(decimal v) => v.ToString("#,0.00", MoneyFormat);

        /// <summary>Количество без лишних нулей: 1, 2,5.</summary>
        public static string Qty(decimal v) => v.ToString("#,0.###", MoneyFormat);

        private static readonly NumberFormatInfo MoneyFormat = new NumberFormatInfo { NumberGroupSeparator = " ", NumberDecimalSeparator = "," };
    }
}
