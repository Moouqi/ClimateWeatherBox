using UnityEngine;

namespace ClimateWeather;

public sealed partial class ClimateSystem
{
    private const int WindParticleCount = 240;
    private readonly Vector2[] _windParticlePositions = new Vector2[WindParticleCount];
    private readonly Vector2[,] _windParticleHistory = new Vector2[WindParticleCount, 6];
    private readonly float[] _windParticleLife = new float[WindParticleCount];
    private float _windParticleTime;

    private void DrawWindTrails(Camera camera, Rect rect)
    {
        if (!AtmosphereReady || Event.current.type != EventType.Repaint) return;
        float dt = Mathf.Clamp(_seasonClock - _windParticleTime, 0f, 0.10f);
        _windParticleTime = _seasonClock;
        for (int i = 0; i < WindParticleCount; i++)
        {
            Vector2 p = _windParticlePositions[i];
            if (_windParticleLife[i] <= 0f || p.x < 0f || p.y < 0f ||
                p.x >= MapBox.width || p.y >= MapBox.height)
            {
                p = new Vector2(UnityEngine.Random.value * MapBox.width,
                    UnityEngine.Random.value * MapBox.height);
                _windParticleLife[i] = 2f + UnityEngine.Random.value * 5f;
                for (int j = 0; j < 6; j++) _windParticleHistory[i, j] = p;
            }
            Vector2 v = SampleAir(p.x, p.y).Velocity;
            if (dt > 0f)
            {
                for (int j = 5; j > 0; j--) _windParticleHistory[i, j] = _windParticleHistory[i, j - 1];
                _windParticleHistory[i, 0] = p;
                Vector2 midpoint = p + v * dt * 4f;
                p += SampleAir(midpoint.x, midpoint.y).Velocity * dt * 8f;
                _windParticleLife[i] -= dt * (v.sqrMagnitude < 0.001f ? 4f : 1f);
            }
            _windParticlePositions[i] = p;
            Vector2 previous = p;
            for (int j = 0; j < 6; j++)
            {
                Vector2 next = _windParticleHistory[i, j];
                Vector3 a = camera.WorldToScreenPoint(new Vector3(previous.x, previous.y, 0f));
                Vector3 b = camera.WorldToScreenPoint(new Vector3(next.x, next.y, 0f));
                Vector2 sa = new Vector2(a.x, Screen.height - a.y), sb = new Vector2(b.x, Screen.height - b.y);
                if (rect.Contains(sa) && rect.Contains(sb))
                {
                    Color color = Color.Lerp(new Color(0.4f, 0.8f, 1f), new Color(0.9f, 1f, 0.65f),
                        Mathf.Clamp01(v.magnitude));
                    color.a = (1f - j / 6f) * 0.85f;
                    DrawGuiLine(sa, sb, 1.5f, color);
                }
                previous = next;
            }
        }
    }
}
