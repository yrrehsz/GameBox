using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GameStartMenu.Services
{
    /// <summary>
    /// Provides Win32 interop helpers for DWM acrylic blur, rounded corners,
    /// foreground focus unlocking, DPI-aware cursor queries, and taskbar-anchored window placement.
    /// </summary>
    internal static class Win32WindowHelper
    {
        private const byte VkMenu = 0x12;
        private const uint KeyEventExtendedKey = 0x0001;
        private const uint KeyEventKeyUp = 0x0002;
        private const int VkLeftButton = 0x01;
        private const int VkRightButton = 0x02;

        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwcpRound = 2;
        private const int WcaAccentPolicy = 19;
        private const int AccentEnableAcrylicBlurBehind = 4;
        private const int TaskbarEdgeMargin = 12;
        private const int TaskbarCursorZoneHeight = 40;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public uint GradientColor;
            public int AnimationId;
        }

        /// <summary>
        /// Enables Windows 11 hardware rounded corners and translucent acrylic blur on the specified window.
        /// </summary>
        public static void EnableAcrylicBlur(Window window)
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            int cornerPreference = DwmwcpRound;
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));

            // IMPORTANT: The alpha byte of GradientColor must never be 0x00,
            // otherwise Windows 11 renders a solid grey fallback instead of acrylic blur.
            var accent = new AccentPolicy
            {
                AccentState = AccentEnableAcrylicBlurBehind,
                AccentFlags = 2,
                GradientColor = 0x22181818
            };

            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);

                var data = new WindowCompositionAttributeData
                {
                    Attribute = WcaAccentPolicy,
                    SizeOfData = size,
                    Data = ptr
                };

                SetWindowCompositionAttribute(handle, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>
        /// Forces the window into the foreground, bypassing Windows foreground lock
        /// when activated from non-window input such as a gamepad shortcut.
        /// </summary>
        public static void ForceForegroundFocus(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            // Send an invisible ALT key impulse so Windows unlocks SetForegroundWindow.
            keybd_event(VkMenu, 0, KeyEventExtendedKey, UIntPtr.Zero);
            keybd_event(VkMenu, 0, KeyEventExtendedKey | KeyEventKeyUp, UIntPtr.Zero);

            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);

            window.Topmost = true;
            window.Activate();
            window.Focus();
        }

        /// <summary>
        /// Returns the current cursor position converted to device-independent pixels (DIPs)
        /// for the given visual's DPI scale, or null if the cursor position cannot be queried.
        /// </summary>
        public static Point? GetCursorDipPosition(Visual visual)
        {
            if (!GetCursorPos(out POINT pt)) return null;

            var source = PresentationSource.FromVisual(visual);
            double dpiX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
            double dpiY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

            return new Point(pt.X * dpiX, pt.Y * dpiY);
        }

        /// <summary>
        /// Positions the window along the bottom edge of the work area, centered above the taskbar cursor
        /// position when invoked from the taskbar, or clamped to the last known horizontal anchor.
        /// </summary>
        public static void PositionNearTaskbar(Window window, ref double anchorX)
        {
            Rect workArea = SystemParameters.WorkArea;
            Point? cursor = GetCursorDipPosition(window);

            if (cursor.HasValue && cursor.Value.Y >= workArea.Bottom - TaskbarCursorZoneHeight)
            {
                anchorX = cursor.Value.X - (window.Width / 2);
            }
            else if (anchorX < 0)
            {
                anchorX = workArea.Left + TaskbarEdgeMargin;
            }

            window.Left = Math.Max(
                workArea.Left + TaskbarEdgeMargin,
                Math.Min(anchorX, workArea.Right - window.Width - TaskbarEdgeMargin));

            window.Top = workArea.Bottom - window.Height - TaskbarEdgeMargin;
        }

        /// <summary>
        /// Checks whether the left or right physical mouse button is currently pressed.
        /// </summary>
        public static bool IsMouseButtonDown()
        {
            return (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0 ||
                   (GetAsyncKeyState(VkRightButton) & 0x8000) != 0;
        }

        /// <summary>
        /// Returns true if the cursor position can be read and falls outside the window bounds.
        /// </summary>
        public static bool IsCursorOutsideWindow(Window window)
        {
            Point? cursor = GetCursorDipPosition(window);
            if (!cursor.HasValue) return false;

            return cursor.Value.X < window.Left ||
                   cursor.Value.X > window.Left + window.Width ||
                   cursor.Value.Y < window.Top ||
                   cursor.Value.Y > window.Top + window.Height;
        }
    }
}
