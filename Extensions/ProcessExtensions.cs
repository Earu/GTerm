using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GTerm.Extensions
{
    public static class ProcessExtensions
    {
        public static string? GetExecutablePath(this Process process)
        {
            try
            {
                try
                {
                    string? mainModulePath = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(mainModulePath))
                        return mainModulePath;
                }
                catch {}

                // Platform-specific fallbacks
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // QueryFullProcessImageName works across 32-bit/64-bit
                    char[] buffer = new char[1024];
                    int size = buffer.Length;

                    if (Win32Extensions.QueryFullProcessImageName(process.Handle, 0, buffer, ref size))
                    {
                        return new string(buffer, 0, size);
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // On Linux, read from /proc/[pid]/exe symlink
                    string exePath = $"/proc/{process.Id}/exe";
                    var linkTarget = File.ResolveLinkTarget(exePath, returnFinalTarget: true);
                    if (linkTarget != null)
                        return linkTarget.FullName;
                }

                return null;
            }
            catch (Exception ex)
            {
                LocalLogger.WriteLine($"Failed to get executable path for process {process.Id}: {ex.Message}");
                return null;
            }
        }

        public static Process? GetParent(this Process process) {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;

            try {
                return ParentProcessUtilities.GetParentProcess(process.Handle);
            }
            catch (Exception ex)
            {
                LocalLogger.WriteLine($"Failed to get parent process: {ex.Message}");
                return null;
            }
        }
    }
}
