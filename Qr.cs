namespace DynamicIsland;

using System.Text;

/// <summary>
/// QR codes drawn and read by hand: no library, no package, only the plain rules of ISO/IEC 18004. The island needs
/// them for two things, to point a phone at the screen and open the bridge page without anyone typing an address, and
/// to pick up a code that lies on the screen already. For the first, byte mode at error level M is enough, and
/// versions 1 through 10 hold any link a local network can give; the second has to be ready for whatever another
/// program printed, so the reader knows every version and all four levels. The matrices neither end knows anything of
/// WPF — they are bools, and whoever draws one decides the colors, whoever reads one decides where they came from.
/// </summary>
static partial class Qr
{
    const int LevelM = 0; // the two signs of our error level, which is the only one we make

    /// <summary>
    /// How every version shares its codewords at every error level: <see cref="Short"/> blocks of
    /// <see cref="ShortData"/> signs apiece, then <see cref="Long"/> blocks of one more, every block guarded by
    /// <see cref="Ec"/> check signs. From this both the room for data and the shape of the interleaving follow, so
    /// nothing can fall out of step with it. The rows stand in the order the format marks name their levels, so a
    /// reader that has just unwrapped those marks takes its row by the number it found.
    /// </summary>
    readonly record struct Version(int Short, int ShortData, int Long, int Ec)
    {
        public int Data => Short * ShortData + Long * (ShortData + 1);
        public int Blocks => Short + Long;
        /// <summary>Where the blocks give over from the short ones to the long.</summary>
        public int Split => Short;
    }

