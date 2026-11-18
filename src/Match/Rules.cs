using System.Collections.Generic;
using UnityEngine;

namespace FlippingIsTag.Match
{
    /// <summary>
    /// What makes one game different from the next.
    ///
    /// Every machine has the same rules and reads them against the same published state, which
    /// is what lets a client work out by itself who it may tag, whose name to hide and who won,
    /// without any of that travelling. Only the methods taking a HostMatch change anything,
    /// and only the host calls them.
    /// </summary>
    internal abstract class Rules
    {
        /// <summary>Points for every second spent running or hiding.</summary>
        public const float PointsPerSecond = 1f;

        /// <summary>Points for a tag, a find or a rescue.</summary>
        public const int PointsPerAction = 10;

        private static readonly Rules[] All =
        {
            new ClassicRules(),
            new InfectionRules(),
            new FreezeRules(),
            new TeamRules(),
            new HideAndSeekRules(),
            new PropHuntRules(),
        };

        public static Rules For(GameMode mode)
        {
            int index = (int)mode;
            return index >= 0 && index < All.Length ? All[index] : All[0];
        }

        public abstract GameMode Mode { get; }
        public abstract string Name { get; }
        public abstract string Description { get; }

        /// <summary>Whether the round opens with the seekers blind.</summary>
        public virtual bool HasHiding => false;

        public virtual bool UsesTeams => false;

        /// <summary>Whether this tag would count, roles only: phase, immunity and distance are the host's to check.</summary>
        public abstract bool CanTag(PlayerInfo tagger, PlayerInfo target);

        /// <summary>Whether touching this player frees them.</summary>
        public virtual bool CanRescue(PlayerInfo rescuer, PlayerInfo target) => false;

        /// <summary>Whether this player's name and marker are kept from this viewer.</summary>
        public virtual bool IsConcealedFrom(PlayerInfo target, PlayerInfo viewer) => false;

        /// <summary>Whether this player is held in place right now.</summary>
        public virtual bool IsHeld(PlayerInfo player, Phase phase) =>
            player.Role == Role.Frozen || (phase == Phase.Starting && IsChaser(player.Role));

        /// <summary>Whether this player cannot see: seekers while the rest hide.</summary>
        public virtual bool IsBlind(PlayerInfo player, Phase phase) => false;

        public virtual bool IsChaser(Role role) => role == Role.Chaser;

        /// <summary>The big line at the top of this player's screen.</summary>
        public abstract string Headline(PlayerInfo player, Phase phase);

        /// <summary>Who won, read off the final state the same way on every machine.</summary>
        public abstract string Outcome(MatchView view, System.Func<int, string> nameOf);

        // ---- host only --------------------------------------------------------------------

        /// <summary>Hands out roles and teams for a new round.</summary>
        public abstract void AssignRoles(HostMatch match, List<int> players);

        /// <summary>The role someone joining a round already under way gets.</summary>
        public abstract void AssignLateJoiner(HostMatch match, int ownerId);

        public abstract void ApplyTag(HostMatch match, int tagger, int target);

        public virtual void ApplyRescue(HostMatch match, int rescuer, int target) { }

        /// <summary>Called every frame of a running round, for points and anything on a timer.</summary>
        public virtual void Tick(HostMatch match, float dt)
        {
            foreach (int id in match.PlayerIds)
            {
                Role role = match.View.RoleOf(id);

                if (role == Role.Runner || role == Role.Hider || role == Role.Prop)
                {
                    match.AddTimePoints(id, dt * PointsPerSecond);
                }
            }
        }

        /// <summary>Whether the round is decided before the clock runs out.</summary>
        public abstract bool IsDecided(HostMatch match);

        /// <summary>Makes sure there is still someone chasing after a player leaves.</summary>
        public virtual void Repair(HostMatch match) { }

        /// <summary>How many start as chasers: the setting, or one for every four players.</summary>
        protected static int ChaserCount(HostMatch match, int players)
        {
            int wanted = match.Settings.Chasers.Value;

            if (wanted <= 0)
            {
                wanted = Mathf.Max(1, Mathf.RoundToInt(players / 4f));
            }

            // Always somebody left to chase.
            return Mathf.Clamp(wanted, 1, Mathf.Max(1, players - 1));
        }

        protected static string Names(MatchView view, Role role, System.Func<int, string> nameOf)
        {
            var names = new List<string>();

            foreach (KeyValuePair<int, PlayerInfo> pair in view.Players)
            {
                if (pair.Value.Role == role)
                {
                    names.Add(nameOf(pair.Key));
                }
            }

            return string.Join(", ", names);
        }

