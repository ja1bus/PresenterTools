using System.Windows;
using PresenterTools.Core.Localization;

namespace PresenterTools.UI;

/// <summary>
/// 設定のインポートで、検証済みのファイルについて何を読み込むかを選ぶ（V1.5.0。SPEC §10.2）。
/// ショートカット・マウスボタンの割り当ては既定で読み込まない（PC ごとに空きキー・マウスが違うため）。
/// </summary>
public partial class ImportOptionsWindow : Window
{
    public ImportOptionsWindow(string fileName, string? sourceVersion, string? exportedAt)
    {
        InitializeComponent();
        FileText.Text = fileName;
        SourceText.Text = string.Format(Strings.ImportSourceFormat,
            sourceVersion ?? Strings.SetVersionUnknown, exportedAt ?? Strings.SetVersionUnknown);
    }

    public bool IncludeHotkeys => HotkeysBox.IsChecked == true;
    public bool IncludeMouseButtons => MouseButtonsBox.IsChecked == true;

    private void OnImport(object sender, RoutedEventArgs e) => DialogResult = true;
}
