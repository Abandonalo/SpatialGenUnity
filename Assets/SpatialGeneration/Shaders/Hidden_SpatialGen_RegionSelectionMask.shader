// Inpaint mask: white where the **world-space position of the shaded surface** lies inside
// the region OBB. This is NOT mesh-ID masking - any geometry drawn by the camera that
// falls inside the selection box is white (including wide X/Z roof boxes that still
// contain most of the facade in world space on side views). Narrow the 3D gizmo on all
// axes to match the surface patch you want in screen space.
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
            float _MaskFavorUpwardNormals;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionHCS = TransformWorldToHClip(output.positionWS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // Custom matrix from C# is uploaded for column-vector convention used with mul(M, v).
                // (If mul(float4, matrix) matches your Unity/graphics API better, swap once and compare masks.)
                float3 selectionSpace = mul(_SelectionWorldToLocal, float4(input.positionWS, 1.0)).xyz;
                float3 inside = step(abs(selectionSpace), _SelectionHalfExtents.xyz + 1e-4);
                float value = inside.x * inside.y * inside.z;

                float3 nw = input.normalWS;
                float ny = (dot(nw, nw) > 1e-6) ? normalize(nw).y : 1.0;
                // Attenuate vertical walls / facades so wide X/Z roof boxes do not paint the whole silhouette white.
                float upW = saturate(_MaskFavorUpwardNormals);
                float roofGate = smoothstep(0.06, 0.42, ny);
                value *= lerp(1.0, roofGate, upW);

                return half4(value, value, value, 1.0);
            }
            ENDHLSL
        }
    }
}
