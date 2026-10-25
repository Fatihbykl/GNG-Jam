// Binanın altındaki yükseklik sisi. Yatay bir düzleme çizilir; her pikselde arkadaki sahne noktasının
// düzlemin ne kadar altında kaldığı derinlik dokusundan bulunur ve sis o derinliğe göre koyulaşır.
// Böylece düzlemin hemen altı hafif puslu, derinler tamamen sis rengi olur. Gürültü sise hafif bulut dokusu verir.
Shader "KatKat/HeightFog"
{
    Properties
    {
        _Color ("Renk", Color) = (0.8, 0.86, 0.95, 1)
        _TopY ("Üst yüzey Y", Float) = 0
        _Density ("Yoğunluk (metre başına)", Float) = 0.22
        _NoiseScale ("Gürültü ölçeği", Float) = 0.06
        _NoiseStrength ("Gürültü gücü", Range(0, 1)) = 0.35
        _NoiseSpeed ("Gürültü hızı", Float) = 0.35
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent-50"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "HeightFog"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _TopY;
                float _Density;
                float _NoiseScale;
                float _NoiseStrength;
                float _NoiseSpeed;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float4 screenPos : TEXCOORD1;
                float fogFactor : TEXCOORD2;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                VertexPositionInputs v = GetVertexPositionInputs(input.positionOS.xyz);
                o.positionCS = v.positionCS;
                o.positionWS = v.positionWS;
                o.screenPos = ComputeScreenPos(v.positionCS);
                o.fogFactor = ComputeFogFactor(v.positionCS.z);
                return o;
            }

            float Hash(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), u.x),
                            lerp(Hash(i + float2(0, 1)), Hash(i + float2(1, 1)), u.x), u.y);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.screenPos.xy / input.screenPos.w;
                float sceneEye = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
                float fragEye = -TransformWorldToView(input.positionWS).z;

                // Kameradan bu piksele giden ışını sahnedeki derinliğe kadar uzatıp arkadaki noktayı bul
                float3 toFrag = input.positionWS - _WorldSpaceCameraPos;
                float3 sceneWS = _WorldSpaceCameraPos + toFrag * (sceneEye / max(fragEye, 1e-4));
                float below = max(0.0, _TopY - sceneWS.y);

                float2 p = input.positionWS.xz * _NoiseScale + _Time.y * _NoiseSpeed * float2(1.0, 0.6);
                float n = ValueNoise(p) * 0.65 + ValueNoise(p * 2.3 + 17.0) * 0.35;
                float density = _Density * (1.0 + (n - 0.5) * 2.0 * _NoiseStrength);

                float alpha = (1.0 - exp(-density * below)) * _Color.a;
                half3 color = MixFog(_Color.rgb, input.fogFactor);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
