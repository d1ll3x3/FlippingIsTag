using System;
using EHS;
using UnityEngine;

namespace FlippingIsTag.Net
{
    /// <summary>
    /// Whether we own the server side of the session. Only the host runs the match; everyone
    /// else follows. In an offline session FishNet still starts a local server, so offline
    /// behaves as host.
    /// </summary>
    internal static class NetRole
    {
        private const float RefreshInterval = 0.5f;

        private static bool isHost;
        private static float nextRefresh;

        public static bool IsHost
        {
            get
            {
                Refresh();
                return isHost;
            }
        }

        public static void Invalidate() => nextRefresh = 0f;

        private static void Refresh()
        {
            if (Time.unscaledTime < nextRefresh)
            {
                return;
            }

            nextRefresh = Time.unscaledTime + RefreshInterval;

            try
            {
                PlayerRef player = PlayerRef.LocalPlayer;

                // Nothing resolved: fail safe towards client, which only ever reads.
                isHost = player != null && player.IsServerInitialized;
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Could not resolve the network role: {ex}");
                isHost = false;
            }
        }
    }
}
