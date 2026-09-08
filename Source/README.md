# Source modules

- `ModEntry.cs`: NeoModLoader entry point and Harmony registration.
- `ClimateModels.cs`: climate state, transition snapshots, and river heap data types.
- `ClimateSystem.Rivers.cs`: river destination selection, A*, contour-platform sampling,
  four-neighbour reconstruction, queued terrain writes, and river moisture field.
- `ClimateSystem.Atmosphere.cs`: 8-tile atmospheric grid; fixed one-second steps,
  double-buffered wind, pressure, air heat exchange, water vapour and cloud transport.
- `ClimateSystem.AtmosphericClouds.cs`: water-vapour cloud field, pressure/wind advection,
  terrain blocking, and cloud-cover moisture feedback.
- `ClimateSystem.Fields.cs`: bridge from simulation to rendering. Fills the field
  textures (surface cells, atmosphere grid, light mask) on throttled refreshes and
  exposes the astronomical uniforms used by the night shader.
- `HarmonyPatches.cs`: cloud, tornado, boat, terrain, biome, and lighting integration patches.

## World-space rendering (Source/Render)

All map visuals are drawn by three world-space renderers on the game's `MapOverlay`
sorting layer (above tiles, units and clouds; night renders last among them):

- `ClimateFieldSet.cs`: the field textures every shader reads —
  `SurfaceA` (temperature, soil/air humidity, rain), `SurfaceB` (cloud cover,
  contour elevation, frozen, sunlight), `Atmos` (pressure, wind, vapour) and
  `LightMask` (building/lava light pass-through). Uploads are throttled and dirty-flagged.
- `ClimateLayerRenderer.cs`: night quad (astronomical darkness computed per pixel
  in the shader from solar declination/subsolar longitude uniforms), data-layer quad
  (one shader, seven modes including in-shader contour bands and bilinear isobars),
  and the line mesh. Gating by popups is a plain enable/disable of these objects.
- `ClimateLineMesh.cs`: wind trail particles, latitude/longitude grid, pressure-belt
  guides and the subsolar crosshair in a single dynamic vertex-coloured mesh.
- `ClimateShaderAssets.cs`: locates and loads the `Gpu/climatelayers` AssetBundle
  (built from `.UnityProject/` with Unity 2022.3.60f1 via `build-shaders.ps1`) and
  resolves the legacy GPU bundles (`climateatmosphere`, `climatecompute`) by scanning
  the Mods directory, so renamed mod folders keep working.

Text-only overlays (legend, latitude labels, belt names, hover tooltip) stay in IMGUI
inside `ModClass.cs`; secondary cameras (F9 seam preview, F10 wrap window) now capture
the world-space layers automatically, so no duplicated draw path exists for previews.

`ModClass.cs` retains the core climate simulation, weather, biome, freezing, river and
UI-panel orchestration. New visual layers should be added as a shader mode in
`ClimateLayers.shader` plus a fill in `ClimateSystem.Fields.cs`, not as new IMGUI code.

## Continuous atmosphere (2026-09)

CPU simulation with a GPU compute backend (`ClimateAtmosphere.compute`); both implement
the same step, and the GPU kernel additionally writes the pressure/wind/vapour display
RenderTexture directly, so the isobar layer needs no CPU readback. Readback only feeds
gameplay (biomes, weather, cloud spawning). Any invalid GPU cell automatically falls
back to the last complete CPU snapshot and CPU texture uploads. `ClimateCell.Humidity`
is soil moisture for compatibility with biome rules. `RelativeHumidity` is derived
from air temperature and vapour. Cumulative precipitation/evaporation ledgers use
double precision on CPU so staggered terrain updates do not lose intermediate rainfall.
Tracked rain entities are visual effects and no longer add a second water source. F7
cycles soil moisture, air relative humidity, precipitation, and off.

This is a game-scale, single-layer approximation. Pressure relaxes toward seasonal
climatology and responds to divergence; upper-air exchange is parameterized. Heat
transport is pairwise exchange. Water/precipitation units are not millimetres.
Regional edges allow water outflow; longitude wraps only for full-world maps.
Terrain inputs currently use a representative tile per atmospheric cell, so narrow
coasts and ridges remain sub-grid features.
