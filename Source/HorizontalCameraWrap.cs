using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ClimateWeather;

// Opt-in presentation topology. Never changes World.GetTile or unit pathfinding.
[DefaultExecutionOrder(10000)]
public sealed class HorizontalCameraWrap : MonoBehaviour
{
    internal static HorizontalCameraWrap Active;
    private bool _enabled;
    private WorldTile[] _world;
    private ZoneCamera _zones;
    private Camera _capture;
    private readonly List<HorizontalTopology.ViewSlice> _slices = new();
    private readonly List<Pane> _panes = new();
    private readonly SeamEffectCopies _effects = new();
    private int _count;
    private Rect _windowNormalized, _window;
    private Camera _maskedCamera;
    private int _sceneMask;
    private readonly List<RectInt> _zoneRanges = new(), _previousZoneRanges = new();
    private bool _visibilityCached, _previewWasOpen;
    private int _cachedZoneCount, _visibilityBuilds, _visibilityHits;
    private double _captureTotalMs, _captureMaxMs;
    private int _captureSamples;
    private float _nextMetrics;
    private string _metrics = "等待渲染统计";
    internal void InvalidateVisibility() => _visibilityCached = false;
    internal Rect MapWindow => _window;
    private sealed class Pane { internal RenderTexture Texture; internal Rect Screen, Map; }
    internal bool Running => _enabled && ReferenceEquals(_world, World.world?.tiles_list) &&
        ClimateSystem.Active?.HorizontalWrap == true && Camera.main != null;

