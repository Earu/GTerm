using Spectre.Console;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GTerm
{
    class Program
    {
        private static readonly object Locker = new object();
        private static readonly LogListener Listener = new LogListener();

        private static bool LastMsgHadNewLine = true;
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

            ArchivePath = Path.Combine(processPath, "Archives");
            if (Config.ArchiveLogs)
            {
                if (!Directory.Exists(ArchivePath))
                    Directory.CreateDirectory(ArchivePath);
            }

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
                string log = args.Message;
                string timeStamp = null;

                if (ShouldExcludeLog(log)) return;

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

                if (Config.ArchiveLogs)
                {
                    string fileName = $"{DateTime.Now.ToString("d").Replace("/", "_")}.log";
                    File.AppendAllText(Path.Combine(ArchivePath, fileName), log);
                }
            }
        }
    }
}
