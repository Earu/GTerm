using System;
using System.Runtime.InteropServices;

namespace GTerm.Extensions
{
    internal class Win32Extensions
    {
        internal const int SW_MINIMIZE = 6;
        internal const int SW_HIDE = 0;

        internal delegate bool ConsoleEventDelegate(int eventType);

        [DllImport("Kernel32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        internal static extern IntPtr GetConsoleWindow();

        [DllImport("User32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow([In] IntPtr hWnd, [In] int nCmdShow);

        [DllImport("User32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible([In] IntPtr hWnd);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetConsoleCtrlHandler(ConsoleEventDelegate callback, bool add);

        internal enum WinMessages : uint
        {
            /// <summary>
            /// An application sends the WM_SETICON message to associate a new large or small icon with a window. 
            /// The system displays the large icon in the ALT+TAB dialog box, and the small icon in the window caption. 
            /// </summary>
            SETICON = 0x0080,
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, IntPtr lParam);

        private static void SetWindowIcon(System.Drawing.Icon icon)
        {
            IntPtr mwHandle = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            SendMessage(mwHandle, (int)WinMessages.SETICON, 0, icon.Handle);
            SendMessage(mwHandle, (int)WinMessages.SETICON, 1, icon.Handle);
        }

        internal static void SetConsoleIcon(string iconFilePath)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                if (!string.IsNullOrEmpty(iconFilePath))
                {
                    System.Drawing.Icon icon = System.Drawing.Icon.ExtractAssociatedIcon(iconFilePath);
                    SetWindowIcon(icon);
                }
            }
        }
    }
}
