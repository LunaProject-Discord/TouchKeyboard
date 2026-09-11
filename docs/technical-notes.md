# 技術メモ

## なぜ ATOK ではクラシックが選べないのか

タッチキーボードは IME がアクティブになると、`ITfFunctionProvider::GetFunction` で `IID_ITfFnGetPreferredTouchKeyboardLayout` を照会する。IME がこのインターフェイスを実装していれば、そのレイアウトを表示する。実装していなければ、その言語の既定レイアウトにフォールバックする。

レイアウト種別は `TKBLayoutType` 列挙体で表される。

| 定数 | 値 | 意味 |
|---|---|---|
| `TKBLT_UNDEFINED` | 0 | 未定義。クラシックレイアウトにフォールバックする |
| `TKBLT_CLASSIC` | 1 | クラシックレイアウトを使う |
| `TKBLT_OPTIMIZED` | 2 | タッチ最適化レイアウトを使う |

このインターフェイスは TSF 版 IME のみが対象で、IMM32 の旧式 IME には適用されない。また対象言語は日本語・韓国語・簡体字中国語・繁体字中国語に限られる。

### 実測した結果（確定）

**Windows 11 はこのインターフェイスを参照していない。** 上記の説明は仕様としては正しいが、実際には機能していない。

#### 1. ATOK は実装しており、タッチ最適化を宣言している

`ITfThreadMgr2::GetFunctionProvider` で ATOK の関数プロバイダを取得し、`GetLayout` を呼んだ結果:

```
provider: ATOK
TKB function name: ATOK GetPreferredTouchKeyboardLayout
GetLayout hr=0x00000000  type=TKBLT_OPTIMIZED (2)  preferredLayoutId=1041
```

| IME | `ITfFnGetPreferredTouchKeyboardLayout` | 宣言値 |
|---|---|---|
| ATOK | **実装あり** | `TKBLT_OPTIMIZED` (2) |
| Microsoft IME | `ITfFunctionProvider` 自体を公開しない（`TF_E_NOPROVIDER`） | — |

**「ATOK が未実装だから選択肢が出ない」は誤り。** 実装している。

#### 2. しかし何を宣言しても変わらない

検証用に、レイアウトを宣言するだけの最小のテキストサービス（ClassicTip）を作って登録し、有効な入力方式に切り替えてタッチキーボードを開いた。挙動は `HKCU\Software\ClassicTip` の `Mode` で切り替えられるようにした。

| Mode | 状態 | 結果 |
|---|---|---|
| 0 | `ITfFunctionProvider` 有り + **`TKBLT_CLASSIC`** を返す | **変化なし** |
| 2 | **`ITfFunctionProvider` 自体を公開しない**（MS-IME と同一条件） | **変化なし** |

どちらでもレイアウトは標準のままで、**レイアウト切替メニューの選択肢も一切変化しなかった。**

判定基準は「返した値」でも「プロバイダの有無」でもない。つまり:

- 「ATOK が `TKBLT_OPTIMIZED` を宣言している」ことと「最適化配列で表示される」ことは**因果ではなく偶然の一致**
- **Windows-classic-samples の「Windows 10 1709 以降このインターフェイスが照会されなくなった」という報告が正しい**

#### 3. では MS-IME でだけ選択肢が出るのはなぜか

TSF を通じた一般的な仕組みではない。**Microsoft IME が特別扱いされている。** MS-IME と完全に同一の条件（プロバイダ無し）を再現しても選択肢は現れなかったため、TSF から観測・制御できる差分ではないと結論できる。

**サードパーティの IME からは、正規の手段で介入する余地がない。**

#### 4. 傍受による回避も不可能

ATOK の COM 登録を自作プロキシに差し替えて `GetLayout` を横取りする案も、上記により無意味である。そもそも Windows がこのインターフェイスを呼んでいない。

なお傍受地点についても実測した。**ATOK の TIP は「文字を入力する各アプリケーションのプロセス」に読み込まれ、タッチキーボードのプロセス（`TextInputHost.exe`）には読み込まれない。**

| プロセス | ATOK の TIP |
|---|---|
| `TextInputHost.exe` | 読み込まれない（msctf.dll のみ） |
| `ctfmon.exe` | 読み込まれない |
| `notepad.exe` / `explorer.exe` | **読み込まれる** |

