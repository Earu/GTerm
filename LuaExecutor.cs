using System.Text;

namespace GTerm
{
    internal class LuaExecutor
    {
        private readonly CommandCollector Collector;
        private static readonly string GTermLuaDir = "lua/gterm";

        internal LuaExecutor(CommandCollector collector)
        {
            this.Collector = collector;
        }

        public async Task<ExecuteLuaResult> ExecuteLuaAsync(string luaCode, int? customCollectionWindowMs = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(luaCode))
            {
                return new ExecuteLuaResult
                {
                    Success = false,
                    Error = "Lua code cannot be empty"
                };
            }

            if (!GmodInterop.TryGetGmodPath(out string gmodPath, false))
            {
                return new ExecuteLuaResult
                {
                    Success = false,
                    Error = "Could not find Garry's Mod installation path"
                };
            }

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

                LocalLogger.WriteLine($"Writing Lua code to: {filePath}");
                await File.WriteAllTextAsync(filePath, luaCode, cancellationToken);

                string command = $"lua_openscript_cl gterm/{fileName}";
                LocalLogger.WriteLine($"Executing command: {command}");

                CommandResult result = await this.Collector.ExecuteCommandAsync(command, customCollectionWindowMs, cancellationToken);

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

                if (!result.Success)
                {
                    return new ExecuteLuaResult
                    {
                        Success = false,
                        Error = result.Error ?? "Command execution failed"
                    };
                }

                return new ExecuteLuaResult
                {
                    Success = true,
                    FileName = fileName,
                    Output = result.Output,
                    CollectionDurationMs = result.CollectionDurationMs
                };
            }
            catch (Exception ex)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                }
                catch { }

                return new ExecuteLuaResult
                {
                    Success = false,
                    Error = $"Exception: {ex.Message}"
                };
            }
        }
    }

    internal class ExecuteLuaResult
    {
        public bool Success { get; set; }
        public string? FileName { get; set; }
        public List<OutputLine> Output { get; set; } = [];
        public double CollectionDurationMs { get; set; }
        public string? Error { get; set; }
    }
}


