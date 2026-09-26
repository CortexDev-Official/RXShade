// Bloom stage 1 - bright-pass extraction, run at quarter resolution.
//
// Params0.x = threshold      (luminance where bloom starts)
// Params0.y = soft knee 0..1 (how gradually it ramps in)
//
// Downsampling to 1/4 res before blurring is what makes bloom affordable:
// the blur then costs 1/16 of the samples, and because bloom is inherently
// low-frequency nothing visible is lost.
//
// The 4-tap box downsample (rather than a single point sample) matters here -
// point sampling a bright thin feature makes it flicker as the camera moves.

float3 Prefilter(float3 c, float threshold, float softKnee)
{
    float brightness = max(c.r, max(c.g, c.b));

    // Quadratic ramp across the knee region so there is no hard cut-off line
    // where surfaces start to glow.
    float knee = threshold * softKnee + 1e-5;
    float soft = brightness - threshold + knee;
    soft = clamp(soft, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee + 1e-5);

    float contribution = max(soft, brightness - threshold) / max(brightness, 1e-5);
    return c * contribution;
}

float4 PSMain(PSInput input) : SV_Target
{
    float2 uv = input.uv;

    // InvSourceSize is the *destination* (quarter-res) texel here, so half a
    // texel in each direction is exactly the 4x4 source footprint we want.
    float2 t = InvSourceSize * 0.5;

    float3 c = SampleSrc(uv + float2(-t.x, -t.y))
             + SampleSrc(uv + float2( t.x, -t.y))
             + SampleSrc(uv + float2(-t.x,  t.y))
             + SampleSrc(uv + float2( t.x,  t.y));
    c *= 0.25;

    return float4(Prefilter(c, Params0.x, Params0.y), 1.0);
}
