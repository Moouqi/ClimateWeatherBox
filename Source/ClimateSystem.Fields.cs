using System;
using UnityEngine;

namespace ClimateWeather;

/// <summary>
/// 模拟状态到字段纹理的桥接：把地表格点、大气网格与灯光透光写进
/// ClimateFieldSet，并暴露夜幕 shader 所需的天文 uniform。
/// 显示刷新与模拟节奏解耦，暂停时夜幕仍持续走 shader。
/// </summary>
public sealed partial class ClimateSystem
{
    internal ClimateFieldSet Fields { get; private set; }
    private const float FieldRefreshSeconds = 0.25f;
    private const float AtmosRefreshSeconds = 0.5f;
    private const float LightMaskRefreshSeconds = 0.4f;
    private float _nextSurfaceRefresh, _nextAtmosRefresh, _nextLightMaskRefresh;

    internal bool ReadyForRendering => Fields != null && Fields.Valid && _cells.Length > 0;
    internal ClimateLayer VisibleLayer => _visibleLayer;
    internal float SimClock => _seasonClock;

    internal void ToggleLayer(ClimateLayer layer)
    {
        _visibleLayer = _visibleLayer == layer ? ClimateLayer.None : layer;
        Fields?.MarkSurfaceDirty();
        Fields?.MarkAtmosDirty();
    }

    private float CurrentSolarDeclinationRadians()
    {
        float yearPhase = (_seasonClock / (SeasonSeconds * 4f)) * Mathf.PI * 2f;
        return 23.44f * Mathf.Deg2Rad * Mathf.Sin(yearPhase);
    }

    private float CurrentSubsolarLongitudeRadians()
    {
        // 与季节和热惯性共用同一模拟时钟，暂停及速度变化时保持相位一致。
        return Mathf.PI * 0.5f + _seasonClock * 0.035f;
    }

    // 夜幕/参考线 uniform。
    internal float DeclinationRadians => CurrentSolarDeclinationRadians();
    internal float DeclinationDegrees => CurrentSolarDeclinationRadians() * Mathf.Rad2Deg;
    internal float SubsolarLongitudeRadians => CurrentSubsolarLongitudeRadians();
    internal float LatitudeMinDegrees => _latitudeMinDegrees;
    internal float LatitudeMaxDegrees => _latitudeMaxDegrees;
    internal float LongitudeMinDegrees => _longitudeMinDegrees;
    internal float LongitudeMaxDegrees => _longitudeMaxDegrees;
    private static float GetDarkAgeOverlayAlpha()
    {
        // 当前纪元本身已有原版全局暗幕时，不再叠加局地夜幕；建筑灯光仍由
        // WorldAgeManager 和 IsNightAt 使用原版逻辑显示，避免黑暗纪元被压暗两次。
        if (World.world?.era_manager?.getCurrentAge()?.overlay_darkness == true) return 0f;
        WorldAgeAsset darkAge = AssetManager.era_library?.get("age_dark");
        float layerAlpha = darkAge == null ? 0.30f : Mathf.Clamp01(darkAge.era_effect_overlay_alpha);
        // 原版 WorldAgeEffects 同时作用于 top 与 material 两个夜色通道。
        // 屏幕空间单纹理使用两层 Alpha 合成后的等效值，才能匹配黑暗纪元观感。
        return 1f - (1f - layerAlpha) * (1f - layerAlpha);
    }

    internal float NightAlphaUniform => GetDarkAgeOverlayAlpha();
    internal float DayDimUniform => Mathf.Clamp01(1f - SurfaceLightTransmission()) * 0.55f;

    internal void InitializeFields()
    {
        Fields ??= new ClimateFieldSet();
        Fields.Ensure(MapBox.width, MapBox.height, _airWidth, _airHeight);
        if (_gpuAtmosphere?.DisplayTexture != null)
            Fields.UseGpuAtmosTexture(_gpuAtmosphere.DisplayTexture);
        if (_gpuThermal != null && _gpuThermal.Error == null)
            Fields.UseGpuSurfaceTextures(_gpuThermal.DisplayTextureA, _gpuThermal.DisplayTextureB);
        _nextSurfaceRefresh = _nextAtmosRefresh = _nextLightMaskRefresh = 0f;
        ClimateLayerRenderer.Active?.EnsureMap(MapBox.width, MapBox.height, _airWidth, _airHeight);
    }

