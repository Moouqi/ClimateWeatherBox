using HarmonyLib;
using UnityEngine;

namespace ClimateWeather;

// 只纠正普通航行这一帧从有效水面进入无效落点的移动，不救活已搁浅或被攻击的船。
[HarmonyPatch(typeof(Actor), nameof(Actor.updateMovement))]
internal static class BoatNavigationSafety
{
    internal struct Snapshot { internal bool Active; internal Vector2 Position; internal object World; }
    internal static WorldTile Water(Vector2 position)
    {
        if (float.IsNaN(position.x) || float.IsInfinity(position.x) ||
            float.IsNaN(position.y) || float.IsInfinity(position.y) ||
            position.x < 0 || position.x >= MapBox.width || position.y < 0 || position.y >= MapBox.height) return null;
        WorldTile tile = World.world.GetTileSimple((int)position.x, (int)position.y);
        return HorizontalBoatMovement.Passable(tile) ? tile : null;
    }
    private static void Prefix(Actor __instance, out Snapshot __state)
    {
        __state = default;
        if (ClimateSystem.Active?.HorizontalWrap != true || __instance.asset?.is_boat != true ||
            !__instance.isAlive() || __instance.under_forces || HorizontalGroundMovement.Has(__instance) ||
            Water(__instance.current_position) == null) return;
        __state = new Snapshot { Active = true, Position = __instance.current_position, World = World.world.tiles_list };
    }
    private static void Postfix(Actor __instance, Snapshot __state)
    {
        if (!__state.Active || !ReferenceEquals(__state.World, World.world?.tiles_list) ||
            !__instance.isAlive() || __instance.under_forces || HorizontalGroundMovement.Has(__instance)) return;
        if (Water(__instance.current_position) != null) return;
        // 地形若已改变则不恢复旧落点，让原版处理真正的搁浅。
        if (Water(__state.Position) == null) return;
        __instance.current_position = __state.Position;
        __instance.dirty_current_tile = true;
        __instance.findCurrentTile(false);
        __instance.stopMovement();
    }
}

[HarmonyPatch(typeof(Actor), nameof(Actor.checkDieOnGroundBoat))]
internal static class BoatGroundIndexSafety
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(Actor __instance)
    {
        if (ClimateSystem.Active?.HorizontalWrap != true || __instance.asset?.is_boat != true ||
            !__instance.isAlive() || __instance.current_tile?.Type?.liquid != false) return;
        WorldTile actual = BoatNavigationSafety.Water(__instance.current_position);
        // 只纠正“坐标在水里、索引却在陆地”的不一致；坐标真正上岸仍交给原版。
        if (actual != null) __instance.setCurrentTile(actual);
    }
}
