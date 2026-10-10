using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static DynamicIsland.Motion;

namespace DynamicIsland;

public partial class MainWindow
{
    const double UpdatePagePadding = 14;
    const int MegabyteShift = 20;
    const int ScaleStep = 5, GapStep = 2;

    static readonly string[] Pulses = ["Выкл.", "Слабый", "Средний", "Сильный"];

    void SettingsRow_Click(object sender, RoutedEventArgs e) => ShowPanelAndCheckUpdate(Panel.Settings);

    /// Dimming the whole desktop is only worth it when there is something to take; with nothing new the island
    /// answers in its own pill instead. A check still running opens the screen too, since it follows states live.
    async void UpdateRow_Click(object sender, RoutedEventArgs e)
    {
        await _updater.CheckAsync();
        if (_updater.State is Updater.Stage.Available or Updater.Stage.Loading or Updater.Stage.Checking) OpenUpdate();
        else if (_updater.State == Updater.Stage.Latest) Notify(Glyph.Check, _green, "Обновлений нет", "Уже стоит v" + Updater.CurrentVersion, force: true);
        else Notify(Glyph.Cross, _red, "Не проверить", "GitHub не отвечает", force: true);
    }

    void LookRow_Click(object sender, RoutedEventArgs e)
    {
        _pages[View.Look].Enter(); // a page is entered from its beginning, wherever the rows were left last time
        ShowPanel(Panel.Look);
    }

