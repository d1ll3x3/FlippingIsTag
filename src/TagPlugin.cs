using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using FlippingIsTag.Net;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FlippingIsTag
{
    [BepInPlugin(Guid, "Flipping is Tag", Version)]
    public class TagPlugin : BasePlugin
    {
        public const string Guid = "com.dani.flippingistag";
        public const string Version = "1.0.0";

        internal static ManualLogSource Logger { get; private set; }
        internal static TagSettings Settings { get; private set; }

        public override void Load()
        {
            Logger = Log;

            // The config file lives next to the dll instead of BepInEx\config, so the whole
            // mod is one folder you can copy between installs and keep your binds.
            string folder = Path.GetDirectoryName(typeof(TagPlugin).Assembly.Location) ?? Paths.ConfigPath;
            string path = Path.Combine(folder, "FlippingIsTag.cfg");

            Settings = new TagSettings(new ConfigFile(path, true));

            // Before any player can ping or touch anyone: the patches are how a client's tag
            // reaches the host.
            var harmony = new Harmony(Guid);
            Messages.Install(harmony);
            PingHook.Install(harmony);

            ClassInjector.RegisterTypeInIl2Cpp<TagBehaviour>();

            var host = new GameObject("FlippingIsTag");
            Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<TagBehaviour>();

            Logger.LogInfo($"Loaded. Menu={Settings.MenuKey.Value} Start={Settings.StartKey.Value} " +
                           $"Prop={Settings.PropKey.Value}. Settings file: {path}");
        }

        public override bool Unload()
        {
            Settings?.Save();
            return true;
        }
    }
}
