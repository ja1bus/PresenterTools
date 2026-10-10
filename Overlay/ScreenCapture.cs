using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PresenterTools.Core.Geometry;
using static PresenterTools.Interop.NativeMethods;

namespace PresenterTools.Overlay;

/// <summary>
/// Magnifier 用の画面取得（SPEC §16.3）。GDI の BitBlt（SRCCOPY、CAPTUREBLT なし）で取得範囲だけを
/// DIB セクションへ複写し、WriteableBitmap へ転送する。CAPTUREBLT を付けないため、
/// レイヤードウィンドウ（拡大窓自身・オーバーレイ）は写らない。
/// </summary>
public sealed class ScreenCapture : IDisposable
{
    private IntPtr _memoryDc;
    private IntPtr _dib;
    private IntPtr _oldBitmap;
    private IntPtr _bits;
    private int _width;
    private int _height;

    public WriteableBitmap? Bitmap { get; private set; }

    /// <summary>取得して Bitmap を更新する。失敗したら false（直前の画像はそのまま）。</summary>
    public bool Capture(RectInt source)
    {
        if (source.IsEmpty) return false;
        EnsureBuffers(source.Width, source.Height);
        if (_memoryDc == IntPtr.Zero || Bitmap is null) return false;

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return false;
        try
        {
            if (!BitBlt(_memoryDc, 0, 0, _width, _height, screenDc, source.Left, source.Top, SRCCOPY))
                return false; // 画面ロック中・セキュアデスクトップなど
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        var stride = _width * 4;
        Bitmap.WritePixels(new Int32Rect(0, 0, _width, _height), _bits, stride * _height, stride);
        return true;
    }

    public void Dispose()
    {
        ReleaseBuffers();
        Bitmap = null;
    }

    private void EnsureBuffers(int width, int height)
    {
        if (width == _width && height == _height && _memoryDc != IntPtr.Zero) return;
        ReleaseBuffers();

        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // トップダウン
            biPlanes = 1,
            biBitCount = 32,
        };
        var screenDc = GetDC(IntPtr.Zero);
        try
        {
            _memoryDc = CreateCompatibleDC(screenDc);
            _dib = CreateDIBSection(screenDc, ref header, DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
        if (_memoryDc == IntPtr.Zero || _dib == IntPtr.Zero)
        {
            ReleaseBuffers();
            return;
        }
        _oldBitmap = SelectObject(_memoryDc, _dib);
        _width = width;
        _height = height;
        Bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
    }

    private void ReleaseBuffers()
    {
        if (_memoryDc != IntPtr.Zero)
        {
            if (_oldBitmap != IntPtr.Zero) SelectObject(_memoryDc, _oldBitmap);
            DeleteDC(_memoryDc);
        }
        if (_dib != IntPtr.Zero) DeleteObject(_dib);
        _memoryDc = _dib = _oldBitmap = _bits = IntPtr.Zero;
        _width = _height = 0;
    }
}
