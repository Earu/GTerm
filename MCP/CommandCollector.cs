using GTerm.Listeners;
using System.Collections.Concurrent;

namespace GTerm.MCP
{
    internal class CommandResult
    {
        public bool Success { get; set; }
        public string Command { get; set; } = string.Empty;
        public List<OutputLine> Output { get; set; } = [];

        /// <summary>Sentinel lines emitted by GTerm's own generated Lua. Never present in <see cref="Output"/>.</summary>
        public List<Sentinel> Sentinels { get; set; } = [];

        /// <summary>False when the command ran but printed nothing at all. Not an error.</summary>
        public bool ProducedOutput { get; set; }

        public double CollectionDurationMs { get; set; }
        public string? Error { get; set; }

        internal bool TryGetSentinel(string marker, out Sentinel sentinel)
        {
            foreach (Sentinel s in this.Sentinels)
            {
                if (s.Marker == marker)
                {
                    sentinel = s;
                    return true;
                }
            }

            sentinel = default;
            return false;
        }
    }

    internal class OutputLine
    {
        public string Timestamp { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public ColorInfo Color { get; set; } = new();
    }

    internal class ColorInfo
    {
        public int R { get; set; }
        public int G { get; set; }
        public int B { get; set; }
        public int A { get; set; }
    }

    internal class CommandCollector
    {
        internal const string BusyError = "Another command is currently being executed";
        internal const string DisconnectedError = "Not connected to Garry's Mod";

        /// <summary>
        /// How long to wait for a command's first console line before giving up on it printing
        /// anything. Expiring is NOT a failure - plenty of commands succeed silently.
        /// </summary>
        private const int DefaultFirstOutputGraceMs = 2000;

        private const int PollIntervalMs = 25;

        private readonly ILogListener Listener;
        private readonly int CollectionWindowMs;
        private readonly object Locker = new();

        private bool IsCollecting = false;
        private bool HasReceivedFirstOutput = false;
        private DateTime? CollectionStart;
        private readonly ConcurrentQueue<LogEventArgs> CollectedOutput = new();
        private readonly List<Sentinel> CollectedSentinels = [];
        private readonly HashSet<string> SeenMarkers = new(StringComparer.Ordinal);

        internal CommandCollector(ILogListener listener, int collectionWindowMs = 1000)
        {
            this.Listener = listener;
            this.CollectionWindowMs = collectionWindowMs;
            this.Listener.OnLog += OnLogReceived;
        }

        internal bool IsBusy
        {
            get { lock (this.Locker) return this.IsCollecting; }
        }

        private void OnLogReceived(object sender, LogEventArgs args)
        {
            lock (this.Locker)
            {
                if (!this.IsCollecting) return;

                // First output received - start the timer
                if (!this.HasReceivedFirstOutput)
                {
                    this.HasReceivedFirstOutput = true;
                    this.CollectionStart = DateTime.Now;
                    LocalLogger.WriteLine($"First output received, starting {this.CollectionWindowMs}ms collection window");
                }

                // Sentinels are GTerm's own out-of-band signalling. They are results, not console
                // output, so they are kept apart and never surface to callers as log lines.
                if (GTermSentinels.IsSentinel(args.Message))
                {
                    if (GTermSentinels.TryParse(args.Message, out Sentinel sentinel))
                    {
                        this.CollectedSentinels.Add(sentinel);
                        this.SeenMarkers.Add(sentinel.Marker);
                    }

                    return;
                }

                this.CollectedOutput.Enqueue(args);
            }
        }

        private void BeginCollecting(bool skipFirstOutputWait)
        {
            this.IsCollecting = true;
            this.HasReceivedFirstOutput = skipFirstOutputWait;
            this.CollectionStart = skipFirstOutputWait ? DateTime.Now : null;
            this.CollectedOutput.Clear();
            this.CollectedSentinels.Clear();
            this.SeenMarkers.Clear();
        }

        private void EndCollecting()
        {
            lock (this.Locker)
            {
                this.IsCollecting = false;
                this.HasReceivedFirstOutput = false;
                this.CollectionStart = null;
            }
        }

        private (List<OutputLine> Output, List<Sentinel> Sentinels, double DurationMs, bool ProducedOutput) Drain()
        {
            lock (this.Locker)
            {
                double duration = this.CollectionStart.HasValue
                    ? (DateTime.Now - this.CollectionStart.Value).TotalMilliseconds
                    : 0;

                List<OutputLine> output = [];
                while (this.CollectedOutput.TryDequeue(out LogEventArgs? logEvent))
                {
                    if (logEvent == null) continue;

                    output.Add(new OutputLine
                    {
                        Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                        Message = logEvent.Message,
                        Color = new ColorInfo
                        {
                            R = logEvent.Color.R,
                            G = logEvent.Color.G,
                            B = logEvent.Color.B,
                            A = logEvent.Color.A,
                        }
                    });
                }

                bool produced = this.HasReceivedFirstOutput;
                return (output, [.. this.CollectedSentinels], duration, produced);
            }
        }

