using System;
using UnityEngine;

namespace ClimateWeather;

public sealed partial class ClimateSystem
{
    private GpuAtmosphereBackend _gpuAtmosphere;
    private GpuAtmosphereBackend.Surface[] _gpuSurface;
    private float _gpuSnapshotTime, _gpuRequestStarted;
    private bool _gpuFirstSnapshot;

    private void InitializeGpuAtmosphere()
    {
        _gpuAtmosphere?.Dispose(); _gpuAtmosphere = null;
        _gpuSnapshotTime = _airTime; _gpuFirstSnapshot = false;
        try
        {
            var initial = new GpuAtmosphereBackend.State[_air.Length];
            _gpuSurface = new GpuAtmosphereBackend.Surface[_air.Length];
            for (int i = 0; i < _air.Length; i++)
                initial[i] = new GpuAtmosphereBackend.State {
                    Thermo = new Vector4(_air[i].Temperature, _air[i].Vapor, _air[i].Cloud, _air[i].Pressure),
                    Motion = new Vector4(_air[i].Rain, _air[i].Velocity.x, _air[i].Velocity.y, 0),
                    Ledger = new Vector4((float)_air[i].WaterBalance, (float)_air[i].Snowfall, 0, 0) };
            _gpuAtmosphere = new GpuAtmosphereBackend(_airWidth, _airHeight, initial);
            Debug.Log("[ClimateWeather GPU] Atmosphere enabled: " + _airWidth + "x" + _airHeight + "; asynchronous snapshots.");
        }
        catch (Exception e) { Debug.LogWarning("[ClimateWeather GPU] Atmosphere unavailable, CPU fallback: " + e.Message); }
    }

    private void ConsumeGpuAtmosphere()
    {
        if (_gpuAtmosphere == null) return;
        if (_gpuAtmosphere.Error != null || (_gpuAtmosphere.Pending && Time.realtimeSinceStartup - _gpuRequestStarted > 30f))
        {
            Debug.LogWarning("[ClimateWeather GPU] Returning to last complete CPU snapshot: " + (_gpuAtmosphere.Error ?? "timeout"));
            _gpuAtmosphere.Dispose(); _gpuAtmosphere = null;
            _airTime = _gpuSnapshotTime;
            return;
        }
        if (!_gpuAtmosphere.Ready) return;
        var values = _gpuAtmosphere.Results;
        for (int i = 0; i < _air.Length; i++)
        {
            var v = values[i];
            _air[i] = new AirState { Temperature = v.Thermo.x, Vapor = v.Thermo.y, Cloud = v.Thermo.z,
                Pressure = v.Thermo.w, Rain = v.Motion.x, Velocity = new Vector2(v.Motion.y, v.Motion.z),
                WaterBalance = (double)v.Ledger.x - v.Ledger.z, Snowfall = (double)v.Ledger.y - v.Ledger.w };
        }
        _gpuSnapshotTime = _gpuAtmosphere.ResultTime;
        _gpuAtmosphere.Consume();
        if (!_gpuFirstSnapshot)
        {
            _gpuFirstSnapshot = true;
            Debug.Log("[ClimateWeather GPU] First atmosphere snapshot applied; wind/pressure/air heat/vapor/cloud/rain computed on GPU.");
        }
    }

    private void DispatchGpuAtmosphere(float time)
    {
        WorldTile[] tiles = World.world.tiles_list;
        for (int y = 0; y < _airHeight; y++)
        for (int x = 0; x < _airWidth; x++)
        {
            int px = Math.Min(MapBox.width - 1, x * AtmosphereStride + 4);
            int py = Math.Min(MapBox.height - 1, y * AtmosphereStride + 4);
            int ci = _cellIndexByPixel[py * MapBox.width + px];
            WorldTile tile = tiles[ci]; ClimateCell c = _cells[ci];
            bool ocean = tile.Type?.ocean == true;
            float elevation = GetTerrainElevation(tile);
            _gpuSurface[y * _airWidth + x] = new GpuAtmosphereBackend.Surface {
                Ground = new Vector4(ClimateCelsius(c.Temperature), c.Humidity, ocean ? 1 : 0, elevation),
                Geo = new Vector4(TerrainElevationAt(px + 4, py, elevation) - TerrainElevationAt(px - 4, py, elevation),
                    TerrainElevationAt(px, py + 4, elevation) - TerrainElevationAt(px, py - 4, elevation),
                    GetSignedLatitude(py), IsFrozenClimateSurface(tile) ? 1 : 0),
                Position = new Vector4(px, py, OceanInfluence(tile, ocean), tile.Type?.lava == true ? 1 : 0) };
        }
        _gpuRequestStarted = Time.realtimeSinceStartup;
        _gpuAtmosphere.Dispatch(_gpuSurface, time, _latitudeMinDegrees, _latitudeMaxDegrees,
            _longitudeMaxDegrees - _longitudeMinDegrees, CurrentSolarDeclinationRadians() * Mathf.Rad2Deg,
            _greenhouseGasLevel, SurfaceSolarHeatTransmission(), _ageClimateProfile.TemperatureOffsetC, _worldSeed);
    }
}
