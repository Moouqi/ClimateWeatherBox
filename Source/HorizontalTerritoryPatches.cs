using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ClimateWeather;

internal static class HorizontalTerritoryQueries
{
    private static int _routeFrame = -1, _routeChecks;

    internal static bool SameIslandOrSeam(WorldTile target, WorldTile origin)
    {
        if (target.isSameIsland(origin)) return true;
        if (ClimateSystem.Active?.HorizontalWrap != true || target.zone.hasCity() ||
            !HorizontalGroundMovement.Walkable(target) || !HorizontalGroundMovement.Walkable(origin) ||
            Math.Abs(target.x - origin.x) <= MapBox.width * .5f) return false;
        // 仅放行实际可走的安全陆地，且给重复的 AI 目标检查设置统一帧预算。
        if (_routeFrame != UnityEngine.Time.frameCount) { _routeFrame = UnityEngine.Time.frameCount; _routeChecks = 0; }
        if (_routeChecks >= 2) return false;
        _routeChecks++;
        int width = MapBox.width;
        var path = new List<int>();
        return HorizontalGroundPathfinder.Find(width, MapBox.height, origin.y * width + origin.x,
            target.y * width + target.x, true,
            index => HorizontalGroundMovement.Walkable(World.world.GetTileSimple(index % width, index / width)),
            2048, path, out _) == HorizontalGroundPathfinder.Result.Found;
    }

    internal static TileZone[] Neighbours(TileZone zone)
    {
        if (ClimateSystem.Active?.HorizontalWrap != true || !zone.world_edge) return zone.neighbours;
        var list = new List<TileZone>();
        HorizontalTerritoryTopology.CollectNeighbours(zone, list);
        return list.ToArray();
    }
    internal static TileZone[] ClaimNeighbours(TileZone zone)
    {
        if (zone.hasCity() || ClimateSystem.Active?.HorizontalWrap != true || !zone.world_edge)
            return zone.neighbours;
        var neighbours = new List<TileZone>(Neighbours(zone));
        // 最终落地校验也要求陆地直接相接，不能仅依赖前面的候选搜索。
        neighbours.RemoveAll(next => Array.IndexOf(zone.neighbours, next) < 0 &&
            !HorizontalTerritoryTopology.HasLandBridge(zone, next));
        return neighbours.ToArray();
    }

    internal static bool Connected(TileZone from, TileZone to, MapRegion region, ListPool<MapRegion> output)
    {
        // 只替换真正跨缝的邻区连接，其他连接保留原版规则。
        if (ClimateSystem.Active?.HorizontalWrap != true || Array.IndexOf(from.neighbours, to) >= 0)
            return TileZone.hasZonesConnectedViaRegions(from, to, region, output);
        var regions = new List<MapRegion>();
        bool connected = HorizontalTerritoryTopology.CollectSeamRegions(from, to, region, regions);
        foreach (MapRegion next in regions) output.Add(next);
        return connected;
    }
    internal static int Distance(int x1, int y1, int x2, int y2)
    {
        if (ClimateSystem.Active?.HorizontalWrap != true) return Toolbox.SquaredDist(x1, y1, x2, y2);
        int columns = World.world.GetTileSimple(MapBox.width - 1, 0).zone.x + 1;
        int dx = Math.Abs(x1 - x2), dy = y1 - y2;
        dx = Math.Min(dx, columns - dx);
        return dx * dx + dy * dy;
    }
    internal static TileZone Left(TileZone zone) => Side(zone, true);
    internal static TileZone Right(TileZone zone) => Side(zone, false);
    internal static TileZone MetaLeft(TileZone zone, MetaTypeAsset asset) =>
        Left(zone);
    internal static TileZone MetaRight(TileZone zone, MetaTypeAsset asset) =>
        Right(zone);
    private static TileZone Side(TileZone zone, bool left)
    {
        TileZone next = left ? zone.zone_left : zone.zone_right;
        if (next != null || ClimateSystem.Active?.HorizontalWrap != true) return next;
        // 仅影响视图边线查询，不把邻区写回全局字段或合并不同归属。
        return World.world.GetTileSimple(left ? MapBox.width - 1 : 0, zone.centerTile.y)?.zone;
    }
}

[HarmonyPatch(typeof(BehActorCheckZoneTarget), nameof(BehActorCheckZoneTarget.execute))]
internal static class HorizontalTerritoryTargetPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        int count = 0;
        foreach (CodeInstruction code in instructions)
        {
            if (code.operand is MethodInfo method && method.DeclaringType == typeof(WorldTile) &&
                method.Name == nameof(WorldTile.isSameIsland))
            {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(HorizontalTerritoryQueries), "SameIslandOrSeam");
                count++;
            }
            yield return code;
        }
        if (count != 2) throw new InvalidOperationException("领土目标补丁未匹配岛屿检查");
    }
}

