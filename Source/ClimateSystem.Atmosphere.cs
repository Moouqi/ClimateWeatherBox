using System;
using UnityEngine;

namespace ClimateWeather;

public sealed partial class ClimateSystem
{
    // Coarse atmosphere; the terrain and biome application queues remain tile based.
    private const int AtmosphereStride = 8;
    private struct AirState
    {
        public float Temperature, Vapor, Cloud, Pressure, Rain;
        public Vector2 Velocity;
        public double WaterBalance, Snowfall;
    }
    private AirState[] _air = Array.Empty<AirState>();
    private AirState[] _airNext = Array.Empty<AirState>();
    private int _airWidth, _airHeight;
    private float _airTime;
    private double _airExportedWater;
    private bool AtmosphereReady => _air.Length > 0;

    private static float SaturationWater(float temperature)
    {
        // Water units are a game-scale atmospheric column, not relative humidity.
        float t = Mathf.Clamp(temperature, -70f, 65f);
        return 0.12f * Mathf.Exp(17.625f * t / (243.04f + t));
    }

    private int AirIndex(int x, int y)
    {
        if (HorizontalWrap) x = HorizontalTopology.Wrap(x, _airWidth);
        return Mathf.Clamp(y, 0, _airHeight - 1) * _airWidth + Mathf.Clamp(x, 0, _airWidth - 1);
    }

    private void InitializeContinuousAtmosphere()
    {
        _airWidth = (MapBox.width + AtmosphereStride - 1) / AtmosphereStride;
        _airHeight = (MapBox.height + AtmosphereStride - 1) / AtmosphereStride;
        _air = new AirState[_airWidth * _airHeight];
        _airNext = new AirState[_air.Length];
        _airTime = _seasonClock;
        _airExportedWater = 0;
        for (int y = 0; y < _airHeight; y++)
        for (int x = 0; x < _airWidth; x++)
        {
            int px = Mathf.Min(MapBox.width - 1, x * AtmosphereStride + 4);
            int py = Mathf.Min(MapBox.height - 1, y * AtmosphereStride + 4);
            ClimateCell c = _cells[_cellIndexByPixel[py * MapBox.width + px]];
            float t = Mathf.Clamp(ClimateCelsius(c.Temperature), -65f, 55f);
            _air[y * _airWidth + x] = new AirState { Temperature = t,
                Vapor = SaturationWater(t) * Mathf.Clamp(c.Humidity, 0.2f, 0.85f),
                Pressure = c.Pressure > 0f ? c.Pressure : 1013.25f };
        }
        foreach (ClimateCell c in _cells) { c.LastWaterBalance = 0f; c.LastSnowfall = 0f; }
        for (int i = 0; i < _cells.Length; i++)
            SyncAtmosphere(World.world.tiles_list[i], _cells[i], 0f);
        InitializeGpuAtmosphere();
    }

    private AirState SampleAir(float x, float y)
    {
        float gx = (x - 4f) / AtmosphereStride, gy = (y - 4f) / AtmosphereStride;
        int ix = Mathf.FloorToInt(gx), iy = Mathf.FloorToInt(gy);
        float fx = gx - ix, fy = gy - iy;
        return MixAir(MixAir(_air[AirIndex(ix, iy)], _air[AirIndex(ix + 1, iy)], fx),
            MixAir(_air[AirIndex(ix, iy + 1)], _air[AirIndex(ix + 1, iy + 1)], fx), fy);
    }

    private static AirState MixAir(AirState a, AirState b, float t)
    {
        return new AirState { Temperature = Mathf.Lerp(a.Temperature, b.Temperature, t),
            Vapor = Mathf.Lerp(a.Vapor, b.Vapor, t), Cloud = Mathf.Lerp(a.Cloud, b.Cloud, t),
            Pressure = Mathf.Lerp(a.Pressure, b.Pressure, t), Rain = Mathf.Lerp(a.Rain, b.Rain, t),
            Velocity = Vector2.Lerp(a.Velocity, b.Velocity, t),
            WaterBalance = a.WaterBalance + (b.WaterBalance - a.WaterBalance) * t,
            Snowfall = a.Snowfall + (b.Snowfall - a.Snowfall) * t };
    }

    private void AdvanceContinuousAtmosphere()
    {
        if (!AtmosphereReady) InitializeContinuousAtmosphere();
        ConsumeGpuAtmosphere();
        // Keep unconsumed time: bounded work per frame without changing simulation speed.
        int budget = 4;
        while (_seasonClock - _airTime >= 1f && budget-- > 0)
        {
            if (_gpuAtmosphere != null)
            {
                if (_gpuAtmosphere.Pending) break;
                DispatchGpuAtmosphere(_airTime + 1f);
                _airTime += 1f;
                break;
            }
            StepContinuousAtmosphere();
            _airTime += 1f;
        }
    }

