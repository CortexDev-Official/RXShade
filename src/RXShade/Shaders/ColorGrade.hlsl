// Colour grading: exposure -> temperature/tint -> contrast -> saturation -> vignette.
//
// Params0 = (saturation, contrast, temperature, tint)
// Params1 = (vignetteIntensity, vignetteRadius, vignetteSoftness, exposure)
//
// Order matters. Exposure and white balance are "camera" operations and belong
// first, while they still act on something resembling scene light. Contrast and
// saturation are "look" operations applied to the graded image. Vignette is
// last because it is a lens artefact - it should darken the final picture, not
// feed back into the contrast curve.

// Stylistic temperature control, NOT a colorimetric white-balance transform.
// A true white balance needs the source white point, which a screen capture
// does not carry. Positive temp = warmer, negative = cooler.
//
// Applied in LINEAR light. A channel gain applied to display-encoded values
// means different things at different points on the gamma curve, so the same
// "warm" setting skews hue in shadows differently from highlights. Doing it in
// linear keeps the shift consistent across the whole tonal range.
float3 ApplyTemperature(float3 c, float temp, float tint)
{
    if (abs(temp) < 0.001 && abs(tint) < 0.001)
        return c;

    float3 linearColor = pow(max(c, 0.0), 2.2);

    float3 warm  = float3(1.0 + 0.32 * temp, 1.0 + 0.02 * temp, 1.0 - 0.28 * temp);
    float3 green = float3(1.0 - 0.07 * tint, 1.0 + 0.14 * tint, 1.0 - 0.07 * tint);
    linearColor *= warm * green;

    return pow(max(linearColor, 0.0), 1.0 / 2.2);
}

float4 PSMain(PSInput input) : SV_Target
{
    float2 uv = input.uv;
    float3 c = SampleSrc(uv);

    float saturation = Params0.x;
    float contrast   = Params0.y;
    float temp       = Params0.z;
    float tint       = Params0.w;

    float vigIntensity = Params1.x;
    float vigRadius    = Params1.y;
    float vigSoftness  = Params1.z;
    float exposure     = Params1.w;

    c *= exposure;
    c = ApplyTemperature(c, temp, tint);

    // Pivot around mid-grey so contrast brightens highlights and deepens
    // shadows symmetrically instead of just making the image brighter.
    c = (c - 0.5) * contrast + 0.5;

    float luma = Luma(max(c, 0.0));
    c = lerp(luma.xxx, c, saturation);

    if (vigIntensity > 0.0001)
    {
        // Aspect-correct the distance so the vignette stays circular on
        // ultrawide rather than stretching into an ellipse.
        float2 d = uv - 0.5;
        d.x *= Aspect;
        float dist = length(d) * 1.41421356;

        float v = smoothstep(vigRadius, max(vigRadius - vigSoftness, 1e-4), dist);
        c *= lerp(1.0, v, vigIntensity);
    }

    return float4(max(c, 0.0), 1.0);
}
