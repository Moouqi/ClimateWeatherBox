using UnityEngine;

namespace ClimateWeather;

public sealed partial class ClimateSystem
{
    private float CalculatePressure(WorldTile tile, ClimateCell cell)
    {
        if (tile == null || cell == null) return 1013.25f;
        float signedLatitude = GetSignedLatitude(tile.pos.y);
        float latitude = Mathf.Abs(signedLatitude);
        float latitudeDegrees = signedLatitude * 90f;
        float temperatureC = cell.Temperature * 100f - 50f;
        bool ocean = tile.main_type?.ocean == true || tile.Type?.layer_type == TileLayerType.Ocean;

        // 以海平面气压表示天气系统，避免山顶因高度导致的静力低压反过来把
        // 近地风吸向山体。地形对风的影响在风向计算中单独处理。
        float declinationDegrees = CurrentSolarDeclinationRadians() * Mathf.Rad2Deg;
        float seasonalPhase = Mathf.Clamp(declinationDegrees / 23.44f, -1f, 1f);
        float itczLatitude = declinationDegrees * 0.65f;
        float beltShift = declinationDegrees * 0.24f;
        float subpolarShift = declinationDegrees * 0.14f;
        float equatorialLow = PressureBelt(latitudeDegrees, itczLatitude, 17f) * -9f;
        float subtropicalHigh =
            PressureBelt(latitudeDegrees, 30f + beltShift, 15f) * 9.5f +
            PressureBelt(latitudeDegrees, -30f + beltShift, 15f) * 9.5f;
        float subpolarLow =
            PressureBelt(latitudeDegrees, 60f + subpolarShift, 17f) * -9f +
            PressureBelt(latitudeDegrees, -60f + subpolarShift, 17f) * -9f;

        // 冬半球冷空气下沉更强，夏半球极地高压减弱。
        float northPolarStrength = 8f - seasonalPhase * 3.5f;
        float southPolarStrength = 8f + seasonalPhase * 3.5f;
        float polarHigh = Mathf.Clamp01((latitudeDegrees - 72f) / 18f) * northPolarStrength +
                          Mathf.Clamp01((-latitudeDegrees - 72f) / 18f) * southPolarStrength;

        // 移动的天气尺度扰动形成非纬向、随时间演化的高低压中心；海洋上的
        // 天气系统范围和振幅略大，大陆受地表摩擦与热力破碎影响更零散。
        float weatherTime = _seasonClock * 0.0024f;
        float synopticNoise = ClimateNoise(tile.pos.x, tile.pos.y, 0.004f,
            _worldSeed * 0.0017f - weatherTime,
            -_worldSeed * 0.0011f + weatherTime * 0.37f) - 0.5f;
        float synoptic = synopticNoise * (ocean ? 2f : 1.5f);

        // 使用相对当地季节常态的温度异常，而非绝对温度，避免把寒冷极地
        // 永久重复计算成超强高压。暖异常形成热低压，冷异常形成冷高压。
        float yearPhase = (_seasonClock / (SeasonSeconds * 4f)) * Mathf.PI * 2f;
        float zonalReferenceC = -45f + 39f * (1f - Mathf.Pow(latitude, 1.30f)) +
                                _greenhouseGasLevel * 33f +
                                Mathf.Sin(yearPhase) * Mathf.Sign(signedLatitude) *
                                Mathf.Pow(latitude, 0.80f) * 16f +
                                (SurfaceSolarHeatTransmission() - 1f) * 18f +
                                _ageClimateProfile.TemperatureOffsetC;
        float temperatureAnomaly = Mathf.Clamp(temperatureC - zonalReferenceC, -22f, 22f);
        float thermalAnomalyPressure = -temperatureAnomaly * (ocean ? 0.24f : 0.43f);

        // 海陆热力差异：夏季大陆低压、冬季大陆高压；海洋热惯性大，形成
        // 相反但较弱的季风压差。沿海地块在两者之间连续过渡。
        float localSummer = seasonalPhase * Mathf.Sign(signedLatitude);
        if (Mathf.Abs(signedLatitude) < 0.08f) localSummer *= latitude / 0.08f;
        float oceanInfluence = OceanInfluence(tile, ocean);
        float landInfluence = 1f - oceanInfluence;
        float seasonalLandSea = localSummer *
            (oceanInfluence * Mathf.Lerp(1.5f, 4.5f, latitude) -
             landInfluence * Mathf.Lerp(3.5f, 9f, latitude));

        // 湿空气、深厚云层和凝结潜热共同支持低压；幅度保持有限，防止云量
        // 与气压形成不可逆的自激反馈。
        float moistureLow = 0f;
        float cloudLow = 0f;
        return Mathf.Clamp(1013.25f + equatorialLow + subtropicalHigh + subpolarLow +
                           polarHigh + synoptic + thermalAnomalyPressure + seasonalLandSea +
                           moistureLow + cloudLow, 968f, 1052f);
    }

