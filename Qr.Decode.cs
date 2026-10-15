using System.Numerics;
using System.Text;

namespace DynamicIsland;

/// <summary>
/// The reading half of the same rules. The marks are taken back off a grid, its blocks unweaved, their check signs
/// spent on whatever the screen lost, and the words put together again. Everything here answers for what some other
/// program printed rather than for what the island prints, so it knows all forty versions and the four levels, and it
/// asks nothing of the picture the grid came from — a matrix of bools in, the words out, or null when the marks say
/// it is no code at all.
/// </summary>
static partial class Qr
{
    const int FormatFixes = 3; // the format marks are guarded well enough to lose three of their fifteen signs
    const int FirstRoot = 0;   // where the block's generator starts counting its roots, as the standard lays down

    static readonly string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    /// <summary>UTF-8 that says plainly when the bytes are no such writing, rather than hiding the hurt in question marks.</summary>
    static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The words a grid carries. The matrix is left as it was found: its marks are unwrapped on a copy, since a grid
    /// that turns out to be no code is tried again at another size or turned another way.
    /// </summary>
    public static string? Read(bool[,] cells)
    {
        int size = cells.GetLength(0);
        int version = (size - 17) / 4;
        if (version is < 1 or > 40 || size != version * 4 + 17) return null;
        if (!Level(cells, size, out int level, out int mask)) return null;

        bool[,] grid = (bool[,])cells.Clone();
        bool[,] held = Held(size, version);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                if (!held[x, y] && Masked(mask, x, y)) grid[x, y] = !grid[x, y];

        Version param = Blocks[version - 1, level];
        byte[]? signs = Unweave(Gather(grid, held, size, param), param);
        return signs == null ? null : Words(signs, version);
    }

    /// <summary>
    /// The level and the mask a grid names, read from both copies of its format marks and mended if either has lost a
    /// sign or two on the way to the screen. The pair whose marks come closest to one of the thirty-two the standard
    /// allows wins; nothing is trusted that stands further from every one of them than three signs.
    /// </summary>
    static bool Level(bool[,] cells, int size, out int level, out int mask)
    {
        level = mask = 0;
        int nearest = int.MaxValue, found = -1;
        for (int copy = 0; copy < 2; copy++)
        {
            int read = 0;
            for (int bit = 0; bit < 15; bit++) if (Mark(cells, size, copy, bit)) read |= 1 << bit;
            for (int pair = 0; pair < 32; pair++)
            {
                int stray = BitOperations.PopCount((uint)(read ^ FormatMarks(pair)));
                if (stray >= nearest) continue;
                nearest = stray;
                found = pair;
            }
        }
        if (found < 0 || nearest > FormatFixes) return false;
        level = found >> 3;
        mask = found & 7;
        return true;
    }

    /// <summary>Where the bit a copy of the format marks carries at a given rank stands in the grid.</summary>
    static bool Mark(bool[,] cells, int size, int copy, int bit) => copy == 0
        ? bit < 8 ? cells[8, bit < 6 ? bit : bit + 1] : cells[8, size - 15 + bit]
        : bit < 8 ? cells[size - 1 - bit, 8] : cells[bit == 8 ? 7 : 14 - bit, 8];

    /// <summary>
    /// Which modules hold no data because the standard spends them on its own marks: the three finder corners with
    /// their light ground, the two timing lines, the alignment squares, the bands of format marks, and from version 7
    /// the blocks of version marks. The same hands that lay them down for the writer say where they stand here.
    /// </summary>
    static bool[,] Held(int size, int version)
    {
        var blank = new bool[size, size];
        var held = new bool[size, size];
        Finders(blank, held, size);
        Timing(blank, held, size);
        Aligns(blank, held, size, version);
        Reserve(held, size, version);
        return held;
    }

