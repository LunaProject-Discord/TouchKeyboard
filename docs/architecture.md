# 構成方針

## プロジェクト構成

```
src/
  TouchKeyboard/
    App.xaml / App.xaml.cs   起動、ウィンドウのサブクラス、終了処理
    SingleInstance.cs        二重起動の抑止（名前付きミューテックス）
    app.manifest             Per-Monitor DPI Awareness V2
    Interop/                 ネイティブ宣言とラッパー（呼び出し側に散らさない）
      NativeMethods.cs         P/Invoke 宣言のみ
      KeySender.cs             SendInput ラッパー
      WindowStyles.cs          WS_EX_NOACTIVATE の適用
      WindowSubclass.cs        SetWindowSubclass によるメッセージ購読
      AppBar.cs                SHAppBarMessage ラッパー
      MonitorInfo.cs           モニタの列挙と座標変換
      ShellNotify.cs           Shell_NotifyIcon とポップアップメニュー
    Input/
      KeyDispatcher.cs         押されたキーを送出するスキャンコード列へ変換
      ModifierState.cs         修飾キーのラッチ・ロック・押さえ続け
      KeyRepeat.cs             長押しリピートの間隔
    Layout/
      KeyDefinition.cs         キー 1 つ分の定義
      LayoutDefinition.cs      レイアウト全体と整合性の検証
      LayoutLoader.cs          外部ファイルの読み込み
    Settings/
      AppSettings.cs           設定の読み書きと自動起動
    Views/
      KeyboardWindow.xaml(.cs) ウィンドウとキーの配置
      KeyButton.cs             キー 1 つ分の表示
      Theme.cs                 配色（ライト / ダーク）
      DockManager.cs           AppBar による画面領域の確保
      AcrylicBackdrop.cs       非アクティブでも効く Acrylic
      TrayIcon.cs              通知領域アイコンとメニュー
layouts/
  jis-full.json              既定レイアウト
docs/
```

`Automation/`（UI Automation によるフォーカス監視）は Phase 4 で追加する。

## レイヤの責務

**Interop** — ネイティブ API の呼び出しのみを担う。ここより上には `DllImport` を書かない。単体で動作確認しやすい形にしておく。

**Input** — 「どのキーが押されたか」を「どのスキャンコード列を送るか」に変換する。修飾キーの状態管理はここに閉じる。UI を知らない。

**Layout** — レイアウト定義の読み込みと保持。キーの物理配置とスキャンコードの対応を持つ。整合性の検証もここで行う。

**Automation** — フォーカス監視。入力欄かどうかの判定ロジックをここに閉じる。判定は取りこぼしが前提なので、差し替えやすくしておく。

**Views** — 描画とタップの受付のみ。入力ロジックを持ち込まない。

## レイアウト定義フォーマット

キーのサイズはユニット倍率で表現する。絶対値にすると解像度・DPI ごとに定義を作り直すことになる。

```json
{
  "name": "JIS フルレイアウト",
  "rowUnits": 15.5,
  "rows": [
    {
      "height": 0.5,
      "keys": [
        { "label": "Esc", "scanCode": "0x01", "width": 1.75, "repeatable": false },
        { "label": "F1",  "scanCode": "0x3B" }
      ]
    },
    {
      "keys": [
        { "label": "半/全", "scanCode": "0x29", "width": 1.25,
          "fnLabel": "Esc", "fnScanCode": "0x01", "repeatable": false },
        { "label": "1", "shiftLabel": "!", "scanCode": "0x02" },
        { "label": "BS", "icon": "E750", "scanCode": "0x0E", "width": 1.25 }
      ]
    },
    {
      "height": 0.5,
      "rowSpan": 2,
      "keys": [
        { "label": "Shift", "icon": "E752", "scanCode": "0x2A", "width": 2.5, "modifier": "Shift" },
        { "label": "", "scanCode": "0x00", "spacer": true, "width": 3.0 }
      ]
    },
    {
      "overlayPrevious": true,
      "keys": [
        { "label": "↑", "icon": "E70E", "scanCode": "0x48", "extended": true }
      ]
    }
  ]
}
```

### 行の属性

| 属性 | 内容 |
|---|---|
| `height` | 行の高さ倍率（既定 1.0）。ファンクション段や矢印段は 0.5 |
| `rowSpan` | 縦にまたがる行数（既定 1）。またがられる側は該当位置を `spacer` で空ける |
| `overlayPrevious` | 直前の行と同じ位置に重ねる。新しい行を消費しない |

`rowSpan` と `overlayPrevious` は、主キー列の中に逆 T 字の矢印クラスタを差し込むための仕組み。

### キーの属性