仮に傍受が有効だったとしても、タッチキーボードだけに影響する地点は存在せず、ユーザーが文字を入力する全アプリにコードを注入することになる。1 つでも読み込みに失敗すればそのアプリで日本語入力が死ぬ。割に合わない。

### 利用者側で解決できるか

できない。以下をいずれも実機で確認した。

**1. ATOK 側に設定項目がない**

`HKCU\Software\Justsystem\ATOK` と `HKLM\SOFTWARE\Justsystem\ATOK` を再帰的に走査したが、タッチキーボードのレイアウトに関する項目は存在しない。戻り値は DLL に埋め込まれていると考えられる。

**2. Windows 側の `KeyboardLayoutPreference` も効かない**

`HKCU\Software\Microsoft\TabletTip\1.7` の `KeyboardLayoutPreference`（既定値 0）を 0〜5 まで順に変更し、そのつど `TextInputHost.exe` を終了して再起動させ、ATOK が有効な状態でタッチキーボードを表示させた。**どの値でもタッチ最適化レイアウトのままで、変化しなかった。**

IME が `TKBLT_OPTIMIZED` を宣言している間、この設定は無視される。

**3. 宣言を変えられるのはアクティブな TIP 本人だけ**

TIP は排他であるため、外部から介入する余地がない（後述「検討して却下したアプローチ」）。

**結論。ATOK を使う限り標準タッチキーボードでクラシック配列は使えず、自作する以外の手段がない。** ただし理由は「ATOK が未実装だから」ではなく「ATOK がタッチ最適化を明示的に要求しており、Windows がそれに従っているから」である。

### 調査に使った手段

タッチキーボードはタスクバーのボタンが非表示（`TipbandDesiredVisibility = 2`）だと `TabTip.exe` を起動しても出てこない。COM の `ITipInvocation::Toggle` で確実に表示できる。

- CLSID `{4CE576FA-83DC-4F88-951C-9D0782B4E376}`（UIHostNoLaunch Class）
- IID `{37c994e7-432b-4834-a2f7-dce1f13b834b}`
- `Toggle(HWND)` は表示と非表示を切り替える。`TabTip.exe` が動いている必要がある

### 参考

- `ITfFnGetPreferredTouchKeyboardLayout` interface (ctffunc.h)
- `TKBLayoutType` enumeration (ctffunc.h)
- Input Method Editor (IME) requirements — Windows apps

## 検討して却下したアプローチ

将来の再検討を避けるため、判断とその根拠を残す。

### 自作 TSF TIP で TKBLT_CLASSIC を宣言し、標準タッチキーボードを使う

**成立しない。** TSF の TIP は排他であり、一度にアクティブになれるテキストサービスは 1 つだけ。TextInputHost が `ITfFunctionProvider` を照会する相手は常にアクティブな TIP である。したがって次の 2 状態しかない。

- ATOK がアクティブ → 補正は効くが、レイアウト宣言は ATOK が握り介入できない
- 自作 TIP がアクティブ → `TKBLT_CLASSIC` を返せるが ATOK は動かない

「レイアウトだけ自作 TIP が宣言し、変換は ATOK に委譲する」分離は TSF に仕組みがない。加えて TIP は in-proc COM サーバとして入力先アプリのプロセスへロードされるため C++ 必須で、本プロジェクトの技術スタックと合わない。

### ITfFnSearchCandidateProvider で ATOK を変換エンジンとして使う

`ITfFnSearchCandidateProvider`（Windows 8 で追加）を使うと、バックグラウンドスレッドから他の IME に変換候補を問い合わせられる。これを使って「自作 TIP がレイアウトを宣言し、変換は ATOK に委譲する」構成が作れないかを実機で検証した。

**結論: 目的（入力ミス補正）は達成できない。**

検証環境: Windows 11 ARM64 / HKL 0x04110411。**ATOK をアクティブにした場合と MS-IME をアクティブにした場合の両方で実行し、結果が同一であることを確認した。**

