using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace StageMask.App.Capture;

/// <summary>
/// Windows.Graphics.Capture(公式API)でウィンドウの内容を取得する。
/// - 他のウィンドウに隠れていても、対象ウィンドウ自身の内容を取得できる。
/// - 非パッケージアプリでは、キャプチャ中の対象ウィンドウに黄色い枠が表示される
///   (枠を消すにはパッケージマニフェストで graphicsCaptureWithoutBorder の宣言が必要)。
/// ウィンドウごとにキャプチャセッションを開始し、最新フレームを保持する。
/// </summary>
internal sealed class GraphicsCaptureCapturer : IWindowCapturer
{
    // フレームプールを2つの処理で取り合わないよう、1つずつ順番に取り込む
    public bool SupportsConcurrentCapture => false;

    private sealed class SessionState : IDisposable
    {
        public required GraphicsCaptureItem Item;
        public required Direct3D11CaptureFramePool Pool;
        public required GraphicsCaptureSession Session;
        public Windows.Graphics.SizeInt32 PoolSize;
        public CapturedFrame? Last;
        public bool Closed;

        public void Dispose()
        {
            try { Session.Dispose(); } catch { }
            try { Pool.Dispose(); } catch { }
        }
    }

    private readonly object _gate = new();
    private readonly Dictionary<IntPtr, SessionState> _sessions = new();
    private IDirect3DDevice? _device;

    public static bool IsSupported()
    {
        try { return GraphicsCaptureSession.IsSupported(); }
        catch { return false; }
    }

    public async Task<CapturedFrame?> CaptureAsync(IntPtr hwnd, CancellationToken ct)
    {
        SessionState? state;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(hwnd, out state) || state.Closed)
            {
                state?.Dispose();
                state = StartSession(hwnd);
                if (state is null) return null;
                _sessions[hwnd] = state;
            }
        }

        // 溜まっているフレームのうち最新のものだけを使う。開始直後はフレームが届くまで少し待つ。
        Direct3D11CaptureFrame? latest = null;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            Direct3D11CaptureFrame? f;
            while ((f = state.Pool.TryGetNextFrame()) is not null)
            {
                latest?.Dispose();
                latest = f;
            }
            if (latest is not null || state.Last is not null) break;
            await Task.Delay(30, ct);
        }

        if (latest is null) return state.Last; // 内容に変化がなければ前回の画像を使う

        using (latest)
        {
            var content = latest.ContentSize;
            if (content.Width <= 0 || content.Height <= 0) return state.Last;

            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(latest.Surface, BitmapAlphaMode.Premultiplied);
            int bw = bitmap.PixelWidth, bh = bitmap.PixelHeight;
            var pixels = new byte[bw * bh * 4];
            bitmap.CopyToBuffer(pixels.AsBuffer());

            int w = Math.Min(content.Width, bw);
            int h = Math.Min(content.Height, bh);
            var frame = new CapturedFrame(pixels, w, h, bw * 4, CaptureOrigin.FrameRect);
            state.Last = frame;

            // ウィンドウのサイズが変わったらフレームプールを作り直す
            if (content.Width != state.PoolSize.Width || content.Height != state.PoolSize.Height)
            {
                state.PoolSize = content;
                state.Pool.Recreate(_device!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, content);
            }
            return frame;
        }
    }

    public void RetainOnly(IReadOnlyCollection<IntPtr> hwnds)
    {
        lock (_gate)
        {
            foreach (var key in _sessions.Keys.Where(k => !hwnds.Contains(k)).ToList())
            {
                _sessions[key].Dispose();
                _sessions.Remove(key);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var s in _sessions.Values) s.Dispose();
            _sessions.Clear();
        }
    }

    private SessionState? StartSession(IntPtr hwnd)
    {
        _device ??= Direct3DHelper.CreateDevice();
        GraphicsCaptureItem? item;
        try { item = CaptureItemHelper.CreateForWindow(hwnd); }
        catch { return null; }
        if (item is null) return null;

        var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;

        var state = new SessionState { Item = item, Pool = pool, Session = session, PoolSize = item.Size };
        item.Closed += (_, _) => state.Closed = true;
        session.StartCapture();
        return state;
    }
}

/// <summary>HWND から GraphicsCaptureItem を作る(IGraphicsCaptureItemInterop)。</summary>
internal static class CaptureItemHelper
{
    // IGraphicsCaptureItem の IID
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    public static GraphicsCaptureItem? CreateForWindow(IntPtr hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemGuid;
        var itemPointer = interop.CreateForWindow(hwnd, ref iid);
        return GraphicsCaptureItem.FromAbi(itemPointer);
    }
}

/// <summary>Direct3D 11 デバイスを作り、WinRT の IDirect3DDevice に変換する。</summary>
internal static class Direct3DHelper
{
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const int D3D_DRIVER_TYPE_WARP = 5;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;
    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr pAdapter, int driverType, IntPtr software, uint flags,
        IntPtr pFeatureLevels, uint featureLevels, uint sdkVersion,
        out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public static IDirect3DDevice CreateDevice()
    {
        int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                   IntPtr.Zero, 0, D3D11_SDK_VERSION, out var device, out _, out var context);
        if (hr < 0)
        {
            // GPU が使えない環境(仮想マシンなど)ではソフトウェアラスタライザにフォールバック
            hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                   IntPtr.Zero, 0, D3D11_SDK_VERSION, out device, out _, out context);
        }
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            var iid = IID_IDXGIDevice;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in iid, out var dxgiDevice));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable));
                try
                {
                    return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            if (context != IntPtr.Zero) Marshal.Release(context);
            if (device != IntPtr.Zero) Marshal.Release(device);
        }
    }
}
