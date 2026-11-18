using System;
using System.Collections.Generic;
using EHS;
using FlippingIsTag.Net;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FlippingIsTag.Local
{
    /// <summary>
    /// Lets players bump into each other, when the host turns it on.
    ///
    /// In the game they pass straight through one another. Rather than change the game's own
    /// physics settings (the layer matrix is global and other things live on those layers),
    /// each machine gives every other player a solid stand-in: a kinematic collider shaped like
    /// their body that follows them every physics step. Our own player is a normal rigidbody,
    /// so it is pushed off a stand-in the way it would be off a moving platform. Each machine
    /// only ever pushes its own player, which is the only one it moves anyway, so nothing here
    /// fights the network.
    ///
    /// The stand-in is a little thinner than the real body, so a ping aimed at a player still
    /// hits the player and not the stand-in in front of them.
    /// </summary>
    internal sealed class Collisions
    {
        private const float Shrink = 0.9f;

        private sealed class StandIn
        {
            public GameObject Object;
            public Rigidbody Body;
            public int SourceId;
        }

        private readonly Dictionary<int, StandIn> standIns = new();
        private readonly List<int> scratch = new();

        private bool warned;

        public void Clear()
        {
            foreach (StandIn standIn in standIns.Values)
            {
                if (standIn.Object != null)
                {
                    Object.Destroy(standIn.Object);
                }
            }

            standIns.Clear();
        }

        public void FixedTick()
        {
            bool enabled = StateSync.Connected && StateSync.View.Info.Collisions;

            if (!enabled)
            {
                if (standIns.Count > 0)
                {
                    Clear();
                }

                return;
            }

            PlayerRef local = Roster.Local;
            Collider ownBody = local?.PlayerCollider;

            if (ownBody == null)
            {
                return;
            }

            int me = Roster.LocalId;
            IReadOnlyList<int> ids = Roster.Ids;

            scratch.Clear();

            foreach (int id in standIns.Keys)
            {
                if (!Roster.Contains(id) || id == me)
                {
                    scratch.Add(id);
                }
            }

            foreach (int id in scratch)
            {
                Remove(id);
            }

            for (int i = 0; i < ids.Count; i++)
            {
                int id = ids[i];

                if (id == me)
                {
                    continue;
                }

                try
                {
                    Follow(id, ownBody);
                }
                catch (Exception ex)
                {
                    TagPlugin.Logger.LogError($"Could not update player {id}'s collider: {ex.Message}");
                    Remove(id);
                }
            }
        }

        private void Follow(int id, Collider ownBody)
        {
            PlayerRef player = Roster.Find(id);
            Collider body = player?.PlayerCollider;

            if (body == null)
            {
                Remove(id);
                return;
            }

            // A respawn gives the player a new body; the stand-in is rebuilt to match it.
            if (standIns.TryGetValue(id, out StandIn standIn) &&
                (standIn.Object == null || standIn.SourceId != body.GetInstanceID()))
            {
                Remove(id);
                standIn = null;
            }

            if (standIn == null)
            {
                standIn = Create(id, body, ownBody);

                if (standIn == null)
                {
                    return;
                }

                standIns[id] = standIn;
            }

            Transform source = body.transform;
            standIn.Object.transform.localScale = source.lossyScale;
            standIn.Body.MovePosition(source.position);
            standIn.Body.MoveRotation(source.rotation);
        }

        private StandIn Create(int id, Collider body, Collider ownBody)
        {
            int layer = SolidLayerFor(ownBody.gameObject.layer, body.gameObject.layer);

            if (layer < 0)
            {
                if (!warned)
                {
                    warned = true;
                    TagPlugin.Logger.LogWarning("No physics layer collides with the player; collisions stay off.");
                }

                return null;
            }

            var go = new GameObject($"FlippingIsTag_Body_{id}") { layer = layer };
            Transform source = body.transform;
            go.transform.SetPositionAndRotation(source.position, source.rotation);
            go.transform.localScale = source.lossyScale;

            Collider copy = CopyShape(go, body);

            if (copy == null)
            {
                Object.Destroy(go);
                return null;
            }

            Rigidbody rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;

            // It stands in for this body; touching the body itself would only be noise.
            Physics.IgnoreCollision(copy, body, true);

            return new StandIn { Object = go, Body = rb, SourceId = body.GetInstanceID() };
        }

        private static Collider CopyShape(GameObject go, Collider body)
        {
            CapsuleCollider capsule = body.TryCast<CapsuleCollider>();

            if (capsule != null)
            {
                CapsuleCollider c = go.AddComponent<CapsuleCollider>();
                c.center = capsule.center;
                c.radius = capsule.radius * Shrink;
                c.height = capsule.height * Shrink;
                c.direction = capsule.direction;
                return c;
            }

            SphereCollider sphere = body.TryCast<SphereCollider>();

            if (sphere != null)
            {
                SphereCollider c = go.AddComponent<SphereCollider>();
                c.center = sphere.center;
                c.radius = sphere.radius * Shrink;
                return c;
            }

            BoxCollider box = body.TryCast<BoxCollider>();

            if (box != null)
            {
                BoxCollider c = go.AddComponent<BoxCollider>();
                c.center = box.center;
                c.size = box.size * Shrink;
                return c;
            }

            TagPlugin.Logger.LogWarning($"Player body is a {body.GetIl2CppType().Name}, which has no stand-in; skipped.");
            return null;
        }

        /// <summary>
        /// The layer the stand-in goes on. Default first: on the players' own layer the game's
        /// trigger zones (checkpoints, the end zone) would take it for a player and look for a
        /// PlayerRef that is not there. Then their layer, then the first one that collides.
        /// </summary>
        private static int SolidLayerFor(int ownLayer, int theirLayer)
        {
            const int Default = 0;

            if (!Physics.GetIgnoreLayerCollision(ownLayer, Default))
            {
                return Default;
            }

            if (!Physics.GetIgnoreLayerCollision(ownLayer, theirLayer))
            {
                return theirLayer;
            }

            for (int layer = 0; layer < 32; layer++)
            {
                if (!string.IsNullOrEmpty(LayerMask.LayerToName(layer)) &&
                    !Physics.GetIgnoreLayerCollision(ownLayer, layer))
                {
                    return layer;
                }
            }

            return -1;
        }

        private void Remove(int id)
        {
            if (standIns.TryGetValue(id, out StandIn standIn) && standIn.Object != null)
            {
                Object.Destroy(standIn.Object);
            }

            standIns.Remove(id);
        }
    }
}
