using GTerm.Extensions;
using Microsoft.Win32;
using Spectre.Console;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VdfParser;

namespace GTerm
{
    internal class GmodInterop
    {
        private const string GMOD_ID = "4000";

        private static Process? GetGmodProcess()
            => Process.GetProcessesByName("gmod").FirstOrDefault();

        /// <summary>
        /// Whether Garry's Mod is running at all. Distinguishes "not launched" from "launched but the
        /// GTerm module has not connected yet", which need different advice.
        /// </summary>
        internal static bool IsGmodRunning() => GetGmodProcess() != null;

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

        private static bool IsGmodRunningWithProton()
        {
            // Proton is a Linux-only concept
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return false;

            if (!TryGetSteamVDFPath(out string vdfPath)) return false;

            try
            {
                // config.vdf lives in the Steam root, which is the parent of the steamapps folder
                string? steamAppsDir = Path.GetDirectoryName(vdfPath);
                string? steamRoot = steamAppsDir != null ? Path.GetDirectoryName(steamAppsDir) : null;
                if (steamRoot == null) return false;

                string configPath = Path.Combine(steamRoot, "config/config.vdf");
                if (!File.Exists(configPath)) return false;

                using FileStream configFile = File.OpenRead(configPath);
                VdfDeserializer deserializer = new();
                dynamic config = deserializer.Deserialize(configFile);
                dynamic? compatMapping = config?.InstallConfigStore?.Software?.Valve?.Steam?.CompatToolMapping;
                if (compatMapping == null) return false;

                // A per-game mapping (keyed by the gmod app id) always wins over the global
                // default mapping (keyed by "0").
                string? appTool = null;
                string? defaultTool = null;
                foreach (dynamic kv in compatMapping)
                {
                    if (kv.Key == GMOD_ID) appTool = kv.Value?.name as string;
                    else if (kv.Key == "0") defaultTool = kv.Value?.name as string;
                }

                string? tool = !string.IsNullOrWhiteSpace(appTool) ? appTool : defaultTool;

                // A Steam Linux Runtime (sniper/soldier) entry still runs the native Linux
                // binaries, so only treat an explicit Proton tool as Proton.
                return !string.IsNullOrWhiteSpace(tool)
                    && tool.Contains("proton", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                LocalLogger.WriteLine("Could not determine Proton usage for Garry's Mod: ", ex.Message);
                return false;
            }
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
                    string? path = gmodProcess.GetExecutablePath()?.Replace('\\', '/');
                    if (path != null)
                    {
                        if (toBin)
                        {
                            gmodPath = path;
                            return true;
                        }

                        int index = path.IndexOf("GarrysMod");
                        gmodPath = Path.Combine(path[..index], "GarrysMod");
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
                                string gmodPathDir = gmodPath;
                                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || IsGmodRunningWithProton())
                                    gmodPath = FirstExistingPath(gmodPathDir, "bin/win64/gmod.exe", "gmod.exe", "hl2.exe");
                                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                                    gmodPath = Path.Combine(gmodPathDir, "hl2_osx");
                                else
                                    gmodPath = FirstExistingPath(gmodPathDir, "linux64/gmod", "linux64/srcds", "hl2_linux", "srcds");
                            }

                            return true;
                        }
                    }
                }

                LocalLogger.WriteLine("Could not find Gmod directory: Maybe it's not installed or installed via steamcmd?");
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

        private static string FirstExistingPath(string baseDir, params string[] candidates)
        {
            foreach (string candidate in candidates)
            {
                string path = Path.Combine(baseDir, candidate);
                if (File.Exists(path))
                    return path;
            }

            return Path.Combine(baseDir, candidates[^1]);
        }

        private static string GetBinaryFileName(bool isX64)
        {
            string moduleName = "gmsv_gterm_";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || IsGmodRunningWithProton())
            {
                moduleName += isX64 ? "win64" : "win32";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // that's always x64
                moduleName += "osx64";
            }
            else
            {
                moduleName += isX64 ? "linux64" : "linux";
            }

            moduleName += ".dll";

            return moduleName;
        }

        private static async Task<byte[]> DownloadFileAsync(string url)
        {
            using HttpClient client = new();
            HttpResponseMessage response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsByteArrayAsync();
        }

        private static async Task WriteModuleAsync(string path, byte[] content)
        {
            string stalePath = path + ".old";
            try
            {
                if (File.Exists(stalePath))
                    File.Delete(stalePath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            string newPath = path + ".new";
            await File.WriteAllBytesAsync(newPath, content);

            try
            {
                File.Move(newPath, path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                File.Move(path, stalePath, true);
                File.Move(newPath, path);
            }
        }

        private static async Task<bool> InstallBinary(bool isX64, string luaBinPath, ModuleRealm realm, bool skipUpdate)
        {
            string moduleName = GetBinaryFileName(isX64);
            string targetModuleFilePath = Path.Combine(luaBinPath, realm == ModuleRealm.Client ?moduleName.Replace("gmsv_", "gmcl_") : moduleName);
            bool exists = File.Exists(targetModuleFilePath);
            if (exists && skipUpdate) return false;

            string moduleUrl = $"https://raw.githubusercontent.com/Earu/GTerm/master/prebuilt-bins/{moduleName}";
            byte[] content = await DownloadFileAsync(moduleUrl);
            if (exists)
            {
                byte[] current = await File.ReadAllBytesAsync(targetModuleFilePath);
                if (content.AsSpan().SequenceEqual(current))
                    return false;
            }

            await WriteModuleAsync(targetModuleFilePath, content);
            LocalLogger.WriteLine(exists ? $"Updated {targetModuleFilePath}" : $"Installed {targetModuleFilePath}");

            return true;
        }

        private static bool[] GetModuleArchitectures()
            => RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? [true] : [true, false];

        internal static async Task<(bool, bool)> InstallGTerm(Config config)
        {
            bool modifiedGameFiles = false;
            bool success = false;

            if (!TryGetGmodPath(out string baseGmodPath, false)) return (success, modifiedGameFiles);

            try
            {
                LocalLogger.WriteLine("Installing gterm");
                string luaPath = Path.Combine(baseGmodPath, "garrysmod/lua");
                string luaBinPath = Path.Combine(luaPath, "bin");

                if (!config.SkipModule)
                {
                    if (!Directory.Exists(luaBinPath))
                    {
                        Directory.CreateDirectory(luaBinPath);
                        modifiedGameFiles = true;
                    }

                    foreach (bool isX64 in GetModuleArchitectures())
                    {
                        try
                        {
                            if (await InstallBinary(isX64, luaBinPath, config.ModuleRealm, config.SkipModuleUpdate))
                                modifiedGameFiles = true;
                        }
                        catch (Exception ex)
                        {
                            LocalLogger.WriteLine($"Could not install {GetBinaryFileName(isX64)}: ", ex.Message);
                        }
                    }
                }

                string menuInitFilePath = Path.Combine(luaPath, "menu/menu.lua");
                if (!config.SkipGameFiles && !File.ReadAllText(menuInitFilePath).Contains("gterm", StringComparison.CurrentCulture))
                {
                    File.AppendAllText(menuInitFilePath, "\nrequire(\"gterm\")\n");
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
                LocalLogger.WriteLine("Could not install gterm: ", ex.Message);
                AnsiConsole.MarkupLine("[white on red]Could not install gterm![/]");
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
