using System.Windows.Media;
using Microsoft.Win32;

namespace DynamicIsland;

/// <summary>What lies behind the open player: the glow of the bass, a matrix of dots, a sky of stars, or both at once.</summary>
[Flags]
enum Backdrop
{
    Glow = 0,
    Matrix = 1,
    Stars = 2,
    MatrixAndStars = Matrix | Stars,
}

/// <summary>How the closed island's line of words gives way to the next one.</summary>
enum LyricChange
{
    Smooth,
    Wave,
    Drum,
}

enum Hover
{
    Disc,
    Magnet,
    Flow,
}

enum SeekHover
{
    Magnifier,
    Lift,
    Wave,
}

static class Settings
{
    const string Key = @"Software\DynamicIsland";
    public const int MaxGap = 200;
    const int MinScale = 85, MaxScale = 130, MaxAlong = 4096, MinFontScale = 80, MaxFontScale = 160, MaxPulse = 3;
    const int DefaultScale = 100, DefaultGap = 8, DefaultPulse = 2;
    const int MinFrost = 10, MaxFrost = 100, DefaultFrost = 60;

    static bool _lyrics = ReadSwitch(nameof(Lyrics)), _lyricEffects = ReadSwitch(nameof(LyricEffects));
    static bool _network = ReadSwitch(nameof(Network)), _hideFullscreen = ReadSwitch(nameof(HideFullscreen));
    static bool _rim = ReadSwitch(nameof(Rim)), _appVolume = ReadSwitch(nameof(AppVolume));
    static bool _timerPauses = ReadSwitch(nameof(TimerPauses)), _workArea = ReadSwitch(nameof(WorkArea));
    static bool _mic = ReadSwitch(nameof(Mic));
    static bool _notices = ReadSwitch(nameof(Notices), false);
    static bool _phoneNotices = ReadSwitch(nameof(PhoneNotices));
    static bool _phoneAlerts = ReadSwitch(nameof(PhoneAlerts)), _phoneTimer = ReadSwitch(nameof(PhoneTimer));
    static bool _phoneClipboard = ReadSwitch(nameof(PhoneClipboard));
    static bool _dots = ReadSwitch(nameof(Dots), false);
    static bool _lineBar = ReadSwitch(nameof(LineBar));
    static bool _glass = ReadSwitch(nameof(Glass), false);
    static Backdrop _backdrop = (Backdrop)Math.Clamp(Read(nameof(Backdrop), 0), 0, (int)Backdrop.MatrixAndStars);
    static LyricChange _lyricChange = (LyricChange)Math.Clamp(Read(nameof(LyricChange), (int)LyricChange.Wave), 0, (int)LyricChange.Drum);
    static Hover _hover = (Hover)Math.Clamp(Read(nameof(Hover), 0), 0, (int)Hover.Flow);
    static SeekHover _seekHover = (SeekHover)Math.Clamp(Read(nameof(SeekHover), 0), 0, (int)SeekHover.Wave);
    static bool _bridge = ReadSwitch(nameof(Bridge), false), _bridgeKde = ReadSwitch(nameof(BridgeKde), false);
    static string _bridgeKey = Read(nameof(BridgeKey), "");
    static string _bridgePhoneId = Read(nameof(BridgePhoneId), "");
    static int _scale = Math.Clamp(Read(nameof(Scale), DefaultScale), MinScale, MaxScale);
    static int _gap = Math.Clamp(Read(nameof(Gap), DefaultGap), 0, MaxGap);
    static int _accent = Read(nameof(Accent), 0);
    static int _edge = Math.Clamp(Read(nameof(Edge), 0), 0, 3);
    static int _anchor = Math.Clamp(Read(nameof(Anchor), 0), 0, 3);
    static int _along = Math.Clamp(Read(nameof(Along), 0), -MaxAlong, MaxAlong);
    static int _fontScale = Math.Clamp(Read(nameof(FontScale), 100), MinFontScale, MaxFontScale);
    static int _pulse = Math.Clamp(Read(nameof(Pulse), DefaultPulse), 0, MaxPulse);
    static int _frost = Math.Clamp(Read(nameof(Frost), DefaultFrost), MinFrost, MaxFrost);
    static string _monitor = Read(nameof(Monitor), "");
    static string _font = Read(nameof(Font), "");

    public static bool Dots
    {
        get => _dots;
        set => Write(nameof(Dots), _dots = value);
    }

    public static bool Bridge
    {
        get => _bridge;
        set => Write(nameof(Bridge), _bridge = value);
    }

    /// <summary>Whether the island also answers KDE Connect, the protocol an Android phone already speaks.</summary>
    public static bool BridgeKde
    {
        get => _bridgeKde;
        set => Write(nameof(BridgeKde), _bridgeKde = value);
    }

    /// <summary>The sign asked of every phone at the door. The bridge makes one the first time it is started.</summary>
    public static string BridgeKey
    {
        get => _bridgeKey;
        set => Write(nameof(BridgeKey), _bridgeKey = value, RegistryValueKind.String);
    }

    /// <summary>The name this island answers to on the network; made once and kept, so phones do not see a new device each run.</summary>
    public static string BridgePhoneId
    {
        get => _bridgePhoneId;
        set => Write(nameof(BridgePhoneId), _bridgePhoneId = value, RegistryValueKind.String);
    }

    public static bool LineBar
    {
        get => _lineBar;
        set => Write(nameof(LineBar), _lineBar = value);
    }

    public static bool Glass
    {
        get => _glass;
        set => Write(nameof(Glass), _glass = value);
    }

    /// <summary>How far the glass lets the frosted blur behind the island stand in its own body: at its least the body
    /// stays nearly solid and only a hint of the glass shows, at its most the blur carries the look of the island.</summary>
    public static int Frost
    {
        get => _frost;
        set => Write(nameof(Frost), _frost = Math.Clamp(value, MinFrost, MaxFrost));
    }

