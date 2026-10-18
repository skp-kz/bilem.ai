using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace Bilem.Agent.Core.Print
{
    /// <summary>Настройки печатных форм базы (своё у каждой базы — разные юрлица). Картинки — файлами рядом, не в config.json.</summary>
    public sealed class PrintSettings
    {
        /// <summary>«Счёт от имени» — в строке «Исполнитель ___ /…/». Свободный текст (должность или Фамилия И.О.), из 1С не подставляем.</summary>
        public string IssuedBy { get; set; }
        /// <summary>Устарело (до пачки 2): подписант и должность — читаются, если «Счёт от имени» не задан.</summary>
        public string SignerName { get; set; }
        public string SignerTitle { get; set; }
        /// <summary>Адрес поставщика вручную — только переопределение; по умолчанию — из контактной информации организации в 1С.</summary>
        public string AddressOverride { get; set; }

        public string IssuedByEffective =>
            !string.IsNullOrWhiteSpace(IssuedBy) ? IssuedBy.Trim()
            : $"{(string.IsNullOrWhiteSpace(SignerTitle) ? DefaultSignerTitle : SignerTitle.Trim())} {SignerName}".Trim();
        public string HeaderText { get; set; } = DefaultHeader;
        public string Kbe { get; set; } = "17";
        public string Knp { get; set; } = "710";
        /// <summary>Юридический адрес организации (ключ организации → адрес), загружается из 1С кнопкой «Реквизиты из 1С…».</summary>
        public Dictionary<string, string> Addresses { get; set; } = new Dictionary<string, string>();

        // ----- «Общее»: подписанты. Пусто — из 1С («Ответственные лица организаций», загружаются с адресом); своё значение главнее -----
        /// <summary>Руководитель — «Фамилия И.О.» (своё значение вместо 1С).</summary>
        public string Director { get; set; }
        /// <summary>Должность руководителя (своё значение вместо 1С), напр. «Директор».</summary>
        public string DirectorTitle { get; set; }
        /// <summary>Главный бухгалтер — «Фамилия И.О.» (своё значение вместо 1С).</summary>
        public string ChiefAccountant { get; set; }
        /// <summary>Ответственные лица из 1С: ключ организации → руководитель и главный бухгалтер.</summary>
        public Dictionary<string, OrgSigner> Signers { get; set; } = new Dictionary<string, OrgSigner>();

        // ----- Подвкладки форм -----
        public FormParts Invoice { get; set; } = new FormParts();
        /// <summary>Счёт: «Образец платёжного поручения» сверху.</summary>
        public bool InvoicePaymentSample { get; set; } = true;
        /// <summary>Счёт: примечание внизу (пусто по умолчанию).</summary>
        public string InvoiceFooter { get; set; }

        public FormParts PowerOfAttorney { get; set; } = new FormParts();
        /// <summary>Доверенность: срок действия, если в 1С не указан.</summary>
        public int PoaValidDays { get; set; } = 10;
        /// <summary>Доверенность: удостоверение личности из 1С.</summary>
        public bool PoaIdentity { get; set; } = true;

        public FormParts Reconciliation { get; set; } = new FormParts();
        /// <summary>Акт сверки: период по умолчанию — month / quarter / year (прошлый месяц, прошлый квартал, с начала года).</summary>
        public string ActPeriod { get; set; } = "quarter";
        /// <summary>Акт сверки: заполнять половину контрагента зеркально (по умолчанию выкл — контрагент заполняет сам).</summary>
        public bool ActMirror { get; set; }
        /// <summary>Акт сверки: разбивка по договорам, если их несколько.</summary>
        public bool ActByContracts { get; set; } = true;
        /// <summary>Акт сверки: вступление с подстановками {подписант} (должность), {организация}, {фио}, {контрагент}.
        /// В config.json — только своё значение: стандартный текст не сохраняем, чтобы его можно было улучшать в новых версиях.</summary>
        [Newtonsoft.Json.JsonIgnore]
        public string ActIntro
        {
            get => _actIntro ?? DefaultActIntro;
            set => _actIntro = string.IsNullOrWhiteSpace(value) || value.Trim() == DefaultActIntro || value.Trim() == OldActIntro ? null : value.Trim();
        }
        private string _actIntro;
        // Чтение из config.json — через свойство: старый стандартный текст превращается в «пусто = стандартный»
        [Newtonsoft.Json.JsonProperty("ActIntro")] private string ActIntroStored { get => _actIntro; set => ActIntro = value; }
        private const string OldActIntro =
            "Мы, нижеподписавшиеся, {подписант} {организация}, с одной стороны, и ____________________ {контрагент}, с другой стороны, " +
            "составили настоящий акт сверки в том, что состояние взаимных расчётов по данным учёта следующее:";

        public const string DefaultActIntro =
            "Мы, нижеподписавшиеся, {подписант} {организация} {фио}, с одной стороны, и ____________ {контрагент} ____________, с другой стороны, " +
            "составили настоящий акт сверки в том, что состояние взаимных расчётов по данным учёта следующее:";

        /// <summary>Руководитель для подписи: своё значение или из 1С (по организации документа) — («Иванов И.И.», «Директор»); нет — (null, null).</summary>
        public (string Name, string Title) DirectorFor(string orgKey)
        {
            OrgSigner s = null;
            if (orgKey != null) Signers?.TryGetValue(orgKey, out s);
            var name = !string.IsNullOrWhiteSpace(Director) ? Director.Trim() : Words.ShortName(s?.Director?.Name);
            var title = !string.IsNullOrWhiteSpace(DirectorTitle) ? DirectorTitle.Trim() : s?.Director?.Title;
            return (string.IsNullOrWhiteSpace(name) ? null : name, string.IsNullOrWhiteSpace(title) ? null : title.Trim());
        }

        /// <summary>Главный бухгалтер для подписи: своё значение или из 1С; нет — null (на форме «/________/»).</summary>
        public string ChiefAccountantFor(string orgKey)
        {
            if (!string.IsNullOrWhiteSpace(ChiefAccountant)) return ChiefAccountant.Trim();
            OrgSigner s = null;
            if (orgKey != null) Signers?.TryGetValue(orgKey, out s);
            var n = Words.ShortName(s?.ChiefAccountant?.Name);
            return string.IsNullOrWhiteSpace(n) ? null : n;
        }

        public const string DefaultSignerTitle = "Бухгалтер";
        public const string DefaultHeader =
            "Внимание! Оплата данного счета означает согласие с условиями поставки товара. Уведомление об оплате обязательно, " +
            "в противном случае не гарантируется наличие товара на складе. Товар отпускается по факту прихода денег на р/с Поставщика, " +
            "самовывозом, при наличии доверенности и документов удостоверяющих личность.";

        /// <summary>Копия через JSON — все поля, включая подвкладки.</summary>
        public PrintSettings Clone() =>
            Newtonsoft.Json.JsonConvert.DeserializeObject<PrintSettings>(Newtonsoft.Json.JsonConvert.SerializeObject(this));
    }

    /// <summary>Что печатать на форме: логотип, печать, подпись (картинки — общие для базы, на «Общем»).</summary>
    public sealed class FormParts
    {
        public bool Logo { get; set; } = true;
        public bool Stamp { get; set; } = true;
        public bool Sign { get; set; } = true;
    }

    public sealed class Person
    {
        public string Name { get; set; }
        public string Title { get; set; }
    }

    /// <summary>Ответственные лица организации из 1С (ФИО полностью и должность).</summary>
    public sealed class OrgSigner
    {
        public Person Director { get; set; }
        public Person ChiefAccountant { get; set; }

        /// <summary>Ответ помощника COM orgSigners: { "&lt;ключ организации&gt;": { "director": {name,title}, "chief_accountant": {…} } }.</summary>
        public static Dictionary<string, OrgSigner> Parse(Newtonsoft.Json.Linq.JObject o)
        {
            var d = new Dictionary<string, OrgSigner>();
            if (o == null) return d;
            Person P(Newtonsoft.Json.Linq.JToken t) => t == null ? null : new Person { Name = (string)t["name"], Title = (string)t["title"] };
            foreach (var p in o.Properties()) d[p.Name] = new OrgSigner { Director = P(p.Value["director"]), ChiefAccountant = P(p.Value["chief_accountant"]) };
            return d;
        }
    }

    public enum PrintImage { Logo, Stamp, Sign }

    /// <summary>
    /// Картинки печатных форм базы: C:\ProgramData\Bilem\bases\&lt;имя&gt;\print\ (только Администраторы и SYSTEM — печать и подпись чувствительные).
    /// Больше лимита — агент сам ужимает и сообщает об этом.
    /// </summary>
    public static class PrintStore
    {
        public static string Dir(string slug) => Path.Combine(Paths.Home, "bases", slug, "print");
        public static string FileOf(string slug, PrintImage kind) => Path.Combine(Dir(slug), kind.ToString().ToLowerInvariant() + ".png");
        public static string Existing(string slug, PrintImage kind) { var f = FileOf(slug, kind); return File.Exists(f) ? f : null; }

        /// <summary>Лимиты из ТЗ: размер файла и пикселей.</summary>
        public static (long Bytes, int MaxW, int MaxH) Limit(PrintImage kind)
        {
            switch (kind)
            {
                case PrintImage.Logo: return (200 * 1024, 1000, 400);
                case PrintImage.Stamp: return (200 * 1024, 600, 600);
                default: return (100 * 1024, 600, 300);
            }
        }

        /// <summary>Сохранить картинку (PNG): больше лимита — уменьшаем. Возвращает текст «ужато до …» или null.</summary>
        public static string Save(string slug, PrintImage kind, string source)
        {
            var (bytes, maxW, maxH) = Limit(kind);
            Directory.CreateDirectory(Dir(slug));
            Secrets.RestrictDirToAdmins(Dir(slug));
            var dest = FileOf(slug, kind);
            string note = null;
            using (var src = Image.FromFile(source))
            {
                var scale = Math.Min(1.0, Math.Min((double)maxW / src.Width, (double)maxH / src.Height));
                byte[] png = null;
                for (var i = 0; i < 12; i++)
                {
                    var w = Math.Max(16, (int)(src.Width * scale)); var h = Math.Max(16, (int)(src.Height * scale));
                    using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.Clear(Color.Transparent);
                            g.DrawImage(src, 0, 0, w, h);
                        }
                        using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); png = ms.ToArray(); }
                    }
                    if (png.Length <= bytes) break;
                    scale *= 0.8;
                }
                if (scale < 0.999 || new FileInfo(source).Length > bytes)
                    note = Txt.F("PrintImageShrunk", (int)(src.Width * scale), (int)(src.Height * scale), png.Length / 1024);
                File.WriteAllBytes(dest, png);
            }
            Secrets.RestrictToAdmins(dest);
            return note;
        }

        public static void Remove(string slug, PrintImage kind)
        {
            var f = FileOf(slug, kind);
            if (File.Exists(f)) File.Delete(f);
        }

        /// <summary>Чего не хватает базе для полной печатной формы: «логотипа, печати, подписи» или null — всё есть.</summary>
        public static string Missing(string slug)
        {
            var miss = new List<string>();
            if (Existing(slug, PrintImage.Logo) == null) miss.Add(Txt.S("PrintMissLogo"));
            if (Existing(slug, PrintImage.Stamp) == null) miss.Add(Txt.S("PrintMissStamp"));
            if (Existing(slug, PrintImage.Sign) == null) miss.Add(Txt.S("PrintMissSign"));
            return miss.Count == 0 ? null : string.Join(", ", miss);
        }

        /// <summary>«Скопировать из другой базы…»: картинки и настройки (кроме адресов и подписантов из 1С — у другой базы другие организации).</summary>
        public static void Copy(BaseConfig from, BaseConfig to)
        {
            foreach (PrintImage k in Enum.GetValues(typeof(PrintImage)))
            {
                var s = Existing(from.Slug, k);
                if (s == null) { Remove(to.Slug, k); continue; }
                Directory.CreateDirectory(Dir(to.Slug));
                Secrets.RestrictDirToAdmins(Dir(to.Slug));
                File.Copy(s, FileOf(to.Slug, k), true);
                Secrets.RestrictToAdmins(FileOf(to.Slug, k));
            }
            var addresses = to.Print?.Addresses;
            var over = to.Print?.AddressOverride;
            var signers = to.Print?.Signers;
            to.Print = (from.Print ?? new PrintSettings()).Clone();
            to.Print.Addresses = addresses ?? new Dictionary<string, string>();
            to.Print.AddressOverride = over;
            to.Print.Signers = signers ?? new Dictionary<string, OrgSigner>();
        }
    }
}
