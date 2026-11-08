using System.Globalization;
using System.Text;

namespace AiPulse.Services.Security;

/// <summary>
/// A small QR Code (ISO/IEC 18004) encoder: byte mode, error-correction level M, versions 1-40, automatic mask choice. Written
/// here rather than pulled in as a package because the only thing AiPulse ever encodes is a ~120-character otpauth:// URI for
/// two-factor enrolment, and a dependency (plus its image stack) is a lot to carry for that. The structure follows Project
/// Nayuki's reference implementation (MIT); the tests decode the output with an independent reader and check the ISO vectors.
/// </summary>
public sealed class QrCode
{
    public int Version { get; }
    public int Size { get; }
    public int Mask { get; private set; }
    private readonly bool[,] _modules;   // [y, x], true = dark
    private readonly bool[,] _isFunction;

    public bool this[int x, int y] => _modules[y, x];

    // Error-correction level M only. Index = version (0 unused). From ISO/IEC 18004 table 9.
    private static readonly int[] EccCodewordsPerBlock =
    {
        -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26,
        26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28
    };
    private static readonly int[] NumErrorCorrectionBlocks =
    {
        -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16,
        17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49
    };
    private const int FormatBitsForLevelM = 0; // ECC level M is encoded as 00 in the format information

    private QrCode(int version)
    {
        Version = version;
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _isFunction = new bool[Size, Size];
    }

    public static QrCode EncodeText(string text) => EncodeBytes(Encoding.UTF8.GetBytes(text));

