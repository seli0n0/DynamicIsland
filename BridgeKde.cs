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
    const long LargestPicture = 512 * 1024; // an app's icon as its own phone draws it; longer than this is not a picture
    const int ProtocolVersion = 8;     // a phone that is offered less than eight will not answer the door at all

    readonly Bridge _bridge;
    readonly string _id = Id();
    readonly X509Certificate2 _certificate;
    readonly byte[] _ourKey;
    readonly Dictionary<string, Phone> _live = [];
    /// <summary>Which phone showed each notice the island is holding, and under what key that phone knows it by.</summary>
    readonly Dictionary<string, (Phone Holder, string Id, string Reply)> _held = [];
    readonly Dictionary<string, DateTime> _knocked = [];
    readonly object _gate = new();
    /// <summary>
    /// The pictures phones have sent with their notices, kept by the count the phone set over the bytes. A phone sends
    /// an app's picture once and afterwards only its count, meaning the other end to have kept it — and a run that has
    /// ended forgets what it kept, which is why the same count finds the same picture again on disk.
    /// </summary>
    readonly Dictionary<string, byte[]> _pictures = [];
    /// <summary>
    /// This machine's music, as the island last said it. A phone looks once when its media page opens and listens
    /// after, so a late asking is answered from here rather than with nothing.
    /// </summary>
    Bridge.Playing _playing = new("", "", false, 0, -1, false, 0);
    DateTime _mirrored = DateTime.MinValue;
    /// <summary>The phone whose ask for a place at the door the owner has not answered yet. Kept by the phone's own
    /// name rather than by the channel it asked on: a real vivo crosses a fresh channel every half-minute or so while
    /// a person reads the pill and decides, and a yes written down the channel that has gone is a yes nobody hears.</summary>
    (string Id, string Name, string Fingerprint)? _pending;
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
            // one honest line about what this phone claims to speak, taken from its identity: the app counts the types of
            // every plugin it knows rather than the ones it runs for us, so what is *here* rules a phone in or out —
            // a phone that names no `kdeconnect.notification` at all has no notice dialect, and no island will ever be
            // shown one — while a phone that names it only says the dialect exists, not that its owner has let it read
            // the notice bar
            Said($"{phone.Name} sends {peer.Sends.Length} kind{(peer.Sends.Length == 1 ? "" : "s")} of packet"
                + (peer.Sends.Contains("kdeconnect.notification") ? ", notices among them"
                    : $", and none of them notices: {string.Join(", ", peer.Sends)}"));
            lock (_gate)
            {
                // a phone that crosses itself anew keeps the pairing this island has already answered: the owner's yes
                // is on its way out, and the phone's own yes back may well arrive on this second channel rather than
                // the one the ask came over — where answering it again would read to the app as a stranger's request
                if (_live.TryGetValue(phone.Id, out Phone? before) && before.Confirming > phone.Confirming)
                    phone.Confirming = before.Confirming;
                _live[phone.Id] = phone;
                _knocked.Remove(phone.Id);
            }
            _bridge.Visit(phone.Name);
            // what the phone was holding before this channel existed is asked for the moment it settles, as the family
            // asks it: a notice that arrived while the island was closed is read here rather than lost on the phone
            if (phone.Paired && Settings.PhoneNotices) await Holds(phone);
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
                bool waiting, alone;
                lock (_gate)
                {
                    // only the crossing that still owns this place gives it up: the one that reached a channel owns
                    // the channel, and the one that came to nothing owns the place it took — while a phone that has
                    // since been crossed to again holds a newer door, which the older crossing must not close
                    if (!_live.TryGetValue(leaving, out Phone? still) || ReferenceEquals(still, phone)
                        || ReferenceEquals(still, place)) _live.Remove(leaving);
                    // a notice answered through a channel that has ended is a word said to no phone, so it is given up
                    // here rather than kept as a key the page can press for nothing
                    alone = !_live.ContainsKey(leaving);
                    if (alone) foreach (string key in _held.Where(one => one.Value.Holder.Id == leaving).Select(one => one.Key))
                            _held.Remove(key);
                    // an ask the owner has not answered is dropped only when the phone itself has gone: a channel that
                    // has ended while another stands under the same name is a phone that has crossed itself again,
                    // and its request is still open on its own screen waiting for the very answer being taken away
                    waiting = alone && _pending?.Id == leaving;
                    if (waiting) _pending = null;
                }
                // a phone that left without an answer takes its request with it; the page stops asking
                if (waiting) _bridge.Answer(phone!.Name, false);
                if (alone) _bridge.Gone(leaving, phone?.Name ?? come?.Name ?? "");
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

    /// <summary>
    /// Says the channel is still there, and ends the talk below when it plainly is not. Only to a phone this island
    /// stands paired with: the app on the other side reads any packet that is not about pairing while its pairing is
    /// still open — a request of its own or ours, waiting for a person to answer — as proof that this side has taken
    /// itself away, and says so on its screen («отменено другим участником») before the owner has had the breath to
    /// press anything. A hello sent into that window is the one word a phone cannot ignore quietly.
    /// </summary>
    async Task Keepalive(Phone phone, CancellationTokenSource breath)
    {
        CancellationToken token = breath.Token;
        try
        {
            while (true)
            {
                await Task.Delay(Alive, token);
                if (phone.Paired) await Send(phone, Frame("kdeconnect.keepalive", new JsonObject()), token);
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
                lock (_gate) _pending = (phone.Id, phone.Name, phone.Fingerprint);
                Said($"{phone.Name} asks to pair");
                _bridge.Ask(new Bridge.Asking(phone.Name, phone.Fingerprint, Fingerprint(_certificate),
                    Code(phone, Whole(packet.Body, "timestamp") ?? 0)));
                break;

            case "kdeconnect.pair" when Ask(packet) && phone.Confirming > DateTime.UtcNow:
                // the phone's own yes, come back to the one the island just said. Nothing is answered here, and that
                // is the whole of the difference: an ask and an answer are made of the same words, so a device that
                // stands paired and is asked again takes it for a stranger's request and unpairs the pairing it has
                // just agreed to (PairingHandler: state Paired + pair=true → unpaired, then a fresh request of its own)
                phone.Confirming = DateTime.MinValue;
                Said($"{phone.Name} agreed to the pairing");
                // and only now is it asked what it holds: a notice request sent a half-second earlier is a plugin
                // packet reaching a phone that does not yet stand paired, and such a packet is what makes the app
                // unpair a device it is in the middle of agreeing to (Device.onPacketReceived: !isPaired → unpair)
                if (Settings.PhoneNotices) AskNotices();
                break;

            case "kdeconnect.pair" when Ask(packet):
                // a phone we already stand paired with asks again after every joining; the yes is said back rather
                // than leave it waiting for an answer the owner gave in a run that has ended — and it is said back
                // once, with the same waiting for its own yes that the owner's answer leaves behind
                Said($"{phone.Name} says it is already paired, and is answered so");
                phone.Confirming = DateTime.UtcNow.AddSeconds(30);
                await Send(phone, Frame("kdeconnect.pair", PairBody(true), packet.Id), token);
                break;

            case "kdeconnect.pair": // the phone has taken itself away from this island
                // said with the packet itself: a phone that unpair itself is a phone whose trust line is about to be
                // deleted, and an owner asked to pair again deserves to know what was heard the first time
                Said($"{phone.Name} unpaired itself: {Brief(packet.Raw)}");
                phone.Paired = false;
                phone.Confirming = DateTime.MinValue;
                lock (_gate) if (_pending?.Id == phone.Id) _pending = null;
                Untrust(phone.Fingerprint);
                // and no word is said back. A pair that says no is made of the same shape as a refusal, so an echo of
                // the phone's own leaving reaches it as the island refusing — which is the message on the phone's own
                // screen: «отменено другим участником», the app's word for a no heard while a pairing stands open
                break;

            case "kdeconnect.device.battery" or "kdeconnect.battery":
                // the app says the phone's charge as kdeconnect.battery, the desktop as kdeconnect.device.battery, and
                // an island that listens for only one spelling hears neither from the phones that use the other
                int charge = Number(body, "currentCharge") is int now ? now : Number(body, "charge") ?? 0;
                bool onCharge = Flag(body, "isCharging");
                Said($"{phone.Name} is at {charge} percent{(onCharge ? ", on charge" : "")}");
                _bridge.Report(phone.Name, charge, onCharge);
                break;

            case "kdeconnect.notification":
                Notice(phone, packet, await Art(phone, packet, token));
                break;

            case "kdeconnect.clipboard" or "kdeconnect.clipboard.connectivity" or "kdeconnect.clipboard.connect":
                string said = Text(body, "content");
                Said($"{phone.Name} sent {(said.Length > 0 ? $"{said.Length} signs of clipboard" : "an empty clipboard")}");
                if (said.Length > 0) _bridge.Take(said);
                break;

            case "kdeconnect.share.request":
                await Share(phone, packet, token);
                break;

            case "kdeconnect.mpris.request":
                await Music(phone, packet, token);
                break;

            case "kdeconnect.mpris":
                // the phone mirrors its own player at this machine the way the island mirrors this one at it: heard,
                // and left where it lies. An island showing two players would have to say which one its own buttons
                // answer, and a phone's music is not this machine's to drive — while the saying of it would fill the
                // phone page every time the phone changes a song.
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

        // The file's own name is `filename` — one word, a lower-case n — both in the app (`FilesHelper.uriToNetworkPacket`
        // writes `packet["filename"]`, and `SharePlugin` hears a packet only `if (np.has("filename"))`) and on the desktop.
        // An island that looks for a capital there finds no name at all and lays the file down under its own guess, which
        // is how every photo and PDF a phone ever sent came back to its owner as `Телефон.bin`, unopenable.
        string name = Text(packet.Body, "filename");
        string mime = Text(packet.Body, "mimeType");
        int port = 0;
        if (packet.Root.TryGetProperty("payloadTransferInfo", out JsonElement where)
            && where.ValueKind == JsonValueKind.Object)
        {
            port = Number(where, "port") ?? 0;
            if (name.Length == 0) name = Text(where, "filename");
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
                    ["filename"] = name,
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

    /// <summary>
    /// A notice as the phone holds it, in the three shapes one packet type carries: one that names a moment taken
    /// away, one that was there before this channel opened and says so, and the rest arrived just now or changed in
    /// place. A notice may ride with the picture of the app that made it, and the phone sends those bytes only the
    /// first time it has them for that notice: after that the packet names a count over the bytes it offered once,
    /// meaning the other end to have kept them. So the count is kept beside the bytes, in this run and on disk, and a
    /// notice whose picture was never offered — or was offered at a door the island could not reach — is shown without
    /// one, which is a notice still worth showing.
    /// </summary>
    void Notice(Phone phone, Packet packet, byte[]? given)
    {
        JsonElement body = packet.Body;
        string where = Text(body, "id");
        if (where.Length == 0) return; // no key to hold it by, and none to answer it with
        string key = phone.Id + "/" + where;

        if (Flag(body, "isCancel")) // the owner took it off the phone: it is gone, and a gone notice wants no toast
        {
            lock (_gate) _held.Remove(key);
            Said($"{phone.Name} took a notice away: {where}");
            _bridge.Took(key);
            return;
        }

        // an app that hides its contents still leaves its ticker, and one that hides both leaves its name
        string said = Text(body, "text");
        if (said.Length == 0) said = Text(body, "ticker");

        byte[]? art = Picture(Text(body, "payloadHash"), given);
        var notice = new Bridge.Notice(key, phone.Name, Text(body, "appName"), Text(body, "title"), said,
            Titles(body), Text(body, "requestReplyId"), Flag(body, "isClearable"), Flag(body, "silent"), When(body), art);
        Said($"{phone.Name} holds a notice of {(notice.App.Length > 0 ? notice.App : "an app that did not name itself")},"
            + $" {(notice.Silent ? "there before this channel opened" : "just arrived")}"
            + $"{(notice.Actions.Length > 0 ? $", {notice.Actions.Length} buttons on it" : "")}"
            + $"{(notice.ReplyId.Length > 0 ? ", answerable" : "")}"
            + $"{(art != null ? $", its picture in {art.Length} bytes" : "")}");
        lock (_gate) _held[key] = (phone, where, notice.ReplyId);
        _bridge.Bring(notice);
    }

    /// <summary>
    /// The picture a notice came with. The count the phone named over its bytes is the key they are kept under: bytes
    /// that arrived are remembered and laid on disk, and a notice that brings only a count is shown with whatever that
    /// count has been since — from this run, or from the last one. A count the island has never had bytes for is a
    /// notice without a picture, since nothing here can ask the phone for them.
    /// </summary>
    byte[]? Picture(string count, byte[]? given)
    {
        // the count comes off the wire and becomes a file name, so only the thirty-two hex signs a hash is made of
        // are taken as one; anything else is a notice whose picture is kept for this run alone, if it came at all
        bool shaped = count.Length == 32 && count.All(Uri.IsHexDigit);
        if (!shaped) return given;

        lock (_gate) if (_pictures.TryGetValue(count, out byte[]? kept)) return given ?? kept;
        if (given != null)
        {
            lock (_gate) _pictures[count] = given;
            Try(() => File.WriteAllBytes(IconsOf(count), given)); // a run that ends must not forget a whole shelf
            return given;
        }

        string here = IconsOf(count);
        if (!File.Exists(here)) return null;
        try
        {
            byte[] off = File.ReadAllBytes(here);
            lock (_gate) _pictures[count] = off;
            return off;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    static string IconsOf(string count)
    {
        string dir = Path.Combine(Bridge.Folder, "icons");
        Directory.CreateDirectory(dir); // a folder of pictures is a folder the phone never asked to have
        return Path.Combine(dir, count + ".png");
    }

    /// <summary>A disk that is busy or full costs an owner a picture, not a channel.</summary>
    static void Try(Action way)
    {
        try { way(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.Log(ex); }
    }

    /// <summary>
    /// The shelf of notices the phone is holding, asked for the way the family asks for it: the moment a channel
    /// settles. A phone answers by sending everything it has and marking each one silent, which is why an island that
    /// asks after a reconnection is not an island that toasts its owner with yesterday's messages.
    /// </summary>
    public void AskNotices()
    {
        foreach (Phone phone in Standing()) _ = Holds(phone);
    }

    /// <summary>Ask one phone for the notices it holds, on its own wire and after whatever else was said on it.</summary>
    async Task Holds(Phone phone) =>
        await Send(phone, Frame("kdeconnect.notification.request", new JsonObject { ["request"] = true }), _turns);

    /// <summary>The phones this island stands talking with, trusted and channelled.</summary>
    List<Phone> Standing()
    {
        lock (_gate) return _live.Values.Where(one => one.Paired && one.Secure != null).ToList();
    }

    /// <summary>
    /// A word back about one notice, carried to the phone that showed it and spoken in that phone's own keys, which
    /// are not the ones the page holds. An answer goes back under the notice's reply key rather than its id: the
    /// phone files the box it answers into under that one, and a key made of the notice's own name finds nothing.
    /// A key the island no longer holds belongs to a notice whose phone has gone off the channel, and nothing is sent
    /// for it: the page is always a moment behind the wire, and a press there is no promise that the wire still stands.
    /// </summary>
    void About(string key, string what, string type, Func<(Phone Holder, string Id, string Reply), JsonObject> body)
    {
        (Phone Holder, string Id, string Reply) found;
        lock (_gate)
        {
            if (!_held.TryGetValue(key, out found))
            {
                Said($"a notice was pressed that no phone is holding here any more: {key}");
                return;
            }
        }

        Said($"{found.Holder.Name} is asked to {what}");
        _ = Send(found.Holder, Frame(type, body(found)), _turns);
    }

    /// <summary>Take a notice off the phone's own screen, as its owner would have.</summary>
    public void Dismiss(string key) => About(key, "put a notice away", "kdeconnect.notification",
        one => new JsonObject { ["cancel"] = one.Id });

    /// <summary>Press one of the buttons the phone put under its notice, by the name the phone gave it.</summary>
    public void Press(string key, string action) => About(key, $"press “{action}”", "kdeconnect.notification.action",
        one => new JsonObject { ["key"] = one.Id, ["action"] = action });

    /// <summary>Answer a notice with the words the island was holding when its owner pressed.</summary>
    public void Reply(string key, string message) => About(key, $"answer with {message.Length} signs",
        "kdeconnect.notification.reply",
        one => new JsonObject { ["requestReplyId"] = one.Reply, ["message"] = message });

    // ------------------------------------------------------ this machine's sayings, said at the phone

    /// <summary>
    /// A saying of this machine, put on the phone's own screen. The phone shows a notice a desktop sends it under the
    /// notice's own type, but by a plugin the owner has to switch on for this device, and that plugin asks for three
    /// fields by name before it builds anything: who the saying comes from, what it says, and a key of its own. A
    /// packet missing any of the three is dropped without a word, and one marked silent is dropped on purpose — so the
    /// island names all three and never says silent.
    /// The key is a counting number rather than the long string a phone gives its own notices, since the phone files
    /// what it shows under a whole number and two sayings sharing one are the same one on its screen. A saying sent
    /// this way does not come back as a notice from the phone: the app keeps notifications of its own package out of
    /// what it forwards, and marks this one local besides.
    /// </summary>
    public void Announce(string app, string text)
    {
        List<Phone> phones = Standing();
        if (phones.Count == 0)
        {
            Said($"a saying was ready for the phone and no phone stands here: {app}");
            return;
        }

        string id = (++_spoken).ToString();
        foreach (Phone phone in phones)
            _ = Send(phone, Frame("kdeconnect.notification", new JsonObject
            {
                ["appName"] = app,
                ["ticker"] = text,
                ["id"] = id,
            }), _turns);
        Said($"{app} is said on {phones.Count} phone: {Trimmed(text)}");
    }

    /// <summary>
    /// A copy from this machine, laid on the phone's clipboard. The phone's clipboard plugin takes words under the very
    /// type it sends them with, and writes them into its own record of what its clipboard holds before the system
    /// notices the change — which is why a copy across the wire does not come straight back over it. Only words
    /// cross: a phone's clipboard, as that plugin reads it, is one string, and a picture or a file of this machine has
    /// no shape to be poured into it.
    /// </summary>
    public void Pass(string text)
    {
        List<Phone> phones = Standing();
        if (phones.Count == 0) return; // a copy nobody is standing to take is not worth a line in the log
        foreach (Phone phone in phones)
            _ = Send(phone, Frame("kdeconnect.clipboard", new JsonObject { ["content"] = text }), _turns);
        Said($"a copy of {text.Length} signs went to {phones.Count} phone");
    }

    /// <summary>The number the next saying is filed under on the phone. Started from the clock so an island that is
    /// closed and opened again does not replace the saying the last run left on the phone's bar.</summary>
    long _spoken = Environment.TickCount64;

    /// <summary>A saying shortened for the log, which is read one line at a time.</summary>
    static string Trimmed(string text) => text.Length <= 70 ? text : text[..70] + "…";

    // ------------------------------------------------------------- this machine's music, at the phone's word

    /// <summary>
    /// The one player this island offers a phone: this machine's music, whichever program is making it. The family
    /// lists players by name and asks about each under that name, and an island with a dozen sessions behind it still
    /// has one set of buttons — so it answers for one name, and drives whatever is really playing behind it.
    /// </summary>
    const string Player = "DynamicIsland";

    /// <summary>
    /// What the phone said about this machine's music. The asking and the doing are one packet type and are told apart
    /// by their fields: a list asked for is given back whole, a state asked for is given back for one player, and a
    /// button named is a button pressed here. The two counts the phone sends for where a song should be put are not in
    /// one coin — `SetPosition` is milliseconds of the song, `Seek` is microseconds of a stride over it — and both are
    /// read here as the phone means them, in seconds. A loop and a shuffle are the phone's own asking: no music on
    /// this machine is driven by a button that promises them, so nothing is said back and nothing is pretended.
    /// </summary>
    async Task Music(Phone phone, Packet packet, CancellationToken token)
    {
        JsonElement body = packet.Body;
        string who = Text(body, "player");

        if (Flag(body, "requestPlayerList"))
        {
            Said($"{phone.Name} asked what music this machine plays");
            await Send(phone, Frame("kdeconnect.mpris", new JsonObject
            {
                ["playerList"] = new JsonArray(JsonValue.Create(Player)),
            }, packet.Id), token);
            return;
        }

        if (who.Length > 0 && who != Player) return; // a player this island does not answer for is not its to drive

        if (Flag(body, "requestNowPlaying") || Flag(body, "requestVolume"))
        {
            await Say(phone, token);
            return;
        }

        string action = Text(body, "action");
        if (action.Length > 0)
        {
            Bridge.Command? order = action switch
            {
                "PlayPause" => new Bridge.Command("toggle"),
                "Play" => new Bridge.Command("play"),
                "Pause" => new Bridge.Command("pause"),
                "Stop" => new Bridge.Command("stop"),
                "Next" => new Bridge.Command("next"),
                "Previous" => new Bridge.Command("previous"),
                _ => null,
            };
            if (order != null) Said($"{phone.Name} asked the music to {action}");
            else Said($"{phone.Name} asked the music to {action}, which this machine has no button for");
            if (order != null) _bridge.Order(order);
            return;
        }

        if (Whole(body, "SetPosition") is long at)
        {
            Said($"{phone.Name} put the music at {at / 1000} seconds");
            _bridge.Order(new Bridge.Command("seek", at / 1000.0));
        }
        else if (Whole(body, "Seek") is long stride) // counted in microseconds, the way the phone's own stride is set
        {
            Said($"{phone.Name} moved the music by {stride / 1_000_000} seconds");
            _bridge.Order(new Bridge.Command("skip", stride / 1_000_000.0));
        }
        else if (Whole(body, "setVolume") is long level)
        {
            Said($"{phone.Name} set this machine at {level} hundredths of its loudest");
            _bridge.Order(new Bridge.Command("volume", level / 100.0));
        }
    }

    /// <summary>
    /// This machine's music, said to every phone that stands talking. A phone looks once when its media page opens and
    /// listens after, so a change left unsaid is a phone showing yesterday's song until its owner opens the page again.
    /// A song merely moving is not a change worth a packet — the phone counts the going from the moment it was told —
    /// and one is said every half-minute at most while it plays, so that count does not drift.
    /// </summary>
    public void Mirror(Bridge.Playing state)
    {
        bool changed = NotTheSameAs(_playing, state);
        bool drifted = state.IsPlaying && DateTime.UtcNow - _mirrored > TimeSpan.FromSeconds(30);
        _playing = state;
        if (!changed && !drifted) return;
        _mirrored = DateTime.UtcNow;
        foreach (Phone phone in Standing()) _ = Say(phone, _turns);
    }

    static bool NotTheSameAs(Bridge.Playing was, Bridge.Playing now) =>
        was.Title != now.Title || was.Artist != now.Artist || was.IsPlaying != now.IsPlaying
        || was.LengthMs != now.LengthMs || was.Volume != now.Volume || was.Seekable != now.Seekable;

    /// <summary>The state of this machine's music, in the coin a phone's media page is built to read.</summary>
    async Task Say(Phone phone, CancellationToken token)
    {
        Bridge.Playing now = _playing;
        var body = new JsonObject
        {
            ["player"] = Player,
            ["title"] = now.Title,
            ["artist"] = now.Artist,
            ["isPlaying"] = now.IsPlaying,
            ["canPlay"] = true,
            ["canPause"] = true,
            ["canGoNext"] = true,
            ["canGoPrevious"] = true,
            ["canSeek"] = now.Seekable,
            ["volume"] = now.Volume,
            // the island has no picture of the song to hand over, and a phone told so goes and looks for one itself
            ["supportAlbumArtPayload"] = false,
        };
        if (now.LengthMs > 0)
        {
            body["length"] = now.LengthMs;
            body["pos"] = Math.Clamp(now.PositionMs, 0, now.LengthMs);
        }

        await Send(phone, Frame("kdeconnect.mpris", body), token);
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

    /// <summary>
    /// The picture a notice rides with, taken from the door the phone opened for it. A payload of this family never
    /// travels down the packet channel: `LanLink.sendPacket` binds a port of its own on the phone's road, names it at
    /// the packet's root beside the byte count, waits ten seconds for the far side to knock, and writes the bytes there
    /// once they have been asked for over TLS. So the island crosses to that port exactly as it crosses for a promised
    /// file — and a picture nobody knocks for is not lost, since the phone only stops offering it for that one notice
    /// and sends it again at the next, the count over the bytes being what tells the two apart.
    /// </summary>
    async Task<byte[]?> Art(Phone phone, Packet packet, CancellationToken token)
    {
        // the family puts a payload's promise at the packet's root, but the notices plugin has been seen to write its
        // count into the body beside the hash, so both places are read — a picture is worth the second look
        long? promised = Whole(packet.Body, "payloadSize") ?? Whole(packet.Root, "payloadSize");
        if (promised is not long size || size <= 0) return null;
        int port = 0;
        foreach (JsonElement where in new[] { packet.Root, packet.Body })
        {
            if (where.TryGetProperty("payloadTransferInfo", out JsonElement door)
                && door.ValueKind == JsonValueKind.Object)
            {
                port = Number(door, "port") ?? 0;
                if (port > 0) break;
            }
        }
        if (port <= 0 || phone.Address.Length == 0)
        {
            // a promise of bytes with no door to bring them through is worth one honest line, since without it an
            // owner sees a notice without a picture and no telling why
            Said($"{phone.Name} promised a notice picture of {size} bytes and named no door to carry it through");
            return null;
        }
        if (size > LargestPicture)
        {
            Said($"{phone.Name} offers a notice picture of {size} bytes, past what the island keeps");
            return null;
        }

        // The phone hangs up on this crossing after its own ten seconds, so the island's patience is shorter than the
        // notice's worth of waiting and a dozing phone costs a picture rather than a pause in the talking.
        using var breath = CancellationTokenSource.CreateLinkedTokenSource(token);
        breath.CancelAfter(Hurried);
        var pile = new MemoryStream((int)size);
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

            var spare = new byte[(int)Math.Min(size, 16384)];
            while (pile.Length < size)
            {
                int got = await wire.ReadAsync(spare, 0, spare.Length, breath.Token);
                if (got <= 0) break;
                pile.Write(spare, 0, got);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException
            or OperationCanceledException or ArgumentException or InvalidOperationException)
        {
            // a picture that did not arrive leaves the notice whole; only a channel closing around it says nothing
            if (!token.IsCancellationRequested)
            {
                App.Log(new IOException($"{phone.Name} offered a notice picture that did not arrive: {ex.Message}", ex));
            }
            return null;
        }

        return pile.Length >= size ? pile.ToArray() : null;
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
            ["outgoingCapabilities"] = new JsonArray(CanSay.Select(s => (JsonNode)s).ToArray()),
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

    /// <summary>
    /// The plugins of a phone that find something to do with an island: what it listens for, by their names. The names
    /// are the packet types themselves, and they have to be spelled as the phone spells them, because the app decides
    /// which of its plugins to run for a device by matching these two lists against each plugin's own — see
    /// <see cref="CanSay" />. A type named wrongly is a plugin the phone sees no reason to keep for this island.
    /// </summary>
    static readonly string[] CanHear =
    [
        // a notice arrives as `kdeconnect.notification`, singular, the way the phone's outgoing names it; a file
        // arrives as `kdeconnect.share.request`, and a `kdeconnect.share` of our inventing matches nothing; a phone's
        // media page asks about this machine's music under `kdeconnect.mpris.request`, and hears it under the shorter
        // name — see <see cref="CanSay" />
        "kdeconnect.battery", "kdeconnect.notification", "kdeconnect.clipboard", "kdeconnect.share.request",
        "kdeconnect.mpris.request",
    ];

    /// <summary>
    /// What this island speaks back, named the same way, and just as much a matter of the phone's arithmetic: a plugin
    /// is kept for a device when the device can send one of the types the plugin can hear, or receive one of the types
    /// the plugin sends. The notices plugin answers an ask, a reply and a button press, and takes a notice away under
    /// the notice's own type; the media page of a phone only runs its controls for a device that says it can be told
    /// what it is playing.
    /// </summary>
    static readonly string[] CanSay =
    [
        // the clipboard is named here as well as below: the phone keeps its clipboard plugin for a device that can
        // both send it and hear it, and this island now does — a copy made here is laid on the phone's clipboard, and
        // one made there is kept here
        "kdeconnect.notification", "kdeconnect.notification.request", "kdeconnect.notification.reply",
        "kdeconnect.notification.action", "kdeconnect.mpris", "kdeconnect.clipboard",
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
    /// back over whatever channel that phone now stands on, which is not necessarily the one its ask came over — a
    /// phone crosses itself anew while a person reads the pill, and a yes written down a channel that has ended is a
    /// yes that never leaves this machine while the phone's screen goes on asking.
    /// </summary>
    public void Answer(bool yes)
    {
        (string Id, string Name, string Fingerprint)? asked;
        Phone? phone;
        lock (_gate)
        {
            asked = _pending;
            _pending = null;
            // a place taken at the door before any channel exists carries no wire yet, and nothing is written there
            phone = asked is { } want && _live.TryGetValue(want.Id, out Phone? now) && now.Secure != null ? now : null;
        }
        if (asked == null) return;

        Said($"the owner said {(yes ? "yes" : "no")} to {asked.Value.Name}"
            + (phone == null ? ", with no channel standing under that name to carry it" : ""));
        if (yes)
        {
            // trusted by the fingerprint the owner judged, whether or not a channel is here to be told now: a phone
            // that crosses again a second later is found trusted on that new channel and answered from there
            Trust(asked.Value.Id, asked.Value.Name, asked.Value.Fingerprint);
            if (phone != null)
            {
                phone.Paired = true;
                // the yes is on its way, so the phone's own yes is expected back — and must not be answered with a
                // second one. The shelf of notices it holds is asked for when that answer arrives, not here.
                phone.Confirming = DateTime.UtcNow.AddSeconds(30);
            }
        }
        if (phone != null) _ = Task.Run(async () =>
        {
            await Send(phone, Frame("kdeconnect.pair", PairBody(yes)), _turns);
            // and only after the yes has left this machine is the phone asked what it holds. The order on the one wire
            // is what matters, not the delay: the app marks itself paired as it reads the yes, and a notice request
            // that arrived first would be a plugin packet reaching a phone that does not yet stand paired — which the
            // app takes for the other side having unpaired, and answers by unpairing (Device.onPacketReceived)
            if (yes && Settings.PhoneNotices) await Holds(phone);
        }, _turns);
        _bridge.Answer(asked.Value.Name, yes);
    }

    // ------------------------------------------------------------- trust, kept between runs

    static void Trust(string id, string name, string fingerprint)
    {
        try
        {
            List<string> known = Phones().ToList();
            string entry = $"{id}\t{name}\t{fingerprint}";
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
    /// Whether a phone said a thing as true. Absent is false, as the family means it, and so is anything that is not a
    /// saying of yes: the app spells most of its flags as JSON words, but a phone counting its own packet may send a
    /// number or a word of text, and the one left unread is a charge drawn as though the phone were running down.
    /// </summary>
    static bool Flag(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out JsonElement value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => (value.TryGetInt32(out int told) ? told : value.TryGetDouble(out double part) ? part : 0) != 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out bool word) && word,
            _ => false,
        };
    }

    /// <summary>A list of packet types said as names, which is how a phone counts out the plugins it is running.</summary>
    static string[] Names(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out JsonElement found)
            && found.ValueKind == JsonValueKind.Array
            ? found.EnumerateArray().Where(one => one.ValueKind == JsonValueKind.String)
                .Select(one => one.GetString()!).ToArray()
            : [];

    /// <summary>
    /// The buttons a notice carries, in the order the phone puts them under it. The app names them plainly, and no
    /// island can press one it cannot name: the press is carried back to the phone by that same name.
    /// </summary>
    static string[] Titles(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("actions", out JsonElement value)
        && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(one => one.ValueKind == JsonValueKind.String).Select(one => one.GetString() ?? "")
                .Where(one => one.Length > 0).Take(2).ToArray()
            : [];

    /// <summary>
    /// The moment a notice was put up. The app counts it out in milliseconds and says it as text, the desktop as a
    /// number, and an island that reads only one of the two sorts the other phone's shelf as notices from no time at all.
    /// </summary>
    static long When(JsonElement body) =>
        Whole(body, "time") ?? (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("time", out JsonElement value) && value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), out long counted) ? counted : 0);

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

        /// <summary>Until when a pair packet from this phone is taken as its own yes to the one the island has just
        /// said, rather than as a fresh ask to be answered back. The two are made of the same words, and answering a
        /// confirmation is how a phone comes to unpair a pairing it has agreed to.</summary>
        public DateTime Confirming = DateTime.MinValue;

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
    /// <summary>What a phone says it is, and the packet types it says it can send — which are the plugins it runs,
    /// named as those plugins name themselves.</summary>
    sealed record Identity(string Id, string Name, string Address, int Port, int Protocol, string[] Sends)
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
                return new Identity(id, name, from, given is >= 1716 and <= 1764 ? given : TcpPort, protocol,
            Names(body, "outgoingCapabilities"));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                return null;
            }
        }
    }
}
