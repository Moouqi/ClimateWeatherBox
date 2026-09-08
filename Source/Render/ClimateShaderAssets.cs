using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ClimateWeather;

/// <summary>
/// 定位并加载模组的 shader AssetBundle。NML 运行时编译的程序集位于临时目录，
/// 不能用程序集路径反查模组目录；这里从游戏 Mods 目录按已知名称定位，
/// 并兼容旧的 ClimateWeather 目录名。GPU 大气后端共享同一解析逻辑。
/// </summary>
internal static class ClimateShaderAssets
{
    private const string BundleName = "climatelayers";
    private static readonly string[] FolderNames = { "ClimateWeatherBox", "ClimateWeather" };

    private static AssetBundle _bundle;
    private static int _users;

    internal static Material CreateMaterial(string shaderName)
    {
        Acquire();
        if (_bundle == null) return null;
        // bundle 内的资产名是文件名（ClimateLayers/ClimateNight）；
        // shader 的声明名（ClimateWeather/Layers）只在 Shader.Find 中有效。
        string suffix = shaderName[(shaderName.LastIndexOf('/') + 1)..];
        Shader shader = _bundle.LoadAsset<Shader>("Climate" + suffix) ??
                        _bundle.LoadAsset<Shader>(shaderName);
        if (shader == null)
        {
            Shader[] all = _bundle.LoadAllAssets<Shader>();
            shader = all.FirstOrDefault(s => s.name.EndsWith(suffix, StringComparison.Ordinal));
            if (shader == null)
            {
                Debug.LogWarning($"[ClimateWeather] bundle 缺少 shader {shaderName}；现有资产: " +
                                 string.Join(", ", _bundle.GetAllAssetNames()));
                return null;
            }
        }
        return new Material(shader) { hideFlags = HideFlags.DontSave };
    }

    /// <summary>加载大气 compute kernel（资产名 = 文件名 ClimateAtmosphere）。</summary>
    internal static ComputeShader LoadCompute(string assetName)
    {
        Acquire();
        if (_bundle == null) return null;
        ComputeShader shader = _bundle.LoadAsset<ComputeShader>(assetName);
        if (shader == null)
            Debug.LogWarning($"[ClimateWeather] bundle 缺少 compute {assetName}");
        return shader;
    }

    /// <summary>在 Mods 目录下定位模组资源文件（如旧的 climateatmosphere bundle）。</summary>
    internal static string LocateFile(string relativePath)
    {
        string mods = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Mods"));
        foreach (string folder in FolderNames)
        {
            string candidate = Path.Combine(mods, folder, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        // 目录被用户重命名时按内容兜底扫描一次。
        try
        {
            foreach (string directory in Directory.GetDirectories(mods))
            {
                string candidate = Path.Combine(directory, relativePath);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (IOException) { }
        return null;
    }

    private static void Acquire()
    {
        if (_bundle != null) { _users++; return; }
        string path = LocateFile($"Gpu/{BundleName}");
        if (path == null)
        {
            Debug.LogWarning($"[ClimateWeather] 未找到 Gpu/{BundleName}；" +
                             "请先运行 build-shaders.ps1 构建 shader 资源包，图层渲染不可用。");
            return;
        }
        _bundle = AssetBundle.LoadFromFile(path);
        if (_bundle == null)
            Debug.LogWarning("[ClimateWeather] shader bundle 加载失败（Unity 版本不匹配？）");
        _users = 1;
    }

    internal static void Release()
    {
        if (_bundle == null) return;
        if (--_users > 0) return;
        _bundle.Unload(false);
        _bundle = null;
        _users = 0;
    }
}