    public static QrCode EncodeBytes(byte[] data)
    {
        int version;
        for (version = 1; ; version++)
        {
            if (version > 40) throw new ArgumentException("Data too long for a QR code.", nameof(data));
            var capacityBits = NumDataCodewords(version) * 8;
            if (4 + CharCountBits(version) + data.Length * 8 <= capacityBits) break;
        }

        var bits = new List<bool>();
        AppendBits(bits, 0b0100, 4); // byte mode
        AppendBits(bits, data.Length, CharCountBits(version));
        foreach (var b in data) AppendBits(bits, b, 8);

        var capacity = NumDataCodewords(version) * 8;
        AppendBits(bits, 0, Math.Min(4, capacity - bits.Count)); // terminator
        AppendBits(bits, 0, (8 - bits.Count % 8) % 8);            // byte align
        for (var pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11)
            AppendBits(bits, pad, 8);

        var dataCodewords = new byte[bits.Count / 8];
        for (var i = 0; i < bits.Count; i++)
            if (bits[i]) dataCodewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));

        var qr = new QrCode(version);
        qr.DrawFunctionPatterns();
        qr.DrawCodewords(qr.AddEccAndInterleave(dataCodewords));
        qr.ChooseAndApplyMask();
        return qr;
    }

    /// <summary>Data codewords available at level M - public so tests can check them against the ISO capacity table.</summary>
    public static int NumDataCodewords(int version) =>
        NumRawDataModules(version) / 8 - EccCodewordsPerBlock[version] * NumErrorCorrectionBlocks[version];

    private static int CharCountBits(int version) => version <= 9 ? 8 : 16;

    private static int NumRawDataModules(int version)
    {
        var result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var numAlign = version / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (version >= 7) result -= 36;
        }
        return result;
    }

    private static void AppendBits(List<bool> bits, int value, int length)
    {
        for (var i = length - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
    }

    // ---- Function patterns ----

    private void DrawFunctionPatterns()
    {
        for (var i = 0; i < Size; i++)
        {
            SetFunction(6, i, i % 2 == 0);
            SetFunction(i, 6, i % 2 == 0);
        }

        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        var positions = AlignmentPatternPositions(Version);
        var n = positions.Length;
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0)) continue; // overlaps a finder
                DrawAlignment(positions[i], positions[j]);
            }

        DrawFormatBits(0); // placeholder so the area is reserved; redrawn with the real mask later
        DrawVersion();
    }

    public static int[] AlignmentPatternPositions(int version)
    {
        if (version == 1) return Array.Empty<int>();
        var numAlign = version / 7 + 2;
        var step = (version * 8 + numAlign * 3 + 5) / (numAlign * 4 - 4) * 2;
        var result = new int[numAlign];
        result[0] = 6;
        for (int i = numAlign - 1, pos = version * 4 + 17 - 7; i >= 1; i--, pos -= step) result[i] = pos;
        return result;
    }

    private void DrawFinder(int x, int y)
    {
        for (var dy = -4; dy <= 4; dy++)
            for (var dx = -4; dx <= 4; dx++)
            {
                var dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int xx = x + dx, yy = y + dy;
                if (xx >= 0 && xx < Size && yy >= 0 && yy < Size)
                    SetFunction(xx, yy, dist != 2 && dist != 4);
            }
    }

    private void DrawAlignment(int x, int y)
    {
        for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
                SetFunction(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    /// <summary>The 15-bit format information (level + mask, BCH-protected, XOR-masked) - public for the ISO-table test.</summary>
    public static int FormatBits(int mask)
    {
        var data = (FormatBitsForLevelM << 3) | mask;
        var rem = data;
        for (var i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        return ((data << 10) | rem) ^ 0x5412;
    }

    private void DrawFormatBits(int mask)
    {
        var bits = FormatBits(mask);
        for (var i = 0; i <= 5; i++) SetFunction(8, i, GetBit(bits, i));
        SetFunction(8, 7, GetBit(bits, 6));
        SetFunction(8, 8, GetBit(bits, 7));
        SetFunction(7, 8, GetBit(bits, 8));
        for (var i = 9; i < 15; i++) SetFunction(14 - i, 8, GetBit(bits, i));

        for (var i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, GetBit(bits, i));
        for (var i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, GetBit(bits, i));
        SetFunction(8, Size - 8, true); // the always-dark module
    }

    /// <summary>The 18-bit version information block (versions 7+) - public for the ISO-table test.</summary>
    public static int VersionBits(int version)
    {
        var rem = version;
        for (var i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        return (version << 12) | rem;
    }

    private void DrawVersion()
    {
        if (Version < 7) return;
        var bits = VersionBits(Version);
        for (var i = 0; i < 18; i++)
        {
            var bit = GetBit(bits, i);
            int a = Size - 11 + i % 3, b = i / 3;
            SetFunction(a, b, bit);
            SetFunction(b, a, bit);
        }
    }

    private void SetFunction(int x, int y, bool dark)
    {
        _modules[y, x] = dark;
        _isFunction[y, x] = true;
    }

    private static bool GetBit(int x, int i) => ((x >> i) & 1) != 0;

    // ---- Error correction ----

    private byte[] AddEccAndInterleave(byte[] data)
    {
        var numBlocks = NumErrorCorrectionBlocks[Version];
        var blockEccLen = EccCodewordsPerBlock[Version];
        var rawCodewords = NumRawDataModules(Version) / 8;
        var numShortBlocks = numBlocks - rawCodewords % numBlocks;
        var shortBlockLen = rawCodewords / numBlocks;

        var divisor = ReedSolomonDivisor(blockEccLen);
        var blocks = new List<byte[]>();
        for (int i = 0, k = 0; i < numBlocks; i++)
        {
            var datLen = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
            var dat = data.AsSpan(k, datLen).ToArray();
            k += datLen;
            var block = new byte[shortBlockLen + 1];
            dat.CopyTo(block, 0);
            ReedSolomonRemainder(dat, divisor).CopyTo(block, block.Length - blockEccLen);
            blocks.Add(block);
        }

        var result = new byte[rawCodewords];
        for (int i = 0, k = 0; i < blocks[0].Length; i++)
            for (var j = 0; j < blocks.Count; j++)
                if (i != shortBlockLen - blockEccLen || j >= numShortBlocks) // short blocks have no byte at this column
                    result[k++] = blocks[j][i];
        return result;
    }

    public static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < result.Length; j++)
            {
                result[j] = (byte)GfMultiply(result[j], root);
                if (j + 1 < result.Length) result[j] ^= result[j + 1];
            }
            root = GfMultiply(root, 0x02);
        }
        return result;
    }

    public static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++) result[i] ^= (byte)GfMultiply(divisor[i], factor);
        }
        return result;
    }

    private static int GfMultiply(int x, int y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z;
    }

    // ---- Data placement and masking ----

    private void DrawCodewords(byte[] data)
    {
        var i = 0;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5; // skip the vertical timing column
            for (var vert = 0; vert < Size; vert++)
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? Size - 1 - vert : vert;
                    if (!_isFunction[y, x] && i < data.Length * 8)
                    {
                        _modules[y, x] = GetBit(data[i >> 3], 7 - (i & 7));
                        i++;
                    }
                }
        }
    }

    public static bool MaskBit(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => (x / 3 + y / 2) % 2 == 0,
        5 => x * y % 2 + x * y % 3 == 0,
        6 => (x * y % 2 + x * y % 3) % 2 == 0,
        7 => ((x + y) % 2 + x * y % 3) % 2 == 0,
        _ => throw new ArgumentOutOfRangeException(nameof(mask))
    };

    private void ApplyMask(int mask)
    {
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
                if (!_isFunction[y, x] && MaskBit(mask, x, y))
                    _modules[y, x] = !_modules[y, x];
    }

    private void ChooseAndApplyMask()
    {
        var best = 0;
        var bestPenalty = int.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask);
            DrawFormatBits(mask);
            var penalty = PenaltyScore();
            if (penalty < bestPenalty)
            {
                best = mask;
                bestPenalty = penalty;
            }
            ApplyMask(mask); // XOR again = undo
        }
        Mask = best;
        ApplyMask(best);
        DrawFormatBits(best);
    }

    /// <summary>ISO 18004 section 8.8.2 penalty rules - only steers the mask choice; any mask decodes correctly.</summary>
    private int PenaltyScore()
    {
        var penalty = 0;

        // Rule 1: runs of five or more same-coloured modules in a row or column.
        for (var line = 0; line < Size; line++)
        {
            penalty += RunPenalty(i => _modules[line, i]);
            penalty += RunPenalty(i => _modules[i, line]);
        }

        // Rule 2: 2x2 blocks of one colour.
        for (var y = 0; y < Size - 1; y++)
            for (var x = 0; x < Size - 1; x++)
            {
                var c = _modules[y, x];
                if (c == _modules[y, x + 1] && c == _modules[y + 1, x] && c == _modules[y + 1, x + 1]) penalty += 3;
            }

        // Rule 3: finder-like 1:1:3:1:1 patterns with four light modules on one side.
        bool[] patternA = { true, false, true, true, true, false, true, false, false, false, false };
        bool[] patternB = { false, false, false, false, true, false, true, true, true, false, true };
        for (var line = 0; line < Size; line++)
            for (var start = 0; start + 11 <= Size; start++)
            {
                if (Matches(patternA, i => _modules[line, start + i]) || Matches(patternB, i => _modules[line, start + i])) penalty += 40;
                if (Matches(patternA, i => _modules[start + i, line]) || Matches(patternB, i => _modules[start + i, line])) penalty += 40;
            }

        // Rule 4: overall dark/light balance.
        var dark = 0;
        foreach (var m in _modules) if (m) dark++;
        var total = Size * Size;
        var k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        penalty += Math.Max(0, k) * 10;
        return penalty;
    }

    private int RunPenalty(Func<int, bool> at)
    {
        var penalty = 0;
        var runColor = at(0);
        var runLength = 1;
        for (var i = 1; i < Size; i++)
        {
            if (at(i) == runColor)
            {
                runLength++;
                if (runLength == 5) penalty += 3;
                else if (runLength > 5) penalty++;
            }
            else
            {
                runColor = at(i);
                runLength = 1;
            }
        }
        return penalty;
    }

    private static bool Matches(bool[] pattern, Func<int, bool> at)
    {
        for (var i = 0; i < pattern.Length; i++)
            if (at(i) != pattern[i]) return false;
        return true;
    }

    // ---- Rendering ----

    /// <summary>
    /// A self-contained SVG: one path for every dark module, a white quiet zone, crisp edges. Always dark-on-white whatever the
    /// site theme, because that is what phone cameras decode most reliably.
    /// </summary>
    public string ToSvg(int border = 4, string? title = null)
    {
        var dim = Size + border * 2;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {dim} {dim}\" shape-rendering=\"crispEdges\" role=\"img\"");
        if (title is not null) sb.Append(" aria-label=\"").Append(System.Net.WebUtility.HtmlEncode(title)).Append('"');
        sb.Append('>');
        sb.Append(CultureInfo.InvariantCulture, $"<rect width=\"{dim}\" height=\"{dim}\" fill=\"#ffffff\"/>");
        sb.Append("<path fill=\"#000000\" d=\"");
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
                if (_modules[y, x]) sb.Append(CultureInfo.InvariantCulture, $"M{x + border},{y + border}h1v1h-1z");
        sb.Append("\"/></svg>");
        return sb.ToString();
    }
}