    /// <summary>
    /// The signs a grid holds, gathered the way <see cref="Spread"/> lays them: two columns at a time from the right
    /// edge, upwards then downwards, skipping every module the marks hold. Whatever the version leaves over at the end
    /// of the zigzag is read past, since it carries nothing.
    /// </summary>
    static byte[] Gather(bool[,] grid, bool[,] held, int size, Version param)
    {
        var woven = new byte[param.Data + param.Blocks * param.Ec];
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
                    if (free < woven.Length * 8 && grid[x, y]) woven[free / 8] |= (byte)(1 << (7 - free % 8));
                    free++;
                }
            }
        }
        return woven;
    }

    /// <summary>
    /// The dance of blocks taken apart again — the leading sign of every block, then the next, and the checks in the
    /// same order after all the data — and each block set right by its own check signs. Null when any of them has
    /// lost more than its signs can pay for, which means the grid was never a code, only a pattern that looked like one.
    /// </summary>
    static byte[]? Unweave(byte[] woven, Version param)
    {
        var blocks = new byte[param.Blocks][];
        for (int b = 0; b < blocks.Length; b++) blocks[b] = new byte[Room(b, param)];

        int at = 0;
        for (int i = 0; i <= param.ShortData; i++)
            for (int b = 0; b < blocks.Length; b++)
                if (i < param.ShortData + (b >= param.Split ? 1 : 0)) blocks[b][i] = woven[at++];
        for (int i = 0; i < param.Ec; i++)
            for (int b = 0; b < blocks.Length; b++)
                blocks[b][Data(b, param) + i] = woven[at++];

        var signs = new byte[param.Data];
        for (int b = 0; b < blocks.Length; b++)
        {
            if (!Mend(blocks[b], param.Ec)) return null;
            Array.Copy(blocks[b], 0, signs, Start(b, param), Data(b, param));
        }
        return signs;

        static int Start(int block, Version param) => block * param.ShortData + Math.Max(0, block - param.Split);
        static int Data(int block, Version param) => param.ShortData + (block >= param.Split ? 1 : 0);
        static int Room(int block, Version param) => Data(block, param) + param.Ec;
    }

    /// <summary>
    /// A block brought back to what was written, in place. Its check signs say whether anything was lost and, if so,
    /// which places and by how much; the whole block is checked again afterwards, so a block that comes out wrong
    /// never claims to be right.
    /// </summary>
    static bool Mend(byte[] received, int checks)
    {
        int[] syndromes = Syndromes(received, checks);
        if (Array.TrueForAll(syndromes, sign => sign == 0)) return true;
        if (Locator(syndromes, checks) is not int[] locator) return false;

        var lost = new List<int>();
        for (int degree = 0; degree < received.Length; degree++)
            if (Eval(locator, At(-degree)) == 0) lost.Add(degree);
        if (lost.Count == 0 || lost.Count != locator.Length - 1 || lost.Count * 2 > checks) return false;
        return Values(received, syndromes, lost) && Array.TrueForAll(Syndromes(received, checks), sign => sign == 0);
    }

    /// <summary>The signs a received word answers to at the roots of its generator; all zeroes means nothing was lost.</summary>
    static int[] Syndromes(byte[] received, int checks)
    {
        var syndromes = new int[checks];
        for (int i = 0; i < checks; i++) syndromes[i] = Eval(received, At(i + FirstRoot));
        return syndromes;
    }

    /// <summary>
    /// Where the losses stand, from the signs that say so: the standard's own walk over them, which keeps a running
    /// guess of how many places are hurt and mends its guess one sign at a time. The answer is the locator, whose
    /// roots name the hurt places; nothing of use comes of it when the walk runs past half the check signs.
    /// </summary>
    static int[]? Locator(int[] syndromes, int checks)
    {
        var locator = new[] { 1 };
        var last = new[] { 1 };
        int stray = 1, places = 0, since = 1;
        for (int step = 0; step < checks; step++)
        {
            int gap = syndromes[step];
            for (int i = 1; i < locator.Length && i <= step; i++) gap ^= Multiply(locator[i], syndromes[step - i]);

            if (gap == 0) { since++; continue; }
            // The shape kept against the next mending is the one before it, so it has to be a copy of its own: the
            // array is grown only when it runs short, and an in-place mending of it would mend the kept shape too.
            int[] before = [.. locator];
            int turn = Multiply(gap, Inverse(stray));
            if (locator.Length < last.Length + since) Array.Resize(ref locator, last.Length + since);
            for (int i = 0; i < last.Length; i++) locator[i + since] ^= Multiply(turn, last[i]);

            if (places < step + 1 - places)
            {
                (last, stray, places, since) = (before, gap, step + 1 - places, 1);
            }
            else since++;
        }
        // The walk's guess at how many places are hurt is the locator's own degree; the array is cut to it, since the
        // room it was grown to while mending the guess says nothing about what stands beyond it.
        return places * 2 > checks ? null : [.. locator[..(places + 1)]];
    }

    /// <summary>
    /// How much was lost at each hurt place. The signs are a set of equations over the field — each one says what the
    /// losses add up to at some power — and they are plain enough to solve straight off, since places standing apart
    /// give rows of powers that cannot fall in with one another.
    /// </summary>
    static bool Values(byte[] received, int[] syndromes, List<int> degrees)
    {
        int count = degrees.Count;
        var rows = new int[count][];
        for (int k = 0; k < count; k++)
        {
            rows[k] = new int[count + 1];
            for (int r = 0; r < count; r++) rows[k][r] = At(degrees[r] * k);
            rows[k][count] = syndromes[k];
        }
        if (!Solve(rows)) return false;

        for (int r = 0; r < count; r++) received[received.Length - 1 - degrees[r]] ^= (byte)rows[r][count];
        return true;
    }

    /// <summary>Gaussian elimination over the field; the answers stand in the last column of each row.</summary>
    static bool Solve(int[][] rows)
    {
        int count = rows.Length;
        for (int col = 0; col < count; col++)
        {
            int lead = -1;
            for (int r = col; r < count; r++) if (rows[r][col] != 0) { lead = r; break; }
            if (lead < 0) return false;
            (rows[col], rows[lead]) = (rows[lead], rows[col]);

            int scale = Inverse(rows[col][col]);
            for (int k = col; k <= count; k++) rows[col][k] = Multiply(rows[col][k], scale);
            for (int r = 0; r < count; r++)
            {
                if (r == col || rows[r][col] == 0) continue;
                // The weight of this row against the lead is taken before the row is written, since the first sign it
                // is spent upon is the very one standing in that place.
                int weight = rows[r][col];
                for (int k = col; k <= count; k++) rows[r][k] ^= Multiply(weight, rows[col][k]);
            }
        }
        return true;
    }

    /// <summary>
    /// The words out of a block's signs: mode after mode, until the terminator or the end of the signs. Numbers and
    /// the uppercase alphabet are packed by threes and pairs, everything else arrives as bytes and is read as the
    /// writing's own UTF-8 unless an extended channel header names another single-byte set.
    /// </summary>
    static string? Words(byte[] signs, int version)
    {
        var text = new StringBuilder();
        var bytes = new List<byte>();
        Encoding writing = StrictUtf8;
        int at = 0, last = signs.Length * 8;

        void Flush()
        {
            if (bytes.Count == 0) return;
            text.Append(Written(writing, [.. bytes]));
            bytes.Clear();
        }

        while (at + 4 <= last)
        {
            int room;
            switch (Take(signs, ref at, 4))
            {
                case 0:
                    Flush();
                    return text.Length == 0 ? null : text.ToString();
                case 1: // digits, three of them to ten signs apiece
                    room = Count(signs, ref at, version, 10, 12, 14);
                    if (room < 0 || !Digits(Flush, text, signs, ref at, last, room)) return null;
                    break;
                case 2: // the uppercase alphabet, two letters to eleven signs
                    room = Count(signs, ref at, version, 9, 11, 13);
                    if (room < 0 || !Letters(Flush, text, signs, ref at, last, room)) return null;
                    break;
                case 4:
                    room = Count(signs, ref at, version, 8, 16, 16);
                    if (room < 0 || at + room * 8 > last) return null;
                    for (int i = 0; i < room; i++) bytes.Add((byte)Take(signs, ref at, 8));
                    break;
                case 3:
                    at += 8; // the two halves naming which of a few codes written together this one is
                    break;
                case 5:
                    break; // the mark of a store's code, which says nothing itself and hands what follows over
                case 6:
                    at += 4; // the same mark standing later, telling which part of the number follows
                    break;
                case 7:
                    Extended(signs, ref at, ref writing);
                    break;
                default:
                    return null; // kanji, and anything else the island has no use for
            }
        }
        Flush();
        return text.Length == 0 ? null : text.ToString();
    }

    static bool Digits(Action Flush, StringBuilder text, byte[] signs, ref int at, int last, int room)
    {
        Flush();
        for (; room > 0; room -= 3)
        {
            (int count, int bits) = room switch { >= 3 => (3, 10), 2 => (2, 7), _ => (1, 4) };
            if (at + bits > last) return false;
            text.Append(Take(signs, ref at, bits).ToString().PadLeft(count, '0'));
        }
        return true;
    }

    static bool Letters(Action Flush, StringBuilder text, byte[] signs, ref int at, int last, int room)
    {
        Flush();
        for (; room > 0; room -= 2)
        {
            if (room == 1)
            {
                if (at + 6 > last) return false;
                text.Append(Alphabet[Take(signs, ref at, 6)]);
                break;
            }
            if (at + 11 > last) return false;
            int pair = Take(signs, ref at, 11);
            text.Append(Alphabet[pair / 45]).Append(Alphabet[pair % 45]);
        }
        return true;
    }

    /// <summary>
    /// The bytes of one segment read in the writing it names, and in the single-byte alphabet every reader falls back
    /// on when that writing can make no whole character of them. A code written without an extended channel header
    /// says nothing of its own writing, and the accented names of the world arrive both ways about.
    /// </summary>
    static string Written(Encoding writing, byte[] bytes)
    {
        try { return writing.GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    /// <summary>An extended channel header: the number of the character set it names, and the set it means.</summary>
    static void Extended(byte[] signs, ref int at, ref Encoding writing)
    {
        int first = Take(signs, ref at, 8);
        int number = first switch
        {
            < 0x80 => first,
            < 0xC0 => ((first & 0x3F) << 8) | Take(signs, ref at, 8),
            _ => ((first & 0x1F) << 16) | (Take(signs, ref at, 8) << 8) | Take(signs, ref at, 8),
        };
        writing = number switch
        {
            3 or 4 or 103 => Encoding.Latin1,
            _ => StrictUtf8, // UTF-8 itself, and every set the island has no table for
        };
    }

    /// <summary>How many signs a mode counts its length in, which the version decides; negative when they run out.</summary>
    static int Count(byte[] signs, ref int at, int version, int few, int some, int many)
    {
        int bits = version switch { <= 9 => few, <= 26 => some, _ => many };
        return at + bits > signs.Length * 8 ? -1 : Take(signs, ref at, bits);
    }

    static int Take(byte[] signs, ref int at, int bits)
    {
        int value = 0;
        for (int i = 0; i < bits; i++, at++)
        {
            // A code whose counts ask for more signs than it holds is a malformed one; the zeroes it reads back end it
            // on its own rather than throwing over the screen it was taken from.
            int index = at / 8;
            value = value << 1 | (index < signs.Length ? (signs[index] >> (7 - at % 8) & 1) : 0);
        }
        return value;
    }

    /// <summary>The field's number raised to a power, which the table keeps for any exponent, negative or vast.</summary>
    static int At(int power) => Exp[((power % 255) + 255) % 255];

    static int Inverse(int value) => value == 0 ? 0 : Exp[255 - Log[value]];

    /// <summary>A polynomial at a point, its signs lying from the highest power down.</summary>
    static int Eval(byte[] poly, int at)
    {
        int sum = 0;
        foreach (byte sign in poly) sum = Multiply(sum, at) ^ sign;
        return sum;
    }

    /// <summary>A polynomial at a point, its signs lying from the constant up; the locator is written that way.</summary>
    static int Eval(int[] poly, int at)
    {
        int sum = 0;
        for (int i = poly.Length - 1; i >= 0; i--) sum = Multiply(sum, at) ^ poly[i];
        return sum;
    }
}
