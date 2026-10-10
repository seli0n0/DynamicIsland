using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using static DynamicIsland.Motion;

namespace DynamicIsland;

/// <summary>
/// The island's side of the bridge: the page that shows the code a phone reads, the switches that open the doors, and
/// the answers to what arrives through them. A saying goes to the clipboard, where the ring of copies already watches;
/// a file goes to the shelf, beside the ones dragged there from Explorer.
/// </summary>
public partial class MainWindow
{
    readonly Bridge _bridge = new();
    string _phoneGuest = "";

    /// <summary>What each phone has said of its own battery, kept under the name it came by. One figure for the whole
    /// house would let a silent phone's charge answer for the one the page is looking at; a phone that has said nothing
    /// is shown as no figure at all.</summary>
    readonly Dictionary<string, (int level, bool charging)> _charges = [];

    Bridge.Asking? _pairAsk;

    /// <summary>
    /// What the phones are holding, newest first — the shelf the page shows, and the reason a notice is not a flash that
    /// is gone in three seconds. All of it is kept, since the phone counts its own and an island that forgets half of
    /// those would answer to nothing for them; the page scrolls rather than hiding what its owner asked for.
    /// </summary>
    readonly List<Bridge.Notice> _held = [];

    const int Held = 20; // what the island keeps of what the phones hand it before its oldest plate is dropped

    /// <summary>The plates the owner has unfolded, kept by the notice's own key so a refresh does not fold away the
    /// message being read while a new one arrives.</summary>
    readonly HashSet<string> _readNotices = [];

    /// <summary>What the shelf was last built from. A phone that is holding its notices says so again on every
    /// reconnection, and a shelf rebuilt for a saying that changed nothing would cut short the fold the owner is
    /// halfway through opening.</summary>
    string _shelfLaid = "";

    /// <summary>
    /// An answer given from the command line instead of by hand, for checking both ways of the door on a running
    /// island. A person who can start the island with a switch can already reach the page, so nothing is let in here
    /// that the page does not offer.
    /// </summary>
    bool? _answeredAhead = Of(Argument("--pair"));

    static bool? Of(string? answer) => answer?.ToLowerInvariant() switch
    {
        "yes" or "on" or "1" => true,
        "no" or "off" or "0" => false,
        _ => null,
    };

    void WireBridge()
    {
        _bridge.Said += said => OnUi(() => PhoneSaid(said));
        _bridge.Laid += path => OnUi(() => PhoneLaid(path));
        _bridge.Visited += who => OnUi(() => { _phoneGuest = who; RefreshPhone(); });
        _bridge.Moved += () => OnUi(RefreshPhone); // the QR page and its address are one thing; both are redrawn
        _bridge.Battery += (who, level, charging) => OnUi(() =>
        {
            // a phone answers for its charge again on every reconnection, so only a word that changes the answer —
            // off the charger, then on it — is worth the pill
            bool wasOff = who.Length > 0 && _charges.TryGetValue(who, out (int at, bool on) before) && !before.on;
            if (who.Length > 0) _charges[who] = (level, charging);
            RefreshPhone();
            if (wasOff && charging && level >= 0) ShowPhoneCharge(level);
        });
        _bridge.Noticed += notice => OnUi(() => PhoneNoticed(notice));
        _bridge.NoticeGone += key => OnUi(() => PhoneNoticeGone(key));
        _bridge.PhoneLeft += (gone, device) => OnUi(() => PhoneLeft(gone, device));
        _bridge.Ordered += order => OnUi(() => PhoneOrdered(order));
        _bridge.Wanted += ask => OnUi(() =>
        {
            _pairAsk = ask;
            RefreshPhone();
            ShowPairAsk();
            if (_answeredAhead is { } given)
            {
                _answeredAhead = null; // the switch answers the first request only, the rest are the owner's
                _bridge.AnswerPair(given);
            }
        });
        _bridge.Answered += (who, paired) => OnUi(() =>
        {
            _pairAsk = null;
            RefreshPhone();
            // a refusal is seen on the page the moment it is given; only a yes needs saying out loud
            if (paired) Notify(Glyph.Phone, _green, "Пара разрешена", who, force: true);
            else UpdateView();
        });

        // a brand's picture arrives whenever the base answers, which is usually a heartbeat after the notice that
        // asked for it: the rows are redrawn, and a pill still showing that same app is given the picture in place
        // of the cut it went out with
        PhoneArt.Branded += () => OnUi(() =>
        {
            RefreshPhone();
            if (_view == View.Notice && PhoneArt.Brand(_noticeApp) is { } brand) ShowNoticeArt(brand, _noticeApp);
        });
    }

