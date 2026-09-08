using System;
using System.Collections.Generic;
using UnityEngine;

namespace ClimateWeather;

public sealed partial class ClimateSystem
{
    private float[] _atmosphericCloudDelta = Array.Empty<float>();
    private readonly HashSet<int> _atmosphericCloudDeltaIndices = new HashSet<int>();

    private void InitializeAtmosphericCloudField(WorldTile[] tiles)
    {
        _atmosphericCloudDelta = new float[_cells.Length];
        _atmosphericCloudDeltaIndices.Clear();
        for (int i = 0; i < _cells.Length && i < tiles.Length; i++)
        {
            ClimateCell cell = _cells[i];
            // 新世界或重新载入气候场时不预填充云量；云层统一从水汽循环生成。
            cell.CloudCover = 0f;
            cell.LastCloudTime = _seasonClock;
        }
    }

    /// <summary>
    /// 水汽在低压与风场辐合区凝结成云，并按风向向更低气压区域输送。
    /// 所有变化先写入 delta，在当前气候批次结束后统一提交，避免遍历顺序让云在
    /// 同一批次内被重复搬运。
    /// </summary>
    private void UpdateAtmosphericCloudCell(int index, WorldTile tile, ClimateCell cell)
    {
        if (index < 0 || index >= _cells.Length || tile?.Type == null || cell == null) return;
        float elapsed = cell.LastCloudTime < 0f
            ? TickSeconds
            : Mathf.Clamp(_seasonClock - cell.LastCloudTime, 0f, 120f);
        cell.LastCloudTime = _seasonClock;
        if (elapsed <= 0f) return;

        bool ocean = tile.main_type?.ocean == true || tile.Type.layer_type == TileLayerType.Ocean;
        bool frozenOcean = ocean && IsFrozenClimateSurface(tile);
        float heat = Mathf.Clamp01(Mathf.InverseLerp(0.38f, 0.92f, cell.Temperature));
        float vapor = Mathf.Clamp01(Mathf.InverseLerp(0.42f, 0.94f, cell.Humidity));
        float lowPressure = LowPressureCloudPotential(tile, cell, out float pressureConvergence);
        float river = ocean ? 0f : GetRiverMoisture(tile);
        float vegetation = ocean ? 0f : Mathf.Clamp01(GetSurfaceMoistureRetention(tile));
        float sourcePerSecond = ocean
            ? Mathf.Lerp(0.00065f, 0.00145f, heat)
            : vapor * (0.00018f + vegetation * 0.00030f + river * 0.00090f) *
              Mathf.Lerp(0.65f, 1.20f, heat);
        sourcePerSecond *= Mathf.Lerp(0.78f, 1.30f, LocalSummerStrength(tile));
        // 冰面封闭海水并反射太阳辐射，蒸发量只保留开放海面的极小部分。
        if (frozenOcean) sourcePerSecond *= 0.03f;

        // 水汽在低压和风场辐合区抬升凝结；高压下沉区仍允许少量水汽进入
        // 大气，但不会像旧实现那样直接生成连续云幕。
        float pressureCondensation = Mathf.Lerp(0.08f, 1.80f, lowPressure) *
                                     Mathf.Lerp(0.82f, 1.42f, pressureConvergence);
        sourcePerSecond *= pressureCondensation;

        // 云量接近饱和时必须停止继续凝结，否则每个海洋格都会独立把云量
        // 推到 100%，输送得到的空间梯度也会被 Clamp01 吞掉。
        float cloudHeadroom = Mathf.Pow(Mathf.Clamp01(1f - cell.CloudCover), 1.65f);
        float condensedPerSecond = sourcePerSecond * cloudHeadroom;
        float decayPerSecond = Mathf.Lerp(0.00010f, 0.00024f, heat) *
                               Mathf.Lerp(1.15f, 0.72f, vapor);
        // 高压区下沉增温、云滴蒸发更快；低压区则延长云团寿命。
        decayPerSecond *= Mathf.Lerp(1.65f, 0.68f, lowPressure);

        // 降水是云场本身的连续汇；实体雨云只是可见效果，不能作为唯一回收渠道。
        float precipitationPerSecond = 0f;
        if (cell.CloudCover > 0.30f)
        {
            float rainCloud = Mathf.InverseLerp(0.30f, 1f, cell.CloudCover);
            precipitationPerSecond = rainCloud * rainCloud *
                                     Mathf.Lerp(0.00012f, 0.00034f, vapor);
        }
        float cloudDelta = (condensedPerSecond - decayPerSecond - precipitationPerSecond) * elapsed;
        AccumulateAtmosphericCloudDelta(index, cloudDelta);

        // 陆地凝结会从近地水汽中取水，避免凭空制造云水；海洋水体作为水源，
        // 但冻结后已由上面的蒸发系数限制。
        if (!ocean && condensedPerSecond > 0f)
        {
            cell.Humidity = Mathf.Max(0f, cell.Humidity - condensedPerSecond * elapsed * 0.42f);
        }

        // 只有从云中实际落下的水才回补地面湿度，增湿量与云量消耗来自同一水账。
        if (!ocean && precipitationPerSecond > 0f)
        {
            float moistureGain = precipitationPerSecond * elapsed * 0.72f;
            cell.Humidity = Mathf.Min(0.98f, cell.Humidity + moistureGain);
        }

        Vector2 flow = AtmosphericCloudFlow(tile, cell);
        if (flow.sqrMagnitude < 0.0001f || cell.CloudCover <= 0.015f) return;
        int targetIndex = SelectAtmosphericCloudTarget(tile, flow, out WorldTile targetTile);
        if (targetIndex < 0 || targetIndex == index || targetTile == null) return;

        float transferFraction = 1f - Mathf.Exp(-elapsed / 150f *
            Mathf.Lerp(0.55f, 1.45f, Mathf.Clamp01(cell.WindSpeed)));
        float currentElevation = GetTerrainElevation(tile);
        float targetElevation = GetTerrainElevation(targetTile);
        if (targetElevation > currentElevation + 0.08f) transferFraction *= 0.30f;
        if (IsSummit(targetTile)) transferFraction *= IsSnowPeak(targetTile) ? 0.08f : 0.16f;
        float transfer = Mathf.Min(cell.CloudCover * 0.42f,
            cell.CloudCover * Mathf.Clamp01(transferFraction));
        if (transfer <= 0.0001f) return;
        AccumulateAtmosphericCloudDelta(index, -transfer);
        AccumulateAtmosphericCloudDelta(targetIndex, transfer);
    }

