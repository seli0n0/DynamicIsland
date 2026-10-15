using System.Runtime.InteropServices;
using System.Windows;

namespace DynamicIsland;

/// <summary>
/// A QR code looked for on the screen itself. The picture is taken as it stands, turned into dark and light, and the
/// three finder corners are picked out of it by the one shape only they keep: a dark bar, a light one, three times as
/// dark again, a light one, and a dark one, in every row and every column that crosses a corner. Those three centers
/// say where the code lies and how it is turned, so a grid is laid over them module by module and handed to the
/// reader, which knows the rest. Nothing here asks for a library, and nothing asks the screen to cooperate: a code
/// may stand anywhere in the picture, at any size the corner lets one count, and turned as the page it came from
/// turned it.
/// </summary>
static class QrReader
{
    const int DesktopLeft = 76, DesktopTop = 77, DesktopWidth = 78, DesktopHeight = 79;
    const int CopyWithLayeredWindows = 0x00CC0020 | 0x40000000;
    const int BytesPerPixel = 4;

    const int SmallestCode = 21;   // a version 1 code is no narrower than this, corners and all
    const int BetweenCenters = 7;  // modules the two finder centers stand apart by, which the whole edge is longer by
    const int FewestRows = 2;      // a corner seen in one row alone may be any two shadows that fell alike
    const int MostCorners = 24;    // enough for a screenful of codes, and few enough to keep the search short
    const double Stray = 0.6;      // how far a bar may stray from its share of the corner before it is no corner

    /// <summary>The whole desktop, in the picture's own pixels, which is what the screen is asked to copy.</summary>
    public static Int32Rect Desktop => new(GetSystemMetrics(DesktopLeft), GetSystemMetrics(DesktopTop),
        GetSystemMetrics(DesktopWidth), GetSystemMetrics(DesktopHeight));

    /// <summary>The words of the first code seen in an area of the screen, or null when it holds none.</summary>
    public static string? Read(Int32Rect area)
    {
        int width = area.Width, height = area.Height;
        if (width < SmallestCode || height < SmallestCode) return null;
        return FromPixels(Capture(area.X, area.Y, width, height), width, height);
    }

    /// <summary>
    /// The same, out of a picture already taken: BGRA, four bytes to the pixel, the first pixel the top left. A code
    /// is looked for in the picture's own dark and light, and in the same picture turned the other way round, since a
    /// page that shows white marks on black is no rarer than the other way about.
    /// </summary>
    public static string? FromPixels(byte[] pixels, int width, int height)
    {
        byte[] shades = Luminance(pixels);
        foreach (bool adaptive in new[] { false, true })
            foreach (bool inverted in new[] { false, true })
            {
                bool[] dark = Binarize(shades, width, height, adaptive, inverted);
                if (Decode(dark, width, height) is string found) return found;
            }
        return null;
    }

    static string? Decode(bool[] dark, int width, int height)
    {
        List<Corner> corners = Corners(dark, width, height);
        if (corners.Count < 3) return null;

        for (int first = 0; first < corners.Count; first++)
            for (int second = 0; second < corners.Count; second++)
                for (int third = 0; third < corners.Count; third++)
                {
                    if (second == first || third == first || third == second) continue;
                    if (Read(corners, corners[first], corners[second], corners[third], dark, width, height) is string found)
                        return found;
                }
        return null;
    }

    /// <summary>
    /// One corner taken for the near right angle of the code, and the other two for the ends of its two sides. What
    /// they give back is the grid laid over the picture, if the three stand far enough apart, at right angles, and by
    /// the same count of modules apiece — and if the marks the grid lands on read as a code.
    /// </summary>
    static string? Read(List<Corner> corners, Corner at, Corner right, Corner down, bool[] dark, int width, int height)
    {
        double acrossX = right.X - at.X, acrossY = right.Y - at.Y;
        double downX = down.X - at.X, downY = down.Y - at.Y;
        double across = Math.Sqrt(acrossX * acrossX + acrossY * acrossY);
        double downLength = Math.Sqrt(downX * downX + downY * downY);
        if (across < 1 || downLength < 1) return null;

        double lean = (acrossX * downX + acrossY * downY) / (across * downLength);
        double ratio = Math.Min(across, downLength) / Math.Max(across, downLength);
        if (Math.Abs(lean) > 0.35 || ratio < 0.7) return null;

        double module = (at.Module + right.Module + down.Module) / 3;
        int side = (int)Math.Round((across + downLength) / (2 * module)) + BetweenCenters;
        int version = (int)Math.Round((side - 17) / 4.0);
        if (version is < 1 or > 40) return null;
        side = version * 4 + 17;

        double stepX = acrossX / (side - BetweenCenters), stepY = acrossY / (side - BetweenCenters);
        double nearX = downX / (side - BetweenCenters), nearY = downY / (side - BetweenCenters);
        bool[,] grid = new bool[side, side];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
                // The corner stands at the middle of its module three, so a module's own middle is three of its steps off.
                grid[x, y] = Shaded(dark, width, height,
                    at.X + (x - 3) * stepX + (y - 3) * nearX,
                    at.Y + (x - 3) * stepY + (y - 3) * nearY, module);

        return Timed(grid, side) ? Qr.Read(grid) : null;
    }

