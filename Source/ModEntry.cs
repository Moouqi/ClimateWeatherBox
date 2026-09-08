using HarmonyLib;
using NeoModLoader.api;

namespace ClimateWeather;

public sealed class ModClass : BasicMod<ModClass>
{
    private ClimateSystem _climate;

    protected override void OnModLoad()
    {
        gameObject.AddComponent<GpuCapabilityProbe>();
        _climate = gameObject.AddComponent<ClimateSystem>();
        gameObject.AddComponent<ClimateLayerRenderer>();
        gameObject.AddComponent<HorizontalSeamPreview>();
        gameObject.AddComponent<HorizontalCameraWrap>();
        new Harmony("WB.CLIMATE.WEATHER").PatchAll();
    }
}
