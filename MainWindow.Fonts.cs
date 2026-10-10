using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;

namespace DynamicIsland;

public partial class MainWindow
{
    const double FaceTileSample = 21;

    static readonly int[] FontScales = [80, 90, 100, 110, 120, 130, 140, 150, 160];

    readonly Dictionary<DependencyObject, double> _drawnSizes = new();

    /// <summary>The family behind each tile of the strip, in tile order: nothing at all for SF Pro, then the loose
    /// families in the order the strip shows them.</summary>
    readonly List<string> _faceTiles = [];

    FontFamily? _plainFace, _plainDisplay;

    void SetFace(string family)
    {
        Settings.Font = family;
        ApplyFonts();
    }

    void FaceTile_Click(object sender, RoutedEventArgs e) =>
        SetFace(_faceTiles[FaceStrip.Children.IndexOf((UIElement)sender)]);

    void FontScaleSlider_Changed(object? sender, EventArgs e) => SetFontScale((int)FontScaleSlider.Value);

    void SetFontScale(int percent)
    {
        percent = Math.Clamp(percent, FontScales[0], FontScales[^1]);
        if (percent == Settings.FontScale) return;
        Settings.FontScale = percent;
        ApplyFonts();
    }

    void FontAdd_Click(object sender, RoutedEventArgs e)
    {
        var pick = new OpenFileDialog
        {
            Title = "Загрузить шрифт",
            Multiselect = true,
            Filter = "Шрифты (*.ttf;*.otf)|*.ttf;*.otf",
        };
        _pickingFiles = true;
        try
        {
            if (pick.ShowDialog(this) != true) return;
            string[] added = FontPack.Import(pick.FileNames).Families;
            if (added.Length > 0) SetFace(added[^1]);
        }
        finally
        {
            _pickingFiles = false;
        }
    }

    void FontFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(FontPack.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{FontPack.Folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    void ApplyFonts()
    {
        _plainFace ??= (FontFamily)FindResource("Face");
        _plainDisplay ??= (FontFamily)FindResource("FaceDisplay");
        if (string.IsNullOrWhiteSpace(Settings.Font))
        {
            Resources["Face"] = _plainFace;
            Resources["FaceDisplay"] = _plainDisplay;
        }
        else
        {
            Resources["Face"] = FontPack.Face(Settings.Font, "SF Pro Text");
            Resources["FaceDisplay"] = FontPack.Face(Settings.Font, "SF Pro Display");
        }
        ScaleFonts();
        UpdateFonts();
    }

    void ScaleFonts() => FitFonts(this, Math.Clamp(Settings.FontScale / 100.0, 0.6, 1.7));

    void FitFonts(DependencyObject from, double k)
    {
        foreach (object entry in LogicalTreeHelper.GetChildren(from))
        {
            if (entry is not DependencyObject child) continue;
            if (child is TextBlock or Control)
            {
                double drawn = _drawnSizes.TryGetValue(child, out double kept) ? kept : OwnFontSize(child) ?? double.NaN;
                if (!double.IsNaN(drawn))
                {
                    _drawnSizes[child] = drawn;
                    child.SetValue(TextElement.FontSizeProperty, Math.Round(drawn * k, 2));
                }
            }
            FitFonts(child, k);
        }
    }

    static double? OwnFontSize(DependencyObject node)
    {
        if (node.ReadLocalValue(TextElement.FontSizeProperty) is double local) return local;
        if (node is not FrameworkElement { Style: not null } styled) return null;
        foreach (SetterBase entry in styled.Style.Setters)
            if (entry is Setter { Property: var property, Value: double value } && property == TextElement.FontSizeProperty)
                return value;
        return null;
    }

    void UpdateFonts()
    {
        SyncFaceTiles();
        FontNameText.Text = FontPack.Preview(Settings.Font);
        FontScaleText.Text = Settings.FontScale + "%";
        FontScaleSlider.Set(Settings.FontScale, LookView.IsVisible);
        string shown = FontPack.Preview(Settings.Font);
        for (int i = 0; i < _faceTiles.Count; i++)
            if (FontPack.Preview(_faceTiles[i]) == shown)
            {
                ((RadioButton)FaceStrip.Children[i]).IsChecked = true;
                break;
            }
    }

    /// <summary>
    /// Builds a tile per family there is to choose from, each drawing its own «Aa» in the face it stands for so the
    /// strip is read rather than remembered. Only rebuilt when the families themselves have changed — loading a file
    /// adds one — since a rebuilt strip would throw away the pointer's hover and the ring of the tile in use.
    /// </summary>
    void SyncFaceTiles()
    {
        var families = new List<string> { "" };
        families.AddRange(FontPack.Families()
            .Where(family => !FontPack.Bundled.Contains(family, StringComparer.OrdinalIgnoreCase)));
        if (_faceTiles.SequenceEqual(families, StringComparer.OrdinalIgnoreCase)) return;

        _faceTiles.Clear();
        _faceTiles.AddRange(families);
        FaceStrip.Children.Clear();
        foreach (string family in families)
        {
            var tile = new RadioButton
            {
                Style = (Style)FindResource("LookTile"),
                Tag = FontPack.Preview(family),
                Content = new TextBlock
                {
                    Text = "Aa",
                    FontSize = FaceTileSample,
                    FontFamily = FontPack.Face(family, "SF Pro Text"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            tile.Click += FaceTile_Click;
            FaceStrip.Children.Add(tile);
        }
        FitFonts(FaceStrip, Math.Clamp(Settings.FontScale / 100.0, 0.6, 1.7));
    }
}
