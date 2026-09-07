using System;
using System.Collections.Generic;
using UnityEngine;

namespace ClimateWeather;

public sealed partial class ClimateSystem
{
    private readonly HashSet<int> _dirtyLayerPixels = new HashSet<int>();
    private bool _layerNeedsFullRefresh = true;

    private void ToggleLayer(ClimateLayer layer)
    {
        _visibleLayer = _visibleLayer == layer ? ClimateLayer.None : layer;
        // WorldBox 地图由专用渲染管线绘制，普通 SpriteRenderer 会被地图覆盖。
        // 图层纹理改由 OnGUI 按摄像机投影直接绘制。
        if (_layerRenderer != null) _layerRenderer.enabled = false;
        _layerNeedsFullRefresh = true;
        RefreshLayer(true);
    }

    private void BuildLayerRenderer()
    {
        if (_pressureTexture != null) Destroy(_pressureTexture);
        _pressureTexture = null;
        if (_layerRenderer != null) Destroy(_layerRenderer.gameObject);
        int width = Math.Max(1, MapBox.width);
        int height = Math.Max(1, MapBox.height);
        _layerTexture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            name = "ClimateWeatherLayer",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        _layerPixels = new Color32[width * height];
        _nightTexture = new Texture2D(256, 256, TextureFormat.RGBA32, false)
        {
            name = "ClimateWeatherNight",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        _nightPixels = new Color32[256 * 256];
        _nightSinLatitudes = new float[256];
        _nightCosLatitudes = new float[256];
        _nightCosHourAngles = new float[256];
        _nextNightRefresh = 0f;
        _dirtyLayerPixels.Clear();
        _layerNeedsFullRefresh = true;
        GameObject go = new GameObject("ClimateWeatherLayer");
        _layerRenderer = go.AddComponent<SpriteRenderer>();
        _layerRenderer.sprite = Sprite.Create(_layerTexture,
            new Rect(0, 0, width, height), Vector2.zero, 1f);
        _layerRenderer.sortingOrder = 32000;
        _layerRenderer.enabled = false;
        go.transform.position = new Vector3(0f, 0f, -5f);
    }

    private void MarkLayerDirty(WorldTile tile)
    {
        if (tile == null) return;
        MarkLayerDirtyPixel(tile.pos.y * MapBox.width + tile.pos.x);
    }

    private void MarkLayerDirtyIndex(int cellIndex, WorldTile tile)
    {
        if (cellIndex < 0 || cellIndex >= _cells.Length || tile == null) return;
        MarkLayerDirty(tile);
    }

    private void MarkLayerDirtyPixel(int pixel)
    {
        if (_visibleLayer == ClimateLayer.None || _visibleLayer == ClimateLayer.Wind || pixel < 0 || pixel >= _layerPixels.Length) return;
        _dirtyLayerPixels.Add(pixel);
    }

    /// <summary>
    /// 常规刷新只重新计算 dirty tile，并通过 Texture2D.SetPixel 修改对应像素；
    /// 一批修改完成后统一 Apply。地图初始化、纹理重建和切换图层时才全量写入。
    /// </summary>
    private void RefreshLayer(bool forceFull = false)
    {
        // Wind uses a coherent atmospheric snapshot, never dirty terrain caches.
        if (_visibleLayer == ClimateLayer.Wind) return;
        if (_visibleLayer == ClimateLayer.None || _layerTexture == null || _cells.Length == 0) return;
        WorldTile[] tiles = World.world?.tiles_list;
        if (tiles == null) return;

        bool full = forceFull || _layerNeedsFullRefresh ||
                    _layerPixels.Length != MapBox.width * MapBox.height;
        if (full)
        {
            Array.Clear(_layerPixels, 0, _layerPixels.Length);
            for (int i = 0; i < tiles.Length && i < _cells.Length; i++)
            {
                WorldTile tile = tiles[i];
                if (tile == null) continue;
                int pixel = tile.pos.y * MapBox.width + tile.pos.x;
                if (pixel < 0 || pixel >= _layerPixels.Length) continue;
                _layerPixels[pixel] = LayerColorForPixel(i, pixel);
            }
            _layerTexture.SetPixels32(_layerPixels);
            _layerTexture.Apply(false, false);
            _dirtyLayerPixels.Clear();
            _layerNeedsFullRefresh = false;
            return;
        }

        if (_dirtyLayerPixels.Count == 0) return;
        foreach (int pixel in _dirtyLayerPixels)
        {
            if (pixel < 0 || pixel >= _cellIndexByPixel.Length) continue;
            int cellIndex = _cellIndexByPixel[pixel];
            if (cellIndex < 0 || cellIndex >= _cells.Length) continue;
            Color32 color = LayerColorForPixel(cellIndex, pixel);
            _layerPixels[pixel] = color;
            _layerTexture.SetPixel(pixel % MapBox.width, pixel / MapBox.width, color);
        }
        _layerTexture.Apply(false, false);
        _dirtyLayerPixels.Clear();
    }

    private Color32 LayerColorForPixel(int cellIndex, int pixel)
    {
        if (_visibleLayer == ClimateLayer.AirHumidity) return HumidityColor(_cells[cellIndex].RelativeHumidity);
        if (_visibleLayer == ClimateLayer.Rainfall) return HumidityColor(Mathf.Clamp01(_cells[cellIndex].RainRate * 1000f));
        if (_visibleLayer == ClimateLayer.Wind)
            return PressureColor(cellIndex, pixel);
        return _visibleLayer == ClimateLayer.Temperature
            ? TemperatureColor(_cells[cellIndex].Temperature)
            : _visibleLayer == ClimateLayer.Humidity
                ? HumidityColor(_cells[cellIndex].Humidity)
                : _visibleLayer == ClimateLayer.Clouds
                    ? AtmosphericCloudColor(_cells[cellIndex].CloudCover)
                : ElevationContourColor(pixel);
    }

    private Color32 PressureColor(int cellIndex, int pixel)
    {
        float pressure = _cells[cellIndex].Pressure;
        float normalized = Mathf.Clamp01(Mathf.InverseLerp(980f, 1040f, pressure));
        Color low = new Color(0.08f, 0.30f, 0.92f, 0.46f);
        Color normal = new Color(0.72f, 0.82f, 0.76f, 0.25f);
        Color high = new Color(0.94f, 0.18f, 0.10f, 0.48f);
        Color fill = normalized < 0.5f
            ? Color.Lerp(low, normal, normalized * 2f)
            : Color.Lerp(normal, high, (normalized - 0.5f) * 2f);

        int band = Mathf.FloorToInt((pressure - 960f) / 4f);
        int x = pixel % MapBox.width;
        int y = pixel / MapBox.width;
        bool contour = PressureBandAt(x - 1, y, band) != band ||
                       PressureBandAt(x, y - 1, band) != band;
        if (contour) fill = new Color(0.10f, 0.12f, 0.18f, 0.72f);
        return fill;
    }

    private int PressureBandAt(int x, int y, int fallback)
    {
        if (x < 0 || y < 0 || x >= MapBox.width || y >= MapBox.height) return fallback;
        int pixel = y * MapBox.width + x;
        if (pixel < 0 || pixel >= _cellIndexByPixel.Length) return fallback;
        int index = _cellIndexByPixel[pixel];
        return index >= 0 && index < _cells.Length
            ? Mathf.FloorToInt((_cells[index].Pressure - 960f) / 4f)
            : fallback;
    }

    private static Color32 TemperatureColor(float value)
    {
        Color cold = new Color(0.05f, 0.35f, 1f, 0.58f);
        Color mild = new Color(0.2f, 1f, 0.25f, 0.50f);
        Color hot = new Color(1f, 0.12f, 0.02f, 0.62f);
        Color c = value < 0.5f
            ? Color.Lerp(cold, mild, value * 2f)
            : Color.Lerp(mild, hot, (value - 0.5f) * 2f);
        return c;
    }

    private static Color32 HumidityColor(float value)
    {
        Color dry = new Color(0.95f, 0.65f, 0.12f, 0.52f);
        Color wet = new Color(0.02f, 0.38f, 1f, 0.65f);
        return Color.Lerp(dry, wet, value);
    }

    private static Color32 AtmosphericCloudColor(float value)
    {
        float cover = Mathf.Clamp01(value);
        float visibleCover = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.06f, 0.92f, cover));
        Color thin = new Color(0.58f, 0.70f, 0.78f, 0f);
        Color dense = new Color(0.92f, 0.96f, 1f, 0.44f);
        Color color = Color.Lerp(thin, dense, visibleCover);
        color.a = cover < 0.08f ? 0f : Mathf.Lerp(0.05f, 0.44f, visibleCover);
        return color;
    }

    private Color32 ElevationContourColor(int pixel)
    {
        float elevation = ContourElevationAtPixel(pixel);
        Color deep = new Color(0.03f, 0.16f, 0.42f, 0.58f);
        Color coast = new Color(0.12f, 0.58f, 0.72f, 0.52f);
        Color lowland = new Color(0.28f, 0.70f, 0.28f, 0.50f);
        Color upland = new Color(0.72f, 0.61f, 0.25f, 0.54f);
        Color peak = new Color(0.94f, 0.94f, 0.91f, 0.62f);
        Color fill = elevation < 0.16f
            ? Color.Lerp(deep, coast, elevation / 0.16f)
            : elevation < 0.45f
                ? Color.Lerp(coast, lowland, (elevation - 0.16f) / 0.29f)
                : elevation < 0.78f
                    ? Color.Lerp(lowland, upland, (elevation - 0.45f) / 0.33f)
                    : Color.Lerp(upland, peak, (elevation - 0.78f) / 0.22f);

        const int contourBands = 12;
        int band = Mathf.Clamp(Mathf.FloorToInt(elevation * contourBands), 0, contourBands - 1);
        int x = pixel % MapBox.width;
        int y = pixel / MapBox.width;
        bool contour = false;
        if (x > 0) contour |= Mathf.Clamp(
            Mathf.FloorToInt(ContourElevationAtPixel(pixel - 1) * contourBands), 0,
            contourBands - 1) != band;
        if (y > 0) contour |= Mathf.Clamp(
            Mathf.FloorToInt(ContourElevationAtPixel(pixel - MapBox.width) * contourBands), 0,
            contourBands - 1) != band;
        if (contour) fill = Color.Lerp(fill, new Color(0.10f, 0.08f, 0.05f, 0.88f), 0.72f);
        return fill;
    }
}
