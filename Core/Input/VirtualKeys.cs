namespace PresenterTools.Core.Input;

/// <summary>修飾キー。値は Win32 RegisterHotKey の MOD_* と同じ。</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>使用する Win32 仮想キーコード（VK_*）。</summary>
public static class VirtualKeys
{
    public const int Back = 0x08;
    public const int Tab = 0x09;
    public const int Clear = 0x0C;
    public const int Return = 0x0D;
    public const int Shift = 0x10;
    public const int Control = 0x11;
    public const int Menu = 0x12; // Alt
    public const int Pause = 0x13;
    public const int Capital = 0x14;
    public const int Kana = 0x15;
    public const int Kanji = 0x19;
    public const int Escape = 0x1B;
    public const int Convert = 0x1C;
    public const int NonConvert = 0x1D;
    public const int Space = 0x20;
    public const int Prior = 0x21; // PageUp
    public const int Next = 0x22;  // PageDown
    public const int End = 0x23;
    public const int Home = 0x24;
    public const int Left = 0x25;
    public const int Up = 0x26;
    public const int Right = 0x27;
    public const int Down = 0x28;
    public const int Snapshot = 0x2C; // PrintScreen
    public const int Insert = 0x2D;
    public const int Delete = 0x2E;
    public const int D0 = 0x30;
    public const int D9 = 0x39;
    public const int A = 0x41;
    public const int Z = 0x5A;
    public const int LWin = 0x5B;
    public const int RWin = 0x5C;
    public const int Apps = 0x5D;
    public const int NumPad0 = 0x60;
    public const int NumPad9 = 0x69;
    public const int Multiply = 0x6A;
    public const int Add = 0x6B;
    public const int Subtract = 0x6D;
    public const int Decimal = 0x6E;
    public const int Divide = 0x6F;
    public const int F1 = 0x70;
    public const int F12 = 0x7B;
    public const int F13 = 0x7C;
    public const int F24 = 0x87;
    public const int NumLock = 0x90;
    public const int Scroll = 0x91;
    public const int LShift = 0xA0;
    public const int RShift = 0xA1;
    public const int LControl = 0xA2;
    public const int RControl = 0xA3;
    public const int LMenu = 0xA4;
    public const int RMenu = 0xA5;
    public const int OemAttn = 0xF0;   // JP: 英数
    public const int OemCopy = 0xF2;   // JP: カタカナ/ひらがな
    public const int OemAuto = 0xF3;   // JP: 半角/全角
    public const int OemEnlw = 0xF4;   // JP: 半角/全角

    public static bool IsModifier(int vk) => vk is Shift or Control or Menu or LShift or RShift
        or LControl or RControl or LMenu or RMenu or LWin or RWin;

    public static bool IsFunctionKey(int vk) => vk is >= F1 and <= F24;

    /// <summary>日本語入力の切替系キー。</summary>
    public static bool IsImeKey(int vk) => vk is Kana or Kanji or Convert or NonConvert
        or OemAttn or OemCopy or OemAuto or OemEnlw;

    /// <summary>
    /// Key Display で「特殊キー」として名前で表示するキー。
    /// 修飾キーなしで押しても Chord として扱う（Space・文字キー・テンキーは含まない）。
    /// </summary>
    public static bool IsNamedSpecialKey(int vk) =>
        vk is Back or Tab or Return or Escape or Prior or Next or End or Home
            or Left or Up or Right or Down or Snapshot or Insert or Delete or Pause or Apps
        || IsFunctionKey(vk)
        || IsImeKey(vk);
}
