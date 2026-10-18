using System.Collections.Generic;
using System.Linq;

namespace Bilem.Agent.Core.Print
{
    public enum ReadyLevel { Ready, Missing, Soon }

    public enum PrintForm { General, Invoice, PowerOfAttorney, Reconciliation }

    /// <summary>
    /// Точка статуса подвкладки «Печатных форм»: зелёная — всё задано, жёлтая — чего-то не хватает (подсказка — чего),
    /// серая — форма скоро. Форма печатается и с жёлтой точкой — просто без недостающего.
    /// </summary>
    public static class PrintReadiness
    {
        public static (ReadyLevel Level, string Hint) Of(BaseConfig b, PrintForm form)
        {
            var ps = b?.Print ?? new PrintSettings();
            var slug = b?.Slug ?? "";
            var miss = new List<string>();
            var org = ps.Addresses?.Keys.FirstOrDefault() ?? ps.Signers?.Keys.FirstOrDefault();
            void Images(FormParts parts, bool all)
            {
                parts = parts ?? new FormParts();
                if ((all || parts.Logo) && PrintStore.Existing(slug, PrintImage.Logo) == null) miss.Add(Txt.S("PrintMissLogo"));
                if ((all || parts.Stamp) && PrintStore.Existing(slug, PrintImage.Stamp) == null) miss.Add(Txt.S("PrintMissStamp"));
                if ((all || parts.Sign) && PrintStore.Existing(slug, PrintImage.Sign) == null) miss.Add(Txt.S("PrintMissSign"));
            }
            bool Director() => ps.DirectorFor(org).Name != null;
            switch (form)
            {
                case PrintForm.General:
                    Images(null, true);
                    if (string.IsNullOrWhiteSpace(ps.AddressOverride) && (ps.Addresses == null || ps.Addresses.Count == 0)) miss.Add(Txt.S("PrintMissAddress"));
                    if (!Director()) miss.Add(Txt.S("PrintMissDirector"));
                    break;
                case PrintForm.Invoice:
                    Images(ps.Invoice, false);
                    if (string.IsNullOrWhiteSpace(ps.IssuedByEffective)) miss.Add(Txt.S("PrintMissIssuedBy"));
                    break;
                case PrintForm.PowerOfAttorney:
                    Images(ps.PowerOfAttorney, false);
                    if (!Director()) miss.Add(Txt.S("PrintMissDirector"));
                    break;
                case PrintForm.Reconciliation:
                    Images(ps.Reconciliation, false);
                    if (!Director()) miss.Add(Txt.S("PrintMissDirector"));
                    break;
            }
            return miss.Count == 0 ? (ReadyLevel.Ready, Txt.S("PrintReadyAll")) : (ReadyLevel.Missing, Txt.F("PrintReadyMissing", string.Join(", ", miss)));
        }
    }
}