        public async Task<CommandResult> CaptureConsoleAsync(int durationMs, CancellationToken cancellationToken = default)
        {
            string label = $"capture_{durationMs}ms";

            if (!this.Listener.IsConnected)
            {
                return new CommandResult { Success = false, Command = label, Error = DisconnectedError };
            }

            lock (this.Locker)
            {
                if (this.IsCollecting)
                {
                    return new CommandResult
                    {
                        Success = false,
                        Command = label,
                        Error = "Another command or capture is currently in progress"
                    };
                }

                BeginCollecting(skipFirstOutputWait: true);
            }

            try
            {
                LocalLogger.WriteLine($"Capturing console output for {durationMs}ms");
                await Task.Delay(durationMs, cancellationToken);

                (List<OutputLine> output, List<Sentinel> sentinels, double duration, bool produced) = Drain();

                LocalLogger.WriteLine($"Capture completed. Collected {output.Count} output lines in {duration:F0}ms");

                return new CommandResult
                {
                    Success = true,
                    Command = label,
                    Output = output,
                    Sentinels = sentinels,
                    ProducedOutput = produced,
                    CollectionDurationMs = duration,
                };
            }
            catch (Exception ex)
            {
                return new CommandResult { Success = false, Command = label, Error = $"Exception: {ex.Message}" };
            }
            finally
            {
                EndCollecting();
            }
        }

        /// <summary>
        /// Sends a console command and collects what the game prints back.
        /// </summary>
        /// <param name="completionMarkers">
        /// When any of these sentinel markers appears, stop collecting immediately rather than
        /// waiting out the whole window.
        /// </param>
        public async Task<CommandResult> ExecuteCommandAsync(
            string command,
            int? customCollectionWindowMs = null,
            IReadOnlyCollection<string>? completionMarkers = null,
            int firstOutputGraceMs = DefaultFirstOutputGraceMs,
            CancellationToken cancellationToken = default)
        {
            if (!this.Listener.IsConnected)
            {
                return new CommandResult { Success = false, Command = command, Error = DisconnectedError };
            }

            lock (this.Locker)
            {
                if (this.IsCollecting)
                {
                    return new CommandResult { Success = false, Command = command, Error = BusyError };
                }

                BeginCollecting(skipFirstOutputWait: false);
            }

            try
            {
                LocalLogger.WriteLine($"Executing command: {command}");

                await this.Listener.WriteMessage(command);

                CommandResult? cancelled = await WaitForFirstOutput(command, firstOutputGraceMs, cancellationToken);
                if (cancelled != null) return cancelled;

                bool sawOutput;
                lock (this.Locker) sawOutput = this.HasReceivedFirstOutput;

                if (sawOutput)
                {
                    cancelled = await CollectWindow(command, customCollectionWindowMs ?? this.CollectionWindowMs, completionMarkers, cancellationToken);
                    if (cancelled != null) return cancelled;
                }
                else
                {
                    LocalLogger.WriteLine($"Command produced no console output within {firstOutputGraceMs}ms (not an error)");
                }

                (List<OutputLine> output, List<Sentinel> sentinels, double duration, bool produced) = Drain();

                LocalLogger.WriteLine($"Command completed. Collected {output.Count} output lines in {duration:F0}ms");

                return new CommandResult
                {
                    Success = true,
                    Command = command,
                    Output = output,
                    Sentinels = sentinels,
                    ProducedOutput = produced,
                    CollectionDurationMs = duration,
                };
            }
            catch (Exception ex)
            {
                return new CommandResult { Success = false, Command = command, Error = $"Exception: {ex.Message}" };
            }
            finally
            {
                EndCollecting();
            }
        }

        private async Task<CommandResult?> WaitForFirstOutput(string command, int graceMs, CancellationToken cancellationToken)
        {
            DateTime startWait = DateTime.Now;
            while (true)
            {
                lock (this.Locker)
                {
                    if (this.HasReceivedFirstOutput) return null;
                }

                if ((DateTime.Now - startWait).TotalMilliseconds >= graceMs) return null;
                if (cancellationToken.IsCancellationRequested) return Cancelled(command);

                await Task.Delay(PollIntervalMs, cancellationToken);
            }
        }

        private async Task<CommandResult?> CollectWindow(string command, int windowMs, IReadOnlyCollection<string>? completionMarkers, CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (this.Locker)
                {
                    if (completionMarkers != null)
                    {
                        foreach (string marker in completionMarkers)
                        {
                            // The command signalled that it is done; no reason to wait out the window.
                            if (this.SeenMarkers.Contains(marker)) return null;
                        }
                    }

                    if (this.CollectionStart.HasValue
                        && (DateTime.Now - this.CollectionStart.Value).TotalMilliseconds >= windowMs)
                    {
                        return null;
                    }
                }

                if (cancellationToken.IsCancellationRequested) return Cancelled(command);

                await Task.Delay(PollIntervalMs, cancellationToken);
            }
        }

        private static CommandResult Cancelled(string command)
            => new() { Success = false, Command = command, Error = "Command execution cancelled" };
    }
}
