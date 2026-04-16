Shader "Hidden/SpatialGen/RegionSelectionOverlay"
{
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            float4x4 _SelectionWorldToLocal;
            float4 _SelectionHalfExtents;
            float4 _OverlayColor;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            v2f vert(appdata input)
            {
                v2f output;
                output.positionWS = mul(unity_ObjectToWorld, input.vertex).xyz;
                output.vertex = UnityObjectToClipPos(input.vertex);
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                float3 selectionSpace = mul(_SelectionWorldToLocal, float4(input.positionWS, 1.0)).xyz;
                float3 inside = step(abs(selectionSpace), _SelectionHalfExtents.xyz + 1e-4);
                clip(inside.x * inside.y * inside.z - 0.5);
                return _OverlayColor;
            }
            ENDHLSL
        }
    }
}
