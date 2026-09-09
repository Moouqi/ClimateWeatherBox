using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace ClimateWeather;

/// <summary>
/// 原版 Cloud.update 将移动硬编码为 Translate(speed * elapsed, 0, 0)，并且 Cloud.spawn
/// 不维护 tile/current_tile。这里先从云的实时坐标反查地块，按局地风完成二维位移，随后
/// 暂时把 speed 置零，让原版继续处理降水、闪电、淡入淡出和寿命但不再额外向右移动。
/// </summary>
[HarmonyPatch(typeof(Cloud), "update")]
internal static class LocalCloudWindPatch
{
    private sealed class MotionState
    {
        internal bool EnteredMap;
        internal float LastAliveTime;
        internal bool HasLastPosition;
        internal Vector2 LastPosition;
        internal float StuckSeconds;
        internal bool HasCachedWind;
        internal Vector2 CachedWind;
        internal float NextWindRefresh;
    }

    private static readonly ConditionalWeakTable<Cloud, MotionState> MotionStates =
        new ConditionalWeakTable<Cloud, MotionState>();

    private struct FrameState
    {
        internal float OriginalSpeed;
        internal float Elapsed;
        internal bool ReplacedMovement;
        internal bool MeasureMovement;
        internal MotionState Motion;
    }

    private static WorldTile TileUnderCloud(Cloud cloud)
    {
        if (cloud == null || MapBox.instance == null) return null;
        Vector3 position = cloud.transform.localPosition;
        float shadowOffsetY = cloud.spriteShadow == null ? 0f : cloud.spriteShadow.offset.y;
        int x = Mathf.FloorToInt(position.x);
        int y = Mathf.FloorToInt(position.y + shadowOffsetY);
        if (ClimateSystem.Active?.HorizontalWrap == true) x = HorizontalTopology.Wrap(x, MapBox.width);
        if (x < 0 || x >= MapBox.width || y < 0 || y >= MapBox.height) return null;
        return MapBox.instance.GetTileSimple(x, y);
    }

    private static void Prefix(Cloud __instance, float pElapsed, ref FrameState __state)
    {
        if (__instance == null) return;
        __state.OriginalSpeed = __instance.speed;
        __state.Elapsed = pElapsed;
        MotionState motion = MotionStates.GetOrCreateValue(__instance);
        // Cloud 由对象池复用；alive_time 在 prepare 时归零，据此清除上一轮的入图状态。
        if (__instance.alive_time + 0.001f < motion.LastAliveTime)
        {
            motion.EnteredMap = false;
            motion.HasLastPosition = false;
            motion.StuckSeconds = 0f;
            motion.HasCachedWind = false;
            motion.NextWindRefresh = 0f;
        }
        motion.LastAliveTime = __instance.alive_time;
        __state.Motion = motion;
        if (MapBox.instance == null || World.world == null || World.world.isPaused()) return;
        __state.MeasureMovement = true;

        WorldTile tile = TileUnderCloud(__instance);
        ClimateSystem climate = ClimateSystem.Active;
        if (tile == null || climate == null || !climate.TryGetClimate(tile, out ClimateCell cell)) return;
        motion.EnteredMap = true;

        if (!motion.HasCachedWind || Time.unscaledTime >= motion.NextWindRefresh)
        {
            motion.CachedWind = climate.GetCloudWind(__instance, tile, cell);
            motion.NextWindRefresh = Time.unscaledTime + 0.2f;
            motion.HasCachedWind = true;
        }
        Vector2 wind = motion.CachedWind;
        __instance.transform.Translate(
            wind.x * __state.OriginalSpeed * pElapsed,
            wind.y * __state.OriginalSpeed * pElapsed,
            0f);

        if (climate.HorizontalWrap)
        {
            Vector3 wrapped = __instance.transform.localPosition;
            wrapped.x = HorizontalTopology.Wrap(wrapped.x, MapBox.width);
            __instance.transform.localPosition = wrapped;
        }

        WorldTile landingTile = TileUnderCloud(__instance) ?? tile;
        __instance.current_tile = landingTile;
        __instance.tile = landingTile;
        climate.UpdatePrecipitationPhase(__instance, landingTile);
        climate.ApplyRainfallMoisture(__instance, landingTile);

        // Cloud.update 会在本帧稍后执行固定的 +X 位移；置零后只保留上面的局地风位移。
        __instance.speed = 0f;
        __state.ReplacedMovement = true;
    }

