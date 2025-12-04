using GTerm.Listeners;
using System.Collections.Concurrent;

namespace GTerm
{
    internal class CommandResult
    {
        public bool Success { get; set; }
        public string Command { get; set; } = string.Empty;
        public List<OutputLine> Output { get; set; } = [];
        public double CollectionDurationMs { get; set; }
        public string? Error { get; set; }
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
        private readonly ILogListener Listener;
        private readonly int CollectionWindowMs;
        private readonly object Locker = new();

        private bool IsCollecting = false;
        private bool HasReceivedFirstOutput = false;
        private DateTime? CollectionStart;
        private readonly ConcurrentQueue<LogEventArgs> CollectedOutput = new();

        internal CommandCollector(ILogListener listener, int collectionWindowMs = 1000)
        {
            this.Listener = listener;
            this.CollectionWindowMs = collectionWindowMs;
            this.Listener.OnLog += OnLogReceived;
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

                // Add to collected output
                this.CollectedOutput.Enqueue(args);
            }
        }

        public async Task<CommandResult> CaptureConsoleAsync(int durationMs, CancellationToken cancellationToken = default)
        {
            lock (this.Locker)
            {
                if (this.IsCollecting)
                {
                    return new CommandResult
                    {
                        Success = false,
                        Command = $"capture_{durationMs}ms",
                        Error = "Another command or capture is currently in progress"
                    };
                }

                // Start collecting immediately
                this.IsCollecting = true;
                this.HasReceivedFirstOutput = true; // Skip waiting for first output
                this.CollectionStart = DateTime.Now;
                this.CollectedOutput.Clear();
            }

            try
            {
                LocalLogger.WriteLine($"Capturing console output for {durationMs}ms");
                await Task.Delay(durationMs, cancellationToken);

                double totalDuration = this.CollectionStart.HasValue
                    ? (DateTime.Now - this.CollectionStart.Value).TotalMilliseconds
                    : 0;

                List<OutputLine> output = [];
                while (this.CollectedOutput.TryDequeue(out LogEventArgs? logEvent))
                {
                    if (logEvent != null)
                    {
                        output.Add(new OutputLine
                        {
                            Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                            Message = logEvent.Message,
                            Color = new ColorInfo
                            {
                                R = logEvent.Color.R,
                                G = logEvent.Color.G,
                                B = logEvent.Color.B,
                                A = logEvent.Color.A
                            }
                        });
                    }
                }

                LocalLogger.WriteLine($"Capture completed. Collected {output.Count} output lines in {totalDuration:F0}ms");

                return new CommandResult
                {
                    Success = true,
                    Command = $"capture_{durationMs}ms",
                    Output = output,
                    CollectionDurationMs = totalDuration
                };
            }
            catch (Exception ex)
            {
                return new CommandResult
                {
                    Success = false,
                    Command = $"capture_{durationMs}ms",
                    Error = $"Exception: {ex.Message}"
                };
            }
            finally
            {
                lock (this.Locker)
                {
                    this.IsCollecting = false;
                    this.HasReceivedFirstOutput = false;
                    this.CollectionStart = null;
                }
            }
        }

        public async Task<CommandResult> ExecuteCommandAsync(string command, int? customCollectionWindowMs = null, CancellationToken cancellationToken = default)
        {
            if (!this.Listener.IsConnected)
            {
                return new CommandResult
                {
                    Success = false,
                    Command = command,
                    Error = "Not connected to Garry's Mod"
                };
            }

            lock (this.Locker)
            {
                if (this.IsCollecting)
                {
                    return new CommandResult
                    {
                        Success = false,
                        Command = command,
                        Error = "Another command is currently being executed"
                    };
                }

                // Start collecting
                this.IsCollecting = true;
                this.HasReceivedFirstOutput = false;
                this.CollectionStart = null;
                this.CollectedOutput.Clear();
            }

            try
            {
                LocalLogger.WriteLine($"Executing command: {command}");

                // Send the command
                await this.Listener.WriteMessage(command);

                // Wait for first output (max 10 seconds timeout)
                DateTime startWait = DateTime.Now;
                while (!this.HasReceivedFirstOutput)
                {
                    if ((DateTime.Now - startWait).TotalSeconds > 10)
                    {
                        return new CommandResult
                        {
                            Success = false,
                            Command = command,
                            Error = "Timeout waiting for first output (10s)"
                        };
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new CommandResult
                        {
                            Success = false,
                            Command = command,
                            Error = "Command execution cancelled"
                        };
                    }

                    await Task.Delay(50, cancellationToken);
                }

                // Now wait for the collection window
                int collectionWindow = customCollectionWindowMs ?? this.CollectionWindowMs;
                while (true)
                {
                    lock (this.Locker)
                    {
                        if (this.CollectionStart.HasValue)
                        {
                            double elapsed = (DateTime.Now - this.CollectionStart.Value).TotalMilliseconds;
                            if (elapsed >= collectionWindow)
                            {
                                break;
                            }
                        }
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new CommandResult
                        {
                            Success = false,
                            Command = command,
                            Error = "Command execution cancelled"
                        };
                    }

                    await Task.Delay(50, cancellationToken);
                }

                double totalDuration = this.CollectionStart.HasValue
                    ? (DateTime.Now - this.CollectionStart.Value).TotalMilliseconds
                    : 0;

                List<OutputLine> output = [];
                while (this.CollectedOutput.TryDequeue(out LogEventArgs? logEvent))
                {
                    if (logEvent != null)
                    {
                        output.Add(new OutputLine
                        {
                            Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                            Message = logEvent.Message,
                            Color = new ColorInfo
                            {
                                R = logEvent.Color.R,
                                G = logEvent.Color.G,
                                B = logEvent.Color.B,
                                A = logEvent.Color.A
                            }
                        });
                    }
                }

                LocalLogger.WriteLine($"Command completed. Collected {output.Count} output lines in {totalDuration:F0}ms");

                return new CommandResult
                {
                    Success = true,
                    Command = command,
                    Output = output,
                    CollectionDurationMs = totalDuration
                };
            }
            catch (Exception ex)
            {
                return new CommandResult
                {
                    Success = false,
                    Command = command,
                    Error = $"Exception: {ex.Message}"
                };
            }
            finally
            {
                lock (this.Locker)
                {
                    this.IsCollecting = false;
                    this.HasReceivedFirstOutput = false;
                    this.CollectionStart = null;
                }
            }
        }
    }
}




