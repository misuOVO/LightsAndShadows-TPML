// Per-layer cast-shadow darkening: scene.rgb *= (1 - mask.a * strength), alpha preserved.
sampler2D uScene : register(s0);

texture2D uMaskTex;
sampler2D uMask = sampler_state
{
    Texture = <uMaskTex>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

float uStrength;

struct PS_INPUT
{
    float2 uv : TEXCOORD0;
};

float4 PS_Darken(PS_INPUT i) : COLOR0
{
    float4 c = tex2D(uScene, i.uv);
    float m = tex2D(uMask, i.uv).a;
    c.rgb *= saturate(1.0 - m * uStrength);
    return float4(c.rgb, c.a);
}

technique Technique1
{
    pass Darken
    {
        PixelShader = compile ps_2_0 PS_Darken();
    }
}
