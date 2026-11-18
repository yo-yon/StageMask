using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using StageMask.App.Native;
using StageMask.App.Services;
using StageMask.Core.Settings;

namespace StageMask.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private MaskController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(initiallyOwned: true, @"Local\StageMask.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("StageMask はすでに起動しています。", "StageMask", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // マスク表示中に落ちると隠したいものが見えてしまうため、例外はログに残して処理を続ける
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Write("UI", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Logger.Write("AppDomain", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Write("Task", args.Exception);
            args.SetObserved();
        };

        // NGワードは DPAPI で暗号化して保存する(同じPC・同じユーザーだけが復号できる)
        var store = new SettingsStore(SettingsStore.DefaultPath, new DpapiProtector());
        var settings = store.Load();
        _controller = new MaskController(store, settings, Dispatcher);

        var main = new MainWindow(_controller);
        this.MainWindow = main;
        main.Show();
        _controller.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _controller?.Save();
            _controller?.Dispose();
        }
        finally
        {
            if (_ownsMutex) _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
