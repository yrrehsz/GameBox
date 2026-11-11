using System;
using System.Diagnostics;
using System.IO;

namespace GameStartMenu.Services
{
    /// <summary>
    /// Generates and compiles a lightweight Windows executable in the user's Documents folder
    /// so it can be pinned to the Windows Taskbar to toggle or launch GameStartMenu.
    /// </summary>
    internal static class TaskbarLauncherService
    {
        private const string CompilerPath = @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe";
        private const string OutputExeName = "LauncherGiochi.exe";
        private const string TempSourceName = "LauncherGiochi.cs";
        private const string MainExeName = "GameStartMenu.exe";

        /// <summary>
        /// Compiles the taskbar shim executable into My Documents and selects it in Windows Explorer.
        /// </summary>
        public static void CreateAndRevealLauncher()
        {
            if (!File.Exists(CompilerPath))
            {
                throw new FileNotFoundException($"C# compiler not found at '{CompilerPath}'.", CompilerPath);
            }

            string docsDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string launcherPath = Path.Combine(docsDir, OutputExeName);
            string tempCsPath = Path.Combine(Path.GetTempPath(), TempSourceName);
            string currentExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, MainExeName);
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "app", "app_icon.ico");

            string sourceCode = BuildLauncherSource(currentExePath, IpcSignalListener.ToggleSignalName);

            try
            {
                File.WriteAllText(tempCsPath, sourceCode);

                // Embed the application icon inside the generated executable when available.
                string iconArg = File.Exists(iconPath) ? $"/win32icon:\"{iconPath}\" " : string.Empty;

                using Process? compiler = Process.Start(new ProcessStartInfo
                {
                    FileName = CompilerPath,
                    Arguments = $"/target:winexe {iconArg}/out:\"{launcherPath}\" \"{tempCsPath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });

                compiler?.WaitForExit();
                if (compiler == null || compiler.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Failed to compile taskbar launcher (exit code: {compiler?.ExitCode}).");
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(tempCsPath)) File.Delete(tempCsPath);
                }
                catch
                {
                    // Ignore cleanup failures for temporary build source files.
                }
            }

            Process.Start("explorer.exe", $"/select,\"{launcherPath}\"");
        }

        private static string BuildLauncherSource(string mainExePath, string toggleSignalName)
        {
            return $@"
using System.Threading;
using System.Diagnostics;
class Program {{
    static void Main() {{
        EventWaitHandle s;
        if (EventWaitHandle.TryOpenExisting(""{toggleSignalName}"", out s)) {{
            s.Set();
        }} else {{
            Process.Start(new ProcessStartInfo {{ FileName = @""{mainExePath}"", UseShellExecute = true }});
        }}
    }}
}}";
        }
    }
}
