using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClimateWeather;

internal sealed class GpuAtmosphereBackend : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct State { public Vector4 Thermo, Motion, Ledger, Extra; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Surface { public Vector4 Ground, Geo, Position; }
    private AssetBundle _bundle;
    private static AssetBundle _sharedBundle;
    private static int _bundleUsers;
    private ComputeShader _shader;
    private ComputeBuffer _front, _back, _terrain;
    private int _kernel, _width, _height;
    private bool _disposed;
    internal bool Pending { get; private set; }
    internal bool Ready { get; private set; }
    internal string Error { get; private set; }
    internal State[] Results { get; private set; }
    internal float ResultTime { get; private set; }

    internal GpuAtmosphereBackend(int width, int height, State[] initial)
    {
        try
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
                throw new NotSupportedException("GPU compute/async readback unavailable");
            if (Marshal.SizeOf(typeof(State)) != 64 || Marshal.SizeOf(typeof(Surface)) != 48)
                throw new Exception("GPU structure layout mismatch");
            if (_sharedBundle == null)
                _sharedBundle = AssetBundle.LoadFromFile(Path.GetFullPath(Path.Combine(Application.dataPath,
                    "../Mods/ClimateWeather/Gpu/climateatmosphere")));
            _bundle = _sharedBundle;
            if (_bundle == null) throw new IOException("climateatmosphere bundle missing or incompatible");
            _bundleUsers++;
            _shader = _bundle.LoadAsset<ComputeShader>("Assets/ClimateAtmosphere.compute");
            if (_shader == null) throw new IOException("ClimateAtmosphere kernel missing");
            _kernel = _shader.FindKernel("Step"); _width = width; _height = height;
            _front = new ComputeBuffer(initial.Length, 64); _back = new ComputeBuffer(initial.Length, 64);
            _terrain = new ComputeBuffer(initial.Length, 48);
            _front.SetData(initial); Results = new State[initial.Length];
        }
        catch { Release(); throw; }
    }

    internal void Dispatch(Surface[] input, float time, float latitudeMin, float latitudeMax,
        float longitudeSpan, float declination, float greenhouse, float heat, float ageTemperature, int seed)
    {
        if (Pending || _disposed || Error != null) return;
        Ready = false;
        try
        {
            _terrain.SetData(input);
            _shader.SetInt("Width", _width); _shader.SetInt("Height", _height);
            _shader.SetInt("WrapLongitude", longitudeSpan >= 359f ? 1 : 0);
            _shader.SetFloat("LatitudeMin", latitudeMin); _shader.SetFloat("LatitudeMax", latitudeMax);
            _shader.SetFloat("LongitudeSpan", longitudeSpan); _shader.SetFloat("Clock", time);
            _shader.SetFloat("Declination", declination); _shader.SetFloat("Greenhouse", greenhouse);
            _shader.SetFloat("HeatTransmission", heat); _shader.SetFloat("AgeTemperature", ageTemperature);
            _shader.SetFloat("Seed", seed);
            _shader.SetBuffer(_kernel, "Previous", _front); _shader.SetBuffer(_kernel, "Next", _back);
            _shader.SetBuffer(_kernel, "Terrain", _terrain);
            _shader.Dispatch(_kernel, (_width + 7) / 8, (_height + 7) / 8, 1);
            ComputeBuffer swap = _front; _front = _back; _back = swap;
            Pending = true;
            AsyncGPUReadback.Request(_front, request =>
            {
                try
                {
                    if (_disposed) return;
                    if (request.hasError) throw new Exception("GPU readback error");
                    var data = request.GetData<State>();
                    for (int i = 0; i < Results.Length; i++)
                    {
                        State value = data[i];
                        if (!Finite(value.Thermo) || !Finite(value.Motion) || !Finite(value.Ledger) || !Finite(value.Extra) ||
                            value.Thermo.y < -0.00001f || value.Thermo.z < -0.00001f)
                            throw new Exception("Invalid GPU climate cell " + i + " at " + time +
                                " thermo=" + value.Thermo.ToString("G9") + " motion=" + value.Motion.ToString("G9") +
                                " ledger=" + value.Ledger.ToString("G9") + " extra=" + value.Extra.ToString("G9"));
                        Results[i] = value;
                    }
                    ResultTime = time; Ready = true;
                }
                catch (Exception e) { Error = e.Message; }
                finally { Pending = false; if (_disposed) Release(); }
            });
        }
        catch (Exception e) { Pending = false; Error = e.Message; }
    }
    private static bool Finite(Vector4 v) => Finite(v.x) && Finite(v.y) && Finite(v.z) && Finite(v.w);
    private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    internal void Consume() { Ready = false; }
    public void Dispose() { _disposed = true; if (!Pending) Release(); }
    private void Release()
    {
        _front?.Release(); _front = null; _back?.Release(); _back = null;
        _terrain?.Release(); _terrain = null;
        if (_bundle != null)
        {
            _bundle = null;
            if (--_bundleUsers == 0 && _sharedBundle != null)
            { _sharedBundle.Unload(true); _sharedBundle = null; }
        }
    }
}
