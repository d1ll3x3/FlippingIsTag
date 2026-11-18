using System.Collections.Generic;
using System.Linq;
using FlippingIsTag.Local;
using FlippingIsTag.Match;
using FlippingIsTag.Net;
using FlippingIsTag.Test;
using UnityEngine;

namespace FlippingIsTag.UI
{
    /// <summary>
    /// Everything the round puts on screen.
    ///
    ///   top centre    match bar: mode, phase, timer, team score or head count
    ///   under it      your role, on a banner in your role's colour, and the distance that matters
    ///   centre        big announcements: the start, your role changing, the end
    ///   top right     a feed of who tagged whom
    ///   right         the scoreboard
    ///   over heads    who is what, and how far
    ///
    /// Labels and textures only: GUI.Box, GUI.Button and GUI.TextField are stripped in this
    /// IL2CPP build and crash the game.
    ///
    /// The feed and the announcements are worked out here from the published state rather than
    /// sent: a player's tag count going up, with who tagged them, is the event. A client that
    /// missed a publish misses a line, never a role.
    /// </summary>
    internal sealed class Hud
    {
        private const float FeedSeconds = 5f;
        private const int MaxFeed = 6;
        private const float AnnouncementSeconds = 1.8f;
        private const float FlashSeconds = 0.6f;

        private static readonly Color RunnerColor = new(0.36f, 0.86f, 0.46f);
        private static readonly Color ChaserColor = new(1f, 0.3f, 0.28f);
        private static readonly Color FrozenColor = new(0.42f, 0.82f, 1f);
        private static readonly Color HiderColor = new(1f, 0.82f, 0.3f);
        private static readonly Color SeekerColor = new(1f, 0.55f, 0.18f);
        private static readonly Color OutColor = new(0.56f, 0.56f, 0.62f);
        private static readonly Color RedTeam = new(1f, 0.32f, 0.32f);
        private static readonly Color BlueTeam = new(0.35f, 0.58f, 1f);

        private readonly struct FeedLine
        {
            public readonly string Text;
            public readonly float At;

            public FeedLine(string text, float at)
            {
                Text = text;
                At = at;
            }
        }

        private readonly List<FeedLine> feed = new();
        private readonly Dictionary<int, PlayerInfo> previous = new();

        private Phase previousPhase;
        private int previousRound = -1;
        private Role previousRole;

        private string announcement;
        private Color announcementColor;
        private float announcedAt = float.NegativeInfinity;
        private float flashAt = float.NegativeInfinity;
        private Color flashColor;

        private GUIStyle tinyStyle;
        private GUIStyle smallStyle;
        private GUIStyle smallRightStyle;
        private GUIStyle timerStyle;
        private GUIStyle bannerStyle;
        private GUIStyle infoStyle;
        private GUIStyle announceStyle;
        private GUIStyle feedStyle;
        private GUIStyle markerStyle;
        private GUIStyle rowStyle;
        private GUIStyle rowRightStyle;
        private GUIStyle titleStyle;
        private GUIStyle outcomeStyle;
        private GUIStyle blindStyle;

        /// <summary>A line in the feed, for things the state cannot tell (a notice from the host or the mod).</summary>
        public void Notify(string text)
        {
            feed.Add(new FeedLine(text, Time.unscaledTime));

            if (feed.Count > MaxFeed)
            {
                feed.RemoveAt(0);
            }
        }

        public void Clear()
        {
            feed.Clear();
            previous.Clear();
            previousRound = -1;
            announcedAt = float.NegativeInfinity;
        }