| 属性 | 内容 |
|---|---|
| `label` | 通常時の表示 |
| `icon` | Segoe Fluent Icons のコードポイント（例 `"E750"`）。指定するとアイコンを描く |
| `iconOnly` | アイコンのみ表示し、ラベルを併記しない（Windows キーなど） |
| `shiftLabel` | Shift 時の表示。記号キーでは左上に小さく併記する |
| `fnLabel` / `fnIcon` / `fnScanCode` / `fnExtended` | Fn ラッチ時の表示と送出内容 |
| `scanCode` | 送出するスキャンコード |
| `extended` | `KEYEVENTF_EXTENDEDKEY` を付けるか |
| `width` | ユニット倍率（既定 1.0） |
| `modifier` | 修飾キーの場合その種別。指定時はラッチ動作になる |
| `repeatable` | 長押しリピートの対象か（既定 true、修飾キーとスペーサーは常に false） |
| `spacer` | レイアウト上の隙間。描画も送出もしない |
| `mergeDown` / `mergeUp` | 上下のキーと結合して 1 つのキーに見せる（L 字の Enter） |
| `hideFace` | ラベルもアイコンも描かない。結合したキーの片側に使う |

`rowUnits` は 1 行あたりのユニット総数。全行をこの値で割って配置するため、行ごとのキー数が違っても縦の位置が揃う。行の合計がこれを超える定義は読み込み時に弾く。

## 設計上の判断

**修飾キーの送出方法** — ラッチした修飾キーは、通常キーの押下時にまとめて「修飾キーダウン → 通常キーダウン → 通常キーアップ → 修飾キーアップ」の順で送る。修飾キーを先に単独で送って保持する方式は、アプリの異常終了時に押しっぱなしが残る。

**修飾キーの押さえ続け** — タップでラッチ（次の 1 文字で消費）、再タップでロック。これに加えて、指で押さえている間も有効として扱う。物理キーボードと同じく、Shift を押さえたまま別の指で複数の文字を打てるようにするため。

タップと押さえ続けは指を離すまで区別できない。押した時点ではタップとして状態を巡回させ、押さえている間に実際に使われたと分かった時点で巡回を取り消す。これで 1 つのキーで両方の使い方が成立する。

**AppBar の再登録** — 高さ変更のたびに `ABM_QUERYPOS` → `ABM_SETPOS` を通す。自前で座標を決めて `SetWindowPos` するだけでは、他アプリの作業領域が更新されない。

**DPI 変更への追従** — `WM_DPICHANGED` を受けてレイアウトを再計算する。フレームワークの自動スケーリングに任せると AppBar が確保した物理領域とずれる。

**結合したキーの枠線** — `Border` の一辺を消して不足分を継ぎ足す方式は、角丸の弧と継ぎ目が噛み合わない。L 字の輪郭を 1 本のパスとして引き、`Border` には塗りだけを担当させる。

枠線は上辺が明るく下辺が暗い縦グラデーションで立体感を出しているため、段ごとに 0→1 を繰り返すと継ぎ目で色が飛ぶ。上下の実寸から各段が占める区間を切り出し、1 本のグラデーションが全体を貫くようにする。

段差にできる入隅の丸みは隣のキーとの隙間へ回り込む。ボタンの内側に置くと切り落とされるため、輪郭のパスはボタンの外側の階層に置く。また行は上から順に描かれるので、丸みは**下段側**に描かせないと下段の塗りに覆われる。

**通知領域メニューの位置** — `NOTIFYICON_VERSION_4` のコールバックが `wParam` で渡してくる画面座標を使う。`GetCursorPos` はタッチでタップ位置に追従しないため使わない。

**自動表示の判定** — 種別だけでは決められない。読み取り専用かどうかを種別より先に見る。Edge の Web ページはメモ帳の編集領域と同じ `ControlType.Document` で、テキストパターンにも対応する。

サジェストや変換候補が出てもフォーカスは入力欄から外れるが、これらは呼び出し元に所有された別ウィンドウとして前面に出る。所有関係（`GA_ROOTOWNER`）をたどって「同じウィンドウの付属物か」を見れば、猶予時間に頼らず判別できる。

要素の `NativeWindowHandle` は当てにできない（Edge は常に 0 を返す）。前面ウィンドウから取ること。

**物理キーボードの検出** — `SM_CONVERTIBLESLATEMODE` を使う。Windows 自身がタッチキーボードの自動表示を決めるのに使う値。Raw Input でキーボードを数える方法は採れない。実機では仮想デバイスを含めて 8 個が並び、物理的な接続を表さない。

打鍵の検出には低レベルフックを使う。**自分が `SendInput` で送ったキーも同じフックに流れてくる**ため、`LLKHF_INJECTED` が立っていないものだけを物理入力とみなす。これを見落とすと、自分のキーを押した瞬間に自分が隠れる。

フックは OS 全体の入力経路上で呼ばれる。中では判定だけを行い、AppBar の操作はディスパッチャへ逃がす。

**シェルより上に出る** — `uiAccess="true"` による。タスクバーとスタートメニューはシェルが Z 順を管理しており、`WS_EX_NOACTIVATE` のウィンドウが正攻法で上へ戻る手段は無い。支援技術としての権限を得れば上位のバンドに入る。

`SetWindowBand` のような非公開 API には頼らない。このプロジェクトは Acrylic のために非公開 API を使う状態を嫌って WinUI 3 へ移行した経緯があり、同じ判断を踏襲する。

**グリップの操作の切り分け** — 押した時点では長押しかスワイプか決まらない。時間か移動量のどちらか先に満たしたほうで確定させる。動き始めた時点で長押しのタイマーを止めないと、ゆっくり動かしている間に時間が過ぎて高さ変更に入ってしまう。