    private void Awake()
    {
        Active = this;
        Camera.onPreCull += BeforeCamera;
        Camera.onPostRender += AfterCamera;
    }
    private void BeforeCamera(Camera camera)
    {
        if (!Running || _count == 0 || ClimateUiLayout.NativePopupVisible || camera != Camera.main) return;
        _maskedCamera = camera;
        _sceneMask = camera.cullingMask;
        camera.cullingMask = 0; // Only the bounded compositor presents the world.
    }
    private void AfterCamera(Camera camera)
    {
        if (camera != _maskedCamera) return;
        camera.cullingMask = _sceneMask;
        _maskedCamera = null;
    }
    private void InitializeWindow()
    {
        Camera cam = Camera.main;
        Vector3 a = cam.WorldToScreenPoint(Vector3.zero);
        Vector3 b = cam.WorldToScreenPoint(new Vector3(MapBox.width,MapBox.height,0));
        float bottom = Screen.height-ClimateUiLayout.Gameplay.height;
        float left = Mathf.Clamp(Mathf.Min(a.x,b.x),0,Screen.width-1);
        float right = Mathf.Clamp(Mathf.Max(a.x,b.x),left+1,Screen.width);
        float top = Mathf.Clamp(Screen.height-Mathf.Max(a.y,b.y),0,Screen.height-bottom-1);
        float end = Mathf.Clamp(Screen.height-Mathf.Min(a.y,b.y),top+1,Screen.height-bottom);
        _windowNormalized = new Rect(left/Screen.width,top/Screen.height,(right-left)/Screen.width,(end-top)/Screen.height);
    }
    internal bool ContainsPointer()
    {
        Vector3 p = Input.mousePosition;
        Vector2 mouse=new Vector2(p.x,Screen.height-p.y);
        return !ClimateUiLayout.NativePopupVisible && ClimateUiLayout.Gameplay.Contains(mouse) && _window.Contains(mouse);
    }
    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F10) && !Globals.TRAILER_MODE)
        {
            if (_enabled) Close();
            else if (ClimateSystem.Active?.HorizontalWrap == true && World.world?.tiles_list != null)
            {
                _world = World.world.tiles_list;
                InitializeWindow();
                HorizontalBrush.EndStroke(World.world.player_control);
                MoveCamera.instance?.clearTouches();
                _enabled = true;
                Debug.Log("[ClimateWeather] 有界地图窗口 v2 已开启（F10关闭）；窗口内循环滑动，不改变单位寻路。");
            }
            else Debug.LogWarning("[ClimateWeather] 左右回绕需要全球经度范围（跨度至少359度）。");
        }
        if (_enabled && !Running) Close();
        if (Running && (!ContainsPointer() || ScrollWindow.isWindowActive() ||
            World.world.isOverUI() || !InputHelpers.GetMouseButton(0)))
            HorizontalBrush.EndStroke(World.world.player_control);
    }

    internal void Recenter(MoveCamera mover)
    {
        Vector3 p = mover.transform.position;
        float x = HorizontalTopology.Wrap(p.x, MapBox.width);
        float shift = x - p.x;
        // _origin is in world space; _first_touch is in screen space.
        if (mover._origin != new Vector3(-1,-1,-1))
        {
            mover._origin.x += shift;
        }
        mover.transform.position = new Vector3(x,Mathf.Clamp(p.y,0,MapBox.height),-.5f);
    }

    private bool View(out Vector3 low, out Vector3 high)
    {
        Camera cam = Camera.main;
        _window = new Rect(_windowNormalized.x*Screen.width,_windowNormalized.y*Screen.height,
            _windowNormalized.width*Screen.width,_windowNormalized.height*Screen.height);
        // Zooming out must never reveal multiple complete world copies.
        float scale = Screen.height/(cam.orthographicSize*2);
        Vector2 center = _window.center;
        _window.width = Mathf.Min(_window.width,MapBox.width*scale);
        _window.height = Mathf.Min(_window.height,MapBox.height*scale);
        _window.center = center;
        low = cam.ScreenToWorldPoint(new Vector3(_window.x,Screen.height-_window.yMax,cam.nearClipPlane));
        high = cam.ScreenToWorldPoint(new Vector3(_window.xMax,Screen.height-_window.y,cam.nearClipPlane));
        return cam.orthographic && high.y > low.y &&
            HorizontalTopology.SplitView(low.x,high.x,MapBox.width,_slices,8);
    }

    internal bool Visibility(ZoneCamera zones)
    {
        if (!Running || !View(out var low,out var high)) return false;
        _zoneRanges.Clear();
        // Visit zone rectangles, never scan all map tiles. Union each source strip.
        foreach (var slice in _slices)
        {
            int x0 = Mathf.Clamp((int)Math.Floor(slice.SourceStart),0,MapBox.width-1);
            int x1 = Mathf.Clamp((int)Math.Ceiling(slice.SourceEnd)-1,0,MapBox.width-1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(low.y),0,MapBox.height-1);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(high.y),0,MapBox.height-1);
            var a = World.world.GetTileSimple(x0,y0).zone;
            var b = World.world.GetTileSimple(x1,y1).zone;
            int startX=Mathf.Max(0,a.x-1), startY=Mathf.Max(0,a.y-1);
            _zoneRanges.Add(new RectInt(startX,startY,
                Mathf.Min(zones._zone_manager.zones_total_x-1,b.x+1)-startX+1,
                Mathf.Min(zones._zone_manager.zones_total_y-1,b.y+1)-startY+1));
        }
        bool preview = HorizontalSeamPreview.Active?.IsOpen == true;
        bool same = _visibilityCached && _zones == zones && !preview && !_previewWasOpen &&
            zones._visible_zones.Count == _cachedZoneCount && _zoneRanges.Count == _previousZoneRanges.Count;
        for (int i=0;same && i<_zoneRanges.Count;i++) same = _zoneRanges[i].Equals(_previousZoneRanges[i]);
        if (same) { _visibilityHits++; return true; }
        _zones = zones;
        zones.clear();
        foreach (RectInt range in _zoneRanges)
        {
            for (int y=range.yMin;y<range.yMax;y++)
            for (int x=range.xMin;x<range.xMax;x++)
            {
                var z = zones._zone_manager.getZoneUnsafe(x,y);
                if (z.visible) continue;
                z.visible = z.visible_main_centered = true;
                zones._visible_zones.Add(z);
                if (zones._set_visible_chunks.Add(z.chunk)) zones._list_visible_chunks.Add(z.chunk);
            }
        }
        _previousZoneRanges.Clear();
        _previousZoneRanges.AddRange(_zoneRanges);
        _cachedZoneCount = zones._visible_zones.Count;
        _previewWasOpen = preview;
        _visibilityCached = true;
        _visibilityBuilds++;
        zones._last_start_x = -1;
        return true;
    }

    private void LateUpdate()
    {
        _count = 0;
        if (!Running || ClimateUiLayout.NativePopupVisible) return;
        long captureStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (!View(out var low,out var high)) { Close(); return; }
            if (_capture == null)
            {
                _capture = new GameObject("ClimateWrapCamera").AddComponent<Camera>();
                _capture.enabled = false;
            }
            _effects.Prepare(low.y,high.y);
            foreach (var slice in _slices)
            {
                if (_count == _panes.Count) _panes.Add(new Pane());
                Pane pane = _panes[_count];
                float left = _window.x+(float)((slice.DisplayStart-low.x)/(high.x-low.x)*_window.width);
                float right = _window.x+(float)((slice.DisplayEnd-low.x)/(high.x-low.x)*_window.width);
                pane.Screen = new Rect(left,_window.y,right-left,_window.height);
                // Bound allocation cost, and reuse targets while dragging/resizing.
                // Each pane reserves window width: seam movement must not allocate
                // a new GPU texture every time the seam crosses a 64-pixel boundary.
                int w = Mathf.Clamp(Mathf.CeilToInt(_window.width/64)*64,64,1024);
                int h = Mathf.Clamp(Mathf.CeilToInt(_window.height/64)*64,64,1024);
                if (pane.Texture == null || pane.Texture.width < w || pane.Texture.height < h)
                {
                    Release(pane);
                    pane.Texture = new RenderTexture(w,h,16,RenderTextureFormat.ARGB32);
                    pane.Texture.Create();
                }
                Camera main = Camera.main;
                _capture.CopyFrom(main);
                _capture.enabled = false;
                _capture.targetTexture = pane.Texture;
                _capture.rect = new Rect(0,0,1,1);
                // CopyFrom may inherit Depth/Nothing clearing from the game camera.
                // Auxiliary targets have no background camera, so explicitly clear.
                _capture.clearFlags = CameraClearFlags.SolidColor;
                _capture.orthographicSize = (high.y-low.y)*.5f;
                _capture.aspect = (float)(slice.SourceEnd-slice.SourceStart)/(high.y-low.y);
                _capture.ResetProjectionMatrix();
                _capture.transform.SetPositionAndRotation(new Vector3((float)((slice.SourceStart+slice.SourceEnd)*.5),
                    (low.y+high.y)*.5f,main.transform.position.z),main.transform.rotation);
                _capture.Render();
                float scaleX = pane.Screen.width/(float)(slice.SourceEnd-slice.SourceStart);
                float scaleY = _window.height/(high.y-low.y);
                pane.Map = new Rect(-(float)slice.SourceStart*scaleX,(high.y-MapBox.height)*scaleY,
                    MapBox.width*scaleX,MapBox.height*scaleY);
                _count++;
            }
        }
        catch (Exception e) { Debug.LogWarning("[ClimateWeather] 主镜头回绕已安全关闭："+e.Message); Close(); }
        finally
        {
            _effects.Hide();
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp()-captureStarted)*1000.0/System.Diagnostics.Stopwatch.Frequency;
            _captureTotalMs += ms; _captureMaxMs = Math.Max(_captureMaxMs,ms); _captureSamples++;
            if (Time.unscaledTime >= _nextMetrics)
            {
                _metrics = $"CPU提交 {_captureTotalMs/_captureSamples:F1} ms / 峰值 {_captureMaxMs:F1} · 区域重建 {_visibilityBuilds} / 复用 {_visibilityHits}";
                _nextMetrics = Time.unscaledTime+1f;
                _captureTotalMs=0; _captureMaxMs=0; _captureSamples=0;
                _visibilityBuilds=0; _visibilityHits=0;
            }
        }
    }

    private void OnGUI()
    {
        if (!Running || ClimateUiLayout.NativePopupVisible) return;
        GUI.depth = 1000; // Behind original game UI and climate controls.
        for (int i=0;i<_count;i++)
        {
            Pane pane = _panes[i];
            Rect clip = pane.Screen;
            clip.height = Mathf.Min(clip.height,Mathf.Max(0,ClimateUiLayout.Gameplay.yMax-clip.y));
            GUI.BeginGroup(clip);
            try
            {
                GUI.DrawTexture(new Rect(0,0,pane.Screen.width,pane.Screen.height),pane.Texture,ScaleMode.StretchToFill,false);
                ClimateSystem.Active?.DrawPreviewLayers(pane.Map,pane.Screen.width,pane.Screen.height);
            }
            finally { GUI.EndGroup(); }
        }
        GUI.BeginGroup(ClimateUiLayout.Gameplay);
        try
        {
            GUI.Label(new Rect(_window.x,Mathf.Max(0,_window.y-24),_window.width,24),"窗口内自由滑动 · 左右循环 · F10关闭");
            GUI.Label(new Rect(_window.x+4,_window.y+4,_window.width-8,22),_metrics);
        }
        finally { GUI.EndGroup(); }
    }

    private static void Release(Pane pane)
    {
        if (pane.Texture == null) return;
        pane.Texture.Release(); Destroy(pane.Texture); pane.Texture=null;
    }
    private void Close()
    {
        bool wasEnabled = _enabled;
        if (_maskedCamera != null) AfterCamera(_maskedCamera);
        _enabled=false; _count=0; _world=null;
        if (wasEnabled)
        {
            HorizontalBrush.EndStroke(World.world?.player_control);
            if (World.world?.player_control != null) World.world.player_control._cached_mouse_tile_pos = null;
            if (MoveCamera.instance != null)
            {
                MoveCamera.instance.clearTouches();
                MoveCamera.instance._move_velocity = Vector2.zero;
                MoveCamera.camera_drag_run = false;
            }
        }
        if (_zones!=null) _zones._last_start_x=-1;
        _zones=null;
        _visibilityCached=false;
        _previousZoneRanges.Clear(); _zoneRanges.Clear();
        _captureTotalMs=0; _captureMaxMs=0; _captureSamples=0;
        _visibilityBuilds=0; _visibilityHits=0;
        _effects.Dispose();
        if (_capture!=null) { _capture.targetTexture=null; Destroy(_capture.gameObject); _capture=null; }
        foreach (var pane in _panes) Release(pane);
        _panes.Clear();
        if (World.world?.tiles_list != null) MoveCamera.instance?.cameraToBounds();
    }
    private void OnDisable() => Close();
    private void OnDestroy()
    {
        Close(); Camera.onPreCull -= BeforeCamera; Camera.onPostRender -= AfterCamera;
        if (Active==this) Active=null;
    }
}