    /// <summary>在 Update 中每帧调用（位于暂停检查之前），按节流周期填充并上传字段。</summary>
    internal void RefreshDisplayFields()
    {
        if (Fields == null || _cells.Length == 0) return;
        float now = Time.unscaledTime;
        bool surfaceNeeded = _visibleLayer is ClimateLayer.Temperature or ClimateLayer.Humidity
            or ClimateLayer.Clouds or ClimateLayer.Elevation or ClimateLayer.AirHumidity
            or ClimateLayer.Rainfall;
        if (surfaceNeeded && !Fields.SurfaceOwnedByGpu && now >= _nextSurfaceRefresh)
        {
            _nextSurfaceRefresh = now + FieldRefreshSeconds;
            Bench.bench("mod.SurfaceFill", "cpu");
            FillSurfaceFields();
            Bench.benchEnd("mod.SurfaceFill", "cpu", false, 0);
        }
        if (_visibleLayer == ClimateLayer.Wind && AtmosphereReady && now >= _nextAtmosRefresh)
        {
            _nextAtmosRefresh = now + AtmosRefreshSeconds;
            FillAtmosField();
        }
        if (now >= _nextLightMaskRefresh)
        {
            _nextLightMaskRefresh = now + LightMaskRefreshSeconds;
            Bench.bench("mod.LightMask", "cpu");
            RebuildLightMask();
            Bench.benchEnd("mod.LightMask", "cpu", false, 0);
        }
        Bench.bench("mod.FieldUpload", "cpu");
        Fields.Update();
        Bench.benchEnd("mod.FieldUpload", "cpu", false, 0);
    }

    private void FillSurfaceFields()
    {
        WorldTile[] tiles = World.world?.tiles_list;
        if (tiles == null) return;
        Color[] surfaceA = Fields.SurfaceAPixels;
        Color[] surfaceB = Fields.SurfaceBPixels;
        bool fillElevation = _visibleLayer == ClimateLayer.Elevation;
        for (int i = 0; i < _cells.Length && i < tiles.Length; i++)
        {
            WorldTile tile = tiles[i];
            if (tile == null) continue;
            int pixel = tile.pos.y * MapBox.width + tile.pos.x;
            if (pixel < 0 || pixel >= surfaceA.Length) continue;
            ClimateCell cell = _cells[i];
            surfaceA[pixel] = new Color(cell.Temperature, cell.Humidity,
                cell.RelativeHumidity, Mathf.Clamp01(cell.RainRate * 1000f));
            surfaceB[pixel] = new Color(
                cell.CloudCover,
                fillElevation ? ContourElevationAtPixel(pixel) : surfaceB[pixel].g,
                IsFrozenClimateSurface(tile) ? 1f : 0f,
                cell.Sunlight);
        }
        Fields.MarkSurfaceDirty();
    }

    private void FillAtmosField()
    {
        if (Fields.AtmosOwnedByGpu) return;
        Color[] atmos = Fields.AtmosPixels;
        for (int i = 0; i < _air.Length && i < atmos.Length; i++)
            atmos[i] = new Color(_air[i].Pressure, _air[i].Velocity.x,
                _air[i].Velocity.y, _air[i].Vapor);
        Fields.MarkAtmosDirty();
    }

