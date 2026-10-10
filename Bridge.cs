using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DynamicIsland;

/// <summary>
/// The island's door to the phone that sits beside the computer on the same network. The door has two faces: a page
/// opened from a QR code, which any browser on any phone reads, and the KDE Connect protocol, which an Android phone
/// already speaking that protocol reaches without a browser at all. Both faces hand the same three things inward — a
/// saying, a file, and the phone's battery — and this class holds what they share: the address the phone must come to,
/// the sign that tells a stranger from the owner's own phone, and the folder where what the phone brings is laid down.
/// </summary>
sealed partial class Bridge
{
    public const int Port = 8484;

    /// <summary>How much a phone may send at once. More than this is either a mistake or something else.</summary>
    public const long LargestUpload = 64L * 1024 * 1024;

    /// <summary>
    /// Raised for a saying the phone pushed, a file laid in the folder, a phone that came to the door, and the
    /// battery it reported. Each is raised on a worker thread; the island decides how to be shown.
    /// </summary>
    public event Action<string>? Said;
    public event Action<string>? Laid;
    public event Action<string>? Visited;
    public event Action<string, int, bool>? Battery;

    /// <summary>
    /// A notice the phone holds, and the key of one the phone has taken away. The first is raised for a notice that
    /// has just appeared and for one the phone has changed in place; the second for the moment it stopped holding it.
    /// </summary>
    public event Action<Notice>? Noticed;
    public event Action<string>? NoticeGone;

    /// <summary>
    /// A phone whose channel has ended: everything it kept here should go with it. The sign it came in under is what
    /// numbers its notices, and the name it called itself is what the page shows and what its charge is kept under —
    /// one phone is known by both, and a leaving that carries only one of them cannot be matched to the other.
    /// </summary>
    public event Action<string, string>? PhoneLeft;

    /// <summary>
    /// The phone's word about this machine's music, and this machine's music back at the phone. The first is raised for
    /// a button the phone pressed on its own screen and for the place in the song it slid to; the second carries what
    /// every phone is told when it asks, or when the island notices the music has moved. Both are raised away from the
    /// UI thread, since the KDE channel is one thread of its own, and the island marshals what it does about them.
    /// </summary>
    public event Action<Command>? Ordered;

    /// <summary>
    /// A phone that asked to be trusted: the name it calls itself and the two fingerprints the owner compares. Nothing
    /// is let in until the owner answers on the page.
    /// </summary>
    public event Action<Asking>? Wanted;

    /// <summary>A pair request that left the page — answered, or gone with the phone's channel.</summary>
    public event Action<string, bool>? Answered;

    /// <summary>The machine has rejoined the network and the address a phone must come to is another one now.</summary>
    public event Action? Moved;

    /// <summary>
    /// What stands at the KDE door and waits: a name, the fingerprints of both sides of it, and the eight signs the
    /// phone itself shows while the pair is being agreed to. Empty signs mean the phone asked in the older tongue,
    /// where only the fingerprints could be compared.
    /// </summary>
    public sealed record Asking(string Phone, string Theirs, string Ours, string Code = "");

    /// <summary>
    /// What the phone asked the island to do with this machine's music. `What` is the island's own short word for it:
    /// a button ("toggle", "play", "pause", "stop", "next", "previous"), or one of the three ways a song is moved
    /// ("seek" to the moment `At` counts in seconds, "skip" across `At` of them, "volume" to the `At` part of the
    /// loudest the endpoint will go). The phone's own words are longer and belong on its side of the channel.
    /// </summary>
    public sealed record Command(string What, double At = 0);

    /// <summary>
    /// The music this machine is playing, in the shape every phone is told about. `LengthMs` is negative when the song
    /// has no end the island can see — a live stream, a radio — which is the one answer a phone's slider can be built
    /// around; `Seekable` says whether moving the song is offered at all. `Volume` is the hundredths the machine itself
    /// is at, so the phone's slider starts where the real one stands.
    /// </summary>
    public sealed record Playing(string Title, string Artist, bool IsPlaying, long PositionMs, long LengthMs,
        bool Seekable, int Volume);