    static readonly Version[,] Blocks =
    {
        //  M                    L                     H                     Q
        { new(1,16,0,10),  new(1,19,0,7),   new(1,9,0,17),   new(1,13,0,13) },  // v1
        { new(1,28,0,16),  new(1,34,0,10),  new(1,16,0,28),  new(1,22,0,22) },  // v2
        { new(1,44,0,26),  new(1,55,0,15),  new(2,13,0,22),  new(2,17,0,18) },  // v3
        { new(2,32,0,18),  new(1,80,0,20),  new(4,9,0,16),   new(2,24,0,26) },  // v4
        { new(2,43,0,24),  new(1,108,0,26), new(2,11,2,22),  new(2,15,2,18) },  // v5
        { new(4,27,0,16),  new(2,68,0,18),  new(4,15,0,28),  new(4,19,0,24) },  // v6
        { new(4,31,0,18),  new(2,78,0,20),  new(4,13,1,26),  new(2,14,4,18) },  // v7
        { new(2,38,2,22),  new(2,97,0,24),  new(4,14,2,26),  new(4,18,2,22) },  // v8
        { new(3,36,2,22),  new(2,116,0,30), new(4,12,4,24),  new(4,16,4,20) },  // v9
        { new(4,43,1,26),  new(2,68,2,18),  new(6,15,2,28),  new(6,19,2,24) },  // v10
        { new(1,50,4,30),  new(4,81,0,20),  new(3,12,8,24),  new(4,22,4,28) },  // v11
        { new(6,36,2,22),  new(2,92,2,24),  new(7,14,4,28),  new(4,20,6,26) },  // v12
        { new(8,37,1,22),  new(4,107,0,26), new(12,11,4,22), new(8,20,4,24) },  // v13
        { new(4,40,5,24),  new(3,115,1,30), new(11,12,5,24), new(11,16,5,20) }, // v14
        { new(5,41,5,24),  new(5,87,1,22),  new(11,12,7,24), new(5,24,7,30) },  // v15
        { new(7,45,3,28),  new(5,98,1,24),  new(3,15,13,30), new(15,19,2,24) }, // v16
        { new(10,46,1,28), new(1,107,5,28), new(2,14,17,28), new(1,22,15,28) }, // v17
        { new(9,43,4,26),  new(5,120,1,30), new(2,14,19,28), new(17,22,1,28) }, // v18
        { new(3,44,11,26), new(3,113,4,28), new(9,13,16,26), new(17,21,4,26) }, // v19
        { new(3,41,13,26), new(3,107,5,28), new(15,15,10,28), new(15,24,5,30) }, // v20
        { new(17,42,0,26), new(4,116,4,28), new(19,16,6,30), new(17,22,6,28) }, // v21
        { new(17,46,0,28), new(2,111,7,28), new(34,13,0,24), new(7,24,16,30) }, // v22
        { new(4,47,14,28), new(4,121,5,30), new(16,15,14,30), new(11,24,14,30) }, // v23
        { new(6,45,14,28), new(6,117,4,30), new(30,16,2,30), new(11,24,16,30) }, // v24
        { new(8,47,13,28), new(8,106,4,26), new(22,15,13,30), new(7,24,22,30) }, // v25
        { new(19,46,4,28), new(10,114,2,28), new(33,16,4,30), new(28,22,6,28) }, // v26
        { new(22,45,3,28), new(8,122,4,30), new(12,15,28,30), new(8,23,26,30) }, // v27
        { new(3,45,23,28), new(3,117,10,30), new(11,15,31,30), new(4,24,31,30) }, // v28
        { new(21,45,7,28), new(7,116,7,30), new(19,15,26,30), new(1,23,37,30) }, // v29
        { new(19,47,10,28), new(5,115,10,30), new(23,15,25,30), new(15,24,25,30) }, // v30
        { new(2,46,29,28), new(13,115,3,30), new(23,15,28,30), new(42,24,1,30) }, // v31
        { new(10,46,23,28), new(17,115,0,30), new(19,15,35,30), new(10,24,35,30) }, // v32
        { new(14,46,21,28), new(17,115,1,30), new(11,15,46,30), new(29,24,19,30) }, // v33
        { new(14,46,23,28), new(13,115,6,30), new(59,16,1,30), new(44,24,7,30) }, // v34
        { new(12,47,26,28), new(12,121,7,30), new(22,15,41,30), new(39,24,14,30) }, // v35
        { new(6,47,34,28), new(6,121,14,30), new(2,15,64,30), new(46,24,10,30) }, // v36
        { new(29,46,14,28), new(17,122,4,30), new(24,15,46,30), new(49,24,10,30) }, // v37
        { new(13,46,32,28), new(4,122,18,30), new(42,15,32,30), new(48,24,14,30) }, // v38
        { new(40,47,7,28), new(20,117,4,30), new(10,15,67,30), new(43,24,22,30) }, // v39
        { new(18,47,31,28), new(19,118,6,30), new(20,15,61,30), new(34,24,34,30) }, // v40
    };

    // The centers of the small square patterns, as the standard names them; none for version 1.
    static readonly int[][] Alignments =
    [
        [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34], [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50],
        [6, 30, 54], [6, 32, 58], [6, 34, 62], [6, 26, 46, 66], [6, 26, 48, 70], [6, 26, 50, 74], [6, 30, 54, 78],
        [6, 30, 56, 82], [6, 30, 58, 86], [6, 34, 62, 90], [6, 28, 50, 72, 94], [6, 26, 50, 74, 98],
        [6, 30, 54, 78, 102], [6, 28, 54, 80, 106], [6, 32, 58, 84, 110], [6, 30, 58, 86, 114], [6, 34, 62, 90, 118],
        [6, 26, 50, 74, 98, 122], [6, 30, 54, 78, 102, 126], [6, 26, 52, 78, 104, 130], [6, 30, 56, 82, 108, 134],
        [6, 34, 60, 86, 112, 138], [6, 30, 58, 86, 114, 142], [6, 34, 62, 90, 118, 146], [6, 30, 54, 78, 102, 126, 150],
        [6, 24, 50, 76, 102, 128, 154], [6, 28, 54, 80, 106, 132, 158], [6, 32, 58, 84, 110, 136, 162],
        [6, 26, 54, 82, 110, 138, 166], [6, 30, 58, 86, 114, 142, 170],
    ];

