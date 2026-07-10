using GTerm.Listeners;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Text;

namespace GTerm.MCP
{
    internal class MCPServer
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
        /// rules that agents get wrong: check status, name a realm, and never assume disk == game.
        /// </summary>
        private const string Instructions = """
GTerm bridges to a running Garry's Mod via its console command buffer. Nothing works unless GMod is CONNECTED and a Lua realm is actually reachable.

1. Every tool result starts with a [GTerm] status line. READ IT. If DISCONNECTED or NO_SESSION, stop and tell the user - do not retry blindly.
2. Call get_game_status first when unsure. It works even when disconnected.
3. Realms. GMod runs Lua in three realms: server (entities, gamemode logic), client (HUD, rendering, input), menu (main-menu UI). execute_lua_code REQUIRES realm="client" or "server"; "menu" is unreachable by any console command and errors. Which realms work depends on where GTerm is installed and what the game is doing: server realm is unreachable at the main menu and when joined to a remote server; client realm is unreachable on a dedicated server and is blocked whenever sv_allowcslua is 0. get_game_status reports exactly which realms are reachable - trust it over assumption.
4. Disk is not the game. read_gmod_file and list_gmod_directory read ON DISK. The running game uses a virtual filesystem including mounted addons and GMAs, and does not pick up edits until a file is reloaded. Before assuming an edited script is live: check_game_file to confirm the game sees the path, then reload_lua_file to load it into a realm. Editing a file changes nothing by itself.
5. Validate before executing. validate_lua_syntax compile-checks without running anything. Prefer it over running code to see whether it parses. read_gmod_wiki fetches the real signature of a GLua function before you use it. take_screenshot returns what is on screen (works even when sv_allowcslua blocks Lua).
6. Precondition failures return isError with a status snapshot and a one-line fix. Pass force=true only when certain, and say why.
7. Console output is asynchronous; unrelated lines interleave. A command that prints nothing still succeeded.
""";

        private readonly CommandCollector Collector;
        private readonly LuaExecutor LuaExecutor;
        private readonly ScreenshotCapturer Screenshot;
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
            this.Screenshot = new ScreenshotCapturer(collector);
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

        private static bool Forced(JObject? arguments) => arguments?["force"]?.Value<bool>() ?? false;

        /// <summary>Refuses when the game cannot service the call. Returns null when it is safe to proceed.</summary>
        private object? GateConnection(JObject? arguments)
        {
            if (this.Listener.IsConnected || Forced(arguments)) return null;

            return Err("Garry's Mod is not connected to GTerm, so no command can run. "
                + "Launch GMod with the GTerm module installed and load into a game, then retry. "
                + "Pass force=true to attempt the call anyway.");
        }