    /// <summary>
    /// Whether the two timing lines alternate as the standard promises they do, which they must however the code is
    /// turned and whatever else it says. Cheap enough to ask before the whole grid is read, and it ends nearly every
    /// guess that was not a corner of a code at all.
    /// </summary>
    static bool Timed(bool[,] grid, int side)
    {
        int stray = 0;
        for (int i = 8; i < side - 8; i++)
        {
            if (grid[i, 6] == grid[i - 1, 6]) stray++;
            if (grid[6, i] == grid[6, i - 1]) stray++;
        }
        return stray <= (side - 16) / 4;
    }

    /// <summary>
    /// Whether a module leans towards dark. The three points along each way are a third of a module apart, so they
    /// stay inside the module they are asked about however small it is; set them a fixed pixel apart and a code of two
    /// pixels to the module gets more votes from its neighbours than from itself.
    /// </summary>
    static bool Shaded(bool[] dark, int width, int height, double x, double y, double module)
    {
        int at = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                at += Shaded(dark, width, height,
                    (int)Math.Round(x + dx * module / 3), (int)Math.Round(y + dy * module / 3)) ? 1 : 0;
        return at >= 5;
    }

    static bool Shaded(bool[] dark, int width, int height, int x, int y) =>
        x >= 0 && y >= 0 && x < width && y < height && dark[y * width + x];

    /// <summary>
    /// The three finder corners of every code the picture may hold. Each row is walked once for the five bars a
    /// corner is made of, and every center so found is joined to the ones standing over it in the rows before, so a
    /// corner seven modules across gives seven sightings that come to one center.
    /// </summary>
    static List<Corner> Corners(bool[] dark, int width, int height)
    {
        var found = new List<Corner>();
        var runs = new List<(bool Dark, int From, int To)>();
        for (int y = 0; y < height; y++)
        {
            runs.Clear();
            int start = 0;
            bool state = dark[y * width];
            for (int x = 1; x < width; x++)
            {
                if (dark[y * width + x] == state) continue;
                runs.Add((state, start, x - 1));
                state = dark[y * width + x];
                start = x;
            }
            runs.Add((state, start, width - 1));

            for (int at = 0; at + 4 < runs.Count; at++)
            {
                if (!runs[at].Dark || runs[at + 1].Dark || !runs[at + 2].Dark || runs[at + 3].Dark || !runs[at + 4].Dark)
                    continue;
                int narrow = Length(runs[at]), light = Length(runs[at + 1]), middle = Length(runs[at + 2]);
                int other = Length(runs[at + 3]), last = Length(runs[at + 4]);
                int whole = narrow + light + middle + other + last;
                if (whole < 7) continue;

                double module = whole / 7.0;
                if (!Keeps(narrow, module) || !Keeps(light, module) || !Keeps(middle, 3 * module)
                    || !Keeps(other, module) || !Keeps(last, module)) continue;

                Join(found, (runs[at].From + runs[at + 4].To) / 2.0, y, module);
            }
        }
        found.RemoveAll(corner => corner.Seen < FewestRows);
        return found.Count > MostCorners
            ? [.. found.OrderByDescending(corner => corner.Seen).Take(MostCorners)]
            : found;
    }

    static int Length((bool Dark, int From, int To) run) => run.To - run.From + 1;

    static bool Keeps(int given, double want) => Math.Abs(given - want) <= want * Stray;

    /// <summary>A sighting taken for the corner it belongs to, or for a new one where none was seen near enough.</summary>
    static void Join(List<Corner> found, double x, int y, double module)
    {
        for (int at = 0; at < found.Count; at++)
        {
            Corner near = found[at];
            // The row it stands in must be the next one, and the column must be the one the corner was first seen in:
            // a corner may only ever be judged against where it began, or a slow drift over the picture takes one
            // finder's count of rows and hands it to whatever pattern happened to lie along the way.
            if (Math.Abs(near.Column - x) > near.Module || y - near.Row > Math.Max(2, near.Module)) continue;
            found[at] = near with
            {
                X = (near.X * near.Seen + x) / (near.Seen + 1),
                Y = (near.Y * near.Seen + y) / (near.Seen + 1),
                Module = (near.Module * near.Seen + module) / (near.Seen + 1),
                Seen = near.Seen + 1,
                Row = y,
            };
            return;
        }
        found.Add(new Corner(x, y, module, 1, x, y));
    }

