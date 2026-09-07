using HarmonyLib;

namespace ClimateWeather;

[HarmonyPatch(typeof(PlayerControl),nameof(PlayerControl.isOverUI))]
internal static class ClimatePanelUiPatch
{
    private static void Postfix(ref bool __result)
    {
        if (ClimateSystem.Active?.IsPointerOnClimatePanel == true) __result=true;
    }
}

[HarmonyPatch(typeof(PlayerControl),nameof(PlayerControl.getMouseTilePos))]
internal static class ClimatePanelPickPatch
{
    private static void Postfix(ref WorldTile __result)
    {
        if (ClimateUiLayout.NativePopupVisible || ClimateSystem.Active?.IsPointerOnClimatePanel == true)
            __result=null;
    }
}
