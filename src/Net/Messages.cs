using System;
using System.Reflection;
using EHS;
using FishNet.Serializing;
using HarmonyLib;
using FlippingIsTag.Match;

namespace FlippingIsTag.Net
{
    /// <summary>
    /// What a client tells the host: a touch, a rescue, the prop it picked, the team it wants.
    ///
    /// A client cannot write a SyncVar, so this rides on PlayerFeatureVisualRelay instead, the
    /// same way ChainedIsHard sends its countdown: the RPC a pad uses to have its effect played
    /// everywhere takes any ushort from its owner and hands it to every machine. A feature id is
    /// numbered up from 1 by the game, one per feature in a scene, so everything from 0x4000 up is
    /// ours. An id the game does not know is looked up, not found and ignored. ChainedIsHard
    /// uses 0xF000 and up.
    ///
    ///   0x4000 + part      prop hunt: the prop picked, 24 bits as two 12 bit parts in order:
    ///                      0x4000 + bits 0-11, then 0x5000 + bits 12-23
    ///   0xE000 + owner     I touched this player
    ///   0xE100 + owner     I freed this frozen player
    ///   0xE200 + team      I want this team next round
    ///   0xE300 + pose      prop hunt: how I turned my prop (see PropPose)
    ///   0x8000 + axis|step prop hunt: how far I shifted my disguise on one axis (see PropOffset)
    ///
    /// The sender is whoever owns the relay it arrived on, so it never has to be written down.
    /// </summary>
    internal static class Messages
    {
        private const ushort PropBase = 0x4000;
        private const int PropParts = 2;
        private const int PropPartBits = 12;
        private const ushort TagBase = 0xE000;
        private const ushort RescueBase = 0xE100;
        private const ushort TeamBase = 0xE200;
        private const ushort PoseBase = 0xE300;
        private const ushort OffsetBase = 0x8000;
        private const ushort End = PoseBase + PropPose.Mask + 1;

        private static bool hooked;

        private static readonly System.Collections.Generic.Dictionary<int, (long Value, int Next)> propParts = new();

        /// <summary>Raised on the host for every message, with the sender's owner id.</summary>
        public static event Action<int, ushort> Received;

        public enum Op
        {
            None,
            PropPart,
            Tag,
            Rescue,
            Team,
            Pose,
            Offset,
        }

        public static Op Decode(ushort id, out int value)
        {
            value = 0;

            if (id >= PropBase && id < PropBase + (PropParts << PropPartBits))
            {
                value = id - PropBase;
                return Op.PropPart;
            }

            if (id >= TagBase && id < RescueBase)
            {
                value = id - TagBase;
                return Op.Tag;
            }

            if (id >= RescueBase && id < TeamBase)
            {
                value = id - RescueBase;
                return Op.Rescue;
            }

            if (id >= TeamBase && id < TeamBase + 0x10)
            {
                value = id - TeamBase;
                return Op.Team;
            }

            if (id >= PoseBase && id < End)
            {
                value = id - PoseBase;
                return Op.Pose;
            }

            if (id >= OffsetBase && id < OffsetBase + (3 << 6))
            {
                value = id - OffsetBase;
                return Op.Offset;
            }

            return Op.None;
        }

        /// <summary>
        /// Hooks the relay's observer RPC reader, where every machine, the host included, receives
        /// a relayed id. Not ActivateOneShot(ushort): the compiler inlined it into the reader, so
        /// a patch there never runs. The reader has a body of its own (checked in
        /// fih-address-map.json) and takes only a reader and an enum, so it is safe to patch.
        /// </summary>
        public static void Install(Harmony harmony)
        {
            try
            {
                MethodInfo target = null;

                foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(PlayerFeatureVisualRelay)))
                {
                    if (method.Name.StartsWith("RpcReader___Observers_ActivateOneShotObserverRpc", StringComparison.Ordinal))
                    {
                        target = method;
                        break;
                    }
                }

                if (target == null)
                {
                    TagPlugin.Logger.LogWarning("The relay's one-shot reader is not in this build; " +
                                                "clients cannot report touches or props to the host.");
                    return;
                }

