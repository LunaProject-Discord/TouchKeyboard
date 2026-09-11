using System;
using System.IO;
using System.Threading.Tasks;
using TouchKeyboard.Diagnostics;
using Windows.Media.Audio;
using Windows.Storage;

namespace TouchKeyboard.Interop;

/// <summary>打鍵の音。</summary>
public enum KeySoundKind
{
    /// <summary>文字のキー。</summary>
    Tap,

    /// <summary>スペース。</summary>
    Space,

    /// <summary>役割のキー。Backspace や Enter、修飾キー。</summary>
    Function,
}

/// <summary>
/// 標準のタッチキーボードと同じ音を鳴らす。
///
/// 音源は OS が持っているものをそのまま読む。似せた音を自前で用意すると、
/// 利用者が同じ端末で聞き比べたときに違うものだと分かる。
/// 配布はせず、その場で読むだけに留める。
///
/// 見つからない環境では黙って鳴らさない。音が出ないことは打鍵の妨げにならない。
/// </summary>
public sealed class KeySound
{
    /// <summary>
    /// 同時に鳴らせる数。文字ごとに複数持つ。
    ///
    /// 1 つのノードを使い回すと、連打したときに前の再生が打ち切られて
    /// 音が重ならない。実機の連続入力では前の音が消える前に次が鳴る。
    /// </summary>
    private const int PoolSize = 4;

    /// <summary>種類ごとの再生用ノード。読めなければ null のまま。</summary>
    private readonly AudioFileInputNode?[][] _pool = new AudioFileInputNode?[3][];

    /// <summary>次に使うノードの番地。種類ごとに順に回す。</summary>
    private readonly int[] _next = new int[3];

    private AudioGraph? _graph;

    public KeySound()
    {
        // 読み込みも再生の準備も非同期の API しかない。コンストラクタを
        // 待たせるとウィンドウの起動が遅れるので、裏で進めて完了を待たない。
        _ = InitializeAsync();
    }

    /// <summary>1 つでも読めたか。</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>鳴らす。読めていない種類は何もしない。</summary>
    public void Play(KeySoundKind kind)
    {
        var pool = _pool[(int)kind];
        if (pool is null) return;

        var index = _next[(int)kind];
        _next[(int)kind] = (index + 1) % pool.Length;

        var node = pool[index];
        if (node is null) return;

        // 頭から鳴らし直す。使い回しているノードなので、前回の再生位置が
        // 残ったままだと途中から鳴る。
        node.Seek(TimeSpan.Zero);
        node.Start();
    }

    private async Task InitializeAsync()
    {
        var assets = FindAssets();
        if (assets is null)
        {
            TraceLog.Write("打鍵音: 音源が見つかりません");
            return;
        }

        try
        {
            // ゲーム効果音と同じ扱いにする。実測ではないが、UI の短い効果音として
            // 素通しで低遅延に鳴らしたい用途に最も近い。
            var graphResult = await AudioGraph.CreateAsync(
                new AudioGraphSettings(Windows.Media.Render.AudioRenderCategory.GameEffects));

            if (graphResult.Status != AudioGraphCreationStatus.Success)
            {
                TraceLog.Write($"打鍵音: グラフの作成に失敗 {graphResult.Status}");
                return;
            }

            _graph = graphResult.Graph;

            var outputResult = await _graph.CreateDeviceOutputNodeAsync();
            if (outputResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                TraceLog.Write($"打鍵音: 出力の作成に失敗 {outputResult.Status}");
                return;
            }

            var output = outputResult.DeviceOutputNode;

            await LoadAsync(KeySoundKind.Tap, Path.Combine(assets, "KbdKeyTapModernUX.wav"), output);
            await LoadAsync(KeySoundKind.Space, Path.Combine(assets, "KbdSpaceBarModernUX.wav"), output);
            await LoadAsync(KeySoundKind.Function, Path.Combine(assets, "KbdFunctionModernUX.wav"), output);

            // グラフを鳴らしたまま止めない。
            //
            // Bluetooth イヤホンは、しばらく音が流れないと省電力状態に入り、
            // 次に鳴らすときは無線のリンクを起こすところから始まる。そのぶん
            // 音の立ち上がりに数百 ms かかり、打鍵のような短い音は間に合わず
            // 聞こえない。無音でも流し続けてリンクを起きたままにしておけば、
            // 実際に鳴らす音は待たされずに届く。
            _graph.Start();
        }
        catch (Exception ex)
        {
            TraceLog.Write($"打鍵音の初期化に失敗: {ex.Message}");
        }
    }

    private async Task LoadAsync(KeySoundKind kind, string path, AudioDeviceOutputNode output)
    {
        if (_graph is null) return;
        if (!File.Exists(path)) return;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var pool = new AudioFileInputNode?[PoolSize];

            for (var i = 0; i < PoolSize; i++)
            {
                var result = await _graph.CreateFileInputNodeAsync(file);
                if (result.Status != AudioFileNodeCreationStatus.Success)
                {
                    TraceLog.Write($"打鍵音の読み込みに失敗: {path} {result.Status}");
                    return;
                }

                var node = result.FileInputNode;
                node.AddOutgoingConnection(output);

                // グラフを開始するまでは黙らせておく。作った時点で鳴り出す版もあるため
                // 明示的に止める。
                node.Stop();

                pool[i] = node;
            }

            _pool[(int)kind] = pool;
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            TraceLog.Write($"打鍵音の読み込みに失敗: {path} {ex.Message}");
        }
    }

    /// <summary>
    /// 音源のある場所を探す。
    ///
    /// タッチキーボードの本体（InputApp）に同梱されている。
    /// 版によって包みの名前が変わりうるため、決め打ちにせず探す。
    /// </summary>
    private static string? FindAssets()
    {
        try
        {
            var apps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps");

            foreach (var package in Directory.GetDirectories(apps, "MicrosoftWindows.Client.CBS_*"))
            {
                var assets = Path.Combine(package, "InputApp", "Assets");
                if (Directory.Exists(assets)) return assets;
            }
        }
        catch (Exception)
        {
            // 探せない環境でも動作は止めない。
        }

        return null;
    }
}
