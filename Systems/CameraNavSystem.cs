using Colossal.Logging;
using Game;
using Game.Rendering;
using UnityEngine;

namespace CityStoryMod.Systems
{
    // Smoothly flies the gameplay camera to a world-space ground position on
    // request. Driven by mapGoto clicks in the Ghostwriter chat (PromptUISystem
    // → FlyTo). CS2 has no built-in animated "go to position": the gameplay
    // CameraController exposes a settable `pivot` (the ground point the camera
    // orbits) and `zoom`, but applies whatever you write instantly. So we ease
    // toward the target ourselves over a short duration, one step per OnUpdate.
    //
    // Registered at SystemUpdatePhase.UIUpdate (UpdateBefore in Mod.OnLoad) so
    // it keeps ticking while the sim is paused — the player may pause, read a
    // line, then click a coordinate. We integrate against UnityEngine.Time.
    // unscaledDeltaTime for the same reason (scaled delta is 0 under pause).
    public partial class CameraNavSystem : GameSystemBase
    {
        static readonly ILog _log = Mod.Log;

        // Seconds to ease from start to target. Short enough to feel responsive,
        // long enough to read as a deliberate move rather than a teleport.
        const float FlyDurationSec = 0.6f;
        // Default framing on arrival. CameraController.zoom is roughly distance-
        // from-pivot; the controller re-clamps it to its own zoomRange each
        // frame. ~400 reads as a neighborhood/street view. Zoom *inference*
        // (tight for an intersection, wide for a district) is a future
        // enhancement — for now every jump lands at the same framing.
        const float TargetZoom = 400f;
        // Snap to target and stop once pivot is within this many meters.
        const float ArriveEpsilonM = 2f;

        bool _flying;
        float _elapsed;
        Vector3 _startPivot;
        float _startZoom;
        Vector3 _targetPivot;

        // Note: GameSystemBase (unlike UISystemBase) exposes no overridable
        // gameMode. The system is harmless outside a loaded game — FlyTo and
        // OnUpdate both no-op when gamePlayController is null (only set once a
        // save is loaded).

        // Arm a fly-to. Coordinates are CS2 world meters (x east, z north); y is
        // ignored — the controller snaps pivot.y to terrain height each frame.
        // Called on the main thread from PromptUISystem's mapGoto trigger.
        public void FlyTo(double worldX, double worldZ)
        {
            CameraController ctrl = ResolveController();
            if (ctrl == null)
            {
                _log.Warn("CameraNavSystem.FlyTo: no gameplay camera controller (not in a loaded game?). Ignoring.");
                return;
            }
            _startPivot = ctrl.pivot;
            _startZoom = ctrl.zoom;
            _targetPivot = new Vector3((float)worldX, 0f, (float)worldZ);
            _elapsed = 0f;
            _flying = true;
            _log.Info($"CameraNavSystem.FlyTo target=({worldX:F0}, {worldZ:F0}).");
        }

        protected override void OnUpdate()
        {
            if (!_flying) return;

            CameraController ctrl = ResolveController();
            if (ctrl == null) { _flying = false; return; }

            // Make sure the gameplay controller is the active one. If the player
            // is mid orbit-follow (e.g. clicked a chirper), pivot writes would
            // otherwise land on an inactive controller and do nothing visible.
            CameraUpdateSystem cam = World.GetExistingSystemManaged<CameraUpdateSystem>();
            if (cam != null) cam.activeCameraController = ctrl;

            // Fully qualified: the inherited ComponentSystemBase.Time (ECS
            // TimeData) shadows the unqualified name, so reach for UnityEngine's
            // wall-clock delta explicitly. unscaledDeltaTime keeps advancing
            // while the sim is paused, which is exactly when a player may click.
            _elapsed += Mathf.Max(0f, UnityEngine.Time.unscaledDeltaTime);
            float t = FlyDurationSec > 0f ? Mathf.Clamp01(_elapsed / FlyDurationSec) : 1f;
            float e = Mathf.SmoothStep(0f, 1f, t);   // ease in/out

            Vector3 pivot = Vector3.Lerp(_startPivot, _targetPivot, e);
            // Drive only x/z; leave the controller's own pivot.y (terrain snap).
            ctrl.pivot = new Vector3(pivot.x, ctrl.pivot.y, pivot.z);
            ctrl.zoom = Mathf.Lerp(_startZoom, TargetZoom, e);

            float dx = _targetPivot.x - ctrl.pivot.x;
            float dz = _targetPivot.z - ctrl.pivot.z;
            float planarDist = Mathf.Sqrt(dx * dx + dz * dz);
            if (t >= 1f || planarDist <= ArriveEpsilonM)
            {
                ctrl.pivot = new Vector3(_targetPivot.x, ctrl.pivot.y, _targetPivot.z);
                _flying = false;
            }
        }

        CameraController ResolveController()
        {
            CameraUpdateSystem cam = World.GetExistingSystemManaged<CameraUpdateSystem>();
            return cam != null ? cam.gamePlayController : null;
        }
    }
}
