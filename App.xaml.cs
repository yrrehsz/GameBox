using System;
using System.Linq;
using System.Threading;
using System.Windows;
using GameBox.Services;

namespace GameBox
{
    public partial class App : Application
    {
        private static Mutex? _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            // "--toggle" signals the already-running instance and exits immediately.
            if (e.Args.Any(arg => string.Equals(arg, "--toggle", StringComparison.OrdinalIgnoreCase)))
            {
                if (EventWaitHandle.TryOpenExisting(IpcSignalListener.ToggleSignalName, out var signal))
                {
                    signal.Set();
                }
                Environment.Exit(0);
                return;
            }

            // Ask any older background instance to exit cleanly before launching a new one.
            if (EventWaitHandle.TryOpenExisting(IpcSignalListener.QuitSignalName, out var quitSignal))
            {
                quitSignal.Set();
                Thread.Sleep(200);
            }
            if (EventWaitHandle.TryOpenExisting(IpcSignalListener.LegacyQuitSignalName, out var oldQuitSignal))
            {
                oldQuitSignal.Set();
                Thread.Sleep(200);
            }

            _mutex = new Mutex(true, "GameBox_SingleInstanceMutex", out _);

            base.OnStartup(e);

            bool startHidden = e.Args.Any(arg =>
                string.Equals(arg, WindowsStartupService.BackgroundLaunchArg, StringComparison.OrdinalIgnoreCase));

            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;

            if (!startHidden)
            {
                mainWindow.Show();
            }
        }
    }
}
