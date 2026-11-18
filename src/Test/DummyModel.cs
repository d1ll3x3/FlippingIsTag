using System;
using System.Collections.Generic;
using EHS;
using FlippingIsTag.Local;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FlippingIsTag.Test
{
    /// <summary>
    /// What a dummy looks like: a copy of the local player's own phone, skin and all.
    ///
    /// Nothing of the player is instantiated - its hierarchy is full of network and gameplay
    /// components that would wake up and act. Only what is drawn is copied: every mesh renderer
    /// as it is, and every skinned one baked into a plain mesh in the pose it has right now,
    /// each with the game's own materials. Those materials are what make it look right: a new
    /// material from a stock shader is what drew the first dummies magenta, because this URP
    /// game does not ship the built-in pipeline's shaders.
    ///
    /// The copy is taken as the phone stands in the world at that moment, not relative to the
    /// player object: the object the phone hangs off does not turn with it, so a copy made
    /// relative to it lay flat on its back with its hands and eyes left beside it. Only the
    /// direction the camera faces is taken out, so a dummy's front is the way the player faced.
    /// </summary>
    internal sealed class DummyModel
    {
        private readonly List<Object> owned = new();

        /// <summary>The copied phone, under the dummy's root, standing on its feet.</summary>
        public Transform Body { get; private set; }

        public Renderer[] Renderers { get; private set; } = Array.Empty<Renderer>();

        /// <summary>Where Body sits when standing still, so the walk can bob around it.</summary>
        public Vector3 BodyRest { get; private set; }

        public float Height { get; private set; } = 1.2f;
        public float Width { get; private set; } = 0.6f;

        /// <summary>Copies the local player's look under a dummy's root. False when there is nothing to copy.</summary>
        public bool Build(Transform root, PlayerRef source)
        {
            var body = new GameObject("Body");
            body.transform.SetParent(root, false);
            Body = body.transform;

            Vector3 origin = Roster.TryGetPosition(source, out Vector3 centre) ? centre : source.transform.position;
            Quaternion toLocal = Quaternion.Inverse(FacingOf(source));

            var renderers = new List<Renderer>();

            foreach (Renderer renderer in source.GetComponentsInChildren<Renderer>(false))
            {
                if (renderer == null || !renderer.enabled)
                {
                    continue;
                }

                try
                {
                    Renderer copy = Copy(renderer, origin, toLocal);

                    if (copy != null)
                    {
                        renderers.Add(copy);
                    }
                }
                catch (Exception ex)
                {
                    TagPlugin.Logger.LogWarning($"Skipped part '{renderer.name}' of the dummy: {ex.Message}");
                }
            }

            if (renderers.Count == 0)
            {
                return false;
            }

            StandOnFeet(root, renderers);
            Renderers = renderers.ToArray();
            return true;
        }

        /// <summary>Destroys the meshes made for this dummy. The game's own are left alone.</summary>
        public void Destroy()
        {
            foreach (Object item in owned)
            {
                if (item != null)
                {
                    Object.Destroy(item);
                }
            }

            owned.Clear();
        }

        /// <summary>The way the player faces, flattened: the camera's heading, which the player follows.</summary>
        private static Quaternion FacingOf(PlayerRef source)
        {
            Camera camera = GameCamera.Get();
            Vector3 forward = camera != null ? camera.transform.forward : source.transform.forward;
            forward.y = 0f;

            return forward.sqrMagnitude > 0.001f ? Quaternion.LookRotation(forward.normalized) : Quaternion.identity;
        }

        private Renderer Copy(Renderer renderer, Vector3 origin, Quaternion toLocal)
        {
            Mesh mesh;
            Vector3 scale;

            SkinnedMeshRenderer skinned = renderer.TryCast<SkinnedMeshRenderer>();

            if (skinned != null)
            {
                // Baked with its scale, so the copy itself is drawn at scale one.
                mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                skinned.BakeMesh(mesh, true);
                owned.Add(mesh);
                scale = Vector3.one;
            }
            else
            {
                MeshRenderer plain = renderer.TryCast<MeshRenderer>();
                MeshFilter filter = plain != null ? plain.GetComponent<MeshFilter>() : null;

                // Particles, trails and lines are effects, not the phone.
                if (filter == null || filter.sharedMesh == null)
                {
                    return null;
                }

                mesh = filter.sharedMesh;
                scale = renderer.transform.lossyScale;
            }

            var part = new GameObject(renderer.name);
            part.transform.SetParent(Body, false);
            part.transform.localPosition = toLocal * (renderer.transform.position - origin);
            part.transform.localRotation = toLocal * renderer.transform.rotation;
            part.transform.localScale = scale;

            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer drawn = part.AddComponent<MeshRenderer>();
            drawn.sharedMaterials = renderer.sharedMaterials;
            drawn.shadowCastingMode = renderer.shadowCastingMode;

            return drawn;
        }

        /// <summary>Moves the copy so its lowest point is at the root and it is centred over it.</summary>
        private void StandOnFeet(Transform root, List<Renderer> renderers)
        {
            Bounds bounds = renderers[0].bounds;

            for (int i = 1; i < renderers.Count; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            Vector3 offset = root.position - new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            Body.position += offset;
            BodyRest = Body.localPosition;

            Height = Mathf.Max(0.3f, bounds.size.y);
            Width = Mathf.Max(0.3f, Mathf.Max(bounds.size.x, bounds.size.z));
        }
    }
}