| 事実 | 詳細 |
|---|---|
| 現行 ATOK は対応している | `provider: ATOK` / `function: ATOK SearchCandidateProvider` を取得できた |
| **ローマ字を変換しない** | `konnnichiha` を渡すと `konnnichiha` がそのまま返る。かな文字列を受け取る前提の API |
| かな表記の誤りは一部補正する | `ふいんき` → `雰囲気`。ただし `こんにちわ` → `こんにちは` は効かない |
| 速度 | 9〜68ms（初回は初期化込みで 160〜170ms）。打鍵ごとに走らせるには重い |
| **MS-IME は非対応** | `GetFunctionProvider` が `TF_E_NOPROVIDER` (0x80040503)。2013 年の報告とは逆転している |
| 非アクティブな IME にも問い合わせられる | MS-IME がアクティブな状態で ATOK から候補を取得できた。手法自体は今も有効 |

MS-IME の非対応はアクティブ・非アクティブのいずれでも再現するため、実装として持っていないと判断してよい。Windows 11 で MS-IME が刷新された際に落ちた可能性があるが、確認はしていない。

本プロジェクトの存在意義である入力ミス補正は、**ローマ字→かな変換の段階**で働く。この API はその段階を持たないため同じ補正は再現できない。`ふいんき` → `雰囲気` は*かな表記*の誤りに対する辞書レベルの補正であり、打鍵ミスの補正とは別物。

将来キーボード上に変換候補バーを自前で実装する場合には使える。その際はローマ字→かな変換を自前で持つ必要があり、MS-IME では動かない。

#### 実装上の注意

この API を扱う場合、以下で詰まる。

- **IID と vtable は SDK ヘッダーで確認すること。** `ctffunc.h` / `msctf.h` の `MIDL_INTERFACE` と `*Vtbl` の `DECLSPEC_XFGVIRT` を読む。記憶や web の断片は誤りが多い
- `ITfInputProcessorProfileMgr` = `{71C6E74C-...}`、`IEnumTfInputProcessorProfiles` = `{71C6E74D-...}`。1 文字違いで紛らわしい
- `ITfFnSearchCandidateProvider` = `{87a2ad8f-f27b-4920-8501-67602280175d}`
- **`ITfThreadMgr2` は `ITfThreadMgr` と並びが違う。** スロット 3 は `Activate` で `ActivateEx` は 13 番目。`AssociateFocus` は存在しない。ずれたまま呼ぶとクラッシュする
- `SPI_SETTHREADLOCALINPUTSETTINGS` で IME 切り替えをスレッドローカルに閉じ込める必要がある。これを怠るとデスクトップ全体の IME 設定に影響する

### バイナリ解析による介入

TextInputHost や ATOK を解析して割り込ませる案は、Windows のライセンス条項がリバースエンジニアリングを原則禁じている。加えて Windows Update のたびに壊れる。採用しない。

## WinUI 3 の実現性（スパイクで検証済み）

WPF の代わりに WinUI 3 を使えるかを、最小のアプリで実機検証した。

**結論: 絶対制約をすべて満たせる。しかも Acrylic を公開 API だけで実現できる。**

| 検証項目 | 結果 |
|---|---|
| `WS_EX_NOACTIVATE` を付けてもボタンが反応する | ✅ |
| 送出の前後で入力先のフォーカスが外れない | ✅ |
| タッチでも反応する | ✅ |
| Per-Monitor DPI V2 | ✅（`app.manifest` の宣言が必須） |
| **非アクティブなウィンドウでも Acrylic が描かれる** | ✅ |

環境: .NET 10 / ARM64 / アンパッケージ / 自己完結 / Windows App SDK 2.4.0。

### Acrylic を非アクティブウィンドウで描く方法

簡易 API の `SystemBackdrop = new DesktopAcrylicBackdrop()` では**描かれない**。この API はウィンドウのアクティブ状態に応じて `SystemBackdropConfiguration.IsInputActive` を切り替えるため、`WS_EX_NOACTIVATE` のウィンドウでは常に `false` になる。

コントローラを直接使い、`IsInputActive` を `true` に固定する。

```csharp
var config = new SystemBackdropConfiguration
{
    IsInputActive = true,   // アクティブ状態に関係なく「入力中」として扱わせる
    Theme = SystemBackdropTheme.Default,
};

var acrylic = new DesktopAcrylicController();
acrylic.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
acrylic.SetSystemBackdropConfiguration(config);
```

