using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ai.behaviours;

namespace ClimateWeather;

// 只改变原版合格候选的距离排序，不绕过资源、地形、占用或布局筛选。
[HarmonyPatch(typeof(CityBehBuild), nameof(CityBehBuild.tryToBuildInZones))]
internal static class HorizontalCityPlacement
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo distance = AccessTools.Method(typeof(Toolbox), nameof(Toolbox.SquaredDistTile),
            new[] { typeof(WorldTile), typeof(WorldTile) });
        int count = 0;
        foreach (CodeInstruction code in instructions)
        {
            if (Equals(code.operand, distance))
            {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(HorizontalWorkTargets), "Distance");
                count++;
            }
            yield return code;
        }
        if (count != 1) throw new InvalidOperationException("城市选址补丁未匹配距离排序");
    }
}

[HarmonyPatch(typeof(TownPlans), nameof(TownPlans.isPassableCross))]
internal static class HorizontalCrossPlan
{
    private static bool Prefix(TileZone pZone, TileZone pCityZone, ref bool __result)
    {
        if (ClimateSystem.Active?.HorizontalWrap != true || pZone == null || pCityZone == null) return true;
        // 保持十字臂宽和南北距离，只将东西差值折算成最近镜像。
        int dxSquared = HorizontalTerritoryQueries.Distance(pZone.x, 0, pCityZone.x, 0);
        __result = dxSquared <= 1 || Math.Abs(pZone.y - pCityZone.y) <= 1;
        return false;
    }
}

[HarmonyPatch(typeof(TownPlans), nameof(TownPlans.isInPassableRing))]
internal static class HorizontalRingPlan
{
    private static bool Prefix(TileZone pZone, TileZone pCityCenterZone, ref bool __result)
    {
        if (ClimateSystem.Active?.HorizontalWrap != true || pZone == null || pCityCenterZone == null) return true;
        // 仍使用原版传入的圆心（地图圆心或城市圆心），不改变环带宽度。
        float distance = UnityEngine.Mathf.Sqrt(HorizontalTerritoryQueries.Distance(
            pZone.x, pZone.y, pCityCenterZone.x, pCityCenterZone.y));
        __result = distance % 2f >= 1f;
        return false;
    }
}
