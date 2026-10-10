Shader "Hidden/MediaCapture/MetalVideoColor"
{
    Properties { _MainTex ("Source", 2D) = "white" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _EncodeSRGB;
            float4 frag(v2f_img input) : SV_Target
            {
                float3 rgb = tex2D(_MainTex, input.uv).rgb;
                if (_EncodeSRGB > 0.5) rgb = LinearToGammaSpace(rgb);
                return float4(rgb, 1);
            }
            ENDCG
        }
    }
}
