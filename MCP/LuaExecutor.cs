using System.Text;

namespace GTerm.MCP
{
    internal enum LuaRealm
    {
        Client,
        Server,
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

            return RunScriptAsync(BuildRunner(luaCode), realm, [GTermSentinels.LuaOk, GTermSentinels.LuaErr], collectionWindowMs, cancellationToken);
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

        /// <summary>Loads an existing Lua file into the running game so on-disk edits take effect.</summary>
        public Task<LuaScriptResult> ReloadLuaFileAsync(string luaPath, LuaRealm realm, int? collectionWindowMs = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(luaPath))
                return Task.FromResult(Failed("Lua path cannot be empty"));

            return RunScriptAsync(BuildReload(luaPath), realm, [GTermSentinels.ReloadOk, GTermSentinels.ReloadErr], collectionWindowMs, cancellationToken);
        }

        private async Task<LuaScriptResult> RunScriptAsync(
            string luaSource,
            LuaRealm realm,
            IReadOnlyCollection<string> completionMarkers,
            int? collectionWindowMs,
            CancellationToken cancellationToken)
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

                SweepOrphans(gtermDir);

                LocalLogger.WriteLine($"Writing Lua script to: {filePath}");
                await File.WriteAllTextAsync(filePath, luaSource, cancellationToken);

                // lua_openscript runs in the server realm, lua_openscript_cl in the client realm.
                // Both resolve relative to garrysmod/lua/.
                string command = realm == LuaRealm.Server
                    ? $"lua_openscript {GTermLuaDir[4..]}/{fileName}"
                    : $"lua_openscript_cl {GTermLuaDir[4..]}/{fileName}";

                LocalLogger.WriteLine($"Executing command: {command}");

                CommandResult result = await this.Collector.ExecuteCommandAsync(command, collectionWindowMs, completionMarkers, cancellationToken: cancellationToken);

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

        private static string BuildReload(string luaPath)
        {
            StringBuilder sb = new();
            sb.Append("local __p = ").AppendLine(GTermSentinels.LuaLiteral(luaPath));
            sb.AppendLine("local __ok, __err = pcall(include, __p)");
            sb.AppendLine($"if __ok then {GTermSentinels.LuaEmit(GTermSentinels.ReloadOk, "__p")}");
            sb.AppendLine($"else {GTermSentinels.LuaEmit(GTermSentinels.ReloadErr, "tostring(__err)")} end");

            return sb.ToString();
        }

        private static void SweepOrphans(string gtermDir)
        {
            try
            {
                DateTime cutoff = DateTime.Now - OrphanAge;
                foreach (string orphan in Directory.EnumerateFiles(gtermDir, "*.lua"))
                {
                    if (File.GetLastWriteTime(orphan) < cutoff) TryDelete(orphan);
                }
            }
            catch (Exception ex)
            {
                LocalLogger.WriteLine($"Warning: failed to sweep {gtermDir}: {ex.Message}");
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
