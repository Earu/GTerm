using Spectre.Console;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace GTerm
{
    class Program
    {
        private static readonly object Locker = new object();
        private static readonly LogListener Listener = new LogListener();
        private static string ProcessPath = string.Empty;

        static void Main()
        {
            Console.Title = "GTerm";

            // prevent running it multiple times
            Process curProc = Process.GetCurrentProcess();
            string processName = curProc.ProcessName;
            if (Process.GetProcesses().Count(p => p.ProcessName == processName) > 1)
                return;

            ProcessPath = Path.Combine(Path.GetDirectoryName(curProc.MainModule.FileName), "Archives");
            if (!Directory.Exists(ProcessPath))
                Directory.CreateDirectory("Archives");

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

        private static bool LastMsgHadNewLine = true;
        private static void OnLog(object sender, LogEventArgs args)
        {
            lock (Locker)
            {
                //if (args.Message.Trim().Length == 0) return;
                string log = args.Message;
                string timeStamp = null; 

                System.Drawing.Color col = IsBlack(args.Color) ? System.Drawing.Color.White : args.Color;
                string mk = $"[rgb({col.R},{col.G},{col.B})]{SanitizeLogMessage(log)}[/]";

                if (LastMsgHadNewLine)
                {
                    if (log == "\n") return;

                    timeStamp = DateTime.Now.ToString("T");
                    mk = $"[#ffaf00]{timeStamp}[/] | " + mk;
                    log = $"{timeStamp} | {log}";
                }

                LastMsgHadNewLine = args.Message.EndsWith("\n");
                AnsiConsole.Write(new Markup(mk));

                string fileName = $"{DateTime.Now.ToString("d").Replace("/", "_")}.log";
                File.AppendAllText(Path.Combine(ProcessPath, fileName), log);
            }
        }
    }
}