        /// <summary>The highest scorer, for the MVP line.</summary>
        protected static string Best(MatchView view, System.Func<int, string> nameOf)
        {
            int best = -1;
            int bestScore = int.MinValue;

            foreach (KeyValuePair<int, PlayerInfo> pair in view.Players)
            {
                if (pair.Value.Role != Role.None && pair.Value.Score > bestScore)
                {
                    best = pair.Key;
                    bestScore = pair.Value.Score;
                }
            }

            return best < 0 ? "-" : $"{nameOf(best)} ({bestScore} pts)";
        }
    }

    /// <summary>One player is it. A tag passes it on; the longer you stay free, the more you score.</summary>
    internal sealed class ClassicRules : Rules
    {
        public override GameMode Mode => GameMode.Classic;
        public override string Name => "Classic Tag";
        public override string Description => "One player is it and passes it on with a tag. Score a point for every second you stay free.";

        public override bool CanTag(PlayerInfo tagger, PlayerInfo target) =>
            tagger.Role == Role.Chaser && target.Role == Role.Runner;

        public override string Headline(PlayerInfo player, Phase phase) => player.Role switch
        {
            Role.Chaser when phase == Phase.Starting => "YOU'RE IT · wait for it…",
            Role.Chaser => "YOU'RE IT! Tag someone",
            Role.Runner => "RUN!",
            _ => string.Empty,
        };

        public override string Outcome(MatchView view, System.Func<int, string> nameOf) =>
            $"{Best(view, nameOf)} wins  ·  It at the end: {Names(view, Role.Chaser, nameOf)}";

        public override void AssignRoles(HostMatch match, List<int> players)
        {
            int chasers = ChaserCount(match, players.Count);

            for (int i = 0; i < players.Count; i++)
            {
                match.SetRole(players[i], i < chasers ? Role.Chaser : Role.Runner, 0);
            }
        }

        public override void AssignLateJoiner(HostMatch match, int ownerId) => match.SetRole(ownerId, Role.Runner, 0);

        public override void ApplyTag(HostMatch match, int tagger, int target)
        {
            match.SetRole(tagger, Role.Runner);
            match.SetRole(target, Role.Chaser);

            // No tag-backs: the one who just passed it on gets away first.
            match.MakeImmune(tagger);
        }

        public override bool IsDecided(HostMatch match) => false;

        public override void Repair(HostMatch match)
        {
            if (match.View.Count(Role.Chaser) == 0)
            {
                match.PromoteRandom(Role.Runner, Role.Chaser);
            }
        }
    }

    /// <summary>The tagged join the chasers. Survive to the end, or catch everyone.</summary>
    internal sealed class InfectionRules : Rules
    {
        public override GameMode Mode => GameMode.Infection;
        public override string Name => "Infection";
        public override string Description => "Whoever gets tagged joins the infected. Last one standing wins.";

        public override bool CanTag(PlayerInfo tagger, PlayerInfo target) =>
            tagger.Role == Role.Chaser && target.Role == Role.Runner;

        public override string Headline(PlayerInfo player, Phase phase) => player.Role switch
        {
            Role.Chaser when phase == Phase.Starting => "INFECTED · wait for it…",
            Role.Chaser => "INFECTED · spread it to everyone",
            Role.Runner => "SURVIVE!",
            _ => string.Empty,
        };

        public override string Outcome(MatchView view, System.Func<int, string> nameOf) =>
            view.Count(Role.Runner) > 0
                ? $"Survivors win: {Names(view, Role.Runner, nameOf)}"
                : $"The infection wins  ·  MVP {Best(view, nameOf)}";

        public override void AssignRoles(HostMatch match, List<int> players)
        {
            int chasers = ChaserCount(match, players.Count);

            for (int i = 0; i < players.Count; i++)
            {
                match.SetRole(players[i], i < chasers ? Role.Chaser : Role.Runner, 0);
            }
        }

        public override void AssignLateJoiner(HostMatch match, int ownerId) => match.SetRole(ownerId, Role.Chaser, 0);

        public override void ApplyTag(HostMatch match, int tagger, int target)
        {
            match.SetRole(target, Role.Chaser);
            match.AddPoints(tagger, PointsPerAction);
        }

        public override bool IsDecided(HostMatch match) => match.View.Count(Role.Runner) == 0;

        public override void Repair(HostMatch match)
        {
            if (match.View.Count(Role.Chaser) == 0)
            {
                match.PromoteRandom(Role.Runner, Role.Chaser);
            }
        }
    }

