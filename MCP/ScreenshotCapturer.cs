using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace GTerm.MCP
{
    /// <summary>
    /// Captures the client's screen through Lua's render.Capture, relayed via data/.
    ///
    /// This deliberately does NOT use the engine's `jpeg` console command. That command works without
    /// sv_allowcslua, but every call also makes Steam save a full-resolution copy plus a thumbnail into
    /// the user's screenshot library, and it can only ever grab the whole screen. render.Capture leaves
    /// nothing behind and takes a rectangle natively; the price is that it needs a reachable client realm.
    /// </summary>
    internal sealed class ScreenshotCapturer
    {
        /// <summary>Source quality. Measured: a 900x520 region at q98 outweighed a whole 2560x1440 frame at q85.</summary>
        private const int RegionSourceQuality = 90;
        private const int RegionOutputQuality = 90;

        /// <summary>Clients resize images down to roughly this long edge and this pixel count before the model sees them.</summary>
        private const int TargetLongEdge = 1568;
        private const double TargetPixels = 1_100_000.0;

        /// <summary>Past this, enlargement is just undone by the client and magnifies JPEG blocks.</summary>
        private const double MaxZoom = 4.0;

        private static readonly TimeSpan FrameTtl = TimeSpan.FromSeconds(60);

        private readonly LuaExecutor Lua;

        private readonly Lock FrameLock = new();
        private byte[]? LastFrame;
        private int LastFrameWidth;
        private int LastFrameHeight;
        private DateTime LastFrameAt = DateTime.MinValue;

        internal ScreenshotCapturer(LuaExecutor lua)
        {
            this.Lua = lua;
        }

        internal sealed class ShotResult
        {
            public bool Success { get; init; }
            public byte[]? Jpeg { get; init; }
            public int? Width { get; init; }
            public int? Height { get; init; }
            public string? Error { get; init; }
        }

        internal sealed class RegionResult
        {
            public bool Success { get; init; }
            public byte[]? Jpeg { get; init; }

            public int ScreenWidth { get; init; }
            public int ScreenHeight { get; init; }

            /// <summary>The rectangle actually used, after clamping to the frame.</summary>
            public int RectX { get; init; }
            public int RectY { get; init; }
            public int RectWidth { get; init; }
            public int RectHeight { get; init; }

            public bool Clamped { get; init; }
            public double Scale { get; init; }
            public int OutWidth { get; init; }
            public int OutHeight { get; init; }

            public bool ReusedFrame { get; init; }
            public double FrameAgeSeconds { get; init; }

            public string? Error { get; init; }
        }

        /// <summary>Grabs the whole screen. Returned verbatim: no decode, no re-encode.</summary>
        internal async Task<ShotResult> CaptureAsync(int quality, CancellationToken cancellationToken = default)
        {
            ScreenCaptureResult shot = await this.Lua.CaptureScreenAsync(0, 0, 0, 0, quality, cancellationToken: cancellationToken);
            if (shot.Outcome != ScreenCaptureOutcome.Ok || shot.Jpeg == null)
                return new ShotResult { Success = false, Error = Describe(shot) };

            RememberFrame(shot.Jpeg, shot.ScreenWidth, shot.ScreenHeight);

            return new ShotResult
            {
                Success = true,
                Jpeg = shot.Jpeg,
                Width = shot.ScreenWidth,
                Height = shot.ScreenHeight,
            };
        }

        /// <summary>
        /// Grabs one rectangle and enlarges it toward the client's own ceiling, so a small element
        /// arrives legible instead of as a handful of pixels.
        /// </summary>
        internal async Task<RegionResult> CaptureRegionAsync(int x, int y, int width, int height, bool reuseLastFrame, CancellationToken cancellationToken = default)
        {
            if (width <= 0 || height <= 0)
                return new RegionResult { Success = false, Error = "width and height must both be at least 1 pixel." };

            if (reuseLastFrame && TryTakeFrame(out byte[] cached, out int cachedW, out int cachedH, out double age))
                return CropStoredFrame(cached, cachedW, cachedH, x, y, width, height, reused: true, age);

            // Asked to reuse but nothing is cached: grab a FULL frame so the next correction has
            // something stable to re-crop, then serve this call out of it.
            if (reuseLastFrame)
            {
                ScreenCaptureResult full = await this.Lua.CaptureScreenAsync(0, 0, 0, 0, RegionSourceQuality, cancellationToken: cancellationToken);
                if (full.Outcome != ScreenCaptureOutcome.Ok || full.Jpeg == null)
                    return new RegionResult { Success = false, ScreenWidth = full.ScreenWidth, ScreenHeight = full.ScreenHeight, Error = Describe(full) };

                RememberFrame(full.Jpeg, full.ScreenWidth, full.ScreenHeight);
                return CropStoredFrame(full.Jpeg, full.ScreenWidth, full.ScreenHeight, x, y, width, height, reused: false, 0);
            }

            // Normal path: let the game crop for us. Far cheaper than shipping a whole frame back.
            ScreenCaptureResult shot = await this.Lua.CaptureScreenAsync(x, y, width, height, RegionSourceQuality, cancellationToken: cancellationToken);
            if (shot.Outcome != ScreenCaptureOutcome.Ok || shot.Jpeg == null)
                return new RegionResult { Success = false, ScreenWidth = shot.ScreenWidth, ScreenHeight = shot.ScreenHeight, Error = Describe(shot) };

            try
            {
                (byte[] bytes, double scale, int outW, int outH) = Enlarge(shot.Jpeg, shot.RectWidth, shot.RectHeight);

                return new RegionResult
                {
                    Success = true,
                    Jpeg = bytes,
                    ScreenWidth = shot.ScreenWidth,
                    ScreenHeight = shot.ScreenHeight,
                    RectX = shot.RectX,
                    RectY = shot.RectY,
                    RectWidth = shot.RectWidth,
                    RectHeight = shot.RectHeight,
                    Clamped = shot.Clamped,
                    Scale = scale,
                    OutWidth = outW,
                    OutHeight = outH,
                };
            }
            catch (Exception ex)
            {
                return new RegionResult { Success = false, ScreenWidth = shot.ScreenWidth, ScreenHeight = shot.ScreenHeight, Error = $"Could not process the captured region: {ex.Message}" };
            }
        }

        /// <summary>Crops a rectangle out of a frame we already hold, so coordinate fixes hit a frozen image.</summary>
        private static RegionResult CropStoredFrame(byte[] frame, int screenW, int screenH, int x, int y, int width, int height, bool reused, double ageSeconds)
        {
            try
            {
                using Image<Rgb24> image = Image.Load<Rgb24>(frame);

                // Read these BEFORE the Mutate below: cropping and resizing change image.Width/Height
                // in place, so afterwards they describe the output, not the screen.
                int srcW = image.Width;
                int srcH = image.Height;

                // Rectangle.Intersect is the only correct clamp here; hand-rolled min/max gets the
                // fully-off-screen case wrong.
                Rectangle requested = new(x, y, width, height);
                Rectangle rect = Rectangle.Intersect(requested, new Rectangle(0, 0, srcW, srcH));

                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    return new RegionResult
                    {
                        Success = false,
                        ScreenWidth = srcW,
                        ScreenHeight = srcH,
                        Error = $"That rectangle is entirely off-screen. The screen is {srcW}x{srcH}, with (0,0) at the top-left.",
                    };
                }

                double scale = ComputeScale(rect.Width, rect.Height);
                int outW = Math.Max(1, (int)Math.Round(rect.Width * scale));
                int outH = Math.Max(1, (int)Math.Round(rect.Height * scale));

                image.Mutate(c =>
                {
                    c.Crop(rect);
                    if (outW != rect.Width || outH != rect.Height)
                        c.Resize(new ResizeOptions { Size = new Size(outW, outH), Sampler = KnownResamplers.Lanczos3, Mode = ResizeMode.Stretch });
                });

                using MemoryStream ms = new();
                image.SaveAsJpeg(ms, new JpegEncoder { Quality = RegionOutputQuality });

                return new RegionResult
                {
                    Success = true,
                    Jpeg = ms.ToArray(),
                    ScreenWidth = srcW,
                    ScreenHeight = srcH,
                    RectX = rect.X,
                    RectY = rect.Y,
                    RectWidth = rect.Width,
                    RectHeight = rect.Height,
                    Clamped = rect != requested,
                    Scale = scale,
                    OutWidth = outW,
                    OutHeight = outH,
                    ReusedFrame = reused,
                    FrameAgeSeconds = ageSeconds,
                };
            }
            catch (Exception ex)
            {
                return new RegionResult { Success = false, ScreenWidth = screenW, ScreenHeight = screenH, Error = $"Could not crop the cached frame: {ex.Message}" };
            }
        }

        /// <summary>Scales toward the client's ceiling. Returns the source untouched when it is already the right size.</summary>
        private static (byte[] Bytes, double Scale, int Width, int Height) Enlarge(byte[] jpeg, int width, int height)
        {
            double scale = ComputeScale(width, height);
            int outW = Math.Max(1, (int)Math.Round(width * scale));
            int outH = Math.Max(1, (int)Math.Round(height * scale));

            if (outW == width && outH == height) return (jpeg, 1.0, width, height);

            using Image<Rgb24> image = Image.Load<Rgb24>(jpeg);
            image.Mutate(c => c.Resize(new ResizeOptions
            {
                Size = new Size(outW, outH),
                Sampler = KnownResamplers.Lanczos3,
                Mode = ResizeMode.Stretch,
            }));

            using MemoryStream ms = new();
            image.SaveAsJpeg(ms, new JpegEncoder { Quality = RegionOutputQuality });

            return (ms.ToArray(), scale, outW, outH);
        }

        /// <summary>
        /// Enlarging adds no information: it buys vision patches over the pixels that are there.
        /// Both ceilings matter: the long edge and the total pixel count.
        /// </summary>
        private static double ComputeScale(int width, int height)
        {
            double byEdge = TargetLongEdge / (double)Math.Max(width, height);
            double byArea = Math.Sqrt(TargetPixels / (width * (double)height));

            return Math.Min(Math.Min(byEdge, byArea), MaxZoom);
        }

        private void RememberFrame(byte[] jpeg, int width, int height)
        {
            lock (this.FrameLock)
            {
                this.LastFrame = jpeg;
                this.LastFrameWidth = width;
                this.LastFrameHeight = height;
                this.LastFrameAt = DateTime.UtcNow;
            }
        }

        private bool TryTakeFrame(out byte[] frame, out int width, out int height, out double ageSeconds)
        {
            lock (this.FrameLock)
            {
                double age = (DateTime.UtcNow - this.LastFrameAt).TotalSeconds;
                if (this.LastFrame != null && age <= FrameTtl.TotalSeconds)
                {
                    frame = this.LastFrame;
                    width = this.LastFrameWidth;
                    height = this.LastFrameHeight;
                    ageSeconds = age;
                    return true;
                }

                // Expired: drop it rather than hold a multi-megabyte frame for the process lifetime.
                this.LastFrame = null;
            }

            frame = [];
            width = height = 0;
            ageSeconds = 0;
            return false;
        }

        private static string Describe(ScreenCaptureResult shot) => shot.Outcome switch
        {
            ScreenCaptureOutcome.NotExecuted =>
                "The capture never ran. render.Capture is deferred to the next rendered frame, so this happens when the game "
                + "is minimised, paused on a loading screen, or otherwise not drawing. Bring the game to the foreground and retry.",

            ScreenCaptureOutcome.NotCaptured => shot.Error ?? "render.Capture returned no data.",

            _ => shot.Error ?? "Screenshot failed",
        };
    }
}
