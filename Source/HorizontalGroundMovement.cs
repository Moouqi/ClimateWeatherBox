using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using ai;

namespace ClimateWeather;

internal static class HorizontalGroundMovement
{
    internal sealed class Route
    {
        internal object Data;
        internal WorldTile[] World;
        internal bool StepReady;
    }
    private sealed class Attempt { internal float Next; internal object Data; }
    private static readonly ConditionalWeakTable<Actor, Route> Routes = new();
    private static readonly ConditionalWeakTable<Actor, Attempt> Attempts = new();
    private static int _frame = -1, _attempts;
    internal static bool Has(Actor actor) => Routes.TryGetValue(actor, out _);
    internal static bool StepReady(Actor actor) => Routes.TryGetValue(actor, out Route route) && route.StepReady;
    internal static void BeginStep(Actor actor)
    {
        if (Routes.TryGetValue(actor, out Route route)) route.StepReady = true;
    }
    internal static void Forget(Actor actor) => Routes.Remove(actor);
    internal static bool Valid(Actor actor) => Routes.TryGetValue(actor, out Route route) &&
        ReferenceEquals(route.Data, actor.data) && ReferenceEquals(route.World, World.world?.tiles_list) &&
        ClimateSystem.Active?.HorizontalWrap == true && actor.isAlive() && !actor.under_forces &&
        !actor.asset.is_boat && !actor.isFlying() && !actor.isWaterCreature() && !actor.is_inside_boat;

    // 第一版只允许普通安全陆地；特殊通行能力及火灾逃生仍交给原版。
    internal static bool Walkable(WorldTile tile) => tile?.Type != null && tile.Type.ground &&
        !tile.Type.block && !tile.Type.lava && !tile.Type.liquid && !tile.isOnFire();

    internal static bool TryRoute(Actor actor, WorldTile target)
    {
        if (actor?.current_tile == null || target == null || ClimateSystem.Active?.HorizontalWrap != true ||
            MapBox.width < 3 || !actor.isAlive() || actor.under_forces || actor.asset.is_boat ||
            actor.isFlying() || actor.isWaterCreature() || actor.is_inside_boat ||
            !Walkable(actor.current_tile) || !Walkable(target)) return false;
        int width = MapBox.width;
        if (Math.Abs(actor.current_tile.x - target.x) <= width / 2f) return false;
        if (!DebugConfig.isOn(DebugOption.SystemUnitPathfinding)) return false;
        // 每帧最多两次有限搜索，并限制同一单位重试频率，避免批量 AI 请求拖慢帧率。
        if (_frame != Time.frameCount) { _frame = Time.frameCount; _attempts = 0; }
        if (_attempts >= 2) return false;
        Attempt attempt = Attempts.GetOrCreateValue(actor);
        if (ReferenceEquals(attempt.Data, actor.data) && Time.time < attempt.Next) return false;
        attempt.Data = actor.data; attempt.Next = Time.time + .5f; _attempts++;
        var path = new List<int>();
        var result = HorizontalGroundPathfinder.Find(width, MapBox.height,
            actor.current_tile.y * width + actor.current_tile.x, target.y * width + target.x, true,
            p => Walkable(World.world.GetTileSimple(p % width, p / width)), 2048, path, out _);
        if (result != HorizontalGroundPathfinder.Result.Found) return false;
        bool crossed = false;
        for (int i = 1; i < path.Count; i++)
            if (Math.Abs(path[i] % width - path[i - 1] % width) > 1) crossed = true;
        if (!crossed) return false;
        actor.clearOldPath();
        actor.split_path = SplitPathStatus.Normal;
        for (int i = 1; i < path.Count; i++)
            actor.current_path.Add(World.world.GetTileSimple(path[i] % width, path[i] / width));
        actor.setTileTarget(target);
        Route route = Routes.GetOrCreateValue(actor);
        route.Data = actor.data; route.World = World.world.tiles_list;
        return true;
    }

    internal static void SetPosition(Actor actor, Vector2 p)
    {
        p.x = HorizontalTopology.Wrap(p.x, MapBox.width);
        // 在局部变量中完成回绕，再发布位置，不能短暂暴露越界坐标给其他检查。
        actor.current_position = p;
        actor.dirty_current_tile = true;
        actor.findCurrentTile(false);
    }
}

