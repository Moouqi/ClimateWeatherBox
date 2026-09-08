using System.Collections.Generic;
using UnityEngine;

namespace ClimateWeather;

/// <summary>
/// 世界空间线条 Mesh：风尾迹粒子、经纬网、气压带参考线、太阳直射点十字，
/// 全部写入同一个 Mesh，一次 DrawCall 完成，替代旧版逐段旋转 GUI 纹理的画法。
/// </summary>
internal sealed class ClimateLineMesh
{
    private const int WindParticleCount = 240;
    private const int HistoryPoints = 6;
    private const float LineZ = -7f;

    private readonly Vector2[] _positions = new Vector2[WindParticleCount];
    private readonly Vector2[,] _history = new Vector2[WindParticleCount, HistoryPoints];
    private readonly float[] _life = new float[WindParticleCount];
    private float _lastSimTime = float.MinValue;

    private readonly List<Vector3> _vertices = new(WindParticleCount * HistoryPoints * 4);
    private readonly List<Color32> _colors = new(WindParticleCount * HistoryPoints * 4);
    private readonly List<int> _triangles = new(WindParticleCount * HistoryPoints * 6);

    internal readonly Mesh Mesh = new() { name = "ClimateWeatherLines" };

    internal void Reset()
    {
        _lastSimTime = float.MinValue;
        for (int i = 0; i < WindParticleCount; i++) _life[i] = 0f;
        ClearMesh();
    }

    /// <summary>推进风尾迹粒子；dt 来自模拟时钟，暂停时自然停住。</summary>
    internal void AdvanceWind(ClimateSystem climate)
    {
        float clock = climate.SimClock;
        if (_lastSimTime == float.MinValue) _lastSimTime = clock;
        float dt = Mathf.Clamp(clock - _lastSimTime, 0f, 0.10f);
        _lastSimTime = clock;
        if (dt <= 0f) return;
        int width = ClimateFieldSetMapWidth(climate);
        int height = climate.Fields.MapHeight;
        for (int i = 0; i < WindParticleCount; i++)
        {
            Vector2 p = _positions[i];
            if (_life[i] <= 0f || p.x < 0f || p.y < 0f || p.x >= width || p.y >= height)
            {
                p = new Vector2(Random.value * width, Random.value * height);
                _life[i] = 2f + Random.value * 5f;
                for (int j = 0; j < HistoryPoints; j++) _history[i, j] = p;
            }
            Vector2 velocity = climate.SampleAir(p.x, p.y).Velocity;
            for (int j = HistoryPoints - 1; j > 0; j--) _history[i, j] = _history[i, j - 1];
            _history[i, 0] = p;
            Vector2 midpoint = p + velocity * dt * 4f;
            p += climate.SampleAir(midpoint.x, midpoint.y).Velocity * dt * 8f;
            if (climate.HorizontalWrap && (p.x < 0f || p.x >= width))
            {
                p.x = HorizontalTopology.Wrap(p.x, width);
                // 接缝处重置历史，避免画出横跨整图的假直线。
                for (int j = 0; j < HistoryPoints; j++) _history[i, j] = p;
            }
            _life[i] -= dt * (velocity.sqrMagnitude < 0.001f ? 4f : 1f);
            _positions[i] = p;
        }
    }

    private static int ClimateFieldSetMapWidth(ClimateSystem climate) => climate.Fields.MapWidth;

    internal void Rebuild(Camera camera, ClimateSystem climate, ClimateLayer layer)
    {
        ClearMesh();
        if (layer == ClimateLayer.Wind) BuildWindMode(camera, climate);
        else if (layer == ClimateLayer.Temperature) BuildTemperatureMode(camera, climate);
        Upload();
    }

    private void BuildWindMode(Camera camera, ClimateSystem climate)
    {
        float width = LineWidth(camera, 1f);
        int mapWidth = climate.Fields.MapWidth;
        // 气压带参考线：随太阳赤纬季节摆动，与旧版 GUI 版本同一组带与颜色。
        float declination = climate.DeclinationDegrees;
        AddBeltGuide(climate, declination * 0.65f, new Color32(242, 77, 46, 153), width * 2f);
        AddBeltGuide(climate, 30f + declination * 0.24f, new Color32(235, 92, 199, 117), width);
        AddBeltGuide(climate, -30f + declination * 0.24f, new Color32(235, 92, 199, 117), width);
        AddBeltGuide(climate, 60f + declination * 0.14f, new Color32(240, 184, 77, 107), width);
        AddBeltGuide(climate, -60f + declination * 0.14f, new Color32(240, 184, 77, 107), width);

        for (int i = 0; i < WindParticleCount; i++)
        {
            if (_life[i] <= 0f) continue;
            Vector2 p = _positions[i];
            Vector2 velocity = climate.SampleAir(p.x, p.y).Velocity;
            Color tail = Color.Lerp(new Color(0.4f, 0.8f, 1f), new Color(0.9f, 1f, 0.65f),
                Mathf.Clamp01(velocity.magnitude));
            Vector2 previous = p;
            for (int j = 0; j < HistoryPoints; j++)
            {
                Vector2 next = _history[i, j];
                if (Mathf.Abs(next.x - previous.x) < mapWidth * 0.5f)
                    AddLine(previous, next, width,
                        new Color(tail.r, tail.g, tail.b, (1f - j / (float)HistoryPoints) * 0.85f));
                previous = next;
            }
        }
    }