        /// <summary>Turns changes in the state into feed lines and announcements.</summary>
        public void Tick()
        {
            MatchView view = StateSync.View;
            MatchInfo info = view.Info;
            Rules rules = Rules.For(info.Mode);
            int me = Roster.LocalId;
            PlayerInfo mine = view.Get(me);

            bool newRound = info.Round != previousRound && info.Phase == Phase.Starting;

            if (newRound)
            {
                previous.Clear();
                Announce(rules.Name.ToUpperInvariant(), Gui.Accent);
            }
            else if (info.Phase != previousPhase)
            {
                switch (info.Phase)
                {
                    case Phase.Hiding:
                        Announce(mine.Role == Role.Seeker ? "NO PEEKING" : "HIDE!", HiderColor);
                        break;
                    case Phase.Playing:
                        Announce(rules.HasHiding ? "SEEKERS RELEASED" : "GO!", Gui.Accent);
                        break;
                    case Phase.Results:
                        Announce("ROUND OVER", Gui.Accent);
                        break;
                }
            }
            else if (view.InRound && mine.Role != previousRole && info.Phase == Phase.Playing)
            {
                AnnounceRole(mine, previousRole, info.Mode);
            }

            if (info.Phase == Phase.Playing)
            {
                foreach (KeyValuePair<int, PlayerInfo> pair in view.Players)
                {
                    if (previous.TryGetValue(pair.Key, out PlayerInfo before) && before.TagCount != pair.Value.TagCount)
                    {
                        Notify(TagLine(pair.Value, pair.Key, before, info.Mode));

                        if (pair.Key == me)
                        {
                            Flash(ChaserColor);
                        }
                    }
                }
            }

            previous.Clear();

            foreach (KeyValuePair<int, PlayerInfo> pair in view.Players)
            {
                previous[pair.Key] = pair.Value;
            }

            previousPhase = info.Phase;
            previousRound = info.Round;
            previousRole = mine.Role;
        }

        private void AnnounceRole(PlayerInfo mine, Role before, GameMode mode)
        {
            Color color = ColorOf(mine, mode);

            switch (mine.Role)
            {
                case Role.Chaser:
                    Announce(mode == GameMode.Infection ? "INFECTED!" : mode == GameMode.Freeze ? "YOU'RE A FREEZER" : "YOU'RE IT!", color);
                    break;
                case Role.Frozen:
                    Announce(mode == GameMode.Teams ? "STUNNED!" : "FROZEN!", color);
                    break;
                case Role.Seeker when before == Role.Hider || before == Role.Prop:
                    Announce("FOUND! NOW SEEK", color);
                    break;
                case Role.Out:
                    Announce("FOUND!", color);
                    break;
                case Role.Runner when before == Role.Frozen:
                    Announce("FREE!", color);
                    break;
                case Role.Runner when before == Role.Chaser:
                    Announce("TAGGED THEM!", color);
                    break;
            }
        }

        private void Announce(string text, Color color)
        {
            announcement = text;
            announcementColor = color;
            announcedAt = Time.unscaledTime;
        }

        private void Flash(Color color)
        {
            flashColor = color;
            flashAt = Time.unscaledTime;
        }

        private static string TagLine(PlayerInfo now, int target, PlayerInfo before, GameMode mode)
        {
            string who = now.TaggedBy >= 0 ? Name(now.TaggedBy) : "Someone";
            string whom = Gui.Colored(Roster.NameOf(target), ColorOf(before, mode));

            return now.Role switch
            {
                Role.Frozen when mode == GameMode.Teams => $"{who}  stunned  {whom}",
                Role.Frozen => $"{who}  froze  {whom}",
                Role.Seeker or Role.Out when before.Role == Role.Prop || before.Role == Role.Hider => $"{who}  found  {whom}",
                Role.Chaser when mode == GameMode.Infection => $"{who}  infected  {whom}",
                _ => $"{who}  tagged  {whom}",
            };

            string Name(int id) => Gui.Colored(Roster.NameOf(id), ColorOf(StateSync.View.Get(id), mode));
        }

        // ---- drawing ------------------------------------------------------------------------

