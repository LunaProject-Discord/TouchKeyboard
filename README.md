# タッチキーボード（自作）

Windows タブレットで ATOK を使いながら、ハードウェアキーボード相当のフルレイアウトをタッチ入力で使うためのソフトウェアキーボード。

## なぜ作るのか

Windows 11 のタッチキーボードには「クラシック」をはじめとする複数のレイアウトがあるが、これらは Microsoft IME がアクティブなときにしか選択できない。ATOK を使っていると既定レイアウトに固定される。

これは Microsoft IME が特別扱いされているためで、**サードパーティの IME 側からは介入できない**ことを実機で確認済み。TSF が用意している `ITfFnGetPreferredTouchKeyboardLayout` は Windows 11 では参照されておらず、自作のテキストサービスで `TKBLT_CLASSIC` を宣言しても、逆に MS-IME と同じくインターフェイス自体を持たない状態にしても、レイアウトも選択肢も変化しなかった。**利用者側の設定でも回避できない。** 詳細は [docs/technical-notes.md](docs/technical-notes.md) を参照。

一方で ATOK の入力ミス補正は手放せない。タッチ入力は打鍵ミスが出やすく、大画面タブレットではこの補正の価値が最も大きい。

したがって「ATOK を維持したまま、クラシック相当のフルレイアウトをタッチで使う」には、自作する以外の手段がない。

## ドキュメント

| ファイル | 内容 |
|---|---|
| [docs/requirements.md](docs/requirements.md) | 要件定義。目的・スコープ・機能要件・非機能要件 |
| [docs/architecture.md](docs/architecture.md) | プロジェクト構成、モジュール分割、レイアウト定義フォーマット |
| [docs/development-plan.md](docs/development-plan.md) | 開発フェーズと受け入れ基準 |
| [docs/technical-notes.md](docs/technical-notes.md) | Win32 / TSF 周りの技術メモと既知の落とし穴 |
| [docs/open-questions.md](docs/open-questions.md) | 着手前に決めるべき未決事項 |
| [CLAUDE.md](CLAUDE.md) | Claude Code 向けの作業ルール |

## 技術スタック

- C# / WinUI 3（Windows App SDK）/ .NET 10
- ARM64、アンパッケージ、自己完結配置

当初は WPF で Phase 3 まで実装した。Acrylic が `WS_EX_NOACTIVATE` のウィンドウで効かず、非公開 API に頼らざるを得なかったため移行している。経緯は [docs/requirements.md](docs/requirements.md) 8 章、実機での検証結果は [docs/technical-notes.md](docs/technical-notes.md) を参照。

## 状態

Phase 3 まで完了。

| フェーズ | 内容 | 状態 |
|---|---|---|
| 0 | P/Invoke ラッパー（SendInput / ウィンドウスタイル / AppBar） | 完了 |
| 1 | 最小の入力。**ATOK の入力ミス補正が働くことを実証** | 完了 |
| 1.5 | 日本語配列固有キーのスキャンコード確定 | 完了 |
| 2 | JIS フルレイアウト、修飾キーのラッチ・ロック、Fn 段、長押しリピート | 完了 |
| 3 | AppBar によるドッキング、高さ変更、設定の永続化、Per-Monitor DPI、タスクトレイ常駐 | 完了（モニタ間移動のみ未検証） |
| 4 | UI Automation による自動表示 | 完了 |

Phase 3 の完了後、WinUI 3 へ移行したうえで外観と操作性を実機に合わせて調整している。

- 7 段構成（半分の高さのファンクション段、逆 T 字の矢印クラスタ）
- L 字の Enter（上下 2 段を 1 つのキーとして結合）
- 修飾キーの押さえ続け（複数指での Shift + 文字）
- ドッキング中はヘッダーを畳み、トレイから操作する

### 既知の未検証項目

- モニタ間の移動（外部ディスプレイが無いため）
- トレイの「Windows 起動時に開始」

### ビルドと実行

`uiAccess="true"` を指定しているため、**ビルド結果を直接実行できない**。署名して `Program Files` 配下へ配置する必要がある。

```
dotnet build TouchKeyboard.slnx
```

続けて、管理者として開いた PowerShell で配置する。証明書の作成・登録、署名、配置、起動までを行う。

```
powershell -ExecutionPolicy Bypass -File tools\install-dev.ps1
```

最終版ではこの処理をインストーラーに移す。

事前に OS 標準タッチキーボードの自動表示を止めておくこと（[docs/requirements.md](docs/requirements.md) 9 章）。
