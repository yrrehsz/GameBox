using System;
using System.Threading;
using System.Threading.Tasks;

namespace GameStartMenu.Services
{
    /// <summary>
    /// Listens on named Win32 <see cref="EventWaitHandle"/> IPC signals for single-instance toggle
    /// and graceful shutdown notifications from the taskbar launcher or build scripts.
    /// </summary>
    internal sealed class IpcSignalListener : IDisposable
    {
        public const string ToggleSignalName = "GameStartMenu_ToggleSignal";
        public const string QuitSignalName = "GameStartMenu_QuitSignal";

        private readonly Action _onToggleRequested;
        private readonly Action _onQuitRequested;
        private readonly CancellationTokenSource _disposeCts = new();
        private bool _started;

        public IpcSignalListener(Action onToggleRequested, Action onQuitRequested)
        {
            _onToggleRequested = onToggleRequested ?? throw new ArgumentNullException(nameof(onToggleRequested));
            _onQuitRequested = onQuitRequested ?? throw new ArgumentNullException(nameof(onQuitRequested));
        }

        /// <summary>
        /// Starts background listener tasks for the toggle and quit named events.
        /// </summary>
        public void Start()
        {
            if (_started) return;
            _started = true;

            _ = Task.Run(() => ListenLoop(ToggleSignalName, _onToggleRequested, repeat: true, _disposeCts.Token));
            _ = Task.Run(() => ListenLoop(QuitSignalName, _onQuitRequested, repeat: false, _disposeCts.Token));
        }

        private static void ListenLoop(string signalName, Action callback, bool repeat, CancellationToken token)
        {
            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, signalName);
            WaitHandle[] waitHandles = { signal, token.WaitHandle };

            while (!token.IsCancellationRequested)
            {
                int index = WaitHandle.WaitAny(waitHandles);
                if (index != 0) break;

                callback();

                if (!repeat) break;
            }
        }

        public void Dispose()
        {
            _disposeCts.Cancel();
            _disposeCts.Dispose();
        }
    }
}
