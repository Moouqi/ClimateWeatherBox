using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClimateWeather;

// One-shot startup diagnostic. Does not alter climate state or game terrain.
public sealed class GpuCapabilityProbe : MonoBehaviour
{
    private ComputeBuffer _buffer;
    private AssetBundle _bundle;
    private bool _pending;
    private bool _destroyed;
    private float _deadline;

    private void Start()
    {
        try
        {
            Debug.Log("[ClimateWeather GPU] Device=" + SystemInfo.graphicsDeviceName +
                " API=" + SystemInfo.graphicsDeviceType + " Compute=" + SystemInfo.supportsComputeShaders +
                " AsyncReadback=" + SystemInfo.supportsAsyncGPUReadback);
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
                throw new NotSupportedException("ComputeShader or async readback unavailable");
            string path = ClimateShaderAssets.LocateFile("Gpu/climatecompute")
                ?? throw new IOException("climatecompute bundle not found in Mods directory");
            _bundle = AssetBundle.LoadFromFile(path);
            if (_bundle == null) throw new IOException("Unable to load " + path);
            ComputeShader shader = _bundle.LoadAsset<ComputeShader>("Assets/ClimateProbe.compute");
            if (shader == null) throw new IOException("Probe shader missing");
            _buffer = new ComputeBuffer(257, sizeof(float));
            int kernel = shader.FindKernel("Verify");
            shader.SetInt("Count", 257);
            shader.SetBuffer(kernel, "Results", _buffer);
            shader.Dispatch(kernel, 5, 1, 1);
            _deadline = Time.realtimeSinceStartup + 30f;
            _pending = true;
            AsyncGPUReadback.Request(_buffer, OnReadback);
        }
        catch (Exception e)
        {
            _pending = false;
            Debug.LogWarning("[ClimateWeather GPU] FAIL; CPU simulation retained. " + e);
            Release();
        }
    }

    private void OnReadback(AsyncGPUReadbackRequest request)
    {
        _pending = false;
        try
        {
            if (_destroyed) return;
            if (request.hasError) throw new Exception("GPU readback failed");
            var values = request.GetData<float>();
            if (values.Length != 257) throw new Exception("Unexpected result length");
            for (int i = 0; i < values.Length; i++)
                if (values[i] != i * 3f + 7f) throw new Exception("Mismatch at " + i);
            Debug.Log("[ClimateWeather GPU] PASS: AssetBundle + ComputeShader.Dispatch + AsyncGPUReadback; 257/257 correct. Atmosphere backend reports its status separately.");
        }
        catch (Exception e) { Debug.LogWarning("[ClimateWeather GPU] FAIL: " + e); }
        finally { Release(); }
    }

    private void Update()
    {
        if (!_pending || Time.realtimeSinceStartup < _deadline) return;
        Debug.LogWarning("[ClimateWeather GPU] Readback timeout; CPU simulation retained; waiting for safe buffer release.");
        _deadline = float.PositiveInfinity;
    }
    private void OnDestroy() { _destroyed = true; if (!_pending) Release(); }
    private void Release()
    {
        if (_buffer != null) { _buffer.Release(); _buffer = null; }
        if (_bundle != null) { _bundle.Unload(true); _bundle = null; }
    }
}