// Explicit invalidation also covers the original renderer or another mod clearing
// a same-size visible list; collection length alone would miss that case.
[HarmonyPatch(typeof(ZoneCamera),nameof(ZoneCamera.clear))]
internal static class HorizontalVisibilityClearPatch
{
    private static void Postfix() => HorizontalCameraWrap.Active?.InvalidateVisibility();
}

[HarmonyPatch(typeof(ZoneCamera),nameof(ZoneCamera.fullClear))]
internal static class HorizontalVisibilityFullClearPatch
{
    private static void Postfix() => HorizontalCameraWrap.Active?.InvalidateVisibility();
}

[HarmonyPatch(typeof(MoveCamera),nameof(MoveCamera.cameraToBounds))]
internal static class HorizontalCameraBoundsPatch
{
    private static bool Prefix(MoveCamera __instance)
    {
        if (HorizontalCameraWrap.Active?.Running != true) return true;
        HorizontalCameraWrap.Active.Recenter(__instance);
        World.world.nameplate_manager.update();
        return false;
    }
}

[HarmonyPatch(typeof(ZoneCamera),nameof(ZoneCamera.update))]
internal static class HorizontalCameraVisibilityPatch
{
    private static bool Prefix(ZoneCamera __instance) => HorizontalCameraWrap.Active?.Visibility(__instance) != true;
}

