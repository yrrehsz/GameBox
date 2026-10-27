using System;
using System.Threading;
using System.Windows;
using Wpf.Ui.Appearance;

namespace GameStartMenu
{
    public partial class App : Application
    {
        private static Mutex? _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Se viene chiamato con "--toggle" (dal nostro bottone sulla Taskbar),
            // manda il segnale di Apri/Chiudi e muore in 1 millisecondo -> ZERO LINEETTA!
            if (e.Args.Length > 0 && e.Args[0] == "--toggle")
            {
                if (EventWaitHandle.TryOpenExisting("GameStartMenu_ToggleSignal", out var signal))
                {
                    signal.Set();
                }
                Environment.Exit(0);
                return;
            }

            // Se avvii una nuova istanza da VS Code mentre la vecchia è ancora aperta in background,
            // chiudiamo automaticamente la vecchia istanza!
            if (EventWaitHandle.TryOpenExisting("GameStartMenu_QuitSignal", out var quitSignal))
            {
                quitSignal.Set();
                Thread.Sleep(200); // Aspetta che la vecchia istanza rilasci i file
            }

            _mutex = new Mutex(true, "GameStartMenu_SingleInstanceMutex", out _);

            // Forza il tema Dark
            //ApplicationThemeManager.Apply(ApplicationTheme.Dark, Wpf.Ui.Controls.WindowBackdropType.Mica, true);

            base.OnStartup(e);
        }
    }
}