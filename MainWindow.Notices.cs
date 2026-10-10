using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static DynamicIsland.Motion;

namespace DynamicIsland;

public partial class MainWindow
{
    const double NoticeBackgroundAlpha = 0.2;
    const double NoticeSeconds = 3.2;
    static readonly TimeSpan NoticeIconDelay = TimeSpan.FromMilliseconds(160);

    Action? _noticeOpen;

    void Notify(Glyph icon, SolidColorBrush tint, string title, string text, double seconds = NoticeSeconds, bool force = false,
        Glyph? from = null, bool warn = false, ImageSource? art = null, string app = "", Action? open = null)
    {
        if (_ringing && !force) return;

        bool shown = _view == View.Notice;
        Color color = tint.Color;
        NoticeIcon.Kind = icon;
        NoticeIcon.Fill = tint;
        ShowNoticeArt(art, app);
        NoticeBack.Background = new SolidColorBrush(Color.FromArgb((byte)(color.A * NoticeBackgroundAlpha), color.R, color.G, color.B));
        NoticeTitle.Text = title;
        NoticeText.Text = text;
        _noticeOpen = open;
        ShowTransient(View.Notice, seconds, force);
        if (shown) FadeIn(NoticeView);
        AnimateNoticeIcon(icon, from, warn);
    }

    /// <summary>
    /// The app's own picture, in the place its glyph would stand. Only a notice a phone sent carries one, and only
    /// when that phone has sent the app's bytes at some point; every other saying — a timer, the microphone, this
    /// machine's own notices — is drawn by the island and keeps its glyph. The picture is given the glyph's corner and
    /// the glyph's spring, so a notice from a phone does not arrive flat beside one from here. What the phone sends is
    /// usually a white cut rather than a coloured icon, so the cut stands on the app's colour and keeps the glyph's own
    /// margin from the edge of the tile; a picture that arrives with colour in it is given the whole tile.
    /// Either way the tile centres the picture's ink, not the field the phone drew it in — see PhoneArt.Framed.
    /// </summary>
    void ShowNoticeArt(ImageSource? art, string app)
    {
        _noticeApp = app;
        bool has = art != null;
        bool cut = has && PhoneArt.Silhouette(art!);
        NoticeArt.Source = has ? PhoneArt.Framed(art!) : null;
        NoticeArt.Width = NoticeArt.Height = cut ? 25 : 36;
        NoticeArtBack.Background = cut ? new SolidColorBrush(PhoneArt.Of(app)) : Brushes.Transparent;
        NoticeArtBack.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        NoticeIcon.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The app whose picture the pill is holding, so a brand icon that arrives while the pill is up can be
    /// put into it rather than waited for until the next notice.</summary>
    string _noticeApp = "";

    void AnimateNoticeIcon(Glyph icon, Glyph? from, bool warn)
    {
        NoticeSize.AnimateScale(null);
        NoticeTurn.BeginAnimation(RotateTransform.AngleProperty, null);
        NoticeMove.BeginAnimation(TranslateTransform.YProperty, null);
        NoticeTurn.CenterY = 0;

        NoticeIcon.Play(from, NoticeIconDelay.TotalSeconds);
        var back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.8 };
        TimeSpan run = TimeSpan.FromMilliseconds(460);
        if (warn)
            NoticeTurn.BeginAnimation(RotateTransform.AngleProperty, Sway(NoticeIconDelay, TimeSpan.FromMilliseconds(620), -15, 13, -10, 7, -3, 0));
        else if (icon == Glyph.Headphones)
            NoticeMove.BeginAnimation(TranslateTransform.YProperty, Delayed(-9, 0, NoticeIconDelay, run, back));
        else if (icon == Glyph.Wired)
            NoticeMove.BeginAnimation(TranslateTransform.YProperty, Delayed(8, 0, NoticeIconDelay, run, back));
        else if (icon == Glyph.Speaker)
            NoticeSize.AnimateScale(Delayed(0.6, 1, NoticeIconDelay, run, new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 }));

        // an app's own picture is sprung the same way, and has no turn or drift of its own to outlive the last saying
        if (NoticeArtBack.Visibility == Visibility.Visible)
            NoticeArtSize.AnimateScale(Delayed(0.72, 1, NoticeIconDelay, TimeSpan.FromMilliseconds(420),
                new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 }));
        else NoticeArtSize.AnimateScale(null);
    }

    void OnMicChanged(string[] was, string[] now)
    {
        InfoMic.SetVisible(now.Length > 0);
        if (!Settings.Mic) return;

        if (now.Length == 0)
        {
            Notify(Glyph.Mic, _dim, "Микрофон свободен", "Запись прекращена");
            return;
        }
        Notify(Glyph.Mic, _red, "Микрофон занят", string.Join(", ", now));
    }

    void OnNoticeRaised(string who, string what)
    {
        if (!Settings.Notices) return;
        Notify(Glyph.Note, _indigo, who, what);
        if (Settings.PhoneAlerts) PhoneSay(who, what);
    }

    /// <summary>
    /// One of this machine's sayings put on the phone's own screen. The phone only shows what a desktop sends it once
    /// its owner has switched on the plugin that receives notices, and that plugin is off until then, so a saying that
    /// goes nowhere is a quiet wire rather than a broken island.
    /// </summary>
    void PhoneSay(string app, string text)
    {
        if (!Settings.Bridge || !Settings.BridgeKde) return;
        _bridge.Announce(app, text);
    }

    async Task ApplyNotices()
    {
        try
        {
            if (Settings.Notices) await _notices.StartAsync();
            else _notices.Stop();
            if (Settings.Notices && !_notices.Live)
                Notify(Glyph.Note, _dim, "Уведомления Windows",
                    "Система не дала доступ · разрешите в «Параметры → Уведомления»");
        }
        catch (Exception ex) { App.Log(ex); }
    }

    void OnNetworkChanged(NetworkService.State was, NetworkService.State now)
    {
        if (!Settings.Network) return;

        if (now.Vpn != was.Vpn)
        {
            string[] before = was.Tunnels, after = now.Tunnels;
            if (after.Except(before).FirstOrDefault() is { } up)
                Notify(Glyph.Vpn, _green, "VPN включён", up, from: Glyph.VpnOff);
            else if (before.Except(after).FirstOrDefault() is { } down)
                Notify(Glyph.VpnOff, _dim, "VPN отключён", down, from: Glyph.Vpn);
            return;
        }

        if (now.Link == NetworkService.Link.None)
        {
            Notify(Glyph.Offline, _red, "Нет сети", "Подключение потеряно", from: Glyph.Wifi);
            return;
        }

        bool wifi = now.Link == NetworkService.Link.Wifi;
        Glyph icon = wifi ? Glyph.Wifi : Glyph.Wired;
        string title = now.Link switch
        {
            NetworkService.Link.Wired => "Ethernet",
            _ when now.Name.Length > 0 => now.Name,
            NetworkService.Link.Wifi => "Wi-Fi",
            _ => "Мобильная сеть",
        };
        if (now.Internet) Notify(icon, _green, title, wifi ? "Wi-Fi подключён" : "Сеть подключена");
        else Notify(icon, _orange, title, "Без доступа к интернету");
    }
}
