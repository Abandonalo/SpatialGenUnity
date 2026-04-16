Shader "Hidden/SpatialGen/RegionSelectionMask"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
        Pass
        {
            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float4x4 _SelectionWorldToLocal;
            float4 _SelectionHalfExtents;

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionHCS = TransformWorldToHClip(output.positionWS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 selectionSpace = mul(_SelectionWorldToLocal, float4(input.positionWS, 1.0)).xyz;
                float3 inside = step(abs(selectionSpace), _SelectionHalfExtents.xyz + 1e-4);
                float value = inside.x * inside.y * inside.z;
                return half4(value, value, value, 1.0);
            }
            ENDHLSL
        }
    }
}
