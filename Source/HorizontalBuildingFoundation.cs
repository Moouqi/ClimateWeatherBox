using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace ClimateWeather;

// 仅替换城市新建普通建筑的同岛检查；不改变全局岛屿或地基取格方式。
[HarmonyPatch(typeof(BuildingManager), nameof(BuildingManager.canBuildFrom))]
internal static class HorizontalBuildingFoundation
{
    internal struct Context
    {
        internal bool Active;
        internal City City;
        internal WorldTile Anchor;
        internal bool? Reachable;
    }
    [ThreadStatic] private static Context _current;

    private static void Prefix(WorldTile pTile, BuildingAsset pNewBuildingAsset, City pCity,
        BuildPlacingType pType, bool pFloraGrowth, out Context __state)
    {
        __state = _current;
        // 使用值类型上下文，逐候选检查不分配对象；嵌套调用仍各自恢复。
        _current = new Context
        {
            Active = ClimateSystem.Active?.HorizontalWrap == true && pCity != null && pTile != null &&
                pNewBuildingAsset != null && pNewBuildingAsset.city_building && !pNewBuildingAsset.docks &&
                !pNewBuildingAsset.flora && !pFloraGrowth && pType == BuildPlacingType.New,
            City = pCity,
            Anchor = pTile
        };
    }

    internal static bool SameIslandOrConnectedFoundation(WorldTile tile, WorldTile center)
    {
        if (tile.isSameIsland(center)) return true;
        if (!_current.Active || center == null || tile.zone?.city != _current.City ||
            center.zone?.city != _current.City || _current.Anchor.zone?.city != _current.City ||
            tile.region == null || tile.region != _current.Anchor.region ||
            !HorizontalGroundMovement.Walkable(tile)) return false;
        // 一个地基只验证一次到锚点的安全陆路，同一地面区域内的其余格复用结果。
        // 沿用有界查询预算，不为每个地基格重复寻路。
        if (!_current.Reachable.HasValue)
            _current.Reachable = HorizontalWorkTargets.Reachable(center, _current.Anchor);
        return _current.Reachable.Value;
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var original = AccessTools.Method(typeof(WorldTile), nameof(WorldTile.isSameIsland), new[] { typeof(WorldTile) });
        int count = 0;
        foreach (CodeInstruction code in instructions)
        {
            if (Equals(code.operand, original))
            {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(HorizontalBuildingFoundation), nameof(SameIslandOrConnectedFoundation));
                count++;
            }
            yield return code;
        }
        if (count != 1) throw new InvalidOperationException("建筑地基补丁未匹配同岛检查");
    }

    private static void Finalizer(Context __state) => _current = __state;
}
