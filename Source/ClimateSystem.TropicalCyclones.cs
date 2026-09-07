using System;
using UnityEngine;

namespace ClimateWeather;

public sealed partial class ClimateSystem
{
    private const float CycloneMinimumSeaTemperatureC = 26f;
    private const float CycloneMinimumLatitudeDegrees = 5f;
    private const float CycloneMaximumLatitudeDegrees = 38f;
    private int _cycloneTotalEvents, _depressionEvents, _tropicalStormEvents;
    private readonly ClimateCell _severeSnapshot = new ClimateCell();
    private float _nextSevereSpawn;
    private float _nextSevereDiagnostic;
    private int _severeChecks, _severeSeaCandidates, _severeLandCandidates;

    private ClimateCell SevereSnapshot(WorldTile tile, ClimateCell ground)
    {
        AirState air = SampleAir(tile.pos.x, tile.pos.y);
        _severeSnapshot.Temperature = ground.Temperature;
        _severeSnapshot.Humidity = ground.Humidity;
        _severeSnapshot.RelativeHumidity = Mathf.Clamp01(air.Vapor / SaturationWater(air.Temperature));
        _severeSnapshot.CloudCover = 1f - Mathf.Exp(-Mathf.Max(0f, air.Cloud) * 8f);
        _severeSnapshot.Pressure = air.Pressure;
        _severeSnapshot.WindSpeed = Mathf.Clamp01(air.Velocity.magnitude);
        _severeSnapshot.Wind = air.Velocity.normalized;
        return _severeSnapshot;
    }

    private void CheckSevereWeatherCandidates(WorldTile[] tiles)
    {
        if (!AtmosphereReady || _seasonClock < _nextSevereSpawn || !CanSpawnClimateTornado(tiles.Length)) return;
        int sea = -1, land = -1;
        float seaScore = -1f, landScore = -1f;
        // Bounded work, independent of rain-cloud lottery; retain one candidate
        // per environment rather than increasing trials with map size.
        for (int n = 0; n < 64; n++)
        {
            int i = UnityEngine.Random.Range(0, tiles.Length);
            WorldTile tile = tiles[i];
            if (tile?.Type == null) continue;
            ClimateCell c = SevereSnapshot(tile, _cells[i]);
            bool ocean = tile.main_type?.ocean == true || tile.Type.layer_type == TileLayerType.Ocean;
            float t = ClimateCelsius(c.Temperature);
            float lat = Mathf.Abs(GetSignedLatitude(tile.pos.y) * 90f);
            if (ocean && (t < 26f || lat < 5f || lat > 38f)) continue;
            if (!ocean && t < 20f) continue;
            if (c.RelativeHumidity < .60f || c.CloudCover < .20f || c.Pressure > 1012f) continue;
            float score = c.RelativeHumidity + c.CloudCover + Mathf.Clamp01((1012f - c.Pressure) / 12f);
            if (ocean && score > seaScore) { sea = i; seaScore = score; }
            if (!ocean && score > landScore) { land = i; landScore = score; }
        }
        for (int n = 0; n < 2; n++)
        {
            int i = n == 0 ? sea : land;
            if (i < 0 || _seasonClock < _nextSevereSpawn) continue;
            ClimateCell c = SevereSnapshot(tiles[i], _cells[i]);
            TrySpawnPressureDrivenSevereWeather(tiles[i], c, tiles.Length, c.RelativeHumidity);
        }
        _severeChecks++;
        if (sea >= 0) _severeSeaCandidates++;
        if (land >= 0) _severeLandCandidates++;
        if (_seasonClock >= _nextSevereDiagnostic)
        {
            _nextSevereDiagnostic = _seasonClock + 60f;
            Debug.Log($"[ClimateWeather] 强天气候选检查：批次 {_severeChecks}，暖海 {_severeSeaCandidates}，陆地 {_severeLandCandidates}，活跃气旋 {ActiveCycloneCount()}（候选不代表通过全部生成条件）。");
            _severeChecks = _severeSeaCandidates = _severeLandCandidates = 0;
        }
    }

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
        if (temperatureC < 20f || cell.RelativeHumidity < 0.60f || cell.CloudCover < 0.25f ||
            cell.Pressure > 1012f || cell.WindSpeed < 0.25f) return;
        float lowPressure = Mathf.InverseLerp(1012f, 998f, cell.Pressure);
        float convergence = Mathf.Clamp01(SevereConvergence(tile) * 4f);
        float instability = Mathf.Clamp01(Mathf.InverseLerp(20f, 38f, temperatureC)) *
                            Mathf.Clamp01(convection) * Mathf.Lerp(0.55f, 1.35f, convergence) *
                            Mathf.Lerp(0.55f, 1.30f, lowPressure);
        float chance = 1f - Mathf.Exp(-0.08f * instability * instability * _ageClimateProfile.StormMultiplier);
        if (UnityEngine.Random.value >= chance) return;

