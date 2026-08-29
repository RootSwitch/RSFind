// The Win32 surface RSFind needs, and nothing else.
//
// Deliberately not a copy of RSPaster's Native class. That one carries the
// keyboard injection and process-integrity machinery a keystroke sender needs,
// none of which belongs in a search tool - a file searcher that declares
// SendInput reads badly to anyone who checks, and here it would be dead code
// besides.
//
// C# 5 only (in-box csc).

using System;
using System.Runtime.InteropServices;

namespace RSFind
{
    internal static class Native
    {
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        // Dark title bar and dark scrollbars. Neither can be given an
        // arbitrary color: Windows offers a dark variant and a light one, so
        // each palette picks whichever side it sits on.
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Both are undocumented ordinals in uxtheme. They are the only way to
        // get dark scrollbars on .NET Framework, and they are absent before
        // Windows 10 1809 - hence the EntryPointNotFoundException catches at
        // every call site rather than a version check.
        [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
        public static extern int SetPreferredAppMode(int mode);

        [DllImport("uxtheme.dll", EntryPoint = "#133", SetLastError = true)]
        public static extern bool AllowDarkModeForWindow(IntPtr hwnd, bool allow);

        public const int WM_THEMECHANGED = 0x031A;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public const uint RDW_INVALIDATE = 0x0001;
        public const uint RDW_FRAME = 0x0400;
        public const uint RDW_UPDATENOW = 0x0100;

        [DllImport("user32.dll")]
        public static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprc, IntPtr hrgn, uint flags);
    }
}
