using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ClimateWeather;

internal static class HorizontalBrush
{
    [ThreadStatic] internal static int PlayerDepth;
    private static readonly HashSet<int> Seen = new();
    internal static bool Enabled => HorizontalCameraWrap.Active?.Running == true;
    internal static bool Applying => Enabled && PlayerDepth > 0;

    internal static void EndStroke(PlayerControl player)
    {
        if (player == null) return;
        player._last_click.Set(-1,-1);
        player.first_pressed_tile = null;
        player.first_pressed_type = null;
        player.first_pressed_top_type = null;
        player.first_click = true;
    }

    // Finish collecting before invoking actions, so reentrant actions cannot
    // invalidate the deduplication scratch set. ListPool is owned by each caller.
    internal static void Collect(WorldTile center, BrushData brush, ListPool<WorldTile> tiles)
    {
        Seen.Clear();
        foreach (var offset in brush.pos)
        {
            int y = center.y + offset.y;
            if (HorizontalTopology.TryAddWrappedCell(center.x + offset.x, y,
                MapBox.width, MapBox.height, Seen, out int x))
                tiles.Add(World.world.GetTileSimple(x,y));
        }
    }
}

[HarmonyPatch(typeof(PlayerControl),nameof(PlayerControl.clickedStart))]
internal static class HorizontalBrushStrokePatch
{
    private static bool Prefix(PlayerControl __instance)
    {
        if (!HorizontalBrush.Enabled) return true;
        if (!HorizontalCameraWrap.Active.ContainsPointer() || World.world.isOverUI() || ScrollWindow.isWindowActive())
        {
            HorizontalBrush.EndStroke(__instance);
            return false;
        }
        WorldTile tile = __instance.getMouseTilePos();
        var power = World.world.selected_buttons.selectedButton?.godPower;
        if (tile == null || power == null)
        {
            HorizontalBrush.EndStroke(__instance);
            return false;
        }
        __instance.already_used_power = true;
        var brush = Brush.get(string.IsNullOrEmpty(power.force_brush) ? Config.current_brush : power.force_brush);
        Vector2Int current = new Vector2Int(tile.x,tile.y);
        Vector2Int previous = __instance._last_click;
        if (brush.continuous && power.draw_lines && previous.x != -1 && current != previous)
        {
            float dx = HorizontalTopology.Delta(current.x,previous.x,MapBox.width);
            float dy = previous.y-current.y;
            int steps = (int)(Mathf.Sqrt(dx*dx+dy*dy)/(brush.size+1f))+1;
            // Final center is applied once below; do not repeat it at i=0.
            Vector2Int last = current;
            for (int i=1;i<steps;i++)
            {
                float t = i/(float)steps;
                var p = new Vector2Int(HorizontalTopology.Wrap(Mathf.FloorToInt(current.x+dx*t),MapBox.width),
                    Mathf.FloorToInt(current.y+dy*t));
                if (p == last || p.y<0 || p.y>=MapBox.height) continue;
                __instance.clickedFinal(p,power,pTrack:false);
                last=p;
            }
        }
        __instance.clickedFinal(current,power);
        __instance.first_click=false;
        __instance._last_click=current;
        return false;
    }
}

// Only synchronous player power execution enables brush topology. Simulation
// callers of MapBox.loopWithBrush keep their original bounded behavior.
[HarmonyPatch(typeof(PlayerControl),nameof(PlayerControl.clickedFinal))]
internal static class HorizontalBrushScopePatch
{
    private static void Prefix(out int __state)
    {
        __state = HorizontalBrush.PlayerDepth;
        if (HorizontalBrush.Enabled) HorizontalBrush.PlayerDepth++;
    }
    private static Exception Finalizer(Exception __exception,int __state)
    {
        HorizontalBrush.PlayerDepth=__state;
        return __exception;
    }
}

[HarmonyPatch(typeof(MapBox),nameof(MapBox.loopWithBrush),new Type[]{typeof(WorldTile),typeof(BrushData),typeof(PowerAction),typeof(GodPower)})]
internal static class HorizontalPowerBrushPatch
{
    private static bool Prefix(WorldTile pCenterTile,BrushData pBrush,PowerAction pAction,GodPower pPower)
    {
        if (!HorizontalBrush.Applying) return true;
        using var tiles = new ListPool<WorldTile>();
        HorizontalBrush.Collect(pCenterTile,pBrush,tiles);
        foreach (var tile in tiles) pAction(tile,pPower);
        return false;
    }
}

[HarmonyPatch(typeof(MapBox),nameof(MapBox.loopWithBrush),new Type[]{typeof(WorldTile),typeof(BrushData),typeof(PowerActionWithID),typeof(string)})]
internal static class HorizontalIdBrushPatch
{
    private static bool Prefix(WorldTile pCenterTile,BrushData pBrush,PowerActionWithID pAction,string pPowerID)
    {
        if (!HorizontalBrush.Applying) return true;
        using var tiles = new ListPool<WorldTile>();
        HorizontalBrush.Collect(pCenterTile,pBrush,tiles);
        foreach (var tile in tiles) pAction(tile,pPowerID);
        return false;
    }
}

[HarmonyPatch(typeof(MapBox),nameof(MapBox.loopWithBrushPowerForDropsRandom))]
internal static class HorizontalRandomBrushPatch
{
    private static bool Prefix(WorldTile pCenterTile,BrushData pBrush,PowerAction pAction,GodPower pPower)
    {
        if (!HorizontalBrush.Applying) return true;
        using var tiles = new ListPool<WorldTile>();
        HorizontalBrush.Collect(pCenterTile,pBrush,tiles);
        tiles.Shuffle();
        for (int i=0;i<pBrush.drops && tiles.Count>0;i++) pAction(tiles.Pop(),pPower);
        return false;
    }
}

[HarmonyPatch(typeof(PlayerControl),nameof(PlayerControl.highlightFrom))]
internal static class HorizontalBrushHighlightPatch
{
    private static bool Prefix(WorldTile pTile,BrushData pBrushData)
    {
        if (!HorizontalBrush.Enabled) return true;
        if (pTile == null) return false;
        using var tiles = new ListPool<WorldTile>();
        HorizontalBrush.Collect(pTile,pBrushData,tiles);
        foreach (var tile in tiles) World.world.flash_effects.flashPixel(tile,20);
        return false;
    }
}
