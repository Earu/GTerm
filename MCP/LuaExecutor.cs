using Newtonsoft.Json.Linq;
using System.Text;

namespace GTerm.MCP
{
    internal enum LuaRealm
    {
        Client,
        Server,
    }

    internal enum GameFileOutcome
    {
        /// <summary>The command did not round-trip (disconnected, error, etc.).</summary>
        Failed,

        /// <summary>No result came back — the reader script never ran (e.g. sv_allowcslua on client).</summary>
        NotExecuted,

        /// <summary>The game ran the reader but file.Read returned nil — not readable in this realm.</summary>
        NotReadable,

        /// <summary>Contents retrieved.</summary>
        Ok,
    }

    internal sealed class GameFileResult
    {
        public GameFileOutcome Outcome { get; init; }
        public string? Content { get; init; }
        public long Size { get; init; }
        public bool Truncated { get; init; }
        public bool LooksBinary { get; init; }
        public string? Error { get; init; }

        internal static GameFileResult Fail(string error) => new() { Outcome = GameFileOutcome.Failed, Error = error };
    }

    internal enum RelayOutcome
    {
        /// <summary>The command did not round-trip (disconnected, error, etc.).</summary>
        Failed,

        /// <summary>No sentinel came back: the script never ran in that realm.</summary>
        NotExecuted,

        /// <summary>The script ran and reported a problem of its own (see Error).</summary>
        Refused,

        /// <summary>Content retrieved.</summary>
        Ok,
    }

    internal sealed class RelayResult
    {
        public RelayOutcome Outcome { get; init; }
        public string? Content { get; init; }
        public bool Truncated { get; init; }
        public string? Error { get; init; }

        internal static RelayResult Fail(string error) => new() { Outcome = RelayOutcome.Failed, Error = error };
    }

    internal enum ScreenCaptureOutcome
    {
        /// <summary>The command did not round-trip (disconnected, error, etc.).</summary>
        Failed,

        /// <summary>No sentinel came back: the capture script never ran, or no frame was ever rendered.</summary>
        NotExecuted,

        /// <summary>The hook ran but render.Capture refused (see <see cref="ScreenCaptureResult.Error"/>).</summary>
        NotCaptured,

        /// <summary>Pixels retrieved.</summary>
        Ok,
    }

    internal sealed class ScreenCaptureResult
    {
        public ScreenCaptureOutcome Outcome { get; init; }
        public byte[]? Jpeg { get; init; }

        /// <summary>The game's real ScrW()/ScrH(), always reported so a caller can re-aim.</summary>
        public int ScreenWidth { get; init; }
        public int ScreenHeight { get; init; }

        /// <summary>The rectangle actually captured, after the game clamped it to the frame.</summary>
        public int RectX { get; init; }
        public int RectY { get; init; }
        public int RectWidth { get; init; }
        public int RectHeight { get; init; }

        /// <summary>The requested rectangle did not fit and had to be moved or shrunk.</summary>
        public bool Clamped { get; init; }

        public string? Error { get; init; }

        internal static ScreenCaptureResult Fail(string error) => new() { Outcome = ScreenCaptureOutcome.Failed, Error = error };
    }

    internal class LuaExecutor
    {
        private readonly CommandCollector Collector;

        /// <summary>GTerm-owned scratch directory. Anything left here is fair game for the sweeper.</summary>
        private static readonly string GTermLuaDir = "lua/gterm";

        private static readonly TimeSpan OrphanAge = TimeSpan.FromMinutes(2);

        internal LuaExecutor(CommandCollector collector)
        {
            this.Collector = collector;
        }

        /// <summary>Runs arbitrary user Lua, reporting syntax and runtime errors separately.</summary>
        public Task<LuaScriptResult> ExecuteLuaAsync(string luaCode, LuaRealm realm, int? collectionWindowMs = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(luaCode))
                return Task.FromResult(Failed("Lua code cannot be empty"));

            // Stop early on an error, but on success keep collecting for the whole window so late
            // asynchronous prints (timers, hooks, callbacks) are captured — that is what `timeout` is for.
            return RunScriptAsync(BuildRunner(luaCode), realm, [GTermSentinels.LuaOk, GTermSentinels.LuaErr], collectionWindowMs, cancellationToken,
                earlyExitMarkers: [GTermSentinels.LuaErr]);
        }

