sampler uImage0 : register(s0);

texture2D tex0; //Pass1使用 传入做好的阴影
sampler2D uImage1 = sampler_state 
{
    Texture = <tex0>;
    MinFilter = Linear;
    MagFilter = Linear;
    AddressU = Clamp; 
    AddressV = Clamp;
};

texture2D tex1; //世界层遮罩: alpha=1 表示地形/物体, 0 表示天空/云朵
sampler2D uWorldMask = sampler_state
{
    Texture = <tex1>;
    MinFilter = Linear;
    MagFilter = Linear;
    AddressU = Clamp;
    AddressV = Clamp;
};

float2 uScreenResolution;
float m;
float uIntensity;
float2 uPos;
float uCloudDiff;
float uCloudMul;

float colorDiff(float4 c1, float4 c2)
{
    float3 c = c1.rgb - c2.rgb;
    return sqrt(dot(c, c));
}

float4 PixelShaderFunction(float2 coords : TEXCOORD0) : COLOR0//提取蓝色通道小于阈值m的部分
{
    float4 c = tex2D(uImage0,coords);
    float4 finalColor = float4(0, 0, 0, 0);
    // 天空/云朵 (世界层 alpha = 0) 当成高蓝通道, 不产生阴影
    float wm = tex2D(uWorldMask, coords).a;
    float b = lerp(1.0, c.b, wm);
    //float2 offset = (coords - uPos) * float2(uScreenResolution.x / uScreenResolution.y, 1);
    //if (c.b < m)
    float a1 = pow(1 - b,0.75);
    float a2 = 1 - smoothstep(0.1,1,b);
    float a = lerp(a1,a2,1-b);

    finalColor = float4(1, 1, 1, a);
    return finalColor;
}

float4 UseShadow(float2 coords : TEXCOORD0) : COLOR0 //做减法
{
    float4 color = tex2D(uImage0,coords);
    color.rgb -= tex2D(uImage1, coords).rgb;
    return color;
}

technique Technique1
{
	pass GetShadow
	{
		PixelShader = compile ps_2_0 PixelShaderFunction();
	}
    pass UseShadow
    {
        PixelShader = compile ps_2_0 UseShadow();
    }
}