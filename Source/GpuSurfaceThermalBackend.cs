using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClimateWeather;

/// <summary>
/// GPU 地表温度后端：SurfaceThermal.compute 一次推进全图所有格子的
/// 太阳温度、热惯性、大气同步与群系气候记忆，异步回读把状态交还
/// CPU 的群系判定与显示逻辑。大气状态缓冲区由 GpuAtmosphereBackend
/// 持有，本后端每次派发时只读引用它。任何无效数据都会报告错误并由
/// 调用方回退到 CPU 批处理。
/// </summary>
internal sealed class GpuSurfaceThermalBackend : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct SurfaceState { public Vector4 A, B, C, D, E; }

    private const int StateSize = 80;
    private ComputeShader _shader;
    private ComputeBuffer _front, _back, _classification;
    private RenderTexture _displayA, _displayB;
    private int _kernel, _width, _height;
    private bool _disposed;

    internal bool Pending { get; private set; }
    internal bool Ready { get; private set; }
    internal string Error { get; private set; }
    internal SurfaceState[] Results { get; private set; }
    internal float ResultClock { get; private set; }
    internal RenderTexture DisplayTextureA => _displayA;
    internal RenderTexture DisplayTextureB => _displayB;

    internal GpuSurfaceThermalBackend(int width, int height, SurfaceState[] initial,
        Vector4[] classification)
    {
        try
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
                throw new NotSupportedException("GPU compute/async readback unavailable");
            if (Marshal.SizeOf(typeof(SurfaceState)) != StateSize)
                throw new Exception("GPU surface state layout mismatch");
            _shader = ClimateShaderAssets.LoadCompute("SurfaceThermal");
            if (_shader == null) throw new IOException("SurfaceThermal kernel missing");
            _kernel = _shader.FindKernel("Step");
            _width = width; _height = height;
            _front = new ComputeBuffer(initial.Length, StateSize);
            _back = new ComputeBuffer(initial.Length, StateSize);
            _classification = new ComputeBuffer(classification.Length, 16);
            _displayA = NewDisplay("ClimateSurfaceADisplay");
            _displayB = NewDisplay("ClimateSurfaceBDisplay");
            _front.SetData(initial);
            _classification.SetData(classification);
            Results = new SurfaceState[initial.Length];
        }
        catch { Release(); throw; }
    }

    private RenderTexture NewDisplay(string name)
    {
        var texture = new RenderTexture(_width, _height, 0, RenderTextureFormat.ARGBFloat)
        {
            name = name,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            enableRandomWrite = true,
        };
        texture.Create();
        return texture;
    }

    /// <summary>更新地表分类缓冲（海洋/群系/冰冻/岩浆/河流湿度变化后调用）。</summary>
    internal void UpdateClassification(Vector4[] classification)
    {
        if (_disposed || classification.Length != _classification.count) return;
        _classification.SetData(classification);
    }

    internal void Dispatch(ComputeBuffer airState, int airWidth, int airHeight,
        bool wrapLongitude, float latitudeMin, float latitudeMax,
        float longitudeMin, float longitudeMax, float declinationRadians,
        float subsolarRadians, float greenhouse, float lightTransmission,
        float heatTransmission, float ageSunlight, float ageTemperature,
        float seasonPhase, float elapsed, float clock, int seed, bool requestReadback)
    {
        if (Pending || _disposed || Error != null) return;
        // 只有发起新回读时才作废旧快照标记；无回读的派发继续消费同一份 Results。
        if (requestReadback) Ready = false;
        try
        {
            if (airState == null) throw new InvalidOperationException("air state buffer missing");
            _shader.SetInt("Width", _width); _shader.SetInt("Height", _height);
            _shader.SetInt("AirWidth", airWidth); _shader.SetInt("AirHeight", airHeight);
            _shader.SetInt("WrapLongitude", wrapLongitude ? 1 : 0);
            _shader.SetFloat("LatitudeMin", latitudeMin); _shader.SetFloat("LatitudeMax", latitudeMax);
            _shader.SetFloat("LongitudeMin", longitudeMin); _shader.SetFloat("LongitudeMax", longitudeMax);
            _shader.SetFloat("Declination", declinationRadians);
            _shader.SetFloat("SubsolarLongitude", subsolarRadians);
            _shader.SetFloat("Greenhouse", greenhouse);
            _shader.SetFloat("LightTransmission", lightTransmission);
            _shader.SetFloat("HeatTransmission", heatTransmission);
            _shader.SetFloat("AgeSunlight", ageSunlight);
            _shader.SetFloat("AgeTemperature", ageTemperature);
            _shader.SetFloat("SeasonPhase", seasonPhase);
            _shader.SetFloat("Elapsed", elapsed);
            _shader.SetFloat("Clock", clock);
            _shader.SetFloat("Seed", seed);
            _shader.SetBuffer(_kernel, "Previous", _front);
            _shader.SetBuffer(_kernel, "Next", _back);
            _shader.SetBuffer(_kernel, "Classification", _classification);
            _shader.SetBuffer(_kernel, "Air", airState);
            _shader.SetTexture(_kernel, "SurfaceADisplay", _displayA);
            _shader.SetTexture(_kernel, "SurfaceBDisplay", _displayB);
            _shader.Dispatch(_kernel, (_width + 7) / 8, (_height + 7) / 8, 1);
            ComputeBuffer swap = _front; _front = _back; _back = swap;
            if (requestReadback)
            {
                Pending = true;
                float dispatchedClock = clock;
                AsyncGPUReadback.Request(_front, request =>
                {
                    try
                    {
                        if (_disposed) return;
                    if (request.hasError) throw new Exception("GPU surface readback error");
                    var data = request.GetData<SurfaceState>();
                    for (int i = 0; i < Results.Length; i++) Results[i] = data[i];
                    // 抽样校验：NaN/越界会随输运扩散到大量格子，抽样足以发现系统性损坏。
                    for (int i = 0; i < Results.Length; i += 97)
                    {
                        SurfaceState value = Results[i];
                        if (!Finite(value.A) || !Finite(value.B) || !Finite(value.C) ||
                            !Finite(value.D) || !Finite(value.E) ||
                            value.A.x < -0.001f || value.A.x > 1.001f ||
                            value.A.z < -0.001f || value.A.z > 1.001f)
                            throw new Exception("Invalid GPU surface cell " + i + " at " +
                                dispatchedClock + " A=" + value.A.ToString("G9") +
                                " B=" + value.B.ToString("G9") + " C=" + value.C.ToString("G9"));
                    }
                        ResultClock = dispatchedClock; Ready = true;
                    }
                    catch (Exception e) { Error = e.Message; }
                    finally { Pending = false; if (_disposed) Release(); }
                });
            }
        }
        catch (Exception e) { Pending = false; Error = e.Message; }
    }

    private static bool Finite(Vector4 v) => Finite(v.x) && Finite(v.y) && Finite(v.z) && Finite(v.w);
    private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    internal void Consume() { Ready = false; }
    public void Dispose() { _disposed = true; if (!Pending) Release(); }
    private void Release()
    {
        _front?.Release(); _front = null;
        _back?.Release(); _back = null;
        _classification?.Release(); _classification = null;
        if (_displayA != null) { _displayA.Release(); UnityEngine.Object.Destroy(_displayA); _displayA = null; }
        if (_displayB != null) { _displayB.Release(); UnityEngine.Object.Destroy(_displayB); _displayB = null; }
        _shader = null;
        ClimateShaderAssets.Release();
    }
}
