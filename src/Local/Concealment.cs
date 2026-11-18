using System;
using System.Collections.Generic;
using EHS;
using FlippingIsTag.Match;
using FlippingIsTag.Net;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FlippingIsTag.Local
{
    /// <summary>
    /// Hides a hider's name from the seekers.
    ///
    /// The game draws every player's name above their head and, at a distance, through walls -
    /// which in hide and seek gives every hiding place away. Only the name tag is turned off,
    /// and only on the screens of players the rules say should not see it.
    /// </summary>
    internal sealed class Concealment
    {
        private const float RefreshInterval = 0.5f;

        private readonly Dictionary<int, GameObject> hidden = new();
        private readonly List<int> scratch = new();
        private readonly List<int> released = new();

        private PlayerNicknameFollower[] followers = Array.Empty<PlayerNicknameFollower>();
        private float nextRefreshAt;

        /// <summary>Whether this player's name and marker are kept from us right now.</summary>
        public static bool IsConcealed(MatchView view, int ownerId)
        {
            int me = Roster.LocalId;

            if (!view.InRound || view.Info.Phase == Phase.Results || ownerId == me)
            {
                return false;
            }

            PlayerInfo target = view.Get(ownerId);

            // A name floating over a lamp gives the lamp away, so a prop's never shows,
            // whatever the setting says.
            if (!view.Info.HideNames && target.Role != Role.Prop)
            {
                return false;
            }

            return Rules.For(view.Info.Mode).IsConcealedFrom(target, view.Get(me));
        }

        public void Clear()
        {
            foreach (GameObject canvas in hidden.Values)
            {
                if (canvas != null)
                {
                    canvas.SetActive(true);
                }
            }

            hidden.Clear();
            followers = Array.Empty<PlayerNicknameFollower>();
        }

        public void LateTick()
        {
            MatchView view = StateSync.View;

            // Nothing to hide and nothing to give back: no search through the scene either.
            if (!view.InRound && hidden.Count == 0)
            {
                followers = Array.Empty<PlayerNicknameFollower>();
                return;
            }

            if (Time.unscaledTime >= nextRefreshAt)
            {
                nextRefreshAt = Time.unscaledTime + RefreshInterval;

                try
                {
                    followers = Object.FindObjectsOfType<PlayerNicknameFollower>();
                }
                catch (Exception ex)
                {
                    TagPlugin.Logger.LogError($"Could not find the name tags: {ex.Message}");
                    followers = Array.Empty<PlayerNicknameFollower>();
                }
            }

            scratch.Clear();

            foreach (PlayerNicknameFollower follower in followers)
            {
                if (follower == null)
                {
                    continue;
                }

                try
                {
                    int owner = OwnerOf(follower);
                    GameObject canvas = follower.canvasT != null ? follower.canvasT.gameObject : null;

                    if (owner < 0 || canvas == null)
                    {
                        continue;
                    }

                    if (IsConcealed(view, owner))
                    {
                        canvas.SetActive(false);
                        hidden[owner] = canvas;
                        scratch.Add(owner);
                    }
                }
                catch (Exception ex)
                {
                    TagPlugin.Logger.LogError($"Could not hide a name tag: {ex.Message}");
                }
            }

            // Anyone no longer concealed gets their name back.
            released.Clear();

            foreach (KeyValuePair<int, GameObject> pair in hidden)
            {
                if (!scratch.Contains(pair.Key))
                {
                    released.Add(pair.Key);
                }
            }

            foreach (int owner in released)
            {
                if (hidden[owner] != null)
                {
                    hidden[owner].SetActive(true);
                }

                hidden.Remove(owner);
            }
        }

        private static int OwnerOf(PlayerNicknameFollower follower)
        {
            return Roster.OwnerIdOf(follower.PlayerNetworked);
        }
    }
}
