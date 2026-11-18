using System;
using System.Collections.Generic;
using EHS;
using FlippingIsTag.Match;
using FlippingIsTag.Net;
using UnityEngine;

namespace FlippingIsTag.Local
{
    /// <summary>
    /// What the round does to the local player: holding them in place, bringing them to the
    /// host, and noticing the touches the host has to hear about.
    ///
    /// Touches are judged here, on the toucher's machine, because this is the one place that
    /// knows exactly where the toucher is. The host then checks the claim against its own view
    /// with some slack for lag. Erring towards the chaser is what players expect: a touch that
    /// clearly landed on your screen and did not count feels far worse than a near miss that did.
    /// </summary>
    internal sealed class LocalPlay
    {
        /// <summary>Shortest time between two reports about the same player.</summary>
        private const float ReportCooldown = 0.4f;

        /// <summary>Metres between players arranged around the host at the start of a round.</summary>
        private const float GatherSpacing = 2.5f;

        private readonly Dictionary<int, float> lastReportAt = new();

        private bool held;
        private Vector3 holdPosition;
        private int lastRound = -1;
        private Phase lastPhase;
        private int lastRevealSlot = -1;

        public event Action<string> Notice;

        public bool Held => held;

        public void Clear()
        {
            lastReportAt.Clear();
            Release();
            lastRound = -1;
            lastRevealSlot = -1;
        }

        /// <summary>
        /// How far a point is from a player, for touches: the host sets it so that a prop's
        /// disguise counts as the prop. Falls back to the distance to their body.
        /// </summary>
        public Func<int, Vector3, float> DistanceTo { get; set; }

        public void Tick(TagSettings settings, bool hidingInPlace)
        {
            MatchView view = StateSync.View;
            int me = Roster.LocalId;

            if (me < 0 || Roster.Local == null)
            {
                return;
            }

            PlayerInfo mine = view.Get(me);
            Rules rules = Rules.For(view.Info.Mode);
            Phase phase = view.Info.Phase;

            UpdateHold(view.InRound && phase != Phase.Results && (rules.IsHeld(mine, phase) || hidingInPlace));
            WatchRoundStart(view, me);

            if (phase == Phase.Playing && !held)
            {
                ReportTouches(view, rules, mine, me);
                Reveal(view, mine);
            }

            lastPhase = phase;
        }

        public void FixedTick()
        {
            if (!held)
            {
                return;
            }

            try
            {
                Rigidbody rb = Roster.Local?.Rb;

                if (rb == null || rb.isKinematic)
                {
                    return;
                }

                // Pinned rather than slowed: a frozen player sliding off a ledge is not frozen.
                rb.position = holdPosition;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Could not hold the player in place: {ex.Message}");
            }
        }

        private void UpdateHold(bool shouldHold)
        {
            if (shouldHold && !held)
            {
                if (Roster.TryGetPosition(Roster.Local, out Vector3 here))
                {
                    holdPosition = here;
                    held = true;
                }
            }
            else if (!shouldHold && held)
            {
                Release();
            }

            InputBlock.SetHeld(held);
        }

        private void Release()
        {
            held = false;
            InputBlock.SetHeld(false);
        }

        /// <summary>A new round: everyone comes to the host, spread in a ring so nobody lands on anybody.</summary>
        private void WatchRoundStart(MatchView view, int me)
        {
            MatchInfo info = view.Info;

            bool started = info.Phase == Phase.Starting && (info.Round != lastRound || lastPhase != Phase.Starting);
            lastRound = info.Round;

            if (!started)
            {
                return;
            }

            lastRevealSlot = -1;
            lastReportAt.Clear();

            if (!info.Gather || NetRole.IsHost)
            {
                return;
            }

            int host = StateSync.HostId;

            if (host < 0 || !Roster.TryGetPosition(host, out Vector3 centre))
            {
                return;
            }

            IReadOnlyList<int> ids = Roster.Ids;
            int slot = 0;

            for (int i = 0; i < ids.Count; i++)
            {
                if (ids[i] == me)
                {
                    break;
                }

                if (ids[i] != host)
                {
                    slot++;
                }
            }

            int others = Mathf.Max(1, ids.Count - 1);
            float angle = slot * Mathf.PI * 2f / others;
            float radius = Mathf.Max(GatherSpacing, GatherSpacing * others / (Mathf.PI * 2f));
            Vector3 target = centre + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius + Vector3.up * 0.5f;

            try
            {
                PlayerRef local = Roster.Local;
                PlayerTeleportController teleport = local?.PlayerTeleportController;

                if (teleport == null)
                {
                    TagPlugin.Logger.LogWarning("No teleport controller to bring us to the host.");
                    return;
                }

                // Facing the host, so the first thing you see is everyone else.
                Vector3 look = centre - target;
                look.y = 0f;
                Quaternion rotation = look.sqrMagnitude > 0.01f ? Quaternion.LookRotation(look) : local.transform.rotation;

                teleport.TeleportTo(target, rotation);
                holdPosition = target;
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Could not bring us to the host: {ex.Message}");
            }
        }

        private void ReportTouches(MatchView view, Rules rules, PlayerInfo mine, int me)
        {
            if (!view.Info.ContactTag || !Roster.TryGetPosition(Roster.Local, out Vector3 here))
            {
                return;
            }

            float radius = view.Info.TagRadius;

            foreach (KeyValuePair<int, PlayerInfo> pair in view.Players)
            {
                int other = pair.Key;
                PlayerInfo theirs = pair.Value;

                if (other == me)
                {
                    continue;
                }

                bool tag = !theirs.Immune && rules.CanTag(mine, theirs);
                bool rescue = rules.CanRescue(mine, theirs);

                if (!tag && !rescue)
                {
                    continue;
                }

                float distance = DistanceTo?.Invoke(other, here) ??
                                 (Roster.TryGetPosition(other, out Vector3 there) ? Vector3.Distance(here, there) : float.PositiveInfinity);

                if (distance > radius)
                {
                    continue;
                }

                if (lastReportAt.TryGetValue(other, out float last) && Time.unscaledTime - last < ReportCooldown)
                {
                    continue;
                }

                lastReportAt[other] = Time.unscaledTime;

                if (tag)
                {
                    Messages.SendTag(other);
                }
                else
                {
                    Messages.SendRescue(other);
                }
            }
        }

        /// <summary>
        /// Hiders give themselves away every so often with a ping of their own, so a round
        /// cannot be won by finding one perfect corner and waiting. The slot is counted off the
        /// clock every machine shares, so all hiders ping together.
        /// </summary>
        private void Reveal(MatchView view, PlayerInfo mine)
        {
            float interval = view.Info.RevealInterval;

            if (interval <= 0f || mine.Role != Role.Hider && mine.Role != Role.Prop)
            {
                return;
            }

            int slot = Mathf.FloorToInt(StateSync.SecondsLeft / interval);

            if (lastRevealSlot < 0)
            {
                lastRevealSlot = slot;
                return;
            }

            if (slot == lastRevealSlot)
            {
                return;
            }

            lastRevealSlot = slot;

            try
            {
                PlayerRef local = Roster.Local;
                PlayerPings pings = local?.PlayerPings;

                if (pings == null || !Roster.TryGetPosition(local, out Vector3 here))
                {
                    return;
                }

                pings.RequestPingServerRpc(here + Vector3.up, pings.GetDefaultTextureIndex(), null, null);
                Notice?.Invoke("Your position was revealed!");
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Could not reveal our position: {ex.Message}");
            }
        }
    }
}
