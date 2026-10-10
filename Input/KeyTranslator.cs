using PresenterTools.Core.Input;
using static PresenterTools.Interop.NativeMethods;

namespace PresenterTools.Input;

/// <summary>
/// 低レベルフックのキーイベントを <see cref="KeyInput"/> に変換する（SPEC §15.3）。
/// フック用スレッドから呼ばれる。ToUnicodeEx は wFlags=4 で呼び、入力中アプリのデッドキー状態を壊さない。
/// </summary>
internal sealed class KeyTranslator
{
    private const uint ToUnicodeDoNotChangeState = 0x4;
    private readonly byte[] _keyState = new byte[256];
    private readonly char[] _buffer = new char[8];

    public KeyInput Translate(int vk, int scanCode, bool isDown, IntPtr foreground)
    {
        var modifiers = CurrentModifiers();
        if (!isDown) return new KeyInput(vk, false, modifiers);

        // SendInput の KEYEVENTF_UNICODE（リモート操作ツール・一部の IME）は VK_PACKET で文字が直接届く。
        if (vk == VK_PACKET)
            return new KeyInput(vk, true, modifiers, ((char)scanCode).ToString());

        var layout = GetKeyboardLayout(GetWindowThreadProcessId(foreground, out _));
        var mapped = MapVirtualKeyEx((uint)vk, MAPVK_VK_TO_CHAR, layout) & 0x7FFF;
        char? baseChar = mapped != 0 ? (char)mapped : null;

        string? text = null;
        var hasCommand = (modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win)) != 0;
        if (!hasCommand && !VirtualKeys.IsModifier(vk))
        {
            Array.Clear(_keyState);
            if (modifiers.HasFlag(KeyModifiers.Shift)) _keyState[VirtualKeys.Shift] = 0x80;
            if ((GetKeyState(VirtualKeys.Capital) & 1) != 0) _keyState[VirtualKeys.Capital] = 0x01;
            var n = ToUnicodeEx((uint)vk, (uint)scanCode, _keyState, _buffer, _buffer.Length, ToUnicodeDoNotChangeState, layout);
            if (n > 0) text = new string(_buffer, 0, n);
        }
        return new KeyInput(vk, true, modifiers, text, baseChar);
    }

    /// <summary>このイベント以前から押されている修飾キー（GetAsyncKeyState）。</summary>
    private static KeyModifiers CurrentModifiers()
    {
        var m = KeyModifiers.None;
        if (IsKeyDown(VirtualKeys.Control)) m |= KeyModifiers.Control;
        if (IsKeyDown(VirtualKeys.Menu)) m |= KeyModifiers.Alt;
        if (IsKeyDown(VirtualKeys.Shift)) m |= KeyModifiers.Shift;
        if (IsKeyDown(VirtualKeys.LWin) || IsKeyDown(VirtualKeys.RWin)) m |= KeyModifiers.Win;
        return m;
    }
}