**WPF の DWM バックドロップとは原因の層が違う。** あちらは DWM 自身が非アクティブと判断して単色へ落とすため介入できず、非公開 API の `SetWindowCompositionAttribute` に頼るしかなかった。こちらはアプリが渡す設定値なので固定できる。

### 採用する場合の作業量と代償

| レイヤ | 扱い |
|---|---|
| `Interop/` `Input/` `Layout/` `Settings/` | **そのまま流用できる**（UI フレームワークに依存しない設計） |
| `Views/` | 全面的に書き直し |

- **配置サイズ**: 自己完結で 238 MB（WPF は 0.2 MB）。`WindowsAppRuntime` を別途導入すれば小さくなる
- **タスクトレイ**: WinUI 3 に標準の NotifyIcon が無く、`Shell_NotifyIcon` の P/Invoke か外部ライブラリが必要
- **AppBar**: HWND は `WindowNative.GetWindowHandle` で取れるため、既存の `SHAppBarMessage` ラッパーがそのまま使える

## SendInput

### 落とし穴

- `INPUT` 構造体は `KEYBDINPUT` / `MOUSEINPUT` / `HARDWAREINPUT` のユニオンを含む。構造体サイズは最大メンバである `MOUSEINPUT` に合わせて決まる。32bit と 64bit でサイズが異なる
- `cbSize` には `Marshal.SizeOf<INPUT>()` を渡す。定数を直書きしない
- ユニオンは `[StructLayout(LayoutKind.Explicit)]` + `[FieldOffset]` で表現する。64bit ではポインタ整列によるパディングが入る
- 戻り値が 0 のときは送出に失敗している。`Marshal.GetLastWin32Error()` を確認する

### フラグ

| フラグ | 値 | 用途 |
|---|---|---|
| `KEYEVENTF_EXTENDEDKEY` | 0x0001 | E0 プレフィックス付きキー |
| `KEYEVENTF_KEYUP` | 0x0002 | キーアップ |
| `KEYEVENTF_SCANCODE` | 0x0008 | `wScan` をスキャンコードとして扱う（必須） |

`KEYEVENTF_SCANCODE` 使用時は `wVk` を 0 にする。

### 拡張キー（E0 プレフィックスが必要）

矢印キー、Insert、Delete、Home、End、PageUp、PageDown、右 Ctrl、右 Alt、左右 Win、Application キー、テンキーの Enter と `/`。

これらに `KEYEVENTF_EXTENDEDKEY` を付け忘れると、テンキー側のキーとして解釈される。

## スキャンコード（実機確認済み・確定値）

確認環境: Windows 11 ARM64 / `LayerDriver JPN` = `kbd106.dll` / `OverrideKeyboardIdentifier` = `PCAT_106KEY` / HKL `0x04110411`。

### 確認方法

物理キーを押して `WM_KEYDOWN` の `lParam` を読む方法は、半角/全角のように IME が横取りするキーで取りこぼす。代わりに `MapVirtualKeyEx` で現在のキーボードレイアウトへ直接問い合わせた。

- `MAPVK_VSC_TO_VK_EX`（=3）でスキャンコードから仮想キーを逆引き
- `MAPVK_VK_TO_VSC`（=0）で仮想キーからスキャンコードを順引き
- HKL は `GetKeyboardLayoutList` で列挙し、言語 ID `0x0411` のものを選ぶ

**注意**: `LoadKeyboardLayoutW("00000411", ...)` は US 英語の HKL（`0x04090409`）を返すことがある。この HKL で問い合わせると US 配列の表が得られてしまい、誤りに気付きにくい。必ず `GetKeyboardLayoutList` で列挙して言語 ID を確認すること。

### 日本語配列固有キー（確定）

| キー | スキャンコード | 逆引きされる仮想キー |
|---|---|---|
| 半角/全角 | 0x29 | `VK_OEM_AUTO` (0xF3) |
| 無変換 | 0x7B | `VK_NONCONVERT` (0x1D) |
| 変換 | 0x79 | `VK_CONVERT` (0x1C) |
| カタカナ/ひらがな | 0x70 | `VK_OEM_COPY` (0xF2) |
| `¥` | 0x7D | `VK_OEM_5` (0xDC) |
| `\_`（ろ） | 0x73 | `VK_OEM_102` (0xE2) |

