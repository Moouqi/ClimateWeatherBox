using UnityEngine;

namespace ClimateWeather;

public sealed partial class ClimateSystem
{
    private Texture2D _pressureTexture;
    private float[] _displayPressure, _pressureScratch;
    private Color32[] _pressurePixels;
    private float _pressureDisplayNext;
    private int _pressureInterval;

    private void RefreshPressureDisplay(float screenWidth)
    {
        if (Event.current.type != EventType.Repaint || !AtmosphereReady) return;
        int interval = screenWidth < 700f ? 8 : 4;
        if (_pressureTexture != null && Time.unscaledTime < _pressureDisplayNext && interval == _pressureInterval) return;
        _pressureDisplayNext = Time.unscaledTime + 1f;
        _pressureInterval = interval;
        int width = Mathf.Clamp(_airWidth * 4, 32, 512);
        int height = Mathf.Clamp(_airHeight * 4, 32, 512);
        if (_pressureTexture == null || _pressureTexture.width != width || _pressureTexture.height != height)
        {
            if (_pressureTexture != null) Destroy(_pressureTexture);
            _pressureTexture = new Texture2D(width, height, TextureFormat.RGBA32, false) {
                name = "ClimatePressureSnapshot", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            _pressurePixels = new Color32[width * height];
        }
        if (_displayPressure == null || _displayPressure.Length != _air.Length)
        {
            _displayPressure = new float[_air.Length];
            _pressureScratch = new float[_air.Length];
        }
        // All values come from the last fully applied snapshot. Display smoothing
        // does not change simulation pressure, storms or water transport.
        for (int i = 0; i < _air.Length; i++) _displayPressure[i] = _air[i].Pressure;
        for (int pass = 0; pass < 3; pass++)
        {
            for (int y = 0; y < _airHeight; y++)
            for (int x = 0; x < _airWidth; x++)
            {
                float sum = 0f;
                for (int oy = -1; oy <= 1; oy++)
                for (int ox = -1; ox <= 1; ox++)
                    sum += _displayPressure[AirIndex(x + ox, y + oy)] * (ox == 0 ? 2 : 1) * (oy == 0 ? 2 : 1);
                _pressureScratch[y * _airWidth + x] = sum / 16f;
            }
            var swap = _displayPressure; _displayPressure = _pressureScratch; _pressureScratch = swap;
        }
        float sx = MapBox.width / (float)width / AtmosphereStride;
        float sy = MapBox.height / (float)height / AtmosphereStride;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            float gx = (x + .5f) * sx - .5f, gy = (y + .5f) * sy - .5f;
            int ix = Mathf.FloorToInt(gx), iy = Mathf.FloorToInt(gy);
            float fx = gx - ix, fy = gy - iy;
            float a = _displayPressure[AirIndex(ix, iy)], b = _displayPressure[AirIndex(ix + 1, iy)];
            float c = _displayPressure[AirIndex(ix, iy + 1)], d = _displayPressure[AirIndex(ix + 1, iy + 1)];
            float p = Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
            float dx = Mathf.Lerp(b - a, d - c, fy), dy = Mathf.Lerp(c - a, d - b, fx);
            float n = Mathf.InverseLerp(980f, 1040f, p);
            Color neutral = new Color(.72f, .82f, .76f, .20f);
            Color fill = n < .5f ? Color.Lerp(new Color(.08f,.30f,.92f,.36f),neutral,n*2)
                : Color.Lerp(neutral,new Color(.94f,.18f,.10f,.38f),(n-.5f)*2);
            // Distance to a continuous bilinear isobar, normalized by its local
            // gradient: avoids painting the whole quantized tile boundary.
            float distance = Mathf.Abs(p - (960f + Mathf.Round((p - 960f) / interval) * interval));
            float gradient = Mathf.Sqrt(dx * dx + dy * dy);
            float pixelGradient = Mathf.Sqrt(dx * dx * sx * sx + dy * dy * sy * sy);
            float line = (1f - Mathf.SmoothStep(.25f, 1.1f, distance / Mathf.Max(.0001f, pixelGradient)))
                * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.03f, .20f, gradient));
            _pressurePixels[y * width + x] = Color.Lerp(fill, new Color(.10f,.16f,.23f,.46f), line);
        }
        _pressureTexture.SetPixels32(_pressurePixels);
        _pressureTexture.Apply(false, false);
    }
}
