using System;
using HarmonyLib;
using ai;
using UnityEngine;

namespace ClimateWeather;

internal static class HorizontalBoatMovement
{
    internal static bool Eligible(Actor actor)
    {
        if (actor?.asset == null || !actor.asset.is_boat || !actor.isAlive() || actor.under_forces ||
            actor.is_inside_boat || actor.isFlying()) return false;
        Boat boat = actor.getSimpleComponent<Boat>();
        // 乘客由原版 u1_checkInside 跟随船体；运输任务保留原版生命周期。
        return boat != null;
    }
    internal static bool Passable(WorldTile tile) => tile?.Type != null && tile.Type.liquid && tile.isGoodForBoat() &&
        !tile.Type.block && !tile.Type.lava && !tile.isOnFire();
}

[HarmonyPatch(typeof(ActorTool), nameof(ActorTool.getRandomTileForBoat))]
internal static class HorizontalBoatWanderPatch
{
    private static void Postfix(Actor pActor, ref WorldTile __result)
    {
        if (ClimateSystem.Active?.HorizontalWrap != true || MapBox.width <= 32 ||
            !HorizontalBoatMovement.Eligible(pActor) || !HorizontalBoatMovement.Passable(pActor.current_tile)) return;
        int x = pActor.current_tile.x;
        Boat boat = pActor.getSimpleComponent<Boat>();
        // 放行运输路径不等于允许随机巡航覆盖接送目标。
        if (boat.hasPassengers() || boat.taxi_request != null) return;
        if (!Randy.randomBool()) return;
        // 从缓存的真实接缝通道选点，内海船也能主动驶向通道，而非必须先随机碰到边界。
        // 固定采样上限，避免每艘船每次巡航遍历整条边界；保留一半原版巡航行为。
        var seams = HorizontalOceanConnectivity.PatrolSeams;
        if (seams.Count == 0) return;
        WorldTile best = null;
        int bestDistance = int.MaxValue;
        int offset = Randy.randomInt(0, seams.Count - 1);
        int count = Math.Min(16, seams.Count);
        for (int i = 0; i < count; i++)
        {
            int targetX = x < MapBox.width / 2 ? MapBox.width - 1 : 0;
            int y = seams[(offset + i * seams.Count / count) % seams.Count];
            WorldTile tile = World.world.GetTileSimple(targetX, y);
            WorldTile entry = World.world.GetTileSimple(targetX == 0 ? MapBox.width - 1 : 0, y);
            if (!HorizontalBoatMovement.Passable(entry)) continue;
            if (!HorizontalBoatMovement.Passable(tile)) continue;
            if (!HorizontalOceanConnectivity.Connected(pActor.current_tile, tile)) continue;
            if (tile.zone?.city != null && pActor.kingdom != null && tile.zone.city.kingdom.isEnemy(pActor.kingdom)) continue;
            int distance = Math.Abs(pActor.current_tile.y - y);
            if (distance >= bestDistance) continue;
            bestDistance = distance; best = tile;
        }
        if (best != null) __result = best;
    }
}

[HarmonyPatch(typeof(Boat), nameof(Boat.calculateMovementAngle))]
internal static class HorizontalBoatHeadingPatch
{
    private static bool Prefix(Boat __instance)
    {
        Actor actor = __instance.actor;
        if (!HorizontalGroundMovement.Has(actor) || !HorizontalGroundMovement.Valid(actor) ||
            !HorizontalGroundMovement.StepReady(actor)) return true;
        Vector2 target = actor.next_step_position;
        __instance._last_step = target;
        target.x = actor.current_position.x + HorizontalTopology.Delta(actor.current_position.x, target.x, MapBox.width);
        __instance.last_movement_angle = (int)Toolbox.getAngleDegrees(actor.current_position.x, actor.current_position.y, target.x, target.y);
        return false;
    }
}
