using System.ComponentModel;
using System.Diagnostics;
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
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            IntPtr mwHandle = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            SendMessage(mwHandle, (int)WinMessages.SETICON, 0, icon.Handle);
            SendMessage(mwHandle, (int)WinMessages.SETICON, 1, icon.Handle);
        }

        internal static void SetConsoleIcon(string iconFilePath)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            
            if (!string.IsNullOrEmpty(iconFilePath))
            {
                System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(iconFilePath);
                if (icon != null)
                {
                    SetWindowIcon(icon);
                }
            }
        }
    }

    /// <summary>
    /// A utility class to determine a process parent.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ParentProcessUtilities
    {
        // These members must match PROCESS_BASIC_INFORMATION
        internal IntPtr Reserved1;
        internal IntPtr PebBaseAddress;
        internal IntPtr Reserved2_0;
        internal IntPtr Reserved2_1;
        internal IntPtr UniqueProcessId;
        internal IntPtr InheritedFromUniqueProcessId;

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref ParentProcessUtilities processInformation, int processInformationLength, out int returnLength);

        /// <summary>
        /// Gets the parent process of a specified process.
        /// </summary>
        /// <param name="handle">The process handle.</param>
        /// <returns>An instance of the Process class.</returns>
        public static Process? GetParentProcess(IntPtr handle)
        {
            ParentProcessUtilities pbi = new ParentProcessUtilities();
            int status = NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf(pbi), out int returnLength);
            if (status != 0)
                throw new Win32Exception(status);

            try
            {
                return Process.GetProcessById(pbi.InheritedFromUniqueProcessId.ToInt32());
            }
            catch (ArgumentException)
            {
                // not found
                return null;
            }
        }
    }
}