    private void StepContinuousAtmosphere()
    {
        Array.Copy(_air, _airNext, _air.Length);
        WorldTile[] tiles = World.world.tiles_list;
        for (int y = 0; y < _airHeight; y++)
        for (int x = 0; x < _airWidth; x++)
        {
            int i = y * _airWidth + x;
            AirState a = _air[i];
            int px = Mathf.Min(MapBox.width - 1, x * AtmosphereStride + 4);
            int py = Mathf.Min(MapBox.height - 1, y * AtmosphereStride + 4);
            int ci = _cellIndexByPixel[py * MapBox.width + px];
            WorldTile tile = tiles[ci];
            ClimateCell c = _cells[ci];
            bool ocean = tile.Type?.ocean == true;
            AirState l = _air[AirIndex(x - 1, y)], r = _air[AirIndex(x + 1, y)];
            AirState d = _air[AirIndex(x, y - 1)], u = _air[AirIndex(x, y + 1)];
            float lat = GetSignedLatitude(py);
            float dx = Mathf.Max(0.5f, (_longitudeMaxDegrees - _longitudeMinDegrees) /
                _airWidth * Mathf.Max(0.18f, Mathf.Cos(lat * Mathf.PI * 0.5f)));
            float dy = Mathf.Max(0.5f, (_latitudeMaxDegrees - _latitudeMinDegrees) / _airHeight);
            Vector2 force = new Vector2((l.Pressure - r.Pressure) / dx,
                (d.Pressure - u.Pressure) / dy) * 0.10f;
            Vector2 background = GlobalWindBelt(lat, out float strength) * strength;
            Vector2 v = a.Velocity + force + (background - a.Velocity) * 0.006f;
            // Implicit Coriolis solve is stable at the equator and at both poles.
            float f = Mathf.Sin(lat * Mathf.PI * 0.5f) * 0.10f;
            v = new Vector2(v.x + f * v.y, v.y - f * v.x) / (1f + f * f);
            float elevation = GetTerrainElevation(tile);
            Vector2 slope = new Vector2(
                TerrainElevationAt(px + 4, py, elevation) - TerrainElevationAt(px - 4, py, elevation),
                TerrainElevationAt(px, py + 4, elevation) - TerrainElevationAt(px, py - 4, elevation));
            float uphill = Mathf.Max(0f, Vector2.Dot(v, slope));
            if (slope.sqrMagnitude > 0.001f) v -= slope.normalized * uphill * 0.45f;
            v /= 1f + (ocean ? 0.012f : 0.035f + elevation * 0.035f);
            _airNext[i].Velocity = Vector2.ClampMagnitude(v, 1.5f);

            float divergence = (r.Velocity.x - l.Velocity.x) / dx +
                               (u.Velocity.y - d.Velocity.y) / dy;
            // Slow climatological forcing also parameterizes unresolved upper-air exchange.
            float baseline = CalculatePressure(tile, c);
            _airNext[i].Pressure = Mathf.Clamp(a.Pressure + (baseline - a.Pressure) / 180f -
                divergence * 0.12f, 960f, 1055f);
            float ground = Mathf.Clamp(ClimateCelsius(c.Temperature), -70f, 90f);
            _airNext[i].Temperature += (ground - a.Temperature) / (ocean ? 160f : 90f);
            float saturation = SaturationWater(a.Temperature);
            float deficit = Mathf.Clamp01(1f - a.Vapor / saturation);
            float evaporation = (ocean ? 0.0008f : 0.00035f * c.Humidity) *
                Mathf.Lerp(0.12f, 2f, Mathf.InverseLerp(-5f, 45f, ground)) *
                (0.25f + deficit) * (1f + v.magnitude);
            if (IsFrozenClimateSurface(tile)) evaporation *= 0.03f;
            if (tile.Type?.lava == true) evaporation = 0f;
            evaporation = ocean ? evaporation : Mathf.Min(evaporation, c.Humidity * 0.02f);
            _airNext[i].Vapor += evaporation;
            _airNext[i].Temperature -= evaporation * 8f;
            float uplift = Mathf.Clamp(-divergence * 2f + uphill, -0.2f, 0.4f);
            float saturationLifted = SaturationWater(a.Temperature - uplift * 6f);
            float condensation = Mathf.Max(0f, a.Vapor - saturationLifted) * 0.15f;
            float tx = Mathf.Min(0.1f, Mathf.Abs(a.Velocity.x) * 0.025f / dx);
            float ty = Mathf.Min(0.1f, Mathf.Abs(a.Velocity.y) * 0.025f / dy);
            float cloudBudget = Mathf.Max(0f, a.Cloud * (1f - tx - ty));
            float reevaporation = Mathf.Min(cloudBudget, Mathf.Max(0f, saturation - a.Vapor) * 0.015f);
            _airNext[i].Vapor += reevaporation - condensation;
            _airNext[i].Cloud += condensation - reevaporation;
            _airNext[i].Temperature += (condensation - reevaporation) * 8f;
            float rain = Mathf.Min(Mathf.Max(0f, cloudBudget - reevaporation), Mathf.Max(0f, a.Cloud - 0.035f) * 0.025f);
            _airNext[i].Cloud -= rain;
            _airNext[i].Rain = rain;
            // Keep precipitation totals above water too: coast tiles interpolate this column.
            _airNext[i].WaterBalance += (a.Temperature > 0f ? rain : 0f) - (ocean ? 0f : evaporation);
            _airNext[i].Snowfall += a.Temperature <= 0f ? rain : 0f;
            // Conservative donor-cell transport. At most 20% leaves in one step.
            TransferAir(i, AirTransportIndex(x + (a.Velocity.x >= 0 ? 1 : -1), y), tx);
            TransferAir(i, AirTransportIndex(x, y + (a.Velocity.y >= 0 ? 1 : -1)), ty);
        }
        AirState[] swap = _air; _air = _airNext; _airNext = swap;
    }

