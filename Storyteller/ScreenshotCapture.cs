using System;
using System.IO;
using Colossal.Logging;
using UnityEngine;

namespace CityStoryMod.Storyteller
{
    // Unity-API screenshot capture targeted at the storyteller's map screenshot.
    // The /new-city flow looks for <cityDir>/maps/<slug>-overview.* before
    // prompting the player for a screenshot path — when this helper writes a
    // file there, the flow uses it silently.
    //
    // Limitation: Unity's ScreenCapture grabs the current framebuffer including
    // any UI overlay. The first version writes whatever is on screen; a future
    // pass should hide the storyteller window for one frame before capturing
    // so the map view comes through clean. For now the agent tolerates an
    // imperfect screenshot — the spatial data (snapshot.map + carto/processed)
    // is the primary geographic anchor; the screenshot only adds visual texture.
    //
    // Must be called on the main thread (Unity API constraint). ExportSystem's
    // OnUpdate already runs there.
    public static class ScreenshotCapture
    {
        // Asynchronously captures the current frame to a PNG at absolutePath.
        // Returns true if the request was successfully queued; the file
        // appears on disk one or two frames later (Unity writes
        // asynchronously). Creates the parent directory if needed.
        //
        // Path strategy: Unity's ScreenCapture.CaptureScreenshot(filename)
        // resolves relative paths against Application.persistentDataPath and
        // accepts absolute paths on Windows. We pass an absolute path
        // pointing into the city folder so no separate move step is needed.
        //
        // Why file-based instead of CaptureScreenshotAsTexture: under HDRP
        // (which CS2 uses), the texture-returning variant frequently returns
        // null because the custom render pipeline doesn't expose its
        // intermediate framebuffer through the path that variant reads.
        // The file-based variant uses a different code path internally and
        // hooks into HDRP's frame-end correctly. See:
        // https://docs.unity3d.com/2022.3/Documentation/ScriptReference/ScreenCapture.CaptureScreenshot.html
        public static bool TryCaptureToFile(string absolutePath, ILog log)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                log?.Warn("ScreenshotCapture: empty path; skipping.");
                return false;
            }

            try
            {
                string parentDir = Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);

                // Asynchronous: Unity returns immediately and writes the PNG
                // sometime in the next 1–2 frames. We can't catch a failure
                // synchronously, but file existence on disk a few ticks
                // later is the success signal.
                ScreenCapture.CaptureScreenshot(absolutePath);
                log?.Info($"ScreenshotCapture: queued via ScreenCapture.CaptureScreenshot → {absolutePath} ({Screen.width}×{Screen.height} expected; file appears in ~2 frames).");
                return true;
            }
            catch (Exception ex)
            {
                log?.Error(ex, $"ScreenshotCapture.TryCaptureToFile({absolutePath}) failed.");
                return false;
            }
        }

        // Returns the conventional path the /new-city flow expects to find a
        // map screenshot at: <cityDir>/maps/<slug>-overview.png. Pure path
        // composition; doesn't touch the filesystem.
        public static string GetOverviewPath(string cityDir, string citySlug)
        {
            if (string.IsNullOrWhiteSpace(cityDir) || string.IsNullOrWhiteSpace(citySlug)) return null;
            return Path.Combine(cityDir, "maps", $"{citySlug}-overview.png");
        }
    }
}
