using System.Text.RegularExpressions;

namespace GTerm.MCP
{
    /// <summary>
    /// Captures a screenshot of the running client by issuing the engine's `jpeg` console command
    /// and reading back the file it writes. This is not Lua, so it works even when sv_allowcslua is 0
    /// — it only needs a client that is actually rendering a screen.
    /// </summary>
    internal sealed partial class ScreenshotCapturer
    {
        private const string ShotDir = "screenshots";

        private readonly CommandCollector Collector;

        internal ScreenshotCapturer(CommandCollector collector)
        {
            this.Collector = collector;
        }

        internal sealed class ShotResult
        {
            public bool Success { get; init; }
            public byte[]? Jpeg { get; init; }
            public int? Width { get; init; }
            public int? Height { get; init; }
            public string? Error { get; init; }
        }

        internal async Task<ShotResult> CaptureAsync(int quality, CancellationToken cancellationToken = default)
        {
            if (!GmodInterop.TryGetGmodPath(out string gmodPath, false))
                return new ShotResult { Success = false, Error = "Could not find the Garry's Mod installation path" };

            string shotsDir = Path.Combine(gmodPath, "garrysmod", ShotDir);
            string name = $"gterm_{Guid.NewGuid():N}";
            string filePath = Path.Combine(shotsDir, $"{name}.jpg");

            try
            {
                Directory.CreateDirectory(shotsDir);

                // `jpeg <name> <quality>` writes screenshots/<name>.jpg and logs a "Wrote '...'" line.
                CommandResult result = await this.Collector.ExecuteCommandAsync($"jpeg {name} {quality}", 1500, cancellationToken: cancellationToken);
                if (!result.Success)
                    return new ShotResult { Success = false, Error = result.Error ?? "Screenshot command failed" };

                (int? width, int? height) = ParseDimensions(result);

                // The capture lands on the next rendered frame, so the file may appear slightly after
                // the command returns. Poll briefly.
                for (int i = 0; i < 20; i++)
                {
                    if (File.Exists(filePath) && new FileInfo(filePath).Length > 0) break;
                    await Task.Delay(150, cancellationToken);
                }

                if (!File.Exists(filePath))
                {
                    return new ShotResult
                    {
                        Success = false,
                        Error = "The jpeg command ran but no screenshot file appeared. The game may be a dedicated "
                            + "server (no screen), minimized, or not rendering.",
                    };
                }

                byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
                return new ShotResult { Success = true, Jpeg = bytes, Width = width, Height = height };
            }
            catch (Exception ex)
            {
                return new ShotResult { Success = false, Error = $"Exception: {ex.Message}" };
            }
            finally
            {
                try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
            }
        }

        private static (int? Width, int? Height) ParseDimensions(CommandResult result)
        {
            foreach (OutputLine line in result.Output)
            {
                Match m = Dimensions().Match(line.Message);
                if (m.Success) return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            }

            return (null, null);
        }

        // matches "... (2560x1440) ..." in the engine's confirmation line
        [GeneratedRegex(@"\((\d+)x(\d+)\)")]
        private static partial Regex Dimensions();
    }
}
