// 荷叶表面：双面（叶背颜色不同）、蜡质高光、逆光透光、叶缘渐隐。
// 自发光由 MaterialPropertyBlock 在运行时驱动（叶心积水和露珠的高光），所以这里只声明不初始化。
Shader "DigitalPerformance/LotusLeaf"
{
    Properties
    {
        _MainTex ("叶片颜色", 2D) = "white" {}
        _BumpMap ("叶脉法线", 2D) = "bump" {}
        _Color ("整体叠加色", Color) = (1,1,1,1)
        _BackColor ("叶背色", Color) = (0.42, 0.52, 0.34, 1)
        _RimFade ("叶缘渐隐", Range(0.01, 0.6)) = 0.08
        _Gloss ("蜡质高光强度", Range(0, 1)) = 0.16
        _SpecPower ("高光锐度", Range(1, 160)) = 26
        _TransColor ("透光色", Color) = (0.62, 0.90, 0.40, 1)
        _TransPower ("透光锐度", Range(0.5, 8)) = 2.4
        _EmissionColor ("自发光（运行时驱动）", Color) = (0, 0, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" }
        LOD 300
        Cull Off

        CGPROGRAM
        #pragma surface surf Lotus alpha:fade addshadow fullforwardshadows
        #pragma target 3.0

        sampler2D _MainTex;
        sampler2D _BumpMap;
        fixed4 _Color;
        fixed4 _BackColor;
        fixed4 _TransColor;
        fixed4 _EmissionColor;
        half _RimFade;
        half _Gloss;
        half _SpecPower;
        half _TransPower;

        struct Input
        {
            float2 uv_MainTex;
            float2 uv_BumpMap;
            float3 viewDir;
            fixed facing : VFACE;
        };

        void surf(Input IN, inout SurfaceOutput o)
        {
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            o.Normal = UnpackNormal(tex2D(_BumpMap, IN.uv_BumpMap));
            o.Albedo = c.rgb;
            o.Emission = _EmissionColor.rgb;

            // 叶背：颜色偏暗偏黄，法线翻转，这样从下面看才是对的
            if (IN.facing < 0)
            {
                o.Albedo = lerp(o.Albedo, _BackColor.rgb * (c.rgb * 0.6 + 0.4), 0.55);
                o.Normal = -o.Normal;
            }

            // 叶缘渐隐，让叶子看起来是薄的
            float r = length(IN.uv_MainTex - 0.5) * 2.0;
            o.Alpha = c.a * (1.0 - smoothstep(1.0 - _RimFade, 1.0, r));
        }

        half4 LightingLotus(SurfaceOutput s, half3 lightDir, half3 viewDir, half atten)
        {
            half3 n = normalize(s.Normal);
            half ndl = dot(n, lightDir);

            // 保留一点补光余量，但别用纯半兰伯特——那会把明暗拉得太软、发闷
            half3 diff = s.Albedo * _LightColor0.rgb * saturate(ndl * 0.8 + 0.2) * atten;

            // 透光：光在背面、视线朝着光源时，叶片透出浅绿
            half back = pow(saturate(dot(viewDir, -lightDir)), _TransPower) * saturate(-ndl) * 0.35;
            half3 trans = _TransColor.rgb * s.Albedo * back * _LightColor0.rgb * atten;

            // 蜡质高光
            half3 h = normalize(lightDir + viewDir);
            half spec = pow(saturate(dot(n, h)), _SpecPower) * _Gloss;

            return half4(diff + trans + _LightColor0.rgb * spec * atten, s.Alpha);
        }
        ENDCG
    }

    FallBack "Diffuse"
}
