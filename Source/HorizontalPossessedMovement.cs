using HarmonyLib;
using UnityEngine;

namespace ClimateWeather;

[HarmonyPatch(typeof(Actor), nameof(Actor.updatePossessedMovementTowards))]
internal static class HorizontalPossessedMovement
{
    private static bool Prefix(Actor __instance, float pElapsed, Vector2 pMovementPoint, ref float __result)
    {
        if (ClimateSystem.Active?.HorizontalWrap != true || MapBox.width <= 0 ||
            __instance.current_tile == null || !__instance.isAlive() ||
            !ControllableUnit._units.Contains(__instance)) return true;
        __instance.precalcMovementSpeed(pForce: true);
        if (__instance.asset.can_flip && __instance.checkFlip())
        {
            float mismatch = __instance.getMismatchFactorForSideMovement(pMovementPoint);
            if (mismatch > .2f) pElapsed *= Mathf.Lerp(1f, .8f, mismatch);
        }
        float distance = __instance.getMovementDelta(pElapsed);
        // 手动输入是当前位置加方向，不改成最短目标，以免窄地图反转方向。
        Vector2 next = Vector2.MoveTowards(__instance.current_position, pMovementPoint, distance);
        if (float.IsNaN(next.x) || float.IsInfinity(next.x)) { __result = 0f; return false; }
        next.x = HorizontalTopology.Wrap(next.x, MapBox.width);
        next = __instance.checkVelocityAgainstBlock(next);
        if (!Toolbox.inMapBorder(ref next)) { __result = 0f; return false; }
        // 与原版不同的是先完成横向回绕；山体反弹、南北边界、速度规则都保留。
        __instance.current_position = next;
        __instance.dirty_current_tile = true;
        __instance.findCurrentTile(false);
        __result = distance;
        return false;
    }
}

[HarmonyPatch(typeof(ControllableUnit), nameof(ControllableUnit.updateCamera))]
internal static class HorizontalPossessedCamera
{
    private static bool Prefix()
    {
        Actor actor = ControllableUnit._unit_main;
        if (actor == null || ClimateSystem.Active?.HorizontalWrap != true || MapBox.width <= 0 ||
            HorizontalCameraWrap.Active?.Running != true || World.world?.camera == null) return true;
        Camera camera = World.world.camera;
        Vector3 position = camera.transform.position;
        Vector3 target = position;
        // 跟随离当前镜头最近的世界副本，避免单位过缝后镜头横扫整张地图。
        target.x += HorizontalTopology.Delta(position.x, actor.current_position.x, MapBox.width);
        target.y = actor.current_position.y;
        camera.transform.position = Vector3.Lerp(position, target, 1f / Mathf.Max(.001f, camera.orthographicSize));
        return false;
    }
}