    private int AirTransportIndex(int x, int y)
    {
        bool global = HorizontalWrap;
        if ((!global && (x < 0 || x >= _airWidth)) ||
            (y < 0 && _latitudeMinDegrees > -89.9f) ||
            (y >= _airHeight && _latitudeMaxDegrees < 89.9f)) return -1;
        return AirIndex(x, y);
    }

    private void TransferAir(int source, int destination, float amount)
    {
        if (source == destination) return;
        float vapor = _air[source].Vapor * amount, cloud = _air[source].Cloud * amount;
        if (destination < 0)
        {
            _airNext[source].Vapor -= vapor; _airNext[source].Cloud -= cloud;
            _airExportedWater += vapor + cloud;
            return;
        }
        _airNext[source].Vapor -= vapor; _airNext[destination].Vapor += vapor;
        _airNext[source].Cloud -= cloud; _airNext[destination].Cloud += cloud;
        // Pairwise heat exchange preserves the mean without advecting Celsius mass.
        float heat = (_air[source].Temperature - _air[destination].Temperature) * amount;
        _airNext[source].Temperature -= heat; _airNext[destination].Temperature += heat;
    }

    private void SyncAtmosphere(WorldTile tile, ClimateCell cell, float elapsed)
    {
        if (!AtmosphereReady) return;
        AirState a = SampleAir(tile.pos.x, tile.pos.y);
        cell.AirTemperatureC = a.Temperature;
        cell.RelativeHumidity = Mathf.Clamp01(a.Vapor / SaturationWater(a.Temperature));
        cell.Pressure = a.Pressure;
        cell.WindSpeed = Mathf.Clamp01(a.Velocity.magnitude);
        cell.Wind = a.Velocity.sqrMagnitude < 0.000001f ? Vector2.zero : a.Velocity.normalized;
        cell.CloudCover = 1f - Mathf.Exp(-a.Cloud * 8f);
        cell.RainRate = a.Rain;
        if (tile.Type?.ocean != true && elapsed > 0f)
        {
            cell.SnowWater += Mathf.Max(0f, (float)(a.Snowfall - cell.LastSnowfall));
            float melt = Mathf.Min(cell.SnowWater, Mathf.Max(0f, ClimateCelsius(cell.Temperature)) * elapsed * 0.0001f);
            cell.SnowWater -= melt;
            cell.Humidity = Mathf.Clamp01(cell.Humidity + (float)(a.WaterBalance - cell.LastWaterBalance) + melt);
            float river = GetRiverMoisture(tile);
            cell.Humidity += Mathf.Max(0f, river - cell.Humidity) * (1f - Mathf.Exp(-elapsed / 90f));
        }
        if (elapsed > 0f)
        {
            cell.LastWaterBalance = a.WaterBalance;
            cell.LastSnowfall = a.Snowfall;
        }
    }
}
