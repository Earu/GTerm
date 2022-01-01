using GTerm.Extensions;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using VdfParser;

namespace GTerm
{
    internal class GmodInterop
    {
        private const string GMOD_ID = "4000";

        internal static bool TryGetGmodPath(out string gmodPath, bool toBin = true)
        {
            string programsPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string steamLibsDescFilePath = Path.Combine(programsPath, "Steam/steamapps/libraryfolders.vdf");

            FileStream libDescFile = File.OpenRead(steamLibsDescFilePath);
            VdfDeserializer deserializer = new VdfDeserializer();
            dynamic result = deserializer.Deserialize(libDescFile);
            foreach (dynamic kv in result.libraryfolders)
            {
                if (!int.TryParse(kv.Key, out int _)) continue; // dont take things that arent steam libs
                foreach (dynamic appKv in kv.Value.apps)
                {
                    if (appKv.Key != GMOD_ID) continue;

                    gmodPath = Path.Combine(kv.Value.path, "steamapps/common/GarrysMod");

                    if (toBin)
                    {
                        string gmodPathX64 = Path.Combine(gmodPath, "bin/win64/gmod.exe");
                        string gmodPathX86 = Path.Combine(gmodPath, "bin/gmod.exe");
                        if (File.Exists(gmodPathX64))
                            gmodPath = gmodPathX64;
                        else if (File.Exists(gmodPathX86))
                            gmodPath = gmodPathX86;
                        else
                            gmodPath = Path.Combine(gmodPath, "hl2.exe");
                    }

                    return true;
                }
            }

            gmodPath = null;
            return false;
        }

        /// <summary>
        /// Tries to parse the current user gmod console keys, and use them further for minimizing GTerm
        /// </summary>
        /// <returns>Found console keys</returns>
        internal static List<ConsoleKey> GetConsoleBindings()
        {
            List<ConsoleKey> consoleTriggerKeys = new List<ConsoleKey>();

            if (!TryGetGmodPath(out string gmodBinPath)) return consoleTriggerKeys;

            int? index = gmodBinPath?.IndexOf("GarrysMod");
            if (index == null && index == -1) return consoleTriggerKeys;

            string baseGmodPath = gmodBinPath.Substring(0, index.Value + "GarrysMod".Length);
            string cfgPath = Path.Combine(baseGmodPath, "garrysmod/cfg/config.cfg");
            if (!File.Exists(cfgPath)) return consoleTriggerKeys;

            string[] cfgLines = File.ReadAllLines(cfgPath);
            foreach (string cfgLine in cfgLines)
            {
                string[] lineChunks = cfgLine.Split(' ')
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Replace("\"", string.Empty).Trim())
                    .ToArray();

                if (lineChunks.Length >= 3 && lineChunks[0] == "bind" && lineChunks[2].IndexOf("toggleconsole") != -1)
                {
                    string keyName = lineChunks[1].ToUpper()[1] + lineChunks[1].Substring(1).ToLower();
                    if (Enum.TryParse(keyName, out ConsoleKey key))
                        consoleTriggerKeys.Add(key);
                }
            }

            return consoleTriggerKeys;
        }

        internal static bool InstallXConsole()
        {
            if (!TryGetGmodPath(out string baseGmodPath, false) || !TryGetGmodPath(out string gmodBinPath)) return false;

            try
            {
                string luaPath = Path.Combine(baseGmodPath, "garrysmod/lua");
                string luaBinPath = Path.Combine(luaPath, "bin");
                if (!Directory.Exists(luaBinPath))
                    Directory.CreateDirectory(luaBinPath);

                string gtermDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
                bool isX64 = gmodBinPath.IndexOf("win64") != -1;

                string sourceXConsoleX64FilePath = Path.Combine(gtermDir, "Modules/gmsv_xconsole_win64.dll");
                string targetXConsoleX64FilePath = Path.Combine(luaBinPath, "gmsv_xconsole_win64.dll");
                string sourceXConsoleX86FilePath = Path.Combine(gtermDir, "Modules/gmsv_xconsole_win32.dll");
                string targetXConsoleX86FilePath = Path.Combine(luaBinPath, "gmsv_xconsole_win32.dll");
                if (isX64 && !File.Exists(targetXConsoleX64FilePath) && File.Exists(sourceXConsoleX64FilePath))
                {
                    File.Copy(sourceXConsoleX64FilePath, targetXConsoleX64FilePath);
                }
                else if (!isX64 && !File.Exists(targetXConsoleX86FilePath) && File.Exists(sourceXConsoleX86FilePath))
                {
                    File.Copy(sourceXConsoleX86FilePath, targetXConsoleX86FilePath);
                }

                string menuInitFilePath = Path.Combine(luaPath, "menu/menu.lua");
                string menuInitLuaCode = File.ReadAllText(menuInitFilePath);
                if (menuInitLuaCode.IndexOf("xconsole") == -1)
                    File.AppendAllText(menuInitFilePath, "\nrequire(\"xconsole\")\n");
            }
            catch
            {
                AnsiConsole.WriteLine("[white on red]Could not install xconsole![/]");
                return false;
            }

            return true;
        }

        private static bool ConsoleEventCallback(int eventType)
        {
            if (eventType == 2)
                GmodProcess?.Kill();

            return false;
        }

        // Keeps it from getting garbage collected
        private static Win32Extensions.ConsoleEventDelegate handler;
        private static Process GmodProcess;
        
        /// <summary>
        /// Launches gmod in textmode
        /// </summary>
        /// <returns></returns>
        internal static bool StartGmod()
        {
            if (!TryGetGmodPath(out string gmodBinPath)) return false;

            GmodProcess = Process.Start(new ProcessStartInfo
            {
                Arguments = "-textmode",
                FileName = gmodBinPath,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            // this makes sure gmod gets killed when GTerm is closed
            handler = new Win32Extensions.ConsoleEventDelegate(ConsoleEventCallback);
            Win32Extensions.SetConsoleCtrlHandler(handler, true);

            Win32Extensions.ShowWindow(GmodProcess.MainWindowHandle, Win32Extensions.SW_HIDE);
            Timer visibityTimer = new Timer(timer =>
            {
                if (GmodProcess.HasExited)
                {
                    ((Timer)timer).Dispose();
                    Environment.Exit(GmodProcess.ExitCode);
                    return;
                }

                if (Win32Extensions.IsWindowVisible(GmodProcess.MainWindowHandle))
                    Win32Extensions.ShowWindow(GmodProcess.MainWindowHandle, Win32Extensions.SW_HIDE);
            });

            visibityTimer.Change(0, 100);
            return true;
        }
    }
}
