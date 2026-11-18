using System;
using System.Reflection;
using EHS;
using HarmonyLib;
using UnityEngine;

namespace FlippingIsTag.Net
{
    /// <summary>
    /// Tagging with the game's own ping.
    ///
    /// Pinging another player already does something in the game: the client's ping carries the
    /// player it hit as a "push target" (PlayerPings.RequestPingServerRpc), and the server pushes
    /// them. So the server already learns who pinged whom, and a prefix on the server side of
    /// that RPC is all a ping tag needs - no client has to send anything of its own.
    ///
    /// The prefix reads the instance and the push target, both references, and where the ping
    /// landed, a Vector3: a plain blittable struct, passed through the detour without trouble.
    /// The landing point is what lets a ping tag a prop through its disguise, which has no
    /// collider for the game's own ping to hit.
    /// The method's body is not shared with any other (checked in fih-address-map.json), so the
    /// detour cannot fire for the wrong method. It only observes: the game's ping and push go
    /// ahead exactly as before.
    /// </summary>
    internal static class PingHook
    {
        /// <summary>Raised on the host: who pinged, who they hit (-1 for nobody), and where the ping landed.</summary>
        public static event Action<int, int, Vector3> Pinged;

        public static void Install(Harmony harmony)
        {
            try
            {
                MethodInfo target = null;

                foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(PlayerPings)))
                {
                    if (method.Name.StartsWith("RpcLogic___RequestPingServerRpc", StringComparison.Ordinal))
                    {
                        target = method;
                        break;
                    }
                }

                if (target == null || target.GetParameters().Length < 3)
                {
                    TagPlugin.Logger.LogWarning("The ping's server logic is not in this build; pings will not tag.");
                    return;
                }

                harmony.Patch(target, prefix: new HarmonyMethod(typeof(PingHook), nameof(OnPingRequested)));
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogWarning($"Could not hook pings: {ex.Message}");
            }
        }

        private static void OnPingRequested(PlayerPings __instance, Vector3 __0, PlayerPings __2)
        {
            try
            {
                if (!NetRole.IsHost || __instance == null)
                {
                    return;
                }

                int pinger = Roster.OwnerIdOf(__instance.playerRef, __instance);

                if (pinger < 0)
                {
                    return;
                }

                int target = __2 != null ? Roster.OwnerIdOf(__2.playerRef, __2) : -1;
                Pinged?.Invoke(pinger, target, __0);
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Could not read a ping: {ex.Message}");
            }
        }
    }
}
