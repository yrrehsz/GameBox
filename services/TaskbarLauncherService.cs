using System;
using System.Diagnostics;
using System.IO;
using GameStartMenu.Library;

namespace GameStartMenu.Services
{
    /// <summary>
    /// Generates and compiles the lightweight taskbar launcher executable inside
    /// <see cref="AppConfig.AppDataFolder"/> (alongside the JSON configuration files)
    /// so it can be pinned to the Windows Taskbar to toggle or launch GameStartMenu.
    /// </summary>
    internal static class TaskbarLauncherService
    {
        private const string CompilerPath = @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe";
        private const string OutputExeName = "GameBox.exe";
        private const string TempSourceName = "GameBox.cs";

        public static string LauncherExePath => Path.Combine(AppConfig.AppDataFolder, OutputExeName);

        /// <summary>
        /// Ensures the taskbar shim executable exists and is newer than the main executable,
        /// silently recompiling it if needed without opening Windows Explorer.
        /// </summary>
        public static void TryEnsureLauncherBuilt()
        {
            try
            {
                if (!File.Exists(CompilerPath)) return;

                string mainExePath = WindowsStartupService.GetExecutablePath();
                bool needsBuild = !File.Exists(LauncherExePath) ||
                                  (File.Exists(mainExePath) &&
                                   File.GetLastWriteTimeUtc(LauncherExePath) < File.GetLastWriteTimeUtc(mainExePath));

                if (needsBuild)
                {
                    BuildLauncher();
                }
            }
            catch
            {
                // Non-fatal at startup; explicit pin action surfaces any compiler errors.
            }
        }

        /// <summary>
        /// Compiles the taskbar shim executable into <see cref="AppConfig.AppDataFolder"/>
        /// and selects it in Windows Explorer for pinning.
        /// </summary>
        public static void CreateAndRevealLauncher()
        {
            BuildLauncher();
            Process.Start("explorer.exe", $"/select,\"{LauncherExePath}\"");
        }

        private static void BuildLauncher()
        {
            if (!File.Exists(CompilerPath))
            {
                throw new FileNotFoundException($"C# compiler not found at '{CompilerPath}'.", CompilerPath);
            }

            Directory.CreateDirectory(AppConfig.AppDataFolder);

            string tempCsPath = Path.Combine(Path.GetTempPath(), TempSourceName);
            string currentExePath = WindowsStartupService.GetExecutablePath();
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
                    Arguments = $"/target:winexe {iconArg}/out:\"{LauncherExePath}\" \"{tempCsPath}\"",
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
