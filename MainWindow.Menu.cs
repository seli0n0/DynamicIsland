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
    const double LookTilesHeight = 72;
    const int ScaleStep = 5, GapStep = 2;

    Border? _openLookTiles;

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

    void LookRow_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.Look);

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

    void BackdropRow_Click(object sender, RoutedEventArgs e) => ToggleLookTiles(BackdropTiles);

    void LyricChangeRow_Click(object sender, RoutedEventArgs e) => ToggleLookTiles(LyricChangeTiles);

    void HoverRow_Click(object sender, RoutedEventArgs e) => ToggleLookTiles(HoverTiles);

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

    void ToggleLookTiles(Border tiles)
    {
        _openLookTiles = tiles == _openLookTiles ? null : tiles;
        SlideLookTiles(BackdropTiles, BackdropChevron);
        SlideLookTiles(LyricChangeTiles, LyricChangeChevron);
        SlideLookTiles(HoverTiles, HoverChevron);
    }

    void SlideLookTiles(Border tiles, Icon chevron)
    {
        bool open = tiles == _openLookTiles;
        if (!open && tiles.Visibility != Visibility.Visible) return;

        tiles.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slide = new DoubleAnimation(open ? LookTilesHeight : 0, Ms(open ? 320 : 240)) { EasingFunction = ease };
        slide.Completed += (_, _) =>
        {
            if (tiles != _openLookTiles) tiles.Visibility = Visibility.Collapsed;
        };
        tiles.BeginAnimation(HeightProperty, slide);
        tiles.Child.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 1 : 0, Ms(open ? 260 : 160)));
        chevron.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(open ? 90 : 0, Ms(240)) { EasingFunction = ease });
    }

    void LookTiles_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        FitPage(View.Look, LookBody);
        if (_view == View.Look) UpdateTargets();
    }

    void Accent_Click(object sender, RoutedEventArgs e)
    {
        Settings.Accent = ((RadioButton)sender).Background is SolidColorBrush picked ? picked.Color : null;
        RefreshLookPage();
        SyncAccent();
        SyncRim();
    }

    void RefreshLookPage()
    {
        SizeText.Text = Settings.Scale + "%";
        GapText.Text = Settings.Gap + " px";
        SizeSlider.Set(Settings.Scale, LookView.IsVisible);
        GapSlider.Set(Settings.Gap, LookView.IsVisible);
        DotsSegments.Set(Settings.Dots ? 1 : 0, LookView.IsVisible);
        SeekStyleSegments.Set(Settings.LineBar ? 1 : 0, LookView.IsVisible);
        GlassSwitch.Set(Settings.Glass, LookView.IsVisible);
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
    }

    void ApplyLook()
    {
        RefreshLookPage();
        _userScale.Target = Settings.Scale / 100.0;
        _topGap.Target = Settings.Gap;
        UpdateTargets();
    }
}