[HarmonyPatch]
internal static class HorizontalTerritoryFlowPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(CityZoneGrowth), nameof(CityZoneGrowth.startWaveFromTile));
        yield return AccessTools.Method(typeof(CityZoneAbandon), nameof(CityZoneAbandon.startWaveFromTile));
        yield return AccessTools.Method(typeof(City), nameof(City.isZoneToClaimStillGood));
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        int neighbours = 0, connections = 0;
        foreach (CodeInstruction code in instructions)
        {
            if (code.opcode == OpCodes.Ldfld && Equals(code.operand, AccessTools.Field(typeof(TileZone), "neighbours")))
            {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(HorizontalTerritoryQueries),
                    __originalMethod.DeclaringType == typeof(City) ? "ClaimNeighbours" : "Neighbours");
                neighbours++;
            }
            else if (code.operand is MethodInfo method && method.DeclaringType == typeof(TileZone) &&
                     method.Name == nameof(TileZone.hasZonesConnectedViaRegions))
            {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(HorizontalTerritoryQueries), "Connected");
                connections++;
            }
            else if (code.operand is MethodInfo distance && distance == AccessTools.Method(typeof(Toolbox),
                         "SquaredDist", new[] { typeof(int), typeof(int), typeof(int), typeof(int) }))
            {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(HorizontalTerritoryQueries), "Distance");
            }
            yield return code;
        }
        // 游戏升级导致 IL 结构变化时明确失败，不静默装上只有半套逻辑的补丁。
        int expectedNeighbours = __originalMethod.DeclaringType == typeof(CityZoneGrowth) ? 2 : 1;
        if (neighbours != expectedNeighbours || (__originalMethod.DeclaringType != typeof(City) && connections != 1))
            throw new InvalidOperationException("领土跨缝补丁未匹配原版方法：" + __originalMethod.Name);
    }
}

[HarmonyPatch(typeof(CityZoneGrowth), nameof(CityZoneGrowth.getZoneToClaim))]
internal static class HorizontalTerritoryCandidatePatch
{
    private static void Postfix(CityZoneGrowth __instance, City pCity, bool pDebug, int pBonusRange, ref TileZone __result)
    {
        if (__result != null || pDebug || ClimateSystem.Active?.HorizontalWrap != true || pCity?.getTile() == null) return;
        TileZone origin = pCity.getTile().zone;
        float radius = (pCity.getZoneRange() + pBonusRange) * .75f;
        // 原版随机边区支路可能找不到跨缝候选，复用本次波搜索已验证的候选，不另扫全图。
        foreach (ZoneConnection connection in __instance._zones_checked)
        {
            TileZone zone = connection.zone;
            if (Math.Abs(zone.centerTile.x - pCity.getTile().x) <= MapBox.width * .5f ||
                !HorizontalTerritoryTopology.CanPeacefullyClaim(zone, pCity) ||
                HorizontalTerritoryQueries.Distance(origin.x, origin.y, zone.x, zone.y) > radius * radius) continue;
            __result = zone;
            return;
        }
    }
}

[HarmonyPatch]
internal static class HorizontalTerritoryBorderPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ZoneCalculator), nameof(ZoneCalculator.drawZoneCity));
        yield return AccessTools.Method(typeof(ZoneCalculator), nameof(ZoneCalculator.drawZoneAlliance));
        yield return AccessTools.Method(typeof(ZoneCalculator), nameof(ZoneCalculator.drawGenericFluid));
        yield return AccessTools.Method(typeof(ZoneCalculator), nameof(ZoneCalculator.drawZoneMeta),
            new[] { typeof(TileZone), typeof(MetaTypeAsset), typeof(MetaZoneGetMetaSimple) });
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        int count = 0;
        bool meta = __originalMethod.Name == nameof(ZoneCalculator.drawZoneMeta);
        foreach (CodeInstruction code in instructions)
        {
            if (code.opcode == OpCodes.Ldfld && code.operand is FieldInfo field && field.DeclaringType == typeof(TileZone) &&
                (field.Name == "zone_left" || field.Name == "zone_right"))
            {
                // 文化、村庄等通用图层也查询接缝另一侧，归属比较仍由原版完成。
                // 保留原指令的标签和异常块，使跳转落点也会先压入图层参数。
                if (meta)
                {
                    string helper = field.Name == "zone_left" ? "MetaLeft" : "MetaRight";
                    code.opcode = OpCodes.Ldarg_2;
                    code.operand = null;
                    yield return code;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(HorizontalTerritoryQueries), helper));
                    count++;
                    continue;
                }
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(HorizontalTerritoryQueries), field.Name == "zone_left" ? "Left" : "Right");
                count++;
            }
            yield return code;
        }
        if (count != 2) throw new InvalidOperationException("领土边线补丁未匹配左右邻区读取：" + __originalMethod.Name);
    }
}
