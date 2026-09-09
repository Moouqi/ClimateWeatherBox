using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace ClimateWeather;

internal static class HorizontalForcedMovement
{
    internal sealed class State
    {
        internal object Data;
        internal WorldTile[] World;
        internal bool Crossed;
    }
    private static readonly ConditionalWeakTable<Actor, State> States = new();

    internal static void Track(Actor actor)
    {
        if (!actor.under_forces) return;
        State state = States.GetOrCreateValue(actor);
        state.Data = actor.data;
        state.World = World.world.tiles_list;
    }

    internal static State Get(Actor actor)
    {
        if (!States.TryGetValue(actor, out State state)) return null;
        // 对象池复用、换地图、落地或切换区域模式后，不能把许可传给无关单位。
        if (!ReferenceEquals(state.Data, actor.data) || !ReferenceEquals(state.World, World.world?.tiles_list) ||
            !actor.under_forces || ClimateSystem.Active?.HorizontalWrap != true || MapBox.width <= 0)
        {
            States.Remove(actor);
            return null;
        }
        return state;
    }
    internal static void Forget(Actor actor) => States.Remove(actor);
}

[HarmonyPatch(typeof(Actor), nameof(Actor.checkVelocityAgainstBlock))]
internal static class HorizontalForcedCollisionPatch
{
    private static void Prefix(Actor __instance, ref Vector2 pNewPos)
    {
        HorizontalForcedMovement.State state = HorizontalForcedMovement.Get(__instance);
        if (state == null || float.IsNaN(pNewPos.x) || float.IsInfinity(pNewPos.x)) return;
        if (pNewPos.x >= 0 && pNewPos.x < MapBox.width) return;
        // 在原版查找落点之前归一化，另一侧的山体、船只水域限制与反弹仍由原版判断。
        pNewPos.x = HorizontalTopology.Wrap(pNewPos.x, MapBox.width);
        state.Crossed = true;
    }
}

[HarmonyPatch(typeof(Actor), nameof(Actor.updateVelocity))]
internal static class HorizontalForcedPositionPatch
{
    private static void Prefix(Actor __instance, out HorizontalForcedMovement.State __state)
    {
        __state = HorizontalForcedMovement.Get(__instance);
        if (__state != null) __state.Crossed = false;
    }
    private static void Postfix(Actor __instance, HorizontalForcedMovement.State __state)
    {
        if (__state == null) return;
        if (__state.Crossed)
        {
            // 此阶段可能并行执行，只同步该单位的位置索引，不改共享区块容器。
            // 原版 SimObjectsZones 每 0.1 秒据 current_tile 重建单位区块索引。
            __instance.dirty_current_tile = true;
            __instance.findCurrentTile(false);
        }
        if (!__instance.under_forces) HorizontalForcedMovement.Forget(__instance);
    }
}

[HarmonyPatch(typeof(Actor), nameof(Actor.stopForce))]
internal static class HorizontalForcedStopPatch
{
    // 落地或原版主动取消受力后立即撤销，之后的普通爆炸不能继承跨界许可。
    private static void Postfix(Actor __instance) => HorizontalForcedMovement.Forget(__instance);
}
