using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using StageMask.App.Native;
using StageMask.Core.Geometry;
using StageMask.Core.Settings;

namespace StageMask.App.Overlay;

/// <summary>
/// 1台のモニター全体を覆う、透明・最前面・クリック透過のウィンドウ。
/// ここにマスクを描画する。画面共有(画面全体)・録画・プロジェクターのすべてに映る。
/// </summary>
internal sealed class OverlayWindow : Window
{
    private const double MinMaskSizePx = 8;
    private const double ResizeHandleDip = 16;

    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private IntPtr _hwnd;
    private OverlayState? _state;
    private bool _editMode;
    private Point _originPx;   // このウィンドウ左上の物理座標
    private double _scale = 1; // DIP → 物理ピクセルの倍率

    private enum DragMode { None, Create, Move, Resize }
    private DragMode _drag;
    private Point _dragStart;
    private Guid _dragId;
    private Rect _dragOriginalLocal;
    private Rectangle? _rubber;

    public RectD MonitorBounds { get; }

    /// <summary>
    /// 共有用ミラーの取り込み元のモニター: マスクを描かず、隠している場所に枠だけを描く(自分にだけ見える)。
    /// このときは、このウィンドウを取り込みから外す(枠がミラーに映らないように)。
    /// </summary>
    public bool OutlineOnly { get; private set; }
    public bool ShowOutlines { get; private set; } = true;

    public void SetOutlineMode(bool outlineOnly, bool showOutlines)
    {
        if (OutlineOnly == outlineOnly && ShowOutlines == showOutlines) return;
        OutlineOnly = outlineOnly;
        ShowOutlines = showOutlines;
        ApplyCaptureAffinity();
        if (_state is not null) Render(_state);
    }

