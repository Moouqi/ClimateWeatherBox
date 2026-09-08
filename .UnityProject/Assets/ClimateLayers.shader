// 气候与四季：统一数据图层。所有地图图层（温度、湿度、降水、云量、
// 等高线、等压线）由同一 shader 按 _LayerMode 分支，从字段纹理实时合成。
Shader "ClimateWeather/Layers"
{
    Properties
    {
        _SurfaceA ("Surface A (temp01, soil, airRH, rain)", 2D) = "black" {}
        _SurfaceB ("Surface B (cloud, elevation, frozen, sun)", 2D) = "black" {}
        _Atmos ("Atmos (pressure hPa, wind.xy, vapor)", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _SurfaceA;   // point：r 温度(0-1) g 土壤湿度 b 空气相对湿度 a 降水
            sampler2D _SurfaceB;   // point：r 云量 g 等高面(0-1) b 冻结 a 日照
            sampler2D _Atmos;      // bilinear：r 气压 hPa g/b 风 a 水汽
            float4 _SurfaceB_TexelSize;
            float4 _Atmos_TexelSize;
            int _LayerMode;        // 0 温度 1 土壤湿度 2 空气湿度 3 降水 4 云量 5 等高 6 气压

            fixed4 TemperatureRamp(float value)
            {
                fixed3 cold = fixed3(0.05f, 0.35f, 1.00f);
                fixed3 mild = fixed3(0.20f, 1.00f, 0.25f);
                fixed3 hot = fixed3(1.00f, 0.12f, 0.02f);
                fixed3 rgb = value < 0.5f
                    ? lerp(cold, mild, value * 2.0f)
                    : lerp(mild, hot, (value - 0.5f) * 2.0f);
                float a = value < 0.5f ? lerp(0.58f, 0.50f, value * 2.0f)
                                       : lerp(0.50f, 0.62f, (value - 0.5f) * 2.0f);
                return fixed4(rgb, a);
            }

            fixed4 HumidityRamp(float value)
            {
                return fixed4(lerp(fixed3(0.95f, 0.65f, 0.12f), fixed3(0.02f, 0.38f, 1.00f), value),
                              lerp(0.52f, 0.65f, value));
            }

            fixed4 ElevationLayer(float2 uv)
            {
                float elevation = tex2D(_SurfaceB, uv).g;
                fixed4 deep = fixed4(0.03f, 0.16f, 0.42f, 0.58f);
                fixed4 coast = fixed4(0.12f, 0.58f, 0.72f, 0.52f);
                fixed4 lowland = fixed4(0.28f, 0.70f, 0.28f, 0.50f);
                fixed4 upland = fixed4(0.72f, 0.61f, 0.25f, 0.54f);
                fixed4 peak = fixed4(0.94f, 0.94f, 0.91f, 0.62f);
                fixed4 fill = elevation < 0.16f ? lerp(deep, coast, elevation / 0.16f)
                    : elevation < 0.45f ? lerp(coast, lowland, (elevation - 0.16f) / 0.29f)
                    : elevation < 0.78f ? lerp(lowland, upland, (elevation - 0.45f) / 0.33f)
                    : lerp(upland, peak, (elevation - 0.78f) / 0.22f);

                const int bands = 12;
                int band = (int)clamp(floor(elevation * bands), 0, bands - 1);
                float2 tx = _SurfaceB_TexelSize.xy;
                int left = (int)clamp(floor(tex2D(_SurfaceB, uv - float2(tx.x, 0.0f)).g * bands), 0, bands - 1);
                int down = (int)clamp(floor(tex2D(_SurfaceB, uv - float2(0.0f, tx.y)).g * bands), 0, bands - 1);
                if (left != band || down != band)
                    fill = lerp(fill, fixed4(0.10f, 0.08f, 0.05f, 0.88f), 0.72f);
                return fill;
            }

            fixed4 PressureLayer(float2 uv)
            {
                float2 tx = _Atmos_TexelSize.xy;
                float center = tex2D(_Atmos, uv).r;
                float n = tex2D(_Atmos, uv + float2(0.0f, tx.y)).r;
                float s = tex2D(_Atmos, uv - float2(0.0f, tx.y)).r;
                float e = tex2D(_Atmos, uv + float2(tx.x, 0.0f)).r;
                float w = tex2D(_Atmos, uv - float2(tx.x, 0.0f)).r;
                // 一轮五点平滑替代旧版 CPU 三轮盒式模糊的展示平滑。
                float p = (center * 4.0f + n + s + e + w) / 8.0f;

                // 等压线密度门控使用格距梯度（与缩放无关），线宽用屏幕梯度。
                float gradient = length(float2(e - w, n - s)) * 0.5f;
                float gate = smoothstep(0.03f, 0.20f, gradient);
                const float interval = 4.0f;
                float bands = (p - 960.0f) / interval;
                float distance = abs(frac(bands + 0.5f) - 0.5f) * interval;
                float grad = max(fwidth(p), 1e-4);
                float isoline = (1.0f - smoothstep(0.25f, 1.1f, distance / grad)) * gate;

                float normalized = saturate((p - 980.0f) / 60.0f);
                fixed4 lowC = fixed4(0.08f, 0.30f, 0.92f, 0.36f);
                fixed4 neutral = fixed4(0.72f, 0.82f, 0.76f, 0.20f);
                fixed4 highC = fixed4(0.94f, 0.18f, 0.10f, 0.38f);
                fixed4 fill = normalized < 0.5f ? lerp(lowC, neutral, normalized * 2.0f)
                    : lerp(neutral, highC, (normalized - 0.5f) * 2.0f);
                return lerp(fill, fixed4(0.10f, 0.16f, 0.23f, 0.46f), isoline);
            }

            fixed4 frag (v2f_img i) : SV_Target
            {
                float2 uv = i.uv;
                if (_LayerMode == 0) return TemperatureRamp(tex2D(_SurfaceA, uv).r);
                if (_LayerMode == 1) return HumidityRamp(tex2D(_SurfaceA, uv).g);
                if (_LayerMode == 2) return HumidityRamp(tex2D(_SurfaceA, uv).b);
                if (_LayerMode == 3) return HumidityRamp(saturate(tex2D(_SurfaceA, uv).a * 1000.0f));
                if (_LayerMode == 4)
                {
                    float cover = saturate(tex2D(_SurfaceB, uv).r);
                    float visible = smoothstep(0.06f, 0.92f, cover);
                    fixed4 color = fixed4(lerp(fixed3(0.58f, 0.70f, 0.78f), fixed3(0.92f, 0.96f, 1.00f), visible),
                                          cover < 0.08f ? 0.0f : lerp(0.05f, 0.44f, visible));
                    return color;
                }
                if (_LayerMode == 5) return ElevationLayer(uv);
                return PressureLayer(uv);
            }
            ENDCG
        }
    }
    Fallback Off
}
