using System;
using System.Collections.Generic;
using EHS;
using FlippingIsTag.Match;
using FlippingIsTag.Net;
using FlippingIsTag.Test;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace FlippingIsTag.Local
{
    /// <summary>
    /// Prop hunt: turning a player into an object of the level.
    ///
    /// Nothing about the prop is spawned on the network. Every machine has the same level
    /// loaded, so a prop is a name every machine can resolve on its own (see LevelObjects), and
    /// each machine draws the disguise itself from its own copy of the level and hides the
    /// player's body, locally.
    ///
    /// The disguise keeps the rotation and scale the object has in the level, so a prop looks
    /// exactly like the things around it, and stands where its player stands. On top of that
    /// the player can turn it and tip it over (see PropPose), which every machine draws the same.
    ///
    /// Hiding in place: the player stops, and can shift the disguise off their body to fit it
    /// into a corner, raise it onto a shelf or sink it into a bush (see PropOffset). Two limits
    /// keep that honest. The shift is at most 1.55 m on each axis, which the network format
    /// enforces by itself. And the body has to stay inside the disguise, give or take a hand's
    /// width: a prop cannot leave its disguise in one place and hide its body in another, and
    /// touching or pinging the disguise tags the player (see TagBehaviour and HostMatch).
    /// </summary>
    internal sealed class Props
    {
        /// <summary>How far past the player the thing you look at can be.</summary>
        private const float PickRange = 15f;

        private const float PickCooldown = 1f;
        private const float PreviewInterval = 0.2f;

        /// <summary>How long our own turn wins over what the host last published, while the host catches up.</summary>
        private const float PoseHold = 1.5f;

        /// <summary>How fast the disguise slides while a move key is held, in metres per second.</summary>
        private const float SlideSpeed = 0.8f;

        /// <summary>How far the body may poke out of its disguise, in metres.</summary>
        private const float BodyMargin = 0.35f;

        /// <summary>How far from where we hid we can walk before the disguise snaps back onto the body.</summary>
        private const float LeaveDistance = 0.75f;

        /// <summary>Shortest time between two offset messages while a key is held.</summary>
        private const float OffsetSendInterval = 0.1f;

        private sealed class Disguise
        {
            public long Prop;
            public int Pose = -1;
            public GameObject Object;
            public readonly List<MeshRenderer> Parts = new();

            /// <summary>From the player's feet to the disguise's pivot: centred, standing on its lowest point.</summary>
            public Vector3 Offset;

            /// <summary>Longest side of the posed disguise, in metres.</summary>
            public float Size;

            /// <summary>The posed disguise's bounds with its pivot at the origin.</summary>
            public Bounds Shape;

            /// <summary>Where the disguise is in the world right now.</summary>
            public Bounds World;
        }

        /// <summary>What picking right now would do, and what it would copy.</summary>
        public readonly struct Aim
        {
            public readonly long Prop;
            public readonly string Text;

            /// <summary>The object on screen, for the box drawn round it. Null when there is none.</summary>
            public readonly Bounds? Box;

            public Aim(long prop, string text, Bounds? box = null)
            {
                Prop = prop;
                Text = text;
                Box = box;
            }

            public bool Valid => Prop != 0;
        }

        private readonly LevelObjects level = new();
        private readonly Dictionary<int, Disguise> disguises = new();
        private readonly Dictionary<int, List<Renderer>> hiddenBodies = new();
        private readonly List<int> scratch = new();

        private float nextPickAt;
        private float nextPreviewAt;

        private int localPose;
        private float localPoseUntil;

        private Vector3 slide;
        private int sentOffset;
        private float nextOffsetSendAt;
        private float localOffsetUntil;
        private Vector3 hidAt;
        private float warnedAt = float.NegativeInfinity;

        /// <summary>Whether we are hiding in place: stopped, with the disguise ours to shift.</summary>
        public bool HidingInPlace { get; private set; }

        /// <summary>The shift we are showing for our own disguise, in metres.</summary>
        public Vector3 LocalOffset => PropOffset.Snap(slide);

        /// <summary>Why the last shift was refused, for a moment after it was. Null otherwise.</summary>
        public string Warning => Time.unscaledTime - warnedAt < 1.5f ? "Your body has to stay inside the prop" : null;

        public event Action<string> Notice;

        /// <summary>What the crosshair is on, for the preview under it. Null when we are not a prop.</summary>
        public Aim? Current { get; private set; }

        public void Clear()
        {
            foreach (int id in new List<int>(disguises.Keys))
            {
                Remove(id);
            }

            level.Clear();
            Current = null;
        }

        /// <summary>Keeps the preview up to date while we are a prop. A few times a second is plenty.</summary>
        public void Tick()
        {
            if (!CanPick(out _))
            {
                Current = null;
                return;
            }

            if (Time.unscaledTime < nextPreviewAt)
            {
                return;
            }

            nextPreviewAt = Time.unscaledTime + PreviewInterval;
            Current = Look();
        }

        /// <summary>Turns into whatever is in the middle of the screen.</summary>
        public void Pick()
        {
            if (!CanPick(out string why))
            {
                Notice?.Invoke(why);
                return;
            }

            if (Time.unscaledTime < nextPickAt)
            {
                return;
            }

            Aim aim = Look();
            Current = aim;

            if (!aim.Valid)
            {
                Notice?.Invoke(aim.Text);
                return;
            }

            nextPickAt = Time.unscaledTime + PickCooldown;

            // A new prop starts on the body: the host drops the old shift with the old prop.
            slide = Vector3.zero;
            sentOffset = 0;

            Messages.SendProp(aim.Prop);
            Notice?.Invoke($"You are now: {aim.Text}");
            TagPlugin.Logger.LogInfo($"Picked prop {aim.Prop:X6} ({aim.Text}).");
        }

        /// <summary>
        /// Turns or tips our own prop. Shown at once here and sent to the host, which tells
        /// everyone else; until the host's word comes back, ours is the one drawn.
        /// </summary>
        public void HandlePoseKeys(TagSettings settings)
        {
            MatchView view = StateSync.View;
            PlayerInfo mine = view.Get(Roster.LocalId);

            if (mine.Role != Role.Prop || mine.Prop == 0 || view.Info.Phase == Phase.Results)
            {
                return;
            }

            int pose = Time.unscaledTime < localPoseUntil ? localPose : mine.PropPose;
            int next = pose;

            if (InputReader.WasPressedThisFrame(settings.RotateLeftKey.Value))
            {
                next = PropPose.TurnedBy(next, -1);
            }

            if (InputReader.WasPressedThisFrame(settings.RotateRightKey.Value))
            {
                next = PropPose.TurnedBy(next, 1);
            }

            if (InputReader.WasPressedThisFrame(settings.TipOverKey.Value))
            {
                next = PropPose.TippedOver(next);
            }

            if (next == pose)
            {
                return;
            }

            localPose = next;
            localPoseUntil = Time.unscaledTime + PoseHold;
            Messages.SendPose(next);
        }

        /// <summary>
        /// Hiding in place: the hide key stops us and hands the disguise to the move keys; the
        /// arrows slide it across (the way the camera looks), the raise and lower keys move it
        /// up and down. Every step is checked against the body before it is taken.
        /// </summary>
        public void HandleHideKeys(TagSettings settings)
        {
            MatchView view = StateSync.View;
            PlayerInfo mine = view.Get(Roster.LocalId);
            bool disguised = mine.Role == Role.Prop && mine.Prop != 0 && view.Info.Phase != Phase.Results &&
                             disguises.TryGetValue(Roster.LocalId, out Disguise own) && own.Object != null;

            if (!disguised)
            {
                HidingInPlace = false;
                slide = Vector3.zero;
                sentOffset = 0;
                return;
            }

            Roster.TryGetPosition(Roster.Local, out Vector3 body);

            if (InputReader.WasPressedThisFrame(settings.HideKey.Value))
            {
                HidingInPlace = !HidingInPlace;
                hidAt = body;
            }

            if (!HidingInPlace)
            {
                // Walking off puts the disguise back on the body: a shift is for one hiding spot.
                if (slide != Vector3.zero && (body - hidAt).sqrMagnitude > LeaveDistance * LeaveDistance)
                {
                    slide = Vector3.zero;
                }

                SendOffset();
                return;
            }

            Vector3 move = Vector3.zero;
            Vector3 forward = Vector3.forward;
            Camera camera = GameCamera.Get();

            if (camera != null)
            {
                forward = camera.transform.forward;
                forward.y = 0f;
                forward = forward.sqrMagnitude > 0.001f ? forward.normalized : Vector3.forward;
            }

            Vector3 right = Vector3.Cross(Vector3.up, forward);

            if (InputReader.IsHeld(Key.UpArrow))
            {
                move += forward;
            }

            if (InputReader.IsHeld(Key.DownArrow))
            {
                move -= forward;
            }

            if (InputReader.IsHeld(Key.RightArrow))
            {
                move += right;
            }

            if (InputReader.IsHeld(Key.LeftArrow))
            {
                move -= right;
            }

            if (InputReader.IsHeld(settings.RaiseKey.Value))
            {
                move += Vector3.up;
            }

            if (InputReader.IsHeld(settings.LowerKey.Value))
            {
                move -= Vector3.up;
            }

            if (InputReader.WasPressedThisFrame(settings.ResetOffsetKey.Value))
            {
                slide = Vector3.zero;
            }
            else if (move != Vector3.zero)
            {
                Slide(move.normalized * (SlideSpeed * Time.unscaledDeltaTime));
            }

            SendOffset();
        }

        /// <summary>Moves the disguise if the body stays inside it, one axis at a time so it slides along the limit.</summary>
        private void Slide(Vector3 delta)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                if (Mathf.Approximately(delta[axis], 0f))
                {
                    continue;
                }

                Vector3 next = slide;
                next[axis] = Mathf.Clamp(next[axis] + delta[axis], -PropOffset.Limit, PropOffset.Limit);

                if (BodyInside(next))
                {
                    slide = next;
                }
                else
                {
                    warnedAt = Time.unscaledTime;
                }
            }
        }

        /// <summary>Whether our body would still be inside our disguise with it shifted this far.</summary>
        private bool BodyInside(Vector3 offset)
        {
            Collider body = Roster.Local?.PlayerCollider;

            if (body == null || !disguises.TryGetValue(Roster.LocalId, out Disguise disguise) || disguise.Object == null)
            {
                return false;
            }

            Vector3 shift = PropOffset.Snap(offset) - LocalOffset;
            Bounds moved = disguise.World;
            moved.center += shift;
            moved.Expand(BodyMargin * 2f);

            return moved.Contains(body.bounds.center);
        }

        /// <summary>Tells the host about the shift, at most every tenth of a second while a key is held.</summary>
        private void SendOffset()
        {
            int packed = PropOffset.Pack(slide);

            if (packed == sentOffset || Time.unscaledTime < nextOffsetSendAt)
            {
                return;
            }

            Messages.SendOffset(sentOffset, packed);
            sentOffset = packed;
            nextOffsetSendAt = Time.unscaledTime + OffsetSendInterval;
            localOffsetUntil = Time.unscaledTime + PoseHold;
        }

        /// <summary>Where a player's disguise is, for touches and pings against it. False when they have none on.</summary>
        public bool TryGetDisguiseBounds(int ownerId, out Bounds world)
        {
            if (disguises.TryGetValue(ownerId, out Disguise disguise) && disguise.Object != null)
            {
                world = disguise.World;
                return true;
            }

            world = default;
            return false;
        }

        /// <summary>
        /// The prop whose disguise a ping went through: on the line from the pinger to where the
        /// ping landed, the first disguise crossed. A disguise has no collider, so the game's ping
        /// goes straight through it and lands on whatever is behind. -1 for none.
        /// </summary>
        public int PingedThrough(Vector3 from, Vector3 to, int pinger)
        {
            Vector3 along = to - from;
            float length = along.magnitude;

            if (length < 0.01f)
            {
                return -1;
            }

            var ray = new Ray(from, along / length);
            int best = -1;
            float bestDistance = length + 0.5f;

            foreach (KeyValuePair<int, Disguise> pair in disguises)
            {
                if (pair.Key == pinger || pair.Value.Object == null)
                {
                    continue;
                }

                Bounds bounds = pair.Value.World;
                bounds.Expand(0.25f);

                if (bounds.IntersectRay(ray, out float distance) && distance < bestDistance)
                {
                    best = pair.Key;
                    bestDistance = distance;
                }
            }

            return best;
        }

        /// <summary>Longest side of our own disguise, or 0 when we have none on.</summary>
        public float LocalDisguiseSize =>
            disguises.TryGetValue(Roster.LocalId, out Disguise disguise) && disguise.Object != null ? disguise.Size : 0f;

        /// <summary>An object of this level for a dummy to hide as. 0 when there is none.</summary>
        public long RandomProp() => level.RandomObject();

        private static bool CanPick(out string why)
        {
            MatchView view = StateSync.View;

            if (view.Info.Mode != GameMode.PropHunt || !view.InRound || view.Info.Phase == Phase.Results)
            {
                why = "The prop key works during a Prop Hunt round";
                return false;
            }

            if (view.RoleOf(Roster.LocalId) != Role.Prop)
            {
                why = "Only hiders can turn into props";
                return false;
            }

            why = null;
            return true;
        }

        /// <summary>The object of the level under the crosshair: the first thing the ray hits that is not a player.</summary>
        private Aim Look()
        {
            if (!GameCamera.TryGetAim(out Ray ray))
            {
                return new Aim(0, "No camera to aim with");
            }

            float player = CameraDistance(ray);
            float near = Mathf.Max(0f, player - 0.5f);
            float far = PickRange + player;

            RaycastHit? first = null;
            RaycastHit[] hits = Physics.RaycastAll(ray, 300f, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            foreach (RaycastHit hit in hits)
            {
                if (hit.collider != null && !LevelObjects.IsOurs(hit.collider) && hit.distance >= near)
                {
                    first = hit;
                    break;
                }
            }

            long prop = level.FindAt(ray, near, far, first, out Bounds box, out string name);

            if (prop != 0 && level.Resolve(prop) != null)
            {
                return new Aim(prop, name, box);
            }

            if (first.HasValue && first.Value.distance > far)
            {
                return new Aim(0, "Too far away, get closer");
            }

            return first.HasValue ? new Aim(0, "That can't be a prop") : new Aim(0, "Aim at something in the level");
        }

        /// <summary>Third person: the camera sits behind the player, and that stretch does not count against the range.</summary>
        private static float CameraDistance(Ray ray) =>
            Roster.TryGetPosition(Roster.Local, out Vector3 player) ? Vector3.Distance(ray.origin, player) : 0f;

        // ---- disguises ----------------------------------------------------------------------

        /// <summary>Puts each disguise where its player is. Called late in the frame, after they moved.</summary>
        public void LateTick()
        {
            MatchView view = StateSync.View;
            bool active = view.Info.Mode == GameMode.PropHunt && view.InRound && view.Info.Phase != Phase.Results;

            scratch.Clear();

            foreach (KeyValuePair<int, Disguise> pair in disguises)
            {
                PlayerInfo info = view.Get(pair.Key);

                if (!active || info.Role != Role.Prop || info.Prop != pair.Value.Prop)
                {
                    scratch.Add(pair.Key);
                }
            }

            foreach (int id in scratch)
            {
                Remove(id);
            }

            if (!active)
            {
                return;
            }

            foreach (KeyValuePair<int, PlayerInfo> pair in view.Players)
            {
                if (pair.Value.Role != Role.Prop || pair.Value.Prop == 0 ||
                    !TryGetBody(pair.Key, out Vector3 feet, out Func<Renderer[]> renderers))
                {
                    continue;
                }

                if (!disguises.TryGetValue(pair.Key, out Disguise disguise))
                {
                    disguise = Create(pair.Key, pair.Value.Prop);

                    if (disguise == null)
                    {
                        continue;
                    }
                }

                if (disguise.Object == null)
                {
                    continue;
                }

                int pose = pair.Key == Roster.LocalId && Time.unscaledTime < localPoseUntil ? localPose : pair.Value.PropPose;

                if (pose != disguise.Pose)
                {
                    Pose(disguise, pose);
                }

                bool mine = pair.Key == Roster.LocalId && (HidingInPlace || Time.unscaledTime < localOffsetUntil || slide != Vector3.zero);
                Vector3 shift = mine ? LocalOffset : PropOffset.Unpack(pair.Value.PropOffset);
                Vector3 position = feet + disguise.Offset + shift;

                disguise.Object.transform.position = position;
                disguise.World = disguise.Shape;
                disguise.World.center += position;
                HideBody(pair.Key, renderers);
            }
        }

        private Disguise Create(int ownerId, long prop)
        {
            LevelObjects.Piece piece = level.Resolve(prop);

            // Remembered even when it failed, so a prop this level does not have is not looked
            // up again every frame; the player stays visible instead.
            var disguise = new Disguise { Prop = prop };
            disguises[ownerId] = disguise;

            if (piece == null)
            {
                TagPlugin.Logger.LogWarning($"Prop {prop:X6} is not in this level here; player {ownerId} stays visible.");
                return disguise;
            }

            try
            {
                var go = new GameObject($"FlippingIsTag_Prop_{ownerId}");

                foreach (LevelObjects.Part part in piece.Parts)
                {
                    var child = new GameObject("Part");
                    child.transform.SetParent(go.transform, false);
                    child.transform.SetPositionAndRotation(part.Offset, part.Rotation);
                    child.transform.localScale = part.Scale;

                    child.AddComponent<MeshFilter>().sharedMesh = part.Mesh;
                    MeshRenderer drawn = child.AddComponent<MeshRenderer>();
                    drawn.sharedMaterials = part.Materials;
                    disguise.Parts.Add(drawn);
                }

                disguise.Object = go;
                return disguise;
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Could not build the disguise for player {ownerId}: {ex.Message}");
                return disguise;
            }
        }

        /// <summary>
        /// Turns a disguise and measures it again: where its lowest point and middle are once
        /// turned, so it still stands on the ground centred on its player.
        /// </summary>
        private static void Pose(Disguise disguise, int pose)
        {
            Transform t = disguise.Object.transform;
            t.SetPositionAndRotation(Vector3.zero, PropPose.Rotation(pose));

            var bounds = new Bounds();
            bool first = true;

            foreach (MeshRenderer part in disguise.Parts)
            {
                Bounds partBounds = LevelObjects.WorldBounds(part);

                if (first)
                {
                    bounds = partBounds;
                    first = false;
                }
                else
                {
                    bounds.Encapsulate(partBounds);
                }
            }

            disguise.Pose = pose;
            disguise.Shape = bounds;
            disguise.Offset = -new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            disguise.Size = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
        }

        /// <summary>
        /// Where a player's feet are and what draws their body. A test dummy has no PlayerRef,
        /// so it answers for itself.
        /// </summary>
        private static bool TryGetBody(int ownerId, out Vector3 feet, out Func<Renderer[]> renderers)
        {
            if (Dummies.TryGetBody(ownerId, out feet, out Renderer[] dummyRenderers))
            {
                renderers = () => dummyRenderers;
                return true;
            }

            PlayerRef player = Roster.Find(ownerId);
            renderers = null;

            if (player == null || !Roster.TryGetPosition(player, out Vector3 position))
            {
                return false;
            }

            feet = position;

            try
            {
                Collider body = player.PlayerCollider;

                if (body != null)
                {
                    feet.y = body.bounds.min.y;
                }
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogWarning($"Could not read a player's collider: {ex.Message}");
            }

            renderers = () => player.GetComponentsInChildren<Renderer>(true);
            return true;
        }

        /// <summary>
        /// Turns off every renderer on the player's body. Done every frame for the ones we
        /// turned off, because the game turns some of them back on by itself.
        /// </summary>
        private void HideBody(int ownerId, Func<Renderer[]> bodyRenderers)
        {
            // One pass that both hides and notices a respawn, which replaces the body and every
            // renderer we knew about with it: each check on a renderer is a call into the game.
            if (hiddenBodies.TryGetValue(ownerId, out List<Renderer> renderers))
            {
                bool any = false;

                foreach (Renderer renderer in renderers)
                {
                    if (renderer != null)
                    {
                        renderer.enabled = false;
                        any = true;
                    }
                }

                if (any)
                {
                    return;
                }
            }

            renderers = new List<Renderer>();

            foreach (Renderer renderer in bodyRenderers())
            {
                if (renderer.enabled)
                {
                    renderer.enabled = false;
                    renderers.Add(renderer);
                }
            }

            hiddenBodies[ownerId] = renderers;
        }

        private void Remove(int ownerId)
        {
            if (disguises.TryGetValue(ownerId, out Disguise disguise))
            {
                if (disguise.Object != null)
                {
                    Object.Destroy(disguise.Object);
                }

                disguises.Remove(ownerId);
            }

            if (hiddenBodies.TryGetValue(ownerId, out List<Renderer> renderers))
            {
                foreach (Renderer renderer in renderers)
                {
                    if (renderer != null)
                    {
                        renderer.enabled = true;
                    }
                }

                hiddenBodies.Remove(ownerId);
            }
        }
    }
}
