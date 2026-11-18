using System;
using System.Collections.Generic;
using EHS;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace FlippingIsTag.Local
{
    /// <summary>
    /// The objects of the level a prop can be, and how every machine names the same one.
    ///
    /// How the levels are built (read from the game's bundles, area A1 of the tutorial):
    ///
    ///   A1_Assets                      LODGroup over the whole area, 75 m
    ///     A1_Assets_LOD0/1/2           one merged mesh each, "..._A1_Assets_LOD0_Combined"
    ///     Fence/WoodenFence_Short (4)  one object, as placed in the level
    ///       SM_WoodenFence_Short       its own LODGroup
    ///         SM_WoodenFence_Short_LOD0/1/2   its meshes, renderers switched OFF
    ///       Collider_Main ...          its colliders, beside the meshes, not under them
    ///
    /// What is drawn is the area's merged mesh, and the objects' own renderers are all off.
    /// The merged LOD0 holds 18 800 triangles and the objects' LOD0 meshes add up to 18 152,
    /// so the merged mesh is the objects' LOD0 meshes put together: an object's LOD0 is exactly
    /// what it looks like in the level. That is what a prop copies. The merged meshes are never
    /// used (picking one turned players into half a level), and the switched-off renderers have
    /// to be included, or nothing inside an area could be picked at all.
    ///
    /// The ground is not a prop: the ground blocks are objects like any other, but nobody
    /// hides as a 25 m slab of floor, so they are left out by name.
    ///
    /// Most small things have no collider at all (chairs, sticky notes, pebbles, the Rubik's
    /// cube), so a ray never hits them. They are found by testing the ray against each mesh's
    /// own box, turned the way the mesh is, which hugs it far closer than a world-aligned box.
    ///
    /// An object is named by a hash of its place in the scene (scene, names, sibling order),
    /// which is the same on every machine that has the level loaded. Two paths on one hash are
    /// settled the same way everywhere: the alphabetically first keeps it.
    /// </summary>
    internal sealed class LevelObjects
    {
        private const int HashMask = 0xFFFFFF;

        /// <summary>How a prop is drawn: every LOD0 mesh of the object, placed as in the level.</summary>
        public sealed class Piece
        {
            public readonly List<Part> Parts = new();
            public string Name;
        }

        public struct Part
        {
            public Mesh Mesh;
            public Material[] Materials;

            /// <summary>From the object's root, in world space.</summary>
            public Vector3 Offset;
            public Quaternion Rotation;
            public Vector3 Scale;
        }

        /// <summary>One mesh's own box, and the way between it and the world.</summary>
        private struct Box
        {
            public Bounds Local;

            /// <summary>Local's corners, read once rather than through Bounds for every look.</summary>
            public Vector3 LocalMin;
            public Vector3 LocalMax;
            public Matrix4x4 WorldToLocal;
            public Matrix4x4 LocalToWorld;

            /// <summary>
            /// The mesh's scale in the world, for its volume. Not the matrix's determinant:
            /// Matrix4x4.determinant throws InvalidProgramException in this game's interop.
            /// </summary>
            public Vector3 Scale;
        }

        private sealed class Candidate
        {
            public Transform Root;
            public int Hash;
            public string Path;
            public Bounds Bounds;

            /// <summary>Bounds' corners, read once, for the ray test that runs over every candidate.</summary>
            public Vector3 Min;
            public Vector3 Max;

            public float Volume;
            public readonly List<Box> Boxes = new();
        }

        /// <summary>Names of things that are part of the floor or are effects, never props.</summary>
        private static readonly string[] NotProps = { "Ground", "Terrain", "Trashpile", "VFX" };

        private readonly Dictionary<int, Candidate> byHash = new();
        private readonly Dictionary<int, Candidate> byRoot = new();

        /// <summary>Objects by the transform above them: the placed object that also holds their colliders.</summary>
        private readonly Dictionary<int, List<Candidate>> byOwner = new();
        private readonly List<Candidate> candidates = new();
        private readonly Dictionary<long, Piece> pieces = new();

        private bool indexed;

        public void Clear()
        {
            byHash.Clear();
            byRoot.Clear();
            byOwner.Clear();
            candidates.Clear();
            pieces.Clear();
            indexed = false;
        }

        /// <summary>
        /// The object under the crosshair.
        ///
        /// First the object the hit collider belongs to: going up from the collider, the first
        /// transform that is an object, or that holds objects beside the collider. Then every
        /// object whose own box the ray goes through before it hits anything: the smallest of
        /// those wins when it is smaller than the one the collider gave, which is how a sticky
        /// note on a TV, or a pebble on the ground, is picked over what it sits on.
        /// </summary>
        /// <param name="ray">From the camera through the crosshair.</param>
        /// <param name="near">How far along the ray the player is; nothing in front of them counts.</param>
        /// <param name="far">How far along the ray a pick may be.</param>
        /// <param name="hit">The first collider the ray hit, if it hit one.</param>
        public long FindAt(Ray ray, float near, float far, RaycastHit? hit, out Bounds bounds, out string name)
        {
            EnsureIndex();

            bounds = default;
            name = null;

            Candidate found = hit.HasValue ? OwnerOf(hit.Value) : null;
            float limit = hit.HasValue ? Mathf.Min(far, hit.Value.distance + 0.2f) : far;

            Candidate crossed = SmallestCrossed(ray, near, limit);

            if (crossed != null && (found == null || crossed.Volume < found.Volume))
            {
                found = crossed;
            }

            if (found == null || found.Root == null)
            {
                return 0;
            }

            bounds = found.Bounds;
            name = Pretty(found.Root.name);
            return found.Hash;
        }

        private Candidate OwnerOf(RaycastHit hit)
        {
            Transform t = hit.collider.transform;

            for (int up = 0; up <= 5 && t != null; up++, t = t.parent)
            {
                int key = t.GetInstanceID();

                if (byRoot.TryGetValue(key, out Candidate self))
                {
                    return self;
                }

                if (byOwner.TryGetValue(key, out List<Candidate> held))
                {
                    // Only what is right at the hit point: higher up, a transform can hold a
                    // whole row of objects, and the hit belongs to none of them.
                    Candidate near = Closest(held, hit.point);

                    if (near != null && near.Bounds.SqrDistance(hit.point) <= 1.5f * 1.5f)
                    {
                        return near;
                    }
                }
            }

            return null;
        }

        /// <summary>The smallest object whose own boxes the ray enters between near and far.</summary>
        private Candidate SmallestCrossed(Ray ray, float near, float far)
        {
            Candidate best = null;

            foreach (Candidate candidate in candidates)
            {
                if (best != null && candidate.Volume >= best.Volume)
                {
                    continue;
                }

                // The world box first: it rules out nearly everything for the price of one test.
                if (!RayHitsBox(ray.origin, ray.direction, candidate.Min, candidate.Max, out float enter) || enter > far)
                {
                    continue;
                }

                if (Crosses(candidate, ray, near, far))
                {
                    best = candidate;
                }
            }

            return best;
        }

        private static bool Crosses(Candidate candidate, Ray ray, float near, float far)
        {
            foreach (Box box in candidate.Boxes)
            {
                Vector3 origin = box.WorldToLocal.MultiplyPoint3x4(ray.origin);
                // Through two points rather than MultiplyVector: only MultiplyPoint3x4 is known
                // to work in this game's interop, and determinant, its neighbour, does not.
                Vector3 direction = box.WorldToLocal.MultiplyPoint3x4(ray.origin + ray.direction) - origin;

                if (direction.sqrMagnitude < 1e-12f)
                {
                    continue;
                }

                direction.Normalize();

                if (!RayHitsBox(origin, direction, box.LocalMin, box.LocalMax, out float localEnter))
                {
                    continue;
                }

                // Back to metres along the world ray: the mesh may be scaled.
                Vector3 entry = box.LocalToWorld.MultiplyPoint3x4(origin + direction * Mathf.Max(0f, localEnter));
                float distance = Vector3.Dot(entry - ray.origin, ray.direction);

                if (distance >= near && distance <= far)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Slab test of a ray against an axis-aligned box, in plain C#. Bounds.IntersectRay does
        /// the same but is a call into the game, and this runs for thousands of objects a look.
        /// Returns the distance along the ray where it enters, 0 when it starts inside.
        /// </summary>
        private static bool RayHitsBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, out float enter)
        {
            // Components rather than the indexer or vector operators: the interop may route those
            // through the game, and fields are always plain reads.
            float near = float.NegativeInfinity;
            float far = float.PositiveInfinity;
            enter = 0f;

            if (!Slab(origin.x, direction.x, min.x, max.x, ref near, ref far) ||
                !Slab(origin.y, direction.y, min.y, max.y, ref near, ref far) ||
                !Slab(origin.z, direction.z, min.z, max.z, ref near, ref far))
            {
                return false;
            }

            enter = near > 0f ? near : 0f;
            return true;
        }

        private static bool Slab(float origin, float direction, float min, float max, ref float near, ref float far)
        {
            if (direction > -1e-8f && direction < 1e-8f)
            {
                return origin >= min && origin <= max;
            }

            float t1 = (min - origin) / direction;
            float t2 = (max - origin) / direction;

            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }

            if (t1 > near)
            {
                near = t1;
            }

            if (t2 < far)
            {
                far = t2;
            }

            return near <= far && far >= 0f;
        }

        /// <summary>The object a name stands for, ready to draw. Null when this level has no such thing.</summary>
        public Piece Resolve(long prop)
        {
            if (pieces.TryGetValue(prop, out Piece cached))
            {
                return cached;
            }

            EnsureIndex();

            Piece piece = null;

            if (byHash.TryGetValue((int)(prop & HashMask), out Candidate candidate) && candidate.Root != null)
            {
                try
                {
                    piece = Build(candidate.Root);
                }
                catch (Exception ex)
                {
                    TagPlugin.Logger.LogError($"Could not build prop {prop:X6}: {ex.Message}");
                }
            }

            pieces[prop] = piece;
            return piece;
        }

        /// <summary>A random object, for a dummy to hide as. Prefers something about the size of a player.</summary>
        public long RandomObject()
        {
            EnsureIndex();

            long chosen = 0;

            for (int attempt = 0; attempt < 24 && candidates.Count > 0; attempt++)
            {
                Candidate candidate = candidates[Random.Range(0, candidates.Count)];
                Vector3 size = candidate.Bounds.size;
                float longest = Mathf.Max(size.x, Mathf.Max(size.y, size.z));

                chosen = candidate.Hash;

                if (longest > 0.4f && longest < 3f)
                {
                    break;
                }
            }

            return chosen;
        }

        public static bool IsOurs(Component component) =>
            component.GetComponentInParent<PlayerRef>() != null ||
            component.transform.root.name.StartsWith("FlippingIsTag_", StringComparison.Ordinal);

        // ---- index --------------------------------------------------------------------------

        /// <summary>
        /// Every object a prop can be, with its bounds. Built once per level: the level does not
        /// move, and looking at 50 000 renderers every time the crosshair moves would be too slow.
        /// </summary>
        private void EnsureIndex()
        {
            if (indexed)
            {
                return;
            }

            indexed = true;

            var byPath = new Dictionary<int, Candidate>();

            foreach (MeshRenderer renderer in Object.FindObjectsOfType<MeshRenderer>())
            {
                try
                {
                    if (!Usable(renderer))
                    {
                        continue;
                    }

                    Transform root = RootOf(renderer.transform);

                    if (IsNotProp(root.name))
                    {
                        continue;
                    }

                    int key = root.GetInstanceID();
                    Bounds bounds = WorldBounds(renderer);
                    Bounds local = MeshOf(renderer).bounds;
                    var box = new Box
                    {
                        Local = local,
                        LocalMin = local.min,
                        LocalMax = local.max,
                        WorldToLocal = renderer.transform.worldToLocalMatrix,
                        LocalToWorld = renderer.transform.localToWorldMatrix,
                        Scale = renderer.transform.lossyScale,
                    };

                    if (byRoot.TryGetValue(key, out Candidate existing))
                    {
                        existing.Bounds.Encapsulate(bounds);
                        existing.Boxes.Add(box);
                        continue;
                    }

                    var candidate = new Candidate { Root = root, Bounds = bounds };
                    candidate.Boxes.Add(box);
                    byRoot[key] = candidate;
                    byPath[key] = candidate;

                    if (root.parent != null)
                    {
                        int owner = root.parent.GetInstanceID();

                        if (!byOwner.TryGetValue(owner, out List<Candidate> held))
                        {
                            held = new List<Candidate>();
                            byOwner[owner] = held;
                        }

                        held.Add(candidate);
                    }
                }
                catch (Exception ex)
                {
                    TagPlugin.Logger.LogWarning($"Skipped an object for props: {ex.Message}");
                }
            }

            foreach (Candidate candidate in byPath.Values)
            {
                // One object going wrong costs that object, never the whole list: an exception
                // here once left the index half built and most of the level unpickable.
                try
                {
                    candidate.Path = PathOf(candidate.Root);
                    candidate.Hash = Hash(candidate.Path);
                    candidate.Volume = VolumeOf(candidate);
                    candidate.Min = candidate.Bounds.min;
                    candidate.Max = candidate.Bounds.max;
                }
                catch (Exception ex)
                {
                    TagPlugin.Logger.LogWarning($"Skipped '{candidate.Root?.name}' for props: {ex.Message}");
                    continue;
                }

                // Same rule on every machine: the alphabetically first path keeps the hash.
                if (byHash.TryGetValue(candidate.Hash, out Candidate other) &&
                    string.CompareOrdinal(other.Path, candidate.Path) <= 0)
                {
                    continue;
                }

                byHash[candidate.Hash] = candidate;
            }

            candidates.AddRange(byHash.Values);
            TagPlugin.Logger.LogInfo($"{candidates.Count} level objects can be props.");
        }

        /// <summary>
        /// A mesh of one object as seen up close: no merged stand-ins, no distant detail levels.
        /// A switched-off renderer counts when it is an object's detail level, because inside an
        /// area every object's renderers are off and the merged mesh draws them instead.
        /// </summary>
        private static bool Usable(MeshRenderer renderer)
        {
            if (renderer == null || IsOurs(renderer))
            {
                return false;
            }

            string name = renderer.name;

            if (name.Contains("Combined") || IsDistantLod(name) || !renderer.enabled && !name.Contains("_LOD"))
            {
                return false;
            }

            Mesh mesh = MeshOf(renderer);
            return mesh != null && !mesh.name.Contains("Combined");
        }

        private static bool IsNotProp(string name)
        {
            foreach (string word in NotProps)
            {
                if (name.Contains(word))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The space the object's own boxes take up, in cubic metres: what "smallest" means.</summary>
        private static float VolumeOf(Candidate candidate)
        {
            float volume = 0f;

            foreach (Box box in candidate.Boxes)
            {
                Vector3 size = box.Local.size;
                float scale = Mathf.Abs(box.Scale.x * box.Scale.y * box.Scale.z);
                volume += Mathf.Max(size.x, 0.01f) * Mathf.Max(size.y, 0.01f) * Mathf.Max(size.z, 0.01f) * scale;
            }

            return volume;
        }

        /// <summary>A detail level other than the closest: "_LOD1", "_LOD2" and so on.</summary>
        private static bool IsDistantLod(string name)
        {
            int at = name.LastIndexOf("_LOD", StringComparison.Ordinal);
            return at >= 0 && at + 4 < name.Length && char.IsDigit(name[at + 4]) && name[at + 4] != '0';
        }

        /// <summary>The object a mesh belongs to: its parent when it is one of the object's detail levels.</summary>
        private static Transform RootOf(Transform transform) =>
            transform.name.Contains("_LOD") && transform.parent != null ? transform.parent : transform;

        /// <summary>
        /// The mesh a renderer draws. A statically batched one draws from a "Combined Mesh" that
        /// holds a whole stretch of the level, so its collider's mesh, which batching leaves
        /// alone, stands in for it.
        /// </summary>
        private static Mesh MeshOf(MeshRenderer renderer)
        {
            Mesh mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;

            if (mesh == null || !mesh.name.StartsWith("Combined Mesh", StringComparison.Ordinal))
            {
                return mesh;
            }

            MeshCollider collider = renderer.GetComponent<MeshCollider>();
            return collider != null ? collider.sharedMesh : null;
        }

        /// <summary>
        /// A renderer's world bounds, worked out from its mesh: a switched-off renderer is not
        /// kept up to date by the engine, and most of the ones here are switched off.
        /// </summary>
        public static Bounds WorldBounds(MeshRenderer renderer)
        {
            Mesh mesh = MeshOf(renderer);

            if (mesh == null)
            {
                return renderer.bounds;
            }

            Bounds local = mesh.bounds;
            Matrix4x4 toWorld = renderer.transform.localToWorldMatrix;
            Vector3 min = local.min;
            Vector3 max = local.max;
            var world = new Bounds(toWorld.MultiplyPoint3x4(min), Vector3.zero);

            for (int i = 1; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z);
                world.Encapsulate(toWorld.MultiplyPoint3x4(corner));
            }

            return world;
        }

        /// <summary>Of the objects under one placed object, the one whose bounds are nearest the point.</summary>
        private static Candidate Closest(List<Candidate> held, Vector3 point)
        {
            Candidate best = null;
            float bestDistance = float.PositiveInfinity;

            foreach (Candidate candidate in held)
            {
                if (candidate.Root == null)
                {
                    continue;
                }

                float distance = candidate.Bounds.SqrDistance(point);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            return best;
        }

        /// <summary>Scene name, then each name down the hierarchy with its place among its siblings.</summary>
        private static string PathOf(Transform transform)
        {
            var parts = new List<string>();

            for (Transform t = transform; t != null; t = t.parent)
            {
                parts.Add($"{t.name}#{t.GetSiblingIndex()}");
            }

            parts.Reverse();
            return transform.gameObject.scene.name + "/" + string.Join("/", parts);
        }

        /// <summary>1..2^24-1, never 0, which means no prop.</summary>
        private static int Hash(string key)
        {
            unchecked
            {
                uint hash = 2166136261;

                foreach (char c in key)
                {
                    hash = (hash ^ c) * 16777619;
                }

                return (int)(hash % HashMask) + 1;
            }
        }

        /// <summary>"SM_FlatStick_Half_01" -> "FlatStick Half".</summary>
        private static string Pretty(string name)
        {
            if (name.StartsWith("SM_", StringComparison.Ordinal))
            {
                name = name.Substring(3);
            }

            int lod = name.IndexOf("_LOD", StringComparison.Ordinal);

            if (lod > 0)
            {
                name = name.Substring(0, lod);
            }

            name = name.Replace('_', ' ').Trim();

            while (name.Length > 3 && char.IsDigit(name[name.Length - 1]))
            {
                name = name.Substring(0, name.Length - 1).TrimEnd();
            }

            return name;
        }

        private static Piece Build(Transform root)
        {
            var piece = new Piece { Name = Pretty(root.name) };

            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(false))
            {
                if (!Usable(renderer) || RootOf(renderer.transform) != root)
                {
                    continue;
                }

                Transform t = renderer.transform;

                piece.Parts.Add(new Part
                {
                    Mesh = MeshOf(renderer),
                    Materials = renderer.sharedMaterials,
                    Offset = t.position - root.position,
                    Rotation = t.rotation,
                    Scale = t.lossyScale,
                });
            }

            return piece.Parts.Count > 0 ? piece : null;
        }
    }
}
