# Flipping is Tag v1.0.0

## What's New

### Game Modes & Rounds
- **Six Game Modes**: Added Classic Tag, Infection, Freeze Tag, Team Tag, Hide and Seek and Prop Hunt, all in one mod and picked from the menu (`F7`).
- **Host-Run Rounds**: Added rounds that only the host starts and ends (`F8`), with a head start for the chasers, a blindfolded hiding phase for the seekers and a results screen.
- **Gather at Start**: Added an option that brings every player to the host when a round starts, spread in a ring.
- **Late Joiners**: Added roles for players who join a round already under way, and a new chaser when the last one leaves.
- **Player Collisions**: Added a host option that makes players bump into each other instead of passing through, in and out of a round.

### Tagging & Networking
- **Tag by Touch**: Added touch tagging judged on the tagger's machine and checked by the host against its own positions, with lag slack.
- **Tag by Ping**: Integrated the game's own ping as a tag through a host-side hook on `PlayerPings.RpcLogic___RequestPingServerRpc`, so pinging a player tags them.
- **No Tag-Backs**: Added a protection window after a tag or a rescue (`TagBackSeconds`).
- **State Sync**: Introduced match and player state published by the host over `PlayerNetworked.LookDirectionSyncVar`, written on each player's own object, so a client that misses an update is right again on the next one.
- **Client Messages**: Introduced touches, rescues, props, poses and team wishes sent through `PlayerFeatureVisualRelay`, the channel the game uses for pad effects.

### Prop Hunt
- **Level Objects as Props**: Added `G` to turn into the object under the crosshair, copied from its LOD0 meshes so it looks exactly like the level around it.
- **Pick Preview**: Added a crosshair that boxes the object you would become and names it, or says why it cannot be a prop.
- **Collider-less Objects**: Added picking of objects with no collider (chairs, sticky notes, pebbles) by testing the aim against each mesh's own oriented box.
- **Ground Excluded**: Prevented ground blocks, area ground and VFX objects from being picked as props.
- **Prop Pose**: Added `Z` / `X` to turn the prop 15° at a time and `C` to tip it over, drawn the same on every screen.
- **Hide in Place**: Added `H` to stop and shift the disguise with the arrow keys, `Space` and `Left Ctrl`, up to 1.55 m on each axis with the body kept inside it.
- **Disguise Tags**: Fixed seekers having to find the hidden body by making a touch or a ping on the disguise tag the prop.
- **Hitbox Outline**: Visualized the prop's real body on screen while disguised.
- **Prop Camera**: Added a Cinemachine orbit pull-back that fits big props on screen and restores the game's values afterwards.
- **Seeker Misses**: Added a stun for seekers who ping too many things that are not players (`PropMisses`).

### Hide and Seek
- **Hidden Names**: Prevented the name over a hider's head from showing to the seekers, through walls included.
- **Reveal Pings**: Added a ping every `RevealInterval` seconds that gives each hider's position away.

### Test Mode
- **Test Dummies**: Added up to 15 host-only dummies from the menu's TEST page that chase, run, free teammates, hide and pick props, and can be tagged by touch or by ping.
- **Phone Look**: Added dummies that copy your own phone, skin included, from the game's own materials.

### HUD & Menu
- **Round HUD**: Added a match bar with timer and tallies, a role banner with the nearest chaser or target, a tag feed, big announcements, markers over players and a scoreboard.
- **Mouse Menu**: Added a four-page menu (PLAY, RULES, TEST, OPTIONS) with clickable cards, toggles, sliders and rebindable keys, also driven by the keyboard and scaled to the screen.

### Architecture & Core Refactor
- Rules live in one class per mode, read by every machine against the same published state; only the host changes it.
- Props are named by a hash of the object's place in the scene, resolved the same way on every machine with no network spawning.
- Harmony patches go only on methods with a body of their own, checked against the address map.
- `Matrix4x4.determinant` is avoided: it throws `InvalidProgramException` in this game's interop.

### Known Issues
- Shares `LookDirectionSyncVar` with Chaos Is Hard and Chained Is Hard; running them together makes all of them sync badly.
- Only players whose connection id is 255 or lower can be named in a message.
- Dummies exist only on the host's machine; other players do not see them.

## Installation
1. Install BepInEx 6 (IL2CPP) and launch the game once.
2. Copy the `FlippingIsTag` folder from the zip into `BepInEx/plugins/`. Everyone in the lobby needs v1.0.0.
3. Press `F7` in game to open the menu, pick a mode and start a round with `F8` (host).
