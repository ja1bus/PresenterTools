using System.Buffers.Binary;

namespace PresenterTools.Core.ScreenPen;

/// <summary>
/// 画素からカーソル（.cur）のバイト列を作る（docs/archive/design-1.8.0.md P4。WPF に依存しない）。
/// ICONDIR（種別 2）＋項目 1 つ＋32 bpp の BITMAPINFOHEADER（高さは 2 倍）＋下から上の XOR 画素＋全 0 の AND マスク。
/// </summary>
public static class CursorFile
{
    public const int MinSize = 1;
    public const int MaxSize = 256;
    private const int HeaderSize = 6;
    private const int EntrySize = 16;
    private const int InfoHeaderSize = 40;

    /// <summary>AND マスクの 1 行のバイト数（1 bpp、4 バイト境界）。</summary>
    public static int MaskStride(int width) => (width + 31) / 32 * 4;

    /// <param name="bgra">上から下・左から右の BGRA（1 画素 4 バイト、乗算済みでないアルファ）。</param>
    public static byte[] Build(int width, int height, int hotX, int hotY, ReadOnlySpan<byte> bgra)
    {
        if (width is < MinSize or > MaxSize) throw new ArgumentOutOfRangeException(nameof(width));
        if (height is < MinSize or > MaxSize) throw new ArgumentOutOfRangeException(nameof(height));
        if (hotX < 0 || hotX >= width) throw new ArgumentOutOfRangeException(nameof(hotX));
        if (hotY < 0 || hotY >= height) throw new ArgumentOutOfRangeException(nameof(hotY));
        if (bgra.Length != width * height * 4) throw new ArgumentException("Pixel data size does not match.", nameof(bgra));

        var xorSize = width * height * 4;
        var andSize = MaskStride(width) * height;
        var dataSize = InfoHeaderSize + xorSize + andSize;
        var offset = HeaderSize + EntrySize;
        var bytes = new byte[offset + dataSize];
        var s = bytes.AsSpan();

        // ICONDIR
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], 2); // 種別: カーソル
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], 1); // 件数
        // ICONDIRENTRY（256 は 0 で表す）
        s[6] = (byte)(width == 256 ? 0 : width);
        s[7] = (byte)(height == 256 ? 0 : height);
        BinaryPrimitives.WriteUInt16LittleEndian(s[10..], (ushort)hotX);
        BinaryPrimitives.WriteUInt16LittleEndian(s[12..], (ushort)hotY);
        BinaryPrimitives.WriteUInt32LittleEndian(s[14..], (uint)dataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(s[18..], (uint)offset);

        // BITMAPINFOHEADER
        var h = s[offset..];
        BinaryPrimitives.WriteUInt32LittleEndian(h, InfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], height * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(h[12..], 1);  // planes
        BinaryPrimitives.WriteUInt16LittleEndian(h[14..], 32); // bpp
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], (uint)(xorSize + andSize));

        // XOR 画素（下の行から）
        var px = h[InfoHeaderSize..];
        var stride = width * 4;
        for (var y = 0; y < height; y++)
            bgra.Slice((height - 1 - y) * stride, stride).CopyTo(px[(y * stride)..]);
        // AND マスクは全 0（アルファで透過する）。配列の初期値のまま。
        return bytes;
    }
}
