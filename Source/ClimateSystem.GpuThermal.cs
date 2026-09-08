using System;
using System.Collections.Generic;
using UnityEngine;

namespace ClimateWeather;

/// <summary>
/// GPU 地表温度管线：把 UpdateDynamicTemperatures 的太阳温度、热惯性、
/// 大气同步与群系气候记忆搬到 SurfaceThermal.compute 全图一次推进。
/// 依赖 GPU 大气的状态缓冲；大气或地表任一环节失效时整体回退 CPU 批处理，
/// 格子状态保留最近一次读回值，回退不产生跳变。
/// </summary>
public sealed partial class ClimateSystem
{
    private GpuSurfaceThermalBackend _gpuThermal;
    private Vector4[] _thermalClassification;
    private readonly HashSet<int> _thermalDirtyIndices = new HashSet<int>();
    private bool _thermalClassificationRefresh;
    private float _thermalSimClock;
    private bool _thermalGpuFailed;
    private bool _thermalFirstSnapshot;

    /// <summary>GPU 地表温度管线接管期间，玩法侧的风/气压同步交由内核完成。</summary>
    internal bool SurfaceThermalActive => _gpuThermal != null && _thermalGpuFailed == false;

    /// <summary>把回读结果拷进 cells 的分片大小：一次全量拷贝会产生 30ms 级单帧尖峰，
    /// 按太阳节拍分片推进，整图在一个周期内渐进刷新。</summary>
    private int _thermalApplyCursor;

    private void ResetGpuThermal()
    {
        bool owned = _gpuThermal != null;
        _gpuThermal?.Dispose(); _gpuThermal = null;
        _thermalClassification = null;
        _thermalDirtyIndices.Clear();
        _thermalClassificationRefresh = false;
        _thermalGpuFailed = false;
        _thermalFirstSnapshot = false;
        _thermalApplyCursor = 0;
        if (owned) Fields?.RestoreCpuSurfaceTextures();
    }

    /// <summary>地表分类：海洋类别、群系幅度类别、沙地/海岸/雪峰/冰冻/岩浆、海拔与河流湿度。</summary>
    private Vector4 BuildThermalClassification(WorldTile tile)
    {
        uint bits = 0;
        string mainId = tile.main_type?.id;
        if (tile.Type?.layer_type == TileLayerType.Ocean)
            bits |= mainId == "shallow_waters" ? 1u : mainId == "close_ocean" ? 2u : 3u;
        string biome = tile.Type?.biome_asset?.id ?? string.Empty;
        uint biomeClass =
            biome == "biome_desert" ? 1u :
            biome == "biome_savanna" ? 2u :
            biome == "biome_permafrost" ? 3u :
            biome == "biome_birch" ? 4u :
            biome == "biome_maple" ? 5u :
            biome == "biome_jungle" || biome == "biome_swamp" ? 6u : 0u;
        bits |= biomeClass << 2;
        if (tile.main_type?.sand == true || tile.Type?.sand == true) bits |= 1u << 5;
        if (IsCoastal(tile)) bits |= 1u << 6;
        if (IsSnowPeak(tile)) bits |= 1u << 7;
        if (IsFrozenClimateSurface(tile)) bits |= 1u << 8;
        if (tile.Type?.lava == true)
        {
            bits |= 1u << 9;
            bits |= (uint)Mathf.Clamp(Mathf.CeilToInt(tile.Type.lava_level), 0, 3) << 10;
        }
        if (tile.Type?.ocean == true) bits |= 1u << 12;
        int pixel = tile.pos.y * MapBox.width + tile.pos.x;
        float contour = pixel >= 0 && pixel < _terrainElevation.Length
            ? _terrainElevation[pixel]
            : GetTerrainElevation(tile);
        return new Vector4(bits, GetTerrainElevation(tile), GetRiverMoisture(tile), contour);
    }

