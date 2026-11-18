using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace FlippingIsTag.Match
{
    /// <summary>Why a tag reached the host, for the distance it is allowed from.</summary>
    internal enum TagSource
    {
        Contact,
        Ping,
    }

    /// <summary>
    /// The round, as only the host runs it.
    ///
    /// Clients never decide anything: they report what they saw (a touch, a ping, a prop they
    /// picked) and the host checks it against its own state before anything changes. What the
    /// host decides is published as state, never as events, so a client that missed a message
    /// or joined late is right again on the next publish.
    /// </summary>
    internal sealed class HostMatch
    {
        private const float ResultsSeconds = 10f;

        /// <summary>
        /// Extra distance a touch may be from, on top of the tag radius. The tagger sees their
        /// own position exactly and the target's a little in the past, and the host sees both
        /// late: a touch that was real on the tagger's screen is always a little apart here.
        /// </summary>
        private const float LagSlack = 3.5f;

        /// <summary>Shortest time between two tags by the same player, against a burst of duplicates.</summary>
        private const float TagCooldown = 0.5f;

        /// <summary>How long a seeker who missed too often is stunned.</summary>
        private const float MissStunSeconds = 5f;

        private readonly Dictionary<int, float> immuneUntil = new();
        private readonly Dictionary<int, float> frozenUntil = new();
        private readonly Dictionary<int, float> lastTagAt = new();
        private readonly Dictionary<int, float> pointRemainder = new();
        private readonly Dictionary<int, int> requestedTeam = new();
        private readonly List<int> playerIds = new();
        private readonly List<int> scratch = new();

        private float phaseEndsAt;

        public HostMatch(TagSettings settings)
        {
            Settings = settings;
            View.Info.Phase = Phase.Lobby;
        }

        public TagSettings Settings { get; }

        /// <summary>
        /// Distance from a point to a player's disguise, when they have one on: a seeker who
        /// touched or pinged the prop reached the prop, wherever its body is inside it.
        /// </summary>
        public Func<int, Vector3, float?> DisguiseDistance { get; set; }

        public MatchView View { get; } = new();

        public Rules Rules => Rules.For(View.Info.Mode);

        /// <summary>Everyone in the session, in or out of the round.</summary>
        public IReadOnlyList<int> PlayerIds => playerIds;

        /// <summary>Bumped on every change, so the publisher knows to send the match record first.</summary>
        public int Revision { get; private set; }

        /// <summary>Something worth a line on the host's screen, picked up by the HUD.</summary>
        public event Action<string> Notice;

        public void StartRound()
        {
            Settings.Validate();

            List<int> players = new(playerIds);

            if (players.Count < 2)
            {
                Notice?.Invoke("You need at least 2 players with the mod (or spawn a dummy)");
                return;
            }

            Shuffle(players);

            View.Players.Clear();
            immuneUntil.Clear();
            frozenUntil.Clear();
            lastTagAt.Clear();
            pointRemainder.Clear();

            View.Info.Mode = Settings.Mode.Value;
            View.Info.Round = (View.Info.Round + 1) % 256;
            CopyRulesToInfo();

            foreach (int id in players)
            {
                View.Players[id] = PlayerInfo.Empty;
            }

            Rules.AssignRoles(this, players);
            EnterPhase(Phase.Starting, Settings.StartDelay.Value);

            TagPlugin.Logger.LogInfo($"Round {View.Info.Round} of {Rules.Name} started with {players.Count} players.");
        }

        public void StopRound()
        {
            if (!View.InRound)
            {
                return;
            }

            EnterPhase(Phase.Results, ResultsSeconds);
            TagPlugin.Logger.LogInfo("Round stopped by the host.");
        }

        /// <summary>Back to no round at all, roles cleared. On a scene change or when the host leaves.</summary>
        public void Abort()
        {
            View.Players.Clear();
            EnterPhase(Phase.Lobby, 0f);
        }

        /// <summary>Settings a client acts on by itself travel in the match record; refreshed every frame.</summary>
        private void CopyRulesToInfo()
        {
            MatchInfo info = View.Info;

            info.ContactTag = Settings.ContactTag.Value;
            info.PingTag = Settings.PingTag.Value;
            info.HideNames = Settings.HideNames.Value;
            info.TagRadius = Settings.TagRadius.Value;
            info.TauntInterval = Settings.TauntInterval.Value;
            info.PropMisses = Settings.PropMisses.Value;
            info.Collisions = Settings.Collisions.Value;
            info.Gather = Settings.GatherAtStart.Value;

            if (!SameRules(info, View.Info))
            {
                View.Info = info;
                Revision++;
            }
        }

        private static bool SameRules(MatchInfo a, MatchInfo b) =>
            a.ContactTag == b.ContactTag && a.PingTag == b.PingTag && a.HideNames == b.HideNames &&
            Mathf.Approximately(a.TagRadius, b.TagRadius) && Mathf.Approximately(a.TauntInterval, b.TauntInterval) &&
            a.PropMisses == b.PropMisses && a.Collisions == b.Collisions && a.Gather == b.Gather;

        public void Tick(IReadOnlyList<int> sessionPlayers)
        {
            SyncPlayers(sessionPlayers);
            CopyRulesToInfo();

            float now = Time.unscaledTime;
            View.Info.Remaining = Mathf.Max(0f, phaseEndsAt - now);

            switch (View.Info.Phase)
            {
                case Phase.Lobby:
                    return;

                case Phase.Starting:
                    if (now >= phaseEndsAt)
                    {
                        if (Rules.HasHiding)
                        {
                            EnterPhase(Phase.Hiding, Settings.HideSeconds.Value);
                        }
                        else
                        {
                            EnterPhase(Phase.Playing, Settings.RoundMinutes.Value * 60f);
                        }
                    }

                    break;

                case Phase.Hiding:
                    if (now >= phaseEndsAt)
                    {
                        EnterPhase(Phase.Playing, Settings.RoundMinutes.Value * 60f);
                    }

                    break;

                case Phase.Playing:
                    Rules.Tick(this, Time.unscaledDeltaTime);
                    ExpireTimers(now);

                    if (now >= phaseEndsAt || Rules.IsDecided(this))
                    {
                        EnterPhase(Phase.Results, ResultsSeconds);
                    }

                    break;

                case Phase.Results:
                    if (now >= phaseEndsAt)
                    {
                        Abort();
                    }

                    return;
            }

            if (View.InRound && View.Info.Phase != Phase.Results)
            {
                Rules.Repair(this);

                if (CountInRound() < 2)
                {
                    Notice?.Invoke("Round over: not enough players left");
                    EnterPhase(Phase.Results, ResultsSeconds);
                }
            }
        }

        /// <summary>A tag someone claims. Returns whether it counted.</summary>
        public bool TryTag(int tagger, int target, TagSource source)
        {
            if (View.Info.Phase != Phase.Playing || tagger == target)
            {
                return false;
            }

            if (source == TagSource.Contact && !Settings.ContactTag.Value ||
                source == TagSource.Ping && !Settings.PingTag.Value)
            {
                return false;
            }

            if (!View.Players.TryGetValue(tagger, out PlayerInfo from) ||
                !View.Players.TryGetValue(target, out PlayerInfo to))
            {
                return false;
            }

            float now = Time.unscaledTime;

            if (to.Immune || !Rules.CanTag(from, to) ||
                lastTagAt.TryGetValue(tagger, out float last) && now - last < TagCooldown)
            {
                return false;
            }

            float allowed = source == TagSource.Ping ? Settings.PingRange.Value + 2f : Settings.TagRadius.Value + LagSlack;

            if (!WithinReach(tagger, target, allowed, out float distance))
            {
                TagPlugin.Logger.LogInfo(
                    $"Tag {tagger} -> {target} refused: {distance:0.#}m apart here, {allowed:0.#}m allowed.");
                return false;
            }

            lastTagAt[tagger] = now;

            to.TaggedBy = tagger;
            to.TagCount = (to.TagCount + 1) & 31;
            View.Players[target] = to;

            Rules.ApplyTag(this, tagger, target);
            Revision++;

            TagPlugin.Logger.LogInfo($"{Roster.NameOf(tagger)} tagged {Roster.NameOf(target)} ({source}, {distance:0.#}m).");
            return true;
        }

        /// <summary>A touch on a frozen teammate.</summary>
        public bool TryRescue(int rescuer, int target)
        {
            if (View.Info.Phase != Phase.Playing || rescuer == target)
            {
                return false;
            }

            if (!View.Players.TryGetValue(rescuer, out PlayerInfo from) ||
                !View.Players.TryGetValue(target, out PlayerInfo to) ||
                !Rules.CanRescue(from, to))
            {
                return false;
            }

            if (!WithinReach(rescuer, target, Settings.TagRadius.Value + LagSlack, out _))
            {
                return false;
            }

            Rules.ApplyRescue(this, rescuer, target);
            Revision++;

            TagPlugin.Logger.LogInfo($"{Roster.NameOf(rescuer)} freed {Roster.NameOf(target)}.");
            return true;
        }

        /// <summary>A ping that hit nobody. In prop hunt that is a seeker guessing wrong.</summary>
        public void Miss(int seeker)
        {
            if (View.Info.Mode != GameMode.PropHunt || View.Info.Phase != Phase.Playing ||
                Settings.PropMisses.Value <= 0 ||
                !View.Players.TryGetValue(seeker, out PlayerInfo info) || info.Role != Role.Seeker)
            {
                return;
            }

            info.Misses++;

            if (info.Misses >= Settings.PropMisses.Value)
            {
                info.Misses = 0;
                info.Role = Role.Frozen;
                View.Players[seeker] = info;
                FreezeFor(seeker, MissStunSeconds);
                Notice?.Invoke($"{Roster.NameOf(seeker)} missed too often and is stunned");
            }
            else
            {
                View.Players[seeker] = info;
            }

            Revision++;
        }

        /// <summary>A hider picking what to look like.</summary>
        public void SetProp(int ownerId, long prop)
        {
            if (View.Info.Mode != GameMode.PropHunt ||
                !View.Players.TryGetValue(ownerId, out PlayerInfo info) || info.Role != Role.Prop)
            {
                return;
            }

            if (info.Prop == prop)
            {
                return;
            }

            info.Prop = prop;
            info.PropPose = 0;
            info.PropOffset = 0;
            View.Players[ownerId] = info;
            Revision++;
        }

        /// <summary>A prop shifting its disguise on one axis. The value is six bits, so it cannot reach past the limit.</summary>
        public void SetPropOffset(int ownerId, int axisAndValue)
        {
            if (!View.Players.TryGetValue(ownerId, out PlayerInfo info) || info.Role != Role.Prop || info.Prop == 0)
            {
                return;
            }

            int axis = axisAndValue >> 6 & 0x3;
            int value = axisAndValue & PropOffset.AxisMask;

            // -32 is the one value the bits allow beyond the limit.
            if (axis > 2 || value == 32)
            {
                return;
            }

            int offset = PropOffset.WithAxis(info.PropOffset, axis, value);

            if (offset == info.PropOffset)
            {
                return;
            }

            info.PropOffset = offset;
            View.Players[ownerId] = info;
            Revision++;
        }

        /// <summary>A prop turning itself.</summary>
        public void SetPropPose(int ownerId, int pose)
        {
            if (!View.Players.TryGetValue(ownerId, out PlayerInfo info) || info.Role != Role.Prop ||
                info.Prop == 0 || info.PropPose == pose)
            {
                return;
            }

            info.PropPose = pose & PropPose.Mask;
            View.Players[ownerId] = info;
            Revision++;
        }

        /// <summary>A player asking for a team, honoured at the next round.</summary>
        public void RequestTeam(int ownerId, int team)
        {
            requestedTeam[ownerId] = Mathf.Clamp(team, 0, 1);
            Notice?.Invoke($"{Roster.NameOf(ownerId)} wants to play for {TeamRules.TeamName(team)}");
        }

        public int? RequestedTeam(int ownerId) =>
            requestedTeam.TryGetValue(ownerId, out int team) ? team : null;

        // ---- used by the rules ------------------------------------------------------------

        public void SetRole(int ownerId, Role role, int? team = null)
        {
            PlayerInfo info = View.Get(ownerId);
            info.Role = role;

            if (team.HasValue)
            {
                info.Team = team.Value;
            }

            if (role != Role.Prop)
            {
                info.Prop = 0;
                info.PropPose = 0;
                info.PropOffset = 0;
            }

            if (role != Role.Frozen)
            {
                frozenUntil.Remove(ownerId);
            }

            View.Players[ownerId] = info;
            Revision++;
        }

        public void MakeImmune(int ownerId)
        {
            float seconds = Settings.TagBackSeconds.Value;

            if (seconds <= 0f)
            {
                return;
            }

            immuneUntil[ownerId] = Time.unscaledTime + seconds;
            SetImmune(ownerId, true);
        }

        /// <summary>Frozen for a while, then back to running with a moment of immunity.</summary>
        public void FreezeFor(int ownerId, float seconds) => frozenUntil[ownerId] = Time.unscaledTime + seconds;

        public void AddPoints(int ownerId, int points)
        {
            if (!View.Players.TryGetValue(ownerId, out PlayerInfo info))
            {
                return;
            }

            info.Score += points;
            View.Players[ownerId] = info;
            Revision++;
        }

        /// <summary>Points that build up by the second; kept as a fraction until a whole one is earned.</summary>
        public void AddTimePoints(int ownerId, float points)
        {
            pointRemainder.TryGetValue(ownerId, out float remainder);
            remainder += points;

            int whole = Mathf.FloorToInt(remainder);
            pointRemainder[ownerId] = remainder - whole;

            if (whole > 0)
            {
                AddPoints(ownerId, whole);
            }
        }

        /// <summary>Turns a random player of one role into another, to keep someone chasing.</summary>
        public void PromoteRandom(Role from, Role to, int? team = null)
        {
            scratch.Clear();

            foreach (KeyValuePair<int, PlayerInfo> pair in View.Players)
            {
                if (pair.Value.Role == from)
                {
                    scratch.Add(pair.Key);
                }
            }

            if (scratch.Count == 0)
            {
                return;
            }

            int chosen = scratch[Random.Range(0, scratch.Count)];
            SetRole(chosen, to, team);
            Notice?.Invoke($"{Roster.NameOf(chosen)} is now chasing");
        }

        /// <summary>Moves players off the bigger team until the two differ by one at most.</summary>
        public void BalanceTeams()
        {
            for (int guard = 0; guard < 32; guard++)
            {
                int red = 0;
                int blue = 0;

                foreach (PlayerInfo player in View.Players.Values)
                {
                    if (player.Team == 0)
                    {
                        red++;
                    }
                    else
                    {
                        blue++;
                    }
                }

                if (Mathf.Abs(red - blue) <= 1)
                {
                    return;
                }

                int from = red > blue ? 0 : 1;

                foreach (KeyValuePair<int, PlayerInfo> pair in View.Players)
                {
                    if (pair.Value.Team == from)
                    {
                        // Their wish is dropped: an even game beats getting the colour you asked for.
                        requestedTeam.Remove(pair.Key);
                        SetRole(pair.Key, pair.Value.Role, 1 - from);
                        break;
                    }
                }
            }
        }

        // ---- internals --------------------------------------------------------------------

        private void EnterPhase(Phase phase, float seconds)
        {
            View.Info.Phase = phase;
            phaseEndsAt = Time.unscaledTime + seconds;
            View.Info.Remaining = seconds;
            Revision++;

            if (phase == Phase.Results)
            {
                ReleaseAll();
                TagPlugin.Logger.LogInfo($"Round over: {Rules.Outcome(View, Roster.NameOf)}");
            }
        }

        /// <summary>Nobody stays frozen or immune into the results.</summary>
        private void ReleaseAll()
        {
            immuneUntil.Clear();
            frozenUntil.Clear();

            scratch.Clear();
            scratch.AddRange(View.Players.Keys);

            foreach (int id in scratch)
            {
                PlayerInfo info = View.Players[id];
                info.Immune = false;
                View.Players[id] = info;
            }
        }

        private void ExpireTimers(float now)
        {
            scratch.Clear();

            foreach (KeyValuePair<int, float> pair in frozenUntil)
            {
                if (now >= pair.Value)
                {
                    scratch.Add(pair.Key);
                }
            }

            foreach (int id in scratch)
            {
                frozenUntil.Remove(id);

                if (View.RoleOf(id) == Role.Frozen)
                {
                    // A seeker stunned for missing goes back to seeking, anyone else to running.
                    SetRole(id, View.Info.Mode == GameMode.PropHunt ? Role.Seeker : Role.Runner);
                    MakeImmune(id);
                }
            }

            scratch.Clear();

            foreach (KeyValuePair<int, float> pair in immuneUntil)
            {
                if (now >= pair.Value)
                {
                    scratch.Add(pair.Key);
                }
            }

            foreach (int id in scratch)
            {
                immuneUntil.Remove(id);
                SetImmune(id, false);
            }
        }

        private void SetImmune(int ownerId, bool immune)
        {
            if (!View.Players.TryGetValue(ownerId, out PlayerInfo info) || info.Immune == immune)
            {
                return;
            }

            info.Immune = immune;
            View.Players[ownerId] = info;
            Revision++;
        }

        /// <summary>Adds players who arrived and drops the ones who left.</summary>
        private void SyncPlayers(IReadOnlyList<int> sessionPlayers)
        {
            playerIds.Clear();

            for (int i = 0; i < sessionPlayers.Count; i++)
            {
                playerIds.Add(sessionPlayers[i]);
            }

            if (!View.InRound)
            {
                return;
            }

            scratch.Clear();

            foreach (int id in View.Players.Keys)
            {
                if (!playerIds.Contains(id))
                {
                    scratch.Add(id);
                }
            }

            foreach (int id in scratch)
            {
                View.Players.Remove(id);
                immuneUntil.Remove(id);
                frozenUntil.Remove(id);
                Notice?.Invoke($"{Roster.NameOf(id)} left");
                Revision++;
            }

            if (View.Info.Phase == Phase.Results)
            {
                return;
            }

            foreach (int id in playerIds)
            {
                if (!View.Players.ContainsKey(id))
                {
                    View.Players[id] = PlayerInfo.Empty;
                    Rules.AssignLateJoiner(this, id);
                    Notice?.Invoke($"{Roster.NameOf(id)} joined the round");
                }
            }
        }

        private int CountInRound()
        {
            int count = 0;

            foreach (PlayerInfo player in View.Players.Values)
            {
                if (player.Role != Role.None)
                {
                    count++;
                }
            }

            return count;
        }

        private bool WithinReach(int a, int b, float allowed, out float distance)
        {
            distance = float.PositiveInfinity;

            if (!Roster.TryGetPosition(a, out Vector3 pa) || !Roster.TryGetPosition(b, out Vector3 pb))
            {
                return false;
            }

            distance = Vector3.Distance(pa, pb);

            if (DisguiseDistance?.Invoke(b, pa) is float toDisguise)
            {
                distance = Mathf.Min(distance, toDisguise);
            }

            return distance <= allowed;
        }

        private static void Shuffle(List<int> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
