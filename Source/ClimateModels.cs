using System.Collections.Generic;
using UnityEngine;

namespace ClimateWeather;

public enum Season { Spring, Summer, Autumn, Winter }
public enum ClimateTemplate { Global, NorthernHemisphere, SouthernHemisphere }
public enum TemperatureUnit { Celsius, Fahrenheit }

// 可视化数据图层；F3-F7 切换，渲染顺序见 ClimateLayerRenderer.LayerModeIndex。
internal enum ClimateLayer { None, Temperature, Humidity, Wind, Elevation, Clouds, AirHumidity, Rainfall }

internal enum TropicalCycloneKind
{
    TropicalDepression,
    TropicalStorm,
    Typhoon,
    Hurricane,
    TropicalCyclone
}

public sealed class ClimateCell
{
    public float Sunlight;
    public float Temperature;
    public float Humidity;
    public bool BiomeClimateInitialized;
    public float BiomeTemperature;
    public float BiomeMoisture;
    // Humidity retains the legacy soil-water API used by biomes.
    public float AirTemperatureC;
    public float RelativeHumidity;
    public float RainRate;
    public float SnowWater;
    public double LastWaterBalance;
    public double LastSnowfall;
    public float Pressure;
    public float LastPressureTime = -1f;
    public Vector2 Wind;
    public float WindSpeed;
    public float LastTemperatureTime = -1f;
    public float CloudCover;
    public float LastCloudTime = -1f;
}

internal sealed class FrozenTerrainSnapshot
{
    public TileType Main;
    public TopTileType Top;
}

internal sealed class DroughtTerrainSnapshot
{
    public TileType Main;
    public TopTileType Top;
}

internal sealed class RainCloudState
{
    public float NextMoistureTime;
    public float NextPhaseCheckTime;
}

internal sealed class TropicalCycloneState
{
    public TropicalCycloneKind Kind;
    public float Intensity;
    public float SpawnTime;
    public float LastUpdateTime;
    public float NextRainTime;
    public float NextCloudTime;
    public float MaximumLifetime;
    public bool CountedNamedStorm;
    public bool CountedDepression, CountedStorm, ExitLogged;
}

internal sealed class VegetationTemperatureState
{
    public float LastClimateTime = -1f;
    public float ExposureSeconds;
}

internal sealed class BiomeTransitionState
{
    public string Candidate;
    public float SinceSeasonTime;
}

internal sealed class QueuedBiomeTransition
{
    public int CellIndex;
    public string Candidate;
}

internal sealed class LavaCoolingState
{
    public string LavaTypeId;
    public float CoolingProgress;
    public float LastClimateTime;
}

internal struct RiverSearchNode
{
    public int Pixel;
    public float G;
    public float F;
}

internal sealed class RiverMinHeap
{
    private readonly List<RiverSearchNode> _items = new List<RiverSearchNode>();
    public int Count => _items.Count;

    public void Push(RiverSearchNode node)
    {
        int i = _items.Count;
        _items.Add(node);
        while (i > 0)
        {
            int parent = (i - 1) >> 1;
            if (_items[parent].F <= node.F) break;
            _items[i] = _items[parent];
            i = parent;
        }
        _items[i] = node;
    }

    public RiverSearchNode Pop()
    {
        RiverSearchNode root = _items[0];
        int lastIndex = _items.Count - 1;
        RiverSearchNode last = _items[lastIndex];
        _items.RemoveAt(lastIndex);
        if (_items.Count == 0) return root;
        int i = 0;
        while (true)
        {
            int left = i * 2 + 1;
            if (left >= _items.Count) break;
            int right = left + 1;
            int child = right < _items.Count && _items[right].F < _items[left].F ? right : left;
            if (_items[child].F >= last.F) break;
            _items[i] = _items[child];
            i = child;
        }
        _items[i] = last;
        return root;
    }
}

internal struct AgeClimateProfile
{
    public float TemperatureOffsetC;
    public float SunlightMultiplier;
    public float HumidityMultiplier;
    public float HumidityOffset;
    public float RainMultiplier;
    public float StormMultiplier;
    public float HurricaneMultiplier;
    public bool ForceSnow;

    public static AgeClimateProfile Normal => new AgeClimateProfile
    {
        SunlightMultiplier = 1f,
        HumidityMultiplier = 1f,
        RainMultiplier = 1f,
        StormMultiplier = 1f,
        HurricaneMultiplier = 1f
    };
}
