using System.Text;
using AiPulse.Services.Security;
using Xunit;

namespace AiPulse.Tests;

public class QrCodeTests
{
    [Theory] // ISO/IEC 18004 table 7: data codewords at level M
    [InlineData(1, 16)] [InlineData(2, 28)] [InlineData(3, 44)] [InlineData(4, 64)] [InlineData(5, 86)]
    [InlineData(6, 108)] [InlineData(7, 124)] [InlineData(8, 154)] [InlineData(9, 182)] [InlineData(10, 216)]
    [InlineData(20, 669)] [InlineData(40, 2334)]
    public void Data_capacity_matches_the_iso_table(int version, int dataCodewords) =>
        Assert.Equal(dataCodewords, QrCode.NumDataCodewords(version));

    [Theory] // ISO/IEC 18004 table 7: byte-mode capacity at level M (version 1 holds 14 bytes, version 6 holds 106...)
    [InlineData(14, 1)] [InlineData(15, 2)] [InlineData(106, 6)] [InlineData(107, 7)] [InlineData(213, 10)] [InlineData(214, 11)]
    public void The_smallest_version_that_fits_is_chosen(int length, int version) =>
        Assert.Equal(version, QrCode.EncodeBytes(new byte[length]).Version);

    [Theory] // ISO/IEC 18004 annex C, level M rows
    [InlineData(0, 0b101010000010010)] [InlineData(1, 0b101000100100101)] [InlineData(2, 0b101111001111100)] [InlineData(3, 0b101101101001011)]
    [InlineData(4, 0b100010111111001)] [InlineData(5, 0b100000011001110)] [InlineData(6, 0b100111110010111)] [InlineData(7, 0b100101010100000)]
    public void Format_information_matches_the_iso_table(int mask, int expected) => Assert.Equal(expected, QrCode.FormatBits(mask));

    [Theory] // ISO/IEC 18004 annex D
    [InlineData(7, 0x07C94)] [InlineData(8, 0x085BC)] [InlineData(9, 0x09A99)] [InlineData(10, 0x0A4D3)] [InlineData(40, 0x28C69)]
    public void Version_information_matches_the_iso_table(int version, int expected) => Assert.Equal(expected, QrCode.VersionBits(version));

    [Theory]
    [InlineData(1, new int[0])] [InlineData(2, new[] { 6, 18 })] [InlineData(7, new[] { 6, 22, 38 })] [InlineData(10, new[] { 6, 28, 50 })]
    [InlineData(32, new[] { 6, 34, 60, 86, 112, 138 })]
    public void Alignment_pattern_centres_match_the_iso_table(int version, int[] expected) =>
        Assert.Equal(expected, QrCode.AlignmentPatternPositions(version));

    [Fact]
    public void Reed_solomon_matches_the_iso_annex_i_example()
    {
        // "01234567", version 1-M: the standard's worked example.
        byte[] data = { 0x10, 0x20, 0x0C, 0x56, 0x61, 0x80, 0xEC, 0x11, 0xEC, 0x11, 0xEC, 0x11, 0xEC, 0x11, 0xEC, 0x11 };
        byte[] ecc = { 0xA5, 0x24, 0xD4, 0xC1, 0xED, 0x36, 0xC7, 0x87, 0x2C, 0x55 };
        Assert.Equal(ecc, QrCode.ReedSolomonRemainder(data, QrCode.ReedSolomonDivisor(10)));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("Hello, world! 123")]
    [InlineData("otpauth://totp/AiPulse:admin?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=AiPulse&algorithm=SHA1&digits=6&period=30")]
    [InlineData("otpauth://totp/AiPulse:a%20much%20longer%20username%20for%20testing%20purposes?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXPJBSWY3DP&issuer=AiPulse&algorithm=SHA1&digits=6&period=30")]
    public void An_independent_reader_decodes_what_the_encoder_wrote(string text)
    {
        var qr = QrCode.EncodeText(text);
        Assert.True(qr.Version <= 10, "the test reader only knows versions 1-10");
        Assert.Equal(text, Encoding.UTF8.GetString(QrReader.Decode((x, y) => qr[x, y], qr.Size)));
    }

    [Fact]
    public void Every_payload_length_up_to_version_10_round_trips()
    {
        var rng = new Random(42);
        for (var length = 1; length <= 213; length += 7)
        {
            var data = new byte[length];
            rng.NextBytes(data);
            var qr = QrCode.EncodeBytes(data);
            Assert.Equal(data, QrReader.Decode((x, y) => qr[x, y], qr.Size));
        }
    }

