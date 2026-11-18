using BepInEx.Configuration;
using FlippingIsTag.Match;
using FlippingIsTag.Test;
using UnityEngine.InputSystem;

namespace FlippingIsTag
{
    /// <summary>
    /// Everything the mod can be told to do, stored in FlippingIsTag.cfg next to the dll.
    ///
    /// The rules are the host's: only the host runs the match, so a client's copy of them is
    /// what it will play by the day it hosts, not what is being played now. The few rules a
    /// client needs to act on by itself (how close a touch has to be, whether touching counts
    /// at all) travel inside the match state, so nothing here has to be synced on its own.
    /// </summary>
    internal sealed class TagSettings
    {
        private readonly ConfigFile file;

        public ConfigEntry<Key> MenuKey { get; }
        public ConfigEntry<Key> StartKey { get; }
        public ConfigEntry<Key> PropKey { get; }
        public ConfigEntry<Key> RotateLeftKey { get; }
        public ConfigEntry<Key> RotateRightKey { get; }
        public ConfigEntry<Key> TipOverKey { get; }
        public ConfigEntry<Key> HideKey { get; }
        public ConfigEntry<Key> RaiseKey { get; }
        public ConfigEntry<Key> LowerKey { get; }
        public ConfigEntry<Key> ResetOffsetKey { get; }

        public ConfigEntry<GameMode> Mode { get; }
        public ConfigEntry<float> RoundMinutes { get; }
        public ConfigEntry<float> StartDelay { get; }
        public ConfigEntry<float> HideSeconds { get; }
        public ConfigEntry<int> Chasers { get; }
        public ConfigEntry<bool> GatherAtStart { get; }
        public ConfigEntry<bool> Collisions { get; }

        public ConfigEntry<bool> ContactTag { get; }
        public ConfigEntry<float> TagRadius { get; }
        public ConfigEntry<bool> PingTag { get; }
        public ConfigEntry<float> PingRange { get; }
        public ConfigEntry<float> TagBackSeconds { get; }

        public ConfigEntry<float> StunSeconds { get; }
        public ConfigEntry<int> TeamScoreLimit { get; }

        public ConfigEntry<bool> InfectOnFind { get; }
        public ConfigEntry<float> RevealInterval { get; }
        public ConfigEntry<bool> HideNames { get; }
        public ConfigEntry<int> PropMisses { get; }

        public ConfigEntry<DummyBehavior> DummyBehavior { get; }
        public ConfigEntry<float> DummySpeed { get; }

        public ConfigEntry<bool> ShowHud { get; }
        public ConfigEntry<bool> ShowMarkers { get; }
        public ConfigEntry<bool> ShowScoreboard { get; }
        public ConfigEntry<bool> ShowHitbox { get; }
        public ConfigEntry<bool> PropCamera { get; }

