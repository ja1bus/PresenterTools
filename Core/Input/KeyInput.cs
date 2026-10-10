namespace PresenterTools.Core.Input;

/// <summary>
/// キーボードフックから受け取った 1 回分のキーイベント（Win32 から切り離した形）。
/// </summary>
/// <param name="VirtualKey">仮想キーコード。</param>
/// <param name="IsKeyDown">押下なら true、解放なら false。自動リピートは押下が続けて届く。</param>
/// <param name="Modifiers">このイベント時点で押されている修飾キー（このキー自身は含まない）。</param>
/// <param name="Text">現在の配列・Shift・CapsLock で入力される文字。Ctrl/Alt/Win 押下中や文字の無いキーは null。</param>
/// <param name="BaseChar">無修飾で押したときの文字（記号キーの表示ラベル用）。無ければ null。</param>
public readonly record struct KeyInput(int VirtualKey, bool IsKeyDown, KeyModifiers Modifiers, string? Text = null, char? BaseChar = null);