    [Fact]
    public void The_three_finder_patterns_are_in_the_corners()
    {
        var qr = QrCode.EncodeText("finder check");
        foreach (var (ox, oy) in new[] { (0, 0), (qr.Size - 7, 0), (0, qr.Size - 7) })
            for (var dy = 0; dy < 7; dy++)
                for (var dx = 0; dx < 7; dx++)
                {
                    var ring = Math.Max(Math.Abs(dx - 3), Math.Abs(dy - 3));
                    Assert.Equal(ring != 2, qr[ox + dx, oy + dy]);
                }
    }

    [Fact]
    public void The_svg_is_self_contained_and_scales()
    {
        var qr = QrCode.EncodeText("svg");
        var svg = qr.ToSvg(title: "QR <code>");
        Assert.StartsWith("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 29 29\"", svg);
        Assert.Contains("aria-label=\"QR &lt;code&gt;\"", svg);
        Assert.Contains("fill=\"#ffffff\"", svg);
        Assert.DoesNotContain("<script", svg);
    }

    [Fact]
    public void Too_much_data_is_refused() => Assert.Throws<ArgumentException>(() => QrCode.EncodeBytes(new byte[2400]));
}

/// <summary>
/// A deliberately separate QR reader for a perfect module grid (no image processing): its own function-pattern map, mask
/// formulas, block table (ISO table 9 for versions 1-10 at level M) and a syndrome check of every block, so an encoder
/// mistake cannot cancel itself out.
/// </summary>
internal static class QrReader
{
    private static readonly int[][] Alignment =
    {
        Array.Empty<int>(), Array.Empty<int>(), new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 }, new[] { 6, 34 },
        new[] { 6, 22, 38 }, new[] { 6, 24, 42 }, new[] { 6, 26, 46 }, new[] { 6, 28, 50 }
    };

    // (blocks, data codewords per block) groups and EC codewords per block, level M.
    private static readonly ((int Count, int Data)[] Groups, int Ec)[] Blocks =
    {
        (Array.Empty<(int, int)>(), 0),
        (new[] { (1, 16) }, 10), (new[] { (1, 28) }, 16), (new[] { (1, 44) }, 26), (new[] { (2, 32) }, 18), (new[] { (2, 43) }, 24),
        (new[] { (4, 27) }, 16), (new[] { (4, 31) }, 18), (new[] { (2, 38), (2, 39) }, 22), (new[] { (3, 36), (2, 37) }, 22),
        (new[] { (4, 43), (1, 44) }, 26)
    };

    public static byte[] Decode(Func<int, int, bool> dark, int size)
    {
        var version = (size - 17) / 4;
        Assert.InRange(version, 1, 10);

        // Format information: read the copy beside the top-right and bottom-left finders, cross-check the other copy.
        int formatA = 0, formatB = 0;
        for (var i = 0; i < 8; i++) if (dark(size - 1 - i, 8)) formatB |= 1 << i;
        for (var i = 8; i < 15; i++) if (dark(8, size - 15 + i)) formatB |= 1 << i;
        int[] colA = { 0, 1, 2, 3, 4, 5, 7, 8 };
        for (var i = 0; i < 8; i++) if (dark(8, colA[i])) formatA |= 1 << i;
        int[] rowA = { 7, 5, 4, 3, 2, 1, 0 };
        for (var i = 8; i < 15; i++) if (dark(rowA[i - 8], 8)) formatA |= 1 << i;
        Assert.Equal(formatA, formatB);

        var format = formatB ^ 0x5412;
        var data5 = format >> 10;
        Assert.Equal(0, BchRemainder(format, 0x537, 10)); // valid BCH code word
        Assert.Equal(0, data5 >> 3);                       // level M
        var mask = data5 & 7;
        Assert.True(dark(8, size - 8), "dark module missing");

        // Data modules in placement order, unmasked.
        var bits = new List<bool>();
        var upward = true;
        for (var col = size - 1; col > 0; col -= 2)
        {
            if (col == 6) col = 5;
            for (var k = 0; k < size; k++)
            {
                var row = upward ? size - 1 - k : k;
                for (var c = 0; c < 2; c++)
                {
                    var x = col - c;
                    if (IsFunction(x, row, size, version)) continue;
                    bits.Add(dark(x, row) ^ MaskApplies(mask, row, x));
                }
            }
            upward = !upward;
        }

        var codewords = new byte[bits.Count / 8];
        for (var i = 0; i < codewords.Length * 8; i++) if (bits[i]) codewords[i / 8] |= (byte)(0x80 >> (i % 8));

        // De-interleave into blocks and check each block's syndromes are all zero.
        var (groups, ec) = Blocks[version];
        var dataLens = groups.SelectMany(g => Enumerable.Repeat(g.Data, g.Count)).ToArray();
        var blocks = dataLens.Select(len => new List<byte>()).ToArray();
        var p = 0;
        for (var i = 0; i < dataLens.Max(); i++)
            for (var b = 0; b < blocks.Length; b++)
                if (i < dataLens[b]) blocks[b].Add(codewords[p++]);
        var data = blocks.SelectMany(b => b).ToArray();
        for (var i = 0; i < ec; i++)
            foreach (var block in blocks) block.Add(codewords[p++]);
        foreach (var block in blocks)
            for (var j = 0; j < ec; j++)
                Assert.Equal(0, EvaluateAt(block, Exp[j]));

        // Byte-mode segment.
        var reader = new BitReader(data);
        Assert.Equal(0b0100, reader.Read(4));
        var count = reader.Read(version <= 9 ? 8 : 16);
        var result = new byte[count];
        for (var i = 0; i < count; i++) result[i] = (byte)reader.Read(8);
        return result;
    }

    private static bool IsFunction(int x, int y, int size, int version)
    {
        if (x < 9 && y < 9) return true;                 // top-left finder, separator, format
        if (x >= size - 8 && y < 9) return true;         // top-right
        if (x < 9 && y >= size - 8) return true;         // bottom-left (+ dark module)
        if (x == 6 || y == 6) return true;               // timing
        if (version >= 7 && ((x >= size - 11 && x < size - 8 && y < 6) || (y >= size - 11 && y < size - 8 && x < 6))) return true;
        var centres = Alignment[version];
        foreach (var cy in centres)
            foreach (var cx in centres)
            {
                var overlapsFinder = (cx == 6 && cy == 6) || (cx == 6 && cy == centres[^1]) || (cx == centres[^1] && cy == 6);
                if (!overlapsFinder && Math.Abs(x - cx) <= 2 && Math.Abs(y - cy) <= 2) return true;
            }
        return false;
    }

    // ISO/IEC 18004 table 10, written with i = row, j = column.
    private static bool MaskApplies(int mask, int i, int j) => mask switch
    {
        0 => (i + j) % 2 == 0,
        1 => i % 2 == 0,
        2 => j % 3 == 0,
        3 => (i + j) % 3 == 0,
        4 => (i / 2 + j / 3) % 2 == 0,
        5 => i * j % 2 + i * j % 3 == 0,
        6 => (i * j % 2 + i * j % 3) % 2 == 0,
        _ => ((i + j) % 2 + i * j % 3) % 2 == 0,
    };

    private static int BchRemainder(int value, int generator, int degree)
    {
        for (var bit = 14; bit >= degree; bit--)
            if (((value >> bit) & 1) != 0) value ^= generator << (bit - degree);
        return value;
    }

    private static readonly int[] Exp = BuildExp();
    private static readonly int[] Log = BuildLog();

    private static int[] BuildExp()
    {
        var exp = new int[512];
        var x = 1;
        for (var i = 0; i < 255; i++) { exp[i] = x; x <<= 1; if (x >= 256) x ^= 0x11D; }
        for (var i = 255; i < 512; i++) exp[i] = exp[i - 255];
        return exp;
    }

    private static int[] BuildLog()
    {
        var log = new int[256];
        for (var i = 0; i < 255; i++) log[Exp[i]] = i;
        return log;
    }

    private static int Mul(int a, int b) => a == 0 || b == 0 ? 0 : Exp[Log[a] + Log[b]];

    private static int EvaluateAt(List<byte> poly, int x)
    {
        var y = 0;
        foreach (var c in poly) y = Mul(y, x) ^ c; // Horner, highest degree first
        return y;
    }

    private sealed class BitReader(byte[] data)
    {
        private int _pos;
        public int Read(int n)
        {
            var v = 0;
            for (var i = 0; i < n; i++, _pos++) v = (v << 1) | ((data[_pos / 8] >> (7 - _pos % 8)) & 1);
            return v;
        }
    }
}
