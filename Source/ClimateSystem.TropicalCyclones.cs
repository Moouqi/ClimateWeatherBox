using System;
using UnityEngine;

namespace ClimateWeather;

public sealed partial class ClimateSystem
{
    private const float CycloneMinimumSeaTemperatureC = 26f;
    private const float CycloneMinimumLatitudeDegrees = 5f;
    private const float CycloneMaximumLatitudeDegrees = 38f;

    private void TrySpawnPressureDrivenSevereWeather(WorldTile tile, ClimateCell cell,
        int tileCount, float convection)
    {
        if (tile == null || cell == null || !CanSpawnClimateTornado(tileCount)) return;
        bool ocean = tile.main_type?.ocean == true || tile.Type?.layer_type == TileLayerType.Ocean;
        if (ocean)
        {
            TrySpawnTropicalCyclone(tile, cell, tileCount);
            return;
        }

        // 陆地龙卷风需要强对流、低压和较强近地风，避免再由单一高温随机生成。
        float temperatureC = ClimateCelsius(cell.Temperature);
        if (temperatureC < 20f || cell.Humidity < 0.58f || cell.CloudCover < 0.32f ||
            cell.Pressure > 1010f || cell.WindSpeed < 0.34f) return;
        float lowPressure = LowPressureCloudPotential(tile, cell, out float convergence);
        float instability = Mathf.Clamp01(Mathf.InverseLerp(20f, 38f, temperatureC)) *
                            Mathf.Clamp01(convection) * Mathf.Lerp(0.55f, 1.35f, convergence) *
                            Mathf.Lerp(0.55f, 1.30f, lowPressure);
        float chance = 0.0012f * instability * instability * _ageClimateProfile.StormMultiplier;
        if (UnityEngine.Random.value >= chance) return;

        TornadoEffect tornado = EffectsLibrary.spawnAtTile("fx_tornado", tile,
            Mathf.Lerp(0.28f, 0.58f, instability)) as TornadoEffect;
        if (tornado == null) return;
        _tornadoEvents++;
        Debug.Log($"[ClimateWeather] 强对流龙卷风：({tile.pos.x}, {tile.pos.y})，" +
                  $"{temperatureC:F1}°C，{cell.Pressure:F1} hPa，风速 {WindSpeedKmh(cell):F0} km/h。");
    }

    private void TrySpawnTropicalCyclone(WorldTile tile, ClimateCell cell, int tileCount)
    {
        int maximum = Mathf.Clamp(tileCount / 32768, 1, 5);
        if (_activeTropicalCyclones.Count >= maximum) return;
        float latitudeDegrees = Mathf.Abs(GetSignedLatitude(tile.pos.y) * 90f);
        float temperatureC = ClimateCelsius(cell.Temperature);
        if (latitudeDegrees < CycloneMinimumLatitudeDegrees ||
            latitudeDegrees > CycloneMaximumLatitudeDegrees ||
            temperatureC < CycloneMinimumSeaTemperatureC || cell.Humidity < 0.68f ||
            cell.CloudCover < 0.42f || cell.Pressure > 1008f) return;

        float potential = TropicalCyclonePotential(tile, cell, temperatureC, latitudeDegrees,
            out float pressureDeficit, out float rotation);
        if (pressureDeficit < 0.65f || potential < 0.46f) return;
        float seasonalBoost = Mathf.Lerp(0.72f, 1.38f, LocalSummerStrength(tile));
        float chance = 0.0065f * potential * potential * seasonalBoost *
                       _ageClimateProfile.HurricaneMultiplier;
        if (UnityEngine.Random.value >= chance) return;

        float initialIntensity = Mathf.Clamp01(0.22f + potential * 0.48f);
        TornadoEffect effect = EffectsLibrary.spawnAtTile("fx_tornado", tile,
            CycloneVisualScale(initialIntensity)) as TornadoEffect;
        if (effect == null) return;
        TropicalCycloneState state = new TropicalCycloneState
        {
            Intensity = initialIntensity,
            SpawnTime = _seasonClock,
            LastUpdateTime = _seasonClock,
            NextRainTime = Time.time,
            NextCloudTime = Time.time,
            MaximumLifetime = Mathf.Lerp(105f, 220f, potential)
        };
        state.Kind = ClassifyTropicalCyclone(tile, state.Intensity);
        _tropicalCycloneStates.Add(effect, state);
        _activeTropicalCyclones.Add(effect);
        CountNamedCycloneIfNeeded(state);
        Debug.Log($"[ClimateWeather] {CycloneKindName(state.Kind)}生成：({tile.pos.x}, {tile.pos.y})，" +
                  $"海温 {temperatureC:F1}°C，中心气压 {cell.Pressure:F1} hPa，" +
                  $"压差 {pressureDeficit:F1} hPa，旋转度 {rotation:F2}。");
    }

