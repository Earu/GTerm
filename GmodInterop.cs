using GTerm.Extensions;
using Microsoft.Win32;
using Spectre.Console;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using VdfParser;

namespace GTerm
{
    internal class GmodInterop
    {
        private const string GMOD_ID = "4000";

        private static Process? GetGmodProcess() 
            => Process.GetProcessesByName("gmod").FirstOrDefault();

        private static bool TryGetSteamVDFPath(out string vdfPath)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    string? steamInstallPath = steamInstallPath = Registry.GetValue(@"HKEY_CLASSES_ROOT\steamlink\Shell\Open\Command", null, null) as string;
                    if (string.IsNullOrWhiteSpace(steamInstallPath))
                    {
                        steamInstallPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                        vdfPath = Path.Combine(steamInstallPath, "Steam/steamapps/libraryfolders.vdf");
                        if (!File.Exists(vdfPath))
                        {
                            steamInstallPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                            vdfPath = Path.Combine(steamInstallPath, "Steam/steamapps/libraryfolders.vdf");
                            return File.Exists(vdfPath);
                        }

                        return true;
                    }
                    else
                    {
                        steamInstallPath = Path.GetDirectoryName(steamInstallPath.Split('-').First().Replace("\"", string.Empty));
                        if (steamInstallPath == null)
                        {
                            vdfPath = "?";
                            return false;
                        }

                        vdfPath = Path.Combine(steamInstallPath, "steamapps/libraryfolders.vdf");
                        return File.Exists(vdfPath);
                    }

                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    string? homeDir = Environment.GetEnvironmentVariable("HOME");
                    if (homeDir != null) 
                    {
                        vdfPath = Path.Join(homeDir, "/Library/Application Support/Steam/steamapps/libraryfolders.vdf");
                        return File.Exists(vdfPath);
                    }
                }
                else 
                {
                    string? homeDir = Environment.GetEnvironmentVariable("HOME");
                    if (homeDir != null) 
                    {
                        vdfPath = Path.Join(homeDir, "/.steam/steam/steamapps/libraryfolders.vdf");
                        if (!File.Exists(vdfPath)) 
                        {
                            vdfPath = Path.Join(homeDir, "/.local/share/Steam/steamapps/libraryfolders.vdf");
                            if (!File.Exists(vdfPath)) 
                            {
                                // flatpak
                                vdfPath = Path.Join(homeDir, "/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/libraryfolders.vdf");
                            }
                        }

                        return File.Exists(vdfPath);
                    }
                }
            }
            catch {}

            vdfPath = "?";
            return false;
        }

        internal static bool TryGetGmodPath(out string gmodPath, bool toBin = true)
        {
            try
            {
                // push this in priority
                // fallback for installations from steamcmd and other weird things
                // this will require gmod restart
                Process? gmodProcess = GetGmodProcess();
                if (gmodProcess != null)
                {
                    string? path = gmodProcess.MainModule?.FileName.Replace('\\', '/');
                    if (path != null)
                    {
                        if (toBin)
                        {
                            gmodPath = path;
                            return true;
                        }

                        int index = path.IndexOf("GarrysMod/bin");
                        gmodPath = Path.Combine(path.Substring(0, index), "GarrysMod");
                        return true;
                    }
                }

                if (TryGetSteamVDFPath(out string steamLibsDescFilePath))
                {
                    FileStream libDescFile = File.OpenRead(steamLibsDescFilePath);
                    VdfDeserializer deserializer = new();
                    dynamic result = deserializer.Deserialize(libDescFile);
                    foreach (dynamic kv in result.libraryfolders)
                    {
                        if (!int.TryParse(kv.Key, out int _)) continue; // dont take things that arent steam libs
                        LocalLogger.WriteLine("Found Steam game library at :", kv.Value.path);

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
                }

                LocalLogger.WriteLine("Could not find Gmod directory: Maybe it's not installed?");
                gmodPath = string.Empty;
                return false;
            }
            catch (Exception ex)
            {
                LocalLogger.WriteLine("Could not find Gmod directory: ", ex.Message);
                gmodPath = string.Empty;
                return false;
            }
        }

        /// <summary>
        /// Tries to parse the current user gmod console keys, and use them further for minimizing GTerm
        /// </summary>
        /// <returns>Found console keys</returns>
        internal static List<ConsoleKey> GetConsoleBindings()
        {
            try
            {
                List<ConsoleKey> consoleTriggerKeys = [];

                if (!TryGetGmodPath(out string gmodBinPath)) return consoleTriggerKeys;

                int? index = gmodBinPath.IndexOf("GarrysMod");
                if (index == null || index == -1) return consoleTriggerKeys;

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

                    if (lineChunks.Length >= 3 && lineChunks[0] == "bind" && lineChunks[2].Contains("toggleconsole", StringComparison.CurrentCulture))
                    {
                        string keyName = lineChunks[1].ToUpper()[1] + lineChunks[1].Substring(1).ToLower();
                        if (Enum.TryParse(keyName, out ConsoleKey key))
                            consoleTriggerKeys.Add(key);
                    }
                }

                LocalLogger.WriteLine("Found console bindings: ", string.Join("\t", consoleTriggerKeys.Select(t => t.ToString())));
                return consoleTriggerKeys;
            }
            catch (Exception ex)
            {
                LocalLogger.WriteLine("Could not get Gmod console bindings: ", ex.Message);
                return [];
            }
        }

        private static string GetBinaryFileName(bool isX64)
        {
            string moduleName = "gmsv_xconsole_";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                moduleName += (isX64 ? "win64" : "win32");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // that's always x64
                moduleName += "osx64";
            }
            else
            {
                // always x64 as well
                moduleName += "linux64";
            }

            moduleName += ".dll";

            return moduleName;
        }

        private static async Task DownloadFileAsync(string url, string outputPath)
        {
            using (HttpClient client = new())
            {
                HttpResponseMessage response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();

                byte[] content = await response.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(outputPath, content);
            }
        }

        private static async Task<bool> InstallBinary(bool isX64, string gtermDir, string luaBinPath)
        {
            string moduleName = GetBinaryFileName(isX64);
            string targetXConsoleFilePath = Path.Combine(luaBinPath, moduleName);
            if (!File.Exists(targetXConsoleFilePath))
            {
                string XConsoleUrl = $"https://raw.githubusercontent.com/Earu/GTerm/master/Modules/{moduleName}";
                await DownloadFileAsync(XConsoleUrl, targetXConsoleFilePath);

                return true;
            }

            return false;
        }

        private static bool IsGmodX64(string gmodBinPath)
        {
            // Base assumption in case it fails later (windows can do x86 and x64, linux/mac only x64)
            bool isX64 = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || gmodBinPath.Contains("win64", StringComparison.CurrentCulture);

            // Fetch the gmod manifest to make a safer assumption of the current branch
            if (TryGetSteamVDFPath(out string vdfPath)) 
            {
                try 
                {
                    string? vdfDirPath = Path.GetDirectoryName(vdfPath);
                    if (vdfDirPath != null) 
                    {
                        string gmodManifiestPath = Path.Join(vdfDirPath, "appmanifest_4000.acf");
                        if (File.Exists(gmodManifiestPath)) 
                        {
                            FileStream gmodManifestFile = File.OpenRead(gmodManifiestPath);
                            VdfDeserializer deserializer = new();
                            dynamic result = deserializer.Deserialize(gmodManifestFile);

                            isX64 = result?.AppState?.UserConfig?.BetaKey == "x86-64";
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not find Garry's Mod manifest, assuming branch\n" + ex.Message);
                }
            }

            return isX64;
        }

        internal static async Task<(bool, bool)> InstallXConsole()
        {
            bool modifiedGameFiles = false;
            bool success = false;

            if (!TryGetGmodPath(out string baseGmodPath, false) || !TryGetGmodPath(out string gmodBinPath)) return (success, modifiedGameFiles);

            try
            {
                LocalLogger.WriteLine("Installing xconsole");
                string luaPath = Path.Combine(baseGmodPath, "garrysmod/lua");
                string luaBinPath = Path.Combine(luaPath, "bin");

                if (!Directory.Exists(luaBinPath))
                {
                    Directory.CreateDirectory(luaBinPath);
                    modifiedGameFiles = true;
                }
                    
                string? gtermDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName);
                if (gtermDir == null) return (success, modifiedGameFiles);
                
                bool isX64 = IsGmodX64(gmodBinPath);
                bool justInstalledBin = await InstallBinary(isX64, gtermDir, luaBinPath);
                if (justInstalledBin) {
                    modifiedGameFiles = true;
                }

                string menuInitFilePath = Path.Combine(luaPath, "menu/menu.lua");
                string menuInitLuaCode = File.ReadAllText(menuInitFilePath);
                if (!menuInitLuaCode.Contains("xconsole", StringComparison.CurrentCulture))
                {
                    File.AppendAllText(menuInitFilePath, "\nrequire(\"xconsole\")\n");
                    modifiedGameFiles = true;
                }

                if (modifiedGameFiles)
                {
                    LocalLogger.WriteLine("Installation complete!");

                    if (GetGmodProcess() != null)
                    {
                        AnsiConsole.MarkupLine("[white on red]Garry's Mod needs to be restarted for GTerm to work properly.[/]");
                    }
                }
                
                success = true;
            }
            catch (Exception ex)
            {
                LocalLogger.WriteLine("Could not install xconsole: ", ex.Message);
                AnsiConsole.MarkupLine("[white on red]Could not install xconsole![/]");
            }

            return (success, modifiedGameFiles);
        }

        private static bool ConsoleEventCallback(int eventType)
        {
            if (eventType == 2)
                GmodProcess?.Kill();

            return false;
        }

        // Keeps it from getting garbage collected
        private static Win32Extensions.ConsoleEventDelegate? handler;
        private static Process? GmodProcess;
        
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

            if (GmodProcess == null) return false;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                // this makes sure gmod gets killed when GTerm is closed
                handler = new Win32Extensions.ConsoleEventDelegate(ConsoleEventCallback);
                Win32Extensions.SetConsoleCtrlHandler(handler, true);

                Win32Extensions.ShowWindow(GmodProcess.MainWindowHandle, Win32Extensions.SW_HIDE);
                Timer visibityTimer = new(timer =>
                {
                    if (GmodProcess.HasExited)
                    {
                        (timer as Timer)?.Dispose();
                        Environment.Exit(GmodProcess.ExitCode);
                        return;
                    }

                    if (Win32Extensions.IsWindowVisible(GmodProcess.MainWindowHandle))
                        Win32Extensions.ShowWindow(GmodProcess.MainWindowHandle, Win32Extensions.SW_HIDE);
                });

                visibityTimer.Change(0, 100);
            }

            return true;
        }
    }
}
