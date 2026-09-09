using UnityEngine;

namespace ClimateWeather;

internal static class ClimateUiLayout
{
    private static readonly Vector3[] Corners = new Vector3[4];
    private static int _layoutFrame = -1;
    private static int _layoutWidth, _layoutHeight;
    private static Rect _gameplay;
    private static float TopOf(Component component)
    {
        if (component == null || !component.gameObject.activeInHierarchy || !(component.transform is RectTransform rect)) return 0;
        var canvas=component.GetComponentInParent<Canvas>();
        Camera camera=canvas == null || canvas.renderMode==RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        rect.GetWorldCorners(Corners);
        float top=0;
        foreach (var corner in Corners) top=Mathf.Max(top,RectTransformUtility.WorldToScreenPoint(camera,corner).y);
        return top;
    }
    internal static bool NativePopupVisible
    {
        get
        {
            // 仅在原版窗口打开时隐藏覆盖层。悬停 UI 时的原版提示框由
            // Screen Space Overlay 画布绘制，永远在世界空间图层之上，
            // 不需要为此隐藏夜幕、图层或面板。
            return ScrollWindow.isWindowActive() || ScrollWindow.isAnimationActive() ||
                   ScrollWindow._is_any_window_active;
        }
    }
    internal static Rect Gameplay
    {
        get
        {
            // 输入、拼接相机和 IMGUI 会在同一帧多次查询，避免重复遍历所有按钮。
            // 分辨率变化立即重算，其余布局变化最迟下一帧反映，仍跟随工具栏动画。
            int frame = Time.frameCount;
            int width = Screen.width, height = Screen.height;
            if (_layoutFrame == frame && _layoutWidth == width && _layoutHeight == height)
                return _gameplay;
            float bottom = Mathf.Clamp(Screen.height*.14f,110,200);
            var background=ToolbarButtons.instance?.main_background;
            if (background != null && background.gameObject.activeInHierarchy)
            {
                bottom=TopOf(background);
            }
            // 分类按钮会突出背景上沿，必须测量实际按钮，不能用可能铺满屏幕的容器。
            var controller=PowerTabController.instance;
            if (controller != null)
            {
                bottom=Mathf.Max(bottom,TopOf(controller.t_main));
                if (controller._buttons != null)
                    foreach (var button in controller._buttons) bottom=Mathf.Max(bottom,TopOf(button));
                bottom=Mathf.Max(bottom,TopOf(controller.arrowLeft),TopOf(controller.arrowRight));
            }
            bottom=Mathf.Max(bottom,TopOf(PowersTab._current_tab_button));
            var tab=PowersTab._current_tab;
            if (tab != null && tab.gameObject.activeInHierarchy && tab._power_buttons != null)
                foreach (var button in tab._power_buttons) bottom=Mathf.Max(bottom,TopOf(button));
            _gameplay = new Rect(0,0,width,Mathf.Max(0,height-bottom-4));
            _layoutFrame = frame;
            _layoutWidth = width;
            _layoutHeight = height;
            return _gameplay;
        }
    }
    internal static float PanelScale => Mathf.Min(1f,Mathf.Min(Gameplay.width/301f,Gameplay.height/638f));
    internal static bool PointerInPanel
    {
        get
        {
            float scale=PanelScale;
            Vector3 p=Input.mousePosition;
            return !NativePopupVisible && new Rect(8*scale,80*scale,285*scale,550*scale)
                .Contains(new Vector2(p.x,Screen.height-p.y));
        }
    }
}
