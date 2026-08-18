using GTerm.Listeners;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace GTerm.MCP
{
    internal partial class MCPServer
    {
        private const string ServerName = "gterm";
        private const string ServerVersion = "2.0.0";

        /// <summary>Newest first. We echo the client's version when we know it, else answer with ours.</summary>
        private static readonly string[] SupportedProtocolVersions =
        [
            "2025-11-25",
            "2025-06-18",
            "2025-03-26",
            "2024-11-05",
        ];

        /// <summary>
        /// Claude Code truncates server instructions at 2KB, so this stays tight and front-loads the
        /// rules that agents get wrong: check status, name a realm, never assume disk == game, and
        /// never reach for a screenshot to settle something Lua can answer outright.
        /// </summary>
        private const string Instructions = """
GTerm bridges to a running Garry's Mod via its console command buffer. Nothing works unless GMod is CONNECTED and a Lua realm is reachable.

1. Every tool result starts with a [GTerm] status line. READ IT. If DISCONNECTED or NO_SESSION, stop and tell the user - do not retry blindly.
2. Call get_game_status first when unsure. It works even when disconnected.
3. Realms. execute_lua_code REQUIRES realm="client" (HUD, rendering, input) or "server" (entities, gamemode logic); "menu" is unreachable by any console command and errors. server is dead at the main menu and when joined to a remote server; client is dead on a dedicated server and whenever sv_allowcslua is 0. get_game_status reports which realms are reachable - trust it over assumption.
4. Disk vs game. read_gmod_file/list_gmod_directory read ON DISK; read_game_file/check_game_file read the RUNNING game's virtual filesystem (mounted addons, GMAs), and the client realm only sees local files. Edits do not go live until reloaded: execute_lua_code with include("path").
5. Validate before executing. validate_lua_syntax compile-checks without running anything. read_gmod_wiki gives a GLua function's real signature.
6. Screenshots are a LAST RESORT, not a check. Prove claims with Lua state, cvars and arithmetic first (ScrW/ScrH, panel:GetBounds, ent:GetPos, IsValid). Capture pixels only when the user asks or the question is truly visual - then use take_screenshot_region on the exact area, never a full frame. Both need the client realm.
7. Precondition failures return isError with a status snapshot and a one-line fix. Pass force=true only when certain, and say why.
8. Console output is asynchronous. capture_console_output looks BACKWARDS: recent output, newest-first, instantly - call it after an action to see prints. Raise execute_lua_code's timeout for delayed prints. A command that prints nothing still succeeded.
""";

        /// <summary>Both screenshot tools are client Lua by construction, so "try the other realm" is bad advice.</summary>
        private const string ScreenshotRealmAdvice =
            "Screen capture is always client Lua, so there is no other realm to fall back to. "
            + "If you cannot enable it, read the state you were going to look at with execute_lua_code on the server realm instead.";

        private readonly CommandCollector Collector;
        private readonly LuaExecutor LuaExecutor;
        private readonly ScreenshotCapturer Screenshot;
        private readonly ConsoleHistory History;
        private readonly ILogListener Listener;
        private readonly GameStatusProbe Status;
        private readonly int Port;
        private readonly string? Secret;
        private bool IsRunning = false;

        internal MCPServer(CommandCollector collector, ILogListener listener, int port, string? secret)
        {
            this.Collector = collector;
            this.Listener = listener;
            this.LuaExecutor = new LuaExecutor(collector);
            this.Screenshot = new ScreenshotCapturer(this.LuaExecutor);
            this.History = new ConsoleHistory(listener);
            this.Status = new GameStatusProbe(listener, collector);
            this.Port = port;
            this.Secret = secret;
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            this.IsRunning = true;

            HttpListener listener = new();
            listener.Prefixes.Add($"http://localhost:{this.Port}/");

            try
            {
                listener.Start();
                LocalLogger.WriteLine($"MCP Server started on http://localhost:{this.Port}/");
            }
            catch (Exception ex)
            {
                LocalLogger.WriteLine($"Failed to start MCP Server: {ex.Message}");
                return;
            }

            while (this.IsRunning && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    HttpListenerContext context = await listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(context), cancellationToken);
                }
                catch (HttpListenerException)
                {
                    // Listener was stopped
                    break;
                }
                catch (Exception ex)
                {
                    LocalLogger.WriteLine($"MCP Server error: {ex.Message}");
                }
            }

            listener.Stop();
            LocalLogger.WriteLine("MCP Server stopped");
        }

        public void Stop()
        {
            this.IsRunning = false;
        }

        private async Task HandleRequest(HttpListenerContext context)
        {
            try
            {
                HttpListenerRequest request = context.Request;
                HttpListenerResponse response = context.Response;

                response.AddHeader("Access-Control-Allow-Origin", "*");
                response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Mcp-Session-Id, MCP-Protocol-Version");

                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 200;
                    response.Close();
                    return;
                }

                // The MCP transport spec requires validating Origin to blunt DNS-rebinding attacks.
                // Non-browser clients send no Origin at all, which is fine.
                if (!IsOriginAllowed(request.Headers["Origin"]))
                {
                    LocalLogger.WriteLine($"MCP request rejected: disallowed Origin '{request.Headers["Origin"]}'");
                    await SendPlain(response, 403, "Forbidden");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(this.Secret))
                {
                    string? providedSecret = request.QueryString["secret"];
                    if (providedSecret != this.Secret)
                    {
                        LocalLogger.WriteLine("MCP request rejected: invalid or missing secret");
                        await SendPlain(response, 403, "Forbidden");
                        return;
                    }
                }

                string? requestedVersion = request.Headers["MCP-Protocol-Version"];
                if (!string.IsNullOrWhiteSpace(requestedVersion) && !SupportedProtocolVersions.Contains(requestedVersion))
                {
                    LocalLogger.WriteLine($"MCP request rejected: unsupported protocol version '{requestedVersion}'");
                    await SendPlain(response, 400, $"Unsupported MCP-Protocol-Version: {requestedVersion}");
                    return;
                }

                string path = request.Url?.AbsolutePath ?? "/";
                LocalLogger.WriteLine($"MCP request: {request.HttpMethod} {path}");

                if (request.HttpMethod == "POST")
                {
                    await HandlePostRequest(request, response);
                }
                else
                {
                    // We answer POSTs with a single JSON body and never open an SSE stream, so GET
                    // (which clients use to request one) has nothing to offer.
                    response.AddHeader("Allow", "POST");
                    response.StatusCode = 405;
                    response.Close();
                }
            }
            catch (Exception ex)
            {
                LocalLogger.WriteLine($"Error handling MCP request: {ex.Message}");
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch { }
            }
        }

        private static bool IsOriginAllowed(string? origin)
        {
            if (string.IsNullOrWhiteSpace(origin)) return true;

            return Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
                && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
        }

        private async Task HandlePostRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            JToken? id = null;
            try
            {
                string body;
                using (StreamReader reader = new(request.InputStream, request.ContentEncoding))
                {
                    body = await reader.ReadToEndAsync();
                }

                LocalLogger.WriteLine($"MCP POST body: {body}");

                JObject? jsonRequest = JsonConvert.DeserializeObject<JObject>(body);
                if (jsonRequest == null)
                {
                    await SendError(response, -32700, "Parse error", null);
                    return;
                }

                string? method = jsonRequest["method"]?.ToString();
                id = jsonRequest["id"];

                // A JSON-RPC message with no id is a notification: acknowledge it and send no body.
                // notifications/initialized arrives on every session, and answering it with an error
                // is a protocol violation.
                if (id == null || id.Type == JTokenType.Null)
                {
                    LocalLogger.WriteLine($"MCP notification: {method}");
                    response.StatusCode = 202;
                    response.ContentLength64 = 0;
                    response.Close();
                    return;
                }

                try
                {
                    object? result = method switch
                    {
                        "initialize" => HandleInitialize(jsonRequest),
                        "tools/list" => HandleToolsList(),
                        "tools/call" => await HandleToolCall(jsonRequest),
                        "ping" => new { },
                        _ => throw new Exception($"Unknown method: {method}")
                    };

                    await SendResponse(response, result, id);
                }
                catch (Exception ex)
                {
                    LocalLogger.WriteLine($"Error executing method {method}: {ex.Message}");
                    await SendError(response, -32603, ex.Message, id);
                }
            }
            catch (Exception ex)
            {
                LocalLogger.WriteLine($"Error in POST handler: {ex.Message}");
                await SendError(response, -32700, ex.Message, id);
            }
        }

        private static object HandleInitialize(JObject request)
        {
            string? requested = request["params"]?["protocolVersion"]?.ToString();
            string negotiated = requested != null && SupportedProtocolVersions.Contains(requested)
                ? requested
                : SupportedProtocolVersions[0];

            return new
            {
                protocolVersion = negotiated,
                serverInfo = new
                {
                    name = ServerName,
                    version = ServerVersion
                },
                capabilities = new
                {
                    tools = new { }
                },
                instructions = Instructions,
            };
        }

        #region Tool result helpers

        /// <summary>Every tool result is stamped with the current status, so the agent cannot act blind.</summary>
        private object Ok(string body) => new
        {
            content = new[] { new { type = "text", text = $"{this.Status.GetCached().ToHeader()}\n\n{body}" } }
        };

        /// <summary>
        /// A tool-level failure. The MCP spec wants these inside the result with isError, not as a
        /// JSON-RPC error, so the model can see what went wrong and self-correct.
        /// </summary>
        private object Err(string message) => new
        {
            content = new[] { new { type = "text", text = $"{this.Status.GetCached().ToHeader()}\n\nERROR: {message}" } },
            isError = true,
        };

        /// <summary>
        /// The Lua realm a call actually runs in, for the console badge, or null when it runs in none.
        /// Only realms get a badge: a disk read or a console command has no realm to report, and
        /// inventing a label for it would dilute what the block means.
        /// </summary>
        private string? BadgeRealm(string? tool, JObject? args)
        {
            string? realm = args?["realm"]?.ToString();
            bool named = !string.IsNullOrWhiteSpace(realm);

            return tool switch
            {
                // Realm is required here, so never guess one on its behalf.
                "execute_lua_code" => named ? realm : null,

                // Realm is optional; these fall back to whichever realm is reachable.
                "validate_lua_syntax" or "check_game_file" or "read_game_file"
                    => named ? realm : DefaultRealm().ToString(),

                // Capture is client Lua by construction.
                "take_screenshot" or "take_screenshot_region" => "client",

                // Everything else touches the disk, the web, the engine's command buffer, or only
                // GTerm's own state.
                _ => null,
            };
        }

        /// <summary>
        /// The one argument worth putting in front of a human, per tool. Returns null for tools whose
        /// name already says everything (get_game_status) so the console does not fill with noise.
        /// Keeps newlines: in GTerm's own console there is room to read a Lua snippet as actual code.
        /// </summary>
        private static string? Salient(string? tool, JObject? a)
        {
            const int max = 2000;

            string? raw = tool switch
            {
                "check_game_file" or "read_gmod_file" or "list_gmod_directory" => a?["path"]?.ToString(),
                "read_game_file" => $"{a?["path"]} ({a?["searchPath"] ?? "GAME"})",
                "read_gmod_wiki" => $"wiki {a?["page"]}",
                "capture_console_output" => null,
                "take_screenshot" => $"full screen: {a?["reason"]}",
                "take_screenshot_region" => $"region {a?["x"]},{a?["y"]} {a?["width"]}x{a?["height"]}"
                    + (a?["reason"] == null ? "" : $": {a["reason"]}"),
                _ => null,
            };

            if (string.IsNullOrWhiteSpace(raw)) return null;

            raw = raw.Trim();
            return raw.Length > max ? string.Concat(raw.AsSpan(0, max), " …(truncated)") : raw;
        }

        private static bool Forced(JObject? arguments) => arguments?["force"]?.Value<bool>() ?? false;

        /// <summary>Refuses when the game cannot service the call. Returns null when it is safe to proceed.</summary>
        private object? GateConnection(JObject? arguments)
        {
            if (this.Listener.IsConnected || Forced(arguments)) return null;

            return Err("Garry's Mod is not connected to GTerm, so no command can run. "
                + "Launch GMod with the GTerm module installed and load into a game, then retry. "
                + "Pass force=true to attempt the call anyway.");
        }

        /// <summary>
        /// Refuses when the requested realm is known not to work right now.
        /// <paramref name="altAdvice"/> replaces the "use the other realm" suggestion for callers that
        /// have no realm to switch to. The screenshot tools are always client Lua, so telling them to
        /// try realm="server" would send them after a parameter that does not exist.
        /// </summary>
        private object? GateRealm(JObject? arguments, LuaRealm realm, string? altAdvice = null)
        {
            object? connectionError = GateConnection(arguments);
            if (connectionError != null) return connectionError;

            if (Forced(arguments)) return null;

            GameStatus status = this.Status.GetCached();

            // Only refuse on a snapshot we actually trust. An unverified or stale one might be wrong,
            // and a needless refusal is worse than a failed attempt.
            if (status.IsStaleNow) return null;

            if (status.State == GameConnState.NoSession)
            {
                return Err("GMod is connected but no Lua realm is responding — it is at the main menu or loading. "
                    + "Load or join a game, then retry. Pass force=true to attempt the call anyway.");
            }

            if (status.State != GameConnState.Live) return null;

            RealmState realmState = status.RealmFor(realm);
            if (realmState.IsUsable) return null;

            string realmName = realm.ToString().ToLowerInvariant();
            string other = realm == LuaRealm.Server ? "client" : "server";

            string fix = realmState.Reach == RealmReach.Blocked
                ? "sv_allowcslua defaults to 0 and gates lua_run_cl / lua_openscript_cl. Set 'sv_allowcslua 1' if you own the server"
                    + (altAdvice == null ? $", or use realm=\"{other}\"." : ".")
                : $"That realm does not exist in this process right now ({realmState.Reason}). "
                    + (altAdvice == null ? $"Use realm=\"{other}\" instead, or change what the game is doing." : "Change what the game is doing.");

            if (altAdvice != null) fix = $"{fix} {altAdvice}";

            return Err($"The {realmName} Lua realm is not usable: {realmState}. {fix} Pass force=true to attempt it anyway.");
        }

        private static bool TryParseRealm(string? raw, out LuaRealm realm, out string? error)
        {
            realm = LuaRealm.Server;
            error = null;

            switch (raw?.Trim().ToLowerInvariant())
            {
                case "server":
                    realm = LuaRealm.Server;
                    return true;

                case "client":
                    realm = LuaRealm.Client;
                    return true;

                case "menu":
                    error = "The menu realm cannot be reached. GTerm drives the game through its console command buffer, "
                        + "and no console command executes Lua in the menu realm. Use realm=\"client\" or realm=\"server\".";
                    return false;

                case null or "":
                    error = "Missing required parameter: realm. Choose \"server\" (entities, gamemode logic) or \"client\" (HUD, rendering, input). "
                        + "Call get_game_status to see which realms are reachable.";
                    return false;

                default:
                    error = $"Unknown realm '{raw}'. Valid values are \"client\" and \"server\".";
                    return false;
            }
        }

        /// <summary>Picks a realm for tools where it is optional, preferring one that is known to work.</summary>
        private LuaRealm DefaultRealm()
        {
            GameStatus status = this.Status.GetCached();
            if (status.ServerRealm.IsUsable) return LuaRealm.Server;
            if (status.ClientRealm.IsUsable) return LuaRealm.Client;
            return LuaRealm.Server;
        }

        private static void AppendOutput(StringBuilder sb, List<OutputLine> output, string emptyNote)
        {
            sb.AppendLine("Console Output:");
            sb.AppendLine("---------------");

            if (output.Count == 0)
            {
                sb.AppendLine(emptyNote);
                return;
            }

            foreach (OutputLine line in output) sb.Append($"[{line.Timestamp}] {line.Message}");
        }

        private static int ClampWindowMs(JObject? arguments, string key, double fallback, double min, double max)
        {
            double seconds = arguments?[key]?.Value<double>() ?? fallback;
            if (seconds < min) seconds = min;
            if (seconds > max) seconds = max;
            return (int)(seconds * 1000);
        }

        #endregion

        private static object HandleToolsList()
        {
            return new
            {
                tools = new object[]
                {
                    new
                    {
                        name = "get_game_status",
                        description = "Reports whether Garry's Mod is connected to GTerm, whether it is in an active session, and which Lua realms (client/server) can actually run code right now, plus map, gamemode and player count. Call this FIRST whenever you are unsure. It is safe, side-effect free, and works even while GMod is closed. By default it probes the live game; pass refresh=false to return the last cached snapshot without touching the game.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                refresh = new
                                {
                                    type = "boolean",
                                    description = "Probe the live game (default: true). false returns the cached snapshot instantly."
                                }
                            },
                            required = new string[] { }
                        },
                        annotations = new { readOnlyHint = true, destructiveHint = false, idempotentHint = true, openWorldHint = true }
                    },
                    new
                    {
                        name = "run_gmod_command",
                        description = "Executes a Garry's Mod console command and returns the captured output. Commands are injected into the engine's global command buffer, so a command only runs if the realm that registers it exists right now: 'lua_run' and 'lua_openscript' are SERVER realm, 'lua_run_cl' and 'lua_openscript_cl' are CLIENT realm and are blocked when sv_allowcslua is 0. Prefer execute_lua_code over hand-rolling lua_run, because it reports syntax and runtime errors properly. WARNING: this can run dangerous commands like 'quit', 'disconnect', or arbitrary code. A command that prints nothing still succeeded. Output may include unrelated console messages because the game console is asynchronous. PRECONDITION: GMod must be connected, else returns isError.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                command = new
                                {
                                    type = "string",
                                    description = "The console command to execute (e.g., 'status', 'sv_cheats 1'). WARNING: commands like 'lua_run' execute arbitrary code!"
                                },
                                timeout = new
                                {
                                    type = "number",
                                    description = "Seconds to collect output after the first response (default: 1, min: 0.5, max: 30)"
                                },
                                force = new
                                {
                                    type = "boolean",
                                    description = "Skip the connection precondition check and attempt the call anyway."
                                }
                            },
                            required = new[] { "command" }
                        },
                        annotations = new { readOnlyHint = false, destructiveHint = true, idempotentHint = false, openWorldHint = true }
                    },
                    new
                    {
                        name = "execute_lua_code",
                        description = "Executes Lua inside the running game and reports syntax errors, runtime errors, and console output separately. The realm argument is REQUIRED and has no default: use \"server\" for entities, gamemode logic and anything authoritative; use \"client\" for HUD, rendering and input. \"menu\" is NOT reachable through GTerm and returns an error. Client-realm execution is blocked whenever sv_allowcslua is 0, which is the default on most servers — call get_game_status to see which realms are reachable before choosing. WARNING: this runs arbitrary code in the live game. PRECONDITION: GMod connected and the chosen realm reachable, else returns isError.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                code = new
                                {
                                    type = "string",
                                    description = "The Lua code to execute (e.g., 'print(\"Hello World\")')"
                                },
                                realm = new
                                {
                                    type = "string",
                                    @enum = new[] { "client", "server", "menu" },
                                    description = "REQUIRED. Which Lua realm to run in. server = entities/gamemode logic. client = HUD/rendering/input. menu = not reachable, always errors."
                                },
                                timeout = new
                                {
                                    type = "number",
                                    description = "Seconds to KEEP LISTENING for console output after your code runs (default: 1, min: 0.5, max: 30). Errors return immediately, but on success the tool keeps collecting for this long — RAISE IT to capture prints that appear later, e.g. from timer.Simple, hooks, coroutines, net or HTTP callbacks. For output even later than this, call capture_console_output afterward."
                                },
                                force = new
                                {
                                    type = "boolean",
                                    description = "Skip the connection and realm precondition checks and attempt the call anyway."
                                }
                            },
                            required = new[] { "code", "realm" }
                        },
                        annotations = new { readOnlyHint = false, destructiveHint = true, idempotentHint = false, openWorldHint = true }
                    },
                    new
                    {
                        name = "validate_lua_syntax",
                        description = "Compile-checks Lua using the game's own CompileString WITHOUT executing it, so you can catch syntax errors safely. Prefer this over running code just to see whether it parses. Syntax is realm-agnostic, so realm defaults to whichever realm is reachable. PRECONDITION: GMod connected and a realm reachable, else returns isError.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                code = new
                                {
                                    type = "string",
                                    description = "The Lua code to compile-check. It is never executed."
                                },
                                realm = new
                                {
                                    type = "string",
                                    @enum = new[] { "client", "server" },
                                    description = "Which realm compiles the code. Defaults to a reachable realm."
                                },
                                force = new
                                {
                                    type = "boolean",
                                    description = "Skip precondition checks and attempt the call anyway."
                                }
                            },
                            required = new[] { "code" }
                        },
                        annotations = new { readOnlyHint = true, destructiveHint = false, idempotentHint = true, openWorldHint = true }
                    },
                    new
                    {
                        name = "check_game_file",
                        description = "Asks the RUNNING game whether a path exists in its virtual filesystem — which includes mounted addons, Workshop GMAs and mounted games, none of which appear on disk under garrysmod/. Use this before assuming a file you edited or read is actually visible to the game. Reports the 'GAME' search path (all mounted content) and the 'LUA' search path (the current realm's Lua path) separately, because they can disagree. PRECONDITION: GMod connected and a realm reachable, else returns isError.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                path = new
                                {
                                    type = "string",
                                    description = "Path as the game sees it, e.g. 'lua/autorun/foo.lua' for GAME, or 'autorun/foo.lua' for LUA."
                                },
                                realm = new
                                {
                                    type = "string",
                                    @enum = new[] { "client", "server" },
                                    description = "Which realm answers. Matters for the LUA search path. Defaults to a reachable realm."
                                },
                                force = new
                                {
                                    type = "boolean",
                                    description = "Skip precondition checks and attempt the call anyway."
                                }
                            },
                            required = new[] { "path" }
                        },
                        annotations = new { readOnlyHint = true, destructiveHint = false, idempotentHint = true, openWorldHint = true }
                    },
                    new
                    {
                        name = "capture_console_output",
                        description = "Returns the most recent Garry's Mod console output from GTerm's live scrollback buffer, NEWEST LINE FIRST, immediately. This looks BACKWARDS at what already printed — call it right after running a command or Lua to see the output (including asynchronous prints from timers, hooks and callbacks) without racing a capture window. It does not wait or block. GTerm's own internal probe lines are filtered out.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                lines = new
                                {
                                    type = "number",
                                    description = "How many recent lines to return, newest first (default: 50, max: 500)."
                                }
                            },
                            required = new string[] { }
                        },
                        annotations = new { readOnlyHint = true, destructiveHint = false, idempotentHint = true, openWorldHint = true }
                    },
                    new
                    {
                        name = "list_gmod_directory",
                        description = "Lists the directory structure of a path within the Garry's Mod installation ON DISK, as a tree. If no path is given, lists the root garrysmod folder. This reads the disk, NOT the running game: a file being present on disk does not mean the game has it mounted or loaded, and mounted Workshop GMAs do not appear here at all. Use check_game_file to ask the live game instead.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                path = new
                                {
                                    type = "string",
                                    description = "Relative path within the Garry's Mod installation (e.g., 'addons', 'lua/autorun', 'data'). Leave empty for root."
                                },
                                maxDepth = new
                                {
                                    type = "number",
                                    description = "Maximum depth to traverse (default: 3, max: 10)"
                                }
                            },
                            required = new string[] { }
                        },
                        annotations = new { readOnlyHint = true, destructiveHint = false, idempotentHint = true, openWorldHint = false }
                    },
                    new
                    {
                        name = "read_gmod_file",
                        description = "Reads a text file from the Garry's Mod installation ON DISK. This is the disk copy: the running game may hold a different, older, or simply unloaded version, and edits you write here do not take effect until the file is reloaded. Use check_game_file to confirm the game can see a path, and execute_lua_code with include(\"path\") to make an edit live.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                path = new
                                {
                                    type = "string",
                                    description = "Relative path to the file (e.g., 'addons/myAddon/lua/autorun/init.lua', 'cfg/server.cfg')"
                                },
                                maxSizeKB = new
                                {
                                    type = "number",
                                    description = "Maximum file size to read in KB (default: 1024, max: 10240)"
                                }
                            },
                            required = new[] { "path" }
                        },
                        annotations = new { readOnlyHint = true, destructiveHint = false, idempotentHint = true, openWorldHint = false }
                    },
                    new
                    {
                        name = "read_game_file",
                        description = "Reads a file's CONTENTS from the RUNNING game's virtual filesystem — including mounted addons, Workshop GMAs and mounted games, which read_gmod_file (disk-only) cannot see. IMPORTANT: on the CLIENT realm only files the client has LOCALLY are readable (its own addons/ or a mounted Workshop addon). A file that exists only on a server you joined is NOT sent to clients as a readable file, so it returns not-readable — read it from the SERVER realm if you are hosting. On the server realm, all server-side content is readable. PRECONDITION: GMod connected and the chosen realm reachable, else returns isError.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                path = new
                                {
                                    type = "string",
                                    description = "Path as the game sees it (e.g. 'lua/autorun/foo.lua', 'materials/x.vmt'). For the GAME search path this includes the leading folder."
                                },
                                searchPath = new
                                {
                                    type = "string",
                                    description = "Which search path to read from (default: 'GAME' = all mounted content). Others: 'LUA' (current realm's lua/), 'WORKSHOP' (mounted .gma only), 'THIRDPARTY', 'DATA', 'BSP', or a mounted addon's title."
                                },
                                realm = new
                                {
                                    type = "string",
                                    @enum = new[] { "client", "server" },
                                    description = "Which realm reads the file. server sees all server content; client sees only local content. Defaults to a reachable realm."
                                },
                                maxSizeKB = new
                                {
                                    type = "number",
                                    description = "Maximum size to return in KB (default: 1024, max: 10240). Larger files are truncated with a note."
                                },
                                force = new
                                {
                                    type = "boolean",
                                    description = "Skip precondition checks and attempt the call anyway."
                                }
                            },
                            required = new[] { "path" }
                        },
                        annotations = new { readOnlyHint = true, destructiveHint = false, idempotentHint = true, openWorldHint = true }
                    },
                    // Declared before take_screenshot on purpose: models read the tool list top-down, and
                    // the region grab is the one that should win any coin-toss between the two.
                    new
                    {
                        name = "take_screenshot_region",
                        description = "Captures ONE rectangle of the game screen and returns it enlarged, so small things are actually legible. Use this instead of take_screenshot whenever you are checking a specific element: a HUD widget, a viewmodel, a panel border, a line of text. A full-screen shot is downscaled by your client and CANNOT settle questions about occlusion, z-order, clipping or small offsets; this can. Coordinates are screen pixels with the origin at the TOP-LEFT and y growing downward, the same space as ScrW()/ScrH() and vgui panel positions. Get the real rectangle from Lua first (execute_lua_code, realm=\"client\", printing panel:GetPos() and panel:GetSize(), or the values your own drawing code uses) rather than guessing, then pad it by ~20px. An out-of-range rectangle is clamped, and the reply states the true screen size, the rectangle actually used and the zoom factor, so you can correct and call again. This still costs a real frame capture, so it does not excuse checking visually what Lua can answer directly. PRECONDITION: the CLIENT realm must be reachable, else returns isError.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                x = new
                                {
                                    type = "number",
                                    description = "Left edge in screen pixels. 0 is the LEFT of the screen. Clamped to the real frame."
                                },
                                y = new
                                {
                                    type = "number",
                                    description = "Top edge in screen pixels. 0 is the TOP of the screen; y grows downward, as in vgui."
                                },
                                width = new
                                {
                                    type = "number",
                                    description = "Region width in pixels. Include ~20px of margin around the element you are checking."
                                },
                                height = new
                                {
                                    type = "number",
                                    description = "Region height in pixels."
                                },
                                reason = new
                                {
                                    type = "string",
                                    description = "Optional: what you are checking. Recorded in GTerm's log so a human can see why pixels were needed."
                                },
                                reuseLastFrame = new
                                {
                                    type = "boolean",
                                    description = "Re-crop the frame captured in the last 60s instead of grabbing a new one (default: false). Use it to fix your coordinates without the world moving underneath you."
                                },
                                force = new
                                {
                                    type = "boolean",
                                    description = "Skip precondition checks and attempt the capture anyway."
                                }
                            },
                            required = new[] { "x", "y", "width", "height" }
                        },
                        annotations = new { readOnlyHint = true, destructiveHint = false, idempotentHint = false, openWorldHint = true }
                    },
                    new
                    {
                        name = "take_screenshot",
                        description = "LAST RESORT. Do not use this to verify something you can prove without pixels. Prefer execute_lua_code for real state (ScrW()/ScrH(), panel:GetPos/GetSize/IsVisible, ent:GetPos(), IsValid, hook and cvar values), run_gmod_command for engine state and cvars, capture_console_output for errors, and plain arithmetic for layout maths. What you get back is the WHOLE screen, and clients downscale it to about 1568px on the long edge, so occlusion, z-order, viewmodel clipping, 1-2px offsets and thin UI CANNOT be judged from it. For anything smaller than roughly a quarter of the screen, use take_screenshot_region instead. Legitimate uses: the user explicitly asked for a screenshot, or you need whole-screen context (which menu is open, is the game rendering at all). PRECONDITION: the CLIENT realm must be reachable, because capture is client Lua, so this fails when sv_allowcslua is 0, at the main menu, and on a dedicated server.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                reason = new
                                {
                                    type = "string",
                                    description = "REQUIRED. Why pixels are needed here, and which non-visual check you already tried or ruled out (Lua state, cvar, console output, arithmetic). If you cannot name one, do not call this tool."
                                },
                                quality = new
                                {
                                    type = "number",
                                    description = "JPEG quality 1-100 (default: 75). Lower shrinks the image."
                                },
                                force = new
                                {
                                    type = "boolean",
                                    description = "Skip precondition checks and attempt the capture anyway."
                                }
                            },
                            required = new[] { "reason" }
                        },
                        annotations = new { readOnlyHint = true, destructiveHint = false, idempotentHint = false, openWorldHint = true }
                    },
                    new
                    {
                        name = "read_gmod_wiki",
                        description = "Fetches a page from the official Garry's Mod wiki (wiki.facepunch.com/gmod) and returns its text — description, arguments, returns, and examples. Use this to check the real signature or behaviour of a GLua function before writing code, instead of guessing. The `page` is the exact wiki page name.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                page = new
                                {
                                    type = "string",
                                    description = "Exact wiki page name. Globals: 'Global.print'. Library functions: 'util.TableToJSON', 'file.Read'. Entity/type methods: 'Entity:SetHealth', 'Player:Nick'. Hooks: 'GM:PlayerSpawn'."
                                },
                                maxChars = new
                                {
                                    type = "number",
                                    description = "Truncate the returned text to this many characters (default: 6000)."
                                }
                            },
                            required = new[] { "page" }
                        },
                        annotations = new { readOnlyHint = true, destructiveHint = false, idempotentHint = true, openWorldHint = true }
                    }
                }
            };
        }

        private async Task<object> HandleToolCall(JObject request)
        {
            string? toolName = request["params"]?["name"]?.ToString();
            JObject? arguments = request["params"]?["arguments"] as JObject;

            LocalLogger.WriteLine($"Executing tool call: {toolName}");

            // Announce BEFORE running it: the point is to watch an agent work, not to read a receipt
            // once the game has already changed.
            string? realm = BadgeRealm(toolName, arguments);

            if (toolName is "execute_lua_code" or "validate_lua_syntax")
                Program.WriteAgentAction(toolName, arguments?["code"]?.ToString(), Program.AgentDetail.Lua, realm);
            else if (toolName == "run_gmod_command")
                Program.WriteAgentAction(toolName, arguments?["command"]?.ToString(), Program.AgentDetail.Command, realm);
            else
                Program.WriteAgentAction(toolName ?? "(unknown)", Salient(toolName, arguments), realm: realm);

            return toolName switch
            {
                "get_game_status" => await HandleGetGameStatus(arguments),
                "run_gmod_command" => await HandleRunGmodCommand(arguments),
                "execute_lua_code" => await HandleExecuteLuaCode(arguments),
                "validate_lua_syntax" => await HandleValidateLuaSyntax(arguments),
                "check_game_file" => await HandleCheckGameFile(arguments),
                "capture_console_output" => HandleCaptureConsoleOutput(arguments),
                "list_gmod_directory" => HandleListGmodDirectory(arguments),
                "read_gmod_file" => HandleReadGmodFile(arguments),
                "read_game_file" => await HandleReadGameFile(arguments),
                "take_screenshot_region" => await HandleTakeScreenshotRegion(arguments),
                "take_screenshot" => await HandleTakeScreenshot(arguments),
                "read_gmod_wiki" => await HandleReadGmodWiki(arguments),

                // Failing to find a tool is a protocol error, not a tool error.
                _ => throw new Exception($"Unknown tool: {toolName}")
            };
        }

        private async Task<object> HandleGetGameStatus(JObject? arguments)
        {
            bool refresh = arguments?["refresh"]?.Value<bool>() ?? true;

            GameStatus status = refresh
                ? await this.Status.RefreshAsync()
                : this.Status.GetCached();

            // Deliberately not Ok(): the detail body already carries everything the header would say.
            return new
            {
                content = new[] { new { type = "text", text = status.ToDetail() } }
            };
        }

        private async Task<object> HandleRunGmodCommand(JObject? arguments)
        {
            string? command = arguments?["command"]?.ToString();
            if (string.IsNullOrWhiteSpace(command)) return Err("Missing required parameter: command");

            object? gate = GateConnection(arguments);
            if (gate != null) return gate;

            int timeoutMs = ClampWindowMs(arguments, "timeout", 1.0, 0.5, 30);

            LocalLogger.WriteLine($"Running Gmod command: {command} (window: {timeoutMs}ms)");

            CommandResult result = await this.Collector.ExecuteCommandAsync(command, timeoutMs);
            if (!result.Success) return Err(result.Error ?? "Command execution failed");

            this.Status.NoteLiveActivity();

            StringBuilder outputText = new();
            outputText.AppendLine($"Command: {result.Command}");
            outputText.AppendLine($"Collection Duration: {result.CollectionDurationMs:F0}ms");
            outputText.AppendLine($"Lines Captured: {result.Output.Count}");
            outputText.AppendLine();
            AppendOutput(outputText, result.Output, "(the command printed nothing — this is normal for many commands and does NOT mean it failed)");

            return Ok(outputText.ToString());
        }

        private async Task<object> HandleExecuteLuaCode(JObject? arguments)
        {
            string? code = arguments?["code"]?.ToString();
            if (string.IsNullOrWhiteSpace(code)) return Err("Missing required parameter: code");

            if (!TryParseRealm(arguments?["realm"]?.ToString(), out LuaRealm realm, out string? realmError))
                return Err(realmError!);

            object? gate = GateRealm(arguments, realm);
            if (gate != null) return gate;

            int timeoutMs = ClampWindowMs(arguments, "timeout", 1.0, 0.5, 30);

            LocalLogger.WriteLine($"Executing Lua ({code.Length} chars, realm: {realm}, window: {timeoutMs}ms)");

            LuaScriptResult result = await this.LuaExecutor.ExecuteLuaAsync(code, realm, timeoutMs);
            if (!result.Success) return Err(result.Error ?? "Lua execution failed");

            if (result.TryGetSentinel(GTermSentinels.LuaErr, out Sentinel error))
                return Err($"Lua error in the {realm.ToString().ToLowerInvariant()} realm: {error.Payload}\n\n{RenderOutput(result)}");

            if (!result.Executed) return Err(DidNotExecute(realm, result));

            this.Status.NoteLiveActivity();

            StringBuilder sb = new();
            sb.AppendLine("Lua Execution Result");
            sb.AppendLine("====================");
            sb.AppendLine($"Realm: {realm.ToString().ToLowerInvariant()}");
            sb.AppendLine("Status: ok");
            sb.AppendLine($"Collection Duration: {result.CollectionDurationMs:F0}ms");
            sb.AppendLine($"Lines Captured: {result.Output.Count}");
            sb.AppendLine();
            AppendOutput(sb, result.Output, "(no output captured — the code ran successfully but printed nothing)");

            return Ok(sb.ToString());
        }

        private async Task<object> HandleValidateLuaSyntax(JObject? arguments)
        {
            string? code = arguments?["code"]?.ToString();
            if (string.IsNullOrWhiteSpace(code)) return Err("Missing required parameter: code");

            LuaRealm realm = DefaultRealm();
            string? rawRealm = arguments?["realm"]?.ToString();
            if (!string.IsNullOrWhiteSpace(rawRealm) && !TryParseRealm(rawRealm, out realm, out string? realmError))
                return Err(realmError!);

            object? gate = GateRealm(arguments, realm);
            if (gate != null) return gate;

            LuaScriptResult result = await this.LuaExecutor.ValidateSyntaxAsync(code, realm);
            if (!result.Success) return Err(result.Error ?? "Syntax validation failed");

            if (result.TryGetSentinel(GTermSentinels.SyntaxErr, out Sentinel error))
                return Err($"Syntax error (nothing was executed): {error.Payload}");

            if (!result.Executed) return Err(DidNotExecute(realm, result));

            this.Status.NoteLiveActivity();

            return Ok($"Syntax OK — the code compiles in the {realm.ToString().ToLowerInvariant()} realm. Nothing was executed.");
        }

        private async Task<object> HandleCheckGameFile(JObject? arguments)
        {
            string? path = arguments?["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(path)) return Err("Missing required parameter: path");

            LuaRealm realm = DefaultRealm();
            string? rawRealm = arguments?["realm"]?.ToString();
            if (!string.IsNullOrWhiteSpace(rawRealm) && !TryParseRealm(rawRealm, out realm, out string? realmError))
                return Err(realmError!);

            object? gate = GateRealm(arguments, realm);
            if (gate != null) return gate;

            LuaScriptResult result = await this.LuaExecutor.CheckGameFileAsync(path, realm);
            if (!result.Success) return Err(result.Error ?? "File check failed");

            if (!result.TryGetSentinel(GTermSentinels.File, out Sentinel file)) return Err(DidNotExecute(realm, result));

            this.Status.NoteLiveActivity();

            JObject? info = null;
            try { info = JObject.Parse(file.Payload); } catch { }
            if (info == null) return Err($"The game answered but its reply could not be parsed: {file.Payload}");

            bool gameExists = info["game_exists"]?.Value<bool>() ?? false;
            bool luaExists = info["lua_exists"]?.Value<bool>() ?? false;

            StringBuilder sb = new();
            sb.AppendLine($"Live filesystem check for '{path}' (answered by the {realm.ToString().ToLowerInvariant()} realm)");
            sb.AppendLine();
            sb.AppendLine($"GAME path (all mounted content, incl. addons and Workshop GMAs):");
            sb.AppendLine($"  exists: {gameExists}");
            if (gameExists)
            {
                sb.AppendLine($"  size:   {info["game_size"]} bytes");
                sb.AppendLine($"  isdir:  {info["game_isdir"]}");
            }
            sb.AppendLine();
            sb.AppendLine($"LUA path (this realm's Lua search path):");
            sb.AppendLine($"  exists: {luaExists}");
            if (luaExists)
            {
                sb.AppendLine($"  size:   {info["lua_size"]} bytes");
                sb.AppendLine($"  isdir:  {info["lua_isdir"]}");
            }

            if (!gameExists && !luaExists)
            {
                sb.AppendLine();
                sb.AppendLine("The running game cannot see this path at all. It may exist on disk without being mounted, "
                    + "or the path may be wrong: GAME paths include the leading folder ('lua/autorun/foo.lua') while LUA "
                    + "paths are relative to a lua/ root ('autorun/foo.lua').");
            }

            // No echo: the client renders a "path" argument inline, and the body names it anyway.
            return Ok(sb.ToString());
        }


        private object HandleCaptureConsoleOutput(JObject? arguments)
        {
            int lines = arguments?["lines"]?.Value<int>() ?? 50;
            if (lines < 1) lines = 1;
            if (lines > 500) lines = 500;

            // Read the backlog instead of waiting forward: the lines the caller wants have usually
            // already been printed, so returning recent history (newest first) never races them.
            List<OutputLine> recent = this.History.GetRecent(lines);

            LocalLogger.WriteLine($"Returning {recent.Count} recent console line(s) (newest first)");

            StringBuilder sb = new();
            sb.AppendLine("Recent Console Output (most recent first)");
            sb.AppendLine("=========================================");
            sb.AppendLine($"Showing {recent.Count} line(s) (buffer holds {this.History.Count}, requested up to {lines}).");
            sb.AppendLine();

            if (recent.Count == 0)
            {
                sb.AppendLine("(console history is empty — GMod may not be connected, or nothing has printed yet)");
            }
            else
            {
                foreach (OutputLine line in recent) sb.Append($"[{line.Timestamp}] {line.Message}");
            }

            return Ok(sb.ToString());
        }

        private object HandleListGmodDirectory(JObject? arguments)
        {
            if (!GmodInterop.TryGetGmodPath(out string gmodPath, false))
                return Err("Could not find the Garry's Mod installation path on disk.");

            string? relativePath = arguments?["path"]?.ToString() ?? "";
            int maxDepth = arguments?["maxDepth"]?.Value<int>() ?? 3;

            if (maxDepth < 1) maxDepth = 1;
            if (maxDepth > 10) maxDepth = 10;

            string fullPath = string.IsNullOrWhiteSpace(relativePath)
                ? Path.Combine(gmodPath, "garrysmod")
                : Path.Combine(gmodPath, "garrysmod", relativePath);

            LocalLogger.WriteLine($"Listing directory: {fullPath} (depth: {maxDepth})");

            return Ok(GmodFileHelper.GenerateDirectoryTree(fullPath, maxDepth));
        }

        private object HandleReadGmodFile(JObject? arguments)
        {
            if (!GmodInterop.TryGetGmodPath(out string gmodPath, false))
                return Err("Could not find the Garry's Mod installation path on disk.");

            string? relativePath = arguments?["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(relativePath)) return Err("Missing required parameter: path");

            int maxSizeKB = arguments?["maxSizeKB"]?.Value<int>() ?? 1024;

            if (maxSizeKB < 1) maxSizeKB = 1;
            if (maxSizeKB > 10240) maxSizeKB = 10240;

            string basePath = Path.Combine(gmodPath, "garrysmod");

            LocalLogger.WriteLine($"Reading file: {relativePath} (max size: {maxSizeKB}KB)");

            return Ok(GmodFileHelper.ReadFile(basePath, relativePath, maxSizeKB));
        }

        private async Task<object> HandleReadGameFile(JObject? arguments)
        {
            string? path = arguments?["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(path)) return Err("Missing required parameter: path");

            string searchPath = arguments?["searchPath"]?.ToString() is { Length: > 0 } sp ? sp : "GAME";

            LuaRealm realm = DefaultRealm();
            string? rawRealm = arguments?["realm"]?.ToString();
            if (!string.IsNullOrWhiteSpace(rawRealm) && !TryParseRealm(rawRealm, out realm, out string? realmError))
                return Err(realmError!);

            object? gate = GateRealm(arguments, realm);
            if (gate != null) return gate;

            int maxSizeKB = arguments?["maxSizeKB"]?.Value<int>() ?? 1024;
            if (maxSizeKB < 1) maxSizeKB = 1;
            if (maxSizeKB > 10240) maxSizeKB = 10240;

            string realmName = realm.ToString().ToLowerInvariant();
            LocalLogger.WriteLine($"Reading game file '{path}' via {searchPath} ({realmName} realm)");

            GameFileResult result = await this.LuaExecutor.ReadGameFileAsync(path, searchPath, realm, maxSizeKB * 1024);

            switch (result.Outcome)
            {
                case GameFileOutcome.Failed:
                    return Err(result.Error ?? "Read failed");

                case GameFileOutcome.NotExecuted:
                    return Err($"The reader never ran in the {realmName} realm. "
                        + (realm == LuaRealm.Client
                            ? "This is usually sv_allowcslua=0 blocking client Lua. Try realm=\"server\" if you are hosting."
                            : "There may be no server Lua state (main menu, or you are joined to a remote server). Try realm=\"client\"."));

                case GameFileOutcome.NotReadable:
                    return Err($"The running game cannot read '{path}' from the '{searchPath}' search path in the {realmName} realm.\n\n"
                        + (realm == LuaRealm.Client
                            ? "Your client does not have this file locally — it is readable only if it is in your own addons/ or a mounted Workshop addon. A file that exists only on a server you joined is NOT sent to clients as a readable file. If you are hosting, try realm=\"server\"."
                            : "The file does not exist at that path/search-path on the server. Try searchPath=\"GAME\" for all mounted content.")
                        + "\n\nUse check_game_file to see exactly where the game can see the path.");

                default:
                    this.Status.NoteLiveActivity();

                    StringBuilder sb = new();
                    sb.AppendLine($"Read '{path}' from the '{searchPath}' search path ({realmName} realm) — {result.Size} bytes.");
                    if (result.Truncated) sb.AppendLine($"NOTE: truncated to {maxSizeKB}KB; the file is larger. Raise maxSizeKB for more.");
                    if (result.LooksBinary) sb.AppendLine("NOTE: this looks like a binary file; the text below may be garbled.");
                    sb.AppendLine();
                    sb.AppendLine("--- contents ---");
                    sb.Append(result.Content);

                    return Ok(sb.ToString());
            }
        }

        private async Task<object> HandleTakeScreenshot(JObject? arguments)
        {
            // Capture is client Lua now, so the ordinary realm gate already writes every refusal this
            // needs (no session, sv_allowcslua 0, dedicated server), each with its own fix line.
            object? gate = GateRealm(arguments, LuaRealm.Client, ScreenshotRealmAdvice);
            if (gate != null) return gate;

            // force skips PRECONDITIONS, not deliberation: a screenshot with no stated justification
            // is the exact habit this tool is trying to break.
            string? reason = arguments?["reason"]?.ToString();
            if (string.IsNullOrWhiteSpace(reason))
            {
                return Err("Missing required parameter: reason. Say why a visual check is needed here and which "
                    + "non-visual check you already tried or ruled out. If you cannot name one, use execute_lua_code instead.");
            }

            int quality = arguments?["quality"]?.Value<int>() ?? 75;
            if (quality < 1) quality = 1;
            if (quality > 100) quality = 100;

            LocalLogger.WriteLine($"Screenshot (quality {quality}) reason: {reason}");

            ScreenshotCapturer.ShotResult shot = await this.Screenshot.CaptureAsync(quality);
            if (!shot.Success || shot.Jpeg == null) return Err(shot.Error ?? "Screenshot failed");

            this.Status.NoteLiveActivity();

            int width = shot.Width ?? 0;
            int height = shot.Height ?? 0;
            string dims = width > 0 ? $"{width}x{height}" : "unknown size";

            StringBuilder caption = new();
            caption.AppendLine(this.Status.GetCached().ToHeader());
            caption.AppendLine();
            caption.AppendLine($"Full-screen frame: {dims}, {shot.Jpeg.Length / 1024}KB, quality {quality}.");
            caption.Append(ShrinkWarning(width, height));

            // Text FIRST, then the image: a caveat placed after the picture is read after the model has
            // already formed an impression of it.
            return new
            {
                content = new object[]
                {
                    new { type = "text", text = caption.ToString() },
                    new { type = "image", data = Convert.ToBase64String(shot.Jpeg), mimeType = "image/jpeg" },
                }
            };
        }

        /// <summary>
        /// The whole reason take_screenshot keeps getting trusted too far: the frame arrives shrunk, so
        /// judging a small element from it is guesswork. Say so at the moment the model is looking.
        /// </summary>
        private static string ShrinkWarning(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return "WARNING: this is the whole screen and your client shrinks it before you see it. Do not conclude anything "
                    + "about occlusion, z-order, viewmodel clipping, small offsets or thin UI from this image. To judge a specific "
                    + "element, call take_screenshot_region on its rectangle, or read its state with execute_lua_code.";
            }

            double scale = Math.Min(1.0, 1568.0 / Math.Max(width, height));
            int seenW = (int)Math.Round(width * scale);
            int seenH = (int)Math.Round(height * scale);

            return $"WARNING: this is the whole screen and your client shrinks it to roughly {seenW}x{seenH} ({scale:0.00}x), so a 40px "
                + $"element reaches you as ~{Math.Round(40 * scale)}px. Do not conclude anything about occlusion, z-order, viewmodel "
                + "clipping, small offsets or thin UI from this image. To judge a specific element, call take_screenshot_region on its "
                + $"rectangle (coordinates inside {width}x{height}, origin top-left), or read its state with execute_lua_code.";
        }

        private async Task<object> HandleTakeScreenshotRegion(JObject? arguments)
        {
            object? gate = GateRealm(arguments, LuaRealm.Client, ScreenshotRealmAdvice);
            if (gate != null) return gate;

            if (arguments?["x"] == null || arguments["y"] == null || arguments["width"] == null || arguments["height"] == null)
                return Err("take_screenshot_region needs all four of x, y, width and height, in screen pixels with (0,0) at the top-left.");

            int x = arguments["x"]!.Value<int>();
            int y = arguments["y"]!.Value<int>();
            int width = arguments["width"]!.Value<int>();
            int height = arguments["height"]!.Value<int>();

            if (width <= 0 || height <= 0)
                return Err("width and height must both be at least 1 pixel.");

            bool reuse = arguments["reuseLastFrame"]?.Value<bool>() ?? false;
            string? reason = arguments["reason"]?.ToString();

            LocalLogger.WriteLine($"Region screenshot ({x},{y} {width}x{height}, reuse: {reuse}) reason: {reason ?? "(none given)"}");

            ScreenshotCapturer.RegionResult shot = await this.Screenshot.CaptureRegionAsync(x, y, width, height, reuse);
            if (!shot.Success || shot.Jpeg == null) return Err(shot.Error ?? "Region capture failed");

            this.Status.NoteLiveActivity();

            StringBuilder caption = new();
            caption.AppendLine(this.Status.GetCached().ToHeader());
            caption.AppendLine();
            caption.AppendLine($"Region of a {shot.ScreenWidth}x{shot.ScreenHeight} screen.");

            if (shot.Clamped)
            {
                caption.AppendLine($"Requested (x={x}, y={y}, {width}x{height}) -> clamped to "
                    + $"(x={shot.RectX}, y={shot.RectY}, {shot.RectWidth}x{shot.RectHeight}): it ran past the edge of the screen.");
            }

            caption.AppendLine($"Returned at {shot.OutWidth}x{shot.OutHeight} ({shot.Scale:0.00}x zoom, {shot.Jpeg.Length / 1024}KB). "
                + "Origin is top-left, same space as ScrW()/ScrH().");

            if (shot.ReusedFrame)
                caption.AppendLine($"Re-cropped the frame captured {shot.FrameAgeSeconds:0}s ago, so this is NOT the live screen.");

            caption.Append("If this framed the wrong thing, adjust x/y/width/height and call again with reuseLastFrame=true to re-crop this exact frame.");

            return new
            {
                content = new object[]
                {
                    new { type = "text", text = caption.ToString() },
                    new { type = "image", data = Convert.ToBase64String(shot.Jpeg), mimeType = "image/jpeg" },
                }
            };
        }

        private async Task<object> HandleReadGmodWiki(JObject? arguments)
        {
            string? page = arguments?["page"]?.ToString();
            if (string.IsNullOrWhiteSpace(page)) return Err("Missing required parameter: page");

            int maxChars = arguments?["maxChars"]?.Value<int>() ?? 6000;
            if (maxChars < 500) maxChars = 500;
            if (maxChars > 40000) maxChars = 40000;

            LocalLogger.WriteLine($"Fetching GMod wiki page: {page}");

            GmodWiki.WikiResult result = await GmodWiki.FetchAsync(page, maxChars);
            if (!result.Success) return Err(result.Error ?? "Wiki fetch failed");

            // The wiki does not depend on game state, so this returns content without a status header.
            // No echo either: the URL on the first line already says exactly which page was fetched.
            return new
            {
                content = new[] { new { type = "text", text = $"{result.Url}\n\n{result.Text}" } }
            };
        }

        /// <summary>
        /// No completion sentinel came back, so the script never ran. The captured console output
        /// usually names the reason (script enforcer, unknown command, ...), so hand it over.
        /// </summary>
        private static string DidNotExecute(LuaRealm realm, LuaScriptResult result)
        {
            string realmName = realm.ToString().ToLowerInvariant();

            StringBuilder sb = new();
            sb.AppendLine($"The Lua never executed in the {realmName} realm — GTerm sent the command but the game never ran it.");
            sb.AppendLine();
            sb.AppendLine(realm == LuaRealm.Client
                ? "Most likely sv_allowcslua is 0 (the default), which blocks lua_openscript_cl. Set 'sv_allowcslua 1' if you own the server, or use realm=\"server\"."
                : "Most likely there is no server Lua state: GMod is at the main menu, still loading, or you are joined to a remote server. Load or host a game, or use realm=\"client\".");
            sb.AppendLine();
            sb.AppendLine("Call get_game_status to see which realms are reachable.");
            sb.AppendLine();
            sb.Append(RenderOutput(result));

            return sb.ToString();
        }

        private static string RenderOutput(LuaScriptResult result)
        {
            StringBuilder sb = new();
            AppendOutput(sb, result.Output, "(no output captured)");
            return sb.ToString();
        }

        private static async Task SendPlain(HttpListenerResponse response, int statusCode, string message)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(message);
            response.StatusCode = statusCode;
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer);
            response.Close();
        }

        private static async Task SendResponse(HttpListenerResponse response, object result, JToken? id)
        {
            var jsonResponse = new
            {
                jsonrpc = "2.0",
                id = id,
                result = result
            };

            string json = JsonConvert.SerializeObject(jsonResponse);
            byte[] buffer = Encoding.UTF8.GetBytes(json);

            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            response.StatusCode = 200;

            LocalLogger.WriteLine($"MCP response: {json}");

            await response.OutputStream.WriteAsync(buffer);
            response.Close();
        }

        private static async Task SendError(HttpListenerResponse response, int code, string message, JToken? id)
        {
            var jsonResponse = new
            {
                jsonrpc = "2.0",
                id = id,
                error = new
                {
                    code = code,
                    message = message
                }
            };

            string json = JsonConvert.SerializeObject(jsonResponse);
            byte[] buffer = Encoding.UTF8.GetBytes(json);

            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            response.StatusCode = 200; // JSON-RPC errors still return 200

            LocalLogger.WriteLine($"MCP error response: {json}");

            await response.OutputStream.WriteAsync(buffer);
            response.Close();
        }
    }
}
