using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using StageMask.App.Native;
using StageMask.Core.Settings;

namespace StageMask.App.Views;

/// <summary>マスク1つ(またはルール1つ)の、マスク上の文字・画像を設定するダイアログ。</summary>
internal sealed class ContentOverrideDialog : Window
{
    private readonly CheckBox _useCustom = new() { Content = "このマスクだけ個別に設定する(OFFなら「表示・ホットキー」タブの共通設定を使う)", Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBox _text = new() { Margin = new Thickness(0, 2, 0, 8) };
    private readonly TextBox _image = new() { Margin = new Thickness(0, 2, 6, 0) };
    private readonly bool _hideFromCapture;

    public MaskContentOverride Result { get; private set; }

    public ContentOverrideDialog(MaskContentOverride current, string targetName, bool hideFromCapture)
    {
        _hideFromCapture = hideFromCapture;
        Result = current;
        Title = $"マスクの表示 — {targetName}";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        // Fluent テーマの配色に合わせる(ダークモードでも読めるように)
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        _useCustom.IsChecked = current.UseCustom;
        _text.Text = current.Text;
        _image.Text = current.ImagePath;

        var browse = new Button { Content = "参照…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 2, 6, 0) };
        browse.Click += (_, _) =>
        {
            var path = BrowseImage(this);
            if (path is not null) _image.Text = path;
        };
        var clear = new Button { Content = "クリア", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 2, 0, 0) };
        clear.Click += (_, _) => _image.Text = "";

        var imageRow = new DockPanel();
        DockPanel.SetDock(clear, Dock.Right);
        DockPanel.SetDock(browse, Dock.Right);
        imageRow.Children.Add(clear);
        imageRow.Children.Add(browse);
        imageRow.Children.Add(_image);

        var ok = new Button { Content = "OK", IsDefault = true, Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) =>
        {
            Result = new MaskContentOverride
            {
                UseCustom = _useCustom.IsChecked == true,
                Text = _text.Text.Trim(),
                ImagePath = _image.Text.Trim().Trim('"'),
            };
            DialogResult = true;
        };
        var cancel = new Button { Content = "キャンセル", IsCancel = true, Width = 90 };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var fields = new StackPanel();
        fields.Children.Add(new TextBlock { Text = "表示する文字(空欄なら文字なし)" });
        fields.Children.Add(_text);
        fields.Children.Add(new TextBlock { Text = "表示する画像(空欄なら画像なし。マスク全体に敷き詰めて表示)" });
        fields.Children.Add(imageRow);
        var note = new TextBlock
        {
            Text = "画像は塗りつぶしのマスクにだけ表示します(モザイク/ぼかしでは文字だけ表示)。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        // 固定の灰色はダークモードで読みにくいため、テーマの「補足の文字」の色にする
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        fields.Children.Add(note);
        // 個別設定がOFFの間は入力欄を無効にする
        fields.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding(nameof(CheckBox.IsChecked)) { Source = _useCustom });

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(_useCustom);
        root.Children.Add(fields);
        root.Children.Add(buttons);
        Content = root;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_hideFromCapture)
            NativeMethods.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>画像ファイルを選ぶ。キャンセルなら null。</summary>
    public static string? BrowseImage(Window owner)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "マスクに表示する画像を選択",
            Filter = "画像ファイル|*.png;*.jpg;*.jpeg;*.bmp;*.gif|すべてのファイル|*.*",
            CheckFileExists = true,
        };
        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }
}