    private void InitializeGpuThermal()
    {
        if (_gpuThermal != null || _thermalGpuFailed) return;
        if (_gpuAtmosphere == null || _cells.Length == 0) return;
        WorldTile[] tiles = World.world.tiles_list;
        if (tiles == null || tiles.Length != _cells.Length) return;
        try
        {
            int count = _cells.Length;
            var initial = new GpuSurfaceThermalBackend.SurfaceState[count];
            var classification = new Vector4[count];
            for (int i = 0; i < count; i++)
            {
                ClimateCell c = _cells[i];
                initial[i] = new GpuSurfaceThermalBackend.SurfaceState {
                    A = new Vector4(c.Temperature, c.Sunlight, c.Humidity, c.CloudCover),
                    B = new Vector4(c.AirTemperatureC, c.RelativeHumidity, c.Pressure, c.RainRate),
                    C = new Vector4(c.Wind.x, c.Wind.y, c.WindSpeed, c.SnowWater),
                    D = new Vector4(c.BiomeTemperature, c.BiomeMoisture,
                        c.BiomeClimateInitialized ? 1f : 0f, (float)c.LastWaterBalance),
                    E = new Vector4((float)c.LastSnowfall, _seasonClock, 0f, 0f)
                };
                classification[i] = BuildThermalClassification(tiles[i]);
            }
            _thermalClassification = classification;
            _gpuThermal = new GpuSurfaceThermalBackend(MapBox.width, MapBox.height,
                initial, classification);
            _thermalSimClock = _seasonClock;
            Fields?.UseGpuSurfaceTextures(_gpuThermal.DisplayTextureA, _gpuThermal.DisplayTextureB);
            Debug.Log("[ClimateWeather GPU] Surface thermal enabled: solar temperature, thermal inertia, atmosphere sync and biome memory run on GPU; display textures written directly.");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[ClimateWeather GPU] Surface thermal unavailable, CPU fallback: " + e.Message);
            _gpuThermal?.Dispose(); _gpuThermal = null;
            _thermalClassification = null;
            _thermalGpuFailed = true;
        }
    }

    private void MarkThermalClassificationDirty(int cellIndex)
    {
        if (_gpuThermal == null) return;
        _thermalDirtyIndices.Add(cellIndex);
    }

    /// <summary>河流湿度场整体发布后刷新分类缓冲的水汽通道。</summary>
    private void MarkThermalRiverMoistureDirty()
    {
        if (_gpuThermal == null) return;
        _thermalClassificationRefresh = true;
    }

    private bool UpdateDynamicTemperaturesGpu()
    {
        if (_thermalGpuFailed || _gpuAtmosphere == null) return false;
        if (_gpuThermal == null)
        {
            InitializeGpuThermal();
            if (_gpuThermal == null) return false;
        }
        if (_gpuThermal.Error != null)
        {
            Debug.LogWarning("[ClimateWeather GPU] Surface thermal falling back to CPU: " + _gpuThermal.Error);
            _gpuThermal.Dispose(); _gpuThermal = null;
            _thermalClassification = null;
            _thermalDirtyIndices.Clear();
            _thermalGpuFailed = true;
            Fields?.RestoreCpuSurfaceTextures();
            return false;
        }
        // 每个太阳节拍推进一小片拷贝；整片读回期间不重复发起新回读。
        ApplyGpuThermalResults();
        if (!_gpuThermal.Pending)
        {
            RefreshThermalClassification();
            DispatchGpuThermal();
        }
        return true;
    }

