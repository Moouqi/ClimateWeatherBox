# Source modules

## 核心

- `ModEntry.cs`: NeoModLoader entry point and Harmony registration.
- `ModClass.cs`: core climate simulation, weather, biome, freezing, river and
  UI-panel orchestration; tuning constants live at the top of this file.
- `ClimateModels.cs`: climate state, transition snapshots, and river heap data types.
- `ClimateSystem.Coordinates.cs`: latitude/longitude mapping, templates and the
  shared coordinate range that drives sun, climate and wrap logic.
- `ClimateSystem.Topology.cs`: the `HorizontalWrap` flag (full-world longitude span
  enables seamless east-west wrapping).
- `ClimateSystem.LocalTopology.cs`: tile-level land/water/island queries in wrapped
  coordinates, with incremental refresh when terrain changes.
- `ClimateSystem.BiomeSelection.cs`: climate-to-biome core selection with widened
  hysteresis bands.
- `BiomeSuitability.cs`: pure scoring model for biome suitability — deterministic,
  independent of frame rate and Unity state.
- `ClimateSystem.Rivers.cs`: river destination selection, A*, contour-platform sampling,
  four-neighbour reconstruction, queued terrain writes, and river moisture field.
- `RiverMoistureBuilder.cs`: incremental chamfer moisture field around rivers and
  shallow waters, with a periodic halo so the influence stays seamless across the wrap.
- `ClimateSystem.Atmosphere.cs`: 8-tile atmospheric grid; fixed one-second steps,
  double-buffered wind, pressure, air heat exchange, water vapour and cloud transport.
- `ClimateSystem.PressureWind.cs`: seasonal pressure belts, Coriolis deflection,
  terrain flow-around and the moving weather-scale disturbances.
- `ClimateSystem.AtmosphericClouds.cs`: water-vapour cloud field, pressure/wind advection,
  terrain blocking, and cloud-cover moisture feedback.
- `ClimateSystem.TropicalCyclones.cs`: warm-sea
  tropical cyclone spawning, tracking and lightning paths (shared `SpawnLightning` entry).
- `HarmonyPatches.cs`: cloud, tornado, terrain setter, biome, freeze/thaw, era lighting
  and window-light integration patches.

## GPU 后端（CPU 等价实现 + compute 直写显示）

- `GpuCapabilityProbe.cs`: one-shot startup GPU capability diagnostic.
- `ClimateSystem.GpuAtmosphere.cs` / `GpuAtmosphereBackend.cs`: run the atmosphere on
  `ClimateAtmosphere.compute`; the kernel writes the pressure/wind/vapour display
  RenderTexture directly while async readback feeds gameplay. Any invalid cell falls
  back to the last complete CPU snapshot without a visual jump.
- `ClimateSystem.GpuThermal.cs` / `GpuSurfaceThermalBackend.cs`: run surface
  temperature (sun, thermal inertia, atmosphere sync, biome climate memory) on
  `SurfaceThermal.compute`, writing `SurfaceA/B` display textures directly. Readback
  is applied in slices paced by the solar beat; any failure drops the whole GPU path
  back to CPU batch processing and restores CPU-owned textures.
- `ClimateSystem.Fields.cs`: bridge from simulation to rendering. Fills the field
  textures (surface cells, atmosphere grid, light mask) on throttled refreshes,
  restores CPU textures after a GPU fallback, and exposes the astronomical uniforms
  used by the night shader.

## 水平无缝环绕（Horizontal*）

- `HorizontalTopology.cs`: pure wrap math (wrapping deltas, wrapped radius, cell
  dedup, periodic blending) — no Unity or game dependency.
- `HorizontalBrush.cs`: player brush loops collect wrapped, deduplicated tiles.
- `HorizontalGroundPathfinder.cs`: budgeted A* on a cylinder (dictionary-backed
  search area, per-frame shared budget).
- `HorizontalGroundMovement.cs`: actor pathing/movement/force across the seam.
- `HorizontalBoatMovement.cs`: boat routes and shore sampling across the seam;
  taxi/passenger states stay vanilla.
- `HorizontalPossessedMovement.cs`: possessed-unit manual control wraps horizontally;
  bounce, bounds and speed rules unchanged.
- `HorizontalForcedMovement.cs`: forced displacement (storm/god powers) keeps its
  crossing permit only for the same actor instance.
- `HorizontalStormForce.cs` / `HorizontalStormTerrain.cs`: tornado force and
  terraform collect wrapped tiles with dedup before applying side effects.
- `HorizontalCityPlacement.cs`: city build-candidate distance and town-plan passable
  ring checks use wrapped distances (no vanilla filters bypassed).
- `HorizontalBuildingFoundation.cs`: same-island foundation check for new city
  buildings across the seam.
- `HorizontalFarmExpansion.cs`: windmill farm radius wraps at the map edges.
- `HorizontalMineTargets.cs`: fallback mine target search for actors when vanilla
  fails near the seam (rotating start, bounded candidates).
- `HorizontalStorageDelivery.cs`: storage delivery targets across the seam with the
  shared per-frame search budget.
- `HorizontalEquipmentRepair.cs`: equipment-repair building queries wrap; other
  barracks queries untouched.
- `HorizontalDockPlacement.cs`: optional dock-to-shore check patch; a failure only
  disables this patch, never the whole mod.
- `HorizontalTerritoryTopology.cs` / `HorizontalTerritoryPatches.cs`: zone adjacency
  from real edge tiles and claim/route checks across the seam.
- `HorizontalWorkTargets.cs`: per-frame cached worker job target search.
- `SeamEffectCopies.cs`: render-only proxies for effects near the seam — never
  instantiates a `BaseEffect` or copies gameplay scripts.
- `HorizontalSeamPreview.cs`: F9 diagnostic seam preview (no camera teleport, input
  remapping or world copy).
- `HorizontalCameraWrap.cs`: F10 wrap window — presentation-only secondary views;
  never changes `World.GetTile` or unit pathfinding.

## UI 与诊断

- `ClimateUiLayout.cs`: F8 panel layout helpers and native-popup detection.
- `ClimateUiPatches.cs`: patches so the panel does not fight vanilla mouse/UI code.
- `ConstructionDiagnostics.cs`: temporary observation of the construction chain;
  disabled by default and skipped during patch registration.

## World-space rendering (Source/Render)

All map visuals are drawn by world-space renderers on the game's `MapOverlay`
sorting layer (above tiles, units and clouds; night renders last among them):

- `ClimateFieldSet.cs`: the field textures every shader reads —
  `SurfaceA` (temperature, soil/air humidity, rain), `SurfaceB` (cloud cover,
  contour elevation, frozen, sunlight), `Atmos` (pressure, wind, vapour) and
  `LightMask` (building/lava light pass-through). CPU textures are always owned
  here; GPU may take over the public references and hand them back on fallback.
  Uploads are throttled and dirty-flagged; atmosphere textures get a U-axis repeat
  wrap mode when longitude wrapping is on.
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
inside `ModClass.cs`; secondary cameras (F9 seam preview, F10 wrap window) capture
the world-space layers automatically, so no duplicated draw path exists for previews.

New visual layers should be added as a shader mode in `ClimateLayers.shader` plus a
fill in `ClimateSystem.Fields.cs`, not as new IMGUI code.

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
