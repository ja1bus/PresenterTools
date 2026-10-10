using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using PresenterTools.Core.Localization;
using PresenterTools.Services;

namespace PresenterTools;

/// <summary>起動・表示言語の決定・多重起動防止・未処理例外の扱い（SPEC §20・§34）。</summary>
public partial class App : Application
{
    /// <summary>表示言語の変更で再起動したときの引数。前のプロセスが終わるまで多重起動防止の解放を待つ（SPEC §34）。</summary>
    public const string RestartArgument = "--restart";

    /// <summary>再起動のとき、前のプロセスの終了を待つ時間（設定の保存・フック等の解放を含む）。</summary>
    private static readonly TimeSpan RestartWait = TimeSpan.FromSeconds(15);

    private SingleInstance? _instance;
    private AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 言語を適用する前の Windows の表示言語（「Windows の表示言語に合わせる」の判定に使う）。
        var windowsUiCulture = CultureInfo.CurrentUICulture;
        // 設定を読むまでの間（起動途中のエラー表示など）は Windows の表示言語に合わせる。設定の言語は AppHost.Start で適用する。
        UiLanguage.Apply(UiLanguage.Resolve(UiLanguage.System, windowsUiCulture));

        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ErrorLog.Write("Unhandled exception (terminating)", args.ExceptionObject as Exception);

        var restarting = e.Args.Contains(RestartArgument, StringComparer.OrdinalIgnoreCase);
        _instance = SingleInstance.TryAcquire(restarting ? RestartWait : TimeSpan.Zero);
        if (_instance is null)
        {
            // 既に起動済み: 既存の操作パネルを前面に出してもらい、こちらは終了する。
            Shutdown(0);
            return;
        }

        _host = new AppHost(Dispatcher, windowsUiCulture);
        _instance.ListenForActivation(() => Dispatcher.BeginInvoke(() => _host?.ShowPanel()));
        _host.Start();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        _host?.SaveSettings();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write("Unhandled exception", e.Exception);
        e.Handled = true;
        var started = _host?.Panel is not null;
        MessageBox.Show(
            string.Format(Strings.AppUnexpectedErrorFormat, e.Exception.GetType().Name, e.Exception.Message, ErrorLog.FilePath) +
            (started ? "" : "\n\n" + Strings.AppWillExit),
            "PresenterTools", MessageBoxButton.OK, MessageBoxImage.Error);
        if (!started)
        {
            // 起動途中の失敗ではプロセスを残さない（local/C# の既知の落とし穴）。
            _host?.Dispose();
            Shutdown(1);
        }
    }
}
