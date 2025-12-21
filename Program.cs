using GTerm.Extensions;
using GTerm.Listeners;
using Spectre.Console;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace GTerm
{
    class Program
    {
        private const string HEADER = @"
 ██████ ████████ ███████ ██████  ███    ███ 
██         ██    ██      ██   ██ ████  ████ 
██   ███   ██    █████   ██████  ██ ████ ██ 
██    ██   ██    ██      ██   ██ ██  ██  ██ 
 ██████    ██    ███████ ██   ██ ██      ██ 
                                            
";

        private static readonly object Locker = new();
        private static readonly StringBuilder LogBuffer = new();
        private static readonly StringBuilder MarkupBuffer = new();
        private static readonly StringBuilder InputBuffer = new();
        private static readonly Thread UserInputThread = new(ProcessUserInput);

        private static readonly ILogListener Listener = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new WindowsLogListener()
            : new UnixLogListener();

        private static string ArchivePath = string.Empty;
        private static Config Config = new();
        private static WebSocketAPI? API;
        private static CommandCollector? Collector;
        private static MCPServer? MCP;

        static void Main(string[] args)
        {
            LocalLogger.WriteLine("Starting up...");

            // prevent running it multiple times
            Process curProc = Process.GetCurrentProcess();
            string processName = curProc.ProcessName;
            if (Process.GetProcesses().Count(p => p.ProcessName == processName) > 1)
            {
                LocalLogger.WriteLine("Another instance of GTerm is running, aborting");
                return;
            }

            SetMetadata();

            Config = new Config(args);

            Process? parent = curProc.GetParent();
            LocalLogger.WriteLine($"Parent process: {parent?.ProcessName}");
            if (parent?.ProcessName.Equals("gmod", StringComparison.OrdinalIgnoreCase) == true)
            {
                LocalLogger.WriteLine("Started from Gmod!");
                Config.StartAsGmod = false; // this cannot be true if gmod already runs GTerm as a child process

                if (Config.MonitorGmod)
                {
                    LocalLogger.WriteLine("Gmod monitoring starting");
                    parent.EnableRaisingEvents = true;
                    parent.Exited += (_, __) =>
                    {
                        if (parent.ExitCode == 0) return;

                        LocalLogger.WriteLine("Gmod crashed, attempting to restart!");
                        ProcessStartInfo oldStartInfo = parent.StartInfo;
                        if (GmodInterop.TryGetGmodPath(out string gmodBinpath))
                        {
                            oldStartInfo.FileName = gmodBinpath;
                            LocalLogger.WriteLine("Restarting Gmod!");
                            Process.Start(parent.StartInfo); // reboot gmod because crash and it was the owning process
                            curProc.Kill(); // we also kill the current process, because its likely it will be rebooted from gmod
                        }
                    };
                }
            }

            string? processPath = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName);
            if (processPath != null)
            {
                ArchivePath = Path.Combine(processPath, "Archives");
                if (Config.ArchiveLogs)
                {
                    if (!Directory.Exists(ArchivePath))
                    {
                        LocalLogger.WriteLine("Archives directory was not found, creating it at: ", ArchivePath);
                        Directory.CreateDirectory(ArchivePath);
                    }
                }
            }

            Task.Run(GmodInterop.InstallXConsole); // try to install xconsole

            if (Config.StartAsGmod)
            {
                if (!GmodInterop.StartGmod()) return;
            }

            ShowWaitingConnection();

            Listener.OnConnected += (_, __) =>
            {
                Console.Clear();

                Console.ForegroundColor = ConsoleColor.Green;

                Console.WriteLine(new string('=', Console.BufferWidth - 1));
                Console.WriteLine(HEADER);
                Console.WriteLine(new string('=', Console.BufferWidth - 1));

                Console.WriteLine("Welcome to GTerm! You can read your Garry's Mod logs from here in real-time.");
                Console.WriteLine("You can also execute any command you normally would by typing here!");
                Console.WriteLine();
                Console.WriteLine();
                Console.Write("If you have any issue with GTerm please open an issue at: ");

                Console.BackgroundColor = ConsoleColor.White;
                Console.ForegroundColor = ConsoleColor.Black;

                Console.WriteLine("https://github.com/Earu/GTerm/issues");

                Console.BackgroundColor = ConsoleColor.Black;
                Console.ForegroundColor = ConsoleColor.Green;

                Console.WriteLine("Enjoy your stay!");
                Console.Write("\n\n\n");

                Console.BackgroundColor = ConsoleColor.Black;
                Console.ForegroundColor = ConsoleColor.White;
            };

            Listener.OnDisconnected += (_, __) =>
            {
                AnsiConsole.Markup("[bold red]Disconnected[/]");
                ShowWaitingConnection();
            };

            Listener.OnLog += OnLog;
            Listener.OnError += OnError;
            Listener.Start();

            UserInputThread.Start();

            if (Config.API)
            {
                API = new WebSocketAPI(Listener, Config.APIPort, Config.APISecret);
                Task.Run(API.StartAsync);
            }

            if (Config.MCP)
            {
                Collector = new CommandCollector(Listener, Config.MCPCollectionWindowMs);
                MCP = new MCPServer(Collector, Config.MCPPort, Config.MCPSecret);
                Task.Run(() => MCP.StartAsync());
            }
        }

        private static void SetMetadata()
        {
            Console.Clear();

            LocalLogger.WriteLine("Setting metadata");

            Console.Title = "GTerm";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && GmodInterop.TryGetGmodPath(out string gmodPath))
            {
                // copy icon from the gmod exe
                Win32Extensions.SetConsoleIcon(gmodPath);
                LocalLogger.WriteLine("Set icon metadata");
            }
            else
            {
                LocalLogger.WriteLine("Failed to set icon metadata, gmod path not found");
            }
        }

        private static void ProcessUserInput()
        {
            Console.WriteLine(); // on UNIX this prevents a terminal deadlock

            while (true)
            {
                if (!Console.KeyAvailable) {
                    Thread.Sleep(100);
                    continue;
                }

                ConsoleKeyInfo keyInfo = Console.ReadKey(true);
                switch (keyInfo.Key)
                {
                    case ConsoleKey.Enter:
                        {
                            string input = InputBuffer.ToString();
                            InputBuffer.Clear();

                            if (string.IsNullOrWhiteSpace(input.Trim())) continue;

                            if (input.Trim().Equals("clear", StringComparison.CurrentCultureIgnoreCase))
                                Console.Clear();

                            _ = Listener.WriteMessage(input);

                            Console.CursorLeft = 0;
                            Console.Write(new string(' ', Console.BufferWidth - 1));
                        }
                        break;

                    case ConsoleKey.Backspace:
                        if (InputBuffer.Length > 0)
                            InputBuffer.Remove(InputBuffer.Length - 1, 1);

                        int oldCursorLeft = Console.CursorLeft;
                        int oldCursorTop = Console.CursorTop;
                        Console.CursorLeft = 0;
                        Console.Write(new string(' ', Console.BufferWidth - 1));
                        Console.CursorLeft = oldCursorLeft;
                        Console.CursorTop = oldCursorTop;

                        break;

                    case ConsoleKey.Escape:
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                            IntPtr hWndConsole = Win32Extensions.GetConsoleWindow();
                            Win32Extensions.ShowWindow(hWndConsole, Win32Extensions.SW_MINIMIZE);
                        }

                        break;

                    default:
                        InputBuffer.Append(keyInfo.KeyChar);
                        if (InputBuffer.Length > 255) // source console is limited to 255 characters
                            InputBuffer.Remove(254, InputBuffer.Length - 255);

                        break;
                }

                Console.Write("\r" + InputBuffer.ToString());
            }
        }

        private static void ShowWaitingConnection() => AnsiConsole.Status()
            .AutoRefresh(false)
            .Spinner(Spinner.Known.Aesthetic)
            .SpinnerStyle(Style.Parse("red bold"))
            .StartAsync("Waiting for Garry's Mod connection...", async ctx =>
            {
                while (true)
                {
                    if (Listener.IsConnected)
                    {
                        ctx.Status = "Connected";
                        ctx.SpinnerStyle(Style.Parse("green cold"));
                        ctx.Spinner(Spinner.Known.Star);
                    }

                    ctx.Refresh();
                    await Task.Delay(500);
                }
            });

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
            if (Config == null) return false;

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
                DateTime now = DateTime.Now;
                string timeStamp = now.ToString("hh:mm:ss");
                System.Drawing.Color col = IsBlack(args.Color) ? System.Drawing.Color.White : args.Color;
                string msg = args.Message;
                int newLineIndex = msg.IndexOf('\n');
                while (newLineIndex != -1)
                {
                    if (MarkupBuffer.Length == 0 && LogBuffer.Length == 0)
                    {
                        MarkupBuffer.Append($"[#ffaf00]{timeStamp}[/] | ");
                        LogBuffer.Append($"{timeStamp} | ");
                        API?.StartData(now);
                    }

                    string chunk = string.Concat(msg.AsSpan(0, newLineIndex), "\n");
                    string nextChunk = msg.Length > newLineIndex + 1 ? msg[chunk.Length..] : string.Empty;

                    LogBuffer.Append(chunk);
                    MarkupBuffer.Append($"[rgb({col.R},{col.G},{col.B})]{SanitizeLogMessage(chunk)}[/]");
                    API?.AppendData(col, chunk);

                    string mk = MarkupBuffer.ToString();
                    string log = LogBuffer.ToString();

                    MarkupBuffer.Clear();
                    LogBuffer.Clear();
                    API?.FinishDataAsync();

                    string logChunk = log.Contains('|', StringComparison.CurrentCulture) ? string.Join("|", log.Split('|').Skip(1).ToArray()) : log; // there should always be 1
                    if (!string.IsNullOrWhiteSpace(logChunk))
                    {
                        if (ShouldExcludeLog(logChunk)) return;

                        int currentTopCursor = Console.CursorTop;
                        int currentLeftCursor = Console.CursorLeft;

                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                            Console.MoveBufferArea(0, currentTopCursor, Console.WindowWidth, 1, 0, Math.Min(Console.BufferHeight - 1, currentTopCursor + 1));

                        }

                        Console.CursorTop = Math.Min(Console.BufferHeight - 1, currentTopCursor);
                        Console.CursorLeft = 0;

                        AnsiConsole.Markup(mk);
                        Console.Out.Flush();

                        // this makes typing when the console is filled more stable
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

                    msg = nextChunk;
                    newLineIndex = msg.IndexOf('\n');

                    if (msg.Length == 0) break;
                }

                if (MarkupBuffer.Length == 0 && LogBuffer.Length == 0)
                {
                    MarkupBuffer.Append($"[#ffaf00]{timeStamp}[/] | ");
                    LogBuffer.Append($"{timeStamp} | ");
                    API?.StartData(now);
                }

                if (msg.Length > 0)
                {
                    LogBuffer.Append(msg);
                    MarkupBuffer.Append($"[rgb({col.R},{col.G},{col.B})]{SanitizeLogMessage(msg)}[/]");
                    API?.AppendData(col, msg);
                }
            }
        }
    }
}