        TornadoEffect tornado = EffectsLibrary.spawnAtTile("fx_tornado", tile,
            Mathf.Lerp(0.28f, 0.58f, instability)) as TornadoEffect;
        if (tornado == null) return;
        _tornadoEvents++;
        _nextSevereSpawn = _seasonClock + 30f;
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
            temperatureC < CycloneMinimumSeaTemperatureC || cell.RelativeHumidity < 0.65f ||
            cell.CloudCover < 0.25f || cell.Pressure > 1012f) return;

        float potential = TropicalCyclonePotential(tile, cell, temperatureC, latitudeDegrees,
            out float pressureDeficit, out float rotation);
        if (pressureDeficit < 0.20f || potential < 0.40f) return;
        foreach (TornadoEffect active in _activeTropicalCyclones)
        {
            WorldTile center = active == null ? null : active.current_tile ?? active.tile;
            if (center == null) continue;
            float dx=HorizontalWrap ? HorizontalTopology.Delta(center.x,tile.x,MapBox.width) : tile.x-center.x;
            float dy=tile.y-center.y;
            if (dx*dx+dy*dy < AtmosphereStride*AtmosphereStride*64) return;
        }
        float seasonalBoost = Mathf.Lerp(0.72f, 1.38f, LocalSummerStrength(tile));
        float chance = 1f - Mathf.Exp(-0.18f * potential * potential * seasonalBoost *
                       _ageClimateProfile.HurricaneMultiplier);
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
        _cycloneTotalEvents++;
        _nextSevereSpawn = _seasonClock + 30f;
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
        int radius = AtmosphereStride * 4;
        for (int dx = -radius; dx <= radius; dx += radius)
        for (int dy = -radius; dy <= radius; dy += radius)
        {
            if (dx == 0 && dy == 0) continue;
            int x = tile.pos.x + dx, y = tile.pos.y + dy;
            if (y < 0 || y >= MapBox.height ||
                (_longitudeMaxDegrees - _longitudeMinDegrees < 359f && (x < 0 || x >= MapBox.width))) continue;
            ringPressure += SampleAir(x, y).Pressure;
            samples++;
        }
        pressureDeficit = samples > 0 ? ringPressure / samples - cell.Pressure : 0f;
        rotation = Mathf.Abs(WindVorticityAt(tile.pos.x, tile.pos.y));
        float heat = Mathf.Clamp01(Mathf.InverseLerp(26f, 33f, temperatureC));
        float moisture = Mathf.Clamp01(Mathf.InverseLerp(0.60f, 0.95f, cell.RelativeHumidity));
        float clouds = Mathf.Clamp01(Mathf.InverseLerp(0.20f, 0.75f, cell.CloudCover));
        float deficit = Mathf.Clamp01(Mathf.InverseLerp(0.1f, 3f, pressureDeficit));
        float coriolis = Mathf.Clamp01(Mathf.InverseLerp(5f, 18f, latitudeDegrees));
        float spin = Mathf.Clamp01(rotation * 3.2f + cell.WindSpeed * 0.28f);
        return Mathf.Clamp01(heat * 0.25f + moisture * 0.18f + clouds * 0.14f +
                             deficit * 0.25f + coriolis * 0.10f + spin * 0.08f);
    }

    private float WindVorticityAt(int x, int y)
    {
        Vector2 left = SampleAir(x - AtmosphereStride, y).Velocity;
        Vector2 right = SampleAir(x + AtmosphereStride, y).Velocity;
        Vector2 down = SampleAir(x, y - AtmosphereStride).Velocity;
        Vector2 up = SampleAir(x, y + AtmosphereStride).Velocity;
        return (right.y - left.y - (up.x - down.x)) * 0.25f;
    }

    private float SevereConvergence(WorldTile tile)
    {
        int x = tile.pos.x, y = tile.pos.y, r = AtmosphereStride;
        return -(SampleAir(x+r,y).Velocity.x - SampleAir(x-r,y).Velocity.x +
            SampleAir(x,y+r).Velocity.y - SampleAir(x,y-r).Velocity.y) * .5f;
    }

    private Vector2 WindAt(int x, int y)
    {
        if (!HorizontalTopology.NormalizeCell(ref x,y,MapBox.width,MapBox.height,HorizontalWrap)) return Vector2.zero;
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
            TropicalCycloneState state = null;
            if (!ReferenceEquals(effect, null)) _tropicalCycloneStates.TryGetValue(effect, out state);
            if (effect == null || effect.state != 1 || state == null)
            {
                LogCycloneExit(effect, state, "特效结束或被外部移除（具体原因未知）");
                if (!ReferenceEquals(effect, null)) _tropicalCycloneStates.Remove(effect);
                _activeTropicalCyclones.RemoveAt(i);
                continue;
            }
            if (_seasonClock - state.LastUpdateTime < 0.75f) continue;
            float elapsed = Mathf.Clamp(_seasonClock - state.LastUpdateTime, 0f, 3f);
            state.LastUpdateTime = _seasonClock;
            WorldTile tile = effect.current_tile ?? effect.tile;
            if (tile == null || !TryGetClimate(tile, out ClimateCell cell))
            {
                LogCycloneExit(effect, state, "位置或气候数据不可用");
                effect.die();
                continue;
            }

            bool ocean = tile.main_type?.ocean == true || tile.Type?.layer_type == TileLayerType.Ocean;
            if (AtmosphereReady) cell = SevereSnapshot(tile, cell);
            float temperatureC = ClimateCelsius(cell.Temperature);
            float latitudeDegrees = Mathf.Abs(GetSignedLatitude(tile.pos.y) * 90f);
            float support = ocean
                ? TropicalCyclonePotential(tile, cell, temperatureC, latitudeDegrees, out _, out _)
                : 0f;
            float tendency = ocean && temperatureC >= 24f
                ? (support - 0.43f) * 0.055f
                : -0.085f;
            if (cell.Pressure > 1014f || cell.RelativeHumidity < 0.55f) tendency -= 0.035f;
            state.Intensity = Mathf.Clamp01(state.Intensity + tendency * elapsed);
            if (state.Intensity < 0.10f || _seasonClock - state.SpawnTime > state.MaximumLifetime)
            {
                string reason = state.Intensity < .10f
                    ? (!ocean ? "登陆后衰减" : temperatureC < 24f ? "海温不足，强度衰减" : "环境支持不足，强度衰减")
                    : "达到生命周期上限";
                LogCycloneExit(effect, state, reason + $"；温度 {temperatureC:F1}°C，空气湿度 {cell.RelativeHumidity:P0}，气压 {cell.Pressure:F1} hPa");
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
        // Count each stage once per system; weakening/reintensification never
        // subtracts from history or counts the same stage a second time.
        if (state.Kind == TropicalCycloneKind.TropicalDepression && !state.CountedDepression)
        { state.CountedDepression = true; _depressionEvents++; }
        if (state.Kind == TropicalCycloneKind.TropicalStorm && !state.CountedStorm)
        { state.CountedStorm = true; _tropicalStormEvents++; }
        if (state.CountedNamedStorm || state.Intensity < 0.58f) return;
        state.CountedNamedStorm = true;
        switch (state.Kind)
        {
            case TropicalCycloneKind.Typhoon: _typhoonEvents++; break;
            case TropicalCycloneKind.Hurricane: _hurricaneEvents++; break;
            default: _tropicalCycloneEvents++; break;
        }
    }

    private void LogCycloneExit(TornadoEffect effect, TropicalCycloneState state, string reason)
    {
        if (state == null || state.ExitLogged) return;
        state.ExitLogged = true;
        WorldTile tile = effect == null ? null : effect.current_tile ?? effect.tile;
        string position = tile == null ? "未知" : $"({tile.pos.x}, {tile.pos.y})";
        Debug.Log($"[ClimateWeather] {CycloneKindName(state.Kind)}退出：{reason}；位置 {position}，强度 {state.Intensity:P0}。");
    }

    private int ActiveCycloneCount()
    {
        int count = 0;
        foreach (TornadoEffect effect in _activeTropicalCyclones)
            if (effect != null && effect.state == 1 && _tropicalCycloneStates.TryGetValue(effect, out var state) && !state.ExitLogged) count++;
        return count;
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
            int x = tile.pos.x + dx * 7;
            x=HorizontalWrap ? HorizontalTopology.Wrap(x,MapBox.width) : Mathf.Clamp(x,0,MapBox.width-1);
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
        if (HorizontalWrap)
        {
            var visited=new System.Collections.Generic.HashSet<int> { tile.y*MapBox.width+tile.x };
            for (int oy=-1;oy<=1;oy++)
            for (int ox=-1;ox<=1;ox++)
            {
                int x=tile.x+ox,y=tile.y+oy;
                if (!HorizontalTopology.NormalizeCell(ref x,y,MapBox.width,MapBox.height,true) ||
                    !visited.Add(y*MapBox.width+x)) continue;
                AddGroundMoisture(World.world.GetTileSimple(x,y),Mathf.Lerp(0.008f,0.022f,state.Intensity));
            }
            return;
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
        _nextSevereSpawn = 0f;
        _nextSevereDiagnostic = 60f;
        _severeChecks = _severeSeaCandidates = _severeLandCandidates = 0;
        for (int i = 0; i < _activeTropicalCyclones.Count; i++)
        {
            TornadoEffect effect = _activeTropicalCyclones[i];
            if (!ReferenceEquals(effect, null) && _tropicalCycloneStates.TryGetValue(effect, out var state))
                LogCycloneExit(effect, state, "世界重新初始化，停止追踪");
            if (effect != null) _tropicalCycloneStates.Remove(effect);
        }
        _activeTropicalCyclones.Clear();
    }
}
