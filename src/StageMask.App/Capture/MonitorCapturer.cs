using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using StageMask.App.Services;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace StageMask.App.Capture;

/// <summary>
/// 共有用ミラーのために、1台のモニター全体を Windows.Graphics.Capture(公式API)で取り込み続ける。
/// - 画面に変化があったときだけフレームが届く。最新のフレームだけを残し、決めた間隔で画像に変換する
///   (間引いたフレームも捨てないので、画面が止まった後も最後の状態が必ず反映される)。
/// - WDA_EXCLUDEFROMCAPTURE を付けたウィンドウ(操作パネル・取り込み元のモニターの枠)は映らない。
/// - パッケージ化していないアプリでは、取り込み中のモニターの周りに黄色い枠が出る(公式の仕様)。
/// </summary>
internal sealed class MonitorCapturer : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private IDirect3DDevice? _device;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private Windows.Graphics.SizeInt32 _poolSize;
    private Direct3D11CaptureFrame? _pending;   // まだ画像にしていない最新のフレーム
    private byte[] _front = Array.Empty<byte>(); // 表示用に渡す画像
    private byte[] _back = Array.Empty<byte>();  // 変換中の画像
    private int _frontW, _frontH, _frontStride;
    private long _frontVersion;
    private volatile bool _failed;
    private int _intervalMs;

    public IntPtr Monitor { get; }
    public bool Failed => _failed;
    /// <summary>1枚でも画像にできたか</summary>
    public bool HasFrame => Interlocked.Read(ref _frontVersion) > 0;
    /// <summary>最後に画像にした時刻(UTC)</summary>
    public DateTime LastFrameUtc { get; private set; }

    public MonitorCapturer(IntPtr monitor, int maxFps)
    {
        Monitor = monitor;
        _intervalMs = 1000 / Math.Clamp(maxFps, 1, 60);
    }

    public void SetMaxFps(int fps) => _intervalMs = 1000 / Math.Clamp(fps, 1, 60);

    public void Start()
    {
        try
        {
            _device = Direct3DHelper.CreateDevice();
            _item = CaptureItemHelper.CreateForMonitor(Monitor);
            if (_item is null) { _failed = true; return; }
            _poolSize = _item.Size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
            _pool.FrameArrived += OnFrameArrived;
            _item.Closed += (_, _) => _failed = true;
            _session = _pool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = true; // 相手にもマウスの位置が見えるように
            _session.StartCapture();
            _ = Task.Run(() => ConvertLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            _failed = true;
            Logger.Write("MonitorCapturer.Start", ex);
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            var f = sender.TryGetNextFrame();
            if (f is null) return;
            Direct3D11CaptureFrame? old;
            lock (_gate) { old = _pending; _pending = f; }
            old?.Dispose();
        }
        catch (Exception ex)
        {
            _failed = true;
            Logger.Write("MonitorCapturer.FrameArrived", ex);
        }
    }

    private async Task ConvertLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var started = Environment.TickCount64;
            Direct3D11CaptureFrame? frame;
            lock (_gate) { frame = _pending; _pending = null; }
            if (frame is not null)
            {
                try
                {
                    using (frame)
                        await ConvertAsync(frame);
                }
                catch (Exception ex)
                {
                    _failed = true;
                    Logger.Write("MonitorCapturer.Convert", ex);
                }
            }
            var wait = _intervalMs - (int)(Environment.TickCount64 - started);
            try { await Task.Delay(Math.Max(5, wait), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task ConvertAsync(Direct3D11CaptureFrame frame)
    {
        var content = frame.ContentSize;
        if (content.Width <= 0 || content.Height <= 0) return;
        using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied);
        int bw = bitmap.PixelWidth, bh = bitmap.PixelHeight;
        int need = bw * bh * 4;
        if (_back.Length != need) _back = new byte[need]; // 大きさが変わらない限り同じ配列を使い回す
        bitmap.CopyToBuffer(_back.AsBuffer());
        lock (_gate)
        {
            (_front, _back) = (_back, _front);
            _frontW = Math.Min(content.Width, bw);
            _frontH = Math.Min(content.Height, bh);
            _frontStride = bw * 4;
            _frontVersion++;
            LastFrameUtc = DateTime.UtcNow;
        }

        // モニターの解像度が変わったらフレームプールを作り直す
        if ((content.Width != _poolSize.Width || content.Height != _poolSize.Height) && _pool is not null && _device is not null)
        {
            _poolSize = content;
            _pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, content);
        }
    }

    /// <summary>
    /// 前回より新しい画像があれば、copy に渡して true を返す(UIスレッドから呼ぶ)。
    /// copy の中で画像を写し終えるまで、取り込み側はこの画像を書き換えない。
    /// </summary>
    public bool TryReadLatest(ref long version, Action<byte[], int, int, int> copy)
    {
        lock (_gate)
        {
            if (_frontVersion == 0 || _frontVersion == version) return false;
            copy(_front, _frontW, _frontH, _frontStride);
            version = _frontVersion;
            return true;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_gate) { _pending?.Dispose(); _pending = null; }
        try { _session?.Dispose(); } catch { }
        try { _pool?.Dispose(); } catch { }
    }
}
