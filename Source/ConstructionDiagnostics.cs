using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using ai;
using ai.behaviours;

namespace ClimateWeather;

// 只观察施工链，不改返回值、施工进度或任务状态。
internal static class ConstructionDiagnostics
{
    // 实测通过后关闭临时诊断；同时跳过专用 Harmony 补丁注册，保留玩法修复。
    internal static bool Enabled => false;
    private sealed class Sample { internal readonly Dictionary<string, float> Next = new(); }
    private static readonly ConditionalWeakTable<Actor, Sample> Samples = new();
    private static float _window;
    private static int _lines;
    internal static void Log(Actor actor, string stage, object result = null, WorldTile target = null, int before = -1)
    {
        if (!Enabled) return;
        Building building = actor?.beh_building_target;
        if (ClimateSystem.Active?.HorizontalWrap != true || building == null || actor.current_tile == null) return;
        if (!building.isUnderConstruction() && before < 0) return;
        float now = Time.realtimeSinceStartup;
        if (now >= _window) { _window = now + 5f; _lines = 0; }
        if (_lines >= 12) return;
        Sample sample = Samples.GetOrCreateValue(actor);
        if (sample.Next.TryGetValue(stage, out float next) && now < next) return;
        sample.Next[stage] = now + 5f;
        _lines++;
        WorldTile tile = target ?? actor.beh_tile_target;
        int dx = tile == null ? -1 : Math.Abs(tile.x - actor.current_tile.x);
        // 字符串只在限频通过后生成；原始距离和回绕距离同时输出以核对误判。
        Debug.Log($"[ClimateWeather BuildDiag] actor={actor.GetHashCode()} building={building.GetHashCode()} " +
            $"stage={stage} result={result} from=({actor.current_tile.x},{actor.current_tile.y}) " +
            $"target=({tile?.x},{tile?.y}) dx={dx} wrapDx={(dx < 0 ? -1 : Math.Min(dx, MapBox.width - dx))} " +
            $"sameCity={tile?.zone?.city == actor.city} sameIsland={(tile == null ? false : actor.current_tile.isSameIsland(tile))} " +
            $"safeTarget={HorizontalGroundMovement.Walkable(tile)} progressBefore={before} " +
            $"progressNow={building.getConstructionProgress()} needed={building.asset.construction_progress_needed} " +
            $"underConstruction={building.isUnderConstruction()} wrappedRoute={HorizontalGroundMovement.Has(actor)}");
    }
}

// 保存原流程实际返回的候选和施工点，不额外调用随机选点或寻路。
[HarmonyPatch(typeof(ActorTool), nameof(ActorTool.findNewBuildingTarget))]
internal static class ConstructionSelectionDiagnostics
{
    private static bool Prepare() => ConstructionDiagnostics.Enabled;
    internal sealed class Context
    {
        internal Actor Actor;
        internal Building Candidate;
        internal WorldTile Tile;
        internal string Reason = "NoReachabilityCheck";
        internal bool Active;
    }
    [ThreadStatic] internal static Context Current;
    private static float _nextWindow;
    private static int _count;
    private static void Prefix(Actor pActor, string pType, out Context __state)
    {
        __state = Current;
        Current = pType == "new_building" && ClimateSystem.Active?.HorizontalWrap == true
            ? new Context { Actor = pActor, Active = true } : null;
    }
    internal static bool Record(bool value, string reason)
    {
        if (Current?.Active == true) Current.Reason = reason;
        return value;
    }
    private static void Postfix(Building __result)
    {
        Context c = Current;
        if (c?.Active != true || c.Actor?.current_tile == null) return;
        float now = Time.realtimeSinceStartup;
        if (now >= _nextWindow) { _nextWindow = now + 5f; _count = 0; }
        if (_count >= 4) return;
        _count++;
        WorldTile from = c.Actor.current_tile, to = c.Tile;
        int dx = to == null ? -1 : Math.Abs(to.x - from.x);
        Debug.Log($"[ClimateWeather BuildSelect] actor={c.Actor.GetHashCode()} " +
            $"candidate={c.Candidate?.GetHashCode()} selected={__result?.GetHashCode()} " +
            $"reason={(c.Candidate == null ? "NoCandidate" : c.Reason)} " +
            $"from=({from.x},{from.y}) tile=({to?.x},{to?.y}) dx={dx} " +
            $"wrapDx={(dx < 0 ? -1 : Math.Min(dx, MapBox.width - dx))} " +
            $"sameCity={to != null && from.zone?.city != null && from.zone.city == to.zone?.city} " +
            $"safeFrom={HorizontalGroundMovement.Walkable(from)} safeTarget={HorizontalGroundMovement.Walkable(to)}");
    }
    // 异常或嵌套调用后也恢复上下文，不把下一个资源任务算作施工。
    private static void Finalizer(Context __state) => Current = __state;
}

[HarmonyPatch(typeof(City), nameof(City.getBuildingToBuild))]
internal static class ConstructionCandidateDiagnostics
{
    private static bool Prepare() => ConstructionDiagnostics.Enabled;
    private static void Postfix(Building __result)
    {
        if (ConstructionSelectionDiagnostics.Current?.Active == true)
            ConstructionSelectionDiagnostics.Current.Candidate = __result;
    }
}

[HarmonyPatch(typeof(Building), nameof(Building.getConstructionTile))]
internal static class ConstructionSampleDiagnostics
{
    private static bool Prepare() => ConstructionDiagnostics.Enabled;
    private static void Postfix(Building __instance, WorldTile __result)
    {
        var c = ConstructionSelectionDiagnostics.Current;
        if (c?.Active == true && c.Candidate == __instance) c.Tile = __result;
    }
}

[HarmonyPatch]
internal static class ConstructionStageDiagnostics
{
    private static bool Prepare() => ConstructionDiagnostics.Enabled;
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(BehCityActorFindBuilding), "execute");
        yield return AccessTools.Method(typeof(BehFindConstructionTile), "execute");
        yield return AccessTools.Method(typeof(BehGoToTileTarget), "execute");
        yield return AccessTools.Method(typeof(BehCheckStillUnderConstruction), "execute");
        yield return AccessTools.Method(typeof(BehBuildTarget), "execute");
    }
    private static void Prefix(Actor pActor, out int __state) =>
        __state = pActor?.beh_building_target?.isUnderConstruction() == true ?
            pActor.beh_building_target.getConstructionProgress() : -1;
    private static void Postfix(Actor pActor, BehResult __result, MethodBase __originalMethod, int __state) =>
        ConstructionDiagnostics.Log(pActor, __originalMethod.DeclaringType.Name, __result, before: __state);
}

[HarmonyPatch(typeof(ActorMove), nameof(ActorMove.goTo))]
internal static class ConstructionRouteDiagnostics
{
    private static bool Prepare() => ConstructionDiagnostics.Enabled;
    private static void Postfix(Actor pActor, WorldTile pTileTarget, ExecuteEvent __result) =>
        ConstructionDiagnostics.Log(pActor, "ActorMoveResult", __result, pTileTarget);
}