        public void Draw(TagSettings settings, bool menuOpen, Props props)
        {
            Props.Aim? aim = props.Current;
            bool disguised = props.LocalDisguiseSize > 0f;

            EnsureStyles();

            MatchView view = StateSync.View;
            MatchInfo info = view.Info;
            Rules rules = Rules.For(info.Mode);
            int me = Roster.LocalId;
            PlayerInfo mine = view.Get(me);

            bool blind = view.InRound && rules.IsBlind(mine, info.Phase);
            bool running = view.InRound && info.Phase != Phase.Results;

            if (blind)
            {
                DrawBlind();
            }
            else if (running && rules.IsHeld(mine, info.Phase))
            {
                DrawFrame(mine.Role == Role.Frozen ? FrozenColor : Gui.Dimmed, 0.35f);
            }

            DrawFlash();

            if (disguised && settings.ShowHitbox.Value && !blind)
            {
                DrawHitbox();
            }

            if (settings.ShowMarkers.Value && view.InRound && !blind)
            {
                DrawMarkers(view, me);
            }

            if (!settings.ShowHud.Value)
            {
                return;
            }

            if (!view.InRound)
            {
                if (!menuOpen)
                {
                    DrawIdle(settings);
                }
            }
            else if (info.Phase == Phase.Results)
            {
                DrawResults(view, rules, settings);
            }
            else
            {
                DrawMatchBar(view, rules);
                DrawBanner(view, rules, mine, me);

                if (props.HidingInPlace && !menuOpen)
                {
                    DrawHiding(props, settings);
                }
                else if (aim.HasValue && !menuOpen)
                {
                    DrawPropAim(aim.Value, settings, disguised);
                }

                if (settings.ShowScoreboard.Value)
                {
                    DrawScoreboard(view, me, Screen.width - 276f, Screen.height * 0.32f, 260f);
                }
            }

            DrawAnnouncement();
            DrawFeed();
        }

        private void DrawIdle(TagSettings settings)
        {
            string line = NetRole.IsHost
                ? $"FLIPPING IS TAG   {settings.MenuKey.Value} menu  ·  {settings.StartKey.Value} start round" +
                  (Dummies.Count > 0 ? $"  ·  {Dummies.Count} dummies" : string.Empty)
                : StateSync.Connected
                    ? $"FLIPPING IS TAG   waiting for the host  ·  {settings.MenuKey.Value} menu"
                    : "FLIPPING IS TAG   the host doesn't have the mod";

            var rect = new Rect(16f, Screen.height - 42f, 470f, 28f);
            Gui.RoundedFill(rect, Gui.Panel);
            Gui.ShadowLabel(new Rect(rect.x + 12f, rect.y, rect.width - 24f, rect.height), line, smallStyle);
        }

        private void DrawMatchBar(MatchView view, Rules rules)
        {
            MatchInfo info = view.Info;
            const float width = 440f;
            const float height = 58f;
            float x = (Screen.width - width) * 0.5f;
            const float y = 12f;

            Gui.RoundedFill(new Rect(x, y, width, height), Gui.Panel);

            string phase = info.Phase switch
            {
                Phase.Starting => "GET READY",
                Phase.Hiding => "HIDING",
                _ => "LIVE",
            };

            Gui.ShadowLabel(new Rect(x + 16f, y + 8f, 150f, 20f), rules.Name.ToUpperInvariant(), smallStyle);
            Gui.ShadowLabel(new Rect(x + 16f, y + 28f, 150f, 20f), phase, tinyStyle);

            float left = StateSync.SecondsLeft;
            bool urgent = info.Phase == Phase.Playing && left <= 10f;
            float pulse = urgent ? 0.6f + 0.4f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 6f)) : 1f;

            timerStyle.normal.textColor = urgent ? ChaserColor : Gui.Highlight;
            Gui.ShadowLabel(new Rect(x, y, width, height), FormatTime(left), timerStyle, pulse);