    private void BuildTemperatureMode(Camera camera, ClimateSystem climate)
    {
        float thin = LineWidth(camera, 1f);
        float thick = LineWidth(camera, 2f);
        int mapWidth = climate.Fields.MapWidth;
        int mapHeight = climate.Fields.MapHeight;
        float latMin = climate.LatitudeMinDegrees, latMax = climate.LatitudeMaxDegrees;
        float lonMin = climate.LongitudeMinDegrees, lonMax = climate.LongitudeMaxDegrees;

        for (int longitude = Mathf.CeilToInt(lonMin / 30f) * 30; longitude <= lonMax; longitude += 30)
        {
            float x = Mathf.InverseLerp(lonMin, lonMax, longitude) * mapWidth;
            bool prime = longitude == 0;
            AddLine(new Vector2(x, 0f), new Vector2(x, mapHeight),
                prime ? thick : thin, new Color(1f, 1f, 1f, prime ? 0.46f : 0.22f));
        }
        for (int latitude = Mathf.CeilToInt(latMin / 30f) * 30; latitude <= latMax; latitude += 30)
        {
            float y = climate.LatitudeDegreesToMapY(latitude) * mapHeight;
            bool equator = latitude == 0;
            AddLine(new Vector2(0f, y), new Vector2(mapWidth, y),
                equator ? thick : thin,
                equator ? new Color(1f, 0.82f, 0.18f, 0.72f) : new Color(1f, 1f, 1f, 0.28f));
        }

        // 太阳直射点十字：与温度模型中的移动日照波保持同一相位。
        float normalizedY = climate.LatitudeDegreesToMapY(climate.DeclinationDegrees);
        if (climate.TryLongitudeDegreesToMapX(
                ClimateSystem.NormalizeLongitudeDegrees(climate.SubsolarLongitudeRadians * Mathf.Rad2Deg),
                out float normalizedX))
        {
            float x = normalizedX * mapWidth, y = normalizedY * mapHeight;
            const float arm = 1.6f;
            const float thickness = 0.32f;
            AddLine(new Vector2(x - arm, y), new Vector2(x + arm, y), thickness, new Color(1f, 0.92f, 0.05f, 0.95f));
            AddLine(new Vector2(x, y - arm), new Vector2(x, y + arm), thickness, new Color(1f, 0.92f, 0.05f, 0.95f));
        }
    }

    private void AddBeltGuide(ClimateSystem climate, float latitude, Color color, float width)
    {
        if (latitude < climate.LatitudeMinDegrees || latitude > climate.LatitudeMaxDegrees) return;
        int mapWidth = climate.Fields.MapWidth;
        float y = climate.LatitudeDegreesToMapY(latitude) * climate.Fields.MapHeight;
        AddLine(new Vector2(0f, y), new Vector2(mapWidth, y), width, color);
    }

    private static float LineWidth(Camera camera, float scale)
    {
        float ortho = camera != null && camera.orthographic ? camera.orthographicSize : 40f;
        return Mathf.Max(0.08f, ortho * 0.003f) * scale;
    }

    private void AddLine(Vector2 a, Vector2 b, float width, Color color)
    {
        Vector2 delta = b - a;
        float length = delta.magnitude;
        if (length < 0.001f) return;
        Vector2 perpendicular = new Vector2(-delta.y, delta.x) * (width * 0.5f / length);
        int baseIndex = _vertices.Count;
        _vertices.Add(new Vector3(a.x + perpendicular.x, a.y + perpendicular.y, LineZ));
        _vertices.Add(new Vector3(b.x + perpendicular.x, b.y + perpendicular.y, LineZ));
        _vertices.Add(new Vector3(b.x - perpendicular.x, b.y - perpendicular.y, LineZ));
        _vertices.Add(new Vector3(a.x - perpendicular.x, a.y - perpendicular.y, LineZ));
        Color32 c32 = color;
        for (int k = 0; k < 4; k++) _colors.Add(c32);
        _triangles.Add(baseIndex);
        _triangles.Add(baseIndex + 1);
        _triangles.Add(baseIndex + 2);
        _triangles.Add(baseIndex);
        _triangles.Add(baseIndex + 2);
        _triangles.Add(baseIndex + 3);
    }

    private void ClearMesh()
    {
        _vertices.Clear();
        _colors.Clear();
        _triangles.Clear();
    }

    private void Upload()
    {
        Mesh.Clear(false);
        if (_vertices.Count == 0) return;
        Mesh.SetVertices(_vertices);
        Mesh.SetColors(_colors);
        Mesh.SetTriangles(_triangles, 0);
        // 线条贴着地图边缘；手动给全图包围盒，避免逐帧重算和边缘剔除闪动。
        Mesh.bounds = new Bounds(new Vector3(0f, 0f, 0f), new Vector3(1e5f, 1e5f, 1f));
    }
}