        /// <summary>Refuses when the requested realm is known not to work right now.</summary>
        private object? GateRealm(JObject? arguments, LuaRealm realm)
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
            string fix = realmState.Reach == RealmReach.Blocked
                ? "sv_allowcslua defaults to 0 and gates lua_run_cl / lua_openscript_cl. Set 'sv_allowcslua 1' if you own the server, or use realm=\"server\"."
                : $"That realm does not exist in this process right now ({realmState.Reason}). "
                    + $"Use realm=\"{(realm == LuaRealm.Server ? "client" : "server")}\" instead, or change what the game is doing.";

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
                                    description = "Seconds to collect output (default: 1, min: 0.5, max: 30). Execution returns as soon as the code finishes."
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
                        name = "reload_lua_file",
                        description = "Loads an existing Lua file INTO the running game so edits you made on disk take effect. Editing a file changes nothing by itself. lua_path is the path as Lua's include() sees it — relative to a lua/ root, e.g. 'autorun/foo.lua' — NOT a disk path. The realm argument is REQUIRED. Reloading a file re-runs it: side effects like hook registration may duplicate. PRECONDITION: GMod connected and the chosen realm reachable, else returns isError.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                lua_path = new
                                {
                                    type = "string",
                                    description = "Path relative to a lua/ root, as include() resolves it (e.g. 'autorun/foo.lua')."
                                },
                                realm = new
                                {
                                    type = "string",
                                    @enum = new[] { "client", "server" },
                                    description = "REQUIRED. Which realm loads the file."
                                },
                                force = new
                                {
                                    type = "boolean",
                                    description = "Skip precondition checks and attempt the call anyway."
                                }
                            },
                            required = new[] { "lua_path", "realm" }
                        },
                        annotations = new { readOnlyHint = false, destructiveHint = true, idempotentHint = false, openWorldHint = true }
                    },
                    new
                    {
                        name = "capture_console_output",
                        description = "Captures all Garry's Mod console output for a duration. Useful for monitoring ongoing events, server messages, or debugging. Starts collecting immediately and returns everything printed during the window. GTerm's own internal probe lines are filtered out. PRECONDITION: GMod must be connected, else returns isError.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                duration = new
                                {
                                    type = "number",
                                    description = "Seconds to capture console output (default: 5, min: 1, max: 60)"
                                },
                                force = new
                                {
                                    type = "boolean",
                                    description = "Skip the connection precondition check and attempt the call anyway."
                                }
                            },
                            required = new string[] { }
                        },
                        annotations = new { readOnlyHint = true, destructiveHint = false, idempotentHint = false, openWorldHint = true }
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
                        description = "Reads a text file from the Garry's Mod installation ON DISK. This is the disk copy: the running game may hold a different, older, or simply unloaded version, and edits you write here do not take effect until the file is reloaded. Use check_game_file to confirm the game can see a path, and reload_lua_file to make an edit live.",
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
                        name = "take_screenshot",
                        description = "Captures a screenshot of the running game's screen and returns it as an image. Useful for seeing what the player currently sees — HUD, menus, the world, an error on screen. Works whether in-game or at the main menu, and does NOT require sv_allowcslua (it is not Lua). Only fails if there is no client screen (a dedicated server) or the game is not rendering (minimized). PRECONDITION: GMod connected with a client screen, else returns isError.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
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
                            required = new string[] { }
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

            return toolName switch
            {
                "get_game_status" => await HandleGetGameStatus(arguments),
                "run_gmod_command" => await HandleRunGmodCommand(arguments),
                "execute_lua_code" => await HandleExecuteLuaCode(arguments),
                "validate_lua_syntax" => await HandleValidateLuaSyntax(arguments),
                "check_game_file" => await HandleCheckGameFile(arguments),
                "reload_lua_file" => await HandleReloadLuaFile(arguments),
                "capture_console_output" => await HandleCaptureConsoleOutput(arguments),
                "list_gmod_directory" => HandleListGmodDirectory(arguments),
                "read_gmod_file" => HandleReadGmodFile(arguments),
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

            return Ok(sb.ToString());
        }

        private async Task<object> HandleReloadLuaFile(JObject? arguments)
        {
            string? luaPath = arguments?["lua_path"]?.ToString();
            if (string.IsNullOrWhiteSpace(luaPath)) return Err("Missing required parameter: lua_path");

            if (!TryParseRealm(arguments?["realm"]?.ToString(), out LuaRealm realm, out string? realmError))
                return Err(realmError!);

            object? gate = GateRealm(arguments, realm);
            if (gate != null) return gate;

            LuaScriptResult result = await this.LuaExecutor.ReloadLuaFileAsync(luaPath, realm);
            if (!result.Success) return Err(result.Error ?? "Reload failed");

            if (result.TryGetSentinel(GTermSentinels.ReloadErr, out Sentinel error))
            {
                return Err($"Could not load '{luaPath}' into the {realm.ToString().ToLowerInvariant()} realm: {error.Payload}\n\n"
                    + "Check the path with check_game_file — include() resolves relative to a lua/ root, not a disk path.");
            }

            if (!result.Executed) return Err(DidNotExecute(realm, result));

            this.Status.NoteLiveActivity();

            StringBuilder sb = new();
            sb.AppendLine($"Loaded '{luaPath}' into the {realm.ToString().ToLowerInvariant()} realm.");
            sb.AppendLine("Note: the file was re-run, so any side effects (hooks, timers, entity registration) happened again.");
            sb.AppendLine();
            AppendOutput(sb, result.Output, "(the file printed nothing)");

            return Ok(sb.ToString());
        }

        private async Task<object> HandleCaptureConsoleOutput(JObject? arguments)
        {
            object? gate = GateConnection(arguments);
            if (gate != null) return gate;

            int durationMs = ClampWindowMs(arguments, "duration", 5.0, 1, 60);

            LocalLogger.WriteLine($"Capturing console output for {durationMs}ms");

            CommandResult result = await this.Collector.CaptureConsoleAsync(durationMs);
            if (!result.Success) return Err(result.Error ?? "Console capture failed");

            StringBuilder sb = new();
            sb.AppendLine("Console Capture Result");
            sb.AppendLine("======================");
            sb.AppendLine($"Duration: {durationMs / 1000.0:F1}s ({result.CollectionDurationMs:F0}ms actual)");
            sb.AppendLine($"Lines Captured: {result.Output.Count}");
            sb.AppendLine();
            AppendOutput(sb, result.Output, "(no output captured during this time)");

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

        private async Task<object> HandleTakeScreenshot(JObject? arguments)
        {
            object? gate = GateConnection(arguments);
            if (gate != null) return gate;

            // A dedicated server has no screen to capture. Everything else (menu or in-game) does,
            // and a screenshot needs no Lua, so we do not gate on realm reachability.
            if (!Forced(arguments) && this.Status.GetCached().IsDedicated == true)
                return Err("This is a dedicated server with no client screen, so there is nothing to screenshot. Pass force=true to try anyway.");

            int quality = arguments?["quality"]?.Value<int>() ?? 75;
            if (quality < 1) quality = 1;
            if (quality > 100) quality = 100;

            LocalLogger.WriteLine($"Taking screenshot (quality {quality})");

            ScreenshotCapturer.ShotResult shot = await this.Screenshot.CaptureAsync(quality);
            if (!shot.Success || shot.Jpeg == null) return Err(shot.Error ?? "Screenshot failed");

            this.Status.NoteLiveActivity();

            string dims = shot.Width.HasValue ? $"{shot.Width}x{shot.Height}" : "unknown size";
            string caption = $"{this.Status.GetCached().ToHeader()}\n\nScreenshot captured ({dims}, {shot.Jpeg.Length / 1024}KB, quality {quality}).";

            // Image content plus a text caption carrying the status header.
            return new
            {
                content = new object[]
                {
                    new { type = "image", data = Convert.ToBase64String(shot.Jpeg), mimeType = "image/jpeg" },
                    new { type = "text", text = caption },
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