    /// <summary>Tagged runners freeze where they stand until another runner touches them.</summary>
    internal sealed class FreezeRules : Rules
    {
        public override GameMode Mode => GameMode.Freeze;
        public override string Name => "Freeze Tag";
        public override string Description => "A tag freezes you in place. Touch a frozen teammate to set them free.";
        public override bool UsesTeams => true;

        public override bool CanTag(PlayerInfo tagger, PlayerInfo target) =>
            tagger.Role == Role.Chaser && target.Role == Role.Runner;

        public override bool CanRescue(PlayerInfo rescuer, PlayerInfo target) =>
            rescuer.Role == Role.Runner && target.Role == Role.Frozen;

        public override string Headline(PlayerInfo player, Phase phase) => player.Role switch
        {
            Role.Chaser when phase == Phase.Starting => "FREEZER · wait for it…",
            Role.Chaser => "FREEZER · freeze them all",
            Role.Runner => "RUN! Unfreeze your teammates",
            Role.Frozen => "FROZEN! Wait for a teammate",
            _ => string.Empty,
        };

        public override string Outcome(MatchView view, System.Func<int, string> nameOf) =>
            view.Count(Role.Runner) == 0
                ? $"The freezers win  ·  MVP {Best(view, nameOf)}"
                : $"The runners win  ·  MVP {Best(view, nameOf)}";

        public override void AssignRoles(HostMatch match, List<int> players)
        {
            int chasers = ChaserCount(match, players.Count);

            for (int i = 0; i < players.Count; i++)
            {
                bool chaser = i < chasers;
                match.SetRole(players[i], chaser ? Role.Chaser : Role.Runner, chaser ? 0 : 1);
            }
        }

        public override void AssignLateJoiner(HostMatch match, int ownerId) => match.SetRole(ownerId, Role.Runner, 1);

        public override void ApplyTag(HostMatch match, int tagger, int target)
        {
            match.SetRole(target, Role.Frozen);
            match.AddPoints(tagger, PointsPerAction);
        }

        public override void ApplyRescue(HostMatch match, int rescuer, int target)
        {
            match.SetRole(target, Role.Runner);
            match.MakeImmune(target);
            match.AddPoints(rescuer, PointsPerAction);
        }

        // Frozen runners do not score: standing still is not surviving.
        public override bool IsDecided(HostMatch match) => match.View.Count(Role.Runner) == 0;

        public override void Repair(HostMatch match)
        {
            if (match.View.Count(Role.Chaser) == 0)
            {
                match.PromoteRandom(Role.Runner, Role.Chaser, 0);
            }
        }
    }

    /// <summary>Two teams, everybody chases and everybody runs. A tag stuns the enemy and scores.</summary>
    internal sealed class TeamRules : Rules
    {
        public override GameMode Mode => GameMode.Teams;
        public override string Name => "Team Tag";
        public override string Description => "Red against blue. Tag a rival to stun them and score a point for your team.";
        public override bool UsesTeams => true;

        public override bool CanTag(PlayerInfo tagger, PlayerInfo target) =>
            tagger.Role == Role.Runner && target.Role == Role.Runner && tagger.Team != target.Team;

        public override bool IsChaser(Role role) => false;

        public override string Headline(PlayerInfo player, Phase phase) => player.Role switch
        {
            Role.Frozen => "STUNNED!",
            Role.Runner when phase == Phase.Starting => $"TEAM {TeamName(player.Team)} · get ready…",
            Role.Runner => $"TEAM {TeamName(player.Team)} · tag the other team",
            _ => string.Empty,
        };

        public override string Outcome(MatchView view, System.Func<int, string> nameOf)
        {
            int red = view.TeamScore(0);
            int blue = view.TeamScore(1);

            string winner = red == blue ? "Draw" : red > blue ? "RED wins" : "BLUE wins";
            return $"{winner}  {red} - {blue}  ·  MVP {Best(view, nameOf)}";
        }

        public override void AssignRoles(HostMatch match, List<int> players)
        {
            // Shuffled by the host before this, so alternating is a random split.
            for (int i = 0; i < players.Count; i++)
            {
                int team = match.RequestedTeam(players[i]) ?? i % 2;
                match.SetRole(players[i], Role.Runner, team);
            }

            match.BalanceTeams();
        }

        public override void AssignLateJoiner(HostMatch match, int ownerId)
        {
            int red = 0;
            int blue = 0;

            foreach (int id in match.PlayerIds)
            {
                if (match.View.RoleOf(id) == Role.None)
                {
                    continue;
                }

                if (match.View.Get(id).Team == 0)
                {
                    red++;
                }
                else
                {
                    blue++;
                }
            }

            match.SetRole(ownerId, Role.Runner, red <= blue ? 0 : 1);
        }