        public TagSettings(ConfigFile configFile)
        {
            file = configFile;

            MenuKey = file.Bind("Binds", "MenuKey", Key.F7, "Opens the in-game menu.");
            StartKey = file.Bind("Binds", "StartKey", Key.F8,
                "Starts a round, or ends the one being played. Host only.");
            PropKey = file.Bind("Binds", "PropKey", Key.G,
                "Prop hunt: turn into the object you are looking at.");
            RotateLeftKey = file.Bind("Binds", "RotateLeftKey", Key.Z, "Prop hunt: turn your prop left.");
            RotateRightKey = file.Bind("Binds", "RotateRightKey", Key.X, "Prop hunt: turn your prop right.");
            TipOverKey = file.Bind("Binds", "TipOverKey", Key.C, "Prop hunt: tip your prop over, a quarter turn at a time.");
            HideKey = file.Bind("Binds", "HideKey", Key.H,
                "Prop hunt: hide in place. You stop, and the arrow keys shift your disguise.");
            RaiseKey = file.Bind("Binds", "RaiseKey", Key.Space, "Prop hunt, hiding in place: raise your disguise.");
            LowerKey = file.Bind("Binds", "LowerKey", Key.LeftCtrl, "Prop hunt, hiding in place: lower your disguise.");
            ResetOffsetKey = file.Bind("Binds", "ResetOffsetKey", Key.Backspace,
                "Prop hunt, hiding in place: put your disguise back on your body.");

            Mode = file.Bind("Match", "Mode", GameMode.Classic, "Which game the next round plays.");
            RoundMinutes = file.Bind("Match", "RoundMinutes", 4f, "Length of a round, hiding time not included.");
            StartDelay = file.Bind("Match", "StartDelay", 5f,
                "Seconds the chasers wait at the start of a round, so the rest get a head start.");
            HideSeconds = file.Bind("Match", "HideSeconds", 40f,
                "Hide and seek and prop hunt: how long the seekers stay blind while everyone hides.");
            Chasers = file.Bind("Match", "Chasers", 0,
                "How many players start as chasers (the one who is it, the infected, the seekers). " +
                "0 picks one for every four players.");
            GatherAtStart = file.Bind("Match", "GatherAtStart", true,
                "Bring everyone to the host when a round starts.");
            Collisions = file.Bind("Match", "Collisions", false,
                "Players bump into each other instead of passing through. The host decides it for " +
                "the whole lobby, in and out of a round.");

            ContactTag = file.Bind("Tagging", "ContactTag", true, "Touching a player tags them.");
            TagRadius = file.Bind("Tagging", "TagRadius", 1.6f,
                "How close, in metres, a touch has to be. Measured centre to centre.");
            PingTag = file.Bind("Tagging", "PingTag", true,
                "Pinging a player tags them. Uses the game's own ping.");
            PingRange = file.Bind("Tagging", "PingRange", 25f, "Furthest a ping can tag from, in metres.");
            TagBackSeconds = file.Bind("Tagging", "TagBackSeconds", 3f,
                "How long a player who was just tagged, or just freed, cannot be tagged again.");

            StunSeconds = file.Bind("Teams", "StunSeconds", 4f,
                "Team battle: how long a tagged player stays frozen.");
            TeamScoreLimit = file.Bind("Teams", "ScoreLimit", 15,
                "Team battle: tags that win the round early. 0 plays to the timer.");

            InfectOnFind = file.Bind("Hiding", "InfectOnFind", true,
                "Hide and seek and prop hunt: a found player joins the seekers instead of sitting out.");
            RevealInterval = file.Bind("Hiding", "RevealInterval", 60f,
                "Every this many seconds each hider gives away where they are with a ping. 0 never.");
            HideNames = file.Bind("Hiding", "HideNames", true,
                "Hide the name over a hider's head from the seekers. Without it the name shows through walls.");
            PropMisses = file.Bind("Hiding", "PropMisses", 3,
                "Prop hunt: pings at nothing a seeker can make before being stunned. 0 never stuns.");

            DummyBehavior = file.Bind("Test", "DummyBehavior", Test.DummyBehavior.Play,
                "What test dummies do: Still, Wander, or Play the round.");
            DummySpeed = file.Bind("Test", "DummySpeed", 6f, "How fast test dummies move, in metres per second.");

            ShowHud = file.Bind("Display", "ShowHud", true, "Timer, role and messages at the top of the screen.");
            ShowMarkers = file.Bind("Display", "ShowMarkers", true, "Markers over the other players' heads.");
            ShowScoreboard = file.Bind("Display", "ShowScoreboard", true, "Score list in the corner.");
            ShowHitbox = file.Bind("Display", "ShowHitbox", true,
                "Prop hunt: outline your real body while disguised. That is what seekers have to touch.");
            PropCamera = file.Bind("Display", "PropCamera", true,
                "Prop hunt: pull the camera back as far as your prop needs to fit on screen.");

            Validate();
        }

        /// <summary>Puts every value back in range, so a hand-edited file cannot break a round.</summary>
        public void Validate()
        {
            Clamp(RoundMinutes, 0.5f, 60f);
            Clamp(StartDelay, 0f, 30f);
            Clamp(HideSeconds, 5f, 300f);
            Clamp(Chasers, 0, 16);
            Clamp(TagRadius, 0.5f, 6f);
            Clamp(PingRange, 2f, 200f);
            Clamp(TagBackSeconds, 0f, 30f);
            Clamp(StunSeconds, 0.5f, 30f);
            Clamp(TeamScoreLimit, 0, 200);
            Clamp(RevealInterval, 0f, 600f);
            Clamp(PropMisses, 0, 20);
            Clamp(DummySpeed, 0.5f, 20f);
        }

        public void Save() => file.Save();

        private static void Clamp(ConfigEntry<float> entry, float min, float max)
        {
            if (float.IsNaN(entry.Value) || entry.Value < min || entry.Value > max)
            {
                entry.Value = float.IsNaN(entry.Value) ? (float)entry.DefaultValue : UnityEngine.Mathf.Clamp(entry.Value, min, max);
            }
        }

        private static void Clamp(ConfigEntry<int> entry, int min, int max)
        {
            if (entry.Value < min || entry.Value > max)
            {
                entry.Value = UnityEngine.Mathf.Clamp(entry.Value, min, max);
            }
        }
    }
}
