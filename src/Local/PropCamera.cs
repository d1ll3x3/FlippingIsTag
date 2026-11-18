using System;
using EHS;
using Unity.Cinemachine;
using UnityEngine;

namespace FlippingIsTag.Local
{
    /// <summary>
    /// Pulls the camera back while we are a big prop, so the prop fits on screen.
    ///
    /// The game's camera is a Cinemachine orbit around the player (CameraManager.orbitalFollow):
    /// either one sphere of a given radius, or three rings, each with a radius and a height.
    /// The game itself changes the radius for its clean-capture mode, which is the same knob
    /// used here. The values the game had are saved before the first change and put back
    /// exactly when we stop being a prop, so nothing of ours outlives the disguise.
    /// </summary>
    internal sealed class PropCamera
    {
        /// <summary>How fast the camera eases to its new distance, in fractions per second.</summary>
        private const float Ease = 3f;

        /// <summary>A prop up to this long needs no extra room: about the size of a player.</summary>
        private const float PlayerSize = 1.5f;

        /// <summary>How many times its size a prop wants between it and the camera, beyond a player's.</summary>
        private const float RoomPerMetre = 1.1f;

        private CinemachineOrbitalFollow orbit;
        private bool saved;
        private float baseRadius;
        private Cinemachine3OrbitRig.Settings baseOrbits;
        private float factor = 1f;

        /// <param name="spectating">Watching someone else: the camera gets the game's own distance back.</param>
        public void Tick(TagSettings settings, Props props, bool spectating)
        {
            try
            {
                float size = settings.PropCamera.Value && !spectating ? props.LocalDisguiseSize : 0f;
                float target = 1f;

                CinemachineOrbitalFollow current = Find();

                if (current == null)
                {
                    return;
                }

                // A new camera (a new level, a respawn of the rig) starts from its own values.
                if (orbit == null || orbit.Pointer != current.Pointer)
                {
                    orbit = current;
                    saved = false;
                    factor = 1f;
                }

                if (!saved && size <= 0f)
                {
                    return;
                }

                if (!saved)
                {
                    baseRadius = orbit.Radius;
                    baseOrbits = orbit.Orbits;
                    saved = true;
                }

                if (size > 0f)
                {
                    float extra = Mathf.Max(0f, size - PlayerSize) * RoomPerMetre;
                    target = (BaseDistance() + extra) / Mathf.Max(0.1f, BaseDistance());
                }

                factor = Mathf.MoveTowards(factor, target, Ease * Time.unscaledDeltaTime * Mathf.Max(1f, Mathf.Abs(target - factor)));
                Apply(factor);

                if (size <= 0f && Mathf.Approximately(factor, 1f))
                {
                    Restore();
                }
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Could not move the camera for the prop: {ex.Message}");
                orbit = null;
                saved = false;
            }
        }

        /// <summary>Drops what we knew about a camera that went with the old level, without touching it.</summary>
        public void Forget()
        {
            orbit = null;
            saved = false;
            factor = 1f;
        }

        /// <summary>Puts the game's values back, when we stop being a prop or the mod unloads.</summary>
        public void Restore()
        {
            if (saved && orbit != null)
            {
                try
                {
                    orbit.Radius = baseRadius;
                    orbit.Orbits = baseOrbits;
                }
                catch (Exception ex)
                {
                    TagPlugin.Logger.LogWarning($"Could not restore the camera distance: {ex.Message}");
                }
            }

            saved = false;
            factor = 1f;
        }

        /// <summary>How far the camera sits from the player with the game's own settings.</summary>
        private float BaseDistance() =>
            orbit.OrbitStyle == CinemachineOrbitalFollow.OrbitStyles.Sphere ? baseRadius : baseOrbits.Center.Radius;

        private void Apply(float scale)
        {
            if (orbit.OrbitStyle == CinemachineOrbitalFollow.OrbitStyles.Sphere)
            {
                orbit.Radius = baseRadius * scale;
                return;
            }

            Cinemachine3OrbitRig.Settings rings = baseOrbits;
            rings.Top.Radius *= scale;
            rings.Top.Height *= scale;
            rings.Center.Radius *= scale;
            rings.Center.Height *= scale;
            rings.Bottom.Radius *= scale;
            rings.Bottom.Height *= scale;
            orbit.Orbits = rings;
        }

        private static CinemachineOrbitalFollow Find()
        {
            PlayerRef player = Roster.Local;
            CameraManager manager = player?.GameRefs?.CameraManager;
            return manager?.orbitalFollow;
        }
    }
}
