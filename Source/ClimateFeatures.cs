using UnityEngine;

namespace ClimateWeather;

// 缓存设置，避免模拟热路径反复读取 PlayerPrefs。旧安装默认保持全部功能。
internal static class ClimateFeatures
{
    internal enum Feature { Climate, BiomeLimits, BiomeChanges, Terrain, Weather, Rivers, Night }
    private static readonly bool[] Values = Load();
    private static bool[] Load()
    {
        var values = new bool[7];
        for (int i = 0; i < values.Length; i++)
            values[i] = PlayerPrefs.GetInt("ClimateWeather.Feature." + (Feature)i, 1) != 0;
        return values;
    }
    internal static bool Enabled(Feature feature) => Values[(int)feature];
    internal static bool Climate => Enabled(Feature.Climate);
    internal static bool Night => Climate && Enabled(Feature.Night);
    internal static void Set(Feature feature, bool value)
    {
        if (Enabled(feature) == value) return;
        Values[(int)feature] = value;
        PlayerPrefs.SetInt("ClimateWeather.Feature." + feature, value ? 1 : 0);
        PlayerPrefs.Save();
        if (feature == Feature.Climate && value) ClimateSystem.Active?.OnClimateResumed();
    }
}

public sealed partial class ClimateSystem
{
    private bool _showFeatureSettings;
    internal void OnClimateResumed()
    {
        // 暂停期间不消费读回；恢复时不能把停用时间误算为 GPU 请求超时。
        _gpuRequestStarted = Time.realtimeSinceStartup;
    }
    private static readonly string[] FeatureLabels = { "气候总开关", "群系扩张气候限制", "群系自动调整 / 裸地补全",
        "地形影响 / 冻结 / 沙化 / 植被", "新天气生成（雨云 / 灾害）", "自动及手动河流生成", "昼夜明暗显示" };
    private void DrawFeatureSettings()
    {
        for (int i = 0; i < FeatureLabels.Length; i++)
        {
            var feature = (ClimateFeatures.Feature)i;
            bool previous = GUI.enabled;
            GUI.enabled = previous && (i == 0 || ClimateFeatures.Climate);
            bool value = GUI.Toggle(new Rect(18, 112 + i * 40, 265, 30),
                ClimateFeatures.Enabled(feature), FeatureLabels[i]);
            ClimateFeatures.Set(feature, value);
            GUI.enabled = previous;
        }
        GUI.Label(new Rect(18, 408, 265, 100), "设置自动保存，关闭气候后子开关保留。\n关闭不回滚已改变的地形和建筑。\n已有天气不删除；原版天气不受此开关限制。\n左右互通不依赖气候总开关。");
        GUI.Label(new Rect(18, 515, 265, 65), "关闭群系限制后，原版可自由扩张。\n若不希望气候再改写群系，\n请同时关闭群系自动调整。");
    }
}
