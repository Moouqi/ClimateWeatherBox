using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ai.behaviours;

namespace ClimateWeather;

[HarmonyPatch(typeof(BehFindBuilding), nameof(BehFindBuilding.findBuildingType))]
internal static class HorizontalEquipmentRepair
{
    private sealed class Cursor { internal int Index; }
    private static readonly ConditionalWeakTable<Actor, Cursor> Cursors = new();

    private static void Postfix(BehFindBuilding __instance, Actor pActor, string pType, ref Building __result)
    {
        // 兵营查询也用于其他任务，必须限制到装备维修，不能顺带改变军事行为。
        if (pType != "type_barracks" || pActor?.ai?.task?.id != "repair_equipment" ||
            ClimateSystem.Active?.HorizontalWrap != true || pActor.city == null || pActor.current_tile == null ||
            !pActor.isAlive() || pActor.under_forces || pActor.asset.is_boat || pActor.isFlying() ||
            pActor.isWaterCreature() || pActor.is_inside_boat) return;
        if (__result?.current_tile != null && __result.current_tile.isSameIsland(pActor.current_tile)) return;
        var buildings = pActor.city.getBuildingListOfType(pType);
        if (buildings == null || buildings.Count == 0) return;
        Cursor cursor = Cursors.GetOrCreateValue(pActor);
        int start = cursor.Index % buildings.Count;
        for (int i = 0; i < Math.Min(16, buildings.Count); i++)
        {
            int index = (start + i) % buildings.Count;
            Building candidate = buildings[index];
            if (candidate?.current_tile == null || !candidate.isAlive() || !candidate.isUsable() ||
                candidate.isUnderConstruction() || candidate.current_tile.zone?.city != pActor.city ||
                (__instance._only_non_targeted && candidate.current_tile.isTargeted()) ||
                (__instance._only_with_resources && !candidate.hasResourcesToCollect()) ||
                Math.Abs(pActor.current_tile.x - candidate.current_tile.x) <= MapBox.width * .5f) continue;
            if (!HorizontalWorkTargets.Reachable(pActor.current_tile, candidate.current_tile)) continue;
            __result = candidate;
            cursor.Index = (index + 1) % buildings.Count;
            return;
        }
        cursor.Index = (start + 1) % buildings.Count;
    }
}