    private void DispatchGpuThermal()
    {
        float elapsed = Mathf.Clamp(_seasonClock - _thermalSimClock, 0f, 60f);
        if (elapsed <= 0f) elapsed = SolarTickSeconds;
        _thermalSimClock = _seasonClock;
        // 只有上一轮拷贝完成后才刷新 Results，避免同一轮拷贝读到两个时刻的混合快照。
        bool requestReadback = _thermalApplyCursor == 0;
        _gpuThermal.Dispatch(_gpuAtmosphere.LatestStateBuffer, _airWidth, _airHeight, HorizontalWrap,
            _latitudeMinDegrees, _latitudeMaxDegrees, _longitudeMinDegrees, _longitudeMaxDegrees,
            CurrentSolarDeclinationRadians(), CurrentSubsolarLongitudeRadians(),
            _greenhouseGasLevel, SurfaceLightTransmission(), SurfaceSolarHeatTransmission(),
            _ageClimateProfile.SunlightMultiplier, _ageClimateProfile.TemperatureOffsetC,
            (_seasonClock / (SeasonSeconds * 4f)) * Mathf.PI * 2f, elapsed, _seasonClock, _worldSeed,
            requestReadback);
    }

    private void RefreshThermalClassification()
    {
        if (_thermalClassification == null ||
            (!_thermalClassificationRefresh && _thermalDirtyIndices.Count == 0)) return;
        WorldTile[] tiles = World.world.tiles_list;
        if (_thermalClassificationRefresh)
        {
            for (int i = 0; i < _thermalClassification.Length; i++)
            {
                WorldTile tile = tiles[i];
                Vector4 cls = _thermalClassification[i];
                cls.z = tile?.main_type?.ocean == true ? 0f : GetRiverMoisture(tile);
                _thermalClassification[i] = cls;
            }
            _thermalClassificationRefresh = false;
        }
        foreach (int index in _thermalDirtyIndices)
        {
            if (index < 0 || index >= tiles.Length || index >= _thermalClassification.Length) continue;
            _thermalClassification[index] = BuildThermalClassification(tiles[index]);
        }
        _thermalDirtyIndices.Clear();
        _gpuThermal.UpdateClassification(_thermalClassification);
    }

    private void ApplyGpuThermalResults()
    {
        if (!_gpuThermal.Ready || _thermalApplyCursor >= _cells.Length) return;
        Bench.bench("mod.ThermalApply", "cpu");
        var values = _gpuThermal.Results;
        ClimateCell[] cells = _cells;
        int slice = Mathf.Max(1024, cells.Length / 16);
        int end = Mathf.Min(cells.Length, _thermalApplyCursor + slice);
        for (int i = _thermalApplyCursor; i < end; i++)
        {
            var v = values[i];
            ClimateCell c = cells[i];
            c.Temperature = v.A.x;
            c.Sunlight = v.A.y;
            c.Humidity = v.A.z;
            c.CloudCover = v.A.w;
            c.AirTemperatureC = v.B.x;
            c.RelativeHumidity = v.B.y;
            c.Pressure = v.B.z;
            c.RainRate = v.B.w;
            float windX = v.C.x, windY = v.C.y;
            float windMagnitude = Mathf.Sqrt(windX * windX + windY * windY);
            c.Wind = windMagnitude * windMagnitude < 0.000001f
                ? Vector2.zero
                : new Vector2(windX / windMagnitude, windY / windMagnitude);
            c.WindSpeed = Mathf.Clamp01(windMagnitude);
            c.SnowWater = v.C.w;
            c.BiomeTemperature = v.D.x;
            c.BiomeMoisture = v.D.y;
            c.BiomeClimateInitialized = v.D.z > 0.5f;
            c.LastWaterBalance = v.D.w;
            c.LastSnowfall = v.E.x;
            c.LastTemperatureTime = v.E.y;
        }
        _thermalApplyCursor = end >= cells.Length ? 0 : end;
        if (_thermalApplyCursor == 0) _gpuThermal.Consume();
        Bench.benchEnd("mod.ThermalApply", "cpu", false, 0);
        if (!_thermalFirstSnapshot)
        {
            _thermalFirstSnapshot = true;
            Debug.Log("[ClimateWeather GPU] First surface snapshot applied; solar temperature, cloud shading and biome memory computed on GPU.");
        }
    }
}
