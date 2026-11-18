using System;
using System.Collections.Generic;
using System.Reflection;
using EHS;
using FlippingIsTag.Test;
using UnityEngine;

namespace FlippingIsTag
{
    /// <summary>
    /// The players in the session, by FishNet owner id.
    ///
    /// Owner ids are handed out by the server and every machine sees the same numbers, so they
    /// are what the match state is keyed by. Nothing here holds on to a PlayerRef between
    /// frames: an IL2CPP wrapper kept over a respawn points at freed memory and crashes the
    /// process. Only ids are kept, and the player is looked up again from the live list - once
    /// per frame, into a map the rest of that frame reads from, since the mod asks for the same
    /// few players dozens of times a frame.
    /// </summary>
    internal static class Roster
    {
        private const float RefreshInterval = 0.25f;

        private static readonly List<int> ids = new();
        private static readonly Dictionary<int, string> names = new();

        /// <summary>This frame's players by owner id. Never read on another frame: see the class notes.</summary>
        private static readonly Dictionary<int, PlayerRef> frame = new();

        private static readonly Dictionary<Type, PropertyInfo> ownerIdByType = new();

        private static PropertyInfo ownerIdProperty;
        private static Func<PlayerNetworked, int> readOwnerId;
        private static bool ownerIdResolved;
        private static float nextRefreshAt;
        private static int frameBuilt = -1;

        /// <summary>Owner ids of everyone in the session, sorted, ourselves included.</summary>
        public static IReadOnlyList<int> Ids => ids;

        public static int LocalId { get; private set; } = -1;

        /// <summary>False when this build cannot tell players apart; nothing can run then.</summary>
        public static bool Supported => ResolveOwnerId() != null;

        public static PlayerRef Local => PlayerRef.LocalPlayer;

        /// <summary>
        /// The list of players on this client. The backing field, not the IReadOnlyList property:
        /// the interop's version of that interface exposes neither Count nor an indexer.
        /// </summary>
        private static Il2CppSystem.Collections.Generic.List<PlayerRef> Connected
        {
            get
            {
                try
                {
                    return PlayerRef.connectedPlayersClient;
                }
                catch (Exception ex)
                {
                    TagPlugin.Logger.LogError($"Could not read the player list: {ex.Message}");
                    return null;
                }
            }
        }

        public static void Tick()
        {
            if (Time.unscaledTime < nextRefreshAt)
            {
                return;
            }

            nextRefreshAt = Time.unscaledTime + RefreshInterval;

            try
            {
                Rebuild();
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Could not rebuild the player list: {ex.Message}");
                Clear();
            }
        }

        /// <summary>Rebuilds the list on the next tick, for when someone was added or removed by us.</summary>
        public static void Invalidate() => nextRefreshAt = 0f;

        public static void Clear()
        {
            ids.Clear();
            frame.Clear();
            frameBuilt = -1;
            LocalId = -1;
            nextRefreshAt = 0f;
        }

        public static bool Contains(int ownerId) => ids.Contains(ownerId);

        /// <summary>The live PlayerRef for an owner id, or null when they are not here.</summary>
        public static PlayerRef Find(int ownerId)
        {
            if (ownerId < 0 || Dummies.IsDummy(ownerId))
            {
                return null;
            }

            if (frameBuilt != Time.frameCount)
            {
                BuildFrame();
            }

            return frame.TryGetValue(ownerId, out PlayerRef player) ? player : null;
        }

        private static void BuildFrame()
        {
            frameBuilt = Time.frameCount;
            frame.Clear();

            var players = Connected;

            if (players != null)
            {
                for (int i = 0; i < players.Count; i++)
                {
                    PlayerRef player = players[i];
                    int id = OwnerIdOf(player);

                    if (id >= 0)
                    {
                        frame.TryAdd(id, player);
                    }
                }
            }

            PlayerRef local = Local;
            int localId = OwnerIdOf(local);

            if (localId >= 0)
            {
                frame.TryAdd(localId, local);
            }
        }

        /// <summary>The nickname a player goes by, or their id when they have none yet.</summary>
        public static string NameOf(int ownerId)
        {
            if (Dummies.TryGetName(ownerId, out string dummy))
            {
                return dummy;
            }

            if (names.TryGetValue(ownerId, out string name) && !string.IsNullOrEmpty(name))
            {
                return name;
            }

            return $"Player {ownerId}";
        }

        /// <summary>Where a player is, from the rigidbody when there is one.</summary>
        public static bool TryGetPosition(int ownerId, out Vector3 position) =>
            Dummies.TryGetPosition(ownerId, out position) || TryGetPosition(Find(ownerId), out position);

