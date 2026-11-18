using System;
using System.Collections.Generic;
using EHS;
using FishNet.Object.Synchronizing;
using FlippingIsTag.Match;
using UnityEngine;

namespace FlippingIsTag.Net
{
    /// <summary>
    /// Carries the host's match to everybody.
    ///
    /// There is no channel of our own to be had. FishNet generates a serializer per message
    /// type at compile time, so a type declared in a mod has none, and riding on one of the
    /// game's own broadcasts dies in Il2CppInterop, which cannot build a native thunk for a
    /// delegate taking a non-blittable struct. So the state rides on a SyncVar the game already
    /// replicates and never reads outside a debug overlay: PlayerNetworked.LookDirectionSyncVar.
    ///
    /// Only the server may write a SyncVar, but it may write it on every player's object, not
    /// just its own. That is what makes this work for a whole lobby: each player's record goes
    /// on that player's own object, so who it belongs to never has to be encoded, and all of
    /// them go out in parallel. The host's object takes turns between the host's own record and
    /// the match record.
    ///
    /// State, never events: a client that joined late, missed an update or reloaded the mod is
    /// right again on the next publish without anyone noticing it went wrong.
    /// </summary>
    internal static class StateSync
    {
        private const float PublishInterval = 0.1f;

        /// <summary>How long without a match record before we decide the host is not running the mod.</summary>
        private const float HostTimeout = 4f;

        private static readonly MatchView remote = new();
        private static readonly List<int> scratch = new();

        /// <summary>The last prop record heard for each player; the player record says whether it still holds.</summary>
        private static readonly Dictionary<int, (long Prop, int Pose)> props = new();

        /// <summary>Players whose object carries their prop record on the next publish instead of their player record.</summary>
        private static readonly HashSet<int> propTurn = new();

        private static float nextPublishAt;
        private static int publishedRevision = -1;
        private static bool matchTurn;
        private static bool writeChecked;

        private static float lastMatchHeardAt = float.NegativeInfinity;
        private static float lastRemaining = -1f;
        private static float remoteEndsAt;

        /// <summary>The host's match, set by the behaviour on the host only.</summary>
        public static HostMatch Host { get; set; }

        /// <summary>The match as this machine knows it: the real one on the host, the last heard elsewhere.</summary>
        public static MatchView View => Host != null && NetRole.IsHost ? Host.View : remote;

        /// <summary>Whether there is a host running the mod to follow.</summary>
        public static bool Connected => NetRole.IsHost || Time.unscaledTime - lastMatchHeardAt <= HostTimeout;

        /// <summary>Owner id of the player whose object carries the match record: the host.</summary>
        public static int HostId { get; private set; } = -1;

        /// <summary>Seconds left in the current phase, counted down locally between updates.</summary>
        public static float SecondsLeft =>
            NetRole.IsHost && Host != null ? Host.View.Info.Remaining : Mathf.Max(0f, remoteEndsAt - Time.unscaledTime);

        public static void Tick()
        {
            if (NetRole.IsHost)
            {
                HostId = Roster.LocalId;

                if (Host != null && Time.unscaledTime >= nextPublishAt)
                {
                    nextPublishAt = Time.unscaledTime + PublishInterval;
                    Publish(Host);
                }

                return;
            }

            Consume();
        }

        /// <summary>Dropped on a scene change; the host republishes within a tick.</summary>
        public static void Reset()
        {
            remote.Players.Clear();
            remote.Info = default;
            props.Clear();
            propTurn.Clear();
            lastMatchHeardAt = float.NegativeInfinity;
            lastRemaining = -1f;
            HostId = -1;
            publishedRevision = -1;
        }

        private static void Publish(HostMatch match)
        {
            IReadOnlyList<int> ids = Roster.Ids;
            int local = Roster.LocalId;

            for (int i = 0; i < ids.Count; i++)
            {
                int id = ids[i];

                if (id == local)
                {
                    continue;
                }

                Write(id, PlayerOrProp(id, match.View.Get(id)));
            }

            if (local < 0)
            {
                return;
            }

            // The match record goes first whenever something in it changed, so a phase change
            // or a new rule is never held up behind the host's own record.
            bool sendMatch = match.Revision != publishedRevision || matchTurn;
            matchTurn = !sendMatch;

            if (sendMatch)
            {
                publishedRevision = match.Revision;
                Write(local, Wire.EncodeMatch(match.View.Info));
            }
            else
            {
                Write(local, PlayerOrProp(local, match.View.Get(local)));
            }
        }

