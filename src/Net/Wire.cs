using FlippingIsTag.Match;
using UnityEngine;

namespace FlippingIsTag.Net
{
    /// <summary>
    /// How the match state fits in a Vector3.
    ///
    /// The carrier is PlayerNetworked.LookDirectionSyncVar, the same one ChaosIsHard and
    /// ChainedIsHard use (see StateSync for why there is no channel of our own). A record is
    /// told apart by the whole number in x, which is a tag plus 20 bits of payload:
    ///
    ///   PlayerTag + role|team|immune|taggedBy|tagCount   y = score       z = misses|has prop|offset
    ///   MatchTag  + mode|phase|round|flags               y = time left   z = radius|reveal|misses
    ///   PropTag   + pose                                 y = object      z = object (high)
    ///
    /// A prop is 24 bits (see LevelObjects), too much to ride along in the player record, so it has a
    /// record of its own that takes turns with the player's on that player's object. The player
    /// record only says whether there is one, so a prop that was dropped is gone at once rather
    /// than when its record next comes round.
    ///
    /// An untouched SyncVar is zero and a real look direction is a unit vector, so neither can
    /// reach a tag. Every number stays inside a float32's exact integer range of 2^24: the tags
    /// are 11 to 13 times 2^20, above the ranges the other two mods use (1 to 5), so running
    /// one of them alongside never makes either act on the other's records. They do still share
    /// the one SyncVar, so running both at once makes both sync badly.
    /// </summary>
    internal static class Wire
    {
        private const int TagSize = 1 << 20;
        private const int PayloadMask = TagSize - 1;

        public const int PlayerTag = 11 * TagSize;
        public const int MatchTag = 12 * TagSize;
        public const int PropTag = 13 * TagSize;

        /// <summary>Largest whole number y or z may carry exactly.</summary>
        private const int MaxExact = (1 << 24) - 1;

        public enum Kind
        {
            None,
            Player,
            Match,
            Prop,
        }

        public static Kind KindOf(Vector3 v)
        {
            if (!IsWhole(v.x) || !IsWhole(v.y) || !IsWhole(v.z))
            {
                return Kind.None;
            }

            int x = Mathf.RoundToInt(v.x);

            if (x >= PlayerTag && x < PlayerTag + TagSize)
            {
                return Kind.Player;
            }

            if (x >= MatchTag && x < MatchTag + TagSize)
            {
                return Kind.Match;
            }

            return x >= PropTag && x < PropTag + TagSize ? Kind.Prop : Kind.None;
        }

        public static Vector3 EncodePlayer(PlayerInfo p)
        {
            int taggedBy = p.TaggedBy < 0 ? 0 : Mathf.Min(p.TaggedBy + 1, 255);

            int payload =
                ((int)p.Role & 0xF) |
                (p.Team & 0x3) << 4 |
                (p.Immune ? 1 : 0) << 6 |
                taggedBy << 7 |
                (p.TagCount & 0x1F) << 15;

            int z = Mathf.Clamp(p.Misses, 0, 15) | (p.Prop != 0 ? 1 : 0) << 4 |
                    (p.PropOffset & ((1 << PropOffset.Bits) - 1)) << 5;

            return new Vector3(PlayerTag + payload, Mathf.Clamp(p.Score, 0, MaxExact), z);
        }

        /// <summary>The player record. Its prop is left at 0: whether there is one comes out separately.</summary>
        public static PlayerInfo DecodePlayer(Vector3 v, out bool hasProp)
        {
            int payload = Mathf.RoundToInt(v.x) - PlayerTag;
            int z = Mathf.RoundToInt(v.z);
            int taggedBy = payload >> 7 & 0xFF;
            hasProp = (z >> 4 & 1) != 0;

            return new PlayerInfo
            {
                Role = (Role)(payload & 0xF),
                Team = payload >> 4 & 0x3,
                Immune = (payload >> 6 & 1) != 0,
                TaggedBy = taggedBy == 0 ? -1 : taggedBy - 1,
                TagCount = payload >> 15 & 0x1F,
                Score = Mathf.RoundToInt(v.y),
                Misses = z & 0xF,
                PropOffset = z >> 5 & ((1 << PropOffset.Bits) - 1),
            };
        }

        public static Vector3 EncodeProp(long prop, int pose) =>
            new(PropTag + (pose & PropPose.Mask), (float)(prop >> 24 & MaxExact), (float)(prop & MaxExact));

        public static long DecodeProp(Vector3 v, out int pose)
        {
            pose = (Mathf.RoundToInt(v.x) - PropTag) & PropPose.Mask;
            return (long)Mathf.RoundToInt(v.y) << 24 | (long)Mathf.RoundToInt(v.z);
        }

        public static Vector3 EncodeMatch(MatchInfo m)
        {
            int payload =
                ((int)m.Mode & 0xF) |
                ((int)m.Phase & 0x7) << 4 |
                (m.Round & 0xFF) << 7 |
                (m.ContactTag ? 1 : 0) << 15 |
                (m.PingTag ? 1 : 0) << 16 |
                (m.HideNames ? 1 : 0) << 17 |
                (m.Collisions ? 1 : 0) << 18 |
                (m.Gather ? 1 : 0) << 19;

            int radius = Mathf.Clamp(Mathf.RoundToInt(m.TagRadius * 10f), 0, 63);
            int taunt = Mathf.Clamp(Mathf.RoundToInt(m.TauntInterval / 5f), 0, 127);
            int z = radius | taunt << 6 | Mathf.Clamp(m.PropMisses, 0, 31) << 13;

            int tenths = Mathf.Clamp(Mathf.CeilToInt(m.Remaining * 10f), 0, MaxExact);

            return new Vector3(MatchTag + payload, tenths, z);
        }

        public static MatchInfo DecodeMatch(Vector3 v)
        {
            int payload = Mathf.RoundToInt(v.x) - MatchTag;
            int z = Mathf.RoundToInt(v.z);

            return new MatchInfo
            {
                Mode = (GameMode)(payload & 0xF),
                Phase = (Phase)(payload >> 4 & 0x7),
                Round = payload >> 7 & 0xFF,
                ContactTag = (payload >> 15 & 1) != 0,
                PingTag = (payload >> 16 & 1) != 0,
                HideNames = (payload >> 17 & 1) != 0,
                Collisions = (payload >> 18 & 1) != 0,
                Gather = (payload >> 19 & 1) != 0,
                Remaining = Mathf.RoundToInt(v.y) / 10f,
                TagRadius = (z & 0x3F) / 10f,
                TauntInterval = (z >> 6 & 0x7F) * 5f,
                PropMisses = z >> 13 & 0x1F,
            };
        }

        private static bool IsWhole(float value) =>
            !float.IsNaN(value) && value >= 0f && value <= PropTag + TagSize &&
            value == Mathf.Round(value);
    }
}
