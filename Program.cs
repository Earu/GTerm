using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VdfParser;

namespace GTerm
{
    class Program
    {
        private static readonly object Locker = new object();
        private static readonly LogListener Listener = new LogListener();
        private static readonly StringBuilder LogBuffer = new StringBuilder();
        private static readonly StringBuilder MarkupBuffer = new StringBuilder();
        private static readonly StringBuilder InputBuffer = new StringBuilder();
        private static readonly Thread UserInputThread = new Thread(ProcessUserInput);

        private static string ArchivePath = string.Empty;
        private static Config Config = null;

        static void Main()
        {
            Console.Title = "GTerm";

            // prevent running it multiple times
            Process curProc = Process.GetCurrentProcess();
            string processName = curProc.ProcessName;
            if (Process.GetProcesses().Count(p => p.ProcessName == processName) > 1)
                return;


            string processPath = Path.GetDirectoryName(curProc.MainModule.FileName);
            Config = new Config(processPath);

            if (Config.MonitorGmod)
            {
                Process parent = curProc.GetParent();
                if (parent?.MainModule.ModuleName == "gmod.exe")
                {
                    parent.EnableRaisingEvents = true;
                    parent.Exited += (_, __) =>
                    {
                        if (parent.ExitCode == 0) return;
                        Process.Start(parent.StartInfo); // reboot gmod because crash and it was the owning process
                        curProc.Kill(); // we also kill the current process, because its likely it will be rebooted from gmod
                    };
                }
            }

            ArchivePath = Path.Combine(processPath, "Archives");
            if (Config.ArchiveLogs)
            {
                if (!Directory.Exists(ArchivePath))
                    Directory.CreateDirectory(ArchivePath);
            }

            if (!Config.StartAsGmod)
            {
                ShowWaitingConnection();

                Listener.OnConnected += (_, __) => AnsiConsole.Markup("[bold green]Connected![/]");
                Listener.OnDisconnected += (_, __) =>
                {
                    AnsiConsole.Markup("[bold red]Disconnected[/]");
                    ShowWaitingConnection();
                };

                Listener.OnLog += OnLog;
                Listener.OnError += OnError;
                Listener.Start();

                UserInputThread.Start();
            }
            else
            {
                if (TryGetGmodPath(out string gmodPath))
                {
                    Process gmodProc = Process.Start(new ProcessStartInfo
                    {
                        Arguments = "-textmode",
                        FileName = gmodPath,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    });

                    gmodProc.EnableRaisingEvents = true;
                    gmodProc.OutputDataReceived += (s, log) => OnLog(s, new LogEventArgs(0, 0, "General", System.Drawing.Color.White, log.Data));
                    gmodProc.ErrorDataReceived += (s, err) => OnLog(s, new LogEventArgs(0, 0, "General", System.Drawing.Color.Red, err.Data));
                    gmodProc.Exited += (_, __) => Environment.Exit(gmodProc.ExitCode);

                    UserInputThread.Start();
                }
            }
        }

