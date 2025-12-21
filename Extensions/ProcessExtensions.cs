using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GTerm.Extensions
{
    public static class ProcessExtensions
    {
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
