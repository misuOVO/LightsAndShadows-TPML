// Multiply a texture's RGB by another texture's alpha (used to stop the sun/torch
// light from being applied over the background layer when 光照穿过背景 = 关).
sampler2D uScene : register(s0);

texture2D uMaskTex;
sampler2D uMask = sampler_state
{
    Texture = <uMaskTex>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = None;
    AddressU = Clamp; AddressV = Clamp;
};

struct PS_INPUT
{
    float2 uv : TEXCOORD0;
};

float4 PS_Mask(PS_INPUT i) : COLOR0
{
    float4 c = tex2D(uScene, i.uv);
    float m = tex2D(uMask, i.uv).a;
    return float4(c.rgb * m, c.a);
}

technique Technique1
{
    pass Mask
    {
        PixelShader = compile ps_2_0 PS_Mask();
    }
}
