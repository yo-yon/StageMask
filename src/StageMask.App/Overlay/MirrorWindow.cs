using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StageMask.App.Native;
using StageMask.Core.Geometry;
using StageMask.Core.Mirror;
using StageMask.Core.Settings;

namespace StageMask.App.Overlay;

/// <summary>
/// 共有用ミラー: 取り込み元のモニターの映像に、マスクを重ねて表示するウィンドウ。
/// Teams ではこのウィンドウ(全画面にしたモニター)を共有し、OBS ではウィンドウキャプチャで取り込む。
/// 映像とマスクは、どちらも取り込み元のモニターの左上を原点とした物理ピクセルで並べ、全体を Viewbox で拡大・縮小する。
/// </summary>
internal sealed class MirrorWindow : Window
{
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Canvas _masks = new() { ClipToBounds = true, IsHitTestVisible = false };
    private readonly Grid _surface = new();
    private readonly Border _blank;
    private readonly TextBlock _blankText;
    private WriteableBitmap? _bitmap;
    private IntPtr _hwnd;
    private RectD? _fullscreenBounds;

    /// <summary>ユーザーがウィンドウを閉じた(ミラーをやめる)</summary>
    public bool ClosedByUser { get; private set; }

    public MirrorWindow()
    {
        Title = "StageMask 共有用ミラー";
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/StageMask.ico")); }
        catch { /* アイコンが読めなくてもミラーは動かす */ }
        Background = Brushes.Black;
        Width = 960;
        Height = 560;
        ShowActivated = false;

        _surface.Children.Add(_image);
        _surface.Children.Add(_masks);
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        _blankText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 28,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(40),
        };
        _blank = new Border { Background = Brushes.Black, Child = _blankText, Visibility = Visibility.Visible };

        var root = new Grid { Background = Brushes.Black };
        root.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = _surface });
        root.Children.Add(_blank);
        Content = root;
        SetBlank("準備中");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        // 共有・録画に映す必要があるので、取り込みから外す設定は付けない
        NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_NONE);
        if (_fullscreenBounds is { } b) PlaceFullscreen(b);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        ClosedByUser = true;
        base.OnClosing(e);
    }

    /// <summary>指定したモニター全体に枠なしで表示する。null なら通常のウィンドウに戻す。</summary>
    public void SetFullscreen(RectD? monitorBounds)
    {
        _fullscreenBounds = monitorBounds;
        if (monitorBounds is { } b)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            PlaceFullscreen(b);
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
        }
    }

    private void PlaceFullscreen(RectD b)
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, (int)b.X, (int)b.Y, (int)b.Width, (int)b.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_NOZORDER);
    }

    /// <summary>このウィンドウの画面上の位置(物理ピクセル)</summary>
    public RectD ScreenBounds
    {
        get
        {
            if (_hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(_hwnd, out var r))
                return new RectD(r.Left, r.Top, r.Width, r.Height);
            return default;
        }
    }

    /// <summary>真っ黒にして理由を表示する。null なら映像を表示する。</summary>
    public void SetBlank(string? reason)
    {
        if (reason is null)
        {
            _blank.Visibility = Visibility.Collapsed;
            return;
        }
        _blankText.Text = "StageMask: 一時停止中\n" + reason;
        _blank.Visibility = Visibility.Visible;
    }

    /// <summary>取り込んだ画像を写す(UIスレッドから、MonitorCapturer.TryReadLatest の中で呼ぶ)。</summary>
    public void UpdateFrame(byte[] pixels, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0) return;
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
            _image.Source = _bitmap;
            _surface.Width = width;
            _surface.Height = height;
            _masks.Width = width;
            _masks.Height = height;
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
    }

    /// <summary>マスクを描き直す。座標は画面上の物理ピクセル。source は取り込み元のモニターの範囲。</summary>
    public void RenderMasks(OverlayState state, RectD source)
    {
        _masks.Children.Clear();
        if (state.Panic)
        {
            SetBlank(null);
            var panic = new Border
            {
                Background = Brushes.Black,
                Child = new TextBlock
                {
                    Text = state.PanicMessage,
                    Foreground = Brushes.White,
                    FontSize = Math.Max(24, source.Height / 25),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Add(panic, new Rect(0, 0, source.Width, source.Height));
            return;
        }

        // 手動マスクのモザイク/ぼかしは下に、それ以外(塗りつぶし)は上に描く(OverlayWindow と同じ順番)
        foreach (var pass in new[] { 0, 1 })
        {
            foreach (var v in state.Visuals)
            {
                if (v.Kind == MaskKind.DebugBox) continue;
                bool screenBased = v.Kind == MaskKind.Manual && v.Style != MaskStyle.Solid;
                if ((pass == 0) != screenBased) continue;
                var local = MirrorSafety.ToSourceLocal(v.ScreenRect, source);
                if (local.IsEmpty) continue;
                // はみ出した部分を切り取る前の位置で作り、モニターの外は Canvas の ClipToBounds で切る
                var full = new Rect(v.ScreenRect.X - source.X, v.ScreenRect.Y - source.Y, v.ScreenRect.Width, v.ScreenRect.Height);
                var element = OverlayWindow.CreateMaskElement(v, state, full);
                if (v.Occluded is { Count: > 0 })
                {
                    var holes = new GeometryGroup { FillRule = FillRule.Nonzero };
                    foreach (var o in v.Occluded)
                        holes.Children.Add(new RectangleGeometry(new Rect(o.X - v.ScreenRect.X, o.Y - v.ScreenRect.Y, o.Width, o.Height)));
                    element.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
                        new RectangleGeometry(new Rect(0, 0, full.Width, full.Height)), holes);
                }
                Add(element, full);
            }
        }
    }

    private void Add(FrameworkElement e, Rect r)
    {
        Canvas.SetLeft(e, r.X);
        Canvas.SetTop(e, r.Y);
        e.Width = r.Width;
        e.Height = r.Height;
        _masks.Children.Add(e);
    }
}
