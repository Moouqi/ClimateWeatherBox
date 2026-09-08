// 气候与四季：自转夜幕。晨昏线完全由 uniform 驱动逐像素计算，
// 建筑与岩浆灯光通过 LightMask 透光，替代旧版 CPU 夜幕纹理。
Shader "ClimateWeather/Night"
{
    Properties
    {
        _LightMask ("Light Mask (r = pass-through)", 2D) = "white" {}
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
            #pragma target 2.0
            #include "UnityCG.cginc"

            sampler2D _LightMask;

            float _Declination;   // 太阳赤纬，弧度
            float _SubsolarLon;   // 直射经度，弧度
            float _LatMin;        // 地图下边缘纬度，度
            float _LatMax;        // 地图上边缘纬度，度
            float _LonMin;        // 地图左边缘经度，度
            float _LonMax;        // 地图右边缘经度，度
            float _NightAlpha;    // 深夜最大暗度（按黑暗纪元资产校准）
            float _DayDim;        // 白昼侧大气削弱

            fixed4 frag (v2f_img i) : SV_Target
            {
                float latRad = radians(lerp(_LatMin, _LatMax, i.uv.y));
                float lonRad = radians(lerp(_LonMin, _LonMax, i.uv.x));
                float hourAngle = lonRad - _SubsolarLon;
                // 标准太阳高度角公式：极昼整日为正，极夜整日为负。
                float sinAltitude = sin(latRad) * sin(_Declination) +
                                    cos(latRad) * cos(_Declination) * cos(hourAngle);
                float darkness = smoothstep(0.0, 1.0, saturate((-sinAltitude + 0.02) / 0.28));
                float nightAlpha = darkness * _NightAlpha;
                float dayAlpha = (1.0 - darkness) * _DayDim;
                float alpha = 1.0 - (1.0 - nightAlpha) * (1.0 - dayAlpha);
                alpha *= tex2D(_LightMask, i.uv).r;
                return fixed4(4.0 / 255.0, 7.0 / 255.0, 22.0 / 255.0, alpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
