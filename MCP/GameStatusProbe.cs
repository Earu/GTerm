using GTerm.Listeners;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace GTerm.MCP
{
    /// <summary>
    /// Owns the cached view of what the running game is doing, and is the only thing that issues
    /// probe commands.
    ///
    /// The collector is exclusive - one command or capture at a time - so probing on every tool call
    /// would double latency and multiply collisions. Instead the cache is refreshed on connect, on
    /// explicit request, and for free whenever a real tool call succeeds. Rendering a status header
    /// never probes.
    /// </summary>
    internal sealed class GameStatusProbe
    {
        private const int ProbeWindowMs = 800;
        private const int ProbeGraceMs = 1500;

        /// <summary>Console lines that mean the world changed under us, so the snapshot is suspect.</summary>
        private static readonly Regex InvalidatingLine = new(
            @"Host_NewGame|^Map:\s|Server shutting down|^Disconnect:",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly string ServerProbeCommand =
            "lua_run " + GTermSentinels.LuaEmit(GTermSentinels.Status,
                "util.TableToJSON({m=game.GetMap(),g=engine.ActiveGamemode(),mp=game.MaxPlayers(),pc=player.GetCount(),d=game.IsDedicated(),sp=game.SinglePlayer()})");

        // Everything here is available client-side, so the client probe alone fully describes the
        // session even when we are a client on a remote server and the server realm is unreachable.
        private static readonly string ClientProbeCommand =
            "lua_run_cl " + GTermSentinels.LuaEmit(GTermSentinels.Client,
                "util.TableToJSON({m=game.GetMap(),g=engine.ActiveGamemode(),mp=game.MaxPlayers(),pc=player.GetCount(),lp=IsValid(LocalPlayer())})");

        private readonly ILogListener Listener;
        private readonly CommandCollector Collector;
        private readonly object Locker = new();

        private GameStatus Cached = new();
        private Task<GameStatus>? InFlight;

        internal GameStatusProbe(ILogListener listener, CommandCollector collector)
        {
            this.Listener = listener;
            this.Collector = collector;

            this.Listener.OnConnected += OnConnected;
            this.Listener.OnDisconnected += OnDisconnected;
            this.Listener.OnLog += OnLog;
        }

        /// <summary>
        /// The snapshot as best we know it. Never touches the game. Connection state is authoritative
        /// and overrides whatever the last probe said.
        /// </summary>
        internal GameStatus GetCached()
        {
            lock (this.Locker)
            {
                if (!this.Listener.IsConnected)
                {
                    return this.Cached.State == GameConnState.Disconnected
                        ? this.Cached
                        : new GameStatus { State = GameConnState.Disconnected, GmodProcessRunning = GmodInterop.IsGmodRunning() };
                }

                if (this.Cached.State == GameConnState.Disconnected || !this.Cached.CapturedAt.HasValue)
                {
                    return new GameStatus { State = GameConnState.Unverified, GmodProcessRunning = true };
                }

                return this.Cached;
            }
        }

        /// <summary>
        /// Probes both realms. Concurrent callers share one probe. If the collector is busy with a
        /// real command, falls back to the cache rather than failing or queueing behind it.
        /// </summary>
        internal Task<GameStatus> RefreshAsync(CancellationToken cancellationToken = default)
        {
            if (!this.Listener.IsConnected)
            {
                GameStatus disconnected = new() { State = GameConnState.Disconnected, GmodProcessRunning = GmodInterop.IsGmodRunning() };
                lock (this.Locker) this.Cached = disconnected;
                return Task.FromResult(disconnected);
            }

            lock (this.Locker)
            {
                if (this.InFlight != null) return this.InFlight;
                this.InFlight = ProbeAsync(cancellationToken);
                return this.InFlight;
            }
        }

        /// <summary>
        /// A real command round-tripped, so the game is demonstrably live right now. Keeps the
        /// snapshot warm for free without spending a probe.
        /// </summary>
        internal void NoteLiveActivity()
        {
            lock (this.Locker)
            {
                if (this.Cached.State != GameConnState.Live) return;

                this.Cached = Clone(this.Cached, capturedAt: DateTime.Now, stale: false);
            }
        }

        private async Task<GameStatus> ProbeAsync(CancellationToken cancellationToken)
        {
            try
            {
                CommandResult server = await this.Collector.ExecuteCommandAsync(
                    ServerProbeCommand, ProbeWindowMs, [GTermSentinels.Status], ProbeGraceMs, cancellationToken);

                if (IsBusy(server)) return CacheWithNote("could not refresh: another command is in progress");

                CommandResult client = await this.Collector.ExecuteCommandAsync(
                    ClientProbeCommand, ProbeWindowMs, [GTermSentinels.Client], ProbeGraceMs, cancellationToken);

                if (IsBusy(client)) return CacheWithNote("could not refresh: another command is in progress");

                bool serverOk = server.Sentinels.Any(s => s.Marker == GTermSentinels.Status);
                bool clientOk = client.Sentinels.Any(s => s.Marker == GTermSentinels.Client);

                // Neither Lua realm answered. That is ambiguous: it could be the main menu, OR a client
                // joined to a server where sv_allowcslua is 0 (client blocked) and the server realm is
                // remote. `status` is an engine command that prints even when Lua is refused, so it
                // tells the two apart.
                CommandResult? statusResult = null;
                if (!serverOk && !clientOk)
                {
                    statusResult = await this.Collector.ExecuteCommandAsync("status", ProbeWindowMs, null, ProbeGraceMs, cancellationToken);
                    if (IsBusy(statusResult)) return CacheWithNote("could not refresh: another command is in progress");
                }

                GameStatus status = Interpret(server, client, statusResult);
                lock (this.Locker) this.Cached = status;

                return status;
            }
            catch (Exception ex)
            {
                LocalLogger.WriteLine($"Status probe failed: {ex.Message}");
                return CacheWithNote($"could not refresh: {ex.Message}");
            }
            finally
            {
                lock (this.Locker) this.InFlight = null;
            }
        }

        private static bool IsBusy(CommandResult result)
            => !result.Success && result.Error != null && result.Error.Contains("currently", StringComparison.OrdinalIgnoreCase);

        private static GameStatus Interpret(CommandResult server, CommandResult client, CommandResult? statusFallback)
        {
            bool serverOk = server.TryGetSentinel(GTermSentinels.Status, out Sentinel serverSentinel);
            bool clientOk = client.TryGetSentinel(GTermSentinels.Client, out Sentinel clientSentinel);

            JObject? sv = serverOk ? TryParseJson(serverSentinel.Payload) : null;
            JObject? cl = clientOk ? TryParseJson(clientSentinel.Payload) : null;

            // A concommand that is not registered still prints "Unknown command", so silence is not
            // the signal. The absence of our sentinel is.
            if (!serverOk && !clientOk)
            {
                ServerStatus? parsed = statusFallback != null ? ParseStatus(statusFallback) : null;

                // `status` reported a real session, so we ARE in a game — Lua is just refused in both
                // realms (server remote + client blocked by sv_allowcslua). Not a main-menu NoSession.
                if (parsed != null)
                {
                    return new GameStatus
                    {
                        State = GameConnState.Live,
                        GmodProcessRunning = true,
                        CapturedAt = DateTime.Now,
                        Map = parsed.Map,
                        PlayerCount = parsed.Players,
                        MaxPlayers = parsed.MaxPlayers,
                        ClientRealm = new RealmState { Reach = RealmReach.Blocked, Reason = "sv_allowcslua=0 or script enforcer" },
                        ServerRealm = new RealmState { Reach = RealmReach.Unreachable, Reason = "no local server state — joined to a remote server" },
                        Note = "connected to a server but no Lua realm will run code (sv_allowcslua=0 and server realm is remote)",
                    };
                }

                return new GameStatus
                {
                    State = GameConnState.NoSession,
                    GmodProcessRunning = true,
                    CapturedAt = DateTime.Now,
                    ClientRealm = new RealmState { Reach = RealmReach.Unreachable, Reason = "no session" },
                    ServerRealm = new RealmState { Reach = RealmReach.Unreachable, Reason = "no session" },
                };
            }

            bool? isDedicated = sv?["d"]?.Value<bool>();

            RealmState serverRealm = serverOk
                ? new RealmState { Reach = RealmReach.Ok }
                : new RealmState { Reach = RealmReach.Unreachable, Reason = "no local server state — main menu, or joined to a remote server" };

            RealmState clientRealm;
            if (clientOk)
            {
                clientRealm = new RealmState { Reach = RealmReach.Ok };
            }
            else if (isDedicated == true)
            {
                clientRealm = new RealmState { Reach = RealmReach.Unreachable, Reason = "no client on a dedicated server" };
            }
            else
            {
                // The server realm answered, so the game is live and running our commands. The client
                // realm staying silent overwhelmingly means script enforcement refused it.
                clientRealm = new RealmState { Reach = RealmReach.Blocked, Reason = "sv_allowcslua=0 or script enforcer" };
            }

            // Prefer server-probe fields; fall back to the client probe, which now carries the same
            // gameplay info and is our only source when joined to a remote server.
            return new GameStatus
            {
                State = GameConnState.Live,
                GmodProcessRunning = true,
                CapturedAt = DateTime.Now,
                Map = sv?["m"]?.ToString() ?? cl?["m"]?.ToString(),
                Gamemode = sv?["g"]?.ToString() ?? cl?["g"]?.ToString(),
                PlayerCount = sv?["pc"]?.Value<int>() ?? cl?["pc"]?.Value<int>(),
                MaxPlayers = sv?["mp"]?.Value<int>() ?? cl?["mp"]?.Value<int>(),
                IsDedicated = isDedicated,
                SinglePlayer = sv?["sp"]?.Value<bool>(),
                ClientRealm = clientRealm,
                ServerRealm = serverRealm,
            };
        }

        private sealed record ServerStatus(string? Map, int? Players, int? MaxPlayers);

        private static readonly Regex StatusMapLine = new(
            @"^map\s*:\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex StatusPlayersLine = new(
            @"players\s*:\s*(\d+)\s*humans.*?\((\d+)\s*max\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Parses a Source `status` block. Returns null when the output is not a status block
        /// (e.g. at the main menu, where `status` prints nothing useful).</summary>
        private static ServerStatus? ParseStatus(CommandResult result)
        {
            string text = string.Join("\n", result.Output.Select(o => o.Message));

            Match map = StatusMapLine.Match(text);
            if (!map.Success) return null;

            Match players = StatusPlayersLine.Match(text);
            int? count = players.Success ? int.Parse(players.Groups[1].Value) : null;
            int? max = players.Success ? int.Parse(players.Groups[2].Value) : null;

            return new ServerStatus(map.Groups[1].Value, count, max);
        }

        private static JObject? TryParseJson(string payload)
        {
            try
            {
                return JObject.Parse(payload);
            }
            catch (Exception ex)
            {
                LocalLogger.WriteLine($"Could not parse probe payload '{payload}': {ex.Message}");
                return null;
            }
        }

        private GameStatus CacheWithNote(string note)
        {
            lock (this.Locker) return Clone(this.Cached, note: note);
        }

        private static GameStatus Clone(GameStatus source, DateTime? capturedAt = null, bool? stale = null, string? note = null) => new()
        {
            State = source.State,
            GmodProcessRunning = source.GmodProcessRunning,
            Map = source.Map,
            Gamemode = source.Gamemode,
            PlayerCount = source.PlayerCount,
            MaxPlayers = source.MaxPlayers,
            IsDedicated = source.IsDedicated,
            SinglePlayer = source.SinglePlayer,
            ClientRealm = source.ClientRealm,
            ServerRealm = source.ServerRealm,
            CapturedAt = capturedAt ?? source.CapturedAt,
            Stale = stale ?? source.Stale,
            Note = note ?? source.Note,
        };

        private void OnConnected(object? sender, EventArgs args)
        {
            // Give the module a moment to settle before spending a probe on it.
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                if (this.Listener.IsConnected) await RefreshAsync();
            });
        }

        private void OnDisconnected(object? sender, EventArgs args)
        {
            lock (this.Locker)
            {
                this.Cached = new GameStatus { State = GameConnState.Disconnected, GmodProcessRunning = GmodInterop.IsGmodRunning() };
            }
        }

        private void OnLog(object sender, LogEventArgs args)
        {
            if (!InvalidatingLine.IsMatch(args.Message)) return;

            lock (this.Locker)
            {
                if (this.Cached.State != GameConnState.Live || this.Cached.Stale) return;
                this.Cached = Clone(this.Cached, stale: true);
            }
        }
    }
}
