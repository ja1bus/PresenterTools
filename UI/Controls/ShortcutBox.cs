using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PresenterTools.Core.Input;
using PresenterTools.Core.Localization;

namespace PresenterTools.UI.Controls;

/// <summary>
/// ショートカットの入力欄。フォーカスしてキーを押すと、その組み合わせ（"Ctrl+Shift+F9" 形式）が入る。
/// Backspace / Delete を単独で押すと未割当に戻す。
/// フォーカス中は <see cref="CaptureStarted"/> / <see cref="CaptureEnded"/> で既存のグローバルショートカットを
/// 一時解除してもらう（SPEC §32 D-17）。
/// </summary>
public sealed class ShortcutBox : TextBox
{
    public static readonly DependencyProperty ShortcutProperty = DependencyProperty.Register(
        nameof(Shortcut), typeof(string), typeof(ShortcutBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnShortcutChanged));

    public ShortcutBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        InputMethod.SetIsInputMethodEnabled(this, false);
        ToolTip = Strings.ShortcutBoxToolTip;
        MinWidth = 150;
        VerticalContentAlignment = VerticalAlignment.Center;
        Text = Strings.ShortcutBoxEmpty;
    }

    public event EventHandler? CaptureStarted;
    public event EventHandler? CaptureEnded;

    public string Shortcut
    {
        get => (string)GetValue(ShortcutProperty);
        set => SetValue(ShortcutProperty, value);
    }

    private static void OnShortcutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (ShortcutBox)d;
        var s = e.NewValue as string;
        // 保存用の表記（"Ctrl+Shift+F9"）はそのまま出し、未割当だけ表示言語の文字にする。
        box.Text = string.IsNullOrEmpty(s) ? Strings.ShortcutBoxEmpty : s;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        CaptureStarted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        CaptureEnded?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };

        // Tab は フォーカス移動に使えるよう通常どおり処理する（修飾なしの Tab はショートカットにできないため）。
        if (key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift) return;
        e.Handled = true;

        var modifiers = ToModifiers(Keyboard.Modifiers);
        if (modifiers == KeyModifiers.None && key is Key.Back or Key.Delete)
        {
            Shortcut = "";
            return;
        }

        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0 || VirtualKeys.IsModifier(vk)) return; // 修飾キーだけの間は確定しない
        Shortcut = new Shortcut(modifiers, vk).ToString();
    }

    private static KeyModifiers ToModifiers(ModifierKeys m)
    {
        var r = KeyModifiers.None;
        if (m.HasFlag(ModifierKeys.Control)) r |= KeyModifiers.Control;
        if (m.HasFlag(ModifierKeys.Alt)) r |= KeyModifiers.Alt;
        if (m.HasFlag(ModifierKeys.Shift)) r |= KeyModifiers.Shift;
        if (m.HasFlag(ModifierKeys.Windows)) r |= KeyModifiers.Win;
        return r;
    }
}
