using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ClimateWeather;

// Diagnostic preview only: no camera teleport, input remapping or world copy.
public sealed class HorizontalSeamPreview : MonoBehaviour
{
    internal static HorizontalSeamPreview Active;
    private bool _enabled;
    private Camera _camera;
    private RenderTexture _west, _east;
    private Rect _westMap, _eastMap;
    private readonly SeamEffectCopies _effectCopies = new SeamEffectCopies();
    private float _nextRender, _centerY, _halfHeight, _lastCaptureMs;
    private int _worldWidth, _worldHeight;
    private ZoneCamera _zones;
    private readonly HashSet<TileZone> _added = new HashSet<TileZone>();
    private readonly HashSet<TileZone> _stripZones = new HashSet<TileZone>();
    private WorldTile[] _worldTiles;
    private string _status = "等待场景更新";
    private Rect Window => new Rect(Mathf.Max(8, Screen.width - 536), 70, 520, 305);
    internal bool IsOpen => _enabled;
    private bool CanShow => !ClimateUiLayout.NativePopupVisible && ClimateUiLayout.Gameplay.height>=375 && Screen.width>=536;

    private void Awake() { Active = this; }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F9))
        {
            if (_enabled) Close();
            else if (World.world?.tiles_list != null && Camera.main != null)
            {
                _enabled = true;
                _worldTiles = World.world.tiles_list;
                _worldWidth = MapBox.width; _worldHeight = MapBox.height;
                _centerY = Mathf.Clamp(Camera.main.transform.position.y, 0, MapBox.height);
                _halfHeight = Mathf.Min(64f, Mathf.Min(MapBox.width, MapBox.height) * .5f);
                _nextRender = 0;
                _status = "等待场景更新";
            }
        }
        if (_enabled && (!ReferenceEquals(World.world?.tiles_list, _worldTiles) || MapBox.width != _worldWidth || MapBox.height != _worldHeight)) Close();
    }

    // Original clear() also clears these flags on the next visibility rebuild.
    internal void AddVisibleEdges(ZoneCamera zones)
    {
        if (!_enabled || World.world == null) return;
        if (_zones != zones) { Close(); return; }
        if (_stripZones.Count == 0)
        {
            AddStrip(0, _halfHeight * 2);
            AddStrip(MapBox.width - _halfHeight * 2, MapBox.width - 1);
        }
        foreach (TileZone zone in _stripZones)
        {
            if (zone.visible) continue;
            zone.visible = true;
            zone.visible_main_centered = true;
            zones._visible_zones.Add(zone);
            _added.Add(zone);
            if (zones._set_visible_chunks.Add(zone.chunk)) zones._list_visible_chunks.Add(zone.chunk);
        }
    }

    internal void BeforeVisibility(ZoneCamera zones)
    {
        if (!_enabled) return;
        if (_zones == null) _zones = zones;
        else if (_zones != zones) { Close(); return; }
    }

    private void AddStrip(float left, float right)
    {
        // Zone size is derived from tile.zone, never assumed in the renderer.
        // Cache the bounded strip once per preview opening, never every frame.
        int minY = Mathf.Max(0, Mathf.FloorToInt(_centerY - _halfHeight) - 2);
        int maxY = Mathf.Min(MapBox.height - 1, Mathf.CeilToInt(_centerY + _halfHeight) + 2);
        for (int y = minY; y <= maxY; y++)
        for (int x = Mathf.Max(0, Mathf.FloorToInt(left)-2); x <= Mathf.Min(MapBox.width-1, Mathf.CeilToInt(right)+2); x++)
        {
            TileZone zone = World.world.GetTileSimple(x, y)?.zone;
            if (zone != null) _stripZones.Add(zone);
        }
    }

    private void LateUpdate()
    {
        if (!_enabled || _zones == null || Time.unscaledTime < _nextRender || Camera.main == null || !CanShow) return;
        _nextRender = Time.unscaledTime + .5f;
        try
        {
            EnsureResources();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            _effectCopies.Prepare(_centerY - _halfHeight, _centerY + _halfHeight);
            _westMap = Capture(_west, MapBox.width - _halfHeight);
            _eastMap = Capture(_east, _halfHeight);
            _lastCaptureMs = (float)timer.Elapsed.TotalMilliseconds;
            _status = $"只读：跨缝副本 {_effectCopies.Count}" + (_effectCopies.BudgetReached ? "（达到预算）" : "");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[ClimateWeather] 接缝预览已关闭：" + e.Message);
            Close();
        }
        finally { _effectCopies.Hide(); }
    }

    private void EnsureResources()
    {
        if (_camera == null)
        {
            var go = new GameObject("ClimateSeamPreviewCamera");
            _camera = go.AddComponent<Camera>();
            _camera.enabled = false;
        }
        if (_west == null) _west = MakeTexture("ClimateWestEdge");
        if (_east == null) _east = MakeTexture("ClimateEastEdge");
    }

    private static RenderTexture MakeTexture(string name)
    {
        var texture = new RenderTexture(256, 256, 16, RenderTextureFormat.ARGB32) { name = name };
        texture.Create();
        return texture;
    }

    private Rect Capture(RenderTexture target, float x)
    {
        Camera source = Camera.main;
        _camera.CopyFrom(source);
        _camera.enabled = false;
        _camera.targetTexture = target;
        _camera.rect = new Rect(0,0,1,1);
        _camera.orthographic = true;
        _camera.orthographicSize = _halfHeight;
        _camera.aspect = 1f;
        _camera.ResetProjectionMatrix();
        _camera.transform.SetPositionAndRotation(new Vector3(x, _centerY, source.transform.position.z), source.transform.rotation);
        _camera.Render();
        Vector3 bottomLeft = _camera.WorldToViewportPoint(Vector3.zero);
        Vector3 topRight = _camera.WorldToViewportPoint(new Vector3(MapBox.width, MapBox.height, 0));
        return Rect.MinMaxRect(Mathf.Min(bottomLeft.x, topRight.x) * 256f,
            (1f - Mathf.Max(bottomLeft.y, topRight.y)) * 256f,
            Mathf.Max(bottomLeft.x, topRight.x) * 256f,
            (1f - Mathf.Min(bottomLeft.y, topRight.y)) * 256f);
    }

    private void OnGUI()
    {
        if (!_enabled || !CanShow) return;
        GUI.depth = -100;
        Rect window = Window;
        GUI.Box(window, "左右接缝预览（F9关闭；不是主镜头拼接）");
        DrawPane(new Rect(window.x+4, window.y+24,256,256), _west, _westMap);
        DrawPane(new Rect(window.x+260, window.y+24,256,256), _east, _eastMap);
        GUI.Label(new Rect(window.x+8,window.y+280,504,22), $"{_status}；提交 {_lastCaptureMs:F1} ms / 2 Hz");
    }

    private static void DrawPane(Rect pane, RenderTexture scene, Rect map)
    {
        if (scene == null) return;
        GUI.BeginGroup(pane);
        try
        {
            GUI.DrawTexture(new Rect(0,0,pane.width,pane.height),scene,ScaleMode.StretchToFill,false);
            ClimateSystem.Active?.DrawPreviewLayers(map);
        }
        finally { GUI.EndGroup(); }
    }

    internal bool BlocksPointer()
    {
        Vector3 mouse = Input.mousePosition;
        return _enabled && CanShow && Window.Contains(new Vector2(mouse.x, Screen.height - mouse.y));
    }

    private void Close()
    {
        _enabled = false;
        _effectCopies.Dispose();
        foreach (TileZone zone in _added) { zone.visible = false; zone.visible_main_centered = false; }
        _added.Clear();
        _stripZones.Clear();
        _worldTiles = null;
        if (_zones != null) _zones._last_start_x = -1;
        _zones = null;
        if (_camera != null) { _camera.targetTexture = null; Destroy(_camera.gameObject); _camera = null; }
        if (_west != null) { _west.Release(); Destroy(_west); _west = null; }
        if (_east != null) { _east.Release(); Destroy(_east); _east = null; }
    }

    private void OnDisable() { Close(); }
    private void OnDestroy() { Close(); if (Active == this) Active = null; }
}

[HarmonyPatch(typeof(ZoneCamera), nameof(ZoneCamera.update))]
internal static class SeamPreviewVisibilityPatch
{
    private static void Prefix(ZoneCamera __instance) => HorizontalSeamPreview.Active?.BeforeVisibility(__instance);
    private static void Postfix(ZoneCamera __instance) => HorizontalSeamPreview.Active?.AddVisibleEdges(__instance);
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.getMouseTilePos))]
internal static class SeamPreviewReadOnlyPatch
{
    private static bool Prefix(ref WorldTile __result)
    {
        if (HorizontalSeamPreview.Active?.BlocksPointer() != true) return true;
        __result = null;
        return false;
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.isOverUI))]
internal static class SeamPreviewUiPatch
{
    private static void Postfix(ref bool __result)
    {
        if (HorizontalSeamPreview.Active?.BlocksPointer() == true) __result = true;
    }
}