    void SettingsBack_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.Menu);

    /// Closing the island is the one entry that cannot be undone from the island itself, so the red button asks
    /// for a second press and goes quiet again after ExitArmSeconds.
    const int ExitArmSeconds = 4;

    static readonly SolidColorBrush ExitRest = Tint(.13), ExitArmed = Tint(.34);

    bool _exitWaiting;

    readonly DelayedAction _exitDisarm;

    static SolidColorBrush Tint(double alpha) => new(Color.FromArgb((byte)(255 * alpha), 0xFF, 0x45, 0x3A));

    void Exit_Click(object sender, RoutedEventArgs e)
    {
        if (_exitWaiting) { Exit(); return; }
        _exitWaiting = true;
        ExitButton.Background = ExitArmed;
        ExitLabel.Text = "Нажмите ещё раз";
        _exitDisarm.Start(TimeSpan.FromSeconds(ExitArmSeconds));
    }

    void DisarmExit()
    {
        if (!_exitWaiting) return;
        _exitWaiting = false;
        _exitDisarm.Cancel();
        ExitButton.Background = ExitRest;
        ExitLabel.Text = "Закрыть остров";
    }

    void ShowPanelAndCheckUpdate(Panel panel)
    {
        ShowPanel(panel);
        _ = _updater.CheckAsync();
    }

    void Autostart_Click(object sender, RoutedEventArgs e)
    {
        try { Autostart.Set(!Autostart.Enabled); }
        catch (Exception ex) { App.Log(ex); }
        UpdateSwitches(true);
    }

    void Lyrics_Click(object sender, RoutedEventArgs e)
    {
        Settings.Lyrics = !Settings.Lyrics;
        UpdateSwitches(true);
        TrackLyrics();
    }

    void LyricEffects_Click(object sender, RoutedEventArgs e)
    {
        Settings.LyricEffects = !Settings.LyricEffects;
        UpdateSwitches(true);
        _playerLines = [];
    }

    void Rim_Click(object sender, RoutedEventArgs e)
    {
        Settings.Rim = !Settings.Rim;
        UpdateSwitches(true);
        SyncRim();
    }

    void AppVolume_Click(object sender, RoutedEventArgs e)
    {
        Settings.AppVolume = !Settings.AppVolume;
        UpdateSwitches(true);
    }

    void Network_Click(object sender, RoutedEventArgs e)
    {
        Settings.Network = !Settings.Network;
        UpdateSwitches(true);
    }

    void Mic_Click(object sender, RoutedEventArgs e)
    {
        Settings.Mic = !Settings.Mic;
        UpdateSwitches(true);
    }

    void Notices_Click(object sender, RoutedEventArgs e)
    {
        Settings.Notices = !Settings.Notices;
        UpdateSwitches(true);
        _ = ApplyNotices();
    }

    void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        Settings.HideFullscreen = !Settings.HideFullscreen;
        UpdateSwitches(true);
        CheckFullscreen();
    }

    void UpdateSwitches(bool animate)
    {
        LyricsSwitch.Set(Settings.Lyrics, animate);
        LyricEffectsSwitch.Set(Settings.LyricEffects, animate);
        RimSwitch.Set(Settings.Rim, animate);
        AppVolumeSwitch.Set(Settings.AppVolume, animate);
        NetworkSwitch.Set(Settings.Network, animate);
        MicSwitch.Set(Settings.Mic, animate);
        NoticesSwitch.Set(Settings.Notices, animate);
        FullscreenSwitch.Set(Settings.HideFullscreen, animate);
        AutostartSwitch.Set(Autostart.Enabled, animate);
        BridgeSwitch.Set(Settings.Bridge, animate);
        BridgeKdeSwitch.Set(Settings.BridgeKde, animate);
        PhoneNoticesSwitch.Set(Settings.PhoneNotices, animate);
        PhoneAlertsSwitch.Set(Settings.PhoneAlerts, animate);
        PhoneTimerSwitch.Set(Settings.PhoneTimer, animate);
        PhoneClipboardSwitch.Set(Settings.PhoneClipboard, animate);
        // the line that opens these three carries what is already open, the way a row of the look page carries its value
        PhoneOutText.Text = Say(
            (Settings.PhoneAlerts, "уведомления"),
            (Settings.PhoneTimer, "таймер"),
            (Settings.PhoneClipboard, "копия"));
    }

    /// <summary>The open ones of a set, joined for a row that has room for one word and not for three.</summary>
    static string Say(params (bool On, string Name)[] among)
    {
        string[] open = [.. among.Where(entry => entry.On).Select(entry => entry.Name)];
        return open.Length switch { 0 => "ничего", 3 => "всё", _ => string.Join(" · ", open) };
    }

    /// The update screen is a window that dims the whole desktop, so the island itself stands down while the release
    /// is on screen: a pill floating over the dim would sit above the modal and swallow clicks meant for it.
    void OpenUpdate()
    {
        _ = _updater.CheckAsync();
        if (_updateWindow != null) return;
        _updateWindow = new UpdateWindow(_updater, _screen, Exit);
        _updateWindow.Closed += (_, _) =>
        {
            _updateWindow = null;
            Show();
        };
        ShowPanel(Panel.None);
        Hide();
        _updateWindow.Show();
    }

    void RefreshUpdate()
    {
        Updater.Stage stage = _updater.State;
        bool loading = stage == Updater.Stage.Loading, found = loading || stage == Updater.Stage.Available;

        UpdateText.Foreground = found ? _orange : _dim;
        UpdateText.Text = loading ? _updater.Percent + "%" : "v" + (found ? _updater.LatestVersion! : Updater.CurrentVersion);

        if (loading)
        {
            LoadingText.Text = _updater.Percent + "%";
            LoadingRing.BeginAnimation(Ring.ProgressProperty, new DoubleAnimation(_updater.Percent / 100.0, Ms(200)));
        }
        UpdateView();
    }

    void SizeSlider_Changed(object? sender, EventArgs e) => SetScale((int)SizeSlider.Value);

    static int StepOption(int[] among, int value, int by, bool wrap)
    {
        int count = among.Length, at = Array.IndexOf(among, value) + by;
        return among[wrap ? (at % count + count) % count : Math.Clamp(at, 0, count - 1)];
    }

    void GapSlider_Changed(object? sender, EventArgs e) => SetGap((int)GapSlider.Value);

    void SetScale(int percent)
    {
        int was = Settings.Scale;
        Settings.Scale = percent;
        if (Settings.Scale != was) ApplyLook();
    }

    void SetGap(int px)
    {
        int was = Settings.Gap;
        Settings.Gap = px;
        if (Settings.Gap != was) ApplyLook();
    }

    void Dots_Click(object sender, RoutedEventArgs e)
    {
        Eq.Dots = Settings.Dots = DotsSegments.PickUnderPointer() == 1;
        RefreshLookPage();
    }

    void Glass_Click(object sender, RoutedEventArgs e)
    {
        Settings.Glass = !Settings.Glass;
        RefreshLookPage();
        SyncGlass(true);
        OpenFrost(Settings.Glass, LookView.IsVisible);
    }

    void FrostSlider_Changed(object? sender, EventArgs e) => SetFrost((int)FrostSlider.Value);

    void SetFrost(int percent)
    {
        int was = Settings.Frost;
        Settings.Frost = percent;
        if (Settings.Frost != was)
        {
            RefreshLookPage();
            Body.Thin(Settings.Glass ? Settings.Frost : 0, FrostTime, HideGlassOnceCovered);
        }
    }

    /// <summary>
    /// Open or close the row of the glass's own strength under its switch. The weight of a frost is nothing without the
    /// frost, so the row is taken away with the switch that lets it be off; it measures its own room the way a section
    /// of the page does, and the page is refitted every step of the way it arrives.
    /// </summary>
    void OpenFrost(bool on, bool animate)
    {
        if (!on && FrostTiles.Visibility != Visibility.Visible) return;

        FrostTiles.Visibility = Visibility.Visible;
        double room = on ? _pages[View.Look].RoomFor(FrostTiles) : 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slide = new DoubleAnimation(room, animate ? Ms(on ? 320 : 240) : TimeSpan.Zero) { EasingFunction = ease };
        slide.Completed += (_, _) =>
        {
            if (!Settings.Glass) FrostTiles.Visibility = Visibility.Collapsed;
        };
        FrostTiles.BeginAnimation(Border.HeightProperty, slide);
        FrostTiles.Child!.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(on ? 1 : 0, animate ? Ms(on ? 260 : 160) : TimeSpan.Zero));
        if (!animate) FrostTiles.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    /// A row that arrives under the page's other rows changes how tall the page is, and so how far the wheel has to
    /// travel and where the pill ends: the same refit an opened section asks for, asked for by the glass's own row.
    void Frost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        PinnedPage look = _pages[View.Look];
        look.Fit();
        if (look.Host == _views[_view]) UpdateTargets();
    }

    void Pulse_Click(object sender, RoutedEventArgs e) => SetPulse((Settings.Pulse + 1) % Pulses.Length);

    void SetPulse(int level)
    {
        if (level == Settings.Pulse) return;
        Settings.Pulse = level;
        RefreshLookPage();
    }

    void SeekStyle_Click(object sender, RoutedEventArgs e)
    {
        Settings.LineBar = SeekStyleSegments.PickUnderPointer() == 1;
        RefreshLookPage();
        SyncSeekStyle(true);
    }

    void SeekHover_Click(object sender, RoutedEventArgs e)
    {
        Settings.SeekHover = (SeekHover)SeekHoverSegments.PickUnderPointer();
        RefreshLookPage();
    }

    void BackdropRow_Click(object sender, RoutedEventArgs e) => LookTiles(BackdropTiles);

    void LyricChangeRow_Click(object sender, RoutedEventArgs e) => LookTiles(LyricChangeTiles);

    void HoverRow_Click(object sender, RoutedEventArgs e) => LookTiles(HoverTiles);

    void PlaceRow_Click(object sender, RoutedEventArgs e) => LookTiles(PlaceTiles);

    void FaceRow_Click(object sender, RoutedEventArgs e) => LookTiles(FontTiles);

    void BackdropTile_Click(object sender, RoutedEventArgs e)
    {
        Settings.Backdrop = (Backdrop)BackdropStrip.Children.IndexOf((UIElement)sender);
        RefreshLookPage();
        SyncBackdrop();
    }

    void LyricChangeTile_Click(object sender, RoutedEventArgs e)
    {
        Settings.LyricChange = (LyricChange)LyricChangeStrip.Children.IndexOf((UIElement)sender);
        RefreshLookPage();
    }

    void HoverTile_Click(object sender, RoutedEventArgs e)
    {
        Settings.Hover = (Hover)HoverStrip.Children.IndexOf((UIElement)sender);
        RefreshLookPage();
    }

    /// One section of a page is open at a time: they are rooms of the same page, and two of them open at once would
    /// push what is between them out of sight.
    void LookTiles(Border tiles) => _pages[View.Look].Toggle(tiles);

    /// A section measures itself as it opens, and the page is refitted every step of the way: its rows, and so the
    /// height the pill takes, change under the wheel as much as when a row is added or taken away.
    void Section_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Border tiles) return;
        foreach (PinnedPage page in _pages.Values)
        {
            if (!page.Holds(tiles)) continue;
            page.Fit();
            if (page.Host == _views[_view]) UpdateTargets();
        }
    }

    void Accent_Click(object sender, RoutedEventArgs e)
    {
        Settings.Accent = ((RadioButton)sender).Background is SolidColorBrush picked ? picked.Color : null;
        RefreshLookPage();
        SyncAccent();
        SyncRim();
    }

    /// <summary>Every readout of the page, from the sizes and the switches to the placement and the faces that
    /// became sections of it: one place, since a setting can be turned from the page, from the wheel, or from
    /// somewhere else in the island altogether.</summary>
    void RefreshLookPage()
    {
        SizeText.Text = Settings.Scale + "%";
        GapText.Text = Settings.Gap + " px";
        SizeSlider.Set(Settings.Scale, LookView.IsVisible);
        GapSlider.Set(Settings.Gap, LookView.IsVisible);
        DotsSegments.Set(Settings.Dots ? 1 : 0, LookView.IsVisible);
        SeekStyleSegments.Set(Settings.LineBar ? 1 : 0, LookView.IsVisible);
        SeekHoverSegments.Set((int)Settings.SeekHover, LookView.IsVisible);
        GlassSwitch.Set(Settings.Glass, LookView.IsVisible);
        FrostSlider.Set(Settings.Frost, LookView.IsVisible);
        FrostText.Text = Settings.Frost + "%";
        ((RadioButton)BackdropStrip.Children[(int)Settings.Backdrop]).IsChecked = true;
        ((RadioButton)LyricChangeStrip.Children[(int)Settings.LyricChange]).IsChecked = true;
        ((RadioButton)HoverStrip.Children[(int)Settings.Hover]).IsChecked = true;
        PulseText.Text = Pulses[Settings.Pulse];
        BackdropText.Text = Settings.Backdrop switch
        {
            Backdrop.Matrix => "Матрица",
            Backdrop.Stars => "Звёзды",
            Backdrop.MatrixAndStars => "Матрица и звёзды",
            _ => "Свечение",
        };
        LyricChangeText.Text = Settings.LyricChange switch
        {
            LyricChange.Wave => "Волна по буквам",
            LyricChange.Drum => "Барабан по словам",
            _ => "Плавно",
        };
        HoverText.Text = Settings.Hover switch
        {
            Hover.Magnet => "Магнит",
            Hover.Flow => "Перетекание",
            _ => "Диск",
        };
        foreach (RadioButton dot in AccentStrip.Children)
        {
            Color? color = dot.Background is SolidColorBrush own ? own.Color : null;
            if (color != Settings.Accent) continue;
            dot.IsChecked = true;
            AccentText.Text = (string)dot.Tag;
        }
        UpdatePosition();
        UpdateFonts();
    }

    void ApplyLook()
    {
        RefreshLookPage();
        _userScale.Target = Settings.Scale / 100.0;
        _topGap.Target = Settings.Gap;
        UpdateTargets();
    }
}
