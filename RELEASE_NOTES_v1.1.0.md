# Flipping is Tag v1.1.0

## What's New

### Taunts & Whistle
- **Taunt Whistle**: Replaced the automatic reveal pings with a whistle that hiders play with `T`, heard in 3D from where they hide, or from their disguise in Prop Hunt.
- **Auto Taunt**: Added a whistle that plays by itself when a hider has not taunted for `TauntInterval` seconds (60 by default, 0 turns it off), with a countdown under the role banner.
- **Taunt Guard**: Prevented seekers from faking a whistle by having every machine check the sender is a hider in the host's published state.
- **Taunt Channel**: Introduced a relay message that reaches every machine, the host's own taunts included.

### Spectator Camera
- **Watch the Seekers**: Added `V` for hiders, props and players who are out to follow a seeker with the camera, through the game's own `CameraManager.SetTrackingObject`, while their body stays where it hid.
- **Seeker Switching**: Added the left and right arrows to switch between seekers, and a panel that names the one being watched.
- **Camera Return**: Added the camera's return to the player when they press `V` again, get found or the round ends.

### Tagging & Settings
- **Ping Distance**: Lowered the default ping tag range from 25 m to 15 m, under a new `PingDistance` key so existing config files pick up the new default.

### Test Mode
- **Test Mode Toggle**: Added a Test mode option on the menu's OPTIONS page that shows or hides the TEST page; turning it off removes every dummy.
- **Whistling Dummies**: Added dummies that whistle every 8 to 20 seconds while hiding and can be watched with `V` while they seek.

### Performance
- **Player Lookup**: Optimized player lookups to one pass per frame, with the FishNet owner id read through a delegate instead of reflection.
- **HUD Drawing**: Prevented the HUD and menu from drawing on every IMGUI event; they now draw on repaint only.
- **Spectator Camera Cost**: Optimized the spectator camera to tell the game about a new target only when it changes and to check its seekers twice a second.
- **Prop Detection**: Replaced the native `Bounds.IntersectRay` calls in the prop picker with a managed ray test, and halved how often the preview updates.
- **Name Tags**: Disabled the scene search for name tags outside a round.
- **Hitbox Outline**: Reduced the draw calls of the hitbox outline and disabled it while spectating.

### Architecture & Core Refactor
- The whistle ships inside the dll as a 16-bit WAV embedded resource and is turned into an `AudioClip` at runtime; game sounds use FMOD, but Unity audio stays on for voice chat.
- `RevealInterval` became `TauntInterval` in the match record; the wire format is unchanged.

### Known Issues
- Shares `LookDirectionSyncVar` with Chaos Is Hard and Chained Is Hard; running them together makes all of them sync badly.
- Dummies exist only on the host's machine; other players do not see or hear them.

## Installation
1. Install BepInEx 6 (IL2CPP) and launch the game once.
2. Copy the `FlippingIsTag` folder from the zip into `BepInEx/plugins/`. Everyone in the lobby needs v1.1.0.
3. Press `F7` in game to open the menu, pick a mode and start a round with `F8` (host).