        public override void ApplyTag(HostMatch match, int tagger, int target)
        {
            match.SetRole(target, Role.Frozen);
            match.FreezeFor(target, match.Settings.StunSeconds.Value);
            match.AddPoints(tagger, 1);
        }

        // Points are tags here, not time: running away is its own reward.
        public override void Tick(HostMatch match, float dt) { }

        public override bool IsDecided(HostMatch match)
        {
            int limit = match.Settings.TeamScoreLimit.Value;
            return limit > 0 && (match.View.TeamScore(0) >= limit || match.View.TeamScore(1) >= limit);
        }

        public static string TeamName(int team) => team == 0 ? "RED" : "BLUE";
    }

    /// <summary>Seekers count while the rest hide, then go looking.</summary>
    internal class HideAndSeekRules : Rules
    {
        public override GameMode Mode => GameMode.HideAndSeek;
        public override string Name => "Hide and Seek";
        public override string Description => "Seekers wait blindfolded while everyone hides. Then they go looking.";
        public override bool HasHiding => true;

        /// <summary>The role a hider has in this game.</summary>
        protected virtual Role HiddenRole => Role.Hider;

        public override bool CanTag(PlayerInfo tagger, PlayerInfo target) =>
            tagger.Role == Role.Seeker && target.Role == HiddenRole;

        public override bool IsChaser(Role role) => role == Role.Seeker;

        public override bool IsHeld(PlayerInfo player, Phase phase) =>
            player.Role == Role.Frozen ||
            (player.Role == Role.Seeker && (phase == Phase.Starting || phase == Phase.Hiding));

        public override bool IsBlind(PlayerInfo player, Phase phase) =>
            player.Role == Role.Seeker && (phase == Phase.Starting || phase == Phase.Hiding);

        public override bool IsConcealedFrom(PlayerInfo target, PlayerInfo viewer) =>
            target.Role == HiddenRole && viewer.Role != HiddenRole;

        public override string Headline(PlayerInfo player, Phase phase)
        {
            if (player.Role == Role.Seeker)
            {
                return phase == Phase.Playing ? "SEEKER · find them all" : "SEEKER · no peeking…";
            }

            if (player.Role == HiddenRole)
            {
                return phase == Phase.Playing ? HiddenSeekLine : HiddenHideLine;
            }

            return player.Role == Role.Out ? "FOUND · out until next round" : string.Empty;
        }

        protected virtual string HiddenHideLine => "HIDE!";
        protected virtual string HiddenSeekLine => "HIDDEN · don't get found";

        public override string Outcome(MatchView view, System.Func<int, string> nameOf) =>
            view.Count(HiddenRole) > 0
                ? $"The hiders win: {Names(view, HiddenRole, nameOf)}"
                : $"The seekers win  ·  MVP {Best(view, nameOf)}";

        public override void AssignRoles(HostMatch match, List<int> players)
        {
            int seekers = ChaserCount(match, players.Count);

            for (int i = 0; i < players.Count; i++)
            {
                match.SetRole(players[i], i < seekers ? Role.Seeker : HiddenRole, 0);
            }
        }

        public override void AssignLateJoiner(HostMatch match, int ownerId) => match.SetRole(ownerId, Role.Seeker, 0);

        public override void ApplyTag(HostMatch match, int tagger, int target)
        {
            match.SetRole(target, match.Settings.InfectOnFind.Value ? Role.Seeker : Role.Out);
            match.AddPoints(tagger, PointsPerAction);
        }

        public override bool IsDecided(HostMatch match) => match.View.Count(HiddenRole) == 0;

        public override void Repair(HostMatch match)
        {
            if (match.View.Count(Role.Seeker) == 0)
            {
                match.PromoteRandom(HiddenRole, Role.Seeker);
            }
        }
    }

    /// <summary>Hide and seek where the hiders are disguised as things in the level.</summary>
    internal sealed class PropHuntRules : HideAndSeekRules
    {
        public override GameMode Mode => GameMode.PropHunt;
        public override string Name => "Prop Hunt";
        public override string Description => "Disguise yourself as something in the level. Seekers tag by touch or by ping.";

        protected override Role HiddenRole => Role.Prop;
        protected override string HiddenHideLine => "DISGUISE! Look at an object and press the prop key";
        protected override string HiddenSeekLine => "YOU'RE A PROP · keep still";
    }
}
