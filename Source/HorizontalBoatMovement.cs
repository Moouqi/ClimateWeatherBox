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
        // 接送请求及乘客同步尚未适配，航行中出现这些状态也应停止跨缝路径。
        return boat != null && !boat.hasPassengers() && boat.taxi_request == null;
    }
    internal static bool Passable(WorldTile tile) => tile?.Type != null && tile.isGoodForBoat() &&
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
        if (x >= 16 && x < MapBox.width - 16) return;
        if (!Randy.randomBool()) return;
        // 只从另一侧近岸窗口采样，保留原版一半的选择，不创建或强行通过陆地航线。
        for (int i = 0; i < 4; i++)
        {
            int targetX = x < 16 ? MapBox.width - 1 - Randy.randomInt(0, 7) : Randy.randomInt(0, 7);
            int y = Mathf.Clamp(pActor.current_tile.y + Randy.randomInt(-8, 8), 0, MapBox.height - 1);
            WorldTile tile = World.world.GetTileSimple(targetX, y);
            if (!HorizontalBoatMovement.Passable(tile)) continue;
            if (tile.zone?.city != null && pActor.kingdom != null && tile.zone.city.kingdom.isEnemy(pActor.kingdom)) continue;
            __result = tile;
            return;
        }
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