        public static bool TryGetPosition(PlayerRef player, out Vector3 position)
        {
            position = Vector3.zero;

            if (player == null)
            {
                return false;
            }

            try
            {
                Rigidbody rb = player.Rb;

                if (rb != null)
                {
                    position = rb.position;
                    return true;
                }

                Transform transform = player.transform;

                if (transform == null)
                {
                    return false;
                }

                position = transform.position;
                return true;
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Could not read a player's position: {ex.Message}");
                return false;
            }
        }

        /// <summary>The FishNet owner id of a player, or -1 when it cannot be read.</summary>
        public static int OwnerIdOf(PlayerRef player)
        {
            if (player == null)
            {
                return -1;
            }

            try
            {
                return OwnerIdOf(player.PlayerNetworked);
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Could not read an owner id: {ex.Message}");
                return -1;
            }
        }

        /// <summary>The FishNet owner id behind a player's network object, or -1 when it cannot be read.</summary>
        public static int OwnerIdOf(PlayerNetworked networked) =>
            networked == null || ResolveOwnerId() == null ? -1 : readOwnerId(networked);

        /// <summary>
        /// The owner id of any of a player's network components, for the ones that reach us
        /// without their PlayerRef: the game only fills those references in on some machines.
        /// </summary>
        public static int OwnerIdOf(PlayerRef player, FishNet.Object.NetworkBehaviour behaviour)
        {
            int id = OwnerIdOf(player);

            if (id >= 0 || behaviour == null)
            {
                return id;
            }

            try
            {
                Type type = behaviour.GetType();

                if (!ownerIdByType.TryGetValue(type, out PropertyInfo property))
                {
                    property = type.GetProperty("OwnerId", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    ownerIdByType[type] = property;
                }

                return property?.GetValue(behaviour) is int owner ? owner : -1;
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Could not read a component's owner id: {ex.Message}");
                return -1;
            }
        }

        private static void Rebuild()
        {
            ids.Clear();

            PlayerRef local = Local;
            LocalId = local != null ? OwnerIdOf(local) : -1;

            var players = Connected;

            if (players != null)
            {
                for (int i = 0; i < players.Count; i++)
                {
                    PlayerRef player = players[i];
                    int id = OwnerIdOf(player);

                    // A player that has not finished spawning has no owner yet.
                    if (id >= 0 && !ids.Contains(id))
                    {
                        ids.Add(id);
                        names[id] = ReadName(player);
                    }
                }
            }

            // The local player is not always in connectedPlayersClient on the frame it spawns.
            if (LocalId >= 0 && !ids.Contains(LocalId))
            {
                ids.Add(LocalId);
                names[LocalId] = ReadName(local);
            }

            // Test dummies are players as far as the match is concerned. They only exist on the host.
            ids.AddRange(Dummies.Ids);
            ids.Sort();
        }

        private static string ReadName(PlayerRef player)
        {
            try
            {
                return player?.PlayerNetworked?.NicknameSyncVar?.Value;
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogWarning($"Could not read a nickname: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// PlayerNetworked.OwnerId, read by reflection. It is inherited from FishNet's
        /// NetworkBehaviour, so which type the interop puts it on can differ between builds; a
        /// direct call would be a MissingMethodException on the first frame.
        /// </summary>
        private static PropertyInfo ResolveOwnerId()
        {
            if (ownerIdResolved)
            {
                return ownerIdProperty;
            }

            ownerIdResolved = true;
            ownerIdProperty = typeof(PlayerNetworked).GetProperty("OwnerId",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);

            if (ownerIdProperty == null)
            {
                TagPlugin.Logger.LogError(
                    "PlayerNetworked has no OwnerId in this build, so players cannot be told apart. " +
                    "The mod will stay idle.");
                return null;
            }

            // A delegate rather than PropertyInfo.GetValue on every read: it is read for every
            // player every frame, and reflection boxes and checks each call.
            MethodInfo getter = ownerIdProperty.GetGetMethod();

            if (getter == null || getter.ReturnType != typeof(int))
            {
                TagPlugin.Logger.LogError($"PlayerNetworked.OwnerId is not a readable int in this build; the mod will stay idle.");
                ownerIdProperty = null;
                return null;
            }

            try
            {
                readOwnerId = (Func<PlayerNetworked, int>)Delegate.CreateDelegate(typeof(Func<PlayerNetworked, int>), getter);
            }
            catch (ArgumentException ex)
            {
                TagPlugin.Logger.LogWarning($"Reading OwnerId through reflection instead of a delegate: {ex.Message}");
                PropertyInfo property = ownerIdProperty;
                readOwnerId = networked => (int)property.GetValue(networked);
            }

            return ownerIdProperty;
        }
    }
}