    private static float PressureBelt(float latitudeDegrees, float centerDegrees, float halfWidthDegrees)
    {
        float distance = Mathf.Abs(latitudeDegrees - centerDegrees);
        return 1f - Mathf.SmoothStep(0f, 1f,
            Mathf.Clamp01(distance / Mathf.Max(1f, halfWidthDegrees)));
    }

    private float OceanInfluence(WorldTile tile, bool ocean)
    {
        if (ocean) return 1f;
        if (tile == null) return 0f;
        int water = 0;
        int valid = 0;
        for (int i = 0; i < ClimateNeighbourCount(tile); i++)
        {
            WorldTile neighbour = ClimateNeighbour(tile, i);
            if (neighbour?.Type == null) continue;
            valid++;
            if (neighbour.main_type?.ocean == true ||
                neighbour.Type.layer_type == TileLayerType.Ocean) water++;
        }
        return valid == 0 ? 0f : water / (float)valid * 0.55f;
    }

    private void UpdatePressure(WorldTile tile, ClimateCell cell, bool immediate)
    {
        if (tile == null || cell == null) return;
        float target = CalculatePressure(tile, cell);
        float previous = cell.Pressure;
        float elapsed = cell.LastPressureTime < 0f
            ? TickSeconds
            : Mathf.Clamp(_seasonClock - cell.LastPressureTime, 0f, 120f);
        cell.LastPressureTime = _seasonClock;
        bool ocean = tile.main_type?.ocean == true || tile.Type?.layer_type == TileLayerType.Ocean;
        float responseSeconds = ocean ? 115f : 48f;
        float response = 1f - Mathf.Exp(-elapsed / responseSeconds);
        cell.Pressure = immediate || previous <= 0f ? target : Mathf.Lerp(previous, target, response);
    }

    private float PressureAt(int x, int y, float fallback)
    {
        if (x < 0 || y < 0 || x >= MapBox.width || y >= MapBox.height) return fallback;
        int pixel = y * MapBox.width + x;
        if (pixel < 0 || pixel >= _cellIndexByPixel.Length) return fallback;
        int index = _cellIndexByPixel[pixel];
        return index >= 0 && index < _cells.Length && _cells[index].Pressure > 0f
            ? _cells[index].Pressure
            : fallback;
    }

