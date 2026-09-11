using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ai;

namespace ClimateWeather;

[HarmonyPatch(typeof(ActorTool), nameof(ActorTool.findNewBuildingTarget))]
internal static class HorizontalMineTargets
{
    private sealed class Cursor { internal int Index; }
    private static readonly ConditionalWeakTable<Actor, Cursor> Cursors = new();

    private static void Postfix(Actor pActor, string pType, bool pOnlyFreeTile, ref Building __result)
    {
        // 只补矿井的失败回退，不改变篝火、训练场或原版已选中的目标。
        if (__result != null || pType != "type_mine" || ClimateSystem.Active?.HorizontalWrap != true ||
            pActor?.city == null || pActor.current_tile == null || !pActor.isAlive() ||
            pActor.under_forces || pActor.asset.is_boat || pActor.isFlying() ||
            pActor.isWaterCreature() || pActor.is_inside_boat) return;
        var buildings = pActor.city.getBuildingListOfType(pType);
        if (buildings == null || buildings.Count == 0) return;
        Cursor cursor = Cursors.GetOrCreateValue(pActor);
        int start = cursor.Index % buildings.Count;
        // 限制每次候选量，轮换起点避免不可达矿井长期挡住后续目标。
        int count = Math.Min(16, buildings.Count);
        for (int i = 0; i < count; i++)
        {
            int index = (start + i) % buildings.Count;
            Building candidate = buildings[index];
            if (candidate?.current_tile == null || !candidate.isAlive() ||
                candidate.isUnderConstruction() || !candidate.isUsable() ||
                candidate.current_tile.zone?.city != pActor.city ||
                (pOnlyFreeTile && candidate.current_tile.isTargeted()) ||
                Math.Abs(candidate.current_tile.x - pActor.current_tile.x) <= MapBox.width * .5f) continue;
            if (!HorizontalWorkTargets.Reachable(pActor.current_tile, candidate.current_tile)) continue;
            __result = candidate;
            cursor.Index = (index + 1) % buildings.Count;
            return;
        }
        cursor.Index = (start + 1) % buildings.Count;
    }
}
