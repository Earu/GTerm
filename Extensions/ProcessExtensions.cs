using System.Diagnostics;

namespace GTerm.Extensions
{
    public static class ProcessExtensions
    {
        public static Process GetParent(this Process process) => ParentProcessUtilities.GetParentProcess(process.Handle);
    }
}   