    /// <summary>
    /// 重建灯光透光蒙版：与旧版夜幕抠洞完全相同的准入与衰减参数，
    /// 只是写入独立的低分辨率蒙版，夜幕 shader 采样它保留原版灯光。
    /// </summary>
    private void RebuildLightMask()
    {
        Fields.ClearLightMask();
        Color32[] pixels = Fields.LightPixels;
        int maskWidth = Fields.LightMask.width;
        int maskHeight = Fields.LightMask.height;
        if (World.world?.buildings != null)
        {
            foreach (Building building in World.world.buildings)
            {
                if (building == null || building.asset == null || building.current_tile == null) continue;
                BuildingAsset asset = building.asset;
                if (!asset.draw_light_area || asset.draw_light_size <= 0f) continue;
                if (!IsNightAt(building.current_tile)) continue;
                float lightWorldX = building.current_tile.pos.x + 0.5f + asset.draw_light_area_offset_x;
                float lightWorldY = building.current_tile.pos.y + 0.5f + asset.draw_light_area_offset_y;
                int centerX = Mathf.RoundToInt(lightWorldX / Math.Max(1, MapBox.width) * (maskWidth - 1));
                int centerY = Mathf.RoundToInt(lightWorldY / Math.Max(1, MapBox.height) * (maskHeight - 1));
                int radiusX = Mathf.Clamp(
                    Mathf.CeilToInt(asset.draw_light_size / Math.Max(1, MapBox.width) * maskWidth), 1, 48);
                int radiusY = Mathf.Clamp(
                    Mathf.CeilToInt(asset.draw_light_size / Math.Max(1, MapBox.height) * maskHeight), 1, 48);
                DimLightMask(pixels, maskWidth, maskHeight, centerX, centerY, radiusX, radiusY,
                    0.06f, 0.82f);
            }
        }

        PreserveLavaTypeLightMask(TileLibrary.lava0, pixels, maskWidth, maskHeight);
        PreserveLavaTypeLightMask(TileLibrary.lava1, pixels, maskWidth, maskHeight);
        PreserveLavaTypeLightMask(TileLibrary.lava2, pixels, maskWidth, maskHeight);
        PreserveLavaTypeLightMask(TileLibrary.lava3, pixels, maskWidth, maskHeight);
        Fields.MarkLightDirty();
    }

    private void PreserveLavaTypeLightMask(TileType lavaType, Color32[] pixels,
        int maskWidth, int maskHeight)
    {
        if (lavaType?.hashset == null || lavaType.hashset.Count == 0) return;
        float heat01 = Mathf.Clamp01((lavaType.lava_level + 1f) / 4f);
        float corePass = Mathf.Lerp(0.30f, 0.07f, heat01);
        int baseRadius = lavaType.lava_level >= 2 ? 2 : 1;
        int radius = Mathf.Clamp(baseRadius * Mathf.Max(1, maskWidth / 256), 1, 16);
        foreach (WorldTile tile in lavaType.hashset)
        {
            if (tile == null) continue;
            if (!IsNightAt(tile)) continue;
            int centerX = Mathf.RoundToInt((tile.pos.x + 0.5f) / Math.Max(1, MapBox.width) * (maskWidth - 1));
            int centerY = Mathf.RoundToInt((tile.pos.y + 0.5f) / Math.Max(1, MapBox.height) * (maskHeight - 1));
            DimLightMask(pixels, maskWidth, maskHeight, centerX, centerY, radius, radius,
                corePass, 0.86f);
        }
    }

    private static void DimLightMask(Color32[] pixels, int maskWidth, int maskHeight,
        int centerX, int centerY, int radiusX, int radiusY, float corePass, float edgePass)
    {
        for (int offsetY = -radiusY; offsetY <= radiusY; offsetY++)
        for (int offsetX = -radiusX; offsetX <= radiusX; offsetX++)
        {
            int px = centerX + offsetX;
            int py = centerY + offsetY;
            if (px < 0 || py < 0 || px >= maskWidth || py >= maskHeight) continue;
            float normalizedDistance = Mathf.Sqrt(
                (offsetX / (float)radiusX) * (offsetX / (float)radiusX) +
                (offsetY / (float)radiusY) * (offsetY / (float)radiusY));
            if (normalizedDistance > 1f) continue;
            float passThrough = Mathf.Lerp(corePass, edgePass, normalizedDistance);
            int index = py * maskWidth + px;
            byte value = pixels[index].r;
            byte dimmed = (byte)Mathf.RoundToInt(value * passThrough);
            pixels[index] = new Color32(dimmed, dimmed, dimmed, 255);
        }
    }
}
