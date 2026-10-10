using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland;

/// <summary>
/// The colour a phone's picture is given once it stands on the island, and the way any picture is set into its tile.
///
/// A notice arrives with the icon the phone shows in its own status bar, and that icon has no colour in it: measured
/// over the three pictures a real vivo sent, every opaque pixel of them is white or its own anti-aliasing, the whole
/// drawing being a silhouette cut in alpha. The colour an app's icon really has lives in the launcher, which no packet
/// of this family names — the notice carries `appName`, the label a person reads, and never the package an icon store
/// would be looked up by. So the island brings the colour itself: the app's own brand where its label is known, and
/// where it is not, a hue the label always lands on, so each app keeps one colour of its own across notices, phones
/// and restarts.
///
/// A picture that arrives already coloured — a contact's photograph, a preview of a sent image, which the app prefers
/// over its silhouette when the notice carries one — is left exactly as it came: nothing is done to it but showing it.
///
/// Whatever the picture is, it is drawn by its own ink rather than by its canvas. A phone draws its mark inside a field
/// of clear air and does not promise that the mark sits in the middle of it, so scaling the canvas centres the air and
/// leaves the sign to one side. One walk over the pixels answers all three questions at once: whether this is a cut,
/// whether it can be seen on the island's own dark ground, and where its ink is.
/// </summary>
static partial class PhoneArt
{
    /// <summary>A table keeps its answers beside the picture itself and lets them go with it, which a dictionary of
    /// image sources would not: it would hold every picture a phone ever sent alive forever.</summary>
    sealed record Verdict(bool Cut, bool Showable, ImageSource Framed);

    static readonly ConditionalWeakTable<ImageSource, Verdict> Read = new();

    /// <summary>Whether these bytes are a cut in alpha rather than a picture, which decides whether a tile goes behind
    /// them. Answered once per picture and remembered, since a page redraws its rows more often than a phone sends.</summary>
    public static bool Silhouette(ImageSource art) => Looked(art).Cut;

    /// <summary>Whether a picture of its own stands on the island's dark ground: enough colour in it to be a brand, and
    /// enough light to be seen. A black mark drawn for a white page is a picture that shows nothing here.</summary>
    public static bool Showable(ImageSource art) => Looked(art).Showable;

    /// <summary>The picture trimmed to its own ink and squared about it, so a tile centres what a person looks at
    /// instead of the empty field a phone drew it in.</summary>
    public static ImageSource Framed(ImageSource art) => Looked(art).Framed;

    static Verdict Looked(ImageSource art) => Read.GetValue(art, static one => Weighed(one));

    static Verdict Weighed(ImageSource art)
    {
        // only a picture of pixels can be looked at; a drawing that arrives as vectors keeps whatever it has
        if (art is not BitmapSource picture || picture.PixelWidth < 3 || picture.PixelHeight < 3)
            return new Verdict(false, true, art);
        try
        {
            if (!picture.IsFrozen) picture.Freeze(); // the converter below will not read a picture still being built
            var source = picture.Format == PixelFormats.Bgra32
                ? picture
                : new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
            int w = source.PixelWidth, h = source.PixelHeight;
            var bytes = new byte[w * h * 4];
            source.CopyPixels(bytes, w * 4, 0);

            // a logo drawn for a white page is cut out of that page, since a white square is the one thing this island
            // never shows. The field goes from its edges inward, so a white mark standing inside its own brand — a plane
            // in a blue disc — keeps its cloth, and only the field a person never sees on a phone is taken.
            if (Hollowed(bytes, w, h) is { } field)
            {
                bytes = field;
                var bare = BitmapSource.Create(w, h, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, field, w * 4);
                bare.Freeze();
                source = bare; // the crop below cuts a picture, not the bytes it came from
            }

            int seen = 0, lit = 0, spread = 0;
            long massX = 0, massY = 0;
            int left = w, top = h, right = -1, bottom = -1;
            for (int at = 0; at + 3 < bytes.Length; at += 4)
            {
                if (bytes[at + 3] < 64) continue; // nothing drawn here, and nothing to say about it
                int here = at / 4;
                int x = here % w, y = here / w;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
                massX += x; massY += y;

                seen++;
                byte blue = bytes[at], green = bytes[at + 1], red = bytes[at + 2];
                spread = Math.Max(spread, Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)));
                lit += (red + green + blue) / 3;
            }
            if (seen == 0) return new Verdict(false, false, art);