[HarmonyPatch(typeof(PlayerControl),nameof(PlayerControl.getMouseTilePos))]
internal static class HorizontalCameraPickPatch
{
    private static bool Prefix(ref WorldTile __result)
    {
        if (HorizontalCameraWrap.Active?.Running != true || HorizontalSeamPreview.Active?.BlocksPointer() == true) return true;
        Vector3 mouse = Input.mousePosition;
        if (!HorizontalCameraWrap.Active.ContainsPointer()) { __result=null; return false; }
        if (mouse.x<0 || mouse.x>=Screen.width || mouse.y<0 || mouse.y>=Screen.height) return true;
        Vector3 p = Camera.main.ScreenToWorldPoint(mouse);
        __result = HorizontalTopology.TryPick(p.x,p.y,MapBox.width,MapBox.height,out int x,out int y)
            ? World.world.GetTileSimple(x,y) : null;
        return false;
    }
}

[HarmonyPatch(typeof(MoveCamera),nameof(MoveCamera.updateMouseCameraDrag))]
internal static class HorizontalWindowDragPatch
{
    private static bool Prefix(MoveCamera __instance)
    {
        if (HorizontalCameraWrap.Active?.Running != true || HorizontalCameraWrap.Active.ContainsPointer()) return true;
        __instance.clearTouches();
        MoveCamera.camera_drag_run = false;
        __instance._move_velocity = Vector2.zero;
        return false;
    }
}
