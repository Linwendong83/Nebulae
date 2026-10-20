Shader "Nebulae/PlayerNameTag"
{
    Properties
    {
        _MainTex ("Font Atlas", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _OccludedAlpha ("Occluded Alpha", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags { "Queue"="Transparent+50" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        // A reversed depth buffer does not invert ZTest keywords. LEqual still
        // draws the tag in front of opaque geometry, and Greater draws the part
        // hidden behind it. Only that occluded part is faint. It ignores the
        // distance fade carried in the vertex alpha so x-ray visibility works
        // at any range.
        Pass
        {
            ZTest Greater
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragOccluded
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _Color;
            fixed _OccludedAlpha;

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata input)
            {
                v2f output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.color = input.color * _Color;
                output.uv = input.uv;
                return output;
            }

            fixed4 fragOccluded(v2f input) : SV_Target
            {
                // The font atlas stores the glyph mask in its alpha channel; its RGB
                // is not usable as a color, so the tint comes from the vertex color.
                fixed coverage = tex2D(_MainTex, input.uv).a;
                return fixed4(input.color.rgb, coverage * _OccludedAlpha);
            }
            ENDCG
        }

        // Visible pixels use the distance fade carried in the vertex alpha.
        Pass
        {
            ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragVisible
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _Color;

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata input)
            {
                v2f output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.color = input.color * _Color;
                output.uv = input.uv;
                return output;
            }

            fixed4 fragVisible(v2f input) : SV_Target
            {
                // See fragOccluded: the atlas alpha is the glyph coverage mask.
                fixed coverage = tex2D(_MainTex, input.uv).a;
                return fixed4(input.color.rgb, coverage * input.color.a);
            }
            ENDCG
        }
    }
}