    /// <summary>
    /// The levels we write at, which is the column of <see cref="Blocks"/> the format marks name for level M, taken
    /// as far as our own codes go.
    /// </summary>
    static readonly Version[] Versions =
        [.. Enumerable.Range(1, 10).Select(version => Blocks[version - 1, LevelM])];

    static readonly byte[] PadBytes = [0xEC, 0x11];
    static readonly int[] Exp = new int[256], Log = new int[256];
    static readonly Dictionary<int, byte[]> Generators = [];
    static readonly int[] NearFinder = [0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1], PastFinder = [1, 0, 1, 1, 1, 0, 1, 0, 0, 0, 0];

    /// <summary>
    /// Builds the closed modules of a QR code holding <paramref name="text"/>.
    /// </summary>
    /// <param name="mask">A mask to force, for the sake of a test; by default the handsomest of the eight is chosen.</param>
    /// <returns>The matrix, one bool per module, or null when the text is more than version 10 can carry.</returns>
    public static bool[,]? Make(string text, int? mask = null)
    {
        byte[] body = Encoding.UTF8.GetBytes(text);
        int version = Array.FindIndex(Versions, v => 4 + (v == Versions[^1] ? 16 : 8) + body.Length * 8 <= v.Data * 8);
        return version < 0 ? null : Build(version + 1, body, mask);
    }

    /// <summary>The same, at a version named on purpose, so a code printed beside others keeps its size.</summary>
    public static bool[,]? Make(string text, int version, int? mask = null)
    {
        byte[] body = Encoding.UTF8.GetBytes(text);
        return version is < 1 or > 10 || 4 + (version < 10 ? 8 : 16) + body.Length * 8 > Versions[version - 1].Data * 8
            ? null
            : Build(version, body, mask);
    }

    /// <summary>
    /// Paints the matrix into a BGRA buffer, one pixel per module, ringed by <paramref name="quiet"/> pixels of white.
    /// A scanner reads nothing without that margin, and the buffer comes out ready to blit. The matrix counts its
    /// modules by column first and row after, as the laying always has, so a pixel of a given row and column takes the
    /// module of that column and row — painted the other way about the picture is a mirror, and no phone opens it.
    /// </summary>
    /// <returns>The edge of the picture, in pixels.</returns>
    public static int Paint(bool[,] cells, int quiet, byte[] buffer)
    {
        int side = cells.GetLength(0) + quiet * 2;
        for (int at = 0; at < side * side * 4; at += 4)
        {
            buffer[at] = buffer[at + 1] = buffer[at + 2] = 0xFF; // white plate
            buffer[at + 3] = 0xFF;                               // and nothing of it see-through
        }
        for (int row = 0; row < cells.GetLength(1); row++)
        {
            for (int column = 0; column < cells.GetLength(0); column++)
            {
                if (!cells[column, row]) continue;
                int at = ((row + quiet) * side + column + quiet) * 4;
                buffer[at] = buffer[at + 1] = buffer[at + 2] = 0;
            }
        }
        return side;
    }

    static bool[,] Build(int version, byte[] body, int? chosen)
    {
        Version param = Versions[version - 1];
        byte[] stream = Stream(param, version, body);

        int size = version * 4 + 17;
        var matrix = new bool[size, size];
        var held = new bool[size, size];
        Finders(matrix, held, size);
        Timing(matrix, held, size);
        Aligns(matrix, held, size, version);
        Reserve(held, size, version);
        Spread(matrix, held, size, stream);

        int best = chosen ?? 0;
        if (chosen == null)
        {
            int least = int.MaxValue;
            for (int m = 0; m < 8; m++)
            {
                Format(matrix, size, version, m);
                int score = Penalty(matrix, size);
                if (score < least)
                {
                    least = score;
                    best = m;
                }
            }
        }
        Format(matrix, size, version, best);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                if (!held[x, y] && Masked(best, x, y)) matrix[x, y] = !matrix[x, y];
        return matrix;
    }