    private static void Postfix(Cloud __instance, FrameState __state)
    {
        if (__instance == null) return;
        if (__state.ReplacedMovement) __instance.speed = __state.OriginalSpeed;
        if (__state.Motion != null) __state.Motion.LastAliveTime = __instance.alive_time;
        if (MapBox.instance == null) return;

        Vector3 position = __instance.transform.localPosition;
        float shadowOffsetY = __instance.spriteShadow == null ? 0f : __instance.spriteShadow.offset.y;
        float mapY = position.y + shadowOffsetY;
        // 从地图外生成的原版云可首次进入；已经进入过的云越界后不可恢复原版 +X
        // 位移再次弹回，否则逆风边界会形成永不回收的云堆。
        bool wrap = ClimateSystem.Active?.HorizontalWrap == true;
        bool outside = __state.Motion?.EnteredMap == true &&
                       ((!wrap && (position.x < 0f || position.x >= MapBox.width)) ||
                        mapY < 0f || mapY >= MapBox.height);
        bool stuck = false;
        bool overstayed = false;
        MotionState motion = __state.Motion;
        if (__instance.state == 1 && motion?.EnteredMap == true && __state.MeasureMovement)
        {
            Vector2 current = new Vector2(position.x, mapY);
            if (motion.HasLastPosition)
            {
                float dx = wrap ? HorizontalTopology.Delta(motion.LastPosition.x, current.x, MapBox.width)
                    : current.x - motion.LastPosition.x;
                float moved = new Vector2(dx, current.y - motion.LastPosition.y).magnitude;
                // 阈值仅为正常速度的 1.5%，山峰绕流即使降至约 10% 仍会被视为移动。
                float minimumProgress = Mathf.Max(0.0005f,
                    Mathf.Abs(__state.OriginalSpeed) * __state.Elapsed * 0.015f);
                motion.StuckSeconds = moved < minimumProgress
                    ? motion.StuckSeconds + __state.Elapsed
                    : 0f;
            }
            motion.LastPosition = current;
            motion.HasLastPosition = true;
            stuck = motion.StuckSeconds >= 10f;

            // 原版无 lifespan 的云原本依靠固定向右越界销毁；改用二维风场后可能
            // 在闭合环流中永久绕圈，因此按地图宽度和原始云速设置宽松的驻留上限。
            if (__instance._lifespan <= 0f)
            {
                float residenceLimit = Mathf.Clamp(
                    MapBox.width / Mathf.Max(0.75f, Mathf.Abs(__state.OriginalSpeed)) * 1.75f,
                    120f, 360f);
                overstayed = __instance.alive_time >= residenceLimit;
            }
        }
        if (__instance.state == 1 && (outside || stuck || overstayed)) __instance.startToDie();
    }
}

/// <summary>原版龙卷风保留破坏和缩放逻辑，仅将下一移动目标偏向下风侧。</summary>
[HarmonyPatch(typeof(TornadoEffect), "updateMovement")]
internal static class LocalTornadoWindPatch
{
    private static readonly AccessTools.FieldRef<TornadoEffect, WorldTile> TargetTile =
        AccessTools.FieldRefAccess<TornadoEffect, WorldTile>("_target_tile");

    private static bool Prefix(TornadoEffect __instance)
    {
        ClimateSystem climate = ClimateSystem.Active;
        bool wrap = climate?.HorizontalWrap == true && MapBox.width > 0 && MapBox.height > 0;
        if (wrap)
        {
            Vector3 position=__instance.transform.localPosition;
            position.x=HorizontalTopology.Wrap(position.x,MapBox.width);
            position.y=Mathf.Clamp(position.y,0,MapBox.height-1);
            __instance.transform.localPosition=position;
            SyncTile(__instance,position);
        }
        WorldTile tile = __instance?.current_tile ?? __instance?.tile;
        if (climate != null && climate.TryGetTropicalCycloneTarget(__instance, tile, out WorldTile cycloneTarget))
            TargetTile(__instance) = cycloneTarget;
        else if (climate != null && climate.TryGetWindTarget(tile, false, 7, out WorldTile target))
            TargetTile(__instance) = target;
        if (!wrap || tile == null) return true;
        WorldTile destination=TargetTile(__instance);
        if (destination == null || destination == tile)
        {
            int x=HorizontalTopology.Wrap(tile.x+UnityEngine.Random.Range(-5,6),MapBox.width);
            int y=Mathf.Clamp(tile.y+UnityEngine.Random.Range(-5,6),0,MapBox.height-1);
            destination=World.world.GetTileSimple(x,y);
            TargetTile(__instance)=destination;
        }
        Vector3 p=__instance.transform.localPosition;
        Vector3 delta=destination.posV3-p;
        delta.x=HorizontalTopology.Delta(p.x,destination.x,MapBox.width);
        // Preserve the original per-update speed; only topology is changed.
        p+=delta.normalized*0.15f;
        p.x=HorizontalTopology.Wrap(p.x,MapBox.width);
        p.y=Mathf.Clamp(p.y,0,MapBox.height-1);
        __instance.transform.localPosition=p;
        SyncTile(__instance,p);
        return false;
    }

