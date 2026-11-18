using System;
using FlippingIsTag.Local;
using FlippingIsTag.Match;
using FlippingIsTag.Net;
using FlippingIsTag.Test;
using FlippingIsTag.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FlippingIsTag.Menu
{
    /// <summary>
    /// The in-game menu: a window with four pages, driven by the mouse or the keyboard.
    ///
    ///   PLAY      start or end a round, pick the game, the main round settings
    ///   RULES     how tagging works, team tag, hide and seek
    ///   TEST      dummies to try a mode alone
    ///   OPTIONS   team wishes, what the HUD shows, key binds
    ///
    /// Every control edits a BepInEx ConfigEntry directly, so a value changed mid-round is
    /// already the saved value. The rules are the host's: on a client they are shown greyed
    /// out, because the host's are the ones in force.
    /// </summary>
    internal sealed class TagMenu
    {
        private const float WindowWidth = 900f;
        private const float WindowHeight = 720f;
        private const float SidebarWidth = 190f;
        private const float RowHeight = 44f;
        private const float RowGap = 8f;

        private enum Page
        {
            Play,
            Rules,
            Test,
            Options,
        }

        private static readonly string[] PageNames = { "PLAY", "RULES", "TEST", "OPTIONS" };

        private static readonly Color StartColor = new(0.98f, 0.8f, 0.36f, 1f);
        private static readonly Color StopColor = new(0.85f, 0.28f, 0.26f, 1f);
        private static readonly Color PlainColor = new(1f, 1f, 1f, 0.1f);
        private static readonly Color RedColor = new(0.8f, 0.24f, 0.24f, 1f);
        private static readonly Color BlueColor = new(0.24f, 0.42f, 0.85f, 1f);

        private readonly TagSettings settings;
        private readonly Action toggleRound;
        private readonly Action<string> notify;
        private readonly Widgets ui = new();

        private Page page;
        private float tabRepeat;

        private CursorLockMode savedLockState = CursorLockMode.Locked;
        private bool savedCursorVisible;

        private GUIStyle titleStyle;
        private GUIStyle subtitleStyle;
        private GUIStyle textStyle;
        private GUIStyle hintStyle;

        public TagMenu(TagSettings settings, Action toggleRound, Action<string> notify)
        {
            this.settings = settings;
            this.toggleRound = toggleRound;
            this.notify = notify;
        }

        public bool IsOpen { get; private set; }

        /// <summary>Whether the menu is waiting for a key to bind, so the menu key itself can be bound.</summary>
        public bool Capturing => ui.Capturing != null;

        public void Toggle()
        {
            IsOpen = !IsOpen;

            if (IsOpen)
            {
                // Remembered rather than assumed, so closing the menu from a screen that
                // already had a free cursor does not lock it away.
                savedLockState = Cursor.lockState;
                savedCursorVisible = Cursor.visible;
                return;
            }

            ui.CancelCapture();
            settings.Validate();
            settings.Save();
            InputBlock.SetMenu(false);
            Cursor.lockState = savedLockState;
            Cursor.visible = savedCursorVisible;
        }

        public void Close()
        {
            if (IsOpen)
            {
                Toggle();
            }
        }

        public void Tick()
        {
            if (!IsOpen)
            {
                return;
            }

            // The game grabs the cursor back every frame, so it has to be re-released.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            InputBlock.SetMenu(true);

            if (!Capturing && InputReader.WasPressedOrRepeating(Key.Tab, ref tabRepeat))
            {
                int step = InputReader.ShiftHeld ? -1 : 1;
                var next = page;

                do
                {
                    next = (Page)(((int)next + step + PageNames.Length) % PageNames.Length);
                }
                while (!Shown(next));

                Open(next);
            }

            ui.Capture();
        }

        public void Draw()
        {
            if (!IsOpen)
            {
                return;
            }

            EnsureStyles();
            ui.Begin();

            try
            {
                Vector2 canvas = ui.Canvas;
                var window = new Rect((canvas.x - WindowWidth) * 0.5f, (canvas.y - WindowHeight) * 0.5f, WindowWidth, WindowHeight);

                Gui.RoundedFill(new Rect(window.x - 1f, window.y - 1f, window.width + 2f, window.height + 2f), new Color(1f, 1f, 1f, 0.08f));
                Gui.RoundedFill(window, new Color(0.06f, 0.06f, 0.085f, 0.97f));

                // Test mode was turned off while its page was open.
                if (!Shown(page))
                {
                    page = Page.Options;
                }

                DrawHeader(window);
                DrawSidebar(window);

                var content = new Rect(window.x + SidebarWidth + 24f, window.y + 84f,
                    window.width - SidebarWidth - 48f, window.height - 124f);

                switch (page)
                {
                    case Page.Play:
                        DrawPlay(content);
                        break;
                    case Page.Rules:
                        DrawRules(content);
                        break;
                    case Page.Test:
                        DrawTest(content);
                        break;
                    default:
                        DrawOptions(content);
                        break;
                }

                GUI.Label(new Rect(window.x + 24f, window.yMax - 34f, window.width - 48f, 24f),
                    $"mouse or arrows   ·   Enter select   ·   R default   ·   Tab next page   ·   {settings.MenuKey.Value} close",
                    hintStyle);
            }
            finally
            {
                ui.End();
            }
        }

        private void Open(Page next)
        {
            page = next;
            ui.ResetFocus();
            ui.CancelCapture();
        }

        // ---- frame --------------------------------------------------------------------------

        private void DrawHeader(Rect window)
        {
            Gui.ShadowLabel(new Rect(window.x + 28f, window.y + 18f, 400f, 32f), "FLIPPING IS TAG", titleStyle);

            string role = NetRole.IsHost
                ? "You're the host: your rules are the ones played"
                : StateSync.Connected ? "You're a client: the host sets the rules" : "The host isn't running the mod";

            GUI.Label(new Rect(window.x + 30f, window.y + 50f, 600f, 20f), $"v{TagPlugin.Version}   ·   {role}", subtitleStyle);

            if (ui.Button(new Rect(window.xMax - 56f, window.y + 18f, 36f, 36f), "×", PlainColor, true, 20))
            {
                Close();
            }
        }

        /// <summary>The TEST page only shows in test mode.</summary>
        private bool Shown(Page which) => which != Page.Test || settings.TestMode.Value;

        private void DrawSidebar(Rect window)
        {
            float y = window.y + 84f;

            for (int i = 0; i < PageNames.Length; i++)
            {
                if (!Shown((Page)i))
                {
                    continue;
                }

                if (ui.Tab(new Rect(window.x + 16f, y, SidebarWidth - 16f, 46f), PageNames[i], (int)page == i))
                {
                    Open((Page)i);
                }

                y += 52f;
            }

            // What the lobby looks like right now, at a glance.
            MatchView view = StateSync.View;
            var box = new Rect(window.x + 16f, window.yMax - 170f, SidebarWidth - 16f, 120f);
            Gui.RoundedFill(box, new Color(1f, 1f, 1f, 0.04f));

            string state = !view.InRound ? "No round" : view.Info.Phase == Phase.Results ? "Results" : "Round in progress";
            int players = Roster.Ids.Count - Dummies.Count;

            GUI.Label(new Rect(box.x + 12f, box.y + 10f, box.width - 24f, 100f),
                $"{state}\n{Rules.For(view.InRound ? view.Info.Mode : settings.Mode.Value).Name}\n" +
                $"{players} player{(players == 1 ? "" : "s")}" + (Dummies.Count > 0 ? $"\n{Dummies.Count} dummies" : string.Empty),
                textStyle);
        }

        // ---- pages --------------------------------------------------------------------------

        private void DrawPlay(Rect area)
        {
            bool host = NetRole.IsHost;
            MatchView view = StateSync.View;
            bool running = view.InRound && view.Info.Phase != Phase.Results;

            string label = !host ? "ONLY THE HOST CAN START ROUNDS"
                : running ? "END THE ROUND"
                : $"START A ROUND   ·   {Rules.For(settings.Mode.Value).Name.ToUpperInvariant()}";

            if (ui.Button(new Rect(area.x, area.y, area.width, 54f), label, running ? StopColor : StartColor, host, 18))
            {
                toggleRound();
            }

            float y = area.y + 74f;
            ui.Header(new Rect(area.x, y, area.width, 20f), "GAME MODE");
            y += 26f;

            const float gap = 12f;
            float cardWidth = (area.width - gap * 2f) / 3f;
            const float cardHeight = 92f;

            foreach (GameMode mode in (GameMode[])Enum.GetValues(typeof(GameMode)))
            {
                int index = (int)mode;
                var rect = new Rect(area.x + index % 3 * (cardWidth + gap), y + index / 3 * (cardHeight + gap), cardWidth, cardHeight);
                Rules rules = Rules.For(mode);

                if (ui.Card(rect, rules.Name, rules.Description, settings.Mode.Value == mode) && host)
                {
                    settings.Mode.Value = mode;
                }
            }

            y += cardHeight * 2f + gap + 20f;
            ui.Header(new Rect(area.x, y, area.width, 20f), "ROUND");
            y += 26f;

            float column = (area.width - gap) * 0.5f;
            float right = area.x + column + gap;
            const float tall = RowHeight + 6f;

            ui.Slider(new Rect(area.x, y, column, tall), "Round length (min)", settings.RoundMinutes, 0.5f, 20f, 0.5f, "0.#", host);
            ui.IntStepper(new Rect(right, y, column, tall), "Chasers at the start", settings.Chasers, 0, 16, "auto", host);
            y += tall + RowGap;

            ui.Slider(new Rect(area.x, y, column, tall), "Hiding time (s)", settings.HideSeconds, 5f, 180f, 5f, "0", host);
            ui.Toggle(new Rect(right, y, column, tall), "Gather everyone at the start", settings.GatherAtStart, host);
            y += tall + RowGap;

            ui.Slider(new Rect(area.x, y, column, tall), "Head start (s)", settings.StartDelay, 0f, 30f, 1f, "0", host);
            ui.Toggle(new Rect(right, y, column, tall), "Player collisions", settings.Collisions, host);
        }

        private void DrawRules(Rect area)
        {
            bool host = NetRole.IsHost;
            const float gap = 16f;
            const float tall = RowHeight + 6f;
            float column = (area.width - gap) * 0.5f;
            float right = area.x + column + gap;

            float y = area.y;
            ui.Header(new Rect(area.x, y, column, 20f), "TAGGING");
            y += 26f;

            ui.Toggle(new Rect(area.x, y, column, RowHeight), "Tag by touch", settings.ContactTag, host);
            y += RowHeight + RowGap;
            ui.Slider(new Rect(area.x, y, column, tall), "Touch distance (m)", settings.TagRadius, 0.5f, 6f, 0.1f, "0.0", host);
            y += tall + RowGap;
            ui.Toggle(new Rect(area.x, y, column, RowHeight), "Tag by ping", settings.PingTag, host);
            y += RowHeight + RowGap;
            ui.Slider(new Rect(area.x, y, column, tall), "Ping range (m)", settings.PingRange, 2f, 100f, 1f, "0", host);
            y += tall + RowGap;
            ui.Slider(new Rect(area.x, y, column, tall), "Protection after a tag (s)", settings.TagBackSeconds, 0f, 15f, 0.5f, "0.#", host);

            y = area.y;
            ui.Header(new Rect(right, y, column, 20f), "TEAM TAG");
            y += 26f;

            ui.Slider(new Rect(right, y, column, tall), "Stun time (s)", settings.StunSeconds, 0.5f, 15f, 0.5f, "0.#", host);
            y += tall + RowGap;
            ui.IntStepper(new Rect(right, y, column, RowHeight), "Points to win", settings.TeamScoreLimit, 0, 200, "no limit", host);
            y += RowHeight + RowGap + 14f;

            ui.Header(new Rect(right, y, column, 20f), "HIDE AND SEEK  ·  PROP HUNT");
            y += 26f;

            ui.Toggle(new Rect(right, y, column, RowHeight), "Found players become seekers", settings.InfectOnFind, host);
            y += RowHeight + RowGap;
            ui.Slider(new Rect(right, y, column, tall), "Auto taunt after (s, 0 = never)", settings.TauntInterval, 0f, 300f, 5f, "0", host);
            y += tall + RowGap;
            ui.Toggle(new Rect(right, y, column, RowHeight), "Hide names from seekers", settings.HideNames, host);
            y += RowHeight + RowGap;
            ui.IntStepper(new Rect(right, y, column, RowHeight), "Ping misses before a stun", settings.PropMisses, 0, 20, "never", host);
        }

        private void DrawTest(Rect area)
        {
            bool host = NetRole.IsHost;

            GUI.Label(new Rect(area.x, area.y, area.width, 60f),
                "Dummies are test players that look like your phone. They chase, run, free their teammates, hide and " +
                "pick props like players would, and you can tag them by touch or by ping. Only you see them.",
                textStyle);

            float y = area.y + 70f;
            float half = (area.width - 12f) * 0.5f;

            if (ui.Button(new Rect(area.x, y, half, 52f), $"+  SPAWN A DUMMY   ({Dummies.Count}/{Dummies.MaxCount})", StartColor,
                    host && Dummies.Count < Dummies.MaxCount, 16))
            {
                notify(Dummies.Spawn());
            }

            if (ui.Button(new Rect(area.x + half + 12f, y, half, 52f), "REMOVE ALL DUMMIES", PlainColor, host && Dummies.Count > 0, 16))
            {
                Dummies.RemoveAll();
                notify("Dummies removed");
            }

            y += 76f;
            ui.Header(new Rect(area.x, y, area.width, 20f), "WHAT THEY DO");
            y += 26f;

            float third = (area.width - 24f) / 3f;
            string[] names = { "STAND STILL", "WANDER", "PLAY THE ROUND" };

            for (int i = 0; i < names.Length; i++)
            {
                bool selected = (int)settings.DummyBehavior.Value == i;

                if (ui.Button(new Rect(area.x + i * (third + 12f), y, third, 46f), names[i],
                        selected ? StartColor : PlainColor, host, 14))
                {
                    settings.DummyBehavior.Value = (DummyBehavior)i;
                }
            }

            y += 64f;
            ui.Slider(new Rect(area.x, y, area.width, RowHeight + 6f), "Dummy speed (m/s)", settings.DummySpeed, 0.5f, 20f, 0.5f, "0.#", host);

            if (!host)
            {
                GUI.Label(new Rect(area.x, y + 70f, area.width, 24f), "Only the host can use dummies: they live in the host's match.", textStyle);
            }
        }

        private void DrawOptions(Rect area)
        {
            const float gap = 16f;
            float column = (area.width - gap) * 0.5f;
            float right = area.x + column + gap;

            float y = area.y;
            ui.Header(new Rect(area.x, y, column, 20f), "TEAM TAG: NEXT ROUND I WANT TO BE ON");
            y += 26f;

            float half = (column - 10f) * 0.5f;

            if (ui.Button(new Rect(area.x, y, half, 44f), "RED", RedColor))
            {
                Messages.SendTeam(0);
                notify("Asked to be on RED next round");
            }

            if (ui.Button(new Rect(area.x + half + 10f, y, half, 44f), "BLUE", BlueColor))
            {
                Messages.SendTeam(1);
                notify("Asked to be on BLUE next round");
            }

            y += 64f;
            ui.Header(new Rect(area.x, y, column, 20f), "DISPLAY");
            y += 26f;

            ui.Toggle(new Rect(area.x, y, column, RowHeight), "Round HUD", settings.ShowHud);
            y += RowHeight + RowGap;
            ui.Toggle(new Rect(area.x, y, column, RowHeight), "Markers over players", settings.ShowMarkers);
            y += RowHeight + RowGap;
            ui.Toggle(new Rect(area.x, y, column, RowHeight), "Scoreboard", settings.ShowScoreboard);
            y += RowHeight + RowGap;
            ui.Toggle(new Rect(area.x, y, column, RowHeight), "Show my hitbox as a prop", settings.ShowHitbox);
            y += RowHeight + RowGap;
            ui.Toggle(new Rect(area.x, y, column, RowHeight), "Pull the camera back for big props", settings.PropCamera);
            y += RowHeight + RowGap;
            ui.Toggle(new Rect(area.x, y, column, RowHeight), "Test mode (TEST page, dummies)", settings.TestMode);

            y = area.y;
            ui.Header(new Rect(right, y, column, 20f), "KEYS   (click, then press the new key)");
            y += 26f;

            ui.KeyBind(new Rect(right, y, column, RowHeight), "Menu", settings.MenuKey);
            y += RowHeight + RowGap;
            ui.KeyBind(new Rect(right, y, column, RowHeight), "Start / end round", settings.StartKey);
            y += RowHeight + RowGap;
            ui.KeyBind(new Rect(right, y, column, RowHeight), "Become a prop", settings.PropKey);
            y += RowHeight + RowGap;
            ui.KeyBind(new Rect(right, y, column, RowHeight), "Turn prop left", settings.RotateLeftKey);
            y += RowHeight + RowGap;
            ui.KeyBind(new Rect(right, y, column, RowHeight), "Turn prop right", settings.RotateRightKey);
            y += RowHeight + RowGap;
            ui.KeyBind(new Rect(right, y, column, RowHeight), "Tip prop over", settings.TipOverKey);
            y += RowHeight + RowGap;
            ui.KeyBind(new Rect(right, y, column, RowHeight), "Hide in place", settings.HideKey);
            y += RowHeight + RowGap;
            ui.KeyBind(new Rect(right, y, column, RowHeight), "Raise disguise", settings.RaiseKey);
            y += RowHeight + RowGap;
            ui.KeyBind(new Rect(right, y, column, RowHeight), "Lower disguise", settings.LowerKey);
            y += RowHeight + RowGap;
            ui.KeyBind(new Rect(right, y, column, RowHeight), "Taunt (whistle)", settings.TauntKey);
            y += RowHeight + RowGap;
            ui.KeyBind(new Rect(right, y, column, RowHeight), "Watch the seekers", settings.SpectateKey);
        }

        private void EnsureStyles()
        {
            if (titleStyle != null)
            {
                return;
            }

            titleStyle = Gui.Style(26, FontStyle.Bold, Gui.Accent, TextAnchor.MiddleLeft);
            subtitleStyle = Gui.Style(13, FontStyle.Normal, Gui.Dimmed, TextAnchor.MiddleLeft);
            textStyle = Gui.Style(14, FontStyle.Normal, Gui.Normal, TextAnchor.UpperLeft);
            textStyle.wordWrap = true;
            hintStyle = Gui.Style(12, FontStyle.Normal, Gui.Dimmed, TextAnchor.MiddleCenter);
        }
    }
}
