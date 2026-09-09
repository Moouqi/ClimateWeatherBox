using System;
using UnityEngine;

namespace ClimateWeather;

/// <summary>
/// 气候字段纹理集合：渲染的唯一数据来源。模拟侧写入 CPU 数组并标记脏，
/// 上传按节流周期统一执行；shader（图层/夜幕）只读取这些纹理，
/// CPU 与 GPU 大气路径写入同一组字段，渲染层不感知数据来源。
/// 纹理行 0 对应地图南边（世界 y=0），与世界空间面片的 uv.y=0 对齐。
/// </summary>
internal sealed class ClimateFieldSet : IDisposable
{
    // 地表字段（地图等大）：温度(0-1)、土壤湿度、空气相对湿度、降水强度。
    // GPU 地表温度启用时被 compute 直写的 RenderTexture 接管，CPU 不再上传。
    internal Texture SurfaceA;
    // 地表字段：云量、等高面、冻结、日照。
    internal Texture SurfaceB;
    // 大气网格（低分辨率）：气压 hPa、风.x、风.y、水汽。
    // GPU 大气启用时被 compute 直接写入的 RenderTexture 接管，CPU 不再上传。
    internal Texture Atmos;
    // 灯光透光蒙版（地图 1/4 分辨率）：r=1 无灯光，越小夜幕越透。
    internal Texture2D LightMask;

    internal Color[] SurfaceAPixels = Array.Empty<Color>();
    internal Color[] SurfaceBPixels = Array.Empty<Color>();
    internal Color[] AtmosPixels = Array.Empty<Color>();
    internal Color32[] LightPixels = Array.Empty<Color32>();

    internal int MapWidth { get; private set; }
    internal int MapHeight { get; private set; }
    internal bool Valid => SurfaceA != null;

    private bool _surfaceDirty, _atmosDirty, _lightDirty;
    private bool _atmosOwnedByGpu;
    private bool _surfaceOwnedByGpu;
    private Texture2D _cpuAtmos;
    private bool _wrapAtmosLongitude;
    private Texture2D _cpuSurfaceA, _cpuSurfaceB;

    internal void ConfigureAtmosphereWrap(bool wrapLongitude)
    {
        _wrapAtmosLongitude = wrapLongitude;
        ApplyAtmosphereWrap(_cpuAtmos);
        if (Atmos != _cpuAtmos) ApplyAtmosphereWrap(Atmos);
    }

    private void ApplyAtmosphereWrap(Texture texture)
    {
        if (texture == null) return;
        // 等压线的双线性采样与五点平滑都必须跨缝，只有 U 轴允许循环。
        // 同时配置备用 CPU 纹理，避免 GPU 回退后重新出现断线。
        TextureWrapMode u = _wrapAtmosLongitude ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
        if (texture.wrapModeU != u) texture.wrapModeU = u;
        if (texture.wrapModeV != TextureWrapMode.Clamp) texture.wrapModeV = TextureWrapMode.Clamp;
    }
    private int _airWidth, _airHeight;
    private float _nextSurfaceUpload, _nextAtmosUpload;

    private const float SurfaceUploadSeconds = 0.25f;
    private const float AtmosUploadSeconds = 0.5f;

    public void Dispose()
    {
        // CPU 纹理始终归本类所有；SurfaceA/B 引用可能被 GPU 接管为 RT，
        // 回退时把引用切回 CPU 纹理即可，绝不销毁后端的 RT。
        if (_cpuSurfaceA != null) UnityEngine.Object.Destroy(_cpuSurfaceA);
        if (_cpuSurfaceB != null) UnityEngine.Object.Destroy(_cpuSurfaceB);
        if (_cpuAtmos != null) UnityEngine.Object.Destroy(_cpuAtmos);
        if (LightMask != null) UnityEngine.Object.Destroy(LightMask);
        SurfaceA = SurfaceB = LightMask = null;
        Atmos = _cpuAtmos = null;
        _cpuSurfaceA = _cpuSurfaceB = null;
    }

