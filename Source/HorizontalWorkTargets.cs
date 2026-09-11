using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ai;
using ai.behaviours;
using UnityEngine;

namespace ClimateWeather;

internal static class HorizontalWorkTargets
{
    private static int _frame = -1, _searches;
    private static readonly Dictionary<(WorldTile, WorldTile), bool> Results = new();

    internal static bool Reachable(WorldTile from, WorldTile to)
    {
        if (from.isSameIsland(to)) return ConstructionSelectionDiagnostics.Record(true, "SameIsland");
        if (ClimateSystem.Active?.HorizontalWrap != true || MapBox.width < 3)
            return ConstructionSelectionDiagnostics.Record(false, "WrapDisabled");
        if (from.zone?.city == null || to.zone?.city != from.zone.city)
            return ConstructionSelectionDiagnostics.Record(false, "DifferentCity");
        if (!HorizontalGroundMovement.Walkable(from) || !HorizontalGroundMovement.Walkable(to))
            return ConstructionSelectionDiagnostics.Record(false, "UnsafeTile");
        if (Math.Abs(from.x - to.x) <= MapBox.width * .5f)
            return ConstructionSelectionDiagnostics.Record(false, "NotSeamCandidate");
        // 只缓存本帧结果，避免地形改变后沿用旧通路；全体工人共享搜索预算。
        if (_frame != Time.frameCount) { _frame = Time.frameCount; _searches = 0; Results.Clear(); }
        if (Results.TryGetValue((from, to), out bool found)) return ConstructionSelectionDiagnostics.Record(found, found ? "CachedReachable" : "CachedUnreachable");
        if (_searches >= 2) return ConstructionSelectionDiagnostics.Record(false, "SelectionFrameBudget");
        _searches++;
        int width = MapBox.width;
        var path = new List<int>();
        var searchResult = HorizontalGroundPathfinder.Find(width, MapBox.height, from.y * width + from.x,
            to.y * width + to.x, true,
            index => HorizontalGroundMovement.Walkable(World.world.GetTileSimple(index % width, index / width)),
            2048, path, out _);
        found = searchResult == HorizontalGroundPathfinder.Result.Found;
        Results[(from, to)] = found;
        Results[(to, from)] = found;
        if (ConstructionSelectionDiagnostics.Current?.Active == true)
            ConstructionSelectionDiagnostics.Record(found, found ? "Reachable" : "Search:" + searchResult);
        return found;
    }

    internal static bool BuildingReachable(Building building, Actor actor) =>
        building.isSameIslandAs(actor) || (actor.city != null && building.current_tile.zone.city == actor.city &&
            Reachable(actor.current_tile, building.current_tile));

    internal static int Distance(WorldTile a, WorldTile b)
    {
        if (ClimateSystem.Active?.HorizontalWrap != true) return Toolbox.SquaredDistTile(a, b);
        int dx = Math.Abs(a.x - b.x), dy = a.y - b.y;
        dx = Math.Min(dx, MapBox.width - dx);
        return dx * dx + dy * dy;
    }
}

[HarmonyPatch]
internal static class HorizontalWorkTargetPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ActorTool), nameof(ActorTool.findNewBuildingTarget));
        yield return AccessTools.Method(typeof(ActorTool), nameof(ActorTool.findNewTargetInZones));
        yield return AccessTools.Method(typeof(BehFindFarmField), nameof(BehFindFarmField.execute));
        yield return AccessTools.Method(typeof(BehFindTileForFarm), nameof(BehFindTileForFarm.execute));
        yield return AccessTools.Method(typeof(BehFindRandomTileNearBuildingTarget), nameof(BehFindRandomTileNearBuildingTarget.execute));
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        int changed = 0;
        foreach (CodeInstruction code in instructions)
        {
            if (code.operand is MethodInfo method)
            {
                string helper = null;
                if (method.DeclaringType == typeof(WorldTile) && method.Name == nameof(WorldTile.isSameIsland))
                    helper = "Reachable";
                // 仅修改本城资源候选支路；不顺带开放住房、军事或全局建筑查询。
                else if (__originalMethod.Name == nameof(ActorTool.findNewTargetInZones) &&
                    method == AccessTools.Method(typeof(BaseSimObject), nameof(BaseSimObject.isSameIslandAs),
                        new[] { typeof(BaseSimObject) })) helper = "BuildingReachable";
                else if (method == AccessTools.Method(typeof(Toolbox), nameof(Toolbox.SquaredDistTile),
                    new[] { typeof(WorldTile), typeof(WorldTile) })) helper = "Distance";
                if (helper != null)
                {
                    code.opcode = OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(HorizontalWorkTargets), helper);
                    changed++;
                }
            }
            yield return code;
        }
        int expected = __originalMethod.DeclaringType == typeof(BehFindTileForFarm) ? 4 :
            __originalMethod.DeclaringType == typeof(BehFindFarmField) ? 2 : 1;
        if (changed != expected) throw new InvalidOperationException("工作目标跨缝补丁不匹配：" + __originalMethod.Name);
    }
}
