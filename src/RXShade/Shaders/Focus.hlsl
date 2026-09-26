// Cinematic focus - a radial "fake depth of field".
//
// Params0 = (strength, focusRadius, 0, 0)
//
// HONEST ABOUT WHAT THIS IS: real depth of field blurs by distance from the
// camera, which needs the depth buffer. We do not have one (see
// FakeReflection.hlsl for the full explanation). This blurs by distance from
// the CENTRE OF THE SCREEN instead.
//
// It happens to work well in practice for a third-person game, because the
// character sits near the middle of the frame and the scenery that ends up
// blurred is usually genuinely further away. It will blur a nearby wall at the
// edge of frame that a real DOF pass would have left sharp.

static const float2 kPoisson[12] =
{
    float2(-0.326, -0.406), float2(-0.840, -0.074), float2(-0.696,  0.457),
    float2(-0.203,  0.621), float2( 0.962, -0.195), float2( 0.473, -0.480),
    float2( 0.519,  0.767), float2( 0.185, -0.893), float2( 0.507,  0.064),
    float2( 0.896,  0.412), float2(-0.322, -0.933), float2(-0.792, -0.598)
};

float4 PSMain(PSInput input) : SV_Target
{
    float2 uv = input.uv;
    float3 sharp = SampleSrc(uv);

    float strength = saturate(Params0.x);
    if (strength <= 0.0001)
        return float4(sharp, 1.0);

    // Aspect-correct so the in-focus region is a circle, not an ellipse.
    float2 d = uv - 0.5;
    d.x *= Aspect;
    float dist = saturate(length(d) * 1.41421356);

    float focusRadius = Params0.y;
    float defocus = saturate((dist - focusRadius) / max(1.0 - focusRadius, 1e-3));

    // Squared ramp: keeps a generous sharp centre and pushes the transition
    // outwards, which avoids the "vignette made of blur" look.
    defocus *= defocus;
    if (defocus <= 0.002)
        return float4(sharp, 1.0);

    float radius = defocus * strength * 0.014;
    float2 radiusUv = float2(radius / max(Aspect, 1e-4), radius);

    float3 blurred = sharp * 0.24;
    [unroll]
    for (int i = 0; i < 12; i++)
        blurred += SampleSrc(uv + kPoisson[i] * radiusUv) * (0.76 / 12.0);

    return float4(lerp(sharp, blurred, defocus), 1.0);
}
