using System;
using System.IO;
using Microsoft.Win32;

namespace GameStartMenu.Services
{
    /// <summary>
    /// Manages the per-user Windows Registry Run entry so GameStartMenu starts silently
    /// in the background when signing in to Windows.
    /// </summary>
    internal static class WindowsStartupService
    {
        private const string RunRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppValueName = "GameStartMenu";
        public const string BackgroundLaunchArg = "--background";

        /// <summary>
        /// Resolves the path to the running GameStartMenu executable.
        /// </summary>
        public static string GetExecutablePath()
        {
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath) &&
                processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(processPath))
            {
                return processPath;
            }

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameStartMenu.exe");
        }

        /// <summary>
        /// Enables or disables automatic background launch on Windows sign-in.
        /// </summary>
        public static void SetStartupEnabled(bool enabled)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryKeyPath, writable: true)
                                  ?? Registry.CurrentUser.CreateSubKey(RunRegistryKeyPath, writable: true);
            if (key == null) return;

            if (enabled)
            {
                string command = $"\"{GetExecutablePath()}\" {BackgroundLaunchArg}";
                key.SetValue(AppValueName, command, RegistryValueKind.String);
            }
            else if (key.GetValue(AppValueName) != null)
            {
                key.DeleteValue(AppValueName, throwOnMissingValue: false);
            }
        }

        /// <summary>
        /// Returns true if the per-user Windows Run entry is currently configured.
        /// </summary>
        public static bool IsStartupEnabled()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryKeyPath, writable: false);
            return key?.GetValue(AppValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
    }
}
