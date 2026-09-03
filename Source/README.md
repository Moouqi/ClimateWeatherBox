# Source modules

- `ModEntry.cs`: NeoModLoader entry point and Harmony registration.
- `ClimateModels.cs`: climate state, transition snapshots, and river heap data types.
- `ClimateSystem.Rivers.cs`: river destination selection, A*, contour-platform sampling,
  four-neighbour reconstruction, queued terrain writes, and river moisture field.
- `ClimateSystem.LayerRendering.cs`: climate texture construction and dirty-tile updates.
- `ClimateSystem.AtmosphericClouds.cs`: water-vapour cloud field, pressure/wind advection,
  terrain blocking, and cloud-cover moisture feedback.
- `HarmonyPatches.cs`: cloud, tornado, boat, terrain, biome, and lighting integration patches.

`ModClass.cs` retains the core climate simulation, weather, biome, freezing, UI, and
astronomical-night orchestration. New features should be added to the matching partial
class module instead of growing the core file again.
