using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using StageMask.App.Native;
using StageMask.App.Services;
using StageMask.App.Views;
using StageMask.Core.Settings;

namespace StageMask.App;

/// <summary>操作パネル。既定で画面共有・録画に映らない(WDA_EXCLUDEFROMCAPTURE)。</summary>
internal partial class MainWindow : Window
{
    private readonly MaskController _controller;
    private readonly DispatcherTimer _statusTimer;
    private readonly ResourceMonitor _resources = new();
    private readonly DiagnosticsRecorder _diagnostics;
    private HotkeyManager? _hotkeys;
    /// <summary>最後に適用できたホットキー(不正な値を入れて適用したときに戻す先)</summary>
    private HotkeySettings _lastGoodHotkeys = new();
    private bool _confirmedClose;

    public MainWindow(MaskController controller)
    {
        _controller = controller;
        InitializeComponent();
        Title = $"StageMask v{AppInfo.Version} — 登壇用マスク";
        DataContext = controller.Settings;

        RuleList.ItemsSource = controller.Settings.WindowRules;
        ManualList.ItemsSource = controller.Settings.ManualMasks;
        NgWordsBox.Text = string.Join(Environment.NewLine, controller.Settings.Detection.NgWords);
        UpdateNgWordsSummary();
        RefreshMonitorChoices();

        controller.StateChanged += (_, _) =>
        {
            SyncToggles();
            // ミラーのウィンドウを閉じて OFF になったときに、チェックを外す
            if (MirrorEnabledBox.IsChecked != controller.Settings.ShareMirror.Enabled)
                MirrorEnabledBox.IsChecked = controller.Settings.ShareMirror.Enabled;
        };
        controller.ManualMasksChanged += (_, _) => ManualList.Items.Refresh();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _diagnostics = new DiagnosticsRecorder(controller, _resources);
        _statusTimer.Tick += (_, _) =>
        {
            UpdateStatus();
            _diagnostics.Tick();
            DiagnosticsPathText.Text = _diagnostics.CurrentPath is { } p && _controller.Settings.Diagnostics.Enabled
                ? "記録中: " + System.IO.Path.GetFileName(p) : "";
        };
        _statusTimer.Start();

        // パネルを操作している間は、マスクより前に出す(マスクの下に隠れて操作できなくなるのを防ぐ)
        Activated += (_, _) => KeepAboveMasks(true);
        // パネルから離れたら通常の重なり順に戻し、機密情報を含みうる表示(NGワード・ウィンドウ一覧)を隠す。
        // ただし、このアプリのダイアログ(ファイル選択など)に移っただけなら、そのままにする
        Deactivated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(OnFocusMaybeLeftApp));
        SyncToggles();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyCaptureExclusion();
        _hotkeys = new HotkeyManager(this);
        // 設定ファイルに不正な値が入っていた場合は既定値で登録する
        var startupReverted = RevertInvalidHotkeys(new HotkeySettings());
        if (startupReverted.Count > 0) _controller.Save();
        RegisterHotkeys(startupReverted);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_confirmedClose && (_controller.MasksEnabled || _controller.Panic))
        {
            var r = MessageBox.Show(this,
                "StageMask を終了すると、すべてのマスクが消えます。終了しますか?",
                "StageMask", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        _confirmedClose = true;
        _statusTimer.Stop();
        _resources.Dispose();
        _hotkeys?.Dispose();
        base.OnClosing(e);
    }

    // ------------------------------------------------------------------
    // 上部の操作
    // ------------------------------------------------------------------

    private void MasksToggle_Click(object sender, RoutedEventArgs e) => _controller.SetMasksEnabled(MasksToggle.IsChecked == true);
    private void DetectToggle_Click(object sender, RoutedEventArgs e) => _controller.SetDetectionEnabled(DetectToggle.IsChecked == true);
    private void EditToggle_Click(object sender, RoutedEventArgs e) => _controller.SetEditMode(EditToggle.IsChecked == true);
    private void PanicToggle_Click(object sender, RoutedEventArgs e) => _controller.SetPanic(PanicToggle.IsChecked == true);

    private void SyncToggles()
    {
        MasksToggle.IsChecked = _controller.MasksEnabled;
        MasksToggle.Content = _controller.MasksEnabled ? "マスク: ON" : "マスク: OFF";
        DetectToggle.IsChecked = _controller.Settings.Detection.Enabled;
        DetectToggle.Content = _controller.Settings.Detection.Enabled ? "自動検出: ON" : "自動検出: OFF";
        EditToggle.IsChecked = _controller.EditMode;
        EditToggle.Content = _controller.EditMode ? "マスク編集中…" : "マスク編集";
        PanicToggle.IsChecked = _controller.Panic;
        PanicToggle.Content = _controller.Panic ? "パニック解除" : "パニック(全画面を隠す)";
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        _resources.Sample();
        ResourceText.Text =
            $"StageMask の使用量  CPU {_resources.CpuPercent:0.0}%  |  メモリ 物理 {_resources.WorkingSetMb:0} MB / 専用 {_resources.PrivateMb:0} MB  |  スレッド {_resources.Threads}";

        var parts = new List<string>
        {
            $"画面: {_controller.OverlayCount}台",
            $"全体を隠しているウィンドウ: {_controller.HiddenWindowCount}",
            $"OCR対象: {_controller.DetectTargetCount}",
            $"検出マスク: {_controller.DetectionMaskCount}",
            $"OCR: {_controller.OcrStatus}",
            $"キャプチャ: {_controller.CaptureStatus}",
        };
        if (_controller.DetectTargetCount > 0)
        {
            parts.Add($"OCR1周 {(int)_controller.WorkerCycle.TotalMilliseconds}ms");
            parts.Add($"変化確認 {(int)_controller.CheckRound.TotalMilliseconds}ms");
            parts.Add($"未検査を隠している箇所: {_controller.PendingMaskCount}");
        }
        if (_controller.ScrollMaskCount > 0) parts.Add($"スクロール中で隠しているウィンドウ: {_controller.ScrollMaskCount}");
        if (_controller.Settings.Detection.MaskOnWheel && _controller.Settings.Detection.Enabled && !_controller.WheelMonitorRunning) parts.Add("⚠ ホイール検知を開始できませんでした");
        if (_controller.FailClosedCount > 0)
            parts.Add($"🛡 検査できていないため全体を隠しているウィンドウ: {_controller.FailClosedCount}({_controller.FailClosedReason})");
        if (_controller.ShareMirrorStatus is { } mirror) parts.Add("🪞 " + mirror);
        if (_controller.Settings.Diagnostics.Enabled) parts.Add("📝 診断ログ記録中");
        var error = _controller.WorkerError ?? _controller.LastError ?? _controller.ContentImageError ?? _controller.StoreWarning ?? _diagnostics?.Error;
        if (!string.IsNullOrEmpty(error)) parts.Add("⚠ " + error);
        StatusText.Text = string.Join("  |  ", parts);
    }

    // ------------------------------------------------------------------
    // ウィンドウ・ルール
    // ------------------------------------------------------------------

    private void RefreshWindows_Click(object sender, RoutedEventArgs e) => RefreshWindowList();

    private void RefreshWindowList()
    {
        var list = WindowEnumerator.GetWindows()
            .Where(w => !string.IsNullOrWhiteSpace(w.Title) && !w.IsToolWindow)
            .OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Title, StringComparer.CurrentCulture)
            .ToList();
        WindowList.ItemsSource = list;
    }

    private void AddHideRule_Click(object sender, RoutedEventArgs e) => AddRuleFromSelection(RuleAction.HideWindow);
    private void AddDetectRule_Click(object sender, RoutedEventArgs e) => AddRuleFromSelection(RuleAction.AutoDetect);

    private void AddRuleFromSelection(RuleAction action)
    {
        if (WindowList.SelectedItem is not WindowInfo w)
        {
            MessageBox.Show(this, "上の一覧からウィンドウを選択してください。", "StageMask");
            return;
        }
        var title = UseTitleFilter.IsChecked == true ? w.Title : "";
        var rules = _controller.Settings.WindowRules;
        if (rules.Any(r => r.Action == action &&
                           string.Equals(r.ProcessName, w.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(r.TitleContains, title, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "同じルールがすでにあります。", "StageMask");
            return;
        }
        rules.Add(new WindowRule
        {
            ProcessName = w.ProcessName,
            TitleContains = title,
            Action = action,
            Style = action == RuleAction.AutoDetect ? _controller.Settings.Detection.Style : MaskStyle.Solid,
        });
        RuleList.Items.Refresh();
        _controller.Save();
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not WindowRule rule) return;
        _controller.Settings.WindowRules.Remove(rule);
        RuleList.Items.Refresh();
        _controller.Save();
    }

    private void RuleChanged(object sender, RoutedEventArgs e) => _controller.Save();

    // ------------------------------------------------------------------
    // 手動マスク
    // ------------------------------------------------------------------

    private void ManualChanged(object sender, RoutedEventArgs e) => _controller.OnManualMasksEdited(listChanged: false);

    private void DeleteManual_Click(object sender, RoutedEventArgs e)
    {
        if (ManualList.SelectedItem is ManualMask m) _controller.RemoveManualMask(m.Id);
    }

    private void ClearManual_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.Settings.ManualMasks.Count == 0) return;
        if (MessageBox.Show(this, "手動マスクをすべて削除しますか?", "StageMask",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _controller.ClearManualMasks();
    }

    // ------------------------------------------------------------------
    // 自動検出・表示・ホットキー
    // ------------------------------------------------------------------

    private void NgWordsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (NgWordsBox.Visibility == Visibility.Visible) HideNgWords();
        else
        {
            NgWordsBox.Visibility = Visibility.Visible;
            NgWordsToggle.Content = "隠す";
            NgWordsBox.Focus();
        }
    }

    private void HideNgWords()
    {
        NgWordsBox.Visibility = Visibility.Collapsed;
        NgWordsToggle.Content = "表示して編集";
        UpdateNgWordsSummary();
    }

    private void HideSensitiveContent()
    {
        HideNgWords();
        WindowList.ItemsSource = null;
    }

    private void UpdateNgWordsSummary()
    {
        int n = _controller.Settings.Detection.NgWords.Count;
        int pending = ParseNgWords().Count;
        NgWordsSummary.Text = pending == n
            ? $"登録済み: {n}件(内容は非表示)"
            : $"登録済み: {n}件 / 未適用の変更あり({pending}件)";
    }

    private List<string> ParseNgWords() => NgWordsBox.Text
        .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
        .Select(s => s.Trim())
        .Where(s => s.Length > 0)
        .Distinct()
        .ToList();

    private void ApplyDetection_Click(object sender, RoutedEventArgs e)
    {
        _controller.Settings.Detection.NgWords = ParseNgWords();
        _controller.ApplySettings();
        HideNgWords();
        UpdateNgWordsSummary();
        SyncToggles();
        ShowSaved(DetectionSavedText, null);
    }

    /// <summary>「適用・保存」の結果をボタンの横に数秒間表示する。</summary>
    private void ShowSaved(TextBlock target, string? warning)
    {
        var saveError = _controller.LastError is string e && e.StartsWith("設定の保存に失敗", StringComparison.Ordinal) ? e : null;
        var time = DateTime.Now.ToString("HH:mm:ss");
        if (saveError is not null)
        {
            target.Text = $"⚠ 反映しましたが保存に失敗しました({time})";
            target.ClearValue(TextBlock.ForegroundProperty);
            target.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        }
        else if (warning is not null)
        {
            target.Text = $"⚠ 反映・保存しました({time}) {warning}";
            target.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
        }
        else
        {
            target.Text = $"✓ 反映・保存しました({time})";
            target.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorSuccessBrush");
        }

        // 数秒後に消す(次に押したときに、新しく表示されたことがわかるように)
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        var shown = target.Text;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (target.Text == shown) target.Text = "";
        };
        timer.Start();
    }

    private void DiagnosticsToggle_Click(object sender, RoutedEventArgs e)
    {
        _controller.Save();
    }

    private void OpenDiagnosticsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(DiagnosticsRecorder.DefaultDirectory);
            // エクスプローラーでフォルダーを開く(パスは固定の場所。ユーザー入力は渡さない)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { DiagnosticsRecorder.DefaultDirectory },
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            Logger.Write("OpenDiagnosticsFolder", ex);
        }
    }

    // ------------------------------------------------------------------
    // 共有用ミラー
    // ------------------------------------------------------------------

    private sealed record MonitorChoice(string Device, string Name);

    private void RefreshMonitorChoices()
    {
        var monitors = MonitorEnumerator.GetMonitors();
        var sources = new List<MonitorChoice> { new("", "メインのモニター") };
        sources.AddRange(monitors.Select(m => new MonitorChoice(m.DeviceName, m.DisplayName)));
        var targets = new List<MonitorChoice> { new("", "通常のウィンドウで表示(OBS のウィンドウキャプチャ・Teams のウィンドウ共有向け)") };
        targets.AddRange(monitors.Select(m => new MonitorChoice(m.DeviceName, "全画面で表示: " + m.DisplayName)));

        var cfg = _controller.Settings.ShareMirror;
        // 保存したモニターが今は接続されていなくても、選択が消えないように一覧に残す
        if (!string.IsNullOrEmpty(cfg.SourceMonitor) && sources.All(c => c.Device != cfg.SourceMonitor))
            sources.Add(new(cfg.SourceMonitor, cfg.SourceMonitor + "(接続されていません)"));
        if (!string.IsNullOrEmpty(cfg.MirrorMonitor) && targets.All(c => c.Device != cfg.MirrorMonitor))
            targets.Add(new(cfg.MirrorMonitor, cfg.MirrorMonitor + "(接続されていません)"));
        var areas = new List<MonitorChoice> { new("", "すべてのモニター") };
        areas.AddRange(monitors.Select(m => new MonitorChoice(m.DeviceName, m.DisplayName)));
        var tm = _controller.Settings.TargetMonitor;
        if (!string.IsNullOrEmpty(tm) && areas.All(c => c.Device != tm))
            areas.Add(new(tm, tm + "(接続されていません)"));
        TargetMonitorBox.ItemsSource = areas;
        TargetMonitorBox.SelectedValue = tm;
        MirrorSourceBox.ItemsSource = sources;
        MirrorTargetBox.ItemsSource = targets;
        MirrorSourceBox.SelectedValue = cfg.SourceMonitor;
        MirrorTargetBox.SelectedValue = cfg.MirrorMonitor;
    }

    private void RefreshMonitors_Click(object sender, RoutedEventArgs e) => RefreshMonitorChoices();

    private void ApplyMirror_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _controller.Settings.ShareMirror;
        cfg.MaxFps = Math.Clamp(cfg.MaxFps, 1, 60);
        string? warning = null;
        if (cfg.Enabled && !string.IsNullOrEmpty(cfg.MirrorMonitor) &&
            string.Equals(cfg.MirrorMonitor, string.IsNullOrEmpty(cfg.SourceMonitor)
                ? MonitorEnumerator.Find(MonitorEnumerator.GetMonitors(), "")?.DeviceName : cfg.SourceMonitor, StringComparison.OrdinalIgnoreCase))
            warning = "取り込み元と同じモニターには全画面で表示できないため、通常のウィンドウで表示します";
        _controller.ApplySettings();
        ShowSaved(MirrorSavedText, warning);
    }

    private void ApplyAppearance_Click(object sender, RoutedEventArgs e)
    {
        // 解釈できない・重複したホットキーは保存せず、前回まで使えていた値に戻す
        var reverted = RevertInvalidHotkeys(_lastGoodHotkeys);
        _controller.ApplySettings();
        ApplyCaptureExclusion();
        RegisterHotkeys(reverted);
        string? warning = null;
        if (reverted.Count > 0) warning = "正しくないホットキーは保存せず、以前の値に戻しました";
        else if (!string.IsNullOrEmpty(HotkeyErrorText.Text)) warning = "一部のホットキーは登録できませんでした";
        else if (_controller.LastError?.StartsWith("塗りつぶし色", StringComparison.Ordinal) == true) warning = "塗りつぶし色を解釈できないため黒を使います";
        ShowSaved(AppearanceSavedText, warning);
    }

    /// <summary>
    /// 不正なホットキーを lastGood の値に戻し、入力欄も戻した値で表示し直す。戻した内容の説明を返す。
    /// </summary>
    private List<string> RevertInvalidHotkeys(HotkeySettings lastGood)
    {
        var h = _controller.Settings.Hotkeys;
        var messages = HotkeySettingsValidator.RevertInvalid(h, lastGood, HotkeyManager.Validate);
        if (messages.Count > 0)
        {
            // HotkeySettings は変更通知を持たないため、入力欄を結び直して表示を更新する
            HotkeyGroup.DataContext = null;
            HotkeyGroup.DataContext = h;
        }
        _lastGoodHotkeys = HotkeySettingsValidator.Copy(h);
        return messages;
    }

    private void RegisterHotkeys(List<string>? reverted = null)
    {
        if (_hotkeys is null) return;
        _hotkeys.UnregisterAll();
        var h = _controller.Settings.Hotkeys;
        var errors = new List<string>(reverted ?? new List<string>());
        void Reg(string text, Action action)
        {
            var err = _hotkeys.Register(text, action);
            if (err is not null) errors.Add(err);
        }
        Reg(h.ToggleMasks, () => _controller.SetMasksEnabled(!_controller.MasksEnabled));
        Reg(h.Panic, () => _controller.SetPanic(!_controller.Panic));
        Reg(h.EditMode, () => _controller.SetEditMode(!_controller.EditMode));
        Reg(h.ToggleDetection, () => _controller.SetDetectionEnabled(!_controller.Settings.Detection.Enabled));
        Reg(h.ShowPanel, ShowPanel);

        HotkeyErrorText.Text = string.Join(Environment.NewLine, errors);
        HotkeyText.Text =
            $"ホットキー  マスクON/OFF: {h.ToggleMasks}   パニック: {h.Panic}   マスク編集: {h.EditMode}   自動検出ON/OFF: {h.ToggleDetection}   パネルを出す: {h.ShowPanel}" +
            (_controller.Settings.Appearance.HidePanelFromCapture ? "   ※このパネルは画面共有に映りません" : "");
    }

    /// <summary>フォーカスがこのアプリの外に移ったら、パネルを通常の重なり順に戻し、機密情報を隠す。</summary>
    private void OnFocusMaybeLeftApp()
    {
        var fg = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(fg, out var pid);
        if (fg != IntPtr.Zero && pid == (uint)Environment.ProcessId) return;
        KeepAboveMasks(false);
        HideSensitiveContent();
    }

    /// <summary>マスク上の文字・画像を設定するダイアログを開く。OKなら true。</summary>
    private bool EditContent(MaskContentOverride current, string targetName, out MaskContentOverride result)
    {
        var dlg = new ContentOverrideDialog(current, targetName, _controller.Settings.Appearance.HidePanelFromCapture) { Owner = this };
        dlg.Activated += (_, _) => KeepAboveMasks(true);
        dlg.Deactivated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(OnFocusMaybeLeftApp));
        bool ok = dlg.ShowDialog() == true;
        result = dlg.Result;
        return ok;
    }

    private void RuleContent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not WindowRule rule) return;
        var name = string.IsNullOrWhiteSpace(rule.TitleContains) ? rule.ProcessName : $"{rule.ProcessName} / {rule.TitleContains}";
        if (!EditContent(rule.Content, name, out var result)) return;
        rule.Content = result;
        _controller.Save();
    }

    private void ManualContent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ManualMask mask) return;
        if (!EditContent(mask.Content, mask.Label, out var result)) return;
        mask.Content = result;
        _controller.OnManualMasksEdited(listChanged: false);
    }

    private void BrowseLabelImage_Click(object sender, RoutedEventArgs e)
    {
        var path = ContentOverrideDialog.BrowseImage(this);
        if (path is not null) LabelImageBox.Text = path; // 「適用・保存」で反映
    }

    private void ClearLabelImage_Click(object sender, RoutedEventArgs e) => LabelImageBox.Text = "";

    private void KeepAboveMasks(bool keep)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (keep)
        {
            _controller.KeepAboveOverlays = hwnd;
            _controller.RaiseKeptWindow();
        }
        else
        {
            _controller.KeepAboveOverlays = IntPtr.Zero;
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    /// <summary>ホットキーでパネルを呼び出す(最小化・マスクの下に隠れている場合)。</summary>
    private void ShowPanel()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate(); // Activated イベントでマスクより前に出る
        KeepAboveMasks(true);
    }

    private void ApplyCaptureExclusion()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var affinity = _controller.Settings.Appearance.HidePanelFromCapture
            ? NativeMethods.WDA_EXCLUDEFROMCAPTURE
            : NativeMethods.WDA_NONE;
        NativeMethods.SetWindowDisplayAffinity(hwnd, affinity);
    }
}
