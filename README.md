# Tilted Worlds

This plugin tilts you by 23.44 degrees.

> Disclaimer: This project was created with LLM assistance.

A BepInEx plugin for Solar Expanse that gives planets, dwarf planets and moons
their real axial tilt, gives moons their real orbital inclination, and puts their
parking orbits into each body's new equatorial plane.

## Notes

- Axial tilt changes the appearance of planets, not the game's climate model.
- The visual solstice drifts slightly over time because the orbital year differs
  from the calendar year. Loading a save recalibrates it.
- `[Orbit]` points and parked ships follow the tilted equatorial planes; together with
  the moon orbital inclinations this changes the delta-v of within-system transfers.
- Asteroid parking orbits are polar around some planets in vanilla and remain so.
- Moon orbital inclinations and nodes use data for 2020-01-01.
  Their changes over time are not modeled.
- Magnetic axes, auroras and magnetospheres use the observed magnetic-axis tilt for
  Earth, Jupiter, Uranus, Neptune and Ganymede — bodies with intrinsic magnetic fields.
  Set `TiltMagneticAxes = false` to disable.
  (Saturn and Mercury also have intrinsic magnetic fields, but their magnetic axes nearly
  coincide with their rotation axes.)

## Magnetospheres and performance

Dynamic magnetospheres are computationally demanding. By default, `Renderer = Auto`
uses the GPU, with a CPU fallback if GPU rendering is unavailable. In that case,
`BepInEx\LogOutput.log` contains this warning, followed by the reason:
```text
[Warning:Tilted Worlds] [magnetosphere] Auto: GPU rendering unavailable; using CPU.
```
You can also select CPU rendering manually with `Renderer = CPU`.
Settings are in `BepInEx\config\com.lazyranma.tiltedworlds.cfg`, created after the first run.

If performance suffers while magnetospheres are visible, try `DynamicFields = false`.
This uses the vanilla shape, rotating it in the plane perpendicular to the Sun's direction
instead of dynamically deforming it. Restart the game after changing settings.

## Installation

The game branch decides the install path. BepInEx is required either way.

### Manual — stable game branch

1. **Install BepInEx** — follow the [BepInEx setup guide](https://docs.bepinex.dev/articles/user_guide/installation/index.html).
2. **Download** — [TiltedWorlds_v1.0.0.zip](https://github.com/lazyranma/SETiltedWorlds/releases/download/v1.0.0/TiltedWorlds_v1.0.0.zip).
3. **Extract plugin** — unzip the archive into the game folder, so the DLL ends up in
   `BepInEx\plugins\TiltedWorlds\`.

### Steam Workshop — experimental game branch

Workshop mod support only exists in the experimental branch; on stable, use the manual path above.

1. **Install BepInEx** — as above.
2. **Subscribe** to [Tilted Worlds](https://steamcommunity.com/sharedfiles/filedetails/?id=3840282977)
   on the Workshop.
3. **Start twice** — subscribed items are only copied into `BepInEx\plugins` when the game exits,
   so launch, quit, and launch again.
