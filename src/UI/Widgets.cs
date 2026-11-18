using System;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FlippingIsTag.UI
{
    /// <summary>
    /// Clickable controls for the menu, drawn with labels and textures only: GUI.Button,
    /// GUI.Toggle and the rest are stripped in this IL2CPP build and crash the game. So the
    /// widgets do their own hit testing against the mouse, read through the Input System like
    /// every other key the mod reads.
    ///
    /// Immediate mode: each control is drawn and handled in the same call. OnGUI runs several
    /// times a frame (layout, repaint), so input is taken once per frame - the first call
    /// handles it, the rest only draw the result. Every control can also be reached with the
    /// keyboard: up and down move a focus through them in the order they are drawn, left and
    /// right change, Enter activates, R resets.
    ///
    /// Everything is laid out on a 1080p canvas and scaled to the screen, so the menu is the
    /// same size on every monitor and the mouse is mapped back through the same scale.
    /// </summary>
    internal sealed class Widgets
    {
        public const float ReferenceHeight = 1080f;

        private static readonly Color Surface = new(1f, 1f, 1f, 0.045f);
        private static readonly Color SurfaceHover = new(1f, 1f, 1f, 0.09f);
        private static readonly Color Track = new(1f, 1f, 1f, 0.12f);
        private static readonly Color Focus = new(0.98f, 0.8f, 0.36f, 0.55f);

        private bool click;
        private bool held;
        private int lastFrame = -1;
        private bool handling;

        private float upRepeat;
        private float downRepeat;
        private float leftRepeat;
        private float rightRepeat;

        private bool keyUp;
        private bool keyDown;
        private bool keyLeft;
        private bool keyRight;
        private bool keyEnter;
        private bool keyReset;

        private int focusCount;
        private int lastFocusCount;
        private int dragging = -1;
        private bool matrixFailed;

        private GUIStyle label;
        private GUIStyle value;
        private GUIStyle button;
        private GUIStyle cardTitle;
        private GUIStyle cardText;
        private GUIStyle header;

        /// <summary>Index of the control with keyboard focus, in drawing order.</summary>
        public int Focused { get; set; }

        /// <summary>The mouse, on the 1080p canvas.</summary>
        public Vector2 Mouse { get; private set; }

        public float Scale { get; private set; } = 1f;

        /// <summary>The screen, measured on the 1080p canvas.</summary>
        public Vector2 Canvas => new(Screen.width / Scale, Screen.height / Scale);

        /// <summary>A key binding is waiting for its key: nothing else takes input until it has one.</summary>
        public ConfigEntry<Key> Capturing { get; private set; }

        /// <summary>Reads the keyboard and the mouse. Called from Update while the menu is open.</summary>
        public void Capture()
        {
            if (Capturing != null)
            {
                Key pressed = InputReader.ReadAnyKey();

                if (pressed != Key.None)
                {
                    if (pressed != Key.Escape)
                    {
                        Capturing.Value = pressed;
                        TagPlugin.Logger.LogInfo($"{Capturing.Definition.Key} bound to {pressed}.");
                    }

                    Capturing = null;
                }

                return;
            }

            keyUp |= InputReader.WasPressedOrRepeating(Key.UpArrow, ref upRepeat);
            keyDown |= InputReader.WasPressedOrRepeating(Key.DownArrow, ref downRepeat);
            keyLeft |= InputReader.WasPressedOrRepeating(Key.LeftArrow, ref leftRepeat);
            keyRight |= InputReader.WasPressedOrRepeating(Key.RightArrow, ref rightRepeat);
            keyEnter |= InputReader.WasPressedThisFrame(Key.Enter) || InputReader.WasPressedThisFrame(Key.Space);
            keyReset |= InputReader.WasPressedThisFrame(Key.R);

            try
            {
                UnityEngine.InputSystem.Mouse mouse = UnityEngine.InputSystem.Mouse.current;

                if (mouse != null)
                {
                    click |= mouse.leftButton.wasPressedThisFrame;
                    held = mouse.leftButton.isPressed;
                }
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Could not read the mouse: {ex.Message}");
            }
        }

        /// <summary>
        /// Starts drawing a frame of the menu on the 1080p canvas, or at native size when this
        /// build cannot scale IMGUI.
        /// </summary>
        public void Begin()
        {
            EnsureStyles();

            Scale = Mathf.Clamp(Screen.height / ReferenceHeight, 0.6f, 3f);

            if (!matrixFailed)
            {
                try
                {
                    GUI.matrix = Matrix4x4.Scale(new Vector3(Scale, Scale, 1f));
                }
                catch (Exception ex)
                {
                    matrixFailed = true;
                    Scale = 1f;
                    TagPlugin.Logger.LogWarning($"Menu scaling is not available, drawing at native size: {ex.Message}");
                }
            }
            else
            {
                Scale = 1f;
            }

            try
            {
                UnityEngine.InputSystem.Mouse mouse = UnityEngine.InputSystem.Mouse.current;

                if (mouse != null)
                {
                    Vector2 position = mouse.position.ReadValue();
                    Mouse = new Vector2(position.x, Screen.height - position.y) / Scale;
                }
            }
            catch (Exception ex)
            {
                TagPlugin.Logger.LogError($"Could not read the mouse position: {ex.Message}");
            }

            handling = Time.frameCount != lastFrame;

            if (handling)
            {
                lastFrame = Time.frameCount;
                lastFocusCount = focusCount;

                if (lastFocusCount > 0)
                {
                    if (keyUp)
                    {
                        Focused = (Focused - 1 + lastFocusCount) % lastFocusCount;
                    }

                    if (keyDown)
                    {
                        Focused = (Focused + 1) % lastFocusCount;
                    }

                    Focused = Mathf.Clamp(Focused, 0, lastFocusCount - 1);
                }

                if (!held)
                {
                    dragging = -1;
                }
            }

            focusCount = 0;
        }

        /// <summary>Ends a frame: what this frame's input did has been applied, so it is spent.</summary>
        public void End()
        {
            if (!matrixFailed)
            {
                GUI.matrix = Matrix4x4.identity;
            }

            if (handling)
            {
                click = false;
                keyUp = keyDown = keyLeft = keyRight = keyEnter = keyReset = false;
            }
        }

        public void ResetFocus() => Focused = 0;

        // ---- controls ------------------------------------------------------------------------

        public bool Button(Rect rect, string text, Color color, bool enabled = true, int fontSize = 15)
        {
            int id = focusCount++;
            bool hover = enabled && rect.Contains(Mouse);
            bool focused = id == Focused;

            DrawFocus(rect, focused);

            Color fill = enabled ? color : new Color(color.r * 0.4f, color.g * 0.4f, color.b * 0.4f, color.a * 0.6f);

            if (hover)
            {
                fill = Color.Lerp(fill, Color.white, 0.18f);
            }

            Gui.RoundedFill(rect, fill);

            button.fontSize = fontSize;
            button.normal.textColor = enabled ? Readable(color) : Gui.Dimmed;
            Gui.ShadowLabel(rect, text, button);

            return enabled && handling && (hover && click || focused && keyEnter);
        }

        public bool Tab(Rect rect, string text, bool selected)
        {
            bool hover = rect.Contains(Mouse);

            Gui.RoundedFill(rect, selected ? Gui.Selection : hover ? SurfaceHover : Color.clear);

            if (selected)
            {
                Gui.RoundedFill(new Rect(rect.x, rect.y + 10f, 4f, rect.height - 20f), Gui.Accent);
            }

            button.fontSize = 15;
            button.normal.textColor = selected ? Gui.Accent : hover ? Gui.Highlight : Gui.Normal;
            Gui.ShadowLabel(rect, text, button);

            return handling && hover && click;
        }

        public bool Card(Rect rect, string title, string text, bool selected)
        {
            int id = focusCount++;
            bool hover = rect.Contains(Mouse);
            bool focused = id == Focused;

            DrawFocus(rect, focused);

            if (selected)
            {
                Gui.RoundedFill(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f), Gui.Accent);
            }

            Gui.RoundedFill(rect, selected ? new Color(0.2f, 0.17f, 0.08f, 0.98f) : hover ? new Color(0.16f, 0.16f, 0.2f, 0.98f) : new Color(0.11f, 0.11f, 0.14f, 0.98f));

            cardTitle.normal.textColor = selected ? Gui.Accent : Gui.Highlight;
            Gui.ShadowLabel(new Rect(rect.x + 14f, rect.y + 10f, rect.width - 28f, 24f), title, cardTitle);
            GUI.Label(new Rect(rect.x + 14f, rect.y + 36f, rect.width - 28f, rect.height - 42f), text, cardText);

            return handling && (hover && click || focused && keyEnter);
        }

        public void Header(Rect rect, string text) => Gui.ShadowLabel(rect, text, header);

        public void Toggle(Rect rect, string text, ConfigEntry<bool> entry, bool enabled = true)
        {
            int id = focusCount++;
            bool hover = enabled && rect.Contains(Mouse);
            bool focused = id == Focused;

            Row(rect, text, hover, focused, enabled);

            var track = new Rect(rect.xMax - 62f, rect.y + (rect.height - 26f) * 0.5f, 50f, 26f);
            Color on = new(0.36f, 0.78f, 0.46f);
            Gui.RoundedFill(track, entry.Value ? (enabled ? on : on * 0.5f) : Track);

            float knobX = entry.Value ? track.xMax - 24f : track.x + 2f;
            Gui.RoundedFill(new Rect(knobX, track.y + 2f, 22f, 22f), enabled ? Color.white : Gui.Dimmed);

            if (!enabled || !handling)
            {
                return;
            }

            if (hover && click || focused && (keyEnter || keyLeft || keyRight))
            {
                entry.Value = !entry.Value;
            }

            if (focused && keyReset)
            {
                entry.Value = (bool)entry.DefaultValue;
            }
        }

        public void Slider(Rect rect, string text, ConfigEntry<float> entry, float min, float max, float step,
            string format, bool enabled = true)
        {
            int id = focusCount++;
            bool hover = enabled && rect.Contains(Mouse);
            bool focused = id == Focused;

            Row(rect, text, hover, focused, enabled, top: true);

            value.normal.textColor = enabled ? Gui.Highlight : Gui.Dimmed;
            Gui.ShadowLabel(new Rect(rect.x, rect.y + 4f, rect.width - 14f, 22f), entry.Value.ToString(format), value);

            var track = new Rect(rect.x + 14f, rect.yMax - 14f, rect.width - 28f, 6f);
            float t = Mathf.InverseLerp(min, max, entry.Value);

            Gui.RoundedFill(track, Track);
            Gui.RoundedFill(new Rect(track.x, track.y, Mathf.Max(6f, track.width * t), track.height), enabled ? Gui.Accent : Gui.Dimmed);
            Gui.RoundedFill(new Rect(track.x + track.width * t - 8f, track.y - 5f, 16f, 16f), enabled ? Color.white : Gui.Dimmed);

            if (!enabled || !handling)
            {
                return;
            }

            if (hover && click)
            {
                dragging = id;
            }

            if (dragging == id && held)
            {
                float raw = Mathf.Lerp(min, max, Mathf.InverseLerp(track.x, track.xMax, Mouse.x));
                Set(entry, raw, step, min, max);
            }

            if (focused && keyLeft)
            {
                Set(entry, entry.Value - step, step, min, max);
            }

            if (focused && keyRight)
            {
                Set(entry, entry.Value + step, step, min, max);
            }

            if (focused && keyReset)
            {
                entry.Value = (float)entry.DefaultValue;
            }
        }

        /// <summary>A value changed with arrows either side of it: whole numbers, or a list of choices.</summary>
        public void Stepper(Rect rect, string text, string shown, Action<int> adjust, Action reset, bool enabled = true)
        {
            int id = focusCount++;
            bool hover = enabled && rect.Contains(Mouse);
            bool focused = id == Focused;

            Row(rect, text, hover, focused, enabled);

            var right = new Rect(rect.xMax - 40f, rect.y + (rect.height - 30f) * 0.5f, 30f, 30f);
            var left = new Rect(right.x - 132f, right.y, 30f, 30f);
            var middle = new Rect(left.xMax, right.y, right.x - left.xMax, 30f);

            bool hoverLeft = enabled && left.Contains(Mouse);
            bool hoverRight = enabled && right.Contains(Mouse);

            Gui.RoundedFill(left, hoverLeft ? SurfaceHover * 2f : Track);
            Gui.RoundedFill(right, hoverRight ? SurfaceHover * 2f : Track);

            button.fontSize = 18;
            button.normal.textColor = enabled ? Gui.Highlight : Gui.Dimmed;
            Gui.ShadowLabel(left, "‹", button);
            Gui.ShadowLabel(right, "›", button);

            value.normal.textColor = enabled ? Gui.Highlight : Gui.Dimmed;
            value.alignment = TextAnchor.MiddleCenter;
            Gui.ShadowLabel(middle, shown, value);
            value.alignment = TextAnchor.MiddleRight;

            if (!enabled || !handling)
            {
                return;
            }

            if (hoverLeft && click || focused && keyLeft)
            {
                adjust(-1);
            }

            if (hoverRight && click || focused && (keyRight || keyEnter))
            {
                adjust(1);
            }

            if (focused && keyReset)
            {
                reset();
            }
        }

        public void IntStepper(Rect rect, string text, ConfigEntry<int> entry, int min, int max, string zero = null,
            bool enabled = true) =>
            Stepper(rect, text, entry.Value == 0 && zero != null ? zero : entry.Value.ToString(),
                d => entry.Value = Mathf.Clamp(entry.Value + d, min, max),
                () => entry.Value = (int)entry.DefaultValue, enabled);

        public void KeyBind(Rect rect, string text, ConfigEntry<Key> entry)
        {
            int id = focusCount++;
            bool hover = rect.Contains(Mouse);
            bool focused = id == Focused;
            bool capturing = Capturing == entry;

            Row(rect, text, hover, focused, true);

            var key = new Rect(rect.xMax - 172f, rect.y + (rect.height - 30f) * 0.5f, 160f, 30f);
            Gui.RoundedFill(key, capturing ? Gui.Selection : key.Contains(Mouse) ? SurfaceHover * 2f : Track);

            button.fontSize = 14;
            button.normal.textColor = capturing ? Gui.Accent : Gui.Highlight;
            Gui.ShadowLabel(key, capturing ? "press a key…" : entry.Value.ToString(), button);

            if (!handling || Capturing != null)
            {
                return;
            }

            if (hover && click || focused && keyEnter)
            {
                Capturing = entry;
            }

            if (focused && keyReset)
            {
                entry.Value = (Key)entry.DefaultValue;
            }
        }

        public void CancelCapture() => Capturing = null;

        // ---- pieces ---------------------------------------------------------------------------

        private void Row(Rect rect, string text, bool hover, bool focused, bool enabled, bool top = false)
        {
            DrawFocus(rect, focused);
            Gui.RoundedFill(rect, hover ? SurfaceHover : Surface);

            label.normal.textColor = enabled ? Gui.Highlight : Gui.Dimmed;
            Rect at = top ? new Rect(rect.x + 14f, rect.y + 4f, rect.width - 28f, 22f) : new Rect(rect.x + 14f, rect.y, rect.width - 28f, rect.height);
            Gui.ShadowLabel(at, text, label);
        }

        private static void DrawFocus(Rect rect, bool focused)
        {
            if (focused)
            {
                Gui.RoundedFill(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f), Focus);
            }
        }

        private static void Set(ConfigEntry<float> entry, float raw, float step, float min, float max)
        {
            // Rounded to the step so a dragged value does not end up as 0.11999999 in the file.
            float snapped = Mathf.Round(raw / step) * step;
            entry.Value = Mathf.Clamp(snapped, min, max);
        }

        /// <summary>Dark text on a light button, light text on a dark one.</summary>
        private static Color Readable(Color background) =>
            background.r * 0.3f + background.g * 0.59f + background.b * 0.11f > 0.6f && background.a > 0.5f
                ? new Color(0.08f, 0.08f, 0.1f)
                : Gui.Highlight;

        private void EnsureStyles()
        {
            if (label != null)
            {
                return;
            }

            label = Gui.Style(15, FontStyle.Normal, Gui.Highlight, TextAnchor.MiddleLeft);
            value = Gui.Style(15, FontStyle.Bold, Gui.Highlight, TextAnchor.MiddleRight);
            button = Gui.Style(15, FontStyle.Bold, Gui.Highlight, TextAnchor.MiddleCenter);
            cardTitle = Gui.Style(17, FontStyle.Bold, Gui.Highlight, TextAnchor.MiddleLeft);
            cardText = Gui.Style(13, FontStyle.Normal, Gui.Normal, TextAnchor.UpperLeft);
            cardText.wordWrap = true;
            header = Gui.Style(12, FontStyle.Bold, Gui.Dimmed, TextAnchor.LowerLeft);
        }
    }
}