    private float TropicalCyclonePotential(WorldTile tile, ClimateCell cell, float temperatureC,
        float latitudeDegrees, out float pressureDeficit, out float rotation)
    {
        float ringPressure = 0f;
        int samples = 0;
        for (int dx = -6; dx <= 6; dx += 6)
        for (int dy = -6; dy <= 6; dy += 6)
        {
            if (dx == 0 && dy == 0) continue;
            ringPressure += PressureAt(tile.pos.x + dx, tile.pos.y + dy, cell.Pressure);
            samples++;
        }
        pressureDeficit = samples > 0 ? ringPressure / samples - cell.Pressure : 0f;
        rotation = Mathf.Abs(WindVorticityAt(tile.pos.x, tile.pos.y));
        float heat = Mathf.Clamp01(Mathf.InverseLerp(26f, 33f, temperatureC));
        float moisture = Mathf.Clamp01(Mathf.InverseLerp(0.68f, 0.96f, cell.Humidity));
        float clouds = Mathf.Clamp01(Mathf.InverseLerp(0.42f, 0.92f, cell.CloudCover));
        float deficit = Mathf.Clamp01(Mathf.InverseLerp(0.4f, 7f, pressureDeficit));
        float coriolis = Mathf.Clamp01(Mathf.InverseLerp(5f, 18f, latitudeDegrees));
        float spin = Mathf.Clamp01(rotation * 3.2f + cell.WindSpeed * 0.28f);
        return Mathf.Clamp01(heat * 0.25f + moisture * 0.18f + clouds * 0.14f +
                             deficit * 0.25f + coriolis * 0.10f + spin * 0.08f);
    }

    private float WindVorticityAt(int x, int y)
    {
        Vector2 left = WindAt(x - 2, y);
        Vector2 right = WindAt(x + 2, y);
        Vector2 down = WindAt(x, y - 2);
        Vector2 up = WindAt(x, y + 2);
        return (right.y - left.y - (up.x - down.x)) * 0.25f;
    }

