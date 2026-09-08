using UnityEngine;

namespace ClimateWeather;

internal static class ClimateUiLayout
{
    private static readonly Vector3[] Corners = new Vector3[4];
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
            float bottom = Mathf.Clamp(Screen.height*.14f,110,200);
            var background=ToolbarButtons.instance?.main_background;
            if (background != null && background.gameObject.activeInHierarchy)
            {
                bottom=TopOf(background);
            }
            // Category tabs protrude above main_background. Measure actual
            // buttons, not their layout container (which may span the screen).
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
            return new Rect(0,0,Screen.width,Mathf.Max(0,Screen.height-bottom-4));
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
