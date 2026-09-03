using HarmonyLib;
using NeoModLoader.api;

namespace ClimateWeather;

public sealed class ModClass : BasicMod<ModClass>
{
    private ClimateSystem _climate;

    protected override void OnModLoad()
    {
        _climate = gameObject.AddComponent<ClimateSystem>();
        new Harmony("WB.CLIMATE.WEATHER").PatchAll();
    }
}
