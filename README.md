# jamesbond

A Unity spy game in two phases:

1. **Stealth infiltration** — third-person; sneak past guards, use gadgets, avoid
   detection cones.
2. **Driving chase** — reaching the car ends the stealth phase and hands off to a
   vehicle pursuit.

The handoff between phases is the design problem worth settling early: scene load
vs. in-scene controller/camera swap determines how persistent state crosses the
boundary.

## Base

Built on Unity's **Behaviour Blocks** multiplayer sample. Three asset modules ship
with it:

| Module | What it gives us |
| --- | --- |
| `Assets/Core` | Modular player, stat system (`HealthStat`, `StaminaStat`), ScriptableObject game-event bus, Cinemachine FreeLook rigs, `[BB] NetworkManager`, sound-def audio layer |
| `Assets/Shooter` | Weapons, projectiles + pooling, aim rig, HUD, full shoot/reload/strafe/hit-reaction animator |
| `Assets/Platformer` | Locomotion, double jump, moving platforms, jump pads, teleporters |
| `Assets/Blocks` | Multiplayer session UI — create/join/browse, quick join |

The `Shooter` module is the closest starting point for phase 1; `Core`'s ability
and game-event framework is what new stealth and vehicle systems should plug into
rather than bypass.

## Requirements

- Unity **6000.5.0b11**
- Unity Personal license, signed in via Unity Hub

Key packages already present: URP 17.5.0, Cinemachine 3.1.6, Input System 1.19.0,
Netcode for GameObjects 2.11.2, AI Navigation 2.0.12, Animation Rigging 1.4.1,
VFX Graph 17.5.0, and the `vehicles` built-in module.

## Version control

`Library/`, `Temp/`, `Logs/` and `UserSettings/` are gitignored — only `Assets/`,
`Packages/` and `ProjectSettings/` are tracked. Binary art and audio go through
Git LFS (see `.gitattributes`); LFS is configured locally for this repo, so a
fresh clone needs `git lfs pull`.

**Commit `.meta` files.** Unity stores every asset reference by the GUID inside
them; dropping one silently breaks prefab and scene wiring.