    /// <summary>
    /// The whole stream a version lays down: the signs of data and their checks, wound together the way the standard
    /// reads them — the leading sign of each block, then the next, and only after all data the checks in the same
    /// dance. Whatever room is left at the end stays open, which is as it should be.
    /// </summary>
    static byte[] Stream(Version param, int version, byte[] body)
    {
        var bits = new List<bool>(4 + (version < 10 ? 8 : 16) + body.Length * 8 + 4);
        Add(bits, 4, 4); // byte mode
        Add(bits, body.Length, version < 10 ? 8 : 16);
        foreach (byte sign in body) Add(bits, sign, 8);
        Add(bits, 0, Math.Min(4, param.Data * 8 - bits.Count));

        var data = new byte[param.Data];
        for (int at = 0; at < bits.Count; at++)
            if (bits[at]) data[at / 8] |= (byte)(1 << (7 - at % 8));
        int filled = (bits.Count + 7) / 8;
        for (int at = filled; at < data.Length; at++) data[at] = PadBytes[(at - filled) % 2];

        var checks = new byte[param.Blocks][];
        for (int b = 0; b < param.Blocks; b++)
            checks[b] = Remainder(data.AsSpan(Start(b, param), param.ShortData + (b >= param.Split ? 1 : 0)), param.Ec);

        var stream = new byte[param.Data + param.Blocks * param.Ec];
        int at2 = 0;
        for (int i = 0; i <= param.ShortData; i++)
            for (int b = 0; b < param.Blocks; b++)
                if (i < param.ShortData + (b >= param.Split ? 1 : 0)) stream[at2++] = data[Start(b, param) + i];
        for (int i = 0; i < param.Ec; i++)
            for (int b = 0; b < param.Blocks; b++) stream[at2++] = checks[b][i];
        return stream;

        static int Start(int block, Version param) => block * param.ShortData + Math.Max(0, block - param.Split);
    }

    static void Add(List<bool> bits, int value, int count)
    {
        for (int i = count - 1; i >= 0; i--) bits.Add((value & (1 << i)) != 0);
    }

    /// <summary>
    /// Lays the stream over the grid, two columns at a time from the right edge, upwards then downwards, giving each
    /// free module the next sign. The vertical timing line is no column of data and no pair may straddle it, so the
    /// pairing shifts one column left as soon as it reaches that line and every pair after it lands a column over.
    /// </summary>
    static void Spread(bool[,] matrix, bool[,] held, int size, byte[] stream)
    {
        int free = 0;
        for (int pair = 0; pair < (size + 1) / 2; pair++)
        {
            int right = Columns(pair, size);
            bool up = pair % 2 == 0;
            for (int step = 0; step < size; step++)
            {
                int y = up ? size - 1 - step : step;
                for (int side = 0; side < 2; side++)
                {
                    int x = right - side;
                    if (x < 0 || held[x, y]) continue;
                    int index = free++;
                    matrix[x, y] = index < stream.Length * 8 && (stream[index / 8] & (1 << (7 - index % 8))) != 0;
                }
            }
        }
    }

    /// <summary>
    /// The right column of the pair a given step of the zigzag works on. The pairs begin at the edge and step two
    /// columns left apiece, but the vertical timing line standing at column six is no data column and no pair straddles
    /// it, so from that line leftward every pair sits one column nearer the middle than its step would say.
    /// </summary>
    static int Columns(int pair, int size)
    {
        int right = size - 1 - pair * 2;
        return right <= 6 ? right - 1 : right;
    }

