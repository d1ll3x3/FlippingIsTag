using UnityEngine;

namespace FlippingIsTag
{
    /// <summary>
    /// The drawing primitives the HUD and the menu are built from.
    ///
    /// Only GUI.Label and GUI.DrawTexture: GUI.Box, GUI.Button and GUI.TextField are stripped
    /// in this IL2CPP build and crash the game. A rounded panel is therefore a label whose style
    /// carries a rounded texture as its background, which IMGUI nine-slices for us.
    /// </summary>
    internal static class Gui
    {
        public static readonly Color Backdrop = new(0.05f, 0.05f, 0.08f, 0.9f);
        public static readonly Color Panel = new(0.04f, 0.04f, 0.06f, 0.72f);
        public static readonly Color Accent = new(0.98f, 0.8f, 0.36f);
        public static readonly Color Selection = new(0.98f, 0.8f, 0.36f, 0.18f);
        public static readonly Color Highlight = new(1f, 0.97f, 0.9f);
        public static readonly Color Normal = new(0.8f, 0.8f, 0.84f);
        public static readonly Color Dimmed = new(0.55f, 0.56f, 0.62f);
        public static readonly Color Shadow = new(0f, 0f, 0f, 0.75f);

        private const int CornerRadius = 10;

        private static Texture2D rounded;
        private static GUIStyle roundedStyle;
        private static readonly GUIStyle ShadowCopy = new();

        public static void Fill(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        /// <summary>A filled rectangle with rounded corners.</summary>
        public static void RoundedFill(Rect rect, Color color)
        {
            EnsureRounded();

            Color previous = GUI.color;
            GUI.color = color;
            GUI.Label(rect, GUIContent.none, roundedStyle);
            GUI.color = previous;
        }

        /// <summary>Text with a soft drop shadow, readable over a bright sky or a white wall.</summary>
        public static void ShadowLabel(Rect rect, string text, GUIStyle style, float alpha = 1f)
        {
            Color textColor = style.normal.textColor;

            ShadowCopy.font = style.font;
            ShadowCopy.fontSize = style.fontSize;
            ShadowCopy.fontStyle = style.fontStyle;
            ShadowCopy.alignment = style.alignment;
            ShadowCopy.wordWrap = style.wordWrap;
            ShadowCopy.richText = false;
            ShadowCopy.normal.textColor = new Color(0f, 0f, 0f, Shadow.a * alpha);

            float offset = Mathf.Max(1f, style.fontSize / 14f);
            GUI.Label(new Rect(rect.x + offset, rect.y + offset, rect.width, rect.height), style.richText ? StripTags(text) : text, ShadowCopy);

            style.normal.textColor = new Color(textColor.r, textColor.g, textColor.b, textColor.a * alpha);
            GUI.Label(rect, text, style);
            style.normal.textColor = textColor;
        }

        // This interop only exposes GUIStyle's parameterless constructor, so styles are built
        // from scratch instead of copied from GUI.skin.
        public static GUIStyle Style(int fontSize, FontStyle fontStyle, Color color, TextAnchor anchor, bool rich = false)
        {
            var style = new GUIStyle
            {
                alignment = anchor,
                fontSize = fontSize,
                fontStyle = fontStyle,
                richText = rich,
                wordWrap = false,
            };

            style.font = GUI.skin.label.font;
            style.normal.textColor = color;
            return style;
        }

        /// <summary>Rich text colour tag for a name inside a line.</summary>
        public static string Colored(string text, Color color) =>
            $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{text}</color>";

        /// <summary>
        /// The texture is kept out of the game's asset unloading: a scene load calls
        /// UnloadUnusedAssets, and a texture only we reference would be freed under us.
        /// </summary>
        private static void EnsureRounded()
        {
            if (rounded != null && roundedStyle != null)
            {
                return;
            }

            const int size = CornerRadius * 2 + 2;
            rounded = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Distance from the nearest corner centre, for an antialiased edge.
                    float dx = Mathf.Max(0f, Mathf.Abs(x + 0.5f - size * 0.5f) - (size * 0.5f - CornerRadius));
                    float dy = Mathf.Max(0f, Mathf.Abs(y + 0.5f - size * 0.5f) - (size * 0.5f - CornerRadius));
                    float alpha = Mathf.Clamp01(CornerRadius - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                    rounded.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            rounded.Apply();

            roundedStyle = new GUIStyle();
            roundedStyle.normal.background = rounded;

            RectOffset border = roundedStyle.border;
            border.left = CornerRadius;
            border.right = CornerRadius;
            border.top = CornerRadius;
            border.bottom = CornerRadius;
        }

        private static string StripTags(string text)
        {
            if (text.IndexOf('<') < 0)
            {
                return text;
            }

            var builder = new System.Text.StringBuilder(text.Length);
            bool inTag = false;

            foreach (char c in text)
            {
                if (c == '<')
                {
                    inTag = true;
                }
                else if (c == '>')
                {
                    inTag = false;
                }
                else if (!inTag)
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }
    }
}
