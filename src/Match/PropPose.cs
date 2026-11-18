using UnityEngine;

namespace FlippingIsTag.Match
{
    /// <summary>
    /// How a prop is turned, in 7 bits so it fits a message and the prop record:
    ///
    ///   bits 0-4   turn about the vertical, in 15 degree steps (0 to 23)
    ///   bits 5-6   tipped over, in quarter turns (0 to 3)
    ///
    /// Steps rather than angles, so every machine draws exactly the same pose. The pose is on
    /// top of the way the object stands in the level: pose 0 is the object as it was found.
    /// </summary>
    internal static class PropPose
    {
        public const int Mask = 0x7F;

        private const int TurnSteps = 24;
        private const float TurnDegrees = 360f / TurnSteps;

        public static int Turn(int pose) => pose & 0x1F;

        public static int Tip(int pose) => pose >> 5 & 0x3;

        public static int Compose(int turn, int tip) =>
            ((turn % TurnSteps + TurnSteps) % TurnSteps) | (tip & 0x3) << 5;

        public static int TurnedBy(int pose, int steps) => Compose(Turn(pose) + steps, Tip(pose));

        public static int TippedOver(int pose) => Compose(Turn(pose), Tip(pose) + 1);

        /// <summary>Tipped first, about its own sideways axis, then turned about the vertical.</summary>
        public static Quaternion Rotation(int pose) =>
            Quaternion.Euler(0f, Turn(pose) * TurnDegrees, 0f) * Quaternion.Euler(Tip(pose) * 90f, 0f, 0f);
    }
}
