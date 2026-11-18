using System.Collections.Generic;

namespace FlippingIsTag.Match
{
    /// <summary>The games the mod can run. The number travels in the match state, so never reorder.</summary>
    public enum GameMode
    {
        Classic = 0,
        Infection = 1,
        Freeze = 2,
        Teams = 3,
        HideAndSeek = 4,
        PropHunt = 5,
    }

    /// <summary>Where a round is. The number travels in the match state, so never reorder.</summary>
    public enum Phase
    {
        /// <summary>No round: everyone plays the game as usual.</summary>
        Lobby = 0,

        /// <summary>Roles are out and the chasers wait while the rest get a head start.</summary>
        Starting = 1,

        /// <summary>Hiding games only: the seekers are blind.</summary>
        Hiding = 2,

        Playing = 3,

        /// <summary>The round is over and the scores stay up for a moment.</summary>
        Results = 4,
    }

    /// <summary>What a player is in the current round. The number travels, so never reorder.</summary>
    public enum Role
    {
        None = 0,
        Runner = 1,

        /// <summary>The one who is it, the infected, the freezers.</summary>
        Chaser = 2,

        /// <summary>Tagged in freeze tag, or stunned in a team battle. Cannot move or tag.</summary>
        Frozen = 3,

        Hider = 4,
        Seeker = 5,
        Prop = 6,

        /// <summary>Found with InfectOnFind off: out of the round until the next one.</summary>
        Out = 7,
    }

    /// <summary>The round as a whole, as the host publishes it.</summary>
    internal struct MatchInfo
    {
        public GameMode Mode;
        public Phase Phase;
        public int Round;

        /// <summary>Seconds left in the current phase when this was published.</summary>
        public float Remaining;

        public bool ContactTag;
        public bool PingTag;
        public bool HideNames;
        public float TagRadius;
        public float TauntInterval;
        public int PropMisses;

        /// <summary>Whether players bump into each other. Applies in and out of a round.</summary>
        public bool Collisions;

        /// <summary>Whether everyone is brought to the host when a round starts.</summary>
        public bool Gather;
    }

    /// <summary>One player in the round, as the host publishes it.</summary>
    internal struct PlayerInfo
    {
        public Role Role;
        public int Team;

        /// <summary>Just tagged or just freed: cannot be tagged for a moment.</summary>
        public bool Immune;

        /// <summary>Owner id of whoever tagged them last, or -1.</summary>
        public int TaggedBy;

        /// <summary>
        /// Goes up by one on every tag this player takes. A tag that does not change the role
        /// (a stun in a team battle that is over before the next publish) is still seen.
        /// </summary>
        public int TagCount;

        public int Score;

        /// <summary>Prop hunt: the object of the level they are (see LevelObjects), 0 for none.</summary>
        public long Prop;

        /// <summary>Prop hunt: how the prop is turned (see PropPose).</summary>
        public int PropPose;

        /// <summary>Prop hunt: how far the disguise is shifted from the body (see PropOffset).</summary>
        public int PropOffset;

        /// <summary>Prop hunt: pings at nothing so far.</summary>
        public int Misses;

        public static PlayerInfo Empty => new() { TaggedBy = -1 };
    }

    /// <summary>The whole round: the host's copy is the truth, everyone else's is what was last heard.</summary>
    internal sealed class MatchView
    {
        public MatchInfo Info;
        public readonly Dictionary<int, PlayerInfo> Players = new();

        public bool InRound => Info.Phase != Phase.Lobby;

        public PlayerInfo Get(int ownerId) =>
            Players.TryGetValue(ownerId, out PlayerInfo info) ? info : PlayerInfo.Empty;

        public Role RoleOf(int ownerId) => Get(ownerId).Role;

        public int TeamScore(int team)
        {
            int total = 0;

            foreach (PlayerInfo player in Players.Values)
            {
                if (player.Team == team && player.Role != Role.None)
                {
                    total += player.Score;
                }
            }

            return total;
        }

        public int Count(Role role)
        {
            int count = 0;

            foreach (PlayerInfo player in Players.Values)
            {
                if (player.Role == role)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