    internal void Ensure(int mapWidth, int mapHeight, int airWidth, int airHeight)
    {
        if (Valid && MapWidth == mapWidth && MapHeight == mapHeight &&
            _cpuSurfaceA != null && _cpuAtmos != null && _cpuAtmos.width == airWidth) return;
        Dispose();
        MapWidth = mapWidth;
        MapHeight = mapHeight;
        _airWidth = airWidth;
        _airHeight = airHeight;
        _cpuSurfaceA = NewTexture(mapWidth, mapHeight, TextureFormat.RGBAFloat, "ClimateSurfaceA", FilterMode.Point);
        _cpuSurfaceB = NewTexture(mapWidth, mapHeight, TextureFormat.RGBAFloat, "ClimateSurfaceB", FilterMode.Point);
        SurfaceA = _cpuSurfaceA;
        SurfaceB = _cpuSurfaceB;
        _surfaceOwnedByGpu = false;
        _cpuAtmos = NewTexture(airWidth, airHeight, TextureFormat.RGBAFloat, "ClimateAtmos", FilterMode.Bilinear);
        Atmos = _cpuAtmos;
        ApplyAtmosphereWrap(Atmos);
        int lightWidth = Mathf.Max(64, mapWidth / 4);
        int lightHeight = Mathf.Max(64, mapHeight / 4);
        LightMask = NewTexture(lightWidth, lightHeight, TextureFormat.RGBA32, "ClimateLightMask", FilterMode.Bilinear);
        SurfaceAPixels = new Color[mapWidth * mapHeight];
        SurfaceBPixels = new Color[mapWidth * mapHeight];
        AtmosPixels = new Color[airWidth * airHeight];
        LightPixels = new Color32[lightWidth * lightHeight];
        ClearLightMask();
        _surfaceDirty = _atmosDirty = _lightDirty = true;
        _nextSurfaceUpload = _nextAtmosUpload = 0f;
    }

    /// <summary>GPU 大气后端接管显示纹理：直写 RT，CPU 停止上传。</summary>
    internal void UseGpuAtmosTexture(Texture display)
    {
        if (display == null) return;
        Atmos = display;
        ApplyAtmosphereWrap(Atmos);
        _atmosOwnedByGpu = true;
        _atmosDirty = false;
    }

    /// <summary>GPU 回退时恢复 CPU 上传路径。</summary>
    internal void RestoreCpuAtmosTexture()
    {
        if (!_atmosOwnedByGpu) return;
        _atmosOwnedByGpu = false;
        Atmos = _cpuAtmos;
        ApplyAtmosphereWrap(Atmos);
        _atmosDirty = true;
        _nextAtmosUpload = 0f;
    }

    /// <summary>GPU 地表温度后端接管地表字段纹理：compute 直写 RT，CPU 停止填充与上传。</summary>
    internal void UseGpuSurfaceTextures(Texture displayA, Texture displayB)
    {
        if (displayA == null || displayB == null) return;
        SurfaceA = displayA;
        SurfaceB = displayB;
        _surfaceOwnedByGpu = true;
        _surfaceDirty = false;
    }

    /// <summary>GPU 地表温度回退时把引用切回 CPU 纹理并恢复上传路径。</summary>
    internal void RestoreCpuSurfaceTextures()
    {
        if (!_surfaceOwnedByGpu) return;
        _surfaceOwnedByGpu = false;
        SurfaceA = _cpuSurfaceA;
        SurfaceB = _cpuSurfaceB;
        _surfaceDirty = true;
        _nextSurfaceUpload = 0f;
    }

    internal bool SurfaceOwnedByGpu => _surfaceOwnedByGpu;

    private static Texture2D NewTexture(int width, int height, TextureFormat format,
        string name, FilterMode filter)
    {
        return new Texture2D(width, height, format, false)
        {
            name = name,
            filterMode = filter,
            wrapMode = TextureWrapMode.Clamp,
        };
    }

    internal void MarkSurfaceDirty() => _surfaceDirty = true;
    internal void MarkAtmosDirty() => _atmosDirty = true;
    internal void MarkLightDirty() => _lightDirty = true;
    internal bool AtmosOwnedByGpu => _atmosOwnedByGpu;

    /// <summary>按节流周期把脏字段上传到 GPU；在 ClimateSystem.Update 中每帧调用。</summary>
    internal void Update()
    {
        if (!Valid) return;
        float now = Time.unscaledTime;
        if (_surfaceDirty && !_surfaceOwnedByGpu && now >= _nextSurfaceUpload)
        {
            _nextSurfaceUpload = now + SurfaceUploadSeconds;
            _surfaceDirty = false;
            var cpuA = (Texture2D)SurfaceA;
            var cpuB = (Texture2D)SurfaceB;
            cpuA.SetPixels(SurfaceAPixels);
            cpuA.Apply(false, false);
            cpuB.SetPixels(SurfaceBPixels);
            cpuB.Apply(false, false);
        }
        if (_atmosDirty && !_atmosOwnedByGpu && now >= _nextAtmosUpload)
        {
            _nextAtmosUpload = now + AtmosUploadSeconds;
            _atmosDirty = false;
            _cpuAtmos.SetPixels(AtmosPixels);
            _cpuAtmos.Apply(false, false);
        }
        if (_lightDirty)
        {
            _lightDirty = false;
            LightMask.SetPixels32(LightPixels);
            LightMask.Apply(false, false);
        }
    }

    internal void ClearLightMask()
    {
        for (int i = 0; i < LightPixels.Length; i++) LightPixels[i] = new Color32(255, 255, 255, 255);
        MarkLightDirty();
    }
}
