using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace ClimateWeather;

// 只替换原版已通过海域、地基和占用检查后的港口靠岸条件。
internal static class HorizontalDockPlacement
{
    internal static void Install()
    {
        // 港口为独立可选补丁；失败只关闭此项，不能让 PatchAll 中断整个模组。
        var harmony = new Harmony("WB.CLIMATE.WEATHER.DOCK");
        var target = AccessTools.Method(typeof(BuildingManager), nameof(BuildingManager.canBuildFrom));
        try
        {
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(HorizontalDockPlacement), nameof(Transpiler)));
            UnityEngine.Debug.Log("[ClimateWeather] 港口跨界选址补丁已加载");
        }
        catch (Exception error)
        {
            try { harmony.Unpatch(target, HarmonyPatchType.Transpiler, harmony.Id); }
            catch (Exception cleanup) { UnityEngine.Debug.LogWarning("[ClimateWeather] 港口补丁清理失败：" + cleanup); }
            UnityEngine.Debug.LogWarning("[ClimateWeather] 港口跨界选址不可用，保留原版港口规则；其他系统继续运行：" + error);
        }
    }

    private static int LocalIndex(CodeInstruction code)
    {
        if (code.opcode == OpCodes.Ldloc_0) return 0;
        if (code.opcode == OpCodes.Ldloc_1) return 1;
        if (code.opcode == OpCodes.Ldloc_2) return 2;
        if (code.opcode == OpCodes.Ldloc_3) return 3;
        if (code.opcode != OpCodes.Ldloc && code.opcode != OpCodes.Ldloc_S) return -1;
        return code.operand is LocalBuilder local ? local.LocalIndex : Convert.ToInt32(code.operand);
    }

    internal static bool ConnectedShore(WorldTile shore, WorldTile center, City city)
    {
        if (shore.region.island == center?.region.island) return true;
        if (ClimateSystem.Active?.HorizontalWrap != true || city == null || center == null ||
            shore.zone?.city != city || center.zone?.city != city ||
            !HorizontalGroundMovement.Walkable(shore)) return false;
        // 沿用有限陆路查询及共享预算，不合并岛屿，也不允许隔海建立港口。
        return HorizontalWorkTargets.Reachable(center, shore);
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        var region = AccessTools.Field(typeof(WorldTile), nameof(WorldTile.region));
        var island = AccessTools.Field(typeof(MapRegion), nameof(MapRegion.island));
        int matches = 0;
        for (int i = 0; i + 10 < codes.Count; i++)
        {
            // Harmony 会展开短跳转及局部变量指令，匹配语义而非只接受磁盘 IL 的短形式。
            if (LocalIndex(codes[i]) < 0 ||
                !codes[i + 1].LoadsField(region) || !codes[i + 2].LoadsField(island) ||
                LocalIndex(codes[i + 3]) < 0 ||
                (codes[i + 4].opcode != OpCodes.Brtrue_S && codes[i + 4].opcode != OpCodes.Brtrue) ||
                codes[i + 5].opcode != OpCodes.Ldnull ||
                (codes[i + 6].opcode != OpCodes.Br_S && codes[i + 6].opcode != OpCodes.Br) ||
                LocalIndex(codes[i + 3]) != LocalIndex(codes[i + 7]) ||
                !codes[i + 8].LoadsField(region) || !codes[i + 9].LoadsField(island) ||
                (codes[i + 10].opcode != OpCodes.Bne_Un_S && codes[i + 10].opcode != OpCodes.Bne_Un)) continue;
            var replacement = new List<CodeInstruction>
            {
                new CodeInstruction(codes[i]),
                new CodeInstruction(codes[i + 3].opcode, codes[i + 3].operand),
                new CodeInstruction(OpCodes.Ldarg_3),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(HorizontalDockPlacement), nameof(ConnectedShore))),
                new CodeInstruction(OpCodes.Brfalse, codes[i + 10].operand)
            };
            codes.RemoveRange(i, 11);
            codes.InsertRange(i, replacement);
            matches++;
        }
        if (matches != 1) throw new InvalidOperationException("港口跨界补丁未匹配唯一靠岸岛屿检查");
        return codes;
    }
}