    private static void SyncTile(TornadoEffect effect,Vector3 position)
    {
        WorldTile tile=World.world.GetTileSimple(Mathf.FloorToInt(position.x),Mathf.FloorToInt(position.y));
        if (tile == null || tile == effect.current_tile) return;
        if (effect.current_tile != null) effect.removeTornadoFromTile();
        effect.current_tile=tile;
        effect.addTornadoToTile();
    }
}

/// <summary>
/// 原版龙卷风会把经过的海水直接降低一级。热带气旋在海面只产生降水和风暴，
/// 不应沿路径抽干海洋；登陆后仍沿用原版破坏效果。
/// </summary>
[HarmonyPatch(typeof(TornadoEffect), nameof(TornadoEffect.tornadoActionTerraform))]
internal static class TropicalCycloneSurfacePatch
{
    private static bool Prefix(TornadoEffect __instance, WorldTile pTile, float pScale)
    {
        ClimateSystem climate = ClimateSystem.Active;
        if (climate == null || pTile == null) return true;
        bool tropical = climate.IsTrackedTropicalCyclone(__instance);
        if (tropical)
        {
            climate.ApplyTropicalCycloneRain(__instance, pTile);
            if (pTile.main_type?.ocean == true) return false;
        }
        return !climate.HorizontalWrap ||
            !HorizontalStormTerrain.TryApply(pTile, pScale, tropical);
    }
}

/// <summary>顺风提高、逆风降低船速；不覆盖船只目的地和原版水路寻路。</summary>
[HarmonyPatch(typeof(Actor), "getMovementDelta")]
internal static class BoatWindSpeedPatch
{
    private static void Postfix(Actor __instance, ref float __result)
    {
        if (__instance?.asset?.is_boat != true || __instance.current_tile == null) return;
        ClimateSystem climate = ClimateSystem.Active;
        if (climate == null || !climate.TryGetClimate(__instance.current_tile, out ClimateCell cell)) return;
        Vector2 movement = __instance.next_step_position - __instance.current_position;
        if (movement.sqrMagnitude < 0.0001f) return;
        float alignment = Vector2.Dot(movement.normalized, cell.Wind);
        float multiplier = 1f + alignment * Mathf.Lerp(0.12f, 0.42f, cell.WindSpeed);
        __result *= Mathf.Clamp(multiplier, 0.58f, 1.42f);
    }
}

/// <summary>主地形发生实际变化后，将该格交给气候系统在完整 setter 链结束后批量重算。</summary>
[HarmonyPatch(typeof(WorldTile), nameof(WorldTile.setTileType),
    new Type[] { typeof(TileType), typeof(bool) })]
internal static class TerrainMainClimateSyncPatch
{
    private static void Prefix(WorldTile __instance, out TileType __state)
    {
        __state = __instance?.main_type;
    }

    private static void Postfix(WorldTile __instance, TileType __state)
    {
        if (__instance != null && !ReferenceEquals(__state, __instance.main_type))
        {
            bool riverTopologyChanged = __state?.id == "shallow_waters" ||
                                        __instance.main_type?.id == "shallow_waters";
            ClimateSystem.Active?.NotifyTerrainChanged(__instance, riverTopologyChanged);
        }
    }
}

/// <summary>群系、道路、海冰等顶层地形变化会立即使当地保湿和地表温度缓存失效。</summary>
[HarmonyPatch(typeof(WorldTile), nameof(WorldTile.setTopTileType),
    new Type[] { typeof(TopTileType), typeof(bool) })]
internal static class TerrainTopClimateSyncPatch
{
    private static void Prefix(WorldTile __instance, out TopTileType __state)
    {
        __state = __instance?.top_type;
    }

    private static void Postfix(WorldTile __instance, TopTileType __state)
    {
        if (__instance != null && !ReferenceEquals(__state, __instance.top_type))
            ClimateSystem.Active?.NotifyTerrainChanged(__instance);
    }
}

/// <summary>原版冻结只切换 frozen/cur_tile_type，不经过地形 setter，需要单独同步气候。</summary>
[HarmonyPatch(typeof(WorldTile), nameof(WorldTile.freeze))]
internal static class TerrainFreezeClimateSyncPatch
{
    private static void Prefix(WorldTile __instance, out bool __state)
    {
        __state = __instance?.data?.frozen == true;
    }

    private static void Postfix(WorldTile __instance, bool __state)
    {
        if (__instance?.data != null && __state != __instance.data.frozen)
            ClimateSystem.Active?.NotifyTerrainChanged(__instance);
    }
}

[HarmonyPatch(typeof(WorldTile), nameof(WorldTile.unfreeze))]
internal static class TerrainUnfreezeClimateSyncPatch
{
    private static void Prefix(WorldTile __instance, out bool __state)
    {
        __state = __instance?.data?.frozen == true;
    }

