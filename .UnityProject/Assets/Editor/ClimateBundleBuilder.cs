using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 打包气候渲染 shader 为 AssetBundle。产物输出到模组的 Gpu 目录，
/// 运行时由 ClimateShaderAssets 加载。必须使用与游戏一致的 Unity 版本构建。
/// </summary>
public static class ClimateBundleBuilder
{
    private const string BundleName = "climatelayers";

    public static void Build()
    {
        string assets = Application.dataPath;
        string[] shaderPaths =
        {
            "Assets/ClimateNight.shader",
            "Assets/ClimateLayers.shader",
            "Assets/ClimateLines.shader",
            "Assets/ClimateAtmosphere.compute",
            "Assets/SurfaceThermal.compute",
        };
        foreach (string path in shaderPaths)
            if (!File.Exists(Path.Combine(assets, "..", path)))
                throw new FileNotFoundException("缺少 shader 资产: " + path);

        AssetBundleBuild build = new AssetBundleBuild
        {
            assetBundleName = BundleName,
            assetBundleVariant = string.Empty,
            assetNames = shaderPaths,
        };
        string outputDirectory = Path.GetFullPath(Path.Combine(assets, "..", "..", "Gpu"));
        Directory.CreateDirectory(outputDirectory);
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
            outputDirectory, new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression,
            EditorUserBuildSettings.activeBuildTarget);
        if (manifest == null || !manifest.GetAllAssetBundles().Contains(BundleName))
            throw new IOException("AssetBundle 构建失败");

        string bundlePath = Path.Combine(outputDirectory, BundleName);
        Debug.Log($"[ClimateWeather] Shader bundle 已构建: {bundlePath} " +
                  $"({new FileInfo(bundlePath).Length} bytes)");
    }
}
