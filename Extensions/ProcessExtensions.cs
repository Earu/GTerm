using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GTerm.Extensions
{
    public static class ProcessExtensions
    {
        public static Process? GetParent(this Process process) {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;

            return ParentProcessUtilities.GetParentProcess(process.Handle);
        }
    }
}   
