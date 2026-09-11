using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace ClimateWeather;

[HarmonyPatch(typeof(CityBehCheckFarms), nameof(CityBehCheckFarms.behFindTileForFarm))]
internal static class HorizontalFarmExpansion
{
    private static void Postfix(City pCity)
    {
        if (ClimateSystem.Active?.HorizontalWrap != true || MapBox.width <= 18) return;
        Building mill = pCity.getBuildingOfType("type_windmill");
        if (mill?.current_tile == null) return;
        WorldTile origin = mill.current_tile;
        const int radius = 9;
        if (origin.x >= radius && origin.x < MapBox.width - radius) return;
        var zones = new HashSet<TileZone>();
        // 只补风车九格半径越出左右边界的窄带，不增加全城或全图扫描。
        for (int x = origin.x - radius; x <= origin.x + radius; x++)
        {
            if (x >= 0 && x < MapBox.width) continue;
            int wrapped = (x + MapBox.width) % MapBox.width;
            for (int y = Math.Max(0, origin.y - radius); y <= Math.Min(MapBox.height - 1, origin.y + radius); y++)
            {
                TileZone zone = World.world.GetTileSimple(wrapped, y)?.zone;
                if (zone != null && zone.isSameCityHere(pCity)) zones.Add(zone);
            }
        }
        // 复用原版候选容器与地形分类，距离补丁仍将范围限制在半径九格。
        foreach (TileZone zone in zones) CityBehCheckFarms.checkZone(zone, mill, pCity);
    }
}

[HarmonyPatch(typeof(CityBehCheckFarms), nameof(CityBehCheckFarms.checkZone))]
internal static class HorizontalFarmRadius
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var distance = AccessTools.Method(typeof(Toolbox), nameof(Toolbox.SquaredDistTile),
            new[] { typeof(WorldTile), typeof(WorldTile) });
        int matches = 0;
        foreach (CodeInstruction code in instructions)
        {
            if (Equals(code.operand, distance))
            {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(HorizontalWorkTargets), nameof(HorizontalWorkTargets.Distance));
                matches++;
            }
            yield return code;
        }
        if (matches != 1) throw new InvalidOperationException("农田半径补丁未匹配距离检查");
    }
}