        private static bool TryGetGmodPath(out string gmodPath)
        {
            string programsPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string steamLibsDescFilePath = Path.Combine(programsPath, "Steam/steamapps/libraryfolders.vdf");

            FileStream testFile = File.OpenRead(steamLibsDescFilePath);
            VdfDeserializer deserializer = new VdfDeserializer();
            dynamic result = deserializer.Deserialize(testFile);
            foreach(dynamic kv in result.libraryfolders)
            {
                if (!int.TryParse(kv.Key, out int _)) continue; // dont take things that arent steam libs
                foreach (dynamic appKv in kv.Value.apps)
                {
                    if (appKv.Key != "4000") continue;

                    gmodPath = Path.Combine(kv.Value.path, "steamapps/common/GarrysMod");
                    string gmodPathX64 = Path.Combine(gmodPath, "bin/win64/gmod.exe");
                    string gmodPathX86 = Path.Combine(gmodPath, "bin/gmod.exe");
                    if (File.Exists(gmodPathX64))
                        gmodPath = gmodPathX64;
                    else if (File.Exists(gmodPathX86))
                        gmodPath = gmodPathX86;
                    else
                        gmodPath = Path.Combine(gmodPath, "hl2.exe");

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
        private static List<ConsoleKey> GetGmodConsoleKeys()
        {
            List<ConsoleKey> consoleTriggerKeys = new List<ConsoleKey>();

            Process gmodProc = Process.GetProcessesByName("gmod").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
            if (gmodProc == null) return consoleTriggerKeys;

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

        private static void ProcessUserInput()
        {
            List<ConsoleKey> gmodConsoleKeys = GetGmodConsoleKeys();
            while (true)
            {
                ConsoleKeyInfo keyInfo = Console.ReadKey();
                switch (keyInfo.Key)
                {
                    case ConsoleKey.Enter:
                        {
                            string input = InputBuffer.ToString();
                            InputBuffer.Clear();

                            if (string.IsNullOrWhiteSpace(input.Trim())) continue;

                            _ = Listener.WriteMessage(input);
                        }
                        break;

                    case ConsoleKey.Backspace:
                        if (InputBuffer.Length > 0)
                            InputBuffer.Remove(InputBuffer.Length - 1, 1);
                        break;

                    case ConsoleKey key when gmodConsoleKeys.Contains(key):
                    case ConsoleKey.Escape:
                        IntPtr hWndConsole = GetConsoleWindow();
                        ShowWindow(hWndConsole, SW_MINIMIZE);
                        break;

                    default:
                        InputBuffer.Append(keyInfo.KeyChar);
                        if (InputBuffer.Length > 255) // source console is limited to 255 characters
                            InputBuffer.Remove(254, InputBuffer.Length - 255);

                        break;
                }
            }
        }

        private static void ShowWaitingConnection()
        {
            _ = AnsiConsole.Status()
             .AutoRefresh(false)
             .Spinner(Spinner.Known.Aesthetic)
             .SpinnerStyle(Style.Parse("red bold"))
             .StartAsync("Waiting for Garry's Mod connection...", async ctx =>
             {
                 while (true)
                 {
                     ctx.Refresh();
                     await Task.Delay(500);
                     if (Listener.IsConnected)
                     {
                         ctx.Status = "Connected";
                         ctx.SpinnerStyle(Style.Parse("green cold"));
                         ctx.Spinner(Spinner.Known.Star);
                     }
                 }
             });
        }

        private static void OnError(object sender, ErrorEventArgs e)
        {
            string stackTrace = SanitizeLogMessage(e.GetException().ToString());
            AnsiConsole.Markup($"[italic red]{stackTrace}[/]");
        }

        private static bool IsBlack(System.Drawing.Color col)
            => col.R == 0 && col.G == 0 && col.B == 0;

        private static string SanitizeLogMessage(string msg)
        {
            msg = Markup.Escape(msg);
            return msg;
        }

        private static bool ShouldExcludeLog(string log)
        {
            foreach (Regex pattern in Config.ExclusionPatterns)
            {
                if (pattern.IsMatch(log)) return true;
            }

            return false;
        }

        private static void OnLog(object sender, LogEventArgs args)
        {
            lock (Locker)
            {
                string timeStamp = DateTime.Now.ToString("T");

                // if the buffers are empty then its a newline, add timestamp
                if (MarkupBuffer.Length == 0 && LogBuffer.Length == 0)
                {
                    MarkupBuffer.Append($"[#ffaf00]{timeStamp}[/] | ");
                    LogBuffer.Append($"{timeStamp} | ");
                }

                LogBuffer.Append(args.Message);

                System.Drawing.Color col = IsBlack(args.Color) ? System.Drawing.Color.White : args.Color;
                MarkupBuffer.Append($"[rgb({col.R},{col.G},{col.B})]{SanitizeLogMessage(args.Message)}[/]");
                
                // if the message ends with a newline then flush it to the console
                if (args.Message.EndsWith("\n"))
                {
                    string mk = MarkupBuffer.ToString();
                    string log = LogBuffer.ToString();

                    MarkupBuffer.Clear();
                    LogBuffer.Clear();

                    string logChunk = log.Split('|')[1]; // there should always be 1
                    if (string.IsNullOrWhiteSpace(logChunk) || ShouldExcludeLog(logChunk)) return;

                    int currentTopCursor = Console.CursorTop;
                    int currentLeftCursor = Console.CursorLeft;

                    Console.MoveBufferArea(0, currentTopCursor, Console.WindowWidth, 1, 0, currentTopCursor + 1);
                    Console.CursorTop = currentTopCursor;
                    Console.CursorLeft = 0;

                    AnsiConsole.Write(new Markup(mk));
                    
                    if (currentTopCursor + 1 >= Console.BufferHeight)
                    {
                        Console.Write(InputBuffer.ToString());
                    }

                    Console.CursorTop = Math.Min(Console.BufferHeight - 1, currentTopCursor + 1);
                    Console.CursorLeft = currentLeftCursor;

                    if (Config.ArchiveLogs)
                    {
                        string fileName = $"{DateTime.Now.ToString("d").Replace("/", "_")}.log";
                        File.AppendAllText(Path.Combine(ArchivePath, fileName), log);
                    }
                }
            }
        }

        const int SW_MINIMIZE = 6;

        [DllImport("Kernel32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("User32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow([In] IntPtr hWnd, [In] int nCmdShow);
    }
}
