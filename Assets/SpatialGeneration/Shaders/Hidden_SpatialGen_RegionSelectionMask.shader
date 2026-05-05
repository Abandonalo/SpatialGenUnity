// Inpaint mask: inside region OBB, optional linear-depth match to EncodeLinearDepth prepass, and optional
// viewport UV clip from the OBB's projected footprint (aligns wide 3D volumes with per-view screen extent).
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
            float _MaxDepth;
            float _DepthMaskEpsilon;
            float _RegionMaskUseDepthTest;
            // 0 = combined mask; 1 = prepass depth (linear 0..1); 2 = fragment linear depth; 3 = |Δ| (×50)
            float _MaskDebugView;
            float4 _MaskViewportUvMinMax;
            float _RegionMaskUseViewportClip;

            TEXTURE2D(_RegionMaskSceneDepthTex);
            SamplerState sampler_RegionMaskPointClamp
            {
                Filter = MIN_MAG_MIP_POINT;
                AddressU = Clamp;
                AddressV = Clamp;
            };

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
                float3 selectionSpace = mul(_SelectionWorldToLocal, float4(input.positionWS, 1.0)).xyz;
                float3 inside = step(abs(selectionSpace), _SelectionHalfExtents.xyz + 1e-4);

                float2 uv = input.positionHCS.xy / _ScreenParams.xy;

                float3 viewPos = TransformWorldToView(input.positionWS);
                float fragDepthLin = saturate((-viewPos.z) / max(_MaxDepth, 0.0001));

                half refDepthLin = 0.0;
                float depthDelta = 0.0;
                float isVisible = 1.0;
                if (_RegionMaskUseDepthTest > 0.5)
                {
                    refDepthLin = SAMPLE_TEXTURE2D(_RegionMaskSceneDepthTex, sampler_RegionMaskPointClamp, uv).r;
                    depthDelta = abs((float)refDepthLin - fragDepthLin);
                    isVisible = step(depthDelta, _DepthMaskEpsilon);
                }

                float insideOBB = inside.x * inside.y * inside.z;
                float value = insideOBB * isVisible;

                float inViewport = 1.0;
                if (_RegionMaskUseViewportClip > 0.5)
                {
                    inViewport = step(_MaskViewportUvMinMax.x, uv.x) * step(uv.x, _MaskViewportUvMinMax.z)
                        * step(_MaskViewportUvMinMax.y, uv.y) * step(uv.y, _MaskViewportUvMinMax.w);
                }

                value *= inViewport;

                float3 nw = input.normalWS;
                float ny = (dot(nw, nw) > 1e-6) ? normalize(nw).y : 1.0;
                float upW = saturate(_MaskFavorUpwardNormals);
                float roofGate = smoothstep(0.06, 0.42, ny);
                value *= lerp(1.0, roofGate, upW);

                float dbg = _MaskDebugView;
                if (dbg > 0.5 && dbg < 1.5)
                    return half4(refDepthLin, refDepthLin, refDepthLin, 1.0);
                if (dbg > 1.5 && dbg < 2.5)
                    return half4(fragDepthLin, fragDepthLin, fragDepthLin, 1.0);
                if (dbg > 2.5 && dbg < 3.5)
                    return half4(saturate(depthDelta * 50.0).xxx, 1.0);

                return half4(value, value, value, 1.0);
            }
            ENDHLSL
        }
    }
}