            double mean = lit / (double)seen; // the brightness of what is drawn here, on a scale of 0 to 255
            // and only a light cut stands on a coloured tile: a dark silhouette would vanish into the brand behind it,
            // which is how it looked before this file was written, and better than an invisible mark
            bool cut = spread <= 30 && mean >= 100;
            return new Verdict(cut, spread >= 60 && mean / 255 >= 0.18,
                Squared(source, w, h, left, top, right, bottom, massX / (double)seen, massY / (double)seen));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            App.Log(ex);
            return new Verdict(false, true, art);
        }
    }

    /// <summary>The white field a web logo was drawn on, taken away: null when there was nothing of the kind to take,
    /// otherwise the same pixels with the field turned to clear air. A picture whose four corners are that white is
    /// walked from its edges inward, so only the field a mark stands on goes; a white cloth inside a brand's own shape
    /// is part of the mark and is never reached.</summary>
    static byte[]? Hollowed(byte[] bytes, int w, int h)
    {
        if (!(Pale(bytes, 0) && Pale(bytes, w - 1) && Pale(bytes, (h - 1) * w) && Pale(bytes, w * h - 1))) return null;

        var gone = new bool[w * h];
        var queue = new Queue<int>();
        void Loosen(int at)
        {
            if (gone[at] || !Washable(bytes, at)) return;
            gone[at] = true;
            queue.Enqueue(at);
        }
        for (int x = 0; x < w; x++) { Loosen(x); Loosen((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { Loosen(y * w); Loosen(y * w + w - 1); }
        while (queue.Count > 0)
        {
            int at = queue.Dequeue(), x = at % w, y = at / w;
            if (x > 0) Loosen(at - 1);
            if (x + 1 < w) Loosen(at + 1);
            if (y > 0) Loosen(at - w);
            if (y + 1 < h) Loosen(at + w);
        }

        int eaten = 0;
        for (int at = 0; at < gone.Length; at++)
            if (gone[at]) { bytes[at * 4 + 3] = 0; eaten++; }
        return eaten > 0 ? bytes : null;
    }

    /// <summary>A corner of the field, before anything is washed.</summary>
    static bool Pale(byte[] bytes, int at)
    {
        int p = at * 4;
        return bytes[p + 3] > 200 && bytes[p] >= 235 && bytes[p + 1] >= 235 && bytes[p + 2] >= 235;
    }

    /// <summary>Whether the wash reaches this pixel: clear air already is, and near-white is reached only along the way
    /// from the field, which leaves the anti-aliased edge of a mark behind as air rather than as a white halo.</summary>
    static bool Washable(byte[] bytes, int at)
    {
        int p = at * 4;
        return bytes[p + 3] < 64 || (bytes[p] >= 210 && bytes[p + 1] >= 210 && bytes[p + 2] >= 210);
    }

    /// <summary>The crop that makes the ink its own square, or the picture untouched when its ink already fills it.
    /// The square is placed with the ink's own weight in its middle rather than its bounding box, since a mark is
    /// seen by where it is heavy: a plane drawn pointing away from itself carries more cloth on one side, and a tile
    /// centred on the box it fits in still reads as leaning. Where the canvas leaves no room to slide, the box is
    /// kept whole and the picture stands as it came.</summary>
    static ImageSource Squared(BitmapSource source, int w, int h, int left, int top, int right, int bottom,
        double weightX, double weightY)
    {
        int inkW = right - left + 1, inkH = bottom - top + 1;
        double side = Math.Max(inkW, inkH) * 1.06; // a hair of air, so a mark never touches the corner of its tile
        if (side >= Math.Min(w, h) * 0.98) return source; // there is nothing to trim away

        int taken = (int)Math.Min(side, Math.Min(w, h));
        int x = Slide(weightX - taken / 2.0, left, right, taken, w);
        int y = Slide(weightY - taken / 2.0, top, bottom, taken, h);
        try
        {
            var cut = new CroppedBitmap(source, new Int32Rect(x, y, taken, taken));
            cut.Freeze();
            return cut;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            App.Log(ex);
            return source;
        }
    }

    /// <summary>Where a square window of `taken` begins, as close to the wish as the ink and the canvas allow.</summary>
    static int Slide(double wish, int first, int last, int taken, int canvas)
    {
        int low = Math.Max(0, Math.Min(last + 1 - taken, canvas - taken)); // the window keeps the ink whole…
        int high = Math.Min(canvas - taken, first); // …and stays on the canvas
        return Clamp((int)Math.Round(wish), low, Math.Max(low, high));
    }

    static int Clamp(int value, int low, int high) => Math.Max(low, Math.Min(high, value));

    /// <summary>The colour drawn behind an app's silhouette.</summary>
    public static Color Of(string app)
    {
        string key = Key(app);
        foreach ((string name, Color color, string _) in Brands)
        {
            // a label beginning with the brand is that brand, and a longer name found anywhere in the label catches the
            // brands a vendor prefixes («Microsoft Teams»); a short name like «vk» is trusted only at the front, where
            // it cannot hide inside another word
            if (Matches(key, name)) return Lifted(color);
        }
        return Lifted(Own(key));
    }

    /// <summary>The address a brand's picture is kept at, for the few whose label is known and whose picture is
    /// worth having. Empty where no base holds a picture that can be seen on the island. A bare host, never a URL:
    /// it is put into the one question this file asks, and a path or a query in it would ask something else.</summary>
    internal static string Domain(string app)
    {
        string key = Key(app);
        foreach ((string name, Color _, string domain) in Brands)
            if (Matches(key, name)) return domain;
        return "";
    }

    /// <summary>The id a brand's real app picture is kept under in the store that hands it out to anyone and needs no
    /// key for the asking. A site's own picture is a scrap: measured over the brands this table knows, nine of them
    /// answer with a drawing of 48 px or less beside the tile that would show it, and five more draw theirs on a white
    /// page. Where the store knows the app it is asked first, and the web base is left to answer the rest.
    ///
    /// Only ids this file has looked up and read back are listed: a name searched rather than named once brought a
    /// third-party VK client in place of the app itself, which is why no brand is fetched by guessing here.</summary>
    internal static string Store(string app)
    {
        string key = Key(app);
        foreach ((string name, string id) in Stores)
            if (Matches(key, name)) return id;
        return "";
    }

    static readonly (string Name, string Store)[] Stores =
    [
        ("telegram", "ph.telegra.Telegraph"), ("телеграм", "ph.telegra.Telegraph"),
        ("whatsapp", "net.whatsapp.WhatsApp"), ("ватсап", "net.whatsapp.WhatsApp"),
        ("gmail", "com.google.Gmail"), ("slack", "com.tinyspeck.chatlyio"),
        ("spotify", "com.spotify.client"), ("спотифай", "com.spotify.client"),
        ("twitch", "tv.twitch"), ("wildberries", "RU.WILDBERRIES.MOBILEAPP"),
        ("вайлдбер", "RU.WILDBERRIES.MOBILEAPP"), ("яндекс", "ru.yandex.mobile"),
        ("zoom", "us.zoom.videomeetings"), ("opera", "com.opera.OperaTouch"),
        ("опера", "com.opera.OperaTouch"),
    ];

    static bool Matches(string key, string name) =>
        key.StartsWith(name, StringComparison.Ordinal)
        || (name.Length >= 5 && key.Contains(name, StringComparison.Ordinal));

    /// <summary>A brand's own colour and the address of its own picture, by the label a phone puts on its notices.
    /// Compared in lower case and without the spaces a label may or may not carry.</summary>
    static readonly (string Name, Color Color, string Domain)[] Brands =
    [
        ("telegram", Hex("#2AABEE"), "telegram.org"), ("телеграм", Hex("#2AABEE"), "telegram.org"),
        ("whatsapp", Hex("#25D366"), "whatsapp.com"), ("ватсап", Hex("#25D366"), "whatsapp.com"),
        ("viber", Hex("#7360F2"), "viber.com"), ("вайбер", Hex("#7360F2"), "viber.com"),
        ("discord", Hex("#5865F2"), "discord.com"), ("дискорд", Hex("#5865F2"), "discord.com"),
        ("vk", Hex("#0077FF"), "vk.com"), ("вконтакте", Hex("#0077FF"), "vk.com"),
        ("одноклассники", Hex("#EE8208"), "ok.ru"),
        ("instagram", Hex("#E1306C"), "instagram.com"), ("инстаграм", Hex("#E1306C"), "instagram.com"),
        ("youtube", Hex("#FF3333"), "youtube.com"), ("ютуб", Hex("#FF3333"), "youtube.com"),
        ("gmail", Hex("#EA4335"), "mail.google.com"), ("google", Hex("#4285F4"), "google.com"),
        ("хром", Hex("#4285F4"), "chrome.com"), ("chrome", Hex("#4285F4"), "chrome.com"),
        ("play", Hex("#00C378"), "play.google.com"),
        ("spotify", Hex("#1DB954"), "spotify.com"), ("спотифай", Hex("#1DB954"), "spotify.com"),
        ("netflix", Hex("#E50914"), "netflix.com"), ("нетфликс", Hex("#E50914"), "netflix.com"),
        ("steam", Hex("#66C0F4"), "store.steampowered.com"), ("slack", Hex("#E01E5A"), "slack.com"),
        ("twitch", Hex("#9146FF"), "twitch.tv"),
        ("teams", Hex("#6264A7"), "teams.microsoft.com"), ("zoom", Hex("#2D8CFF"), "zoom.us"),
        ("messenger", Hex("#0084FF"), "messenger.com"), ("facebook", Hex("#1877F2"), "facebook.com"),
        ("linkedin", Hex("#0A66C2"), "linkedin.com"), ("pinterest", Hex("#E60023"), "pinterest.com"),
        ("tiktok", Hex("#FE2C55"), ""), ("твиттер", Hex("#1DA1F2"), ""),
        ("signal", Hex("#3A76F4"), "signal.org"), ("опера", Hex("#FF1B2D"), "opera.com"),
        ("opera", Hex("#FF1B2D"), "opera.com"), ("vivaldi", Hex("#EF3939"), "vivaldi.com"),
        ("brave", Hex("#FB542B"), "brave.com"), ("edge", Hex("#0F7EBB"), "microsoft.com"),
        ("ozon", Hex("#005BFF"), "ozon.ru"), ("wildberries", Hex("#CB11AB"), "wildberries.ru"),
        ("вайлдбер", Hex("#CB11AB"), "wildberries.ru"), ("aliexpress", Hex("#FF4747"), "aliexpress.com"),
        ("rutube", Hex("#7B5FF0"), "rutube.ru"), ("2гис", Hex("#D32921"), "2gis.ru"),
        ("госуслуги", Hex("#0D4CD4"), "gosuslugi.ru"), ("макс", Hex("#3C3CFF"), "max.ru"),
        ("сбер", Hex("#21A038"), "sberbank.ru"), ("яндекс", Hex("#FC3F1D"), "yandex.ru"),
        ("авито", Hex("#00AAFF"), "avito.ru"), ("avito", Hex("#00AAFF"), "avito.ru"),
        ("озон", Hex("#005BFF"), "ozon.ru"),
        ("почта", Hex("#0A84FF"), "mail.ru"), ("mail", Hex("#0A84FF"), "mail.ru"),
        ("сообщения", Hex("#2AB5A5"), ""), ("messages", Hex("#2AB5A5"), ""),
        ("телефон", Hex("#34C759"), ""), ("phone", Hex("#34C759"), ""),
        ("календарь", Hex("#FF7043"), ""), ("calendar", Hex("#FF7043"), ""),
    ];

    static string Key(string app)
    {
        var kept = new System.Text.StringBuilder(app.Length);
        foreach (char c in app)
            if (char.IsLetterOrDigit(c)) kept.Append(char.ToLowerInvariant(c));
        return kept.ToString();
    }

    /// <summary>The colour an app of this name always gets: a hue read off its own name, kept in the part of the wheel
    /// that stands on a dark island without shouting.</summary>
    static Color Own(string key)
    {
        uint hash = 2166136261;
        foreach (char c in key) hash = (hash ^ c) * 16777619;
        double hue = hash % 360 * 1.0;
        return FromHue(hue, 0.62, 0.92);
    }

    static Color FromHue(double hue, double saturation, double value)
    {
        int sector = (int)(hue / 60) % 6;
        double f = hue / 60 - Math.Floor(hue / 60);
        // the three sides of this sector's wedge, as fractions of the brightest channel: the full drop, the one leaving
        // it, and the one arriving
        double full = 1 - saturation, leaving = 1 - saturation * f, arriving = 1 - saturation * (1 - f);
        (double r, double g, double b) = sector switch
        {
            0 => (1.0, arriving, full),
            1 => (leaving, 1.0, full),
            2 => (full, 1.0, arriving),
            3 => (full, leaving, 1.0),
            4 => (arriving, full, 1.0),
            _ => (1.0, full, leaving),
        };
        return Color.FromRgb((byte)(r * value * 255), (byte)(g * value * 255), (byte)(b * value * 255));
    }

    /// <summary>A brand too dark to read against the island's own black is lifted toward white until it can be, keeping
    /// its hue — aubergine and midnight blue are still their apps' colours when they are seen.</summary>
    static Color Lifted(Color color)
    {
        for (int n = 0; n < 8 && Luma(color) < 0.28; n++)
            color = Color.FromArgb(color.A, (byte)(color.R + (255 - color.R) / 3), (byte)(color.G + (255 - color.G) / 3),
                (byte)(color.B + (255 - color.B) / 3));
        return color;
    }

    static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    static Color Hex(string text) => (Color)ColorConverter.ConvertFromString(text);
}
