Shader "TiltedWorlds/Magnetosphere"
{
    Properties
    {
        _OpacityTexture ("Vanilla opacity", 2D) = "white" {}
        [HDR] _ColorMultiply ("Vanilla tint", Color) = (1,1,1,1)
        _AnimationSpeed ("Animation", Vector) = (0.1,0,0,0)
        _OpacityTiling ("Opacity tiling", Float) = 0.002
        _OpacityPower ("Opacity power", Float) = 0.3
        _TailRotation ("Tail quaternion", Vector) = (0,0,0,1)
        _LengthScale ("World length scale", Float) = 1
        _WidthMultiplier ("World width multiplier", Float) = 1
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "DisableBatching"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off
        ZTest LEqual
        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _OpacityTexture;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            float4 _OpacityTexture_ST;
            float4 _ColorMultiply, _AnimationSpeed, _TailRotation;
            float _OpacityTiling, _OpacityPower, _LengthScale, _WidthMultiplier;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                // Arc coordinate, ribbon side, radial blend weight, half width (world units).
                float4 data : TEXCOORD0;
                float4 previous : TEXCOORD1;
                float4 next : TEXCOORD2;
            };
            struct v2f
            {
                float4 vertex : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float4 screenPos : TEXCOORD1;
            };

            float3 deform(float3 p, float weight)
            {
                float4 q = lerp(float4(0,0,0,1), _TailRotation, weight);
                q *= rsqrt(max(dot(q,q), 1e-12));
                return p + 2 * cross(q.xyz, cross(q.xyz, p) + q.w * p);
            }

            v2f vert(appdata v)
            {
                v2f o;
                float3 p = mul(unity_ObjectToWorld, float4(deform(v.vertex.xyz, v.data.z), 1)).xyz;
                float3 previous = mul(unity_ObjectToWorld, float4(deform(v.previous.xyz, v.previous.w), 1)).xyz;
                float3 next = mul(unity_ObjectToWorld, float4(deform(v.next.xyz, v.next.w), 1)).xyz;
                float3 tangent = next - previous;
                float3 eye = _WorldSpaceCameraPos.xyz - p;
                if (unity_OrthoParams.w > 0.5) eye = UNITY_MATRIX_V[2].xyz;
                float3 side = cross(tangent, eye);
                if (dot(side, side) < 1e-12) side = cross(tangent, UNITY_MATRIX_V[1].xyz);
                if (dot(side, side) < 1e-12) side = UNITY_MATRIX_V[0].xyz;
                side *= rsqrt(max(dot(side, side), 1e-12));
                p += side * v.data.y * v.data.w * _WidthMultiplier;
                o.vertex = mul(UNITY_MATRIX_VP, float4(p,1));
                o.screenPos = ComputeScreenPos(o.vertex);
                o.color = v.color * _ColorMultiply;
                float2 uv = float2(v.data.x * _LengthScale, 0.5 - v.data.y * 0.5);
                o.uv = uv * _OpacityTiling + _Time.y * _AnimationSpeed.xy;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float opacity = pow(max(tex2D(_OpacityTexture, i.uv).r, 1e-6), max(_OpacityPower, 1e-4));
                // Match the vanilla MagneticFieldLine pixel program's depth intersection fade.
                float sceneDepth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture,
                    UNITY_PROJ_COORD(i.screenPos)));
                float ownDepth = LinearEyeDepth(i.screenPos.z / i.screenPos.w);
                float depthFade = saturate(abs(sceneDepth - ownDepth) * 0.5);
                return float4(i.color.rgb, saturate(i.color.a * opacity * depthFade));
            }
            ENDCG
        }
    }
    // Auto must see unsupported hardware rather than silently select an undeformed fallback shader.
    Fallback Off
}
