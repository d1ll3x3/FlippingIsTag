using System;
using System.Collections.Generic;
using EHS;
using FlippingIsTag.Local;
using FlippingIsTag.Match;
using FlippingIsTag.Net;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace FlippingIsTag.Test
{
    /// <summary>How the test dummies behave.</summary>
    public enum DummyBehavior
    {
        /// <summary>Stand where they were put. Target practice.</summary>
        Still = 0,

        /// <summary>Walk around at random.</summary>
        Wander = 1,

        /// <summary>Play the round: chase, run, rescue, hide.</summary>
        Play = 2,
    }

    /// <summary>
    /// Test players, so a round can be tried without anyone else around.
    ///
    /// A dummy exists only on the host, which is the only machine that runs the match: it is a
    /// player in the host's match state with no network object behind it, so the rules, the
    /// scoring and the HUD treat it like anyone else. Other players do not see dummies; they
    /// are a testing tool, not bots for a lobby.
    ///
    /// Movement is a deliberately simple kinematic walker - snapped to the ground, turned
    /// around by walls and by the edge of a drop - because the point is to exercise the round,
    /// not to play Flipping is Hard well.
    /// </summary>
    internal static class Dummies
    {
        /// <summary>Owner ids for dummies: high enough to never meet a real connection, low enough to fit a message.</summary>
        public const int FirstId = 240;
        public const int MaxCount = 15;

        private const float StepHeight = 0.6f;
        private const float Gravity = 25f;
        private const float ThinkInterval = 0.25f;

        private sealed class Dummy
        {
            public int Id;
            public string Name;
            public GameObject Root;
            public Rigidbody Body;
            public DummyModel Model;
            public BoxCollider Collider;
            public Vector3 Feet;
            public Vector3 Direction;
            public float SpeedScale = 1f;
            public float FallSpeed;
            public float NextThinkAt;
            public float StopAt = float.PositiveInfinity;

            /// <summary>Lean and bounce while walking, so a dummy does not just slide.</summary>
            public float Gait;
            public float Lean;

            public float Height => Model.Height;
            public float Radius => Model.Width * 0.5f;
            public Vector3 Centre => Feet + Vector3.up * (Height * 0.5f);
        }

        private static readonly Dictionary<int, Dummy> dummies = new();
        private static readonly List<int> ids = new();
        private static readonly List<int> scratch = new();

        private static int lastRound = -1;
        private static Phase lastPhase;

        public static int Count => dummies.Count;

        /// <summary>Owner ids of every dummy, sorted.</summary>
        public static IReadOnlyList<int> Ids => ids;

        public static bool IsDummy(int ownerId) => dummies.ContainsKey(ownerId);

        public static bool TryGetName(int ownerId, out string name)
        {
            name = dummies.TryGetValue(ownerId, out Dummy dummy) ? dummy.Name : null;
            return name != null;
        }

        public static bool TryGetPosition(int ownerId, out Vector3 position)
        {
            if (dummies.TryGetValue(ownerId, out Dummy dummy) && dummy.Root != null)
            {
                // The middle of the body, like a player's rigidbody.
                position = dummy.Centre;
                return true;
            }

            position = Vector3.zero;
            return false;
        }

        /// <summary>What the prop disguise needs to stand in for a dummy.</summary>
        public static bool TryGetBody(int ownerId, out Vector3 feet, out Renderer[] renderers)
        {
            if (dummies.TryGetValue(ownerId, out Dummy dummy) && dummy.Root != null)
            {
                feet = dummy.Feet;
                renderers = dummy.Model.Renderers;
                return true;
            }

            feet = Vector3.zero;
            renderers = null;
            return false;
        }

        /// <summary>Puts a new dummy a few metres in front of the local player.</summary>
        public static string Spawn()
        {
            if (!NetRole.IsHost)
            {
                return "Only the host can spawn dummies";
            }

            if (dummies.Count >= MaxCount)
            {
                return $"That's the limit: {MaxCount} dummies";
            }

            PlayerRef local = Roster.Local;

            if (local == null || !Roster.TryGetPosition(local, out Vector3 here))
            {
                return "No player to spawn next to";
            }

            int id = FirstId;

            while (dummies.ContainsKey(id))
            {
                id++;
            }

            Camera camera = GameCamera.Get();
            Vector3 forward = camera != null ? camera.transform.forward : local.transform.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 0.01f ? forward.normalized : Vector3.forward;

            Vector3 spot = here + forward * 3f + Quaternion.Euler(0f, 90f, 0f) * forward * ((dummies.Count % 3 - 1) * 1.5f);
            Vector3 feet = GroundAt(spot, 4f, out Vector3 ground) ? ground : spot - Vector3.up;

            Dummy dummy = Create(id, feet, local);

            if (dummy == null)
            {
                return "Could not build a dummy, see the log";
            }

            dummy.Root.transform.rotation = Quaternion.LookRotation(-forward);
            dummies[id] = dummy;
            RebuildIds();
            Roster.Invalidate();

            TagPlugin.Logger.LogInfo($"Spawned {dummy.Name} (id {id}).");
            return $"{dummy.Name} spawned";
        }

        public static void RemoveAll()
        {
            foreach (Dummy dummy in dummies.Values)
            {
                if (dummy.Root != null)
                {
                    Object.Destroy(dummy.Root);
                }

                dummy.Model.Destroy();
            }

            dummies.Clear();
            ids.Clear();
            Roster.Invalidate();
        }

        /// <summary>The dummy a ray from the camera points at, if any is in reach and in sight.</summary>
        public static int Raycast(Ray ray, float range)
        {
            int best = -1;
            float bestDistance = range;

            foreach (Dummy dummy in dummies.Values)
            {
                if (dummy.Root == null)
                {
                    continue;
                }

                // Sampled along the body rather than solved exactly: a handful of points is
                // plenty for something a player is aiming at by eye.
                for (int i = 0; i <= 4; i++)
                {
                    Vector3 point = dummy.Feet + Vector3.up * Mathf.Lerp(0.15f, dummy.Height - 0.1f, i / 4f);
                    float along = Vector3.Dot(point - ray.origin, ray.direction);

                    if (along < 0f || along > bestDistance)
                    {
                        continue;
                    }

                    if (Vector3.Distance(point, ray.GetPoint(along)) > dummy.Radius + 0.35f)
                    {
                        continue;
                    }

                    if (Blocked(ray.origin, ray.direction, along - 0.6f))
                    {
                        continue;
                    }

                    best = dummy.Id;
                    bestDistance = along;
                    break;
                }
            }

            return best;
        }

        /// <summary>Decisions: where each dummy wants to go, and its prop. Every frame, on the host.</summary>
        public static void Tick(HostMatch match, Props props, TagSettings settings)
        {
            if (dummies.Count == 0)
            {
                return;
            }

            if (!NetRole.IsHost)
            {
                RemoveAll();
                return;
            }

            MatchView view = match.View;
            MatchInfo info = view.Info;

            if (info.Phase == Phase.Starting && (info.Round != lastRound || lastPhase != Phase.Starting))
            {
                GatherAroundHost();
            }

            lastRound = info.Round;
            lastPhase = info.Phase;

            foreach (Dummy dummy in dummies.Values)
            {
                if (dummy.Root == null)
                {
                    continue;
                }

                PlayerInfo me = view.Get(dummy.Id);

                if (me.Role == Role.Prop && me.Prop == 0 && info.Phase != Phase.Results)
                {
                    long prop = props.RandomProp();

                    if (prop != 0)
                    {
                        match.SetProp(dummy.Id, prop);
                    }
                }

                if (Time.unscaledTime >= dummy.NextThinkAt)
                {
                    dummy.NextThinkAt = Time.unscaledTime + ThinkInterval * Random.Range(0.8f, 1.2f);
                    Think(dummy, view, settings);
                }
            }
        }

        /// <summary>Movement and touches, every physics step, on the host.</summary>
        public static void FixedTick(HostMatch match, TagSettings settings)
        {
            if (dummies.Count == 0)
            {
                return;
            }

            MatchView view = match.View;
            bool collisions = view.Info.Collisions;
            float dt = Time.fixedDeltaTime;

            scratch.Clear();

            foreach (Dummy dummy in dummies.Values)
            {
                if (dummy.Root == null)
                {
                    scratch.Add(dummy.Id);
                    continue;
                }

                if (dummy.Collider != null && dummy.Collider.enabled != collisions)
                {
                    dummy.Collider.enabled = collisions;
                }

                Move(dummy, settings.DummySpeed.Value * dummy.SpeedScale, dt);
                Touch(dummy, match);
            }

            foreach (int id in scratch)
            {
                dummies.Remove(id);
            }

            if (scratch.Count > 0)
            {
                RebuildIds();
                Roster.Invalidate();
            }
        }

        // ---- decisions --------------------------------------------------------------------

        private static void Think(Dummy dummy, MatchView view, TagSettings settings)
        {
            MatchInfo info = view.Info;
            PlayerInfo me = view.Get(dummy.Id);
            Rules rules = Rules.For(info.Mode);
            DummyBehavior behavior = settings.DummyBehavior.Value;

            dummy.SpeedScale = 1f;

            if (behavior == DummyBehavior.Still || info.Phase == Phase.Results ||
                view.InRound && rules.IsHeld(me, info.Phase))
            {
                dummy.Direction = Vector3.zero;
                return;
            }

            if (behavior == DummyBehavior.Wander || !view.InRound)
            {
                Wander(dummy, view.InRound ? 1f : 0.4f);
                return;
            }

            bool hiding = me.Role == Role.Hider || me.Role == Role.Prop;

            // While the seekers count, hiders walk off for a while and then settle.
            if (hiding && info.Phase != Phase.Playing)
            {
                if (float.IsPositiveInfinity(dummy.StopAt))
                {
                    dummy.StopAt = Time.unscaledTime + Random.Range(3f, Mathf.Max(4f, StateSync.SecondsLeft * 0.6f));
                    dummy.Direction = RandomDirection();
                }

                if (Time.unscaledTime >= dummy.StopAt)
                {
                    dummy.Direction = Vector3.zero;
                }

                return;
            }

            dummy.StopAt = float.PositiveInfinity;

            if (info.Phase != Phase.Playing)
            {
                dummy.Direction = Vector3.zero;
                return;
            }

            int target = Nearest(view, dummy, (them, mine) => !them.Immune && rules.CanTag(mine, them), 80f, out float targetDistance);
            int threat = Nearest(view, dummy, (them, mine) => !rules.IsHeld(them, info.Phase) && rules.CanTag(them, mine), 25f,
                out float threatDistance);
            int frozen = Nearest(view, dummy, (them, mine) => rules.CanRescue(mine, them), 30f, out _);

            // A hider who is not being chased stays put: moving is what gives a hider away.
            float panic = me.Role == Role.Prop ? 4f : hiding ? 12f : 25f;

            if (target >= 0 && (threat < 0 || targetDistance < threatDistance))
            {
                dummy.Direction = Towards(dummy, target);
                dummy.SpeedScale = 0.92f;
            }
            else if (threat >= 0 && threatDistance <= panic)
            {
                Vector3 away = -Towards(dummy, threat);
                dummy.Direction = (away + RandomDirection() * 0.35f).normalized;
            }
            else if (frozen >= 0 && (threat < 0 || threatDistance > 10f))
            {
                dummy.Direction = Towards(dummy, frozen);
            }
            else if (hiding)
            {
                dummy.Direction = Vector3.zero;
            }
            else
            {
                Wander(dummy, 0.6f);
            }
        }

        private static void Wander(Dummy dummy, float speedScale)
        {
            dummy.SpeedScale = speedScale;

            if (dummy.Direction == Vector3.zero || Random.value < 0.08f)
            {
                dummy.Direction = Random.value < 0.15f ? Vector3.zero : RandomDirection();
            }
        }

        private static int Nearest(MatchView view, Dummy dummy, Func<PlayerInfo, PlayerInfo, bool> wanted, float within,
            out float distance)
        {
            PlayerInfo mine = view.Get(dummy.Id);
            Vector3 here = dummy.Centre;
            int best = -1;
            distance = within;

            foreach (KeyValuePair<int, PlayerInfo> pair in view.Players)
            {
                if (pair.Key == dummy.Id || pair.Value.Role == Role.None || !wanted(pair.Value, mine))
                {
                    continue;
                }

                if (!Roster.TryGetPosition(pair.Key, out Vector3 there))
                {
                    continue;
                }

                float d = Vector3.Distance(here, there);

                if (d < distance)
                {
                    distance = d;
                    best = pair.Key;
                }
            }

            return best;
        }

        private static Vector3 Towards(Dummy dummy, int ownerId)
        {
            if (!Roster.TryGetPosition(ownerId, out Vector3 there))
            {
                return Vector3.zero;
            }

            Vector3 flat = there - dummy.Feet;
            flat.y = 0f;
            return flat.sqrMagnitude > 0.01f ? flat.normalized : Vector3.zero;
        }

        private static Vector3 RandomDirection()
        {
            float angle = Random.Range(0f, Mathf.PI * 2f);
            return new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
        }

        // ---- movement and touches ---------------------------------------------------------

        private static void Move(Dummy dummy, float speed, float dt)
        {
            Vector3 feet = dummy.Feet;
            Vector3 direction = dummy.Direction;

            if (direction != Vector3.zero)
            {
                float stepLength = speed * dt;

                if (Blocked(feet + Vector3.up * Mathf.Min(StepHeight + 0.1f, dummy.Height * 0.5f), direction, stepLength + dummy.Radius + 0.1f) ||
                    Blocked(feet + Vector3.up * (dummy.Height - 0.1f), direction, stepLength + dummy.Radius + 0.1f) ||
                    DropAhead(feet, direction))
                {
                    // Turned away from walls and edges rather than stopped, so it keeps going.
                    float turn = Random.Range(100f, 170f) * (Random.value < 0.5f ? -1f : 1f);
                    dummy.Direction = Quaternion.Euler(0f, turn, 0f) * direction;
                }
                else
                {
                    feet += direction * stepLength;
                }

                dummy.Root.transform.rotation = Quaternion.Slerp(dummy.Root.transform.rotation,
                    Quaternion.LookRotation(direction), 10f * dt);
            }

            if (GroundAt(feet + Vector3.up * StepHeight, StepHeight + Mathf.Max(0.3f, dummy.FallSpeed * dt), out Vector3 ground))
            {
                feet.y = ground.y;
                dummy.FallSpeed = 0f;
            }
            else
            {
                dummy.FallSpeed += Gravity * dt;
                feet.y -= dummy.FallSpeed * dt;
            }

            // Fell off the level: back next to the host.
            if (Roster.TryGetPosition(Roster.Local, out Vector3 host) && feet.y < host.y - 60f)
            {
                feet = GroundAt(host + Vector3.up, 6f, out Vector3 safe) ? safe : host;
                dummy.FallSpeed = 0f;
            }

            dummy.Feet = feet;
            Animate(dummy, direction != Vector3.zero ? speed : 0f, dt);

            if (dummy.Body != null)
            {
                dummy.Body.MovePosition(feet);
            }
            else
            {
                dummy.Root.transform.position = feet;
            }
        }

        private static void Touch(Dummy dummy, HostMatch match)
        {
            MatchView view = match.View;

            if (view.Info.Phase != Phase.Playing || !view.Info.ContactTag)
            {
                return;
            }

            Rules rules = Rules.For(view.Info.Mode);
            PlayerInfo mine = view.Get(dummy.Id);

            if (rules.IsHeld(mine, view.Info.Phase))
            {
                return;
            }

            Vector3 here = dummy.Centre;
            float radius = view.Info.TagRadius;

            scratch.Clear();

            foreach (KeyValuePair<int, PlayerInfo> pair in view.Players)
            {
                if (pair.Key == dummy.Id || !Roster.TryGetPosition(pair.Key, out Vector3 there) ||
                    Vector3.Distance(here, there) > radius)
                {
                    continue;
                }

                if (!pair.Value.Immune && rules.CanTag(mine, pair.Value) || rules.CanRescue(mine, pair.Value))
                {
                    scratch.Add(pair.Key);
                }
            }

            // Applied after the loop: a tag changes the very dictionary being walked.
            foreach (int other in scratch)
            {
                PlayerInfo theirs = view.Get(other);

                if (rules.CanRescue(mine, theirs))
                {
                    match.TryRescue(dummy.Id, other);
                }
                else
                {
                    match.TryTag(dummy.Id, other, TagSource.Contact);
                }
            }

            scratch.Clear();
        }

        /// <summary>Whether something solid that is not a player or one of ours is in the way.</summary>
        private static bool Blocked(Vector3 from, Vector3 direction, float distance)
        {
            if (distance <= 0f)
            {
                return false;
            }

            foreach (RaycastHit hit in Physics.RaycastAll(from, direction, distance, ~0, QueryTriggerInteraction.Ignore))
            {
                if (!IsIgnored(hit.collider))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Whether a step this way walks off something taller than a jump.</summary>
        private static bool DropAhead(Vector3 feet, Vector3 direction) =>
            !GroundAt(feet + direction * 1.2f + Vector3.up * StepHeight, StepHeight + 3f, out _);

        /// <summary>The highest walkable surface under a point, looking at most this far down.</summary>
        private static bool GroundAt(Vector3 from, float depth, out Vector3 ground)
        {
            ground = Vector3.zero;
            float best = float.PositiveInfinity;

            foreach (RaycastHit hit in Physics.RaycastAll(from, Vector3.down, depth, ~0, QueryTriggerInteraction.Ignore))
            {
                if (IsIgnored(hit.collider) || hit.distance >= best)
                {
                    continue;
                }

                best = hit.distance;
                ground = hit.point;
            }

            return !float.IsPositiveInfinity(best);
        }

        private static bool IsIgnored(Collider collider)
        {
            if (collider == null)
            {
                return true;
            }

            Transform root = collider.transform.root;
            return root.name.StartsWith("FlippingIsTag_", StringComparison.Ordinal) ||
                   collider.GetComponentInParent<PlayerRef>() != null;
        }

        /// <summary>At the start of a round the dummies come to the host, like everyone else.</summary>
        private static void GatherAroundHost()
        {
            if (!Roster.TryGetPosition(Roster.Local, out Vector3 centre))
            {
                return;
            }

            int index = 0;

            foreach (Dummy dummy in dummies.Values)
            {
                float angle = (index + 0.5f) * Mathf.PI * 2f / dummies.Count;
                Vector3 spot = centre + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 4f;

                dummy.Feet = GroundAt(spot + Vector3.up * 2f, 8f, out Vector3 ground) ? ground : spot;
                dummy.FallSpeed = 0f;
                dummy.Direction = Vector3.zero;
                dummy.StopAt = float.PositiveInfinity;
                dummy.Root.transform.position = dummy.Feet;
                index++;
            }
        }

        // ---- looks --------------------------------------------------------------------------

        private static Dummy Create(int id, Vector3 feet, PlayerRef look)
        {
            var root = new GameObject($"FlippingIsTag_Dummy_{id}");
            root.transform.position = feet;

            try
            {
                var model = new DummyModel();

                if (!model.Build(root.transform, look))
                {
                    TagPlugin.Logger.LogWarning("The local player has nothing drawn to copy for a dummy.");
                    Object.Destroy(root);
                    return null;
                }

                // Solid only when the host turns collisions on; sized to the copied phone.
                BoxCollider collider = root.AddComponent<BoxCollider>();
                collider.center = Vector3.up * (model.Height * 0.5f);
                collider.size = new Vector3(model.Width, model.Height, model.Width);
                collider.enabled = false;

                Rigidbody rb = root.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.useGravity = false;
                rb.interpolation = RigidbodyInterpolation.Interpolate;

                return new Dummy
                {
                    Id = id,
                    Name = $"Dummy {id - FirstId + 1}",
                    Root = root,
                    Body = rb,
                    Model = model,
                    Collider = collider,
                    Feet = feet,
                };
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Could not build a dummy: {ex}");
                Object.Destroy(root);
                return null;
            }
        }

        /// <summary>A forward lean and a little hop while it walks; upright when it stops.</summary>
        private static void Animate(Dummy dummy, float speed, float dt)
        {
            Transform body = dummy.Model.Body;

            if (body == null)
            {
                return;
            }

            dummy.Lean = Mathf.MoveTowards(dummy.Lean, speed > 0f ? 12f : 0f, 60f * dt);
            dummy.Gait += speed * dt * 2.2f;

            float hop = speed > 0f ? Mathf.Abs(Mathf.Sin(dummy.Gait)) * 0.08f : 0f;

            body.localRotation = Quaternion.Euler(dummy.Lean, 0f, Mathf.Sin(dummy.Gait) * dummy.Lean * 0.4f);
            body.localPosition = dummy.Model.BodyRest + Vector3.up * hop;
        }

        private static void RebuildIds()
        {
            ids.Clear();
            ids.AddRange(dummies.Keys);
            ids.Sort();
        }
    }
}
