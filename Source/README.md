# Source modules

- `ModEntry.cs`: NeoModLoader entry point and Harmony registration.
- `ClimateModels.cs`: climate state, transition snapshots, and river heap data types.
- `ClimateSystem.Rivers.cs`: river destination selection, A*, contour-platform sampling,
  four-neighbour reconstruction, queued terrain writes, and river moisture field.
- `ClimateSystem.LayerRendering.cs`: climate texture construction and dirty-tile updates.
- `ClimateSystem.Atmosphere.cs`: 8-tile atmospheric grid; fixed one-second steps,
  double-buffered wind, pressure, air heat exchange, water vapour and cloud transport.
- `ClimateSystem.WindTrails.cs`: bounded, short-lived wind particles with bilinear sampling.
- `ClimateSystem.AtmosphericClouds.cs`: water-vapour cloud field, pressure/wind advection,
  terrain blocking, and cloud-cover moisture feedback.
- `HarmonyPatches.cs`: cloud, tornado, boat, terrain, biome, and lighting integration patches.

`ModClass.cs` retains the core climate simulation, weather, biome, freezing, UI, and
astronomical-night orchestration. New features should be added to the matching partial
class module instead of growing the core file again.

## Continuous atmosphere (2026-09)

CPU simulation only; no additional packages or GPU backend. `ClimateCell.Humidity`
is soil moisture for compatibility with biome rules. `RelativeHumidity` is derived
from air temperature and vapour. Cumulative precipitation/evaporation ledgers use
double precision so staggered terrain updates do not lose intermediate rainfall.
Tracked rain entities are visual effects and no longer add a second water source.
F7 cycles soil moisture, air relative humidity, precipitation, and off.

This is a game-scale, single-layer approximation. Pressure relaxes toward seasonal
climatology and responds to divergence; upper-air exchange is parameterized. Heat
transport is pairwise exchange. Existing surface thermal response is retained with
air coupling and cloud shading. Water/precipitation units are not millimetres.
Regional edges allow water outflow; longitude wraps only for full-world maps.
Terrain inputs currently use a representative tile per atmospheric cell, so narrow
coasts and ridges remain sub-grid features. Game-world runtime profiling and visual
calibration are still required; standalone tests do not validate WorldBox rendering.
