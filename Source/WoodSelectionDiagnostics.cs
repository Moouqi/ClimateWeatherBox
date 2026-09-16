using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ai;

namespace ClimateWeather;

// 每十秒采样一次真实选树调用，不额外选点、寻路或改变 AI 返回值。
[HarmonyPatch(typeof(ActorTool), nameof(ActorTool.findNewTargetInZones))]
internal static class WoodSelectionDiagnostics
{
    internal sealed class Sample
    {
        internal Actor Actor;
        internal int Checked, Seam, Passed, Empty, Unchoppable;
        internal readonly Dictionary<string, int> Reasons = new();
        internal readonly HashSet<TileZone> Zones = new();
    }
    [ThreadStatic] internal static Sample Current;
    private static float _next;
    private static void Prefix(Actor pActor, string pType, out Sample __state)
    {
        __state = Current;
        Current = null;
        if (pType != "type_tree" || pActor?.city == null || pActor.current_tile == null ||
            ClimateSystem.Active?.HorizontalWrap != true || Time.realtimeSinceStartup < _next) return;
        _next = Time.realtimeSinceStartup + 10f;
        Current = new Sample { Actor = pActor };
    }
    internal static void Record(string reason)
    {
        var s = Current;
        if (s == null) return;
        s.Reasons.TryGetValue(reason, out int count);
        s.Reasons[reason] = count + 1;
    }
    internal static void Candidate(Building building, Actor actor, bool passed)
    {
        var s = Current;
        if (s == null || s.Actor != actor) return;
        s.Checked++;
        if (building.current_tile.zone != null) s.Zones.Add(building.current_tile.zone);
        if (Math.Abs(building.current_tile.x - actor.current_tile.x) > MapBox.width * .5f) s.Seam++;
        if (passed) s.Passed++;
        // 仅在真实可达检查通过后观察后续资格，不把它们误记成寻路失败。
        if (passed && !building.hasResourcesToCollect()) s.Empty++;
        if (passed && !building.asset.can_be_chopped_down) s.Unchoppable++;
    }
    private static void Postfix(Building __result)
    {
        var s = Current;
        if (s == null) return;
        WorldTile from = s.Actor.current_tile, to = __result?.current_tile;
        var reasons = new List<string>();
        foreach (var pair in s.Reasons) reasons.Add(pair.Key + "=" + pair.Value);
        Debug.Log($"[ClimateWeather WoodSelect] actor={s.Actor.GetHashCode()} city={s.Actor.city.GetHashCode()} " +
            $"from=({from.x},{from.y}) selected=({to?.x},{to?.y}) " +
            $"wood={s.Actor.city.getResourcesAmount("wood")} storage={s.Actor.city.hasStorageBuilding()} " +
            $"checkedZones={s.Zones.Count} checked={s.Checked} seamChecked={s.Seam} reachable={s.Passed} " +
            $"emptyAfterReach={s.Empty} unchoppableAfterReach={s.Unchoppable} reasons=[{string.Join(",", reasons)}] " +
            "note=counts_only_candidates_reaching_island_check;targeted_or_unvisited_not_counted");
    }
    private static void Finalizer(Sample __state) => Current = __state;
}
