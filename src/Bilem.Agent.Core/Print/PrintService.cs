using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Bilem.Agent.Core.Print
{
    public sealed class PrintResult
    {
        public string Path { get; set; }
        public byte[] Pdf { get; set; }
        public InvoiceData Invoice { get; set; }
        public PoaData Poa { get; set; }
        public ActData Act { get; set; }
    }

    /// <summary>
    /// Счёт на оплату → PDF в «Документы\Bilem\&lt;база&gt;\Счета» (имя: «Счёт №… от … — покупатель.pdf»); папка вида — при первом документе.
    /// Только чтение 1С.
    /// </summary>
    public static class PrintService
    {
        public const string InvoicesFolder = "Счета", PoaFolder = "Доверенности", ActsFolder = "Акты сверки";
        public static string Root => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Bilem");
        public static string OutputDir(string slug, string kind) => System.IO.Path.Combine(Root, slug, kind);

        private static bool _migrated;

        /// <summary>
        /// Старая раскладка v0.2 (все счета в «Документы\Bilem\Счета») → по базам. База одна — переносим к ней;
        /// баз несколько — по имени файла базу не определить, оставляем как есть и пишем в журнал.
        /// </summary>
        internal static void MigrateOld(IReadOnlyList<string> slugs)
        {
            if (_migrated) return;
            _migrated = true;
            try
            {
                var old = System.IO.Path.Combine(Root, InvoicesFolder);
                if (!Directory.Exists(old) || slugs.Contains(InvoicesFolder)) return;
                var files = Directory.GetFiles(old, "*.pdf");
                if (files.Length == 0) return;
                if (slugs.Count != 1) { Log.Warn(Txt.F("PrintOldFolderKept", old, files.Length)); return; }
                var dest = OutputDir(slugs[0], InvoicesFolder);
                Directory.CreateDirectory(dest);
                foreach (var f in files)
                {
                    var to = System.IO.Path.Combine(dest, System.IO.Path.GetFileName(f));
                    if (!File.Exists(to)) File.Move(f, to);
                }
                if (Directory.GetFileSystemEntries(old).Length == 0) Directory.Delete(old);
                Log.Ok(Txt.F("PrintOldFolderMoved", files.Length, dest));
            }
            catch (Exception e) { Log.Warn(Txt.F("PrintOldFolderFailed", e.Message)); }
        }

        public static PrintResult Invoice(BaseConfig b, ODataClient od, string number, string counterparty = null)
        {
            var ps = b.Print ?? new PrintSettings();
            var key = InvoiceSource.Find(od, number, counterparty);
            var inv = InvoiceSource.Load(od, key, ps);
            MigrateOld(AgentConfig.Load().Bases.Select(x => x.Slug).ToList());
            var (logo, stamp, sign) = Images(b.Slug, ps.Invoice);
            var pdf = InvoicePdf.Render(inv, ps, logo, stamp, sign);
            return Save(b.Slug, InvoicesFolder, FileName(inv, b.Slug), new PrintResult { Pdf = pdf, Invoice = inv });
        }

        /// <summary>Доверенность из 1С → PDF в «Документы\Bilem\&lt;база&gt;\Доверенности». Удостоверение личности — только в PDF.</summary>
        public static PrintResult PowerOfAttorney(BaseConfig b, ODataClient od, string number, string employee = null)
        {
            var ps = b.Print ?? new PrintSettings();
            var p = PoaSource.Load(od, PoaSource.Find(od, number, employee), ps);
            var (logo, stamp, sign) = Images(b.Slug, ps.PowerOfAttorney);
            var pdf = PoaPdf.Render(p, ps, logo, stamp, sign);
            var name = $"Доверенность №{p.PrintNumber} от {p.Date:dd.MM.yyyy} — {p.PersonName ?? "без ФИО"} ({b.Slug})";
            return Save(b.Slug, PoaFolder, Clean(name), new PrintResult { Pdf = pdf, Poa = p });
        }

        /// <summary>Акт сверки по регистру бухгалтерии → PDF в «Документы\Bilem\&lt;база&gt;\Акты сверки».</summary>
        public static PrintResult Reconciliation(BaseConfig b, ODataClient od, string counterparty, DateTime from, DateTime to, string contract = null, string organization = null)
        {
            var ps = b.Print ?? new PrintSettings();
            var cat = Access.CatalogOf(b);
            var act = ReconciliationSource.Load(od, Profiles.For(b.Configuration), ps, counterparty, from, to, contract, organization,
                set => cat.Items.FirstOrDefault(i => ODataClient.SetName(i.FullName) == set)?.Title);
            var (logo, stamp, sign) = Images(b.Slug, ps.Reconciliation);
            var pdf = ReconciliationPdf.Render(act, ps, logo, stamp, sign);
            var name = $"Акт сверки — {ShortBuyer(act.CounterpartyShort ?? act.Counterparty)} — {ReconciliationSource.Period(act.From, act.To)} ({b.Slug})";
            return Save(b.Slug, ActsFolder, Clean(name), new PrintResult { Pdf = pdf, Act = act });
        }

        /// <summary>Картинки базы, которые печатать на этой форме (галочки подвкладки).</summary>
        public static (string Logo, string Stamp, string Sign) Images(string slug, FormParts parts)
        {
            parts = parts ?? new FormParts();
            return (parts.Logo ? PrintStore.Existing(slug, PrintImage.Logo) : null,
                    parts.Stamp ? PrintStore.Existing(slug, PrintImage.Stamp) : null,
                    parts.Sign ? PrintStore.Existing(slug, PrintImage.Sign) : null);
        }

        private static PrintResult Save(string slug, string folder, string fileName, PrintResult r)
        {
            var dir = OutputDir(slug, folder);
            Directory.CreateDirectory(dir);
            r.Path = System.IO.Path.Combine(dir, fileName);
            File.WriteAllBytes(r.Path, r.Pdf);
            return r;
        }

        /// <summary>Имя файла без запрещённых символов, не длиннее 160, с «.pdf».</summary>
        internal static string Clean(string name)
        {
            var bad = System.IO.Path.GetInvalidFileNameChars();
            name = new string(name.Select(c => bad.Contains(c) ? ' ' : c).ToArray()).Replace("  ", " ").Trim();
            if (name.Length > 160) name = name.Substring(0, 160).Trim();
            return name + ".pdf";
        }

        /// <summary>«Счёт №4 от 13.04.2022 — ТОО Пример (demo).pdf» — с базой: у разных баз одинаковые номера.</summary>
        public static string FileName(InvoiceData inv, string slug)
        {
            return Clean($"Счёт №{inv.PrintNumber} от {inv.Date:dd.MM.yyyy} — {ShortBuyer(inv.BuyerName)} ({slug})");
        }

        /// <summary>«Товарищество с ограниченной ответственностью "Пример"» → «ТОО Пример» — для имени файла.</summary>
        public static string ShortBuyer(string s)
        {
            s = (s ?? "").Trim();
            foreach (var (full, abbr) in new[]
            {
                ("Товарищество с ограниченной ответственностью", "ТОО"), ("Акционерное общество", "АО"),
                ("Индивидуальный предприниматель", "ИП"), ("Общество с ограниченной ответственностью", "ООО"),
            })
                if (s.StartsWith(full, StringComparison.OrdinalIgnoreCase)) s = abbr + s.Substring(full.Length);
            foreach (var q in new[] { "\"", "«", "»", "“", "”", "„" }) s = s.Replace(q, "");
            return s.Trim();
        }
    }
}