    private static void Postfix(WorldTile __instance, bool __state)
    {
        if (__instance?.data != null && __state != __instance.data.frozen)
            ClimateSystem.Active?.NotifyTerrainChanged(__instance);
    }
}

/// <summary>
/// 原版“解冻地块”世界行为不读取本模组温度，会持续融化所有临时冰雪。
/// 对仍低于当地融化阈值、且确由气候系统冻结的地块阻止该调用；回暖后正常放行。
/// </summary>
[HarmonyPatch(typeof(WorldTile), nameof(WorldTile.unfreeze))]
internal static class ClimateControlledUnfreezePatch
{
    private static bool Prefix(WorldTile __instance)
    {
        ClimateSystem climate = ClimateSystem.Active;
        return climate == null || !climate.ShouldBlockClimateUnfreeze(__instance);
    }
}

/// <summary>
/// 拦截原版群系扩张最终使用的地表替换入口。特殊/魔法群系不受限制；
/// 普通自然群系必须与目标格当前的气候目标完全一致，否则在写入前直接阻止，
/// 避免不适宜群系短暂出现、触发副作用后再被恢复。
/// </summary>
[HarmonyPatch]
internal static class ClimateBiomeSpreadPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (MethodInfo method in typeof(MapAction).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (method.Name != "terraformTop") continue;
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length >= 2 && parameters[0].ParameterType == typeof(WorldTile) &&
                parameters[1].ParameterType == typeof(TopTileType)) yield return method;
        }
    }

    private static bool Prefix(WorldTile __0, TopTileType __1, out bool __state)
    {
        __state = false;
        if (__0 == null || __1?.biome_asset == null) return true;
        if (!ClimateSystem.ManagedBiomesForPatch.Contains(__1.biome_asset.id)) return true;
        ClimateSystem climate = ClimateSystem.Active;
        if (climate == null || !climate.TryGetClimate(__0, out ClimateCell cell)) return true;
        // 气候系统自身的写入已经在执行前重新校验，不应被识别为原版扩张。
        if (climate.IsApplyingClimateBiome) return true;
        if (!climate.IsBiomeClimateAcceptableForPatch(
                __0, __1.biome_asset.id, cell.Temperature, cell.Humidity)) return false;
        __state = true;
        return true;
    }

    private static void Postfix(WorldTile __0, TopTileType __1, bool __state)
    {
        if (!__state || __0?.Type?.biome_asset?.id != __1?.biome_asset?.id) return;
        ClimateSystem.Active?.AcceptExternalBiomeExpansion(__0, __1.biome_asset.id);
    }
}

/// <summary>
/// 地图仍存在夜晚时保持原版光照管线开启，供窗户光 sprite 使用；
/// 全图白昼（仅可能出现在局部经纬模板下）时关闭，回到零开销路径。
/// </summary>
[HarmonyPatch(typeof(WorldAgeManager), nameof(WorldAgeManager.shouldShowLights))]
internal static class NightLightsEnablePatch
{
    private static void Postfix(ref bool __result)
    {
        ClimateSystem climate = ClimateSystem.Active;
        if (climate?.HasAnyNight == true) __result = true;
    }
}

/// <summary>
/// light_areas 光斑经 EffectsCamera 渲染进 RT 后由 LightRenderer 以
/// alpha = nightMod * 0.6 合成，普通纪元 nightMod 恒为 0——整条每帧
/// 遍历全部可见单位与建筑的链路零可见输出，直接跳过。黑暗纪元
/// nightMod &gt; 0，保持原版行为。建筑夜间的光晕由夜幕 shader 的
/// LightMask 承担，不依赖这条链路。
/// </summary>
[HarmonyPatch(typeof(QuantumSpriteLibrary), "drawLightAreas")]
internal static class SkipInvisibleLightAreasPatch
{
    private static bool Prefix()
    {
        return (World.world?.era_manager?.getNightMod() ?? 0f) > 0f;
    }
}

/// <summary>
/// 窗户光 sprite 直接渲染在 Objects 层（不经 nightMod 合成）。
/// 按建筑所在地昼夜过滤：白天的建筑不提交窗户光，夜侧保持原版效果。
/// getBuildingLight 的唯一调用方就是窗户光循环，过滤安全。
/// </summary>
[HarmonyPatch(typeof(DynamicSprites), nameof(DynamicSprites.getBuildingLight))]
internal static class FilterDayBuildingLightPatch
{
    private static bool Prefix(Building pBuilding, ref Sprite __result)
    {
        ClimateSystem climate = ClimateSystem.Active;
        if (climate == null || pBuilding?.current_tile == null) return true;
        if (climate.IsNightAt(pBuilding.current_tile)) return true;
        __result = null;
        return false;
    }
}