[HarmonyPatch(typeof(ActorMove), nameof(ActorMove.goTo))]
internal static class HorizontalGroundRoutePatch
{
    private static bool Prefix(Actor pActor, WorldTile pTileTarget, bool pPathOnLiquid,
        bool pWalkOnBlocks, bool pPathOnLava, int pLimitPathfindingRegions, ref ExecuteEvent __result)
    {
        if (pPathOnLiquid || pWalkOnBlocks || pPathOnLava || pLimitPathfindingRegions != 0 ||
            !HorizontalGroundMovement.TryRoute(pActor, pTileTarget)) return true;
        __result = ExecuteEvent.True;
        return false;
    }
}

[HarmonyPatch(typeof(Actor), nameof(Actor.clearOldPath))]
internal static class HorizontalGroundClearPatch
{
    private static void Postfix(Actor __instance) => HorizontalGroundMovement.Forget(__instance);
}

[HarmonyPatch(typeof(Actor), nameof(Actor.moveTo))]
internal static class HorizontalGroundStepPatch
{
    private static bool Prefix(Actor __instance, WorldTile pTileTarget)
    {
        if (!HorizontalGroundMovement.Has(__instance)) return true;
        if (!HorizontalGroundMovement.Valid(__instance) || !HorizontalGroundMovement.Walkable(pTileTarget))
        { __instance.stopMovement(); return false; }
        HorizontalGroundMovement.BeginStep(__instance);
        if (Math.Abs(__instance.current_position.x - pTileTarget.x) <= MapBox.width * .5f) return true;
        // 跨缝相邻格不能触发原版的远距离直线步进；保留正常地块进入回调。
        __instance.setIsMoving();
        __instance._next_step_tile = pTileTarget;
        __instance.setCurrentTile(pTileTarget);
        __instance.checkStepActionForTile(pTileTarget);
        if (!HorizontalGroundMovement.Has(__instance)) return false;
        __instance.next_step_position = pTileTarget.posV3;
        return false;
    }
}

[HarmonyPatch(typeof(Actor), nameof(Actor.updateMovement))]
internal static class HorizontalGroundAdvancePatch
{
    private static bool Prefix(Actor __instance, float pElapsed, float pWalkedDistance)
    {
        if (!HorizontalGroundMovement.Has(__instance)) return true;
        if (!HorizontalGroundMovement.Valid(__instance)) { __instance.stopMovement(); return false; }
        // goTo 不会立即结束原来的移动步骤，只能在新路径首次 moveTo 后接管步进。
        if (!HorizontalGroundMovement.StepReady(__instance)) return true;
        float remaining = __instance.getMovementDelta(pElapsed, pWalkedDistance);
        // 使用迭代代替原版递归；高倍速最多推进 64 格，多余路程留给下一帧。
        for (int step = 0; step < 64 && __instance.is_moving; step++)
        {
            WorldTile tile = __instance._next_step_tile;
            if (!HorizontalGroundMovement.Walkable(tile)) { __instance.stopMovement(); break; }
            Vector2 target = tile.posV3;
            target.x = __instance.current_position.x +
                HorizontalTopology.Delta(__instance.current_position.x, target.x, MapBox.width);
            float distance = Vector2.Distance(__instance.current_position, target);
            if (__instance.asset.can_flip && __instance.checkFlip())
                __instance.setFlip(__instance.current_position.x < target.x);
            if (distance > remaining)
            {
                HorizontalGroundMovement.SetPosition(__instance,
                    Vector2.MoveTowards(__instance.current_position, target, remaining));
                break;
            }
            HorizontalGroundMovement.SetPosition(__instance, tile.posV3);
            remaining = Mathf.Max(0, remaining - distance);
            if (__instance.isUsingPath()) __instance.updatePathMovement();
            else __instance.stopMovement();
            if (!HorizontalGroundMovement.Has(__instance) || remaining <= 0) break;
        }
        return false;
    }
}

[HarmonyPatch(typeof(Actor), nameof(Actor.updatePathMovement))]
internal static class HorizontalGroundValidatePatch
{
    private static bool Prefix(Actor __instance)
    {
        if (!HorizontalGroundMovement.Has(__instance) || HorizontalGroundMovement.Valid(__instance)) return true;
        __instance.stopMovement();
        return false;
    }
}

[HarmonyPatch(typeof(Actor), nameof(Actor.addForce))]
internal static class HorizontalGroundInterruptPatch
{
    // 只取消本补丁的路径，不清除力本身，也不修改原版任务选择。
    private static void Postfix(Actor __instance)
    {
        if (__instance.under_forces && HorizontalGroundMovement.Has(__instance)) __instance.stopMovement();
    }
}