    static void Finders(bool[,] matrix, bool[,] held, int size)
    {
        Set(matrix, held, size, 3, 3);
        Set(matrix, held, size, size - 4, 3);
        Set(matrix, held, size, 3, size - 4);

        static void Set(bool[,] matrix, bool[,] held, int size, int cx, int cy)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= size || y >= size) continue;
                    held[x, y] = true;
                    int ring = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    matrix[x, y] = ring is 0 or 1 or 3; // the fourth ring is the light separator
                }
        }
    }

    static void Timing(bool[,] matrix, bool[,] held, int size)
    {
        for (int i = 0; i < size; i++)
        {
            if (!held[6, i])
            {
                held[6, i] = true;
                matrix[6, i] = i % 2 == 0;
            }
            if (!held[i, 6])
            {
                held[i, 6] = true;
                matrix[i, 6] = i % 2 == 0;
            }
        }
    }

    static void Aligns(bool[,] matrix, bool[,] held, int size, int version)
    {
        int[] centers = Alignments[version - 1];
        foreach (int cx in centers)
            foreach (int cy in centers)
            {
                if ((cx <= 8 && cy <= 8) || (cx >= size - 9 && cy <= 8) || (cx <= 8 && cy >= size - 9)) continue;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        held[cx + dx, cy + dy] = true;
                        matrix[cx + dx, cy + dy] = Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1;
                    }
            }
    }

    /// <summary>
    /// Shuts the modules that carry no data out of the zigzag: the two bands of format marks along the finder
    /// corners, and from version 7 the two blocks of version marks. The dark module, which the standard promises on
    /// its own, belongs to no mark and is written once by the finder pattern that covers its place.
    /// </summary>
    static void Reserve(bool[,] held, int size, int version)
    {
        for (int i = 0; i < 9; i++)
        {
            held[8, i] = true;
            held[i, 8] = true;
        }
        for (int i = size - 8; i < size; i++)
        {
            held[8, i] = true;
            held[i, 8] = true;
        }
        if (version < 7) return;
        for (int bit = 0; bit < 18; bit++)
        {
            int row = bit / 3, col = bit % 3;
            held[size - 11 + col, row] = true;
            held[row, size - 11 + col] = true;
        }
    }

    /// <summary>Writes both copies of the format marks, and the dark module between them.</summary>
    static void Format(bool[,] matrix, int size, int version, int mask)
    {
        int bits = FormatMarks((LevelM << 3) | mask);

        for (int bit = 0; bit < 15; bit++)
        {
            bool on = (bits & (1 << bit)) != 0;
            if (bit < 8)
            {
                matrix[8, bit < 6 ? bit : bit + 1] = on; // up the left finder, past its timing line
                matrix[size - 1 - bit, 8] = on;          // along the top of the right one
            }
            else
            {
                matrix[8, size - 15 + bit] = on;         // down the right finder
                matrix[bit == 8 ? 7 : 14 - bit, 8] = on; // along the left finder, past its timing line
            }
        }
        matrix[8, size - 8] = true;

        if (version < 7) return;
        int info = VersionInfo(version);
        for (int bit = 0; bit < 18; bit++)
        {
            bool on = (info & (1 << bit)) != 0;
            int row = bit / 3, col = bit % 3;
            matrix[size - 11 + col, row] = on;
            matrix[row, size - 11 + col] = on;
        }
    }

    /// <summary>
    /// The fifteen signs a level and a mask stand for: the five of them guarded by five more of the field's own
    /// reckoning, and blinded against the all-dark code so no scanner reads a plain grid as one. Both the hand that
    /// writes the marks and the one that reads them back come by this way to the same number.
    /// </summary>
    static int FormatMarks(int pair)
    {
        int rest = pair << 10;
        for (int i = 14; i >= 10; i--) if ((rest & (1 << i)) != 0) rest ^= 0x537 << (i - 10);
        return ((pair << 10) | rest) ^ 0x5412;
    }

    static int VersionInfo(int version)
    {
        int rest = version << 12;
        for (int i = 17; i >= 12; i--) if ((rest & (1 << i)) != 0) rest ^= 0x1F25 << (i - 12);
        return (version << 12) | rest;
    }

    /// <summary>
    /// The four rewards the standard pays for a handsomest matrix: long runs of one color, motifs that echo a finder
    /// pattern, squares of one color, and how far the dark modules stray from half the whole.
    /// </summary>
    static int Penalty(bool[,] matrix, int size)
    {
        int score = 0;
        for (int line = 0; line < size; line++)
        {
            score += Runs(matrix, size, line, true) + Runs(matrix, size, line, false);
            score += Motifs(matrix, size, line, true) + Motifs(matrix, size, line, false);
        }
        for (int y = 0; y + 1 < size; y++)
            for (int x = 0; x + 1 < size; x++)
            {
                bool a = matrix[x, y];
                if (a && a == matrix[x + 1, y] && a == matrix[x, y + 1] && a == matrix[x + 1, y + 1]) score += 3;
            }
        int dark = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                if (matrix[x, y]) dark++;
        int percent = dark * 100 / (size * size);
        return score + (Math.Abs(percent - 50) + 4) / 5 * 10;
    }

    static int Runs(bool[,] matrix, int size, int line, bool rows)
    {
        int score = 0, run = 1;
        for (int i = 1; i < size; i++)
        {
            bool now = rows ? matrix[i, line] : matrix[line, i];
            if (now == (rows ? matrix[i - 1, line] : matrix[line, i - 1])) run++;
            else
            {
                if (run >= 5) score += run + 2;
                run = 1;
            }
        }
        return run >= 5 ? score + run + 2 : score;
    }

    static int Motifs(bool[,] matrix, int size, int line, bool rows)
    {
        int score = 0;
        var window = new int[11];
        for (int at = 0; at + 11 <= size; at++)
        {
            for (int i = 0; i < 11; i++) window[i] = (rows ? matrix[at + i, line] : matrix[line, at + i]) ? 1 : 0;
            if (Matches(window, NearFinder) || Matches(window, PastFinder)) score += 40;
        }
        return score;
    }

    static bool Matches(int[] window, int[] motif)
    {
        for (int i = 0; i < 11; i++) if (window[i] != motif[i]) return false;
        return true;
    }

    static bool Masked(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => (x / 3 + y / 2) % 2 == 0,
        5 => x * y % 2 + x * y % 3 == 0,
        6 => (x * y % 2 + x * y % 3) % 2 == 0,
        _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
    };

    /// <summary>The field of twenty-five signs, with the primitive the standard lays down for it.</summary>
    static Qr()
    {
        int root = 1;
        for (int i = 0; i < 256; i++)
        {
            Exp[i] = root;
            Log[root] = i;
            root <<= 1;
            if ((root & 0x100) != 0) root ^= 0x11D;
        }
        Exp[255] = 1;
    }

    /// <summary>
    /// The check signs of one block: the data taken as a polynomial, less the generator of the needed degree, which
    /// is the remainder of long division over that field.
    /// </summary>
    static byte[] Remainder(ReadOnlySpan<byte> data, int degree)
    {
        byte[] generator = Generator(degree);
        var rest = new byte[degree];
        foreach (byte sign in data)
        {
            int lead = sign ^ rest[0];
            Array.Copy(rest, 1, rest, 0, degree - 1);
            rest[degree - 1] = 0;
            for (int i = 0; i < degree; i++) rest[i] ^= Multiply(lead, generator[i + 1]);
        }
        return rest;
    }

    /// <summary>The generator of a degree, from its high sign down; raised once from the field and kept for good.</summary>
    static byte[] Generator(int degree)
    {
        lock (Generators)
        {
            if (Generators.TryGetValue(degree, out byte[]? known)) return known;
            byte[] poly = [1];
            for (int i = 0; i < degree; i++)
            {
                var next = new byte[poly.Length + 1];
                for (int c = 0; c < poly.Length; c++)
                {
                    next[c] ^= poly[c];
                    next[c + 1] ^= Multiply(poly[c], Exp[i]);
                }
                poly = next;
            }
            return Generators[degree] = poly;
        }
    }

    static byte Multiply(int a, int b) => a == 0 || b == 0 ? (byte)0 : (byte)Exp[(Log[a] + Log[b]) % 255];
}
