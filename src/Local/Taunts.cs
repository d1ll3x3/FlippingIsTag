using System;
using System.Collections.Generic;
using FlippingIsTag.Match;
using FlippingIsTag.Net;
using UnityEngine;

namespace FlippingIsTag.Local
{
    /// <summary>
    /// Hiders whistle: when they press the taunt key, and by themselves when they have not for
    /// TauntInterval seconds, so a round cannot be won by finding one perfect corner and waiting.
    ///
    /// A taunt goes through the relay to every machine, the sender's own included, and each one
    /// plays it where the sender is: on their disguise when they wear one. Every machine checks
    /// the sender is a hider in the state the host published, so a seeker cannot whistle to
    /// send the others the wrong way.
    /// </summary>
    internal sealed class Taunts
    {
        /// <summary>Shortest time between two whistles from one player. The sound is about 2 s long.</summary>
        private const float Cooldown = 3f;

        /// <summary>Slack on the cooldown for whistles heard from others, for network jitter.</summary>
        private const float HeardSlack = 0.5f;

        private readonly Dictionary<int, float> heardAt = new();

        private float lastTauntAt;
        private bool clockRunning;

        public event Action<string> Notice;

        /// <summary>Seconds until we whistle by ourselves, or -1 when we will not.</summary>
        public float SecondsToAuto { get; private set; } = -1f;

        public void Clear()
        {
            heardAt.Clear();
            clockRunning = false;
            SecondsToAuto = -1f;
        }

        public static bool IsHider(Role role) => role == Role.Hider || role == Role.Prop;

        /// <summary>Whether this player may whistle now: a hider, while there is a round to hide in.</summary>
        public static bool CanTaunt(MatchView view, PlayerInfo player) =>
            view.InRound && IsHider(player.Role) && (view.Info.Phase == Phase.Hiding || view.Info.Phase == Phase.Playing);

        /// <summary>Runs the clock that makes us whistle by ourselves.</summary>
        public void Tick()
        {
            MatchView view = StateSync.View;

            // The clock only runs while the seekers are out looking: hiding time does not count.
            if (!CanTaunt(view, view.Get(Roster.LocalId)) || view.Info.Phase != Phase.Playing || view.Info.TauntInterval <= 0f)
            {
                clockRunning = false;
                SecondsToAuto = -1f;
                return;
            }

            if (!clockRunning)
            {
                clockRunning = true;
                lastTauntAt = Time.unscaledTime;
            }

            SecondsToAuto = Mathf.Max(0f, lastTauntAt + view.Info.TauntInterval - Time.unscaledTime);

            if (SecondsToAuto <= 0f)
            {
                Send();
                Notice?.Invoke("You hadn't taunted in a while, so you whistled");
            }
        }

        /// <summary>The taunt key.</summary>
        public void Taunt()
        {
            MatchView view = StateSync.View;

            if (!CanTaunt(view, view.Get(Roster.LocalId)))
            {
                Notice?.Invoke("Only hiders can taunt");
                return;
            }

            if (Time.unscaledTime - lastTauntAt < Cooldown)
            {
                return;
            }

            Send();
        }

        /// <summary>Someone's whistle arrived. Played where they are, if they are allowed to whistle.</summary>
        public void Heard(int sender, Props props)
        {
            MatchView view = StateSync.View;

            if (!CanTaunt(view, view.Get(sender)))
            {
                return;
            }

            if (heardAt.TryGetValue(sender, out float last) && Time.unscaledTime - last < Cooldown - HeardSlack)
            {
                return;
            }

            heardAt[sender] = Time.unscaledTime;

            if (props.TryGetDisguiseBounds(sender, out Bounds disguise))
            {
                Whistle.PlayAt(disguise.center);
            }
            else if (Roster.TryGetPosition(sender, out Vector3 position))
            {
                Whistle.PlayAt(position);
            }
        }

        private void Send()
        {
            lastTauntAt = Time.unscaledTime;
            Messages.SendTaunt();
        }
    }
}
