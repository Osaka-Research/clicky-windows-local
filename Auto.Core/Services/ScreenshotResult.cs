using System.Drawing;

namespace Auto.Services;

public record ScreenshotResult(
    byte[] JpegBytes,      // raw JPEG data
    string Base64,         // base64-encoded JPEG for Claude API
    string Label,          // human-readable label (e.g., "Primary display — cursor here at (1234, 567)")
    Rectangle Bounds       // physical pixel bounds of the captured screen
);
