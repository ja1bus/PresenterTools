# PresenterTools

[English](README.en.md) | 日本語

PresenterTools は、Teams の画面共有・会議室のディスプレイへの投影・IT 研修などで、マウス操作・キー入力・画面上の注目箇所を相手に見やすくする Windows 11 用の常駐ツールです。透明でクリックを奪わない表示を画面に重ねるため、Excel・ブラウザ・ターミナルなどをいつもどおり操作しながら使えます。

このリポジトリは、一般公開している PresenterTools のソースコードです。

## 主な機能

- Cursor Highlight: カーソルの周りにリング（または塗りつぶし円）を表示
- Click Effect: 左クリック・右クリックの位置に波紋を表示（ボタン名の札・押している間の印も表示可）
- Laser: ショートカットを押している間（または押すたびに切り替え）、カーソルの軌跡をレーザーのように描画。軌跡は後ろから消えていく
- Magnifier: カーソル周辺を 200% / 300% / 400% に拡大した小窓を、カーソルを隠さない位置に表示
- Key Display: 入力した文字とショートカット（`Ctrl+C` など）を画面に大きく表示（起動時は常に OFF）
- 各機能は個別に ON/OFF でき、Master でまとめて非表示にできる。操作パネル・タスクトレイ・グローバルショートカット・マウスのサイドボタン（任意）で操作
- 色・サイズ・時間・ショートカットは設定画面で変更でき、見た目はその場でプレビュー
- 表示言語は英語と日本語（Windows の表示言語に合わせるか、設定で固定）

**Key Display が ON の間は、パスワードを含む入力した文字がすべて画面に表示されます。** 見せたくない入力の前に OFF にしてください。入力内容や設定をインターネットへ送信することはありません（詳しくは下記のプライバシーポリシー）。

## 対応 OS と実行環境

- Windows 11（64 ビット、x64）
- Microsoft Store 版は .NET を内包しているため、追加のインストールは不要です。
- このリポジトリのソースからビルドして実行する場合は、[.NET 10 Desktop Runtime（x64）](https://dotnet.microsoft.com/download/dotnet/10.0) が必要です。

## インストール

正式な配布先は Microsoft Store です。通常はこちらから入手してください（無料）。

- Microsoft Store: https://apps.microsoft.com/detail/9PP65NZK3TZ7

この GitHub リポジトリでは実行ファイル（バイナリ）を配布していません。

## 基本的な使い方

1. 起動すると操作パネルが表示され、タスクトレイにアイコンが出ます。
2. 操作パネルのボタンで各機能を ON/OFF します。
3. ショートカットは既定ではすべて未割当です（バージョン 1.5.0 から）。使いたい操作（Laser の描画など）に、操作パネルの［設定］で割り当てます。操作パネルを隠した後は、トレイアイコンのダブルクリックか、もう一度起動すると表示されます。
4. Teams で共有するときは「画面」（モニター全体）を選んでください。ウィンドウ単位の共有では PresenterTools の表示は相手に映りません。
5. 操作パネルの × は隠すだけです。終了はトレイのメニューまたは操作パネルの［終了］です。

詳しい使い方は取扱説明書をご覧ください。

- 取扱説明書: https://www.ja1bus.com/presentertools/manual/

## ビルド方法

### 必要な環境

- Windows 11 x64
- [.NET SDK 10.0](https://dotnet.microsoft.com/download/dotnet/10.0)（確認済み: 10.0.402）
- `dotnet` CLI だけでビルドできます（Visual Studio は不要）。
- 本体は NuGet パッケージに依存しません。テストだけが NuGet の `Microsoft.NET.Test.Sdk`・`xunit`・`xunit.runner.visualstudio` を使います（初回のビルド時に nuget.org から取得します）。

### コマンド

リポジトリのルートで実行します。

```powershell
dotnet build PresenterTools.csproj
dotnet test Tests/PresenterTools.Tests.csproj
dotnet run --project PresenterTools.csproj
```

`dotnet run` で起動すると操作パネルが表示され、タスクトレイに常駐します。

配布用のフォルダ一式（Framework-dependent、win-x64）を作るには次を実行します。出力先は `publish\FrameworkDependent\` で、実行には .NET 10 Desktop Runtime（x64）が必要です。

```powershell
dotnet publish PresenterTools.csproj -c Release -p:PublishProfile=FrameworkDependent
```

出力には `LICENSE` と `THIRD-PARTY-NOTICES.md` も入ります（v1.4.0〜。アプリの設定画面の「MIT License」「第三者のソフトウェア」から表示します）。

### 構成

| パス | 内容 |
|---|---|
| `PresenterTools.csproj` | 本体（WPF、`net10.0-windows`）。タスクトレイだけ WinForms の `NotifyIcon` を使用 |
| `AppHost.cs` | 入力・状態・表示のつなぎ込み、設定の保存と適用 |
| `Core/` | 画面・Win32 に依存しないロジック（設定・ショートカット・状態・キー表示・配置計算）と、画面の文字列（英語 `Strings.resx`・日本語 `Strings.ja.resx`） |
| `Input/` | グローバルショートカット（`RegisterHotKey`）と低レベルのマウス・キーボードフック |
| `Overlay/` | クリックを透過する表示用ウィンドウ（リング・波紋・レーザー・拡大鏡・キー表示） |
| `UI/`・`Services/`・`Interop/` | 操作パネル・設定画面、モニター・トレイ・ログなどの処理、Win32 API の宣言 |
| `Tests/` | `Core` の単体テスト（xUnit） |
| `tools/icon-source.png`・`Resources/icon.ico` | アプリアイコン（`pwsh -File tools/make-icon.ps1` で `icon-source.png` から `icon.ico` を作成） |

Microsoft Store 版のパッケージ（MSIX）を作る仕組みは、このリポジトリに含めていません。`PresenterTools.csproj` にある `PresenterToolsMsix` の条件は Store 版のためのものです。

設定は `%LocalAppData%\PresenterTools\settings.json`、エラーの記録は同じフォルダの `error.log` に保存します。

ソースコード中のコメントは主に日本語です。コメント中の「SPEC §N」（「D-N」などの番号を含む）は、このリポジトリには含めていない開発用の仕様書の節番号です。

## ライセンス

[MIT License](LICENSE) で公開しています。

Copyright (c) 2026 ja1bus（© ja1bus）

第三者のソフトウェアについては [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) を参照してください。

## 無保証

本ソフトウェアは「現状のまま」提供され、明示・黙示を問わず、いかなる保証もありません。本ソフトウェアの使用によって生じたいかなる損害についても、作者は責任を負いません（詳細は [LICENSE](LICENSE)）。

## 関連リンク

- Web ページ: https://www.ja1bus.com/presentertools/
- 取扱説明書: https://www.ja1bus.com/presentertools/manual/
- 更新履歴: https://www.ja1bus.com/presentertools/changelog/
- プライバシーポリシー: https://www.ja1bus.com/presentertools/privacy/
- Microsoft Store: https://apps.microsoft.com/detail/9PP65NZK3TZ7
- お問い合わせ: contact@ja1bus.com
