using UnityEngine;

namespace ClimateWeather;

public sealed partial class ClimateSystem
{
    // Caller clips this shared map projection to a preview pane. No new climate
    // textures or full-map scans are introduced for the second camera.
    internal void DrawPreviewLayers(Rect mapRect, float clipWidth = 256, float clipHeight = 256)
    {
        if (Event.current.type != EventType.Repaint || _cells.Length == 0) return;
        if (_visibleLayer == ClimateLayer.Wind && AtmosphereReady)
        {
            // Keep the same isobar interval as the primary display.
            RefreshPressureDisplay(_pressureInterval == 8 ? 600f : 800f);
            if (_pressureTexture != null) GUI.DrawTexture(mapRect, _pressureTexture, ScaleMode.StretchToFill, true);
        }
        else if (_visibleLayer != ClimateLayer.None && _layerTexture != null)
            GUI.DrawTexture(mapRect, _layerTexture, ScaleMode.StretchToFill, true);
        DrawRotatingNight(mapRect);
        if (_visibleLayer == ClimateLayer.Wind && AtmosphereReady)
        {
            AdvanceWindTrails(); // Once per frame, shared by every window/preview pane.
            // Reuse the primary trail history; rendering a second view must not
            // advance particles or simulation twice.
            for (int i = 0; i < WindParticleCount; i++)
            {
                if (_windParticleLife[i] <= 0f) continue;
                Vector2 previous = _windParticlePositions[i];
                for (int j = 0; j < 6; j++)
                {
                    Vector2 next = _windParticleHistory[i, j];
                    Vector2 a = new Vector2(mapRect.x + previous.x / MapBox.width * mapRect.width,
                        mapRect.yMax - previous.y / MapBox.height * mapRect.height);
                    Vector2 b = new Vector2(mapRect.x + next.x / MapBox.width * mapRect.width,
                        mapRect.yMax - next.y / MapBox.height * mapRect.height);
                    if (Mathf.Max(a.x,b.x) >= 0 && Mathf.Min(a.x,b.x) <= clipWidth &&
                        Mathf.Max(a.y,b.y) >= 0 && Mathf.Min(a.y,b.y) <= clipHeight)
                        DrawGuiLine(a, b, 1f, new Color(.65f,.9f,1f,(1f-j/6f)*.85f));
                    previous = next;
                }
            }
        }
    }
}