    private void ApplyCaptureAffinity()
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowDisplayAffinity(_hwnd, OutlineOnly ? NativeMethods.WDA_EXCLUDEFROMCAPTURE : NativeMethods.WDA_NONE);
    }

    /// <summary>編集モードで新しいマスクが描かれた(物理座標)</summary>
    public event Action<RectD>? ManualMaskCreated;
    /// <summary>編集モードでマスクが移動・リサイズされた(物理座標)</summary>
    public event Action<Guid, RectD>? ManualMaskChanged;
    /// <summary>編集モードでマスクが右クリックされた</summary>
    public event Action<Guid>? ManualMaskDeleted;

    public OverlayWindow(RectD monitorBounds)
    {
        MonitorBounds = monitorBounds;
        Title = "StageMask Overlay";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // 仮の位置。正確な位置(物理ピクセル)は OnSourceInitialized で設定する
        Left = monitorBounds.X;
        Top = monitorBounds.Y;
        Width = Math.Max(1, monitorBounds.Width);
        Height = Math.Max(1, monitorBounds.Height);
        Content = _canvas;

        _canvas.MouseLeftButtonDown += OnMouseLeftButtonDown;
        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseLeftButtonUp += OnMouseLeftButtonUp;
        _canvas.MouseRightButtonUp += OnMouseRightButtonUp;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyExStyle();
        ApplyCaptureAffinity();
        PlaceOnMonitor();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // WPF が推奨サイズに変更した後で、モニター全体に合わせ直す
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            PlaceOnMonitor();
            if (_state is not null) Render(_state);
        }));
    }

    /// <summary>モニター全体を物理ピクセルで覆うように配置し、最前面にする。</summary>
    public void PlaceOnMonitor()
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST,
            (int)MonitorBounds.X, (int)MonitorBounds.Y, (int)MonitorBounds.Width, (int)MonitorBounds.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    /// <summary>他の最前面ウィンドウより前に出し直す(定期的に呼ぶ)。</summary>
    public void BringToTop()
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    private void ApplyExStyle()
    {
        if (_hwnd == IntPtr.Zero) return;
        long ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOPMOST;
        // 編集モード以外はマウス操作をすべて下のウィンドウに通す
        if (_editMode) ex &= ~NativeMethods.WS_EX_TRANSPARENT;
        else ex |= NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex);
    }

    // ------------------------------------------------------------------
    // 描画
    // ------------------------------------------------------------------

    public void Render(OverlayState state)
    {
        _state = state;
        if (_editMode != state.EditMode)
        {
            _editMode = state.EditMode;
            if (!_editMode) CancelDrag();
            ApplyExStyle();
        }

        UpdateOrigin();
        _canvas.Children.Clear();
        _canvas.Background = state.EditMode && !state.Panic
            ? new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x78, 0xD4))
            : null;

        if (state.Panic)
        {
            DrawPanic(state);
            return;
        }

        var monitorLocal = new Rect(0, 0, _canvas.ActualWidth > 0 ? _canvas.ActualWidth : ActualWidth,
                                          _canvas.ActualHeight > 0 ? _canvas.ActualHeight : ActualHeight);

        // 共有用ミラーの取り込み元: マスクの代わりに枠だけを描く(編集中は通常どおり描いて操作できるようにする)
        if (OutlineOnly && !state.EditMode)
        {
            if (!ShowOutlines) return;
            var stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0x8C, 0x00));
            stroke.Freeze();
            foreach (var v in state.Visuals)
            {
                if (v.Kind == MaskKind.DebugBox) continue;
                var local = ToLocal(v.ScreenRect);
                if (local.Width <= 0 || local.Height <= 0 || !local.IntersectsWith(monitorLocal)) continue;
                AddAt(new Rectangle { Stroke = stroke, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false }, local);
            }
            return;
        }
        // 描画順: 手動マスクのモザイク/ぼかし → それ以外。
        // 手動マスクのモザイクは「画面そのもの」を元に作るため、ウィンドウ全体のマスクなど
        // 塗りつぶしの下にある内容が含まれうる。塗りつぶしより下に描いて、上書きで漏れないようにする。
        foreach (var pass in new[] { 0, 1 })
        {
            foreach (var v in state.Visuals)
            {
                bool screenBased = v.Kind == MaskKind.Manual && v.Style != MaskStyle.Solid;
                if ((pass == 0) != screenBased) continue;
                DrawVisual(v, state, monitorLocal);
            }
        }

        if (state.EditMode) DrawEditBanner(state);
        if (_rubber is not null) _canvas.Children.Add(_rubber);
    }

    private void DrawVisual(MaskVisual v, OverlayState state, Rect monitorLocal)
    {
        var local = ToLocal(v.ScreenRect);
        if (local.Width <= 0 || local.Height <= 0 || !local.IntersectsWith(monitorLocal)) return;

        if (v.Kind == MaskKind.DebugBox)
        {
            AddAt(new Rectangle { Stroke = Brushes.Red, StrokeThickness = 2 }, local);
            return;
        }

        var element = CreateMaskElement(v, state, local);
        if (v.Occluded is { Count: > 0 })
        {
            // 手前のウィンドウに重なる部分を切り抜く(座標は要素の左上が原点)
            var holes = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var o in v.Occluded)
            {
                var r = ToLocal(o);
                holes.Children.Add(new RectangleGeometry(new Rect(r.X - local.X, r.Y - local.Y, r.Width, r.Height)));
            }
            element.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, local.Width, local.Height)), holes);
        }
        AddAt(element, local);

        if (state.EditMode && v.Kind == MaskKind.Manual)
        {
            AddAt(new Rectangle
            {
                Stroke = Brushes.Gold,
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 2 },
            }, local);
            var handle = new Rect(local.Right - ResizeHandleDip, local.Bottom - ResizeHandleDip, ResizeHandleDip, ResizeHandleDip);
            AddAt(new Rectangle { Fill = Brushes.Gold }, handle);
        }
    }

    internal static FrameworkElement CreateMaskElement(MaskVisual v, OverlayState state, Rect local)
    {
        var grid = new Grid { ClipToBounds = true };

        if (v.Style != MaskStyle.Solid && v.Image is not null)
        {
            // モザイク/ぼかし
            var img = new Image { Source = v.Image, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(img,
                v.Style == MaskStyle.Mosaic ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Linear);
            grid.Children.Add(img);
        }
        else
        {
            // 塗りつぶし(モザイク/ぼかし用の画像がまだない場合も、安全のため塗りつぶす)
            grid.Background = state.SolidBrush;
            if (v.ContentImage is not null && local.Width >= 24 && local.Height >= 24)
            {
                // マスク全体に敷き詰める(縦横比を保って拡大し、はみ出しは切り取る)
                grid.Children.Add(new Image
                {
                    Source = v.ContentImage,
                    Stretch = Stretch.UniformToFill,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
        }

        if (!string.IsNullOrEmpty(v.Text) && local.Width >= 60 && local.Height >= 22)
        {
            grid.Children.Add(new Border
            {
                Background = v.ContentImage is not null || v.Style != MaskStyle.Solid
                    ? new SolidColorBrush(Color.FromArgb(0x99, 0x00, 0x00, 0x00)) // 画像の上でも読めるよう背景を敷く
                    : null,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = v.Text,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)),
                    FontSize = Math.Clamp(local.Height * 0.3, 11, 32),
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = TextAlignment.Center,
                    MaxWidth = Math.Max(10, local.Width - 20),
                },
            });
        }
        return grid;
    }

    private void DrawPanic(OverlayState state)
    {
        var grid = new Grid { Background = Brushes.Black };
        grid.Children.Add(new TextBlock
        {
            Text = state.PanicMessage,
            Foreground = Brushes.White,
            FontSize = 42,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var size = new Rect(0, 0, Math.Max(ActualWidth, 1), Math.Max(ActualHeight, 1));
        AddAt(grid, size);
    }

    private void DrawEditBanner(OverlayState state)
    {
        var banner = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1F, 0x29, 0x37)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 8, 14, 8),
            Child = new TextBlock
            {
                Text = state.EditHint,
                Foreground = Brushes.White,
                FontSize = 15,
            },
            IsHitTestVisible = false,
        };
        banner.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = banner.DesiredSize.Width;
        Canvas.SetLeft(banner, Math.Max(0, (ActualWidth - w) / 2));
        Canvas.SetTop(banner, 16);
        _canvas.Children.Add(banner);
    }

    private void AddAt(FrameworkElement e, Rect r)
    {
        Canvas.SetLeft(e, r.X);
        Canvas.SetTop(e, r.Y);
        e.Width = r.Width;
        e.Height = r.Height;
        _canvas.Children.Add(e);
    }

    private void UpdateOrigin()
    {
        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (_scale <= 0) _scale = 1;
        if (_hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(_hwnd, out var r))
            _originPx = new Point(r.Left, r.Top);
        else
            _originPx = new Point(MonitorBounds.X, MonitorBounds.Y);
    }

    private Rect ToLocal(RectD screen) => new(
        (screen.X - _originPx.X) / _scale,
        (screen.Y - _originPx.Y) / _scale,
        screen.Width / _scale,
        screen.Height / _scale);

    private RectD ToScreen(Rect local) => new(
        _originPx.X + local.X * _scale,
        _originPx.Y + local.Y * _scale,
        local.Width * _scale,
        local.Height * _scale);

    // ------------------------------------------------------------------
    // 編集モードのマウス操作
    // ------------------------------------------------------------------

    private MaskVisual? HitTestManual(Point p, out bool onResizeHandle)
    {
        onResizeHandle = false;
        if (_state is null) return null;
        for (int i = _state.Visuals.Count - 1; i >= 0; i--)
        {
            var v = _state.Visuals[i];
            if (v.Kind != MaskKind.Manual || v.ManualId is null) continue;
            var local = ToLocal(v.ScreenRect);
            if (!local.Contains(p)) continue;
            onResizeHandle = p.X >= local.Right - ResizeHandleDip && p.Y >= local.Bottom - ResizeHandleDip;
            return v;
        }
        return null;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_editMode || _state is null || _state.Panic) return;
        var p = e.GetPosition(_canvas);
        var hit = HitTestManual(p, out var onHandle);
        if (hit is not null)
        {
            _drag = onHandle ? DragMode.Resize : DragMode.Move;
            _dragId = hit.ManualId!.Value;
            _dragOriginalLocal = ToLocal(hit.ScreenRect);
        }
        else
        {
            _drag = DragMode.Create;
            _dragOriginalLocal = new Rect(p, p);
        }
        _dragStart = p;
        _rubber = new Rectangle
        {
            Stroke = Brushes.Gold,
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 2 },
            Fill = new SolidColorBrush(Color.FromArgb(0x80, 0x2B, 0x2B, 0x2B)),
            IsHitTestVisible = false,
        };
        UpdateRubber(p);
        _canvas.Children.Add(_rubber);
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_drag == DragMode.None) return;
        UpdateRubber(e.GetPosition(_canvas));
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == DragMode.None) return;
        var rect = CurrentDragRect(e.GetPosition(_canvas));
        var mode = _drag;
        var id = _dragId;
        CancelDrag();

        var screen = ToScreen(rect);
        if (screen.Width < MinMaskSizePx || screen.Height < MinMaskSizePx)
        {
            if (_state is not null) Render(_state);
            return;
        }
        if (mode == DragMode.Create) ManualMaskCreated?.Invoke(screen);
        else ManualMaskChanged?.Invoke(id, screen);
        e.Handled = true;
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_editMode || _drag != DragMode.None) return;
        var hit = HitTestManual(e.GetPosition(_canvas), out _);
        if (hit?.ManualId is Guid id)
        {
            ManualMaskDeleted?.Invoke(id);
            e.Handled = true;
        }
    }

    private Rect CurrentDragRect(Point p)
    {
        var dx = p.X - _dragStart.X;
        var dy = p.Y - _dragStart.Y;
        var o = _dragOriginalLocal;
        return _drag switch
        {
            DragMode.Create => new Rect(_dragStart, p), // Rect(Point, Point) は向きを正規化する
            DragMode.Move => new Rect(o.X + dx, o.Y + dy, o.Width, o.Height),
            DragMode.Resize => new Rect(o.X, o.Y, Math.Max(4, o.Width + dx), Math.Max(4, o.Height + dy)),
            _ => o,
        };
    }

    private void UpdateRubber(Point p)
    {
        if (_rubber is null) return;
        var r = CurrentDragRect(p);
        Canvas.SetLeft(_rubber, r.X);
        Canvas.SetTop(_rubber, r.Y);
        _rubber.Width = r.Width;
        _rubber.Height = r.Height;
    }

    private void CancelDrag()
    {
        if (_canvas.IsMouseCaptured) _canvas.ReleaseMouseCapture();
        if (_rubber is not null) _canvas.Children.Remove(_rubber);
        _rubber = null;
        _drag = DragMode.None;
    }
}
