# 未決事項

決まったものはこのファイルから消し、該当ドキュメントに反映する。

## 未決

### 1. アイコン

タスクトレイアイコンは現在、実行時に描画した仮のもの（`Views/TrayIcon.cs`）。正式なアイコンを用意するかどうか。

### 2. モニタ間移動の扱い

外部ディスプレイ接続時の挙動が未検証（`development-plan.md` Phase 3 参照）。実機で確認したうえで、ドッキング先モニタの選択 UI が現状のトレイメニューで足りるかを判断する。

---

## 決定済み

| 項目 | 決定 | 反映先 |
|---|---|---|
| レイアウト定義のフォーマット | JSON。ユニット倍率。実行ファイル横の `layouts/` へビルド時コピー（埋め込まない） | `architecture.md` |
| 日本語配列固有キーのスキャンコード | 実機確認済み。`MapVirtualKeyEx` による確定値 | `technical-notes.md` |
| 設定の保存先 | `%APPDATA%\TouchKeyboard\settings.json`。高さは DIP で保持 | `Settings/AppSettings.cs` |
| キーリピートの既定値 | OS 設定（`SPI_GETKEYBOARDDELAY` / `SPI_GETKEYBOARDSPEED`）を基準に、開始遅延は最低 600ms。タッチは誤リピートしやすいため | `Input/KeyRepeat.cs` |
| 透過・不透明度 | Acrylic を採用。WinUI 3 の `DesktopAcrylicController` で `IsInputActive` を固定する。DWM のシステムバックドロップは `WS_EX_NOACTIVATE` と両立しない | `Views/AcrylicBackdrop.cs` |
| uiAccess への対応 | 対応する。シェルより上の Z 順に入れるため必須だった。副次的に昇格アプリへの入力制限も外れる | `app.manifest` |
| ドッキング以外の配置モード | フローティングは初版では見送り | — |
| タスクバーを覆うか | 選択式（トレイメニュー、既定オン）。AppBar の確保は変えず、ウィンドウだけ画面下端まで伸ばす | `Views/DockManager.cs` |
| キー配列の切り替え UI | 初版は JIS フルレイアウトのみのため不要 | — |
| Fn キーの扱い | スキャンコードを送出せず、数字段を `Esc` / `F1`〜`F12` / `Del` に切り替えるアプリ内部の機能 | `layouts/jis-full.json` |
| 自動表示時の AppBar | 隠すたびに `ABM_REMOVE`、表示で `ABM_NEW`。フォーカス移動の連発に備えデバウンスする（Phase 4） | `Views/DockManager.cs` |
| .NET のバージョン | `net10.0-windows`。選定理由（Per-Monitor DPI V2）は .NET 10 でも満たされ、開発機に .NET 8 ランタイムが無く、.NET 10 は LTS | `TouchKeyboard.csproj` |
| インストーラー | 自己署名証明書のまま、個人利用の範囲に留める。WiX Toolset で MSI を作る。`installer/build-msi.ps1` が publish・署名・証明書書き出し・MSI ビルドまでを行い、`installer/TouchKeyboard.wxs` が配置・証明書の信頼登録（インストール時のカスタムアクション）・スタートメニュー登録・「アプリと機能」への登録を定義する。アンインストールは Windows の標準機能（設定 > アプリ）に任せ、専用スクリプトは持たない | `installer/TouchKeyboard.wxs`, `installer/build-msi.ps1` |
