using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland;

/// <summary>
/// The app's own picture, fetched once from the store a person's phone already downloads from, and failing that from
/// the base of icons every browser already keeps.
///
/// A phone never sends a coloured app icon: what it sends is the status-bar cut, and the launcher's real artwork stays
/// on its screen. So the island asks for it itself. It asks the app store first, because what the store hands over is
/// the picture a person knows at the size the launcher draws it; the store's own answer names nothing but an id, and
/// that id is kept in this file's table only where it was looked up and read back. The other base worth asking needs no
/// key, no account and no package name — the address a brand's picture is kept at is the brand's own domain, which the
/// same table holds beside the colour it already knew. Nothing a person reads ever leaves the machine: the words of a
/// notice, the name of an app and the fact that it notified at all stay here, and what goes out is one plain question
/// about a brand that anyone could have asked anyway.
///
/// A picture that arrives is kept under the phone's own folder and never asked for again, so an island that has met
/// Telegram once shows its icon from the next boot onward without a network. And a picture that comes back too small to
/// draw, or drawn for a white page in a colour that would vanish on the island's dark ground, is refused rather than
/// shown, which leaves the app standing on its colour as it did before this file was written.
/// </summary>
static partial class PhoneArt
{
    /// <summary>Said once when a brand's picture arrives, so the page already showing that app can take it. Called off
    /// the UI thread; whoever listens must cross over.</summary>
    public static event Action? Branded;

    static readonly HttpClient Net = new() { Timeout = TimeSpan.FromSeconds(8) };

    // what each label's picture turned out to be, and which ones are being fetched right now. A label that was asked
    // about and answered with nothing worth showing is kept as null rather than forgotten, so it is not asked again
    static readonly Dictionary<string, ImageSource?> Got = new();
    static readonly HashSet<string> Being = new();
    static readonly object Gate = new();

    /// <summary>The app's own coloured picture, or nothing while it is unknown, off the machine, or not worth showing.
    /// Asking for the first time starts the fetch on its own thread and answers nothing at once.</summary>
    public static ImageSource? Brand(string app)
    {
        string key = Key(app);
        if (key.Length == 0) return null;

        string store, domain;
        lock (Gate)
        {
            if (Got.TryGetValue(key, out ImageSource? kept)) return kept;
            if (Being.Contains(key)) return null;
            Being.Add(key);
            store = Store(app);
            domain = Domain(app);
            if (store.Length == 0 && domain.Length == 0)
            {
                Got[key] = null; // a label this file knows no picture of is settled without asking anyone
                Being.Remove(key);
                return null;
            }
        }
        _ = Task.Run(() => Bring(key, store, domain));
        return null;
    }

    static void Bring(string key, string store, string domain)
    {
        // the store is asked first, because what it hands over is the picture a person knows — the app's own, at a size
        // no tile on this island can blur. The web base answers the brands no store id is kept for, and any brand the
        // store is unreachable for tonight.
        ImageSource? picture =
            (store.Length > 0 ? From(key, "@store", () => StoreArtwork(store)) : null)
            ?? (domain.Length > 0 ? From(key, "", () => Favicon(domain)) : null);

        lock (Gate)
        {
            Got[key] = picture;
            Being.Remove(key);
        }
        if (picture != null) Branded?.Invoke();
    }

    /// <summary>One base asked for one kind of picture, read from the file it was kept in so a brand is asked about
    /// once per machine rather than once per boot. A picture that comes back too small to draw, or too dark to see on
    /// the island, is refused and not kept — and a brand answered once is not asked again this run either, so a base
    /// that has nothing good to say costs one question rather than one per notice.</summary>
    static ImageSource? From(string key, string kind, Func<byte[]> fetch)
    {
        string here = Path.Combine(Folder, key + kind + ".png");
        try
        {
            byte[] bytes = File.Exists(here) ? File.ReadAllBytes(here) : fetch();

            // a base answers a question it does not know with a page of its own, and a page is not a picture
            if (bytes.Length is > 96 and < 400_000 && Decoded(bytes) is { } found && Showable(found))
            {
                if (!File.Exists(here)) File.WriteAllBytes(here, bytes); // a run that ends must not forget a whole shelf
                return found;
            }
        }
        catch (Exception ex) // a background fetch must never be able to take the island down with it
        {
            App.Log(ex); // a phone's notices are not held up by a base that is down
        }
        return null;
    }

    static byte[] Favicon(string domain) => Net.GetByteArrayAsync(
        $"https://www.google.com/s2/favicons?domain={domain}&sz=256").GetAwaiter().GetResult();

    /// <summary>The bytes of an app's own picture, out of the one answer a store gives to a name it knows. No reader of
    /// JSON is asked for: the address of the artwork is the only field this file wants, and a search over the answer
    /// finds it without a package. The store keeps the same picture at several sizes and says so in the address, so
    /// the largest is the same question asked of a longer side.</summary>
    static byte[] StoreArtwork(string id)
    {
        string answer = System.Text.Encoding.UTF8.GetString(Net.GetByteArrayAsync(
            $"https://itunes.apple.com/lookup?bundleId={id}").GetAwaiter().GetResult());
        var found = System.Text.RegularExpressions.Regex.Match(answer, @"""artworkUrl100""\s*:\s*""(.*?)""");
        if (!found.Success) throw new InvalidDataException($"the store named no picture for {id}");
        return Net.GetByteArrayAsync(
            found.Groups[1].Value.Replace(@"\/", "/").Replace("100x100bb", "512x512bb")).GetAwaiter().GetResult();
    }

    static string Folder
    {
        get
        {
            string dir = Path.Combine(Bridge.Folder, "brands");
            Directory.CreateDirectory(dir); // a folder of pictures is a folder the phone never asked to have
            return dir;
        }
    }

    static ImageSource? Decoded(byte[] bytes)
    {
        try
        {
            // The size a base really drew at, read from its header before anything is stretched. A site's own scrap is
            // often 23 px wide, and the tile would show it as a blur — no picture at all stands better than a smudge.
            using (var head = new MemoryStream(bytes))
                if (BitmapDecoder.Create(head, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None)
                        .Frames[0].PixelWidth < 64)
                    return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(bytes);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 128; // a 256-side picture is asked for at the size no tile on this island draws it
            image.EndInit();

            // A base answers most brands with a picture built from a palette, and WPF will not let such a picture leave
            // the thread that made it: freezing it is refused, and the UI thread that asks what size it is gets an
            // exception it never sees, because the island's own reading of the pixels gives up and calls the picture
            // showable. What stands on the tile then is nothing at all. So the pixels are copied out here, at the size
            // they were asked for, into a plain picture that belongs to no thread once it is frozen.
            var flat = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int side = flat.PixelWidth, tall = flat.PixelHeight;
            var pixels = new byte[side * tall * 4];
            flat.CopyPixels(pixels, side * 4, 0);
            var kept = BitmapSource.Create(side, tall, flat.DpiX, flat.DpiY, PixelFormats.Bgra32, null, pixels, side * 4);
            kept.Freeze();
            return kept;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
        {
            App.Log(ex);
            return null;
        }
    }
}
