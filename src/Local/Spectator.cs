using System;
using System.Collections.Generic;
using EHS;
using FlippingIsTag.Match;
using FlippingIsTag.Net;
using FlippingIsTag.Test;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FlippingIsTag.Local
{
    /// <summary>
    /// Lets a hider watch the seekers look for them: the camera leaves our body and orbits a
    /// seeker instead, still turned with the mouse, while our body stays where it hid.
    ///
    /// The camera is moved with the game's own CameraManager.SetTrackingObject, the same call
    /// it uses to put the camera on our player, and put back with it. It follows the same part
    /// of the seeker it followed on us, so it sits as far from them as it sat from us.
    ///
    /// Once the camera is on a seeker, Cinemachine does all the following by itself. So the
    /// game is told only when the target changes, and whether there is still someone to watch
    /// is checked a few times a second, not every frame.
    /// </summary>
    internal sealed class Spectator
    {
        private const float CheckInterval = 0.5f;

        private readonly List<int> seekers = new();

        private CameraManager manager;
        private Transform ownTarget;

        /// <summary>Path of the part of our player the camera followed, or null when it was not under our player.</summary>
        private string ownPath;

        /// <summary>What the game was last told to follow.</summary>
        private Transform followed;

        private int watching = -1;
        private float nextCheckAt;

        public event Action<string> Notice;

        public bool Active => watching >= 0;

        public int Watching => watching;

        /// <summary>Hiders, and the players found and out of the round, while there are seekers to watch.</summary>
        public static bool CanWatch(MatchView view, PlayerInfo player) =>
            view.InRound && (view.Info.Phase == Phase.Hiding || view.Info.Phase == Phase.Playing) &&
            (Taunts.IsHider(player.Role) || player.Role == Role.Out);

        public void HandleKeys(TagSettings settings)
        {
            if (InputReader.WasPressedThisFrame(settings.SpectateKey.Value))
            {
                if (Active)
                {
                    Stop();
                }
                else
                {
                    Start();
                }
            }

            if (!Active)
            {
                return;
            }

            if (InputReader.WasPressedThisFrame(Key.LeftArrow))
            {
                Cycle(-1);
            }
            else if (InputReader.WasPressedThisFrame(Key.RightArrow))
            {
                Cycle(1);
            }
        }

        /// <summary>Stops when there is nothing left to watch, and follows a seeker who respawned.</summary>
        public void Tick()
        {
            if (!Active || Time.unscaledTime < nextCheckAt)
            {
                return;
            }

            nextCheckAt = Time.unscaledTime + CheckInterval;
            MatchView view = StateSync.View;

            if (!CanWatch(view, view.Get(Roster.LocalId)))
            {
                Stop();
                return;
            }

            FindSeekers(view);

            if (seekers.Count == 0)
            {
                Stop();
                Notice?.Invoke("No seekers to watch");
                return;
            }

            if (!seekers.Contains(watching))
            {
                watching = seekers[0];
            }

            Follow(watching);
        }

        /// <summary>Puts the camera back on our own player.</summary>
        public void Stop()
        {
            if (manager != null && ownTarget != null)
            {
                try
                {
                    manager.SetTrackingObject(ownTarget);
                }
                catch (Exception ex)
                {
                    TagPlugin.Logger.LogError($"Could not put the camera back on our player: {ex.Message}");
                }
            }

            Forget();
        }

        /// <summary>Drops a camera that went with the old level, without touching it.</summary>
        public void Forget()
        {
            manager = null;
            ownTarget = null;
            ownPath = null;
            followed = null;
            watching = -1;
        }

        private void Start()
        {
            MatchView view = StateSync.View;

            if (!CanWatch(view, view.Get(Roster.LocalId)))
            {
                Notice?.Invoke("Only hiders can watch the seekers");
                return;
            }

            FindSeekers(view);

            if (seekers.Count == 0)
            {
                Notice?.Invoke("No seekers to watch");
                return;
            }

            try
            {
                PlayerRef local = Roster.Local;
                manager = local?.GameRefs?.CameraManager;
                CinemachineCamera camera = manager?.MainCinemachineCamera;
                ownTarget = camera?.Follow;

                if (local == null || ownTarget == null)
                {
                    TagPlugin.Logger.LogWarning("The camera follows nothing we can put back; not spectating.");
                    Forget();
                    return;
                }

                ownPath = PathUnder(local.transform, ownTarget);
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Could not take over the camera: {ex.Message}");
                Forget();
                return;
            }

            watching = seekers[0];
            nextCheckAt = Time.unscaledTime + CheckInterval;
            Follow(watching);
        }

        private void Cycle(int step)
        {
            FindSeekers(StateSync.View);

            if (seekers.Count == 0)
            {
                return;
            }

            int index = seekers.IndexOf(watching);
            watching = seekers[((index < 0 ? 0 : index + step) % seekers.Count + seekers.Count) % seekers.Count];
            Follow(watching);
        }

        private void FindSeekers(MatchView view)
        {
            Rules rules = Rules.For(view.Info.Mode);
            seekers.Clear();

            foreach (KeyValuePair<int, PlayerInfo> pair in view.Players)
            {
                if (pair.Key != Roster.LocalId && rules.IsChaser(pair.Value.Role) &&
                    (Dummies.IsDummy(pair.Key) || Roster.Find(pair.Key) != null))
                {
                    seekers.Add(pair.Key);
                }
            }

            seekers.Sort();
        }

        private void Follow(int ownerId)
        {
            try
            {
                Transform target = TargetOf(ownerId);

                // Only when it changed: a new target restarts the camera's move onto it.
                if (manager == null || target == null || target == followed)
                {
                    return;
                }

                manager.SetTrackingObject(target);
                followed = target;
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Could not follow a seeker: {ex.Message}");
                Stop();
            }
        }

        /// <summary>
        /// The part of the seeker that matches the part of us the camera followed. Their body
        /// when the camera followed something that is not ours; a test dummy itself.
        /// </summary>
        private Transform TargetOf(int ownerId)
        {
            if (Dummies.TryGetRoot(ownerId, out Transform dummy))
            {
                return dummy;
            }

            PlayerRef seeker = Roster.Find(ownerId);

            if (seeker == null)
            {
                return null;
            }

            if (ownPath != null)
            {
                Transform same = ownPath.Length == 0 ? seeker.transform : seeker.transform.Find(ownPath);

                if (same != null)
                {
                    return same;
                }
            }

            Rigidbody rb = seeker.Rb;
            return rb != null ? rb.transform : seeker.transform;
        }

        /// <summary>"a/b/c" from root down to part, "" for the root itself, null when part is not under root.</summary>
        private static string PathUnder(Transform root, Transform part)
        {
            if (!part.IsChildOf(root))
            {
                return null;
            }

            var names = new List<string>();

            for (Transform t = part; t != root; t = t.parent)
            {
                names.Insert(0, t.name);
            }

            return string.Join("/", names);
        }
    }
}
