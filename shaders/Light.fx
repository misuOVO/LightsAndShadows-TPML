sampler uImage0 : register(s0);
texture2D tex0;
sampler2D uColor = sampler_state
{
    Texture = <tex0>;
    MinFilter = Linear;
    MagFilter = Linear;
    AddressU = Clamp;
    AddressV = Clamp;
};
texture2D uSceneTex;
sampler2D uSceneSampler = sampler_state
{
    Texture = <uSceneTex>;
    MinFilter = Linear;
    MagFilter = Linear;
    AddressU = Clamp;
    AddressV = Clamp;
};
float2 uScreenResolution;
float2 uPos;
float t;
float intensity;
float uRadius;
float uPower;
float3 uTint;
float uAlphaOut;
float uSceneMix;
float4 PSFunction(float2 coords:TEXCOORD0): COLOR0
{
    float2 pos = uPos; //中心的位置
    float2 offset = (coords - pos);
    float2 rpos = offset * float2(uScreenResolution.x / uScreenResolution.y, 1);
    float ls = length(rpos); //取距离
    //length到3时衰减为0
    float i = saturate((uRadius - ls) / uRadius);
    i = pow(i, uPower);
    float4 col = tex2D(uColor, float2(t,0)) * float4(uTint, 1.0) * i * intensity;
    // 火把: 用场景本身的亮度(原版光照/遮挡)做遮罩, 光就不会穿墙, 也不会变成一片雾
    float3 sc = tex2D(uSceneSampler, coords).rgb;
    float lum = max(sc.r, max(sc.g, sc.b));
    float k = min(lerp(1.0, 0.10 + 1.15 * lum, uSceneMix), 1.25);
    col.rgb *= k;
    // 火把: 强制 alpha=1, 避免加色混合时 RGB 被 alpha 再乘一次导致光晕过暗
    col.a = lerp(col.a, 1.0, uAlphaOut);
    return col;
    
}
technique Technique1
{
    pass Light
    {
        PixelShader = compile ps_2_0 PSFunction();
    }
}