using UnityEngine;

namespace FlippingIsTag.UI
{
    /// <summary>
    /// Lines in the world drawn on top of the screen: the outline of a player's real body.
    ///
    /// IMGUI has no line primitive, and turning a texture with the GUI matrix is not something
    /// this build is known to allow, so a line is a row of small squares close enough together
    /// to read as one.
    /// </summary>
    internal static class Outline3D
    {
        // Each dot is one draw call, so a whole body outline has to stay in the hundreds: dots a
        // few pixels apart, circles of 16 sides, and no line longer than MaxDots.
        private const float DotSpacing = 5f;
        private const float DotSize = 2f;
        private const int CircleSegments = 16;
        private const int MaxDots = 120;

        /// <summary>The outline of a collider as it sits in the world: capsule, sphere or box, or its bounds.</summary>
        public static void Collider(Camera camera, Collider collider, Color color)
        {
            CapsuleCollider capsule = collider.TryCast<CapsuleCollider>();

            if (capsule != null)
            {
                Capsule(camera, capsule, color);
                return;
            }

            SphereCollider sphere = collider.TryCast<SphereCollider>();

            if (sphere != null)
            {
                Transform t = sphere.transform;
                Vector3 centre = t.TransformPoint(sphere.center);
                float radius = sphere.radius * MaxAbs(t.lossyScale);
                Circle(camera, centre, Vector3.up, radius, color);
                Circle(camera, centre, t.right, radius, color);
                Circle(camera, centre, t.forward, radius, color);
                return;
            }

            BoxCollider box = collider.TryCast<BoxCollider>();

            if (box != null)
            {
                Box(camera, box.transform.localToWorldMatrix, new Bounds(box.center, box.size), color);
                return;
            }

            Box(camera, Matrix4x4.identity, collider.bounds, color);
        }

        private static void Capsule(Camera camera, CapsuleCollider capsule, Color color)
        {
            Transform t = capsule.transform;
            Vector3 scale = t.lossyScale;

            // The capsule's own axis: 0 = x, 1 = y, 2 = z.
            Vector3 axis = capsule.direction == 0 ? t.right : capsule.direction == 2 ? t.forward : t.up;
            float axisScale = Mathf.Abs(capsule.direction == 0 ? scale.x : capsule.direction == 2 ? scale.z : scale.y);
            float radiusScale = capsule.direction == 0 ? Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z))
                : capsule.direction == 2 ? Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y))
                : Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));

            float radius = capsule.radius * radiusScale;
            float half = Mathf.Max(0f, capsule.height * axisScale * 0.5f - radius);
            Vector3 centre = t.TransformPoint(capsule.center);
            Vector3 top = centre + axis * half;
            Vector3 bottom = centre - axis * half;

            // Two side vectors square to the axis, for the rings and the long edges.
            Vector3 side = Vector3.Cross(axis, Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up).normalized;
            Vector3 other = Vector3.Cross(axis, side).normalized;

            Circle(camera, top, axis, radius, color);
            Circle(camera, bottom, axis, radius, color);
            Circle(camera, centre, axis, radius, color);

            foreach (Vector3 offset in new[] { side, -side, other, -other })
            {
                Line(camera, top + offset * radius, bottom + offset * radius, color);
            }

            // The rounded ends, as two half circles each.
            Arc(camera, top, side, axis, radius, color);
            Arc(camera, top, other, axis, radius, color);
            Arc(camera, bottom, side, -axis, radius, color);
            Arc(camera, bottom, other, -axis, radius, color);
        }

        private static void Box(Camera camera, Matrix4x4 toWorld, Bounds local, Color color)
        {
            Vector3 min = local.min;
            Vector3 max = local.max;
            var corners = new Vector3[8];

            for (int i = 0; i < 8; i++)
            {
                corners[i] = toWorld.MultiplyPoint3x4(new Vector3(
                    (i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z));
            }

            for (int i = 0; i < 8; i++)
            {
                for (int bit = 1; bit < 8; bit <<= 1)
                {
                    // Each edge once: from the corner without the bit to the one with it.
                    if ((i & bit) == 0)
                    {
                        Line(camera, corners[i], corners[i | bit], color);
                    }
                }
            }
        }

        private static void Circle(Camera camera, Vector3 centre, Vector3 normal, float radius, Color color)
        {
            Vector3 a = Vector3.Cross(normal, Mathf.Abs(Vector3.Dot(normal.normalized, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up).normalized;
            Vector3 b = Vector3.Cross(normal.normalized, a);
            Vector3 previous = centre + a * radius;

            for (int i = 1; i <= CircleSegments; i++)
            {
                float angle = i * Mathf.PI * 2f / CircleSegments;
                Vector3 next = centre + (a * Mathf.Cos(angle) + b * Mathf.Sin(angle)) * radius;
                Line(camera, previous, next, color);
                previous = next;
            }
        }

        /// <summary>Half a circle from one side to the other, bulging out along the axis.</summary>
        private static void Arc(Camera camera, Vector3 centre, Vector3 side, Vector3 outward, float radius, Color color)
        {
            Vector3 previous = centre + side * radius;

            for (int i = 1; i <= CircleSegments / 2; i++)
            {
                float angle = i * Mathf.PI / (CircleSegments / 2);
                Vector3 next = centre + (side * Mathf.Cos(angle) + outward.normalized * Mathf.Sin(angle)) * radius;
                Line(camera, previous, next, color);
                previous = next;
            }
        }

        /// <summary>A world line on screen. Lines that pass behind the camera are left out.</summary>
        public static void Line(Camera camera, Vector3 from, Vector3 to, Color color)
        {
            Vector3 a = camera.WorldToScreenPoint(from);
            Vector3 b = camera.WorldToScreenPoint(to);

            if (a.z <= 0.05f || b.z <= 0.05f)
            {
                return;
            }

            var start = new Vector2(a.x, Screen.height - a.y);
            var end = new Vector2(b.x, Screen.height - b.y);
            float length = Vector2.Distance(start, end);
            int dots = Mathf.Clamp(Mathf.CeilToInt(length / DotSpacing), 1, MaxDots);

            for (int i = 0; i <= dots; i++)
            {
                Vector2 p = Vector2.Lerp(start, end, (float)i / dots);
                Gui.Fill(new Rect(p.x - DotSize * 0.5f, p.y - DotSize * 0.5f, DotSize, DotSize), color);
            }
        }

        private static float MaxAbs(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));
    }
}
