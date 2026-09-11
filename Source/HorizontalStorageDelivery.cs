using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ai.behaviours;

namespace ClimateWeather;

internal static class HorizontalStorageDelivery
{
    private sealed class Cursor { internal int Storage, Mill; }
    private static readonly ConditionalWeakTable<Actor, Cursor> Cursors = new();
    internal static bool Eligible(Actor actor) => ClimateSystem.Active?.HorizontalWrap == true &&
        actor?.city != null && actor.current_tile != null && actor.isAlive() && actor.isCarryingResources() &&
        !actor.under_forces && !actor.asset.is_boat && !actor.isFlying() && !actor.isWaterCreature() && !actor.is_inside_boat;

    internal static bool IsLocalStorage(Actor actor, Building building) => building?.current_tile != null &&
        building.current_tile.zone?.city == actor.city &&
        (actor.city.storages.Contains(building) || building.asset.type == "type_windmill");

    internal static Building Find(Actor actor, bool wheat)
    {
        Cursor cursor = Cursors.GetOrCreateValue(actor);
        if (wheat)
        {
            Building mill = FindIn(actor, actor.city.getBuildingListOfType("type_windmill"), ref cursor.Mill, false);
            if (mill != null) return mill;
        }
        return FindIn(actor, actor.city.storages, ref cursor.Storage, wheat);
    }

    private static Building FindIn(Actor actor, List<Building> list, ref int cursor, bool preferFood)
    {
        if (list == null || list.Count == 0) return null;
        int start = cursor % list.Count;
        Building best = null;
        int bestDistance = int.MaxValue;
        // 限制候选扫描并轮换起点；可达性复用现有每帧搜索预算，不扫全图。
        for (int i = 0; i < Math.Min(16, list.Count); i++)
        {
            Building candidate = list[(start + i) % list.Count];
            if (!IsLocalStorage(actor, candidate) || !candidate.isAlive() || !candidate.isUsable() ||
                candidate.isUnderConstruction() || Math.Abs(actor.current_tile.x - candidate.current_tile.x) <= MapBox.width * .5f)
                continue;
            int distance = HorizontalWorkTargets.Distance(actor.current_tile, candidate.current_tile);
            bool preferred = preferFood && candidate.asset.storage_only_food;
            bool bestPreferred = preferFood && best != null && best.asset.storage_only_food;
            if (best != null && (bestPreferred && !preferred || bestPreferred == preferred && distance >= bestDistance)) continue;
            if (!HorizontalWorkTargets.Reachable(actor.current_tile, candidate.current_tile)) continue;
            best = candidate;
            bestDistance = distance;
        }
        cursor = (start + 1) % list.Count;
        return best;
    }
}

[HarmonyPatch]
internal static class HorizontalStorageSelectionPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(BehCityActorFindStorage), "execute");
        yield return AccessTools.Method(typeof(BehCityActorFindStorageWheat), "execute");
    }
    private static void Postfix(Actor pActor, MethodBase __originalMethod, ref BehResult __result)
    {
        // 原版已有可用目标时不覆盖；不干涉其他 AI 的仓储查询。
        if (__result != BehResult.Stop || !HorizontalStorageDelivery.Eligible(pActor)) return;
        Building target = HorizontalStorageDelivery.Find(pActor,
            __originalMethod.DeclaringType == typeof(BehCityActorFindStorageWheat));
        if (target == null) return;
        pActor.beh_building_target = target;
        __result = BehResult.Continue;
    }
}

[HarmonyPatch(typeof(BehFindRaycastTileForBuildingTarget), nameof(BehFindRaycastTileForBuildingTarget.execute))]
internal static class HorizontalStorageApproachPatch
{
    private static bool Prefix(Actor pActor, ref BehResult __result)
    {
        if (!HorizontalStorageDelivery.Eligible(pActor)) return true;
        Building target = pActor.beh_building_target;
        if (!HorizontalStorageDelivery.IsLocalStorage(pActor, target) || !target.isUsable() || target.isUnderConstruction() ||
            Math.Abs(pActor.current_tile.x - target.current_tile.x) <= MapBox.width * .5f) return true;
        __result = BehResult.Stop;
        if (!HorizontalWorkTargets.Reachable(pActor.current_tile, target.current_tile)) return false;
        // 走到仓库所在格再交给原版投递；不跨地图做直线射线，也不直接增减库存。
        pActor.beh_tile_target = target.current_tile;
        __result = BehResult.Continue;
        return false;
    }
}