    public static Backdrop Backdrop
    {
        get => _backdrop;
        set => Write(nameof(Backdrop), (int)(_backdrop = value));
    }

    public static LyricChange LyricChange
    {
        get => _lyricChange;
        set => Write(nameof(LyricChange), (int)(_lyricChange = value));
    }

    public static Hover Hover
    {
        get => _hover;
        set => Write(nameof(Hover), (int)(_hover = value));
    }

    public static SeekHover SeekHover
    {
        get => _seekHover;
        set => Write(nameof(SeekHover), (int)(_seekHover = value));
    }

    public static bool Lyrics
    {
        get => _lyrics;
        set => Write(nameof(Lyrics), _lyrics = value);
    }

    public static bool LyricEffects
    {
        get => _lyricEffects;
        set => Write(nameof(LyricEffects), _lyricEffects = value);
    }

    public static bool Rim
    {
        get => _rim;
        set => Write(nameof(Rim), _rim = value);
    }

    public static bool AppVolume
    {
        get => _appVolume;
        set => Write(nameof(AppVolume), _appVolume = value);
    }

    public static bool TimerPauses
    {
        get => _timerPauses;
        set => Write(nameof(TimerPauses), _timerPauses = value);
    }

    public static bool Network
    {
        get => _network;
        set => Write(nameof(Network), _network = value);
    }

    public static bool Mic
    {
        get => _mic;
        set => Write(nameof(Mic), _mic = value);
    }

    public static bool Notices
    {
        get => _notices;
        set => Write(nameof(Notices), _notices = value);
    }

    /// <summary>
    /// Whether the island takes what the phone is holding: the notices it shows its owner, not only the ones that
    /// arrive while the island happens to be watching. Turning it off leaves the phone's screen its own.
    /// </summary>
    public static bool PhoneNotices
    {
        get => _phoneNotices;
        set => Write(nameof(PhoneNotices), _phoneNotices = value);
    }

    /// <summary>
    /// Whether a notice Windows showed here is also shown on the phone. Reading the phone's notices and writing onto
    /// its screen are opposite directions of one channel, and an owner may want either without the other.
    /// </summary>
    public static bool PhoneAlerts
    {
        get => _phoneAlerts;
        set => Write(nameof(PhoneAlerts), _phoneAlerts = value);
    }

    /// <summary>Whether the end of a timer is said on the phone, for a person who has left the machine it ran on.</summary>
    public static bool PhoneTimer
    {
        get => _phoneTimer;
        set => Write(nameof(PhoneTimer), _phoneTimer = value);
    }

    /// <summary>
    /// Whether words copied here are laid on the phone's clipboard too. The phone answers its own copy back to the
    /// island whatever this says; this is only the half that goes outward.
    /// </summary>
    public static bool PhoneClipboard
    {
        get => _phoneClipboard;
        set => Write(nameof(PhoneClipboard), _phoneClipboard = value);
    }

    public static bool HideFullscreen
    {
        get => _hideFullscreen;
        set => Write(nameof(HideFullscreen), _hideFullscreen = value);
    }

    public static int Scale
    {
        get => _scale;
        set => Write(nameof(Scale), _scale = Math.Clamp(value, MinScale, MaxScale));
    }

    public static int Gap
    {
        get => _gap;
        set => Write(nameof(Gap), _gap = Math.Clamp(value, 0, MaxGap));
    }

    public static ScreenEdge Edge
    {
        get => (ScreenEdge)_edge;
        set => Write(nameof(Edge), _edge = Math.Clamp((int)value, 0, 3));
    }

    public static ScreenAnchor Anchor
    {
        get => (ScreenAnchor)_anchor;
        set => Write(nameof(Anchor), _anchor = Math.Clamp((int)value, 0, 3));
    }

    public static int Along
    {
        get => _along;
        set => Write(nameof(Along), _along = Math.Clamp(value, -MaxAlong, MaxAlong));
    }

    public static string Monitor
    {
        get => _monitor;
        set => Write(nameof(Monitor), _monitor = value ?? "", RegistryValueKind.String);
    }

    public static bool WorkArea
    {
        get => _workArea;
        set => Write(nameof(WorkArea), _workArea = value);
    }

    public static string Font
    {
        get => _font;
        set => Write(nameof(Font), _font = value ?? "", RegistryValueKind.String);
    }

    public static int FontScale
    {
        get => _fontScale;
        set => Write(nameof(FontScale), _fontScale = Math.Clamp(value, MinFontScale, MaxFontScale));
    }

    public static int Pulse
    {
        get => _pulse;
        set => Write(nameof(Pulse), _pulse = Math.Clamp(value, 0, MaxPulse));
    }

    public static Color? Accent
    {
        get => _accent == 0 ? null : Color.FromRgb((byte)(_accent >> 16), (byte)(_accent >> 8), (byte)_accent);
        set => Write(nameof(Accent), _accent = value is { } c ? c.R << 16 | c.G << 8 | c.B : 0);
    }

    public static string[] Shelf
    {
        get => Read<string[]>(nameof(Shelf), []);
        set => Write(nameof(Shelf), value, RegistryValueKind.MultiString);
    }

    static bool ReadSwitch(string name, bool fallback = true) => Read(name, fallback ? 1 : 0) != 0;

    static T Read<T>(string name, T fallback)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(name) is T value ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    static void Write(string name, bool value) => Write(name, value ? 1 : 0);

    static void Write(string name, int value) => Write(name, value, RegistryValueKind.DWord);

    static void Write(string name, object value, RegistryValueKind kind)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue(name, value, kind);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }
}
