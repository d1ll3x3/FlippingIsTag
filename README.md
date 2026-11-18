# Flipping is Tag

A BepInEx mod for **Flipping is Hard** with a whole set of tag games in one mod: classic tag,
infection, freeze tag, team tag, hide and seek and prop hunt.

Everyone in the lobby needs the mod. The host picks the game, sets the rules and starts the rounds.

## Installing

Copy the `FlippingIsTag` folder into `BepInEx\plugins`. The config file lives next to the dll.

## Keys

| Key | What it does |
|---|---|
| `F7` | Menu: game mode, rules, start or end a round, test dummies, keys. Use the mouse, or the arrows, Enter, R (default) and Tab (next page). |
| `F8` | Start or end a round (host only). |
| `G` | Prop hunt: turn into the object under the crosshair. While you are a prop, the crosshair shows what you would become. |
| `Z` / `X` | Prop hunt: turn your prop left / right, 15° at a time. Everyone sees the same pose. |
| `C` | Prop hunt: tip your prop over, a quarter turn at a time. |
| `T` | Hide and seek and prop hunt: taunt. You whistle, and everyone hears it from where you are. |
| `V` | Hide and seek and prop hunt: watch the seekers look for you. Your body stays where it hid; the left and right arrows switch seekers, `V` again brings the camera back. Players who are out can watch too. |
| `H` | Prop hunt: hide in place. You stop where you are, and the arrow keys slide your disguise (the way the camera looks), `Space` / `Left Ctrl` raise and lower it, `Backspace` puts it back on you. `H` again to stop. |

While you are a prop, your real body is outlined on screen, and the camera pulls back as far as a big
prop needs. Both can be turned off in the menu.

Hiding in place cannot be used to cheat: the disguise moves at most 1.55 m from your body on each axis
(the network format cannot carry more), your body has to stay inside it, and a seeker who touches or
pings the disguise tags you, wherever your body is inside it. Walk off and the disguise snaps back onto you.

## Game modes

| Mode | How it plays |
|---|---|
| Classic Tag | One player is it and passes it on with a tag. A point for every second you stay free. Whoever just passed it on is protected for a moment, so there are no tag-backs. |
| Infection | Whoever gets tagged joins the infected. The survivors win if anyone is left when the clock runs out. |
| Freeze Tag | The freezers freeze you with a tag. A runner frees a frozen teammate by touching them. |
| Team Tag | Red against blue, everybody chases everybody. Tagging a rival stuns them and scores a point for your team. |
| Hide and Seek | The seekers wait blindfolded while the rest hide. Found players join the seekers (or sit out). A hider who has not taunted for a minute whistles by themselves (`TauntInterval`, 0 turns it off). |
| Prop Hunt | Hide and seek, but you disguise yourself as something in the level. Props taunt and whistle by themselves the same way. A seeker who pings things that are not players gets stunned after too many misses. |

**Tagging** works by touch and by **ping**, using the game's own ping, up to 15 m away by default. Each can be turned off on its own.
**Player collisions** is a host option: players bump into each other instead of passing through, in and out of a round.

## Test mode

Turn on **Test mode** in the menu's OPTIONS page and the host can spawn up to 15 **dummies** from the TEST page to try any mode alone. A dummy is a
copy of your own phone. Dummies chase, run, free their teammates, hide and pick props like players
would, whistle every 8 to 20 seconds while hiding, and you can tag them by touch or by ping and watch
them with `V` when they seek. They exist only on the host's machine: other players do not see or hear them.

## Known limitations

- Shares a network channel (`LookDirectionSyncVar`) with ChaosIsHard and ChainedIsHard. Running them together makes both sync badly.
- A player can only be named in a message if their connection id is 255 or lower.
- A prop is one object of the level (a rock, a stick, a brick) at its closest level of detail. The ground blocks and the merged stand-ins the game draws whole areas with are never props.
