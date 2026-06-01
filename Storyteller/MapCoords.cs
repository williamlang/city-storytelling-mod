namespace CityStoryMod.Storyteller
{
    // Translates the storyteller's "recentered meters" coordinate frame — the
    // one CartoProcessor produces and the agent quotes in prose (origin = map
    // centroid, +x east, +y north) — back into CS2 world space (game meters,
    // +x east, +z north, origin at map center) so the camera can fly there.
    //
    // Pure math, no Unity / Game.dll references, so CityStoryMod.Tests can
    // <Compile Link> it (same convention as TextUtils). The camera-side code
    // (CameraNavSystem) feeds the result into CameraController.pivot as
    // (worldX, 0, worldZ).
    //
    // The mapping was derived by decompiling Carto's UTM→WGS84 projection and
    // walking it through CartoProcessor's degrees→meters (×111320) +
    // recenter-by-tile-centroid pipeline. It comes out affine, axis-aligned,
    // with NO axis swap and NO sign flip — recentered x is game east, recentered
    // y is game north. The only correction is a per-axis scale very close to 1
    // (Carto's UTM scale vs. the crude equatorial 111320 constant):
    //
    //   recentered_x ≈ 0.9973 · game_x      →   game_x ≈ recentered_x / 0.9973
    //   recentered_y ≈ 1.0040 · game_z      →   game_z ≈ recentered_y / 1.0040
    //
    // These constants are EMPIRICAL (derived numerically near the equator-zoned
    // source coordinate Carto uses). They're accurate to a handful of meters at
    // the map edge — fine for camera framing — but if a camera click ever lands
    // visibly off-target in-game, this is the single place to recalibrate.
    public static class MapCoords
    {
        // game = recentered / scale. Defaults to the inverse of the measured
        // forward scale; set both to 1.0 to fall back to a pure identity.
        public const double XScale = 0.9973;
        public const double ZScale = 1.0040;

        public static void RecenteredToWorld(double x, double y, out double worldX, out double worldZ)
        {
            worldX = x / XScale;
            worldZ = y / ZScale;
        }
    }
}
