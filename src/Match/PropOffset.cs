using UnityEngine;

namespace FlippingIsTag.Match
{
    /// <summary>
    /// How far a hiding prop has shifted its disguise from its body, in 18 bits: three axes of
    /// six bits each, in world space (x, up, z), 5 cm a step, from -31 to +31 steps.
    ///
    /// The range is the first of the limits against cheating: a disguise cannot be more than
    /// 1.55 m from the body on any axis, and no message can say otherwise because there is no
    /// room in it to. The second limit, that the body stays inside the disguise, is checked
    /// where the disguise is moved (see Props), because only a machine with the level can.
    /// </summary>
    internal static class PropOffset
    {
        public const int Bits = 18;
        public const int AxisMask = 0x3F;
        public const int MaxSteps = 31;
        public const float Step = 0.05f;

        public static float Limit => MaxSteps * Step;

        public static int Pack(Vector3 offset) =>
            Encode(offset.x) | Encode(offset.y) << 6 | Encode(offset.z) << 12;

        public static Vector3 Unpack(int packed) =>
            new(Decode(packed & AxisMask), Decode(packed >> 6 & AxisMask), Decode(packed >> 12 & AxisMask));

        /// <summary>One axis as sent in a message: the axis in the high bits, its six bits below.</summary>
        public static int Axis(int packed, int axis) => packed >> (axis * 6) & AxisMask;

        public static int WithAxis(int packed, int axis, int value) =>
            (packed & ~(AxisMask << (axis * 6))) | (value & AxisMask) << (axis * 6);

        /// <summary>Rounded to whole steps and kept inside the range on every axis.</summary>
        public static Vector3 Snap(Vector3 offset) => Unpack(Pack(offset));

        // Six bit two's complement, so that all zeros is no offset at all: a player record
        // that never mentioned an offset means the disguise sits on the body.
        private static int Encode(float metres) =>
            Mathf.Clamp(Mathf.RoundToInt(metres / Step), -MaxSteps, MaxSteps) & AxisMask;

        private static float Decode(int bits)
        {
            bits &= AxisMask;
            return (bits >= 32 ? bits - 64 : bits) * Step;
        }
    }
}
