using System;
using EHS;

namespace FlippingIsTag.Local
{
    /// <summary>
    /// Keeps the local player's controls off while the menu is open or the player is held
    /// (frozen, or a seeker counting). Two reasons share one switch, so neither can release
    /// what the other still needs blocked.
    /// </summary>
    internal static class InputBlock
    {
        private static bool menu;
        private static bool held;
        private static bool applied;

        // Blocker token for the game's own jump block list, created on first use.
        private static Il2CppSystem.Object jumpToken;

        public static void SetMenu(bool blocked)
        {
            menu = blocked;
            Apply();
        }

        public static void SetHeld(bool blocked)
        {
            held = blocked;
            Apply();
        }

        /// <summary>
        /// Re-asserted every frame rather than only on change: the player can respawn into a
        /// fresh instance while blocked, and the block lives on the instance.
        /// </summary>
        public static void Apply()
        {
            bool blocked = menu || held;

            if (!blocked && !applied)
            {
                return;
            }

            try
            {
                PlayerRef player = Roster.Local;

                if (player?.Movement != null)
                {
                    player.Movement.BlockInput = blocked;
                }

                if (player?.MovementJump != null)
                {
                    jumpToken ??= new Il2CppSystem.Object();

                    if (blocked)
                    {
                        player.MovementJump.AddJumpBlock(jumpToken);
                    }
                    else
                    {
                        player.MovementJump.RemoveJumpBlock(jumpToken);
                    }
                }

                applied = blocked;
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Could not {(blocked ? "block" : "restore")} game input: {ex}");
            }
        }
    }
}
