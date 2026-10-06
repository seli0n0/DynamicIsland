using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DynamicIsland;

/// <summary>
/// The other face of the bridge: the one an Android phone running KDE Connect reaches without a browser. The island
/// puts itself on the network exactly as that family does — a shout on the broadcast port, a TLS door behind it, and
/// the same talk the phones already speak. Two habits of the family are unlike the web, and a phone holds both to the
/// letter: a packet is one line of JSON ended by a newline (no length is counted out ahead of it), and whoever dials
/// the TCP port is the one that waits for TLS as a server, having first said who it is in plain words across the open
/// socket. What the phone sends inward (its battery, its notices, its clipboard, a file) goes to the same places the
/// QR page goes to.
/// </summary>
sealed class BridgeKde
{
    const int UdpPort = 1716; // the one port the family shouts on; neither dialect uses another
    const int TcpPort = 1716; // our door, and a phone only comes to a port inside 1716..1764
    static readonly TimeSpan Again = TimeSpan.FromSeconds(25); // how often the island repeats that it is here
    static readonly TimeSpan Alive = TimeSpan.FromSeconds(5);  // how often a channel says it is still there
    static readonly TimeSpan Idle = TimeSpan.FromSeconds(90);  // how long a channel may say nothing before it is given up
    // a paired phone is quiet for long stretches — it reports its charge when that changes and nothing otherwise — and
    // a channel dropped for that quiet has to be crossed again, which is where a real vivo answers its own second door
    // and closes the first. Ninety seconds of silence, with writes that still landing all the while, is a phone gone.
    static readonly TimeSpan Hurried = TimeSpan.FromSeconds(5); // the same patience, cut short for a channel the
    // island has just watched change its address: that road is already gone and only the phone's silence keeps it up
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(15); // how long a crossing waits for a phone to answer
    static readonly TimeSpan Tail = TimeSpan.FromSeconds(1); // how long a counted-out file waits for bytes its count omitted
    // a phone that is knocked at twice inside one second closes the second knock without a word, so the island keeps
    // to one crossing per phone in that span and lets the shout bring the next one
    static readonly TimeSpan Reknock = TimeSpan.FromSeconds(2);
    const int LargestLine = 64 * 1024; // one line of JSON only; a file arrives over a TLS channel of its own
    const int ProtocolVersion = 8;     // a phone that is offered less than eight will not answer the door at all

    readonly Bridge _bridge;
    readonly string _id = Id();
    readonly X509Certificate2 _certificate;
    readonly byte[] _ourKey;
    readonly Dictionary<string, Phone> _live = [];
    readonly Dictionary<string, DateTime> _knocked = [];
    readonly object _gate = new();
    Phone? _pending;
    CancellationToken _turns;

    public BridgeKde(Bridge bridge)
    {
        _bridge = bridge;
        _certificate = SelfSigned(_id);
        _ourKey = KeyOf(_certificate);
    }

    /// <summary>Runs the shout and the listener together until the bridge is closed.</summary>
    public async Task RunAsync(CancellationToken token)
    {
        _turns = token;
        var shout = Task.Run(() => ListenUdp(token), token);
        var door = Task.Run(() => ListenTcp(token), token);
        _ = Announce(token);
        await Task.WhenAll(shout, door);
    }

    // ------------------------------------------------------------- who is on the network

    async Task ListenUdp(CancellationToken token)
    {
        var listener = new UdpClient(AddressFamily.InterNetwork);
        listener.Client.EnableBroadcast = true;
        try { listener.Client.Bind(new IPEndPoint(IPAddress.Any, UdpPort)); }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            listener.Dispose(); // the real KDE Connect is on this machine: it will answer the phones itself
            return;
        }