    private float LowPressureCloudPotential(WorldTile tile, ClimateCell cell,
        out float pressureConvergence)
    {
        float center = cell.Pressure > 0f ? cell.Pressure : 1013.25f;
        float left = PressureAt(tile.pos.x - 1, tile.pos.y, center);
        float right = PressureAt(tile.pos.x + 1, tile.pos.y, center);
        float down = PressureAt(tile.pos.x, tile.pos.y - 1, center);
        float up = PressureAt(tile.pos.x, tile.pos.y + 1, center);
        float neighbourMean = (left + right + down + up) * 0.25f;

        // 绝对低压决定大尺度抬升，低于周围的局地压陷决定辐合强度。
        float synopticLow = 1f - Mathf.Clamp01(Mathf.InverseLerp(988f, 1038f, center));
        pressureConvergence = Mathf.Clamp01((neighbourMean - center + 0.15f) / 3.5f);
        return Mathf.Clamp01(synopticLow * 0.72f + pressureConvergence * 0.58f);
    }

    private Vector2 AtmosphericCloudFlow(WorldTile tile, ClimateCell cell)
    {
        float center = cell.Pressure;
        float left = PressureAt(tile.pos.x - 1, tile.pos.y, center);
        float right = PressureAt(tile.pos.x + 1, tile.pos.y, center);
        float down = PressureAt(tile.pos.x, tile.pos.y - 1, center);
        float up = PressureAt(tile.pos.x, tile.pos.y + 1, center);
        Vector2 pressureToLow = new Vector2(left - right, down - up) * 0.18f;
        Vector2 wind = cell.Wind * Mathf.Lerp(0.65f, 1.35f, cell.WindSpeed);
        Vector2 combined = wind + pressureToLow;
        return combined.sqrMagnitude > 0.0001f ? combined.normalized : cell.Wind;
    }

    private int SelectAtmosphericCloudTarget(WorldTile tile, Vector2 flow, out WorldTile targetTile)
    {
        targetTile = null;
        int bestIndex = -1;
        float bestScore = 0.10f;
        float currentElevation = GetTerrainElevation(tile);
        float currentPressure = PressureAt(tile.pos.x, tile.pos.y, 1013.25f);
        for (int oy = -1; oy <= 1; oy++)
        for (int ox = -1; ox <= 1; ox++)
        {
            if (ox == 0 && oy == 0) continue;
            int x = tile.pos.x + ox;
            int y = tile.pos.y + oy;
            if (x < 0 || y < 0 || x >= MapBox.width || y >= MapBox.height) continue;
            int pixel = y * MapBox.width + x;
            if (pixel < 0 || pixel >= _cellIndexByPixel.Length) continue;
            int index = _cellIndexByPixel[pixel];
            if (index < 0 || index >= _cells.Length) continue;
            WorldTile candidate = World.world.tiles_list[index];
            if (candidate?.Type == null) continue;
            Vector2 direction = new Vector2(ox, oy).normalized;
            float uphill = Mathf.Max(0f, GetTerrainElevation(candidate) - currentElevation);
            float score = Vector2.Dot(direction, flow) - uphill * 1.8f;
            float candidatePressure = PressureAt(x, y, currentPressure);
            // 在大致顺风的候选格中优先向低压输送，让水汽在低压中心汇聚。
            score += Mathf.Clamp((currentPressure - candidatePressure) * 0.075f, -0.30f, 0.60f);
            if (IsSummit(candidate)) score -= 0.35f;
            if (score <= bestScore) continue;
            bestScore = score;
            bestIndex = index;
            targetTile = candidate;
        }
        return bestIndex;
    }

    private void AccumulateAtmosphericCloudDelta(int index, float delta)
    {
        if (index < 0 || index >= _atmosphericCloudDelta.Length || Mathf.Abs(delta) < 0.000001f)
            return;
        _atmosphericCloudDelta[index] += delta;
        _atmosphericCloudDeltaIndices.Add(index);
    }

    private void ApplyAtmosphericCloudDeltas(WorldTile[] tiles)
    {
        if (_atmosphericCloudDeltaIndices.Count == 0) return;
        foreach (int index in _atmosphericCloudDeltaIndices)
        {
            if (index < 0 || index >= _cells.Length) continue;
            float delta = _atmosphericCloudDelta[index];
            _atmosphericCloudDelta[index] = 0f;
            ClimateCell cell = _cells[index];
            float previous = cell.CloudCover;
            cell.CloudCover = Mathf.Clamp01(previous + delta);
        }
        _atmosphericCloudDeltaIndices.Clear();
    }
}