    /// <summary>
    /// A notice as the phone holds it, in the phone's own words: the app that made it, its title and its saying, the
    /// buttons it carries, and the three promises the island could not guess — that the notice can be taken away, that
    /// an answer can be sent back to it, and that it was already there before this channel opened. The last is why a
    /// phone that has just reconnected hands over its whole shelf at once and the island says nothing about it.
    /// `Id` names a notice to this island alone: two phones hold notices their apps numbered the same way, so the
    /// phone's own sign leads it, and what comes back from the page is that whole key.
    /// `Art` is the app's own picture, in the bytes the phone chose to send with it — a notice from an app whose
    /// picture this island has already been given arrives with the same count over the same bytes, and the picture is
    /// then taken from what was kept rather than sent twice.
    /// </summary>
    public sealed record Notice(string Id, string Phone, string App, string Title, string Text,
        string[] Actions, string ReplyId, bool Clearable, bool Silent, long Time, byte[]? Art = null)
    {
        /// <summary>What the phone's own corner would have said: the app, then the title it gave.</summary>
        public string Heading => string.Join(" · ", new[] { App, Title }.Where(s => s.Length > 0));

        /// <summary>An app that hides its contents still leaves its name, so a row is never blank.</summary>
        public string Saying => Text.Length > 0 ? Text : Title.Length > 0 ? Title : App;

        /// <summary>The moment the phone put it up, as a person reads a clock.</summary>
        public string Clock => Time <= 0 ? ""
            : DateTimeOffset.FromUnixTimeMilliseconds(Time).LocalDateTime.ToString("HH:mm");
    }

    readonly CancellationTokenSource _shutdown = new();
    Task? _faces;
    BridgeKde? _kde;
    string _address = "";

    public Bridge()
    {
        if (Settings.BridgeKey.Length == 0) Settings.BridgeKey = NewKey();
    }

    /// <summary>The few signs that tell the owner's page from a stranger's. It does not change between runs.</summary>
    public string Key => Settings.BridgeKey;

    /// <summary>The address a phone would come to, or nothing when the machine has no usable network.</summary>
    public string Address => _address;

    /// <summary>The whole address of the page, QR code ready.</summary>
    public string Url => _address.Length == 0 ? "" : $"http://{_address}:{Port}/?k={Key}";

    /// <summary>Words the phone left, taken as they came in.</summary>
    public void Take(string text) => Said?.Invoke(text);

    /// <summary>A file the phone sent: laid down first, so that what is handed inward is a path that stands.</summary>
    public void Take(string name, string contentType, byte[] bytes)
    {
        string path = Write(name, contentType, bytes);
        if (path.Length > 0) Laid?.Invoke(path);
    }

    /// <summary>A phone that reached the door, by whatever name it calls itself.</summary>
    public void Visit(string remote) => Visited?.Invoke(remote);

    /// <summary>The battery a phone reported, in hundredths, and whether the phone says it is on charge.</summary>
    public void Report(string device, int level, bool charging) => Battery?.Invoke(device, level, charging);

    /// <summary>
    /// A notice the phone holds, by the key this island gives it. A notice the phone already held is said again rather
    /// than added twice, so a phone that reconnects and hands its shelf over is not a phone that doubled everything.
    /// </summary>
    public void Bring(Notice notice) => Noticed?.Invoke(notice);

    /// <summary>The phone took a notice away; the key says which.</summary>
    public void Took(string key) => NoticeGone?.Invoke(key);

    /// <summary>The phone itself has gone off the channel, with all its notices still lying here.</summary>
    public void Gone(string phoneId, string device) => PhoneLeft?.Invoke(phoneId, device);

    /// <summary>Ask the phone again what it is holding, as the family asks the moment a channel settles.</summary>
    public void AskNotices() => To(() => _kde?.AskNotices());

    /// <summary>Take a notice off the phone's own screen.</summary>
    public void DismissNotice(string key) => To(() => _kde?.Dismiss(key));

    /// <summary>Press one of the buttons the phone put under its notice.</summary>
    public void PressNotice(string key, string action) => To(() => _kde?.Press(key, action));

    /// <summary>Answer a notice with words the island holds; the phone decides whether a notice can be answered.</summary>
    public void AnswerNotice(string key, string message) => To(() => _kde?.Reply(key, message));

    /// <summary>
    /// Say something on the phone's own screen, as the island says it here. Nothing is shown for it when the phone has
    /// not switched on the plugin that receives notices, which is off until its owner turns it on.
    /// </summary>
    public void Announce(string app, string text) => To(() => _kde?.Announce(app, text));

    /// <summary>Give this machine's copied words to every phone standing on the channel.</summary>
    public void Pass(string text) => To(() => _kde?.Pass(text));

    /// <summary>The phone said something about this machine's music; the island decides what it means.</summary>
    public void Order(Command order) => Ordered?.Invoke(order);

    /// <summary>Say this machine's music to every phone that stands talking, as the island notices it has moved.</summary>
    public void Mirror(Playing state) => To(() => _kde?.Mirror(state));

