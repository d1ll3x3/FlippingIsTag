using System;
using System.Collections.Generic;
using FlippingIsTag.Local;
using FlippingIsTag.Match;
using FlippingIsTag.Menu;
using FlippingIsTag.Net;
using FlippingIsTag.Test;
using FlippingIsTag.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FlippingIsTag
{
    /// <summary>
    /// Resident component: runs the match on the host, follows it everywhere else, and draws it.
    /// Needs the IntPtr constructor for IL2CPP interop.
    /// </summary>
    public class TagBehaviour : MonoBehaviour
    {
        public TagBehaviour(IntPtr ptr) : base(ptr) { }

        /// <summary>A ping at nothing counts as a miss at most this often, whatever the client spams.</summary>
        private const float MissInterval = 1f;

        private readonly Dictionary<int, float> lastMissAt = new();

        private HostMatch match;
        private LocalPlay localPlay;
        private Props props;
        private Concealment concealment;
        private Collisions collisions;
        private PropCamera propCamera;
        private Hud hud;
        private TagMenu menu;

        private int sceneHandle;
        private bool wasHost;

        private void Awake()
        {
            TagSettings settings = TagPlugin.Settings;

            match = new HostMatch(settings);
            localPlay = new LocalPlay();
            props = new Props();
            concealment = new Concealment();
            collisions = new Collisions();
            propCamera = new PropCamera();
            hud = new Hud();
            menu = new TagMenu(settings, ToggleRound, hud.Notify);

            match.Notice += hud.Notify;
            localPlay.Notice += hud.Notify;
            props.Notice += hud.Notify;

            Messages.Received += OnMessage;
            PingHook.Pinged += OnPinged;

            // A prop's disguise is the prop, for every touch and every reach check.
            localPlay.DistanceTo = DistanceToPlayer;
            match.DisguiseDistance = (id, point) =>
                props.TryGetDisguiseBounds(id, out Bounds bounds) ? Mathf.Sqrt(bounds.SqrDistance(point)) : null;
        }

        private void Update()
        {
            try
            {
                TagSettings settings = TagPlugin.Settings;

                WatchForSceneChange();
                Roster.Tick();

                if (!Roster.Supported)
                {
                    return;
                }

                bool host = NetRole.IsHost;

                if (host)
                {
                    StateSync.Host = match;
                    match.Tick(Roster.Ids);
                    Dummies.Tick(match, props, settings);
                }
                else if (wasHost)
                {
                    // We stopped being the server (left a lobby we hosted): our round ends here.
                    match.Abort();
                    Dummies.RemoveAll();
                }

                wasHost = host;

                StateSync.Tick();
                localPlay.Tick(settings, props.HidingInPlace);
                props.Tick();
                hud.Tick();

                // Not while a key is being bound, or the menu key could never be rebound.
                if (!menu.Capturing && InputReader.WasPressedThisFrame(settings.MenuKey.Value))
                {
                    menu.Toggle();
                }

                if (menu.IsOpen)
                {
                    // Hotkeys stay off while the menu has the keyboard.
                    menu.Tick();
                    return;
                }

                if (InputReader.WasPressedThisFrame(settings.StartKey.Value))
                {
                    if (host)
                    {
                        ToggleRound();
                    }
                    else
                    {
                        hud.Notify("Only the host can start or end rounds");
                    }
                }

                if (InputReader.WasPressedThisFrame(settings.PropKey.Value))
                {
                    props.Pick();
                }

                props.HandlePoseKeys(settings);
                props.HandleHideKeys(settings);

                InputBlock.Apply();
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Error in Update: {ex}");
            }
        }

        private void FixedUpdate()
        {
            try
            {
                if (!Roster.Supported)
                {
                    return;
                }

                localPlay.FixedTick();
                collisions.FixedTick();

                if (NetRole.IsHost)
                {
                    Dummies.FixedTick(match, TagPlugin.Settings);
                }
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Error in FixedUpdate: {ex}");
            }
        }

        private void LateUpdate()
        {
            try
            {
                if (!Roster.Supported)
                {
                    return;
                }

                props.LateTick();
                concealment.LateTick();

                // After the disguise is posed this frame, so the camera fits the prop as it is now.
                propCamera.Tick(TagPlugin.Settings, props);
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Error in LateUpdate: {ex}");
            }
        }

        private void OnGUI()
        {
            try
            {
                hud.Draw(TagPlugin.Settings, menu.IsOpen, props);
                menu.Draw();
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Error drawing the HUD: {ex}");
            }
        }

        private void ToggleRound()
        {
            if (match.View.InRound && match.View.Info.Phase != Phase.Results)
            {
                match.StopRound();
            }
            else
            {
                match.StartRound();
            }
        }

        /// <summary>What a client told us. Host only: Messages only raises it there.</summary>
        private void OnMessage(int sender, ushort id)
        {
            switch (Messages.Decode(id, out int value))
            {
                case Messages.Op.Tag:
                    match.TryTag(sender, value, TagSource.Contact);
                    break;

                case Messages.Op.Rescue:
                    match.TryRescue(sender, value);
                    break;

                case Messages.Op.PropPart:
                    if (Messages.AssembleProp(sender, value, out long prop))
                    {
                        match.SetProp(sender, prop);
                    }

                    break;

                case Messages.Op.Team:
                    match.RequestTeam(sender, value);
                    break;

                case Messages.Op.Pose:
                    match.SetPropPose(sender, value);
                    break;

                case Messages.Op.Offset:
                    match.SetPropOffset(sender, value);
                    break;
            }
        }

        /// <summary>From a point to a player: their disguise when they wear one, their body when they do not.</summary>
        private float DistanceToPlayer(int ownerId, Vector3 point)
        {
            float body = Roster.TryGetPosition(ownerId, out Vector3 there) ? Vector3.Distance(point, there) : float.PositiveInfinity;

            return props.TryGetDisguiseBounds(ownerId, out Bounds bounds)
                ? Mathf.Min(body, Mathf.Sqrt(bounds.SqrDistance(point)))
                : body;
        }

        /// <summary>A ping the server received. Host only.</summary>
        private void OnPinged(int pinger, int target, Vector3 landed)
        {
            if (target >= 0)
            {
                match.TryTag(pinger, target, TagSource.Ping);
                return;
            }

            // A disguise has no collider, so a ping at a prop flies through it and lands behind.
            // The line from the pinger's head to where it landed says what it went through.
            if (Roster.TryGetPosition(pinger, out Vector3 from))
            {
                int prop = props.PingedThrough(from + Vector3.up * 0.5f, landed, pinger);

                if (prop >= 0)
                {
                    match.TryTag(pinger, prop, TagSource.Ping);
                    return;
                }
            }

            // Dummies have no network object for the game's ping to land on, so our own pings are
            // checked against them by the line of sight from the camera instead.
            if (pinger == Roster.LocalId && Dummies.Count > 0 && GameCamera.TryGetAim(out Ray ray))
            {
                int dummy = Dummies.Raycast(ray, TagPlugin.Settings.PingRange.Value + 15f);

                if (dummy >= 0)
                {
                    match.TryTag(pinger, dummy, TagSource.Ping);
                    return;
                }
            }

            // The game refuses pings that come too fast after its own check, which runs after
            // ours, so the spam has to be filtered here too.
            if (lastMissAt.TryGetValue(pinger, out float last) && Time.unscaledTime - last < MissInterval)
            {
                return;
            }

            lastMissAt[pinger] = Time.unscaledTime;
            match.Miss(pinger);
        }

        /// <summary>
        /// A new scene means new player objects. A round cannot survive it: the host's positions,
        /// the gather point and every disguise belong to the old level.
        /// </summary>
        private void WatchForSceneChange()
        {
            int handle = SceneManager.GetActiveScene().handle;

            if (handle == sceneHandle)
            {
                return;
            }

            sceneHandle = handle;

            if (match.View.InRound)
            {
                match.Abort();
            }

            // Dummies belong to the level they were put in.
            Dummies.RemoveAll();
            Roster.Clear();
            StateSync.Reset();
            NetRole.Invalidate();
            localPlay.Clear();
            props.Clear();
            concealment.Clear();
            collisions.Clear();
            propCamera.Forget();
            hud.Clear();
            lastMissAt.Clear();
        }

        private void OnDestroy()
        {
            try
            {
                Messages.Received -= OnMessage;
                PingHook.Pinged -= OnPinged;

                menu?.Close();
                propCamera?.Restore();
                Dummies.RemoveAll();
                localPlay?.Clear();
                props?.Clear();
                concealment?.Clear();
                collisions?.Clear();
                TagPlugin.Settings?.Save();
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Error shutting down: {ex}");
            }
        }
    }
}