    private Vector2 WindAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= MapBox.width || y >= MapBox.height) return Vector2.zero;
        int pixel = y * MapBox.width + x;
        if (pixel < 0 || pixel >= _cellIndexByPixel.Length) return Vector2.zero;
        int index = _cellIndexByPixel[pixel];
        return index >= 0 && index < _cells.Length
            ? _cells[index].Wind * Mathf.Lerp(0.35f, 1f, _cells[index].WindSpeed)
            : Vector2.zero;
    }

    private TropicalCycloneKind ClassifyTropicalCyclone(WorldTile tile, float intensity)
    {
        if (intensity < 0.38f) return TropicalCycloneKind.TropicalDepression;
        if (intensity < 0.58f) return TropicalCycloneKind.TropicalStorm;
        float latitude = GetSignedLatitude(tile.pos.y) * 90f;
        float longitude = LongitudeDegreesAt(tile.pos.x);
        // 西北太平洋称台风；北大西洋和东北太平洋称飓风；其余海域称热带气旋。
        if (latitude >= 0f && longitude >= 100f && longitude <= 180f)
            return TropicalCycloneKind.Typhoon;
        if (latitude >= 0f && ((longitude >= -100f && longitude <= 20f) || longitude <= -100f))
            return TropicalCycloneKind.Hurricane;
        return TropicalCycloneKind.TropicalCyclone;
    }

    private static string CycloneKindName(TropicalCycloneKind kind)
    {
        switch (kind)
        {
            case TropicalCycloneKind.TropicalDepression: return "热带低压";
            case TropicalCycloneKind.TropicalStorm: return "热带风暴";
            case TropicalCycloneKind.Typhoon: return "台风";
            case TropicalCycloneKind.Hurricane: return "飓风";
            default: return "热带气旋";
        }
    }

    private static float CycloneVisualScale(float intensity) => Mathf.Lerp(0.20f, 0.82f, intensity);

    private void UpdateTropicalCyclones()
    {
        for (int i = _activeTropicalCyclones.Count - 1; i >= 0; i--)
        {
            TornadoEffect effect = _activeTropicalCyclones[i];
            if (effect == null || effect.state != 1 ||
                !_tropicalCycloneStates.TryGetValue(effect, out TropicalCycloneState state))
            {
                if (effect != null) _tropicalCycloneStates.Remove(effect);
                _activeTropicalCyclones.RemoveAt(i);
                continue;
            }
            if (_seasonClock - state.LastUpdateTime < 0.75f) continue;
            float elapsed = Mathf.Clamp(_seasonClock - state.LastUpdateTime, 0f, 3f);
            state.LastUpdateTime = _seasonClock;
            WorldTile tile = effect.current_tile ?? effect.tile;
            if (tile == null || !TryGetClimate(tile, out ClimateCell cell))
            {
                effect.die();
                continue;
            }

            bool ocean = tile.main_type?.ocean == true || tile.Type?.layer_type == TileLayerType.Ocean;
            float temperatureC = ClimateCelsius(cell.Temperature);
            float latitudeDegrees = Mathf.Abs(GetSignedLatitude(tile.pos.y) * 90f);
            float support = ocean
                ? TropicalCyclonePotential(tile, cell, temperatureC, latitudeDegrees, out _, out _)
                : 0f;
            float tendency = ocean && temperatureC >= 24f
                ? (support - 0.43f) * 0.055f
                : -0.085f;
            if (cell.Pressure > 1012f || cell.Humidity < 0.55f) tendency -= 0.035f;
            state.Intensity = Mathf.Clamp01(state.Intensity + tendency * elapsed);
            if (state.Intensity < 0.10f || _seasonClock - state.SpawnTime > state.MaximumLifetime)
            {
                effect.die();
                continue;
            }

            effect._shrink_timer = 8f;
            effect.resizeTornado(CycloneVisualScale(state.Intensity));
            SpawnTropicalCycloneRainBand(tile, state);
            TropicalCycloneKind previous = state.Kind;
            state.Kind = ClassifyTropicalCyclone(tile, state.Intensity);
            CountNamedCycloneIfNeeded(state);
            if (previous != state.Kind)
                Debug.Log($"[ClimateWeather] 热带气旋已调整为{CycloneKindName(state.Kind)}，" +
                          $"位置 ({tile.pos.x}, {tile.pos.y})，强度 {state.Intensity:P0}。");
        }
    }

    private void CountNamedCycloneIfNeeded(TropicalCycloneState state)
    {
        if (state.CountedNamedStorm || state.Intensity < 0.58f) return;
        state.CountedNamedStorm = true;
        switch (state.Kind)
        {
            case TropicalCycloneKind.Typhoon: _typhoonEvents++; break;
            case TropicalCycloneKind.Hurricane: _hurricaneEvents++; break;
            default: _tropicalCycloneEvents++; break;
        }
    }

    internal bool IsTrackedTropicalCyclone(TornadoEffect effect) =>
        effect != null && _tropicalCycloneStates.TryGetValue(effect, out _);

    internal bool TryGetTropicalCycloneTarget(TornadoEffect effect, WorldTile tile,
        out WorldTile target)
    {
        target = null;
        if (!IsTrackedTropicalCyclone(effect) || tile == null ||
            !TryGetClimate(tile, out ClimateCell center)) return false;
        Vector2 steering = center.Wind.sqrMagnitude > 0.001f ? center.Wind.normalized : Vector2.right;
        float bestScore = float.NegativeInfinity;
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        {
            if (dx == 0 && dy == 0) continue;
            Vector2 direction = new Vector2(dx, dy).normalized;
            int x = Mathf.Clamp(tile.pos.x + dx * 7, 0, MapBox.width - 1);
            int y = Mathf.Clamp(tile.pos.y + dy * 7, 0, MapBox.height - 1);
            WorldTile candidate = MapBox.instance.GetTileSimple(x, y);
            if (candidate == null || !TryGetClimate(candidate, out ClimateCell candidateCell)) continue;
            float windAlignment = Vector2.Dot(direction, steering);
            float pressureFall = Mathf.Clamp((center.Pressure - candidateCell.Pressure) / 5f, -1f, 1f);
            float score = windAlignment * 0.72f + pressureFall * 0.42f +
                          UnityEngine.Random.Range(-0.08f, 0.08f);
            if (score <= bestScore) continue;
            bestScore = score;
            target = candidate;
        }
        return target != null;
    }

    internal void ApplyTropicalCycloneRain(TornadoEffect effect, WorldTile tile)
    {
        if (effect == null || tile == null ||
            !_tropicalCycloneStates.TryGetValue(effect, out TropicalCycloneState state) ||
            Time.time < state.NextRainTime) return;
        state.NextRainTime = Time.time + 0.8f;
        AddGroundMoisture(tile, Mathf.Lerp(0.018f, 0.050f, state.Intensity));
        if (TryGetClimate(tile, out ClimateCell center))
        {
            center.CloudCover = Mathf.Min(1f, center.CloudCover + 0.035f * state.Intensity);
            if (state.Intensity > 0.62f && UnityEngine.Random.value < 0.08f * state.Intensity)
            {
                MapBox.spawnLightningSmall(tile, 0.18f + state.Intensity * 0.18f, null);
                _stormEvents++;
            }
        }
        if (tile.neighbours == null) return;
        for (int i = 0; i < tile.neighbours.Length; i++)
            AddGroundMoisture(tile.neighbours[i], Mathf.Lerp(0.008f, 0.022f, state.Intensity));
    }

    private void SpawnTropicalCycloneRainBand(WorldTile center, TropicalCycloneState state)
    {
        if (center == null || state == null || Time.time < state.NextCloudTime) return;
        state.NextCloudTime = Time.time + Mathf.Lerp(7f, 3.5f, state.Intensity);
        int maximumClouds = Mathf.Clamp(_cells.Length / 1024, 24, 72);
        if (_activePrecipitationClouds.Count >= maximumClouds) return;
        int count = state.Intensity >= 0.68f ? 2 : 1;
        count = Math.Min(count, maximumClouds - _activePrecipitationClouds.Count);
        for (int i = 0; i < count; i++)
        {
            WorldTile cloudTile = NearbyWeatherTile(center, i == 0 ? 4 : 7);
            string cloudType = PrecipitationCloudType(cloudTile);
            Cloud cloud = EffectsLibrary.spawn("fx_cloud", cloudTile, cloudType) as Cloud;
            if (cloud == null) continue;
            cloud.setLifespan(Mathf.Lerp(18f, 36f, state.Intensity));
            RegisterRainCloud(cloud);
            _rainEvents++;
        }
    }

    private void ClearTrackedTropicalCyclones()
    {
        for (int i = 0; i < _activeTropicalCyclones.Count; i++)
        {
            TornadoEffect effect = _activeTropicalCyclones[i];
            if (effect != null) _tropicalCycloneStates.Remove(effect);
        }
        _activeTropicalCyclones.Clear();
    }
}