        try
        {
            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult datagram = await listener.ReceiveAsync(token);
                // the address the shout came from is the one the phone stands on, whatever it says inside
                Identity? come = Identity.Of(datagram.Buffer, datagram.RemoteEndPoint.Address.ToString());
                if (come == null || come.Id == _id) continue;
                _bridge.Visit(come.Name);
                Reach(come, token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        finally { listener.Dispose(); }
    }

    /// <summary>Says, on the one road a phone can stand on, that an island of this family is here.</summary>
    async Task Announce(CancellationToken token)
    {
        string stood = "";
        while (!token.IsCancellationRequested)
        {
            // said afresh every round, from that road and naming it: a shout that arrives over the Wi-Fi while naming
            // the Ethernet address is one island answering under two names, and a vivo that hears the second of them
            // drops the channel it stands on and comes again — which is how a phone that has been paired for hours
            // has no connection at the moment its owner presses Send.
            foreach ((string road, IPAddress door) in ThisRound())
            {
                if (road != stood)
                {
                    stood = road; // worth one line: a machine with two roads onto one network shouts on one of them,
                    // and which one it picked is the first thing to look for when a phone keeps coming and going
                    Said($"the island says it is here on {road}, heard as {(road == _bridge.Address ? "the page's own address" : "no address the page names")}");
                }
                byte[] message = Bytes(Hello(onAir: true, road: road));
                try
                {
                    // from that road and no port of its own: the door stays on 1716 for whoever shouts at it, and a
                    // second socket on that port would take a phone's shout away from the one that listens for it
                    using var caller = new UdpClient(new IPEndPoint(IPAddress.Parse(road), 0))
                    { EnableBroadcast = true };
                    await caller.SendAsync(message, new IPEndPoint(door, UdpPort), token);
                }
                catch (SocketException) { } // a network that is mid-way through leaving
                catch (ArgumentException) { } // a road that has gone since it was named
            }
            await Task.Delay(Again, token);
        }
    }

    /// <summary>The roads this round's shout goes out on: one, and the road the QR page already names if it can be had.</summary>
    List<(string Road, IPAddress Door)> ThisRound()
    {
        List<(string Road, IPAddress Door)> all = [];
        foreach ((string road, IPAddress door) in Roads()) all.Add((road, door));
        (string Road, IPAddress Door) chief = all.FirstOrDefault(one => one.Road == _bridge.Address);
        return chief.Road is { Length: > 0 } ? [chief] : all;
    }

    /// <summary>Each of this machine's own IPv4 roads, and the broadcast address at the end of it.</summary>
    static IEnumerable<(string Road, IPAddress Door)> Roads()
    {
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (UnicastIPAddressInformation one in nic.GetIPProperties().UnicastAddresses)
                if (one.Address.AddressFamily == AddressFamily.InterNetwork && one.IPv4Mask != null)
                    yield return (one.Address.ToString(), Broadcast(one.Address, one.IPv4Mask));
        }
    }

    static IPAddress Broadcast(IPAddress address, IPAddress mask)
    {
        byte[] a = address.GetAddressBytes(), m = mask.GetAddressBytes();
        var net = new byte[a.Length];
        for (int i = 0; i < a.Length; i++) net[i] = (byte)(a[i] | ~m[i]);
        return new IPAddress(net);
    }

    // ------------------------------------------------------------- the TLS door

    async Task ListenTcp(CancellationToken token)
    {
        var listener = new TcpListener(IPAddress.Any, TcpPort);
        try { listener.Start(); }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            App.Log(ex);
            return;
        }

        using (token.Register(listener.Stop))
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(token); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { break; }
                _ = Task.Run(() => Open(client, null, token), token);
            }
        }
    }

    /// <summary>Crosses to a phone that shouted, or welcomes one that came. Either way the same talk follows.</summary>
    bool Reach(Identity come, CancellationToken token)
    {
        Phone place;
        lock (_gate)
        {
            if (_live.TryGetValue(come.Id, out Phone? inside))
            {
                // a phone that shouts from another address than the channel we hold has changed its way onto the
                // network: the channel it left is kept standing by nothing but our own patience, so it is hurried and
                // the phone come to again. This is how a phone that has rejoined the Wi-Fi is found in seconds rather
                // than at the end of a long silence — and why a shout from the road already talking is left alone.
                if (inside.Address.Length == 0 || inside.Address == come.Address) return true;
                Said($"{come.Name} has changed its address: {inside.Address} to {come.Address}, so that channel is ending");
                inside.Hurry = 1;
            }
            if (_knocked.TryGetValue(come.Id, out DateTime lately) && DateTime.UtcNow - lately < Reknock) return false;
            _knocked[come.Id] = DateTime.UtcNow; // taken at once, so two arrivals do not open two doors
            place = new Phone(come.Id) { Name = come.Name };
            _live[come.Id] = place;
        }
        _ = Task.Run(() => Open(null, come, token, place), token);
        return true;
    }

    /// <summary>
    /// The crossing, in the order the family makes it. Whoever dials the TCP port says who it is across the open
    /// socket first and then waits for TLS as a <em>server</em>; the one already listening reads that, becomes the TLS
    /// client, and asks for the caller's certificate. Both then say themselves again inside the encryption, since a
    /// name said in the clear is a name anyone can wear, and either side gives up on a phone whose name or protocol
    /// changes between the two tellings. Nothing else is read or written until that exchange is settled.
    /// </summary>
    /// <param name="place">The place this crossing took at the door, which it must give back if it comes to
    /// nothing. A claim kept after a failed crossing locks that phone out until the island is started again, and the
    /// phone's own shouts are refused as one already inside.</param>
    async Task Open(TcpClient? direct, Identity? come, CancellationToken token, Phone? place = null)
    {
        Phone? phone = null;
        SslStream? secure = null;
        TcpClient? crossed = null;
        // which way the door was crossed, and to whom: a failure that does not name its phone cannot be followed
        string where = direct != null ? "a phone at " + direct.Client.RemoteEndPoint
            : come != null ? "we came to " + come.Address + ":" + come.Port + " (" + come.Name + ")" : "an unknown phone";
        try
        {
            // a phone whose app has been put to sleep by the manufacturer keeps its port open and answers nothing at
            // all; fifteen seconds of that is a crossing given up on, not a door held until the island is restarted
            using var knock = CancellationTokenSource.CreateLinkedTokenSource(token);
            knock.CancelAfter(Patience);

            TcpClient client = crossed = direct ?? new TcpClient();
            if (direct == null) await client.ConnectAsync(come!.Address, come.Port, knock.Token);
            client.NoDelay = true;
            Stream bare = new NetworkStream(client.Client, true);
            // the same phone written two ways — ::ffff:192.168.31.19 across a dual-stack socket, 192.168.31.19 in its
            // own shout — would otherwise look like two phones on two roads, and the island would fall over itself
            // crossing to a phone it already stands talking with
            var far = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            string standing = far == null ? "phone"
                : (far.IsIPv4MappedToIPv6 ? far.MapToIPv4() : far).ToString();
            var held = new List<byte>();

            // what the far side claimed before the encryption, and the only name it may keep afterwards
            string claimed;
            int promised;
            if (direct == null)
            {
                await Write(bare, Hello(come!.Id, come.Protocol), knock.Token);
                secure = new SslStream(bare, true);
                await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    ClientCertificateRequired = true,
                    // a phone makes its own certificate, so no chain of trust can ever verify it; the owner judges the
                    // fingerprint instead, and the talk below refuses anything that was not agreed to
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                    EnabledSslProtocols = SslProtocols.Tls12, // the app stops at 1.2 of its own accord
                }, knock.Token);
                claimed = come.Id;
                promised = come.Protocol;
            }
            else
            {
                string open = await ReadLine(bare, held, knock.Token, sparing: true)
                    ?? throw new IOException("nothing was said at the door");
                Identity? first = Identity.Of(open, standing);
                if (first == null) throw new IOException("a phone spoke an identity that could not be read: " + Brief(open));
                secure = new SslStream(bare, true);
                await secure.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = first.Id, // the app names its certificate by the same sign it names itself
                    ClientCertificates = [_certificate],
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                    EnabledSslProtocols = SslProtocols.Tls12,
                }, knock.Token);
                claimed = first.Id;
                promised = first.Protocol;
            }

            await Write(secure, Hello(), knock.Token); // both say themselves again where nothing can be edited
            string said = await ReadLine(secure, held, knock.Token)
                ?? throw new IOException("the phone said nothing inside the encryption");
            Identity? peer = Identity.Of(said, standing);
            if (peer == null || peer.Id != claimed)
                throw new IOException($"{claimed} wore two names: {(peer?.Id ?? "unreadable")} after the encryption");
            if (peer.Protocol != promised)
                throw new IOException($"{claimed} changed its protocol half-way through: {promised} to {peer.Protocol}");

            if (secure.RemoteCertificate is not X509Certificate2 certificate)
                throw new IOException($"{claimed} sent no certificate to be judged");

            phone = new Phone(claimed)
            {
                Secure = secure,
                Fingerprint = Fingerprint(certificate),
                PeerKey = KeyOf(certificate),
                Address = standing,
                Name = peer.Name,
                Line = held,
            };
            secure = null; // the channel belongs to the phone from here, and the ending below closes it
            phone.Paired = Trusted(phone.Fingerprint);
            Said($"{phone.Name} at {standing} stands on a channel {(direct == null ? "we made" : "the phone made")}, "
                + $"{(phone.Paired ? "trusted from before" : "not yet trusted")}, protocol {peer.Protocol}");
            lock (_gate)
            {
                _live[phone.Id] = phone;
                _knocked.Remove(phone.Id);
            }
            _bridge.Visit(phone.Name);
            // a phone we already stand paired with needs no asking again: it restored its own trust when it woke, and
            // a pair request sent to a device that considers itself paired makes it unpair itself and start over
            await Talk(phone, token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException
            or OperationCanceledException or ArgumentException or InvalidOperationException or ObjectDisposedException
            or NotSupportedException)
        {
            // a phone that went away mid-sentence; the shout will bring it back
            App.Log(new IOException($"{where} broke off: {ex.Message}", ex));
        }
        finally
        {
            secure?.Dispose(); // a crossing given up on holds neither the port nor the phone's patience
            phone?.Secure.Dispose(); // nor does a channel that has finished its talk
            // both streams above are built to leave the socket standing behind them, so the crossing that ends is the
            // one that has to close it. A phone that walks away mid-handshake otherwise leaves a half-dead channel
            // counted here until the island is started again, and the phone sees its own gone connection kept open.
            crossed?.Dispose();
            // the place taken at the door is given back whether the talk reached a channel or not
            string leaving = phone?.Id ?? come?.Id ?? "";
            if (leaving.Length > 0)
            {
                bool waiting;
                lock (_gate)
                {
                    // only the crossing that still owns this place gives it up: the one that reached a channel owns
                    // the channel, and the one that came to nothing owns the place it took — while a phone that has
                    // since been crossed to again holds a newer door, which the older crossing must not close
                    if (!_live.TryGetValue(leaving, out Phone? still) || ReferenceEquals(still, phone)
                        || ReferenceEquals(still, place)) _live.Remove(leaving);
                    waiting = ReferenceEquals(_pending, phone);
                    if (waiting) _pending = null;
                }
                // a phone that left without an answer takes its request with it; the page stops asking
                if (waiting) _bridge.Answer(phone!.Name, false);
            }
        }
    }

    /// <summary>Reads packets until the phone stops talking, and says something meanwhile so that it knows we listen.</summary>
    async Task Talk(Phone phone, CancellationToken token)
    {
        using var breath = CancellationTokenSource.CreateLinkedTokenSource(token);
        var heartbeat = Task.Run(() => Keepalive(phone, breath), breath.Token);
        try
        {
            while (!token.IsCancellationRequested)
            {
                // a phone that walks out of range leaves no goodbye behind, only silence — but so does a phone that is
                // simply not reporting anything, and a paired channel torn down for that quiet has to be crossed again,
                // which a real vivo answers by closing one of the two. So the silence is given long patience, and only
                // cut short on the one channel a shout has just proved to be on a road the phone has left.
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token, breath.Token);
                wait.CancelAfter(Interlocked.Exchange(ref phone.Hurry, 0) == 1 ? Hurried : Idle);
                string? line = await ReadLine(phone.Secure, phone.Line, wait.Token);
                if (line == null)
                {
                    Said($"{phone.Name} stopped talking");
                    return;
                }
                Packet? come = Parse(line);
                if (come == null) continue; // a line that is no packet at all is skipped, as the family skips it
                try { await Handle(phone, come.Value, token); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // a phone carries twenty plugins' worth of packets and no island can take them all for the shape
                    // they are in; one that cannot be read is written down and passed over, since the alternative is
                    // losing the channel and every packet that follows it — which is how a paired phone goes deaf
                    App.Log(new IOException($"{phone.Name} said a packet the island could not take: {Brief(line)}", ex));
                }
            }
        }
        finally
        {
            breath.Cancel();
            try { await heartbeat; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>Says the channel is still there, and ends the talk below when it plainly is not.</summary>
    async Task Keepalive(Phone phone, CancellationTokenSource breath)
    {
        CancellationToken token = breath.Token;
        try
        {
            while (true)
            {
                await Task.Delay(Alive, token);
                await Send(phone, Frame("kdeconnect.keepalive", new JsonObject()), token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // a write that will not land is a phone that has gone, and it is the one sign the island gets without
            // waiting on silence: the read below stops on this breath rather than at the end of its patience
            Said($"{phone.Name} could not be spoken to any more: {ex.Message}");
            try { breath.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    async Task Handle(Phone phone, Packet packet, CancellationToken token)
    {
        JsonElement body = packet.Body;
        // only the two words that settle who stands at the door are heard from a stranger; everything else a phone
        // sends asks for a place in this machine, and so waits until the owner has agreed to that phone
        if (phone.Paired == false && packet.Type is not ("kdeconnect.identity" or "kdeconnect.pair"))
        {
            // worth a line: a phone that considers itself paired goes on sending to a stranger for as long as the
            // channel stands, and the owner sees a tile pressed with nothing at all answering it
            Said($"{phone.Name} is not trusted here yet, so {packet.Type} went unheard");
            return;
        }

        switch (packet.Type)
        {
            case "kdeconnect.identity":
                // a phone says itself twice over one channel and never again; a third telling is a phone that has lost
                // the first, so only its name is taken afresh from it
                phone.Name = Text(body, "deviceName") is { Length: > 0 } named ? named : phone.Name;
                break;

            case "kdeconnect.pair" when Ask(packet) && phone.Paired == false && NotFresh(packet):
                // from protocol eight on, a request that names no moment — or names one a half-hour away — is how a
                // replay of an old pairing looks, and the family answers it by unpairing rather than by asking twice
                App.Log(new IOException($"{phone.Name} asked for a pair without naming a believable moment"));
                Said($"{phone.Name} asks to pair with no believable moment in it: {Brief(packet.Raw)}");
                break;

            case "kdeconnect.pair" when Ask(packet) && phone.Paired == false:
                // the phone has asked; whether it stands inside is the owner's call, made on the page, and this
                // channel stays silent until that call is given. The eight signs shown on both sides are the same
                // eight the phone's own pairing screen shows, so the owner can match them across the two screens
                lock (_gate) _pending = phone;
                phone.Pending = true;
                Said($"{phone.Name} asks to pair");
                _bridge.Ask(new Bridge.Asking(phone.Name, phone.Fingerprint, Fingerprint(_certificate),
                    Code(phone, Whole(packet.Body, "timestamp") ?? 0)));
                break;

            case "kdeconnect.pair" when Ask(packet):
                // a phone we already stand paired with asks again after every joining; the yes is said back rather
                // than leave it waiting for an answer the owner gave in a run that has ended
                Said($"{phone.Name} says it is already paired, and is answered so");
                await Send(phone, Frame("kdeconnect.pair", PairBody(true), packet.Id), token);
                break;

            case "kdeconnect.pair": // the phone dropped us, or answered a pair request of its own
                Said($"{phone.Name} unpaired itself");
                phone.Paired = false;
                phone.Pending = false;
                lock (_gate) if (ReferenceEquals(_pending, phone)) _pending = null;
                Untrust(phone.Fingerprint);
                await Send(phone, Frame("kdeconnect.pair", PairBody(false), packet.Id), token);
                break;

            case "kdeconnect.device.battery" or "kdeconnect.battery":
                // the app says the phone's charge as kdeconnect.battery, the desktop as kdeconnect.device.battery, and
                // an island that listens for only one spelling hears neither from the phones that use the other
                int charge = Number(body, "currentCharge") is int now ? now : Number(body, "charge") ?? 0;
                Said($"{phone.Name} is at {charge} percent");
                _bridge.Report(phone.Name, charge);
                break;

            case "kdeconnect.notification":
                _bridge.Tell(Text(body, "appName"), Text(body, "title"), Text(body, "text"));
                break;

            case "kdeconnect.clipboard" or "kdeconnect.clipboard.connectivity" or "kdeconnect.clipboard.connect":
                string said = Text(body, "content");
                Said($"{phone.Name} sent {(said.Length > 0 ? $"{said.Length} signs of clipboard" : "an empty clipboard")}");
                if (said.Length > 0) _bridge.Take(said);
                break;

            case "kdeconnect.share.request":
                await Share(phone, packet, token);
                break;

            case "kdeconnect.keepalive":
                // a hello asked of us is answered with a hello, as both dialects do: a channel that never answers
                // leaves the phone counting its own unanswered hellos, and when that count runs out it drops a healthy
                // channel and crosses again — which is how a paired vivo redials every half-minute and has no
                // connection at the moment its owner presses Send. A hello that is itself an answer is not answered
                // back, or the two would say hello to each other for as long as the channel stood.
                if (Whole(packet.Root, "requestId") is null)
                    await Send(phone, Frame("kdeconnect.keepalive", new JsonObject(), packet.Id), token);
                break;

            default:
                // a tile the owner pressed on the phone lands here: the packet was heard and there is nowhere to put
                // it, which is worth a line, since otherwise a pressed tile and a dead channel look the same
                Said($"{phone.Name} said {packet.Type}, which the island has no place for");
                break;
        }
    }

    /// <summary>
    /// A file the phone has promised. The packet counts out a length and names a port of its own; the bytes are
    /// written across a second TLS channel opened to that port, the one channel being kept for packets in this family.
    /// The count is what the phone believed the file to be, not what it finally writes, so the bytes are taken until
    /// the phone stops sending them rather than until the count is spent.
    /// </summary>
    async Task Share(Phone phone, Packet packet, CancellationToken token)
    {
        // counted in the body by the app and in the outer packet by the desktop; a promise bigger than the island
        // takes in is turned down before any channel is opened
        long promised = Whole(packet.Body, "payloadSize") ?? Whole(packet.Root, "payloadSize") ?? 0;
        if (promised > Bridge.LargestUpload || phone.Address.Length == 0) return;

        string name = Text(packet.Body, "fileName");
        string mime = Text(packet.Body, "mimeType");
        int port = 0;
        if (packet.Root.TryGetProperty("payloadTransferInfo", out JsonElement where)
            && where.ValueKind == JsonValueKind.Object)
        {
            port = Number(where, "port") ?? 0;
            if (name.Length == 0) name = Text(where, "fileName");
        }
        if (port <= 0) return; // a request to fetch a page is not a file being sent, and the island pulls nothing

        Said($"{phone.Name} promises {(name.Length > 0 ? name : "a file")} of {promised} bytes at :{port}");

        var pile = new MemoryStream(promised is > 0 and <= Bridge.LargestUpload ? (int)promised : 16384);
        // The phone waits only so long at that port before it says the file failed, and a phone that has been put to
        // sleep by its own system holds a knocking channel open for nothing until it is given up on. So this crossing
        // breathes on its own clock rather than on the packet channel's, which stops for as long as we are reading,
        // and that clock is wound afresh by every read rather than run out once.
        using var breath = CancellationTokenSource.CreateLinkedTokenSource(token);
        breath.CancelAfter(Patience);
        bool whole = false;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(phone.Address, port, breath.Token);
            using var wire = new SslStream(client.GetStream(), true);
            await wire.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = phone.Id,
                ClientCertificates = [_certificate],
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                EnabledSslProtocols = SslProtocols.Tls12,
            }, breath.Token);

            var spare = new byte[16384];
            while (true)
            {
                // The crossing's clock is wound afresh by every read, so a big file out last s no patience it never
                // ran out of. While the count is unspent silence means the phone is still sending, and fifteen
                // seconds of it is a file lost; once the count is spent all that can follow is a tail the count never
                // named, and one second of nothing says no more is coming. That second is kept on a window of its
                // own, since an ending here must not end the crossing — the line said below is asked for on a
                // channel that is still whole.
                breath.CancelAfter(Patience);
                using var tail = CancellationTokenSource.CreateLinkedTokenSource(breath.Token);
                if (pile.Length >= promised) tail.CancelAfter(Tail);
                int got;
                try { got = await wire.ReadAsync(spare, tail.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { break; }
                if (got <= 0) break;
                if (pile.Length + got > Bridge.LargestUpload)
                    throw new IOException($"more than the island takes in, past a promise of {promised} bytes");
                pile.Write(spare, 0, got);
            }
            whole = pile.Length >= promised && pile.Length > 0;

            // A sender of this family that waits for a word before it counts a file as sent is waiting for this line;
            // one that has already hung up never reads it, and the write going unnoticed is not a file that failed.
            try
            {
                await Write(wire, Frame("share.sendFile", new JsonObject
                {
                    ["fileName"] = name,
                    ["mimeType"] = mime,
                    ["isShareFile"] = true,
                }, packet.Id), breath.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                or ObjectDisposedException or InvalidOperationException) { } // a phone gone mid-hangup wants no answer
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException
            or OperationCanceledException or ArgumentException or InvalidOperationException)
        {
            if (!token.IsCancellationRequested)
                App.Log(new IOException($"{phone.Name} offered {(name.Length > 0 ? name : "a file")} that did not arrive: {ex.Message}", ex));
            return;
        }

        if (!whole)
        {
            // fewer bytes than the phone promised, or none at all: kept, it would be a file that opens broken, so it
            // is written down instead and thrown away
            App.Log(new IOException($"{phone.Name} sent {pile.Length} bytes of {(name.Length > 0 ? name : "a file")} against a promise of {promised}"));
            return;
        }

        Said($"{(name.Length > 0 ? name : "a file")} arrived in {pile.Length} bytes against a promise of {promised}");
        _bridge.Take(name, mime, pile.ToArray());
    }

    static bool Ask(Packet packet) =>
        packet.Body.ValueKind == JsonValueKind.Object &&
        packet.Body.TryGetProperty("pair", out JsonElement value) && value.ValueKind == JsonValueKind.True;

    /// <summary>Whether a pair request names no moment at all, or one more than half an hour from this one.</summary>
    static bool NotFresh(Packet packet) =>
        Whole(packet.Body, "timestamp") is not long asked
        || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - asked) > 1800;

    // ------------------------------------------------------------- packets, as the family counts them

    /// <summary>
    /// One line of JSON ended by a newline, with no length counted out ahead of it. Bytes are gathered until that
    /// newline appears, since a packet can arrive in pieces and two can arrive together — and what is left of the
    /// second is held over for the reading after this one. Nothing is said back: a line that never ends, or a far
    /// side that has gone, is the end of the channel.
    /// </summary>
    /// <param name="sparing">Before any encryption exists the line is gathered one byte at a time. The words said at
    /// an open socket are followed at once by a handshake, and a read that takes both leaves the second half of the
    /// first in a buffer no encryption will ever look at again — which is how the family itself reads that line, and
    /// why it says the slowness is only for the handshake.</param>
    static async Task<string?> ReadLine(Stream wire, List<byte> held, CancellationToken token, bool sparing = false)
    {
        var chunk = new byte[sparing ? 1 : 4096];
        int cut;
        while ((cut = held.IndexOf((byte)'\n')) < 0)
        {
            int got;
            try { got = await wire.ReadAsync(chunk, token); }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                or ObjectDisposedException or InvalidOperationException) { return null; }
            if (got <= 0) return null;
            for (int i = 0; i < got; i++) held.Add(chunk[i]);
            if (held.Count > LargestLine) return null;
        }

        string line = Encoding.UTF8.GetString(held.GetRange(0, cut).ToArray());
        held.RemoveRange(0, cut + 1);
        return line;
    }

    static Packet? Parse(string line)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string type = Text(root, "type");
            if (type.Length == 0) return null;
            return new Packet(line, type, Whole(root, "id") ?? 0,
                root.TryGetProperty("body", out JsonElement body) ? body.Clone() : default, root.Clone());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }

    async Task Send(Phone phone, string json, CancellationToken token)
    {
        try
        {
            await phone.Door.WaitAsync(token);
            try { await Write(phone.Secure, json, token); }
            finally { phone.Door.Release(); }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
            or ObjectDisposedException or InvalidOperationException) { }
    }

    static async Task Write(Stream wire, string json, CancellationToken token)
    {
        await wire.WriteAsync(Bytes(json), token);
        await wire.FlushAsync(token); // a handshake line held in a buffer is a phone left waiting for nothing
    }

    static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json + "\n");

    static string DeviceName => "Dynamic Island";

    /// <summary>
    /// Who we are, said as both dialects of the family say it: "deviceName", "protocolVersion", and the two lists of
    /// capabilities, which is everything a phone looks at before it decides that anything is worth sending. The
    /// capabilities are named as the phone's own plugins name themselves, since a phone consults the list of what the
    /// far side takes in. The two target fields belong only to the words said across an open socket: a phone asked to
    /// pair with some other device, or offered a protocol it does not speak, closes the door without an explanation.
    /// </summary>
    /// <param name="road">The address this shout goes out from, which is the one the phone should come back to. Left
    /// out, a shout names the machine's chief address however far it travelled, and a phone that is reached over one
    /// road while being told another answers the two as two different islands.</param>
    string Hello(string? target = null, int targetProtocol = 0, bool onAir = false, string road = "")
    {
        var body = new JsonObject
        {
            ["deviceId"] = _id,
            ["deviceName"] = DeviceName,
            ["deviceType"] = "desktop",
            ["protocolVersion"] = ProtocolVersion,
            ["incomingCapabilities"] = new JsonArray(CanHear.Select(s => (JsonNode)s).ToArray()),
            ["outgoingCapabilities"] = new JsonArray(),
        };
        // a shout names the door to come to, and the address it stands on: a phone can tell neither from the wire
        if (onAir)
        {
            body["tcpPort"] = TcpPort;
            string where = road.Length > 0 ? road : _bridge.Address;
            if (where.Length > 0) body["address"] = where;
        }
        if (target is { Length: > 0 })
        {
            body["targetDeviceId"] = target;
            body["targetProtocolVersion"] = targetProtocol;
        }
        return Frame("kdeconnect.identity", body);
    }

    /// <summary>The plugins of a phone that find something to do with an island: what it listens for, by their names.</summary>
    static readonly string[] CanHear =
    [
        "kdeconnect.battery", "kdeconnect.notifications", "kdeconnect.clipboard", "kdeconnect.share",
    ];

    /// <summary>
    /// One packet, numbered as the family numbers them. A phone keeps its own count of the numbers it has read and
    /// throws away anything below it, so ours climb with the clock instead of repeating a asked packet's number; the
    /// answer is pointed at the question by requestId, which is the one thing a phone reads to tell an answer from a
    /// new asking — and a keepalive it mistakes for a new asking is answered in turn, forever.
    /// </summary>
    static string Frame(string type, JsonObject body, long asks = 0)
    {
        var packet = new JsonObject
        {
            ["id"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["type"] = type,
        };
        if (asks != 0) packet["requestId"] = asks;
        packet["body"] = body;
        return packet.ToJsonString();
    }

    /// <summary>
    /// The one packet that settles whether a phone stands inside. An ask and an answer are made of the same words —
    /// the family has no other pair for them — and from protocol eight on a request that names no moment is dropped
    /// unheard, since that moment is one of the three things the verification signs are made from.
    /// </summary>
    JsonObject PairBody(bool pair) => new()
    {
        ["pair"] = pair,
        ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    };

    /// <summary>
    /// The eight signs both screens show while a pair is being agreed to: the two public keys in a fixed order, and
    /// the moment the request was made, hashed down to what a person can read across two devices. A phone holding a
    /// substituted certificate cannot make the same signs, which is the whole reason for showing them at all.
    /// </summary>
    string Code(Phone phone, long timestamp)
    {
        if (phone.PeerKey.Length == 0 || _ourKey.Length == 0) return "";
        byte[] joined = Ordered(_ourKey, phone.PeerKey);
        byte[] hash = SHA256.HashData(joined.Concat(Encoding.UTF8.GetBytes(timestamp.ToString())).ToArray());
        return Convert.ToHexString(hash, 0, 4);
    }

    /// <summary>Both sides must arrive at the same order, so the greater key of the two is written first.</summary>
    static byte[] Ordered(byte[] a, byte[] b) =>
        a.AsSpan().SequenceCompareTo(b) < 0 ? b.Concat(a).ToArray() : a.Concat(b).ToArray();

    /// <summary>
    /// The owner's answer to the phone waiting at the door. Nothing is trusted before it is said; a refusal is carried
    /// back over the same channel as the plain no the family uses, so that the phone stops asking.
    /// </summary>
    public void Answer(bool yes)
    {
        Phone? phone;
        lock (_gate)
        {
            phone = _pending;
            _pending = null;
        }
        if (phone == null) return;

        phone.Pending = false;
        Said($"the owner said {(yes ? "yes" : "no")} to {phone.Name}");
        if (yes)
        {
            phone.Paired = true;
            Trust(phone);
        }
        _ = Task.Run(() => Send(phone, Frame("kdeconnect.pair", PairBody(yes)), _turns), _turns);
        _bridge.Answer(phone.Name, yes);
    }

    // ------------------------------------------------------------- trust, kept between runs

    static void Trust(Phone phone)
    {
        try
        {
            List<string> known = Phones().ToList();
            string entry = $"{phone.Id}\t{phone.Name}\t{phone.Fingerprint}";
            if (!known.Contains(entry))
            {
                known.Add(entry);
                File.WriteAllText(PhoneFile, string.Join("\n", known));
            }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>Forgets a phone the owner refused, or that unpaired itself, so that it has to ask again.</summary>
    static void Untrust(string fingerprint)
    {
        if (fingerprint.Length == 0) return; // nothing to pick by, and everything would go
        try
        {
            List<string> known = Phones().ToList();
            List<string> kept = known.Where(line => !line.EndsWith(fingerprint, StringComparison.OrdinalIgnoreCase)).ToList();
            if (kept.Count != known.Count) File.WriteAllText(PhoneFile, string.Join("\n", kept));
        }
        catch (Exception ex) { App.Log(ex); }
    }

    static string PhoneFile => Path.Combine(Bridge.Folder, "phones.txt");

    static IEnumerable<string> Phones()
    {
        try
        {
            return File.Exists(PhoneFile)
                ? File.ReadAllLines(PhoneFile).Where(line => line.Length > 0).ToList()
                : [];
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return [];
        }
    }

    // ------------------------------------------------------------- small helpers

    static string Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    static int? Number(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32() : null;

    static long? Whole(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64() : null;

    /// <summary>
    /// The hash a phone shows its owner, so that two screens can be compared: SHA-256 of the certificate itself,
    /// which is what both dialects of the family call a fingerprint and neither of them the shorter SHA-1 that a
    /// Windows certificate store keeps beside it.
    /// </summary>
    static string Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();

    /// <summary>The public key as the family carries it, which is what the verification signs are made of.</summary>
    static byte[] KeyOf(X509Certificate2 certificate)
    {
        try { return certificate.PublicKey.ExportSubjectPublicKeyInfo(); }
        catch (Exception ex) { App.Log(ex); return []; } // a key this island cannot read is only a missing code
    }

    /// <summary>Whether this certificate was let in before, in a run of the island that has long ended.</summary>
    static bool Trusted(string fingerprint) =>
        fingerprint.Length > 0 && Phones().Any(line => line.EndsWith(fingerprint, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The name this island answers to on the network: thirty-two signs, as a phone makes its own, and kept with the
    /// machine rather than the run. A shout or an opening line naming anything shorter or longer goes unheard, since
    /// that is the shape a phone checks before it will even consider who is calling.
    /// </summary>
    static string Id()
    {
        string saved = Settings.BridgePhoneId;
        if (IsDeviceId(saved)) return saved;
        var bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);
        string fresh = Convert.ToHexString(bytes).ToLowerInvariant();
        Settings.BridgePhoneId = fresh;
        return fresh;
    }

    static readonly Regex DeviceId = new("^[a-zA-Z0-9_-]{32,38}$", RegexOptions.Compiled);

    static bool IsDeviceId(string candidate) => DeviceId.IsMatch(candidate);

    /// <summary>The beginning of what a phone said, for the log: a whole packet is more than anyone wants to read.</summary>
    static string Brief(string line) => line.Length <= 120 ? line : line[..120] + "…";

    /// <summary>
    /// One line of this bridge's own speech, in the file its faults use. A real phone that goes quiet, or is refused at
    /// the door, or pairs and unhooks itself a minute later, otherwise reads exactly like a phone that never appeared
    /// at all — and no fix to a complaint about a living phone can be made from a silence.
    /// </summary>
    static void Said(string what) => App.Note("kde · " + what);

    /// <summary>
    /// The island's own certificate, made once and kept beside the list of trusted phones: a phone that has taken a
    /// liking to this island stores exactly this certificate and refuses every other when it is met again, so a
    /// certificate remade at each starting locks the island out of its own pairs. It names itself by the same sign
    /// the island answers to on the network, as the family names its certificates.
    /// </summary>
    static X509Certificate2 SelfSigned(string id)
    {
        try
        {
            string saved = Path.ChangeExtension(PhoneFile, ".pfx");
            if (File.Exists(saved))
                return new X509Certificate2(saved, (string?)null, X509KeyStorageFlags.Exportable);

            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest(
                new X500DistinguishedName($"CN={id}, OU=KDE Connect, O=KDE"), key,
                HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false));

            X509Certificate2 made = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(9));
            File.WriteAllBytes(saved, made.Export(X509ContentType.Pfx));
            return new X509Certificate2(saved, (string?)null, X509KeyStorageFlags.Exportable);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            throw;
        }
    }

    /// <summary>A phone on the far side of one TLS channel.</summary>
    sealed class Phone(string id)
    {
        public string Id { get; } = id;
        public string Name { get; set; } = id;
        public string Fingerprint { get; set; } = "";
        public string Address { get; set; } = "";
        public byte[] PeerKey { get; set; } = [];
        public bool Paired { get; set; }

        /// <summary>Whether this phone's request is the one shown on the page, waiting for the owner.</summary>
        public bool Pending { get; set; }

        public SslStream Secure = null!;

        /// <summary>Set when a shout has come from a different address than this channel stands on: the channel is on a
        /// road the phone has left, so the next silence is taken as an answer rather than as quiet.</summary>
        public int Hurry;

        /// <summary>What has been read past the end of the last packet, kept for the next one.</summary>
        public List<byte> Line { get; set; } = [];

        /// <summary>One packet at a time on the channel: the answer to a pair can come from the page, not from the talk.</summary>
        public SemaphoreSlim Door { get; } = new(1, 1);
    }

    readonly record struct Packet(string Raw, string Type, long Id, JsonElement Body, JsonElement Root);

    /// <summary>A phone as it names itself, on the broadcast or across an open socket before any TLS channel exists.</summary>
    sealed record Identity(string Id, string Name, string Address, int Port, int Protocol)
    {
        public static Identity? Of(byte[] datagram, string from) => Of(Encoding.UTF8.GetString(datagram), from);

        /// <summary>
        /// A shout or an opening line read as the family reads it, and it is stricter than one would guess. The name
        /// must be there to be said at all; the device id must be the thirty-two signs a device makes for itself,
        /// because a phone measures an id before it measures a name; the protocol must be at least eight, which the
        /// desktop refuses below and the app refuses above its own; and the port must be one the family keeps. The
        /// address the packet arrived from is taken over any address the packet claims, since that is the only road
        /// known to carry traffic both ways. Anything else is not a phone, and knocking at it spends the patience of
        /// the real one nearby.
        /// </summary>
        public static Identity? Of(string line, string from)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                if (Text(root, "type") != "kdeconnect.identity") return null;
                if (!root.TryGetProperty("body", out JsonElement body)) return null;

                string id = Text(body, "deviceId");
                if (!IsDeviceId(id)) return null;
                string name = Text(body, "deviceName");
                if (name.Length == 0) return null;
                int protocol = Number(body, "protocolVersion") ?? 0;
                if (protocol < ProtocolVersion) return null;

                // a shout names the door to come to; a phone that came by itself has no need of one
                int given = Number(body, "tcpPort") ?? 0;
                return new Identity(id, name, from, given is >= 1716 and <= 1764 ? given : TcpPort, protocol);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                return null;
            }
        }
    }
}