    private void CalculateWind(WorldTile tile, ClimateCell cell, bool immediate)
    {
        // GPU 地表温度管线接管时，风/气压/云量由内核按节拍同步进 cells；
        // 这里再用过期的 CPU 大气快照同步只会覆盖较新的值。
        if (SurfaceThermalActive) return;
        if (AtmosphereReady && tile != null && cell != null)
        {
            SyncAtmosphere(tile, cell, 0f);
            return;
        }
        if (tile == null || cell == null || _cellIndexByPixel.Length == 0) return;
        UpdatePressure(tile, cell, immediate);
        float center = cell.Pressure;
        float left = PressureAt(tile.pos.x - 1, tile.pos.y, center);
        float right = PressureAt(tile.pos.x + 1, tile.pos.y, center);
        float down = PressureAt(tile.pos.x, tile.pos.y - 1, center);
        float up = PressureAt(tile.pos.x, tile.pos.y + 1, center);

        // 负气压梯度：分量始终由较高气压指向较低气压。
        Vector2 pressureGradient = new Vector2(left - right, down - up);
        Vector2 pressureFlow = pressureGradient * 1.35f;
        float signedLatitude = GetSignedLatitude(tile.pos.y);

        // 科里奥利力只偏转压差风，不反转其从高压指向低压的分量。
        // 北半球向运动方向右偏（顺时针），南半球向左偏（逆时针）。
        float turnRadians = -signedLatitude * 24f * Mathf.Deg2Rad;
        float cos = Mathf.Cos(turnRadians);
        float sin = Mathf.Sin(turnRadians);
        Vector2 deflectedPressure = new Vector2(
            pressureFlow.x * cos - pressureFlow.y * sin,
            pressureFlow.x * sin + pressureFlow.y * cos);

        // 三圈环流提供现实纬向风带：信风趋向赤道，西风趋向高纬，
        // 极地东风趋向副极地低压。边界采用平滑混合，避免 30°/60°突变。
        Vector2 circulation = GlobalWindBelt(signedLatitude, out float beltStrength) * beltStrength;

        float elevation = GetTerrainElevation(tile);
        float elevationLeft = TerrainElevationAt(tile.pos.x - 1, tile.pos.y, elevation);
        float elevationRight = TerrainElevationAt(tile.pos.x + 1, tile.pos.y, elevation);
        float elevationDown = TerrainElevationAt(tile.pos.x, tile.pos.y - 1, elevation);
        float elevationUp = TerrainElevationAt(tile.pos.x, tile.pos.y + 1, elevation);
        Vector2 elevationGradient = new Vector2(elevationRight - elevationLeft, elevationUp - elevationDown);
        Vector2 terrainDeflection = -elevationGradient * (IsSnowPeak(tile) ? 0.42f : 0.32f);

        Vector2 raw = deflectedPressure + circulation + terrainDeflection;
        if (pressureGradient.sqrMagnitude > 0.0004f)
        {
            Vector2 towardLow = pressureGradient.normalized;
            float requiredComponent = Mathf.Max(0.08f, raw.magnitude * 0.42f);
            float currentComponent = Vector2.Dot(raw, towardLow);
            if (currentComponent < requiredComponent)
                raw += towardLow * (requiredComponent - currentComponent);
        }

        float pressureSlope = pressureGradient.magnitude;
        float speed = Mathf.Clamp01(0.10f + pressureSlope * 0.34f + raw.magnitude * 0.40f +
                                    elevationGradient.magnitude * 0.24f);
        float summitExposure = IsSummit(tile) ? 0.14f : 0f;
        speed *= Mathf.Clamp(1f - elevation * 0.22f + summitExposure, 0.62f, 1.12f);
        Vector2 direction = raw.sqrMagnitude > 0.0001f ? raw.normalized : Vector2.right;
        float blend = immediate ? 1f : 0.28f;
        Vector2 blendedDirection = Vector2.Lerp(cell.Wind, direction, blend).normalized;
        if (pressureGradient.sqrMagnitude > 0.0004f &&
            Vector2.Dot(blendedDirection, pressureGradient) <= 0f)
            blendedDirection = (blendedDirection + pressureGradient.normalized * 0.85f).normalized;
        cell.Wind = blendedDirection;
        cell.WindSpeed = Mathf.Lerp(cell.WindSpeed, speed, blend);
    }

    private static Vector2 GlobalWindBelt(float signedLatitude, out float strength)
    {
        float hemisphere = signedLatitude < -0.001f ? -1f : 1f;
        float latitudeDegrees = Mathf.Abs(signedLatitude) * 90f;
        Vector2 trade = new Vector2(-1f, -hemisphere * 0.62f).normalized;
        Vector2 westerly = new Vector2(1f, hemisphere * 0.62f).normalized;
        Vector2 polarEasterly = new Vector2(-1f, -hemisphere * 0.55f).normalized;

        Vector2 direction;
        float zonePosition;
        if (latitudeDegrees < 27f)
        {
            direction = trade;
            zonePosition = latitudeDegrees / 30f;
        }
        else if (latitudeDegrees < 33f)
        {
            float transition = Mathf.SmoothStep(0f, 1f, (latitudeDegrees - 27f) / 6f);
            direction = Vector2.Lerp(trade, westerly, transition).normalized;
            zonePosition = 0f;
        }
        else if (latitudeDegrees < 57f)
        {
            direction = westerly;
            zonePosition = (latitudeDegrees - 30f) / 30f;
        }
        else if (latitudeDegrees < 63f)
        {
            float transition = Mathf.SmoothStep(0f, 1f, (latitudeDegrees - 57f) / 6f);
            direction = Vector2.Lerp(westerly, polarEasterly, transition).normalized;
            zonePosition = 0f;
        }
        else
        {
            direction = polarEasterly;
            zonePosition = (latitudeDegrees - 60f) / 30f;
        }

        // 赤道辐合带、副热带高压脊和副极地低压槽附近平均风较弱，
        // 各风带中央风速较高。
        float interior = Mathf.Sin(Mathf.Clamp01(zonePosition) * Mathf.PI);
        strength = Mathf.Lerp(0.30f, 0.72f, Mathf.Max(0f, interior));
        return direction;
    }

    private static string WindBeltName(float signedLatitude)
    {
        float latitudeDegrees = Mathf.Abs(signedLatitude) * 90f;
        if (latitudeDegrees < 30f) return "信风带";
        if (latitudeDegrees < 60f) return "西风带";
        return "极地东风带";
    }
}
