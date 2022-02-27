using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace GTerm
{
    internal static class LocalLogger
    {
        internal static void WriteLine(params object[] args)
        {
            string gtermDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            string outMsg = $"{DateTime.Now:hh:mm:ss} | {string.Join("\t", args.Select(x => x.ToString()))}\n";

            File.AppendAllText(Path.Combine(gtermDir, "gterm.log"), outMsg);
        }
    }
}