    /// <summary>How light each pixel is, the three of its colors weighed as the eye weighs them.</summary>
    static byte[] Luminance(byte[] pixels)
    {
        var shades = new byte[pixels.Length / BytesPerPixel];
        for (int at = 0; at < shades.Length; at++)
            shades[at] = (byte)((pixels[at * 4 + 2] * 77 + pixels[at * 4 + 1] * 150 + pixels[at * 4] * 28) >> 8);
        return shades;
    }

    /// <summary>
    /// Dark and light out of the shades. The plain way parts them by one line drawn through the whole picture, which
    /// is what a code on a page of its own wants; the local way parts each pixel from the average about it, which is
    /// what a code sitting on a dark ground with a light ground of its own wants.
    /// </summary>
    static bool[] Binarize(byte[] shades, int width, int height, bool adaptive, bool inverted)
    {
        var dark = new bool[shades.Length];
        if (!adaptive)
        {
            // The line Otsu draws belongs to the dark side: a picture of nothing but ink and paper ties at every tone
            // between the two, and the walk keeps the first of them, which would leave the ink on the light side.
            int cut = Otsu(shades);
            for (int at = 0; at < dark.Length; at++) dark[at] = (shades[at] <= cut) != inverted;
            return dark;
        }

        var whole = new long[(width + 1) * (height + 1)];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                whole[(y + 1) * (width + 1) + x + 1] = whole[y * (width + 1) + x + 1]
                    + whole[(y + 1) * (width + 1) + x] - whole[y * (width + 1) + x] + shades[y * width + x];

        const int block = 15;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int from = Math.Max(0, x - block), to = Math.Min(width, x + block + 1);
                int top = Math.Max(0, y - block), bottom = Math.Min(height, y + block + 1);
                long sum = whole[bottom * (width + 1) + to] - whole[top * (width + 1) + to]
                    - whole[bottom * (width + 1) + from] + whole[top * (width + 1) + from];
                double average = sum / (double)((to - from) * (bottom - top));
                dark[y * width + x] = (shades[y * width + x] < average * 0.85) != inverted;
            }
        return dark;
    }

    /// <summary>The one line that parts light from dark with the least of both sides lying on the wrong side of it.</summary>
    static int Otsu(byte[] shades)
    {
        var count = new int[256];
        foreach (byte shade in shades) count[shade]++;
        long total = shades.Length;
        double sum = 0;
        for (int tone = 0; tone < 256; tone++) sum += tone * count[tone];

        double best = -1;
        int cut = 127;
        long first = 0;
        double front = 0;
        for (int tone = 0; tone < 256; tone++)
        {
            first += count[tone];
            if (first == 0) continue;
            front += tone * count[tone];
            long back = total - first;
            if (back == 0) break;

            double meanFirst = front / first, meanBack = (sum - front) / back;
            double apart = meanFirst - meanBack;
            double spread = (double)first * back / total / total * apart * apart;
            if (spread <= best) continue;
            best = spread;
            cut = tone;
        }
        return cut;
    }

    static byte[] Capture(int left, int top, int width, int height)
    {
        byte[] pixels = new byte[width * height * BytesPerPixel];
        IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, width, height);
        try
        {
            IntPtr previous = SelectObject(memory, bitmap);
            BitBlt(memory, 0, 0, width, height, screen, left, top, CopyWithLayeredWindows);
            SelectObject(memory, previous);
            var info = new BITMAPINFOHEADER
            {
                Size = Marshal.SizeOf<BITMAPINFOHEADER>(), Width = width, Height = -height, Planes = 1, BitCount = BytesPerPixel * 8,
            };
            GetDIBits(screen, bitmap, 0, (uint)height, pixels, ref info, 0);
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
        return pixels;
    }

    /// <summary>
    /// A corner as the rows that crossed it described it: where its middle came out, how wide a module it judged, how
    /// many rows said so, and the column and last row it began from, which the next row is checked against.
    /// </summary>
    readonly record struct Corner(double X, double Y, double Module, int Seen, double Column, int Row);

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr to, int x, int y, int width, int height, IntPtr from, int fromX, int fromY, int operation);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
}
