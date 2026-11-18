using System;
using UnityEngine;

namespace FlippingIsTag.Local
{
    /// <summary>
    /// The camera the player is looking through. Camera.main only finds a camera tagged
    /// MainCamera, which is not guaranteed in every scene, so the enabled camera drawn last
    /// stands in when there is none.
    /// </summary>
    internal static class GameCamera
    {
        public static Camera Get()
        {
            try
            {
                Camera main = Camera.main;

                if (main != null)
                {
                    return main;
                }

                Camera best = null;

                foreach (Camera camera in Camera.allCameras)
                {
                    if (camera != null && camera.enabled && (best == null || camera.depth > best.depth))
                    {
                        best = camera;
                    }
                }

                return best;
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Could not find the game camera: {ex.Message}");
                return null;
            }
        }

        /// <summary>The ray through the middle of the screen, where the player aims.</summary>
        public static bool TryGetAim(out Ray ray)
        {
            Camera camera = Get();
            ray = camera != null ? camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)) : default;
            return camera != null;
        }
    }
}