            Gui.ShadowLabel(new Rect(x + width - 166f, y + 8f, 150f, 40f), Tally(view, rules), smallRightStyle);
        }

        /// <summary>Right side of the match bar: the team score, or who is left.</summary>
        private static string Tally(MatchView view, Rules rules)
        {
            switch (view.Info.Mode)
            {
                case GameMode.Teams:
                    return $"{Gui.Colored($"RED {view.TeamScore(0)}", RedTeam)}  ·  {Gui.Colored($"{view.TeamScore(1)} BLUE", BlueTeam)}";
                case GameMode.Classic:
                    return $"{view.Players.Count(p => p.Value.Role != Role.None)} players";
                case GameMode.Infection:
                    return $"{Gui.Colored($"{view.Count(Role.Runner)} alive", RunnerColor)}\n{Gui.Colored($"{view.Count(Role.Chaser)} infected", ChaserColor)}";
                case GameMode.Freeze:
                    return $"{Gui.Colored($"{view.Count(Role.Runner)} running", RunnerColor)}\n{Gui.Colored($"{view.Count(Role.Frozen)} frozen", FrozenColor)}";
                default:
                    Role hidden = view.Info.Mode == GameMode.PropHunt ? Role.Prop : Role.Hider;
                    return $"{Gui.Colored($"{view.Count(hidden)} hidden", HiderColor)}\n{Gui.Colored($"{view.Count(Role.Seeker)} seeking", SeekerColor)}";
            }
        }

        private void DrawBanner(MatchView view, Rules rules, PlayerInfo mine, int me)
        {
            MatchInfo info = view.Info;
            string headline = rules.Headline(mine, info.Phase);

            if (string.IsNullOrEmpty(headline))
            {
                headline = "SPECTATING";
            }

            const float width = 440f;
            float x = (Screen.width - width) * 0.5f;
            const float y = 76f;

            Color color = ColorOf(mine, info.Mode);
            Gui.RoundedFill(new Rect(x, y, width, 34f), new Color(color.r * 0.55f, color.g * 0.55f, color.b * 0.55f, 0.85f));
            Gui.ShadowLabel(new Rect(x, y, width, 34f), headline, bannerStyle);

            string detail = Detail(view, rules, mine, me);

            if (!string.IsNullOrEmpty(detail))
            {
                Gui.ShadowLabel(new Rect(x, y + 36f, width, 22f), detail, infoStyle);
            }
        }

        /// <summary>The one number worth knowing right now: how close the danger, or the prey, is.</summary>
        private static string Detail(MatchView view, Rules rules, PlayerInfo mine, int me)
        {
            var parts = new List<string>();

            if (mine.Immune)
            {
                parts.Add(Gui.Colored("PROTECTED", FrozenColor));
            }

            if (view.Info.Mode == GameMode.PropHunt && mine.Role == Role.Seeker && view.Info.PropMisses > 0)
            {
                parts.Add($"misses {mine.Misses}/{view.Info.PropMisses}");
            }

            // Not in the hiding games: a distance to the nearest hider would give them away.
            if (view.Info.Phase == Phase.Playing && !rules.HasHiding && Roster.TryGetPosition(me, out Vector3 here))
            {
                float target = Nearest(view, me, here, other => !other.Immune && rules.CanTag(mine, other));
                float threat = Nearest(view, me, here, other => rules.CanTag(other, mine) && !rules.IsHeld(other, Phase.Playing));
                float frozen = Nearest(view, me, here, other => rules.CanRescue(mine, other));

                if (!float.IsPositiveInfinity(target))
                {
                    parts.Add($"nearest target {target:0} m");
                }

                if (!float.IsPositiveInfinity(threat))
                {
                    parts.Add(Gui.Colored($"nearest chaser {threat:0} m", threat < 8f ? ChaserColor : Gui.Normal));
                }

                if (!float.IsPositiveInfinity(frozen))
                {
                    parts.Add(Gui.Colored($"frozen teammate {frozen:0} m", FrozenColor));
                }
            }

            return string.Join("   ·   ", parts);
        }

        private static float Nearest(MatchView view, int me, Vector3 here, System.Func<PlayerInfo, bool> wanted)
        {
            float best = float.PositiveInfinity;

            foreach (KeyValuePair<int, PlayerInfo> pair in view.Players)
            {
                if (pair.Key == me || pair.Value.Role == Role.None || !wanted(pair.Value) ||
                    !Roster.TryGetPosition(pair.Key, out Vector3 there))
                {
                    continue;
                }

                best = Mathf.Min(best, Vector3.Distance(here, there));
            }

            return best;
        }

        /// <summary>A crosshair, and under it what the prop key would turn us into.</summary>
        private void DrawPropAim(Props.Aim aim, TagSettings settings, bool disguised)
        {
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            Color color = aim.Valid ? RunnerColor : ChaserColor;

            Gui.Fill(new Rect(cx - 9f, cy - 1f, 18f, 2f), color);
            Gui.Fill(new Rect(cx - 1f, cy - 9f, 2f, 18f), color);

            if (aim.Box.HasValue)
            {
                DrawBrackets(aim.Box.Value, color);
            }

            string text = aim.Valid ? $"[{settings.PropKey.Value}]  become {aim.Text}" : aim.Text;
            float width = 40f + text.Length * 7.5f;
            var rect = new Rect(cx - width * 0.5f, cy + 22f, width, 26f);

            Gui.RoundedFill(rect, new Color(0f, 0f, 0f, 0.6f));
            infoStyle.normal.textColor = color;
            Gui.ShadowLabel(rect, text, infoStyle);
            infoStyle.normal.textColor = Gui.Normal;

            if (disguised)
            {
                string keys = $"[{settings.RotateLeftKey.Value}] [{settings.RotateRightKey.Value}] turn   ·   " +
                              $"[{settings.TipOverKey.Value}] tip over   ·   [{settings.HideKey.Value}] hide in place";
                Gui.ShadowLabel(new Rect(0f, cy + 50f, Screen.width, 22f), keys, infoStyle);
            }
        }

        /// <summary>The hiding-in-place panel: how to shift the disguise, how far it is shifted, and why a shift was refused.</summary>
        private void DrawHiding(Props props, TagSettings settings)
        {
            const float width = 520f;
            float x = (Screen.width - width) * 0.5f;
            float y = Screen.height - 170f;

            Gui.RoundedFill(new Rect(x, y, width, 120f), Gui.Panel);

            bannerStyle.normal.textColor = HiderColor;
            Gui.ShadowLabel(new Rect(x, y + 8f, width, 30f), "HIDING IN PLACE", bannerStyle);
            bannerStyle.normal.textColor = Color.white;

            Gui.ShadowLabel(new Rect(x, y + 40f, width, 22f),
                $"arrows  move   ·   [{settings.RaiseKey.Value}] up   ·   [{settings.LowerKey.Value}] down   ·   " +
                $"[{settings.ResetOffsetKey.Value}] centre", infoStyle);
            Gui.ShadowLabel(new Rect(x, y + 62f, width, 22f),
                $"[{settings.RotateLeftKey.Value}] [{settings.RotateRightKey.Value}] turn   ·   [{settings.TipOverKey.Value}] tip over   ·   " +
                $"[{settings.HideKey.Value}] stop hiding", infoStyle);

            Vector3 offset = props.LocalOffset;
            string warning = props.Warning;
            string line = warning != null
                ? Gui.Colored(warning, ChaserColor)
                : $"shifted  {offset.x:+0.00;-0.00;0.00}  {offset.y:+0.00;-0.00;0.00}  {offset.z:+0.00;-0.00;0.00} m   " +
                  $"(up to {Match.PropOffset.Limit:0.0} m, body stays inside)";

            Gui.ShadowLabel(new Rect(x, y + 88f, width, 22f), line, infoStyle);
        }

        /// <summary>
        /// Our real body while disguised: what a seeker has to touch, which is not the prop. A
        /// tall prop on a small body can be tagged only low down, a flat one sticks out of it.
        /// </summary>
        private static void DrawHitbox()
        {
            Camera camera = GameCamera.Get();
            Collider body = Roster.Local?.PlayerCollider;

            if (camera == null || body == null)
            {
                return;
            }

            Outline3D.Collider(camera, body, new Color(0.45f, 1f, 0.6f, 0.9f));
        }

        /// <summary>Corner brackets round an object on screen: exactly what the prop key would copy.</summary>
        private static void DrawBrackets(Bounds bounds, Color color)
        {
            Camera camera = GameCamera.Get();

            if (camera == null)
            {
                return;
            }

            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            float left = float.PositiveInfinity;
            float right = float.NegativeInfinity;
            float top = float.PositiveInfinity;
            float bottom = float.NegativeInfinity;

            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z);
                Vector3 screen = camera.WorldToScreenPoint(corner);

                // A corner behind the camera has no place on screen; the box would be nonsense.
                if (screen.z <= 0f)
                {
                    return;
                }

                left = Mathf.Min(left, screen.x);
                right = Mathf.Max(right, screen.x);
                top = Mathf.Min(top, Screen.height - screen.y);
                bottom = Mathf.Max(bottom, Screen.height - screen.y);
            }

            left = Mathf.Max(left - 4f, 0f);
            top = Mathf.Max(top - 4f, 0f);
            right = Mathf.Min(right + 4f, Screen.width);
            bottom = Mathf.Min(bottom + 4f, Screen.height);

            float arm = Mathf.Clamp(Mathf.Min(right - left, bottom - top) * 0.25f, 8f, 40f);
            const float thick = 3f;
            var c = new Color(color.r, color.g, color.b, 0.9f);

            Gui.Fill(new Rect(left, top, arm, thick), c);
            Gui.Fill(new Rect(left, top, thick, arm), c);
            Gui.Fill(new Rect(right - arm, top, arm, thick), c);
            Gui.Fill(new Rect(right - thick, top, thick, arm), c);
            Gui.Fill(new Rect(left, bottom - thick, arm, thick), c);
            Gui.Fill(new Rect(left, bottom - arm, thick, arm), c);
            Gui.Fill(new Rect(right - arm, bottom - thick, arm, thick), c);
            Gui.Fill(new Rect(right - thick, bottom - arm, thick, arm), c);
        }

        private void DrawBlind()
        {
            Gui.Fill(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0.02f, 0.02f, 0.03f, 0.985f));

            float y = Screen.height * 0.36f;
            Gui.ShadowLabel(new Rect(0f, y, Screen.width, 90f), Mathf.CeilToInt(StateSync.SecondsLeft).ToString(), blindStyle);
            Gui.ShadowLabel(new Rect(0f, y + 96f, Screen.width, 30f), "NO PEEKING", titleStyle);
            Gui.ShadowLabel(new Rect(0f, y + 126f, Screen.width, 24f), "The others are hiding. You're released when the count ends.", infoStyle);
        }

        /// <summary>A coloured edge round the screen, for being held in place.</summary>
        private static void DrawFrame(Color color, float alpha)
        {
            const int steps = 6;
            const float thickness = 60f;

            for (int i = 0; i < steps; i++)
            {
                float inset = thickness * i / steps;
                float band = thickness / steps;
                var c = new Color(color.r, color.g, color.b, alpha * (1f - (float)i / steps) * 0.5f);

                Gui.Fill(new Rect(inset, inset, Screen.width - inset * 2f, band), c);
                Gui.Fill(new Rect(inset, Screen.height - inset - band, Screen.width - inset * 2f, band), c);
                Gui.Fill(new Rect(inset, inset + band, band, Screen.height - (inset + band) * 2f), c);
                Gui.Fill(new Rect(Screen.width - inset - band, inset + band, band, Screen.height - (inset + band) * 2f), c);
            }
        }

        private void DrawFlash()
        {
            float age = Time.unscaledTime - flashAt;

            if (age >= 0f && age < FlashSeconds)
            {
                DrawFrame(flashColor, 1f - age / FlashSeconds);
            }
        }

        private void DrawAnnouncement()
        {
            float age = Time.unscaledTime - announcedAt;

            if (announcement == null || age < 0f || age > AnnouncementSeconds)
            {
                return;
            }

            // Pops in big and settles, then fades.
            float pop = Mathf.Clamp01(age / 0.15f);
            float alpha = Mathf.Clamp01((AnnouncementSeconds - age) / 0.5f);
            int size = Mathf.RoundToInt(Mathf.Lerp(60f, 46f, pop));

            announceStyle.fontSize = size;
            announceStyle.normal.textColor = announcementColor;
            Gui.ShadowLabel(new Rect(0f, Screen.height * 0.27f, Screen.width, 80f), announcement, announceStyle, alpha);
        }

        private void DrawFeed()
        {
            const float width = 360f;
            float x = Screen.width - width - 16f;
            float y = 16f;

            for (int i = feed.Count - 1; i >= 0; i--)
            {
                float age = Time.unscaledTime - feed[i].At;

                if (age > FeedSeconds)
                {
                    feed.RemoveAt(i);
                }
            }

            foreach (FeedLine line in feed)
            {
                float age = Time.unscaledTime - line.At;
                float alpha = Mathf.Clamp01((FeedSeconds - age) / 0.6f);

                Gui.RoundedFill(new Rect(x, y, width, 26f), new Color(0f, 0f, 0f, 0.55f * alpha));
                Gui.ShadowLabel(new Rect(x + 10f, y, width - 20f, 26f), line.Text, feedStyle, alpha);
                y += 30f;
            }
        }

        private void DrawMarkers(MatchView view, int me)
        {
            Camera camera = GameCamera.Get();

            if (camera == null || !Roster.TryGetPosition(me, out Vector3 here))
            {
                return;
            }

            foreach (KeyValuePair<int, PlayerInfo> pair in view.Players)
            {
                if (pair.Key == me || pair.Value.Role == Role.None || Concealment.IsConcealed(view, pair.Key) ||
                    !Roster.TryGetPosition(pair.Key, out Vector3 there))
                {
                    continue;
                }

                Vector3 screen = camera.WorldToScreenPoint(there + Vector3.up * 1.5f);

                if (screen.z <= 0f)
                {
                    continue;
                }

                float distance = Vector3.Distance(here, there);
                string label = $"{MarkerLabel(pair.Value, view.Info.Mode)}  {distance:0}m";
                Color color = ColorOf(pair.Value, view.Info.Mode);

                float width = 30f + label.Length * 7f;
                var rect = new Rect(screen.x - width * 0.5f, Screen.height - screen.y - 24f, width, 20f);

                Gui.RoundedFill(rect, new Color(color.r * 0.45f, color.g * 0.45f, color.b * 0.45f, 0.8f));
                Gui.ShadowLabel(rect, label, markerStyle);
            }
        }

        private static string MarkerLabel(PlayerInfo player, GameMode mode)
        {
            if (mode == GameMode.Teams)
            {
                return player.Role == Role.Frozen ? "STUNNED" : TeamRules.TeamName(player.Team);
            }

            return player.Role switch
            {
                Role.Chaser => mode == GameMode.Infection ? "INFECTED" : mode == GameMode.Freeze ? "FREEZER" : "IT",
                Role.Frozen => "FROZEN",
                Role.Seeker => "SEEKER",
                Role.Hider => "HIDER",
                Role.Prop => "PROP",
                Role.Out => "OUT",
                _ => "RUNNER",
            };
        }

        private void DrawScoreboard(MatchView view, int me, float x, float y, float width)
        {
            List<KeyValuePair<int, PlayerInfo>> rows = view.Players
                .Where(p => p.Value.Role != Role.None)
                .OrderByDescending(p => p.Value.Score)
                .ToList();

            const float lineHeight = 22f;
            float height = 34f + lineHeight * rows.Count;

            Gui.RoundedFill(new Rect(x, y, width, height), Gui.Panel);
            Gui.ShadowLabel(new Rect(x + 12f, y + 6f, width - 24f, 20f), "SCOREBOARD", tinyStyle);
            Gui.ShadowLabel(new Rect(x + 12f, y + 6f, width - 24f, 20f), "PTS", smallRightStyle);
            y += 28f;

            for (int i = 0; i < rows.Count; i++)
            {
                KeyValuePair<int, PlayerInfo> row = rows[i];
                Color color = ColorOf(row.Value, view.Info.Mode);

                if (row.Key == me)
                {
                    Gui.RoundedFill(new Rect(x + 4f, y, width - 8f, lineHeight), new Color(1f, 1f, 1f, 0.08f));
                }

                Gui.RoundedFill(new Rect(x + 12f, y + 7f, 8f, 8f), color);

                rowStyle.fontStyle = row.Key == me ? FontStyle.Bold : FontStyle.Normal;
                Gui.ShadowLabel(new Rect(x + 28f, y, width - 110f, lineHeight), $"{i + 1}. {Roster.NameOf(row.Key)}", rowStyle);

                tinyStyle.normal.textColor = color;
                Gui.ShadowLabel(new Rect(x + width - 120f, y, 70f, lineHeight), MarkerLabel(row.Value, view.Info.Mode), tinyStyle);
                tinyStyle.normal.textColor = Gui.Dimmed;

                Gui.ShadowLabel(new Rect(x, y, width - 12f, lineHeight), row.Value.Score.ToString(), rowRightStyle);
                y += lineHeight;
            }
        }

        private void DrawResults(MatchView view, Rules rules, TagSettings settings)
        {
            const float width = 560f;
            int count = view.Players.Count(p => p.Value.Role != Role.None);
            float height = 150f + count * 22f + 40f;
            float x = (Screen.width - width) * 0.5f;
            float y = Screen.height * 0.16f;

            Gui.RoundedFill(new Rect(x, y, width, height), Gui.Backdrop);
            Gui.ShadowLabel(new Rect(x, y + 14f, width, 30f), "ROUND OVER", titleStyle);
            Gui.ShadowLabel(new Rect(x + 16f, y + 48f, width - 32f, 56f), rules.Outcome(view, Roster.NameOf), outcomeStyle);

            DrawScoreboard(view, Roster.LocalId, x + 40f, y + 108f, width - 80f);

            string next = NetRole.IsHost
                ? $"{settings.StartKey.Value} for the next round  ·  back to free play in {Mathf.CeilToInt(StateSync.SecondsLeft)}s"
                : $"Back to free play in {Mathf.CeilToInt(StateSync.SecondsLeft)}s";

            Gui.ShadowLabel(new Rect(x, y + height - 32f, width, 22f), next, infoStyle);
        }

        public static Color ColorOf(PlayerInfo player, GameMode mode)
        {
            if (mode == GameMode.Teams && player.Role != Role.Frozen && player.Role != Role.None)
            {
                return player.Team == 0 ? RedTeam : BlueTeam;
            }

            return player.Role switch
            {
                Role.Runner => RunnerColor,
                Role.Chaser => ChaserColor,
                Role.Frozen => FrozenColor,
                Role.Hider or Role.Prop => HiderColor,
                Role.Seeker => SeekerColor,
                _ => OutColor,
            };
        }

        private static string FormatTime(float seconds)
        {
            int total = Mathf.CeilToInt(seconds);
            return $"{total / 60}:{total % 60:00}";
        }

        private void EnsureStyles()
        {
            if (timerStyle != null)
            {
                return;
            }

            tinyStyle = Gui.Style(11, FontStyle.Bold, Gui.Dimmed, TextAnchor.MiddleLeft);
            smallStyle = Gui.Style(13, FontStyle.Bold, Gui.Highlight, TextAnchor.MiddleLeft);
            smallRightStyle = Gui.Style(12, FontStyle.Bold, Gui.Normal, TextAnchor.MiddleRight, true);
            timerStyle = Gui.Style(32, FontStyle.Bold, Gui.Highlight, TextAnchor.MiddleCenter);
            bannerStyle = Gui.Style(18, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
            infoStyle = Gui.Style(13, FontStyle.Bold, Gui.Normal, TextAnchor.MiddleCenter, true);
            announceStyle = Gui.Style(46, FontStyle.Bold, Gui.Accent, TextAnchor.MiddleCenter);
            feedStyle = Gui.Style(13, FontStyle.Bold, Gui.Highlight, TextAnchor.MiddleLeft, true);
            markerStyle = Gui.Style(12, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
            rowStyle = Gui.Style(13, FontStyle.Normal, Gui.Highlight, TextAnchor.MiddleLeft);
            rowRightStyle = Gui.Style(13, FontStyle.Bold, Gui.Highlight, TextAnchor.MiddleRight);
            titleStyle = Gui.Style(22, FontStyle.Bold, Gui.Accent, TextAnchor.MiddleCenter);
            outcomeStyle = Gui.Style(16, FontStyle.Bold, Gui.Highlight, TextAnchor.MiddleCenter);
            outcomeStyle.wordWrap = true;
            blindStyle = Gui.Style(84, FontStyle.Bold, Gui.Accent, TextAnchor.MiddleCenter);
        }
    }
}