        /// <summary>A player with a prop has two records, sent in turns.</summary>
        private static Vector3 PlayerOrProp(int ownerId, PlayerInfo info)
        {
            if (info.Prop == 0 || !propTurn.Remove(ownerId))
            {
                if (info.Prop != 0)
                {
                    propTurn.Add(ownerId);
                }

                return Wire.EncodePlayer(info);
            }

            return Wire.EncodeProp(info.Prop, info.PropPose);
        }

        private static void Write(int ownerId, Vector3 value)
        {
            try
            {
                PlayerNetworked networked = Roster.Find(ownerId)?.PlayerNetworked;
                SyncVar<Vector3> syncVar = networked?.LookDirectionSyncVar;

                if (syncVar == null || syncVar.Value == value)
                {
                    return;
                }

                syncVar.Value = value;
                VerifyWrite(syncVar, value);
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Could not publish to player {ownerId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks the host's write actually landed, once. If FishNet refuses it, every client
        /// silently sees no match - a confusing way to fail, so it gets a line in the log.
        /// </summary>
        private static void VerifyWrite(SyncVar<Vector3> syncVar, Vector3 written)
        {
            if (writeChecked)
            {
                return;
            }

            writeChecked = true;

            if (syncVar.Value != written)
            {
                TagPlugin.Logger.LogWarning(
                    "Writing to LookDirectionSyncVar did not stick, so clients will not see the match.");
            }
        }

        private static void Consume()
        {
            IReadOnlyList<int> ids = Roster.Ids;

            for (int i = 0; i < ids.Count; i++)
            {
                int id = ids[i];
                Vector3 value;

                try
                {
                    SyncVar<Vector3> syncVar = Roster.Find(id)?.PlayerNetworked?.LookDirectionSyncVar;

                    if (syncVar == null)
                    {
                        continue;
                    }

                    value = syncVar.Value;
                }
                catch (Exception ex)
                {
                    TagPlugin.Logger.LogError($"Could not read player {id}'s state: {ex.Message}");
                    continue;
                }

                switch (Wire.KindOf(value))
                {
                    case Wire.Kind.Player:
                        PlayerInfo info = Wire.DecodePlayer(value, out bool hasProp);

                        if (hasProp && props.TryGetValue(id, out (long Prop, int Pose) known))
                        {
                            info.Prop = known.Prop;
                            info.PropPose = known.Pose;
                        }

                        remote.Players[id] = info;
                        break;

                    case Wire.Kind.Prop:
                        long prop = Wire.DecodeProp(value, out int pose);
                        props[id] = (prop, pose);

                        if (remote.Players.TryGetValue(id, out PlayerInfo current))
                        {
                            current.Prop = prop;
                            current.PropPose = pose;
                            remote.Players[id] = current;
                        }

                        break;

                    case Wire.Kind.Match:
                        ReadMatch(id, Wire.DecodeMatch(value));
                        break;
                }
            }

            // Someone who left keeps no role on our screen.
            scratch.Clear();

            foreach (int id in remote.Players.Keys)
            {
                if (!Roster.Contains(id))
                {
                    scratch.Add(id);
                }
            }

            foreach (int id in scratch)
            {
                remote.Players.Remove(id);
            }

            if (!Connected && (remote.Info.Phase != Phase.Lobby || remote.Players.Count > 0))
            {
                TagPlugin.Logger.LogInfo("Lost the host's match; back to no round.");
                Reset();
            }
        }

        private static void ReadMatch(int hostId, MatchInfo info)
        {
            HostId = hostId;
            lastMatchHeardAt = Time.unscaledTime;

            // Time left only moves when the host publishes, so the clock is re-anchored on a
            // new value and runs down locally in between.
            if (info.Phase != remote.Info.Phase || !Mathf.Approximately(info.Remaining, lastRemaining))
            {
                lastRemaining = info.Remaining;
                remoteEndsAt = Time.unscaledTime + info.Remaining;
            }

            remote.Info = info;
        }
    }
}