    /// <summary>
    /// The ask is put in the pill itself, with its answer, because the phone waits at the door for as long as the owner
    /// takes and the island is the only one who can open it. So the pill stays on the ask rather than on the music until
    /// it is answered — and the page underneath still holds the fingerprints, which are the last word.
    /// </summary>
    void ShowPairAsk()
    {
        if (_pairAsk is not { } ask) return;
        PairAskWho.Text = ask.Phone;
        // the eight signs are what the phone's own pairing screen shows, so the two are read across from each other
        PairAskCode.Text = ask.Code.Length > 0 ? "код " + ask.Code : "отпечатки · см. страницу";

        if (_panel != Panel.None)
        {
            // a page already open keeps its ground for a moment, and the ask arrives as a ring about it
            Notify(Glyph.Phone, _orange, "Телефон просит пару", $"{ask.Phone} · ответьте в пилюле", force: true);
            return;
        }
        _transientView = null;
        _transientTimeout.Cancel();
        UpdateView();
    }

    /// <summary>Bridge words arrive on the wire's own threads; the island is only touched from its own.</summary>
    void OnUi(Action action)
    {
        try { Dispatcher.InvokeAsync(action); }
        catch (Exception ex) { App.Log(ex); }
    }

    void PhoneSaid(string text)
    {
        _fromPhone = Lines(text); // heard here, and so not worth sending back the moment the clipboard is read
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Notify(Glyph.Phone, _red, "Текст не лёг", "Буфер обмена занят другим окном");
            return;
        }
        Copied(); // the same ring that watches Ctrl+C keeps the phone's saying, and its toast
    }

    void PhoneLaid(string path)
    {
        _shelf.Add(new[] { path });
        Notify(Glyph.Phone, _green, "Файл на полке", Path.GetFileName(path));
    }

    // ------------------------------------------------------------- what the phone is holding

    /// <summary>
    /// A notice as the phone has it now. One that was already there when the channel opened goes quietly onto the
    /// shelf; one that has just arrived is said in the pill as well, since that is what a notice is for.
    /// </summary>
    void PhoneNoticed(Bridge.Notice notice)
    {
        if (!Settings.PhoneNotices) return;
        // the shelf the page shows is one phone's, and a message is the strongest word a phone has of being here — so
        // the page turns to whoever last said something rather than to whoever last opened a channel and fell silent
        _phoneGuest = notice.Phone;
        int at = _held.FindIndex(held => held.Id == notice.Id);
        if (at < 0) _held.Insert(0, notice);
        else _held[at] = notice; // the same notice said again is one notice, in the place it already had
        while (_held.Count > Held) _held.RemoveAt(_held.Count - 1);

        if (!notice.Silent) Notify(Glyph.Note, _indigo, notice.Heading, notice.Saying, art: ArtOf(notice), app: notice.App);
        RefreshPhone();
    }

    /// <summary>The owner took a notice off the phone, which is the only way it leaves the shelf besides the phone's word.</summary>
    void PhoneNoticeGone(string key)
    {
        if (_held.RemoveAll(held => held.Id == key) == 0) return;
        RefreshPhone();
    }

    /// <summary>
    /// A phone that has gone off the channel takes its whole shelf with it; what is left would be a lie. The phone the
    /// page names at its head takes its charge with it too — the page answers for that one phone's words, and a figure
    /// kept after the mouth that spoke it has gone is a figure of nobody. Notices are numbered by the sign the phone
    /// came in under, while the page and the charge are known by the name it chose, so a leaving has to carry both.
    /// </summary>
    void PhoneLeft(string gone, string device)
    {
        string lead = gone + "/";
        int left = _held.RemoveAll(held => held.Id.StartsWith(lead));
        _charges.Remove(device);
        if (device != _phoneGuest)
        {
            if (left > 0) RefreshPhone();
            return;
        }
        _phoneGuest = "";
        RefreshPhone();
    }

    // --------------------------------------------------- what the phone asked about this machine's music

    /// <summary>
    /// A button pressed on the phone's own media page. The island does not have a second way of driving its music:
    /// these are the same services the player on the glass answers through, so a press across the network and a press
    /// beside the song are one gesture. Where the song is put is a part of a length the island can see an end of — a
    /// stream has no end to be a part of, and the asking is left unanswered rather than guessed at.
    /// </summary>
    void PhoneOrdered(Bridge.Command order)
    {
        switch (order.What)
        {
            case "toggle": _media.TogglePlay(); break;
            case "play": if (!_media.IsPlaying) _media.TogglePlay(); break;
            case "pause": if (_media.IsPlaying) _media.TogglePlay(); break;
            // a stop is taken as a pause: this machine's music is answered through the system's own transport, and
            // that transport has no button that puts a song back to its beginning and turns the player off
            case "stop": if (_media.IsPlaying) _media.TogglePlay(); break;
            case "next": _media.Next(); break;
            case "previous": _media.Previous(); break;
            case "seek": PutAt(order.At); break;
            case "skip": PutAt(_media.Position.TotalSeconds + order.At); break;
            case "volume": _audio.SetVolume((float)order.At); break;
        }
    }

    /// <summary>Move the song to a second of it, as far as the island can see its whole length.</summary>
    void PutAt(double seconds)
    {
        if (_media.Duration.TotalSeconds < 1) return;
        _media.Seek(Math.Clamp(seconds / _media.Duration.TotalSeconds, 0, 1));
    }

    /// <summary>
    /// This machine's music, said outward whenever the island notices it has moved. A phone's media page looks once
    /// when it opens and listens after, so a change left unsaid is a phone showing yesterday's song until its owner
    /// goes and opens the page again.
    /// </summary>
    void MirrorMusic()
    {
        if (!Settings.Bridge || !Settings.BridgeKde) return;
        TimeSpan length = _media.Duration;
        _bridge.Mirror(new Bridge.Playing(
            _media.HasTrack ? _media.Name : "",
            _media.Artist,
            _media.IsPlaying,
            (long)_media.Position.TotalMilliseconds,
            length.TotalSeconds >= 1 ? (long)length.TotalMilliseconds : -1,
            _media.Seekable,
            _audio.TryGetVolume(out float level, out bool muted) ? (int)Math.Round((muted ? 0 : level) * 100) : 0));
    }

    /// <summary>
    /// The notices of the phone the page names at its head. The island keeps what every phone it has met handed it, and
    /// a page that says whose shelf it is must not answer for another phone's messages — nor count two shelves as one.
    /// </summary>
    List<Bridge.Notice> PhoneShelf() => _held.Where(held => held.Phone == _phoneGuest).ToList();

    void SyncPhoneNotices()
    {
        // A phone says what it holds again on every reconnection, and its whole shelf arrives packet by packet: a page
        // rebuilt for every one of those would tear down the plate a person is reading, its fold mid-animation, and
        // the picture under the pointer, several times a second. What is laid is therefore compared with what is
        // already laid, and only a shelf that has really changed is built again.
        string laid = ShelfSignature();
        if (laid == _shelfLaid) return;
        _shelfLaid = laid;

        PhoneNoticeRows.Children.Clear();
        if (!Settings.PhoneNotices)
        {
            PhoneNoticeHint.Text = "Выключены";
            PhoneNoticeHint.Visibility = Visibility.Visible;
            PhoneNoticeCountPlate.Visibility = Visibility.Collapsed;
            PhoneNoticeAsk.Visibility = Visibility.Collapsed;
            return;
        }

        List<Bridge.Notice> shelf = PhoneShelf();
        // asking what a phone holds is a word to that phone, and with nobody on the channel it is a button that answers
        // nothing — the hint under it already says the phone has not come
        PhoneNoticeAsk.Visibility = Settings.Bridge && _phoneGuest.Length > 0
            ? Visibility.Visible : Visibility.Collapsed;
        PhoneNoticeCountPlate.Visibility = shelf.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // the island keeps the latest twenty and the phone keeps its own count, so a shelf taken to its end is said as
        // a floor rather than as a number the phone does not agree to
        PhoneNoticeCount.Text = shelf.Count >= Held ? $"{Held}+" : shelf.Count.ToString();
        foreach (Bridge.Notice notice in shelf) PhoneNoticeRows.Children.Add(PhoneNoticeRow(notice));
        PhoneNoticeHint.Text = !Settings.Bridge ? "Мост выключен"
            : _phoneGuest.Length > 0 ? "Телефон ничего не держит" : "Телефон ещё не заходил";
        PhoneNoticeHint.Visibility = shelf.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Everything a plate is drawn from, in one line: the shelf's words, whose picture the base has answered
    /// for, which of them the owner has unfolded, and the page's own switches.</summary>
    string ShelfSignature() => string.Join(';',
        $"{Settings.PhoneNotices}{Settings.Bridge}{_phoneGuest}",
        string.Join(';', PhoneShelf().Select(held =>
            $"{held.Id}|{held.App}|{held.Title}|{held.Text}|{held.Time}|{PhoneArt.Brand(held.App) != null}")),
        string.Join(',', _readNotices.Order()));

    /// <summary>
    /// A plate that unfolds, and a phone that adds one to the shelf, make the page taller while it is being looked at,
    /// so it is measured again as it grows rather than once it has stopped: a fold into a pill too short for the whole
    /// of it would show half a message and call it read.
    /// </summary>
    void PhoneShelf_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        PinnedPage page = _pages[View.Phone];
        page.Fit();
        if (page.Host == _views[_view]) UpdateTargets();
    }

    /// <summary>The width of a plate's head sign, and how far its words are indented under it: the sign whole, plus the
    /// room a word keeps from the sign beside it.</summary>
    const double Mark = 20, MarkIndent = Mark + 8;

    /// <summary>
    /// The sign at the head of a row: the app's own picture when the base of icons has answered for it, and the
    /// island's bell when it has not. Both are laid on a tile of the same size, since a row's head is otherwise as tall
    /// as whatever the phone sent, and a shelf of notices from apps nobody knows reads as a column of loose marks
    /// rather than as a shelf of pictures. The picture is cut to the island's corner rather than laid square over the
    /// row, since a phone draws its icons as tiles and this page draws its signs as round ones. A white cut stands on
    /// the app's colour, with the colour showing round its edges; a picture that came coloured fills the tile as it is.
    /// </summary>
    FrameworkElement PhoneNoticeMark(Bridge.Notice notice)
    {
        var tile = new Border
        {
            Width = Mark, Height = Mark, CornerRadius = new CornerRadius(5),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // a notice is a bell, and the island's note glyph is a song's: a shelf that could not name its app used to say
        // so with the wrong thing
        if (ArtOf(notice) is not { } art)
        {
            tile.Child = new Icon
            {
                Kind = Glyph.Bell, Width = 13, Height = 13, Fill = (Brush)FindResource("Dim"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            return tile;
        }

        bool cut = PhoneArt.Silhouette(art);
        tile.Background = cut ? new SolidColorBrush(PhoneArt.Of(notice.App)) : Brushes.Transparent;
        tile.Child = new Image
        {
            Source = PhoneArt.Framed(art), Stretch = Stretch.Uniform, Margin = new Thickness(cut ? 3 : 0),
        };
        return tile;
    }

    /// <summary>
    /// The picture a notice is shown with: the app's own icon once the base of icons has answered for it, and the
    /// phone's cut until then. A brand's real artwork beats anything the phone sends, which is the whole reason for
    /// asking for it.
    /// </summary>
    ImageSource? ArtOf(Bridge.Notice notice) => PhoneArt.Brand(notice.App) ?? ArtOf(notice.Art);

    /// <summary>
    /// A phone's picture drawn from the bytes it sent with its notice. A PNG that arrived cut short is no picture at
    /// all, and a row is better bare than broken: the notice keeps its words either way.
    /// </summary>
    ImageSource? ArtOf(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(bytes);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            App.Log(ex);
            return null;
        }
    }

    /// <summary>
    /// One notice of the phone, drawn as a plate rather than as a line cut short: the app's own name at the head with
    /// its picture and one clock beside it, the words across the whole width under it, and nothing else asked of the
    /// owner but the bin the message goes to. A saying longer than two lines keeps its end folded until the owner
    /// presses the plate, since a page of nothing but messages would push the rest of the shelf off the pill.
    /// </summary>
    Button PhoneNoticeRow(Bridge.Notice notice)
    {
        var stack = new StackPanel();
        var plate = new Button { Style = (Style)FindResource("NoticePlate"), Content = stack };

        // The head is three columns: the sign takes what it takes, the tail takes what it takes, and the name is left
        // with the room between them. A name given a room guessed for the tail instead trimmed early and left the clock
        // standing at the plate's far edge with a gap of nothing between the two — and the guess was wrong for every
        // notice whose app's name was short.
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement mark = PhoneNoticeMark(notice);
        Grid.SetColumn(mark, 0);
        head.Children.Add(mark);

        var name = new TextBlock
        {
            Text = notice.App.Length > 0 ? notice.App : "Приложение", FontSize = 13, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);
        head.Children.Add(name);

        var tail = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        // a notice that carries no moment of its own keeps no place for one; a clock that says nothing was read as a
        // clock that had gone wrong
        var clock = new TextBlock
        {
            Text = notice.Clock, FontSize = 11.5, Foreground = (Brush)FindResource("Dim"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            Visibility = notice.Clock.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        tail.Children.Add(clock);
        tail.Children.Add(NoticeBin(notice));
        Grid.SetColumn(tail, 2);
        head.Children.Add(tail);
        stack.Children.Add(head);

        // the phone's own title, then what it said under that title; an app that sent only one of the two sends it plain
        var body = new TextBlock { FontSize = 12, LineHeight = 16, TextWrapping = TextWrapping.Wrap };
        if (notice.Title.Length > 0 && notice.Text.Length > 0)
        {
            body.Inlines.Add(new Run(notice.Title) { FontWeight = FontWeights.SemiBold });
            body.Inlines.Add(new LineBreak());
        }
        body.Inlines.Add(new Run(notice.Text.Length > 0 ? notice.Text : notice.Title));

        var words = new Border { Margin = new Thickness(MarkIndent, 4, 8, 0), ClipToBounds = true, Child = body };
        string said = notice.Text.Length > 0 ? notice.Text : notice.Title;
        double whole = 0, fold = 0;
        if (said.Length > 0)
        {
            // measured at the width the plate has left for words: the page's 300, less the plate's edges and the indent
            body.Measure(new Size(PhoneBody.Width - 22 - MarkIndent - 8, double.PositiveInfinity));
            whole = Math.Ceiling(body.DesiredSize.Height);
            // the line height is set rather than left to the font, so two lines of it are the fold's own height
            fold = Math.Min(whole, Math.Ceiling(body.LineHeight * 2));
            words.Height = fold;
            stack.Children.Add(words);
        }
        bool longer = whole > fold + 1;
        bool open = longer && _readNotices.Contains(notice.Id);
        if (open) words.Height = whole;
        else if (longer) words.OpacityMask = FoldFade();

        // a plate that hides the end of a message says so with a chevron of its own beside the clock, and the press
        // that opens it is the plate itself rather than a button under the words
        if (longer)
        {
            plate.Cursor = Cursors.Hand;
            var turn = new RotateTransform(open ? 90 : 0);
            tail.Children.Insert(1, new Icon
            {
                Kind = Glyph.Chevron, Width = 10, Height = 10, Fill = (Brush)FindResource("Faint"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0),
                RenderTransform = turn, RenderTransformOrigin = new Point(0.5, 0.5),
            });
            plate.Click += (_, _) =>
            {
                bool show = !_readNotices.Contains(notice.Id);
                if (show) _readNotices.Add(notice.Id);
                else _readNotices.Remove(notice.Id);

                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                if (!show) words.OpacityMask = FoldFade();
                var foldAgain = new DoubleAnimation(show ? whole : fold, Ms(260)) { EasingFunction = ease };
                // the fade rides the plate's own height, so it is only taken off once the last of the message stands
                // in the open; taken off at the first step, it would leave the end of it hard-cut for a quarter second
                if (show) foldAgain.Completed += (_, _) => words.OpacityMask = null;
                words.BeginAnimation(HeightProperty, foldAgain);
                turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(show ? 90 : 0, Ms(240))
                {
                    EasingFunction = ease,
                });
            };
        }
        return plate;
    }

    /// <summary>
    /// The mask a folded plate wears: its last shown line dims out towards the plate's ground instead of ending at a
    /// line cut in half, which is what let a message that went on look like one that had stopped. The fade is told in
    /// fractions of the plate's own height, so it rides the unfolding rather than being left behind by it.
    /// </summary>
    static Brush FoldFade()
    {
        var fade = new LinearGradientBrush { StartPoint = new Point(0, 0.7), EndPoint = new Point(0, 1) };
        fade.GradientStops.Add(new GradientStop(Colors.Black, 0));
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
        return fade;
    }

    /// <summary>
    /// The one button a notice plate keeps. It is the bin this copy of the message goes to: the phone is asked to put
    /// the notice away and the shelf drops it at once, since a plate that stayed after its own pressing would look
    /// like a button that does nothing.
    /// </summary>
    Button NoticeBin(Bridge.Notice notice)
    {
        var bin = new Button
        {
            Style = (Style)FindResource("BinButton"),
            Content = new Icon { Style = (Style)FindResource("BinMark") },
        };
        AutomationProperties.SetName(bin, "Убрать уведомление");
        ToolTipService.SetToolTip(bin, "Убрать");
        bin.Click += (_, e) =>
        {
            e.Handled = true; // the plate under it folds a message, and a press here is not a press there
            try { _bridge.DismissNotice(notice.Id); }
            catch (Exception ex) { App.Log(ex); }
            PhoneNoticeGone(notice.Id);
        };
        return bin;
    }

    void PhoneNotices_Click(object sender, RoutedEventArgs e)
    {
        Settings.PhoneNotices = !Settings.PhoneNotices;
        UpdateSwitches(true);
        if (!Settings.PhoneNotices) _held.Clear();
        else _bridge.AskNotices();
        RefreshPhone();
    }

    void PhoneNoticeAsk_Click(object sender, RoutedEventArgs e)
    {
        _bridge.AskNotices();
        Notify(Glyph.Note, _dim, "Спросил у телефона", "Что он держит, придёт сюда");
    }

    void PhoneAlerts_Click(object sender, RoutedEventArgs e)
    {
        Settings.PhoneAlerts = !Settings.PhoneAlerts;
        UpdateSwitches(true);
        // the phone keeps the plugin that shows a desktop's notices switched off until its owner turns it on, so the
        // switch answers itself: a saying that arrives on the phone's screen is the proof the door there is open
        if (Settings.PhoneAlerts) PhoneSay("Остров", "Теперь скажу здесь то, что звучит на этом компьютере");
    }

    void PhoneTimer_Click(object sender, RoutedEventArgs e)
    {
        Settings.PhoneTimer = !Settings.PhoneTimer;
        UpdateSwitches(true);
    }

    void PhoneClipboard_Click(object sender, RoutedEventArgs e)
    {
        Settings.PhoneClipboard = !Settings.PhoneClipboard;
        UpdateSwitches(true);
    }

    void PhoneRow_Click(object sender, RoutedEventArgs e)
    {
        RefreshPhone();
        _pages[View.Phone].Enter(); // entered afresh, so the shelf is seen from its newest plate and not from where it was left
        ShowPanel(Panel.Phone);
    }

    void PhoneBack_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.Menu);

    void PairYes_Click(object sender, RoutedEventArgs e) => _bridge.AnswerPair(true);

    void PairNo_Click(object sender, RoutedEventArgs e) => _bridge.AnswerPair(false);

    void Bridge_Click(object sender, RoutedEventArgs e)
    {
        Settings.Bridge = !Settings.Bridge;
        UpdateSwitches(true);
        if (Settings.Bridge) OpenBridge();
        else
        {
            _bridge.Stop();
            _phoneGuest = "";
            _charges.Clear();
            _pairAsk = null;
            _held.Clear(); // a closed door keeps no shelf of what the phone behind it was holding
            RefreshPhone();
        }
    }

    /// <summary>The KDE face is only chosen when the doors are opened, so a live bridge has to be opened again.</summary>
    void BridgeKde_Click(object sender, RoutedEventArgs e)
    {
        Settings.BridgeKde = !Settings.BridgeKde;
        UpdateSwitches(true);
        if (!Settings.Bridge) return;
        _bridge.Stop();
        _pairAsk = null;
        OpenBridge();
    }

    void OpenBridge()
    {
        try { _bridge.Start(); }
        catch (Exception ex)
        {
            App.Log(ex);
            Notify(Glyph.Phone, _red, "Мост не открылся", ex.Message, force: true);
        }
        RefreshPhone();
    }

    /// <summary>
    /// Both phone pages, the menu line and the address under the code, all from what the bridge says now. The charge is
    /// read out of what the phone at the head of the page has itself said: a phone that has said nothing yet is drawn as
    /// no figure at all, the page saying so in words, and the menu — too short a row for that saying — keeping its place
    /// empty rather than lending a figure from another phone.
    /// </summary>
    void RefreshPhone()
    {
        bool live = Settings.Bridge;
        string url = _bridge.Url;
        int shown = -1;
        bool charging = false;
        if (live && _charges.TryGetValue(_phoneGuest, out (int level, bool on) told))
        {
            shown = told.level;
            charging = told.on;
        }
        bool said = shown >= 0;
        PhoneUrl.Text = live && url.Length > 0 ? Address(url) : "—";
        PhoneNote.Text = live
            ? "Наведите камеру телефона на код · адрес можно открыть и вручную · брандмауэр должен разрешить острову вход"
            : "Включите мост, чтобы телефон перекидывал текст и файлы на остров";
        PhoneDevice.Text = live ? (_phoneGuest.Length > 0 ? _phoneGuest : "Не подключено") : "—";
        PhoneCharge.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        PhoneBattery.Text = said ? shown + "%" : "";
        PhoneBattery.Visibility = said ? Visibility.Visible : Visibility.Collapsed;
        PhoneBatteryNone.Visibility = said ? Visibility.Collapsed : Visibility.Visible;
        PhoneBatteryCell.Set(shown, charging, true);
        PhoneBatteryCell.Visibility = said ? Visibility.Visible : Visibility.Collapsed;
        MenuPhoneCharge.Visibility = said ? Visibility.Visible : Visibility.Collapsed;
        MenuPhoneCell.Set(shown, charging, true);
        MenuPhone.Text = said ? shown + "%" : "";
        if (live && url.Length > 0 && Code(url, out BitmapSource? code, out int side))
        {
            int grown = Math.Max(2, PlateRoom / side) * side;
            PhoneCode.Width = PhoneCode.Height = grown;
            PhoneCode.Source = code;
        }
        else PhoneCode.Source = null;
        CodePlate.Opacity = PhoneCode.Source == null ? 0.25 : 1;
        PairCard.Visibility = _pairAsk == null ? Visibility.Collapsed : Visibility.Visible;
        if (_pairAsk is { } ask)
        {
            PairWho.Text = ask.Phone;
            // the eight signs are the ones the phone's own pairing screen shows; the fingerprints are the last word
            PairPrint.Text = (ask.Code.Length > 0 ? "код " + ask.Code + "\n" : "")
                + $"у телефона {Print(ask.Theirs)}\nу острова {Print(ask.Ours)}";
        }
        SyncPhoneNotices();
        // the pages are measured by their words, and these words were not spoken yet when the pages were first fitted
        _pages[View.Phone].Fit();
        _pages[View.PhoneSet].Fit();
        UpdateTargets();
    }

    void PhoneSetRow_Click(object sender, RoutedEventArgs e)
    {
        _pages[View.PhoneSet].Enter();
        ShowPanel(Panel.PhoneSet);
    }

    void PhoneOutRow_Click(object sender, RoutedEventArgs e) => _pages[View.PhoneSet].Toggle(PhoneOutTiles);

    void CodeRow_Click(object sender, RoutedEventArgs e) => _pages[View.PhoneSet].Toggle(CodeTiles);

    /// <summary>
    /// The beginning of a fingerprint, in signs a person can read aloud. Sixteen of the sixty-four are enough to be
    /// sure two sides speak of the same certificate, and finding another that starts the same way is out of reach.
    /// </summary>
    static string Print(string fingerprint) =>
        fingerprint.Length < 16 ? fingerprint.ToUpperInvariant()
            : string.Join(' ', Enumerable.Range(0, 4).Select(i => fingerprint.Substring(i * 4, 4).ToUpperInvariant()));

    /// <summary>An address said without its beginning: the code carries the whole of it, and the line under it is only
    /// for the owner who would rather type the address than read a picture.</summary>
    static string Address(string url)
    {
        int at = url.IndexOf("://", StringComparison.Ordinal);
        return at < 0 ? url : url[(at + 3)..];
    }

    const int Quiet = 4, PlateRoom = 156; // signs of white the scanner needs around the code, and the room on the plate

    /// <summary>The code last drawn, the address it was drawn from, and the width of its signs. A page that is laid out
    /// again because a phone repeated what it holds does not need a new picture of the same address, and drawing one —
    /// a matrix of bytes, a bitmap, and a growth of it — every few seconds for a code that never changed spent the
    /// island's hour on nothing.</summary>
    static string _codeUrl = "";
    static BitmapSource? _codeDrawn;
    static int _codeSide;

    /// <summary>
    /// The code drawn from its closed modules: no library, only the matrix the island made itself. One pixel per
    /// module, and the picture is then grown by whole signs, so a cell on screen is never half a cell.
    /// </summary>
    static bool Code(string url, out BitmapSource? bitmap, out int side)
    {
        if (url == _codeUrl && _codeDrawn != null)
        {
            bitmap = _codeDrawn;
            side = _codeSide;
            return true;
        }

        bool[,]? cells = Qr.Make(url);
        side = cells == null ? 0 : cells.GetLength(0) + Quiet * 2;
        if (cells == null) { bitmap = null; return false; }

        var pixels = new byte[side * side * 4];
        Qr.Paint(cells, Quiet, pixels);
        var drawn = new WriteableBitmap(side, side, 96, 96, PixelFormats.Bgra32, null);
        drawn.WritePixels(new Int32Rect(0, 0, side, side), pixels, side * 4, 0);
        drawn.Freeze();
        _codeUrl = url;
        _codeDrawn = drawn;
        _codeSide = side;
        bitmap = drawn;
        return true;
    }
}