`VK_KANJI` (0x19) と `VK_KANA` (0x15) は順引きで 0x00 を返す。これらから配列を組み立てないこと。

### 記号キー（US 配列との差分）

同じスキャンコードでも US 配列とは別の文字になる。US の表を流用しないこと。

| スキャンコード | JIS | US |
|---|---|---|
| 0x0D | `^` (`VK_OEM_7`) | `=` |
| 0x1A | `@` (`VK_OEM_3`) | `[` |
| 0x1B | `[` (`VK_OEM_4`) | `]` |
| 0x27 | `;` (`VK_OEM_PLUS`) | `;` (`VK_OEM_1`) |
| 0x28 | `:` (`VK_OEM_1`) | `'` |
| 0x2B | `]` (`VK_OEM_6`) | `\` |
| 0x56 | （割り当てなし） | `\` (`VK_OEM_102`) |

英字・数字段（0x02〜0x0B、0x10〜0x19、0x1E〜0x26、0x2C〜0x32）と、`,` 0x33 / `.` 0x34 / `/` 0x35 / `-` 0x0C は JIS と US で共通。

## ウィンドウスタイル

| 定数 | 値 |
|---|---|
| `GWL_EXSTYLE` | -20 |
| `WS_EX_TOOLWINDOW` | 0x00000080 |
| `WS_EX_NOACTIVATE` | 0x08000000 |

WPF では `SourceInitialized` のタイミングで `HwndSource` からハンドルを取得し、`SetWindowLong` で適用する。コンストラクタではまだハンドルが存在しない。

64bit では `SetWindowLongPtr` を使う。`SetWindowLong` のままだと値が切り詰められる環境がある。

## AppBar

| メッセージ | 値 |
|---|---|
| `ABM_NEW` | 0x00000000 |
| `ABM_REMOVE` | 0x00000001 |
| `ABM_QUERYPOS` | 0x00000002 |
| `ABM_SETPOS` | 0x00000003 |
| `ABM_WINDOWPOSCHANGED` | 0x00000009 |

エッジ指定は `ABE_LEFT` = 0、`ABE_TOP` = 1、`ABE_RIGHT` = 2、`ABE_BOTTOM` = 3。

### 手順

1. `ABM_NEW` で登録。このとき `uCallbackMessage` に `RegisterWindowMessage` で得た独自メッセージ ID を渡す
2. `ABM_QUERYPOS` で希望位置を問い合わせ、システムが返した矩形を使う
3. `ABM_SETPOS` で確定
4. 返ってきた矩形で `SetWindowPos`

### 注意

- アプリ終了時に必ず `ABM_REMOVE` を呼ぶ。呼ばずに終了すると作業領域が縮んだまま残る。異常終了に備えて、起動時に前回の残骸を掃除する処理も検討する
- `ABN_POSCHANGED` 通知を受けたら位置を再計算する
- タスクバーと同じエッジに登録する場合、両者の領域が積み上がる

## UI Automation

`Automation.AddAutomationFocusChangedEventHandler` でフォーカス変更を購読する。

判定は `ControlType.Edit` または `ControlType.Document` に加え、`TextPattern` / `ValuePattern` の対応有無を見る。単一の条件では取りこぼす。

### 注意

- イベントハンドラは UI Automation のスレッドで呼ばれる。UI 更新はディスパッチャ経由で行う
- ハンドラ内で重い処理をすると全体が詰まる。判定は軽く保つ
- UWP / ストアアプリでは情報の取り方が異なり、判定が不安定になる
- 自身のウィンドウへのフォーカス変更を無視する必要がある（`WS_EX_NOACTIVATE` が効いていれば通常は発生しないが、念のため）

## OS 標準タッチキーボードの無効化

自作キーボードと二重に出ないよう、標準のタッチキーボードの自動表示を止める。

- タスクバー設定でタッチキーボードのアイコンを非表示にする
- または `HKEY_CURRENT_USER\Software\Microsoft\TabletTip\1.7` の `EnableDesktopModeAutoInvoke` を 0 にする

なお同じキー配下にある `EnableCompatibilityKeyboard` は Windows 8.1〜10 世代の旧タッチキーボード向けの設定であり、Windows 11 のクラシックレイアウトとは無関係。混同しない。
