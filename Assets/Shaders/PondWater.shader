// 池塘水面：几道方向不同的正弦叠成涟漪，扰动法线，让水面不是一块静止的板。
Shader "DigitalPerformance/PondWater"
{
    Properties
    {
        _Deep ("深处颜色", Color) = (0.02, 0.05, 0.07, 1)
        _Shallow ("浅处颜色", Color) = (0.07, 0.16, 0.17, 1)
        _SpecColor ("高光色", Color) = (0.55, 0.7, 0.7, 1)
        _Gloss ("水面光泽", Range(0, 1)) = 0.85
        _WaveScale ("涟漪尺度", Range(0.2, 20)) = 3.5
        _WaveSpeed ("涟漪速度", Range(0, 2)) = 0.22
        _WaveStrength ("涟漪强度", Range(0, 1)) = 0.18
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        LOD 200

        CGPROGRAM
        #pragma surface surf BlinnPhong fullforwardshadows
        #pragma target 3.0

        fixed4 _Deep;
        fixed4 _Shallow;
        half _Gloss;
        half _WaveScale;
        half _WaveSpeed;
        half _WaveStrength;

        struct Input
        {
            float3 worldPos;
        };

        void surf(Input IN, inout SurfaceOutput o)
        {
            float2 p = IN.worldPos.xz * _WaveScale;
            float t = _Time.y * _WaveSpeed;

            float a = p.x * 1.00 + t * 1.7;
            float b = p.y * 1.30 - t * 1.3;
            float c = (p.x + p.y) * 0.70 + t * 0.9;

            float wave = sin(a) * 0.5 + sin(b) * 0.4 + sin(c) * 0.3;
            float dx = cos(a) + cos(c) * 0.21;
            float dy = cos(b) * 0.52 + cos(c) * 0.21;

            o.Normal = normalize(float3(-dx * _WaveStrength, -dy * _WaveStrength, 1));
            o.Albedo = lerp(_Deep.rgb, _Shallow.rgb, saturate(wave * 0.3 + 0.5));
            o.Gloss = _Gloss;
            o.Specular = 0.5;
        }
        ENDCG
    }

    FallBack "Diffuse"
}