                harmony.Patch(target, prefix: new HarmonyMethod(typeof(Messages), nameof(OnOneShotReceived)));
                hooked = true;
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogWarning($"Could not hook the message channel: {ex.Message}");
            }
        }

        public static void SendTag(int target) => Send(TagBase, target, 0xFF);

        public static void SendRescue(int target) => Send(RescueBase, target, 0xFF);

        /// <summary>A prop goes in two parts. The relay is ordered, so the host gets them in order.</summary>
        public static void SendProp(long prop)
        {
            for (int part = 0; part < PropParts; part++)
            {
                int bits = (int)(prop >> (part * PropPartBits) & 0xFFF);
                Send((ushort)(PropBase + (part << PropPartBits)), bits, 0xFFF);
            }
        }

        /// <summary>
        /// Puts a prop back together on the host from its parts, one sender at a time. Returns
        /// true with the whole prop when the last part arrives after all the others.
        /// </summary>
        public static bool AssembleProp(int sender, int value, out long prop)
        {
            int part = value >> PropPartBits;
            long bits = value & 0xFFF;
            prop = 0;

            if (part == 0)
            {
                propParts[sender] = (bits, 1);
                return false;
            }

            if (!propParts.TryGetValue(sender, out (long Value, int Next) pending) || pending.Next != part)
            {
                // A part out of order means one went missing: drop the lot, the next pick resends it.
                propParts.Remove(sender);
                return false;
            }

            pending = (pending.Value | bits << (part * PropPartBits), part + 1);

            if (pending.Next < PropParts)
            {
                propParts[sender] = pending;
                return false;
            }

            propParts.Remove(sender);
            prop = pending.Value;
            return true;
        }

        public static void SendTeam(int team) => Send(TeamBase, team, 0x1);

        public static void SendPose(int pose) => Send(PoseBase, pose, PropPose.Mask);

        /// <summary>Each axis that changed goes on its own: an axis is one message, never half a move.</summary>
        public static void SendOffset(int before, int after)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                int value = PropOffset.Axis(after, axis);

                if (value != PropOffset.Axis(before, axis))
                {
                    Send(OffsetBase, axis << 6 | value, (3 << 6) - 1);
                }
            }
        }

        private static void Send(ushort block, int value, int max)
        {
            if (value < 0 || value > max)
            {
                TagPlugin.Logger.LogWarning($"Message value {value} does not fit its block (max {max}); not sent.");
                return;
            }

            ushort id = (ushort)(block + value);

            // The host does not wait for its own message to go round the network.
            if (NetRole.IsHost)
            {
                Dispatch(Roster.LocalId, id);
                return;
            }

            if (!hooked)
            {
                return;
            }

            try
            {
                PlayerFeatureVisualRelay relay = Roster.Local?.PlayerFeatureVisualRelay;

                if (relay == null)
                {
                    TagPlugin.Logger.LogWarning("No relay to send through; the host will not hear this.");
                    return;
                }

                relay.ActivateOneShotServerRpc(id);
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Could not send a message to the host: {ex.Message}");
            }
        }

        /// <summary>
        /// Peeks at the id a relay one-shot carries before the game reads it. Runs inside the
        /// game's RPC handling, so nothing may escape it and the reader must be left exactly
        /// where it was.
        /// </summary>
        private static void OnOneShotReceived(PlayerFeatureVisualRelay __instance, PooledReader __0)
        {
            ushort id;

            try
            {
                int position = __0.Position;

                try
                {
                    id = __0.ReadUInt16();
                }
                finally
                {
                    __0.Position = position;
                }
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Could not read a relayed one-shot: {ex.Message}");
                return;
            }

            if (id < PropBase || id >= End)
            {
                return;
            }

            try
            {
                // Only the host acts on these, and its own were handled when it sent them.
                if (!NetRole.IsHost || __instance.IsOwner)
                {
                    return;
                }

                int sender = Roster.OwnerIdOf(__instance.playerRef, __instance);

                if (sender < 0)
                {
                    TagPlugin.Logger.LogWarning($"A message ({id:X4}) came from a player with no owner yet; dropped.");
                    return;
                }

                Dispatch(sender, id);
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Could not handle a message: {ex.Message}");
            }
        }

        private static void Dispatch(int sender, ushort id)
        {
            try
            {
                Received?.Invoke(sender, id);
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Error handling message {id:X4} from {sender}: {ex}");
            }
        }
    }
}