    /// <summary>The page hands a word inward; a face that is not open takes it nowhere and says nothing of it.</summary>
    static void To(Action way)
    {
        try { way(); }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>A phone that wants to be trusted, and the fingerprints to compare before it is.</summary>
    public void Ask(Asking asking) => Wanted?.Invoke(asking);

    /// <summary>The owner's answer, given on the page. The KDE face keeps or shuts the door.</summary>
    public void AnswerPair(bool yes)
    {
        try { _kde?.Answer(yes); }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>A pair request that is no longer waiting, and whether it ended in trust.</summary>
    public void Answer(string device, bool paired) => Answered?.Invoke(device, paired);

    /// <summary>Opens the door: the page first, and the protocol beside it when the phone is a KDE Connect one.</summary>
    public void Start()
    {
        _address = LocalAddress();
        // a network that is rejoined under the running island changes the address on the QR page and the shout alike
        NetworkChange.NetworkAddressChanged += (_, _) => Rejoin();
        _faces = Task.Run(() => RunFaces(_shutdown.Token), _shutdown.Token);
    }

    /// <summary>Takes the new address when the machine moves, and says so once if it really has moved.</summary>
    void Rejoin()
    {
        string now = LocalAddress();
        if (now.Length == 0 || now == _address) return;
        _address = now;
        try { Moved?.Invoke(); } catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>Closes the door and lets the listeners go.</summary>
    public void Stop()
    {
        try { _shutdown.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    async Task RunFaces(CancellationToken token)
    {
        var tasks = new List<Task> { new BridgeHttp(this).RunAsync(token) };
        if (Settings.BridgeKde)
        {
            var kde = new BridgeKde(this);
            _kde = kde; // the page answers through this instance, so it has to be reachable from it
            tasks.Add(kde.RunAsync(token));
        }
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>
    /// The files standing on the owner's shelf, in the order the shelf draws them. A shelf keeps paths rather than
    /// copies, so this is the island's own list — less the gone ones, and less the folders, since a folder is nothing
    /// a phone can take.
    /// </summary>
    public static List<string> OnShelf() => Settings.Shelf.Where(File.Exists).ToList();

    /// <summary>
    /// The folder what the phone brings lies in. It is kept apart from the shelf's own copies: a file from a phone is
    /// a file, and it deserves a place that says so.
    /// </summary>
    public static string Folder
    {
        get
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DynamicIsland", "Phone");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Lays a file down and gives its path back. The name the phone offered is only a suggestion: it is stripped to
    /// its last part, of every sign that cannot stand in a Windows name, and of leading dots, so that nothing the
    /// phone says can put a file outside this folder.
    /// </summary>
    static string Write(string name, string contentType, byte[] bytes)
    {
        string clean = Safe(Path.GetFileName(name));
        if (clean.Length == 0) clean = "Телефон";
        if (Path.GetExtension(clean).Length is 0 or > 5) clean += Ending(contentType);

        try
        {
            for (int n = 1; n < 100; n++)
            {
                string path = Path.Combine(Folder, n == 1 ? clean : $"{Path.GetFileNameWithoutExtension(clean)} {n}{Path.GetExtension(clean)}");
                if (File.Exists(path))
                {
                    if (Same(path, bytes)) return path; // the same file sent twice is one file on the shelf
                    continue;
                }
                File.WriteAllBytes(path, bytes);
                return path;
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        return "";
    }

    static bool Same(string path, byte[] bytes)
    {
        try
        {
            return new FileInfo(path).Length == bytes.Length && File.ReadAllBytes(path).SequenceEqual(bytes);
        }
        catch (IOException)
        {
            return false;
        }
    }

    static string Safe(string name)
    {
        var kept = new StringBuilder(name.Length);
        foreach (char c in name)
            if (c != '.' || kept.Length > 0) kept.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? ' ' : c);
        return kept.ToString().Trim().TrimEnd('.', ' ');
    }

    /// <summary>An ending for a file whose name came without one, from what the phone called it.</summary>
    static string Ending(string contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "application/pdf" => ".pdf",
        "text/plain" => ".txt",
        _ => ".bin",
    };

    /// <summary>
    /// One piece of a multipart body, whether it came as a file or as a plain field of the form. The page a phone
    /// posts back is written by us, so the shapes it can take are known: a line of headers, an empty line, content.
    /// </summary>
    public sealed record Part(string Name, string File, string Type, byte[] Bytes)
    {
        public string Text => Encoding.UTF8.GetString(Bytes);
    }

    static readonly byte[] CrLf = "\r\n"u8.ToArray();

    /// <summary>
    /// Opens a multipart body along its boundaries. What a browser puts between two boundaries is a dash-dash line,
    /// the parts headers, an empty line and the content; the content ends two signs before the next line, and the
    /// last boundary is closed by a further dash-dash.
    /// </summary>
    public static List<Part> Split(byte[] body, string boundary)
    {
        var found = new List<Part>();
        if (boundary.Length == 0 || body.Length == 0) return found;
        byte[] marker = Encoding.ASCII.GetBytes("--" + boundary);

        for (int at = Find(body, 0, marker); at >= 0; at = Find(body, at + 1, marker))
        {
            int from = at + marker.Length;
            if (from + 1 < body.Length && body[from] == '-' && body[from + 1] == '-') break; // the closing line
            int head = Find(body, from, CrLfLf);
            if (head < 0) break;
            int next = Find(body, head + 4, marker);
            if (next < 0) break;

            int size = next - 2 - (head + 4); // the line before a boundary is not part of the content
            if (size < 0) size = 0;
            var bytes = new byte[size];
            Array.Copy(body, head + 4, bytes, 0, size);
            found.Add(Of(Encoding.UTF8.GetString(body, from + 2, head - (from + 2)), bytes));
        }
        return found;
    }

    /// <summary>Which part of the form the piece fills: the field it is named for, the file it is named as.</summary>
    static Part Of(string lines, byte[] bytes)
    {
        string name = "", file = "", type = "";
        foreach (string line in lines.Split(CrLfText, StringSplitOptions.None))
        {
            if (line.StartsWith("Content-Disposition:", StringComparison.OrdinalIgnoreCase))
            {
                name = Bit(line, "name=\"", '"');
                file = Bit(line, "filename=\"", '"');
                if (file.Length == 0) file = Bit(line, "filename*=UTF-8''", ';');
            }
            else if (line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
                type = line[(line.IndexOf(':') + 1)..].Trim();
        }
        return new Part(name, Uri.UnescapeDataString(file), type, bytes);
    }

    static string Bit(string line, string start, char until)
    {
        int at = line.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return "";
        int from = at + start.Length, to = line.IndexOf(until, from);
        return to < 0 ? line[from..].TrimEnd(';', ' ') : line[from..to];
    }

    /// <summary>Where the needle first lies in the haystack at or after the given place, or nowhere.</summary>
    static int Find(byte[] haystack, int from, ReadOnlySpan<byte> needle)
    {
        for (int at = Math.Max(from, 0); at + needle.Length <= haystack.Length; at++)
            if (haystack.AsSpan(at, needle.Length).SequenceEqual(needle)) return at;
        return -1;
    }

    static readonly string[] CrLfText = ["\r\n"];
    static readonly byte[] CrLfLf = "\r\n\r\n"u8.ToArray();

    /// <summary>
    /// The one address a phone on the same network would reach this machine by. Adapters that carry no gateway rank
    /// below those that do, and the virtual ones — Hyper-V, tunnels, a Bluetooth link — are pushed further back still,
    /// since a phone cannot stand behind them.
    /// </summary>
    public static string LocalAddress()
    {
        var candidates = new List<(int Rank, string Address)>();
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                IPInterfaceProperties ip = nic.GetIPProperties();
                int rank = ip.GatewayAddresses.Count > 0 ? 0 : 200;
                if (Virtual(nic.Name) || Virtual(nic.Description)) rank += 40;
                foreach (UnicastIPAddressInformation one in ip.UnicastAddresses)
                {
                    if (one.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] b = one.Address.GetAddressBytes();
                    if (b[0] == 127 || (b[0] == 169 && b[1] == 254)) continue;
                    rank += Private(b);
                    candidates.Add((rank, one.Address.ToString()));
                }
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        return candidates.OrderBy(c => c.Rank).Select(c => c.Address).FirstOrDefault() ?? "";
    }

    /// <summary>How likely this address is to be the one a phone shares: a hotspot's own range first of all.</summary>
    static int Private(byte[] b) =>
        (b[0], b[1]) switch
        {
            (192, 168) => 0,
            (10, _) => 10,
            (172, >= 16 and <= 31) => 20,
            _ => 60,
        };

    static bool Virtual(string text) =>
        text.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
        || text.Contains("TAP", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The sign asked of every phone that comes to the door. It is made once, from random bytes, and kept in the
    /// registry so that a QR code shown today and a QR code shown tomorrow lead to the same page.
    /// </summary>
    public static string NewKey()
    {
        var bytes = new byte[3];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
