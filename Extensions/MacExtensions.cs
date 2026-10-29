using System.Runtime.InteropServices;
using System.Text;

namespace GTerm.Extensions
{
    internal static partial class MacExtensions
    {
        private const int PROC_PIDVNODEPATHINFO = 9;
        private const int VNODE_INFO_SIZE = 152;
        private const int MAXPATHLEN = 1024;
        private const int PROC_VNODEPATHINFO_SIZE = 2 * (VNODE_INFO_SIZE + MAXPATHLEN);

        [LibraryImport("/usr/lib/libproc.dylib")]
        private static partial int proc_pidinfo(int pid, int flavor, ulong arg, [Out] byte[] buffer, int buffersize);

        internal static string? GetWorkingDirectory(int pid)
        {
            byte[] buffer = new byte[PROC_VNODEPATHINFO_SIZE];
            if (proc_pidinfo(pid, PROC_PIDVNODEPATHINFO, 0, buffer, buffer.Length) != buffer.Length)
                return null;

            ReadOnlySpan<byte> path = buffer.AsSpan(VNODE_INFO_SIZE, MAXPATHLEN);
            int end = path.IndexOf((byte)0);
            return Encoding.UTF8.GetString(end < 0 ? path : path[..end]);
        }
    }
}