        /// <summary>Compile-checks Lua without executing it.</summary>
        public Task<LuaScriptResult> ValidateSyntaxAsync(string luaCode, LuaRealm realm, int? collectionWindowMs = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(luaCode))
                return Task.FromResult(Failed("Lua code cannot be empty"));

            return RunScriptAsync(BuildValidator(luaCode), realm, [GTermSentinels.SyntaxOk, GTermSentinels.SyntaxErr], collectionWindowMs, cancellationToken);
        }

        /// <summary>Asks the running game whether a path exists in its virtual filesystem.</summary>
        public Task<LuaScriptResult> CheckGameFileAsync(string path, LuaRealm realm, int? collectionWindowMs = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path))
                return Task.FromResult(Failed("Path cannot be empty"));

            return RunScriptAsync(BuildFileCheck(path), realm, [GTermSentinels.File], collectionWindowMs, cancellationToken);
        }

        /// <summary>
        /// Reads a file's contents from the running game's virtual filesystem. The game reads the file
        /// and writes it to data/ (a "relay"); GTerm then reads that from disk. This sidesteps the
        /// 4096-char cap on print() — the console only carries a tiny status sentinel, never the content.
        /// </summary>
        public async Task<GameFileResult> ReadGameFileAsync(string path, string searchPath, LuaRealm realm, int maxBytes, int? collectionWindowMs = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path))
                return GameFileResult.Fail("Path cannot be empty");

            if (!GmodInterop.TryGetGmodPath(out string gmodPath, false))
                return GameFileResult.Fail("Could not find Garry's Mod installation path");

            // file.Write forces the name lowercase; the "N" GUID format is already lowercase, so the
            // path GTerm reads back matches exactly.
            string nonce = Guid.NewGuid().ToString("N");
            string relayRel = $"gterm/read_{nonce}.txt";  // relative to data/
            string relayDir = Path.Combine(gmodPath, "garrysmod", "data", "gterm");
            string relayDisk = Path.Combine(relayDir, $"read_{nonce}.txt");

            // Clear any relay left behind by a crash mid-read (the finally below handles the normal case).
            SweepOrphans(relayDir, "read_*.txt");

            try
            {
                LuaScriptResult run = await RunScriptAsync(
                    BuildReadGameFile(path, searchPath, relayRel), realm, [GTermSentinels.GameFile], collectionWindowMs, cancellationToken);

                if (!run.Success)
                    return GameFileResult.Fail(run.Error ?? "Read command failed");

                if (!run.TryGetSentinel(GTermSentinels.GameFile, out Sentinel sentinel))
                    return new GameFileResult { Outcome = GameFileOutcome.NotExecuted };

                JObject info = JObject.Parse(sentinel.Payload);
                bool read = info["read"]?.Value<bool>() ?? false;
                if (!read)
                    return new GameFileResult { Outcome = GameFileOutcome.NotReadable };

                bool wrote = info["wrote"]?.Value<bool>() ?? false;
                long size = info["size"]?.Value<long>() ?? 0;

                if (!wrote || !File.Exists(relayDisk))
                    return GameFileResult.Fail("The game read the file but could not relay it (file.Write failed or the relay path did not resolve on disk).");

                // Read only up to maxBytes+1 so we can tell whether it was truncated without loading a huge file.
                await using FileStream fs = new(relayDisk, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                int cap = maxBytes + 1;
                byte[] buffer = new byte[cap];
                int got = await fs.ReadAsync(buffer.AsMemory(0, cap), cancellationToken);

                bool truncated = got > maxBytes;
                int keep = truncated ? maxBytes : got;
                bool binary = LooksBinary(buffer, keep);
                string content = System.Text.Encoding.UTF8.GetString(buffer, 0, keep);

                return new GameFileResult
                {
                    Outcome = GameFileOutcome.Ok,
                    Content = content,
                    Size = size,
                    Truncated = truncated,
                    LooksBinary = binary,
                };
            }
            catch (Exception ex)
            {
                return GameFileResult.Fail($"Exception: {ex.Message}");
            }
            finally
            {
                TryDelete(relayDisk);
            }
        }

        /// <summary>
        /// Runs caller-built Lua that hands a payload back through a data/ relay file. The Lua gets
        /// the relay path (relative to data/) and must end by emitting a RELAY sentinel whose JSON
        /// is {ok=bool, err=string?}; on ok=true it must have file.Write'n the payload there.
        /// Same trick as ReadGameFileAsync, for payloads print() cannot carry.
        /// </summary>
        public async Task<RelayResult> RunWithRelayAsync(Func<string, string> buildLua, LuaRealm realm, int maxBytes, int? collectionWindowMs = null, CancellationToken cancellationToken = default)
        {
            if (!GmodInterop.TryGetGmodPath(out string gmodPath, false))
                return RelayResult.Fail("Could not find Garry's Mod installation path");

            string nonce = Guid.NewGuid().ToString("N");
            string relayRel = $"gterm/relay_{nonce}.txt";
            string relayDir = Path.Combine(gmodPath, "garrysmod", "data", "gterm");
            string relayDisk = Path.Combine(relayDir, $"relay_{nonce}.txt");

            SweepOrphans(relayDir, "relay_*.txt");

            try
            {
                LuaScriptResult run = await RunScriptAsync(buildLua(relayRel), realm, [GTermSentinels.Relay], collectionWindowMs, cancellationToken);

                if (!run.Success)
                    return RelayResult.Fail(run.Error ?? "Relay command failed");

                if (!run.TryGetSentinel(GTermSentinels.Relay, out Sentinel sentinel))
                    return new RelayResult { Outcome = RelayOutcome.NotExecuted };

                JObject info = JObject.Parse(sentinel.Payload);
                if (!(info["ok"]?.Value<bool>() ?? false))
                    return new RelayResult { Outcome = RelayOutcome.Refused, Error = info["err"]?.ToString() ?? "the script reported a failure" };

                if (!File.Exists(relayDisk))
                    return RelayResult.Fail("The game ran the script but the relay file never appeared on disk (file.Write failed or the path did not resolve).");

                await using FileStream fs = new(relayDisk, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                int cap = maxBytes + 1;
                byte[] buffer = new byte[cap];
                int got = await fs.ReadAsync(buffer.AsMemory(0, cap), cancellationToken);

                bool truncated = got > maxBytes;
                int keep = truncated ? maxBytes : got;

                return new RelayResult
                {
                    Outcome = RelayOutcome.Ok,
                    Content = System.Text.Encoding.UTF8.GetString(buffer, 0, keep),
                    Truncated = truncated,
                };
            }
            catch (Exception ex)
            {
                return RelayResult.Fail($"Exception: {ex.Message}");
            }
            finally
            {
                TryDelete(relayDisk);
            }
        }

        /// <summary>
        /// Grabs a rectangle of the client's screen with render.Capture and relays it through data/,
        /// the same trick ReadGameFileAsync uses to dodge print()'s size cap. Deliberately not the
        /// engine's `jpeg` command: that one makes Steam save a copy into the user's screenshot
        /// library on every single call, and it cannot capture a sub-rectangle.
        /// </summary>
        public async Task<ScreenCaptureResult> CaptureScreenAsync(int x, int y, int width, int height, int quality, int? collectionWindowMs = null, CancellationToken cancellationToken = default)
        {
            if (!GmodInterop.TryGetGmodPath(out string gmodPath, false))
                return ScreenCaptureResult.Fail("Could not find Garry's Mod installation path");

            string nonce = Guid.NewGuid().ToString("N");
            string relayRel = $"gterm/shot_{nonce}.jpg";  // relative to data/
            string relayDir = Path.Combine(gmodPath, "garrysmod", "data", "gterm");
            string relayDisk = Path.Combine(relayDir, $"shot_{nonce}.jpg");

            SweepOrphans(relayDir, "shot_*.jpg");

            try
            {
                LuaScriptResult run = await RunScriptAsync(
                    BuildCaptureScreen(x, y, width, height, quality, relayRel), LuaRealm.Client,
                    [GTermSentinels.Shot], collectionWindowMs, cancellationToken);

                if (!run.Success)
                    return ScreenCaptureResult.Fail(run.Error ?? "Capture command failed");

                // The capture lands inside a PostRender hook, so a game that is not drawing frames
                // (minimised, paused at a loading screen) never emits the sentinel at all.
                if (!run.TryGetSentinel(GTermSentinels.Shot, out Sentinel sentinel))
                    return new ScreenCaptureResult { Outcome = ScreenCaptureOutcome.NotExecuted };

                JObject info = JObject.Parse(sentinel.Payload);
                int screenW = info["sw"]?.Value<int>() ?? 0;
                int screenH = info["sh"]?.Value<int>() ?? 0;
                int rectX = info["x"]?.Value<int>() ?? 0;
                int rectY = info["y"]?.Value<int>() ?? 0;
                int rectW = info["w"]?.Value<int>() ?? 0;
                int rectH = info["h"]?.Value<int>() ?? 0;
                bool clamped = info["clamped"]?.Value<bool>() ?? false;
                bool captured = info["ok"]?.Value<bool>() ?? false;

                if (!captured)
                {
                    return new ScreenCaptureResult
                    {
                        Outcome = ScreenCaptureOutcome.NotCaptured,
                        ScreenWidth = screenW,
                        ScreenHeight = screenH,
                        Error = info["err"]?.ToString() ?? "render.Capture returned no data",
                    };
                }

                if (!File.Exists(relayDisk))
                {
                    return new ScreenCaptureResult
                    {
                        Outcome = ScreenCaptureOutcome.NotCaptured,
                        ScreenWidth = screenW,
                        ScreenHeight = screenH,
                        Error = "The game captured the frame but could not relay it (file.Write failed or the relay path did not resolve on disk).",
                    };
                }

                return new ScreenCaptureResult
                {
                    Outcome = ScreenCaptureOutcome.Ok,
                    Jpeg = await File.ReadAllBytesAsync(relayDisk, cancellationToken),
                    ScreenWidth = screenW,
                    ScreenHeight = screenH,
                    RectX = rectX,
                    RectY = rectY,
                    RectWidth = rectW,
                    RectHeight = rectH,
                    Clamped = clamped,
                };
            }
            catch (Exception ex)
            {
                return ScreenCaptureResult.Fail($"Exception: {ex.Message}");
            }
            finally
            {
                TryDelete(relayDisk);
            }
        }

        private static bool LooksBinary(byte[] data, int length)
        {
            for (int i = 0; i < length; i++)
                if (data[i] == 0) return true;

            return false;
        }

        private async Task<LuaScriptResult> RunScriptAsync(
            string luaSource,
            LuaRealm realm,
            IReadOnlyCollection<string> completionMarkers,
            int? collectionWindowMs,
            CancellationToken cancellationToken,
            IReadOnlyCollection<string>? earlyExitMarkers = null)
        {
            if (!GmodInterop.TryGetGmodPath(out string gmodPath, false))
                return Failed("Could not find Garry's Mod installation path");

            string gtermDir = Path.Combine(gmodPath, "garrysmod", GTermLuaDir);
            string fileName = $"{Guid.NewGuid()}.lua";
            string filePath = Path.Combine(gtermDir, fileName);

            try
            {
                if (!Directory.Exists(gtermDir))
                {
                    LocalLogger.WriteLine($"Creating directory: {gtermDir}");
                    Directory.CreateDirectory(gtermDir);
                }

                SweepOrphans(gtermDir, "*.lua");

                LocalLogger.WriteLine($"Writing Lua script to: {filePath}");
                await File.WriteAllTextAsync(filePath, luaSource, cancellationToken);

                // lua_openscript runs in the server realm, lua_openscript_cl in the client realm.
                // Both resolve relative to garrysmod/lua/.
                string command = realm == LuaRealm.Server
                    ? $"lua_openscript {GTermLuaDir[4..]}/{fileName}"
                    : $"lua_openscript_cl {GTermLuaDir[4..]}/{fileName}";

                LocalLogger.WriteLine($"Executing command: {command}");

                // completionMarkers proves the script ran; earlyExitMarkers decides when to stop
                // collecting. They differ for execute_lua_code: it stops early on an error but, on
                // success, keeps listening for the full window so asynchronous prints are captured.
                CommandResult result = await this.Collector.ExecuteCommandAsync(
                    command, collectionWindowMs, earlyExitMarkers ?? completionMarkers, cancellationToken: cancellationToken);

                bool executed = result.Success && result.Sentinels.Any(s => completionMarkers.Contains(s.Marker));

                // Only delete once a sentinel proves the script actually ran. The engine queues our
                // command into its global command buffer, so deleting eagerly can race a script that
                // has not executed yet. Orphans are swept on the next run instead.
                if (executed) TryDelete(filePath);

                if (!result.Success)
                    return Failed(result.Error ?? "Command execution failed");

                return new LuaScriptResult
                {
                    Success = true,
                    Executed = executed,
                    FileName = fileName,
                    Realm = realm,
                    Output = result.Output,
                    Sentinels = result.Sentinels,
                    CollectionDurationMs = result.CollectionDurationMs,
                };
            }
            catch (Exception ex)
            {
                TryDelete(filePath);
                return Failed($"Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Wraps user code in a long string and compiles it, so a syntax error is reported rather
        /// than thrown, and a runtime error is caught separately. Deliberately avoids include():
        /// include() only loads local client files when sv_allowcslua is 1.
        /// </summary>
        private static string BuildRunner(string luaCode)
        {
            string eq = GTermSentinels.LongBracketLevel(luaCode);

            StringBuilder sb = new();
            sb.Append("local __src = [").Append(eq).AppendLine("[");
            sb.AppendLine(luaCode);
            sb.Append(']').Append(eq).AppendLine("]");
            sb.AppendLine("local __f = CompileString(__src, \"gterm_body\", false)");
            sb.AppendLine($"if isstring(__f) then {GTermSentinels.LuaEmit(GTermSentinels.LuaErr, "\"syntax: \" .. __f")} return end");
            sb.AppendLine("local __ok, __err = pcall(__f)");
            sb.AppendLine($"if __ok then {GTermSentinels.LuaEmit(GTermSentinels.LuaOk, "\"ok\"")}");
            sb.AppendLine($"else {GTermSentinels.LuaEmit(GTermSentinels.LuaErr, "tostring(__err)")} end");

            return sb.ToString();
        }

        private static string BuildValidator(string luaCode)
        {
            string eq = GTermSentinels.LongBracketLevel(luaCode);

            StringBuilder sb = new();
            sb.Append("local __src = [").Append(eq).AppendLine("[");
            sb.AppendLine(luaCode);
            sb.Append(']').Append(eq).AppendLine("]");
            sb.AppendLine("local __f = CompileString(__src, \"gterm_validate\", false)");
            sb.AppendLine($"if isstring(__f) then {GTermSentinels.LuaEmit(GTermSentinels.SyntaxErr, "__f")}");
            sb.AppendLine($"else {GTermSentinels.LuaEmit(GTermSentinels.SyntaxOk, "\"ok\"")} end");

            return sb.ToString();
        }

        private static string BuildFileCheck(string path)
        {
            // "GAME" searches all mounted content (addons, GMAs, mounted games); "LUA" resolves to
            // the Lua search path of whichever realm this runs in. They can disagree, so report both.
            const string payload =
                "util.TableToJSON({" +
                "path=__p," +
                "game_exists=file.Exists(__p, \"GAME\")," +
                "game_size=file.Size(__p, \"GAME\")," +
                "game_isdir=file.IsDir(__p, \"GAME\")," +
                "lua_exists=file.Exists(__p, \"LUA\")," +
                "lua_size=file.Size(__p, \"LUA\")," +
                "lua_isdir=file.IsDir(__p, \"LUA\")" +
                "})";

            StringBuilder sb = new();
            sb.Append("local __p = ").AppendLine(GTermSentinels.LuaLiteral(path));
            sb.AppendLine(GTermSentinels.LuaEmit(GTermSentinels.File, payload));

            return sb.ToString();
        }

        private static string BuildReadGameFile(string path, string searchPath, string relayRel)
        {
            StringBuilder sb = new();
            sb.Append("local __p = ").AppendLine(GTermSentinels.LuaLiteral(path));
            sb.Append("local __sp = ").AppendLine(GTermSentinels.LuaLiteral(searchPath));
            sb.AppendLine("local __d = file.Read(__p, __sp)");
            sb.AppendLine("if __d == nil then");
            sb.AppendLine($"  {GTermSentinels.LuaEmit(GTermSentinels.GameFile, "util.TableToJSON({read=false})")}");
            sb.AppendLine("else");
            sb.AppendLine("  file.CreateDir(\"gterm\")");
            sb.Append("  local __w = file.Write(").Append(GTermSentinels.LuaLiteral(relayRel)).AppendLine(", __d)");
            sb.AppendLine($"  {GTermSentinels.LuaEmit(GTermSentinels.GameFile, "util.TableToJSON({read=true, wrote=__w == true, size=#__d})")}");
            sb.AppendLine("end");

            return sb.ToString();
        }

        /// <summary>
        /// render.Capture only works inside a rendering pass, so the grab is deferred to a one-shot
        /// PostRender hook, late enough that HUD, vgui and viewmodels are all in the frame.
        /// The rectangle is clamped HERE rather than in C# because an out-of-bounds rect makes
        /// render.Capture hand back nil through a *successful* pcall, which would look like a
        /// mystery failure. Clamping in the game is also what lets us report the real ScrW()/ScrH().
        /// A width or height of 0 means "the whole screen".
        /// </summary>
        private static string BuildCaptureScreen(int x, int y, int width, int height, int quality, string relayRel)
        {
            const string payload =
                "util.TableToJSON({" +
                "sw=__sw, sh=__sh," +
                "x=__x, y=__y, w=__w, h=__h," +
                "clamped=__clamped," +
                "ok=__data ~= nil," +
                "err=__err" +
                "})";

            StringBuilder sb = new();
            sb.AppendLine($"local __hook = \"gterm_cap_{Guid.NewGuid():N}\"");
            sb.AppendLine($"local __rx, __ry, __rw, __rh, __q = {x}, {y}, {width}, {height}, {quality}");
            sb.AppendLine("hook.Add(\"PostRender\", __hook, function()");
            sb.AppendLine("  hook.Remove(\"PostRender\", __hook)");
            sb.AppendLine("  local __sw, __sh = ScrW(), ScrH()");
            sb.AppendLine("  local __w0 = __rw > 0 and __rw or __sw");
            sb.AppendLine("  local __h0 = __rh > 0 and __rh or __sh");
            sb.AppendLine("  local __x = math.Clamp(math.floor(__rx), 0, math.max(__sw - 1, 0))");
            sb.AppendLine("  local __y = math.Clamp(math.floor(__ry), 0, math.max(__sh - 1, 0))");
            sb.AppendLine("  local __w = math.Clamp(math.floor(__w0), 1, __sw - __x)");
            sb.AppendLine("  local __h = math.Clamp(math.floor(__h0), 1, __sh - __y)");
            sb.AppendLine("  local __clamped = __x ~= __rx or __y ~= __ry or __w ~= __w0 or __h ~= __h0");
            sb.AppendLine("  local __ok, __data = pcall(render.Capture, {");
            sb.AppendLine("    format = \"jpeg\", x = __x, y = __y, w = __w, h = __h, quality = __q, alpha = false");
            sb.AppendLine("  })");
            sb.AppendLine("  local __err = nil");
            sb.AppendLine("  if not __ok then __err = tostring(__data) __data = nil");
            sb.AppendLine("  elseif __data == nil then __err = \"render.Capture returned no data for that rectangle\" end");
            sb.AppendLine("  if __data ~= nil then");
            sb.AppendLine("    file.CreateDir(\"gterm\")");
            sb.Append("    file.Write(").Append(GTermSentinels.LuaLiteral(relayRel)).AppendLine(", __data)");
            sb.AppendLine("  end");
            sb.AppendLine($"  {GTermSentinels.LuaEmit(GTermSentinels.Shot, payload)}");
            sb.AppendLine("end)");

            return sb.ToString();
        }

        private static void SweepOrphans(string dir, string pattern)
        {
            try
            {
                if (!Directory.Exists(dir)) return;

                DateTime cutoff = DateTime.Now - OrphanAge;
                foreach (string orphan in Directory.EnumerateFiles(dir, pattern))
                {
                    if (File.GetLastWriteTime(orphan) < cutoff) TryDelete(orphan);
                }
            }
            catch (Exception ex)
            {
                LocalLogger.WriteLine($"Warning: failed to sweep {dir}: {ex.Message}");
            }
        }

        private static void TryDelete(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    LocalLogger.WriteLine($"Cleaning up: {filePath}");
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                LocalLogger.WriteLine($"Warning: Failed to delete temp file: {ex.Message}");
            }
        }

        private static LuaScriptResult Failed(string error) => new() { Success = false, Error = error };
    }

    internal class LuaScriptResult
    {
        /// <summary>The command was dispatched and the collector returned cleanly.</summary>
        public bool Success { get; set; }

        /// <summary>A completion sentinel came back, proving the script actually ran in the game.</summary>
        public bool Executed { get; set; }

        public string? FileName { get; set; }
        public LuaRealm Realm { get; set; }
        public List<OutputLine> Output { get; set; } = [];
        public List<Sentinel> Sentinels { get; set; } = [];
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
}
