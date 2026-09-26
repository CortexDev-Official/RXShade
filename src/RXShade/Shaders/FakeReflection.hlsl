// ===========================================================================
// "Fake Reflection" - stylised glossy/wet surfaces. NOT screen-space reflection.
//
// WHY THIS IS NOT SSR, AND CANNOT BE:
// Real SSR ray-marches the depth buffer using per-pixel surface positions and
// normals. All of that lives inside the rendering engine. A screen capture
// receives one thing: the final colour image. There is no depth, no normals,
// no way to derive them. So this reproduces what reflective surfaces LOOK
// like rather than computing where reflected rays actually go.
//
// TWO COMPONENTS, because real reflective rooms show two different things:
//
//  A) SURFACE SHEEN  - screen-wide. Every glossy surface stretches nearby
//     light sources into vertical streaks. This is the dominant visual cue in
//     a polished corridor: lamps smear down the floor, along the walls, and
//     across the ceiling. It needs no horizon and no geometry, only a
//     directional gather of the bright parts of the image, so it works
//     everywhere on screen - walls and ceiling included.
//
//  B) FLOOR MIRROR   - below the horizon only. A proper mirrored copy of the
//     scene for the floor plane, where reflections carry real shape rather
//     than just smeared light.
//
// Sheen is what makes walls and ceilings read as wet. Mirror is what makes the
// floor read as a mirror. Both are needed; neither alone is convincing.
//
// Params0 = (horizon, mirrorOpacity, blurAmount, waveAmplitude)
// Params1 = (waveFrequency, waveSpeed, fresnelFalloff, gloss)
// Params2 = (sheenStrength, streakLength, brightThreshold, upBias)
// ===========================================================================

static const float2 kPoisson[12] =
{
    float2(-0.326, -0.406), float2(-0.840, -0.074), float2(-0.696,  0.457),
    float2(-0.203,  0.621), float2( 0.962, -0.195), float2( 0.473, -0.480),
    float2( 0.519,  0.767), float2( 0.185, -0.893), float2( 0.507,  0.064),
    float2( 0.896,  0.412), float2(-0.322, -0.933), float2(-0.792, -0.598)
};

static const float2 kRing8[8] =
{
    float2( 1.0,  0.0), float2(-1.0,  0.0), float2( 0.0,  1.0), float2( 0.0, -1.0),
    float2( 0.7,  0.7), float2(-0.7,  0.7), float2( 0.7, -0.7), float2(-0.7, -0.7)
};

// A wet floor stretches a lamp a long way towards the viewer, so the gather
// has to be both long and finely stepped - too few taps over a long distance
// and the streak breaks into visible bands.
#define STREAK_TAPS 18

// Isolates the light sources. Squared so a lamp dominates and mid-tone walls
// contribute almost nothing - otherwise the sheen turns into a grey haze.
float3 BrightPass(float2 uv, float threshold)
{
    float3 c = SampleSrc(uv);
    float weight = saturate((Luma(c) - threshold) / max(1.0 - threshold, 1e-4));
    return c * weight * weight;
}

// Smears the bright content along the vertical axis. A glossy surface stretches
// a light source into a streak pointing towards the viewer, which is exactly
// what the reference look is built from.
float3 GatherSheen(float2 uv, float streakLength, float threshold, float upBias)
{
    float3 accumulated = 0.0;
    float total = 1e-4;

    [unroll]
    for (int i = 1; i <= STREAK_TAPS; i++)
    {
        float t = (float)i / STREAK_TAPS;

        // Near-linear falloff. A steeper curve concentrates all the energy
        // within a few pixels of the light and the streak never actually
        // reaches down the floor, which was the problem with the first version.
        float weight = pow(1.0 - t, 1.15);
        float offset = t * streakLength;

        accumulated += BrightPass(float2(uv.x, uv.y - offset), threshold) * weight * upBias;
        accumulated += BrightPass(float2(uv.x, uv.y + offset), threshold) * weight * (1.0 - upBias);
        total += weight;
    }

    return accumulated / total;
}

float4 PSMain(PSInput input) : SV_Target
{
    float2 uv = input.uv;
    float3 base = SampleSrc(uv);
    float3 result = base;

    float horizon       = Params0.x;
    float mirrorOpacity = Params0.y;
    float blurAmount    = Params0.z;
    float waveAmp       = Params0.w;

    float waveFreq    = Params1.x;
    float waveSpeed   = Params1.y;
    float fresnelFall = Params1.z;
    float sharpness   = Params1.w;

    float sheenStrength  = Params2.x;
    float streakLength   = Params2.y;
    float brightThreshold= Params2.z;
    float upBias         = Params2.w;

    float shineStrength = Params3.x;

    // ================= C) SPECULAR SHINE =================
    // Makes the OBJECTS themselves read as polished, rather than only putting
    // reflections on the ground.
    //
    // The trick is that this is the opposite of bloom. Bloom SPREADS light
    // outwards into a haze; a glossy surface CONCENTRATES it into a tight
    // highlight. Subtracting the local surround from the local peak keeps only
    // the core of each highlight and discards the diffuse part, which is
    // exactly what separates "shiny" from merely "bright".
    if (shineStrength > 0.001)
    {
        // Deliberately small radius. A wide one would just rebuild bloom.
        float2 r = InvSourceSize * 2.2;

        float3 peak = BrightPass(uv, brightThreshold * 0.82);
        float3 surround = 0.0;
        [unroll]
        for (int j = 0; j < 8; j++)
            surround += BrightPass(uv + kRing8[j] * r, brightThreshold * 0.82);
        surround /= 8.0;

        // Only the part of the highlight that stands proud of its neighbours.
        float3 specular = max(peak - surround * 0.6, 0.0);

        result += specular * shineStrength * 2.2;
    }

    // ================= A) SCREEN-WIDE SURFACE SHEEN =================
    // Applies above AND below the horizon, so walls and ceilings get it too.
    if (sheenStrength > 0.001)
    {
        // Streaks stretch much further on the floor than on a wall, because
        // the floor recedes away from the camera and drags the reflection with
        // it. Walls and ceiling still get a shorter sheen.
        float floorness = smoothstep(horizon - 0.08, horizon + 0.25, uv.y);
        float lengthScale = lerp(0.5, 1.9, floorness);

        // Ripple the sample position on the floor so the streaks shimmer.
        float wetWave = sin(uv.y * waveFreq + Time * waveSpeed) * waveAmp * floorness * 0.6;
        float2 sheenUv = float2(uv.x + wetWave, uv.y);

        float3 sheen = GatherSheen(sheenUv, streakLength * lengthScale, brightThreshold, upBias);

        // ---- RELATIVE headroom, not absolute ----------------------------
        //
        // A reflection is only visible when the reflected light is BRIGHTER
        // than the surface receiving it. Comparing against a fixed threshold
        // was wrong: on a bright screen (a game menu, a snow level, a white
        // UI) every pixel passed the brightness test, every pixel got a
        // streak, and the whole frame washed out to white.
        //
        // Measuring the reflection against the local surface instead makes the
        // effect self-limiting. On a white screen there is no headroom, so it
        // correctly contributes nothing. On a dark floor under a lamp there is
        // a lot, so it goes strong.
        float sheenLuma = Luma(sheen);
        float baseLuma = Luma(base);
        float headroom = saturate((sheenLuma - baseLuma * 0.9) / max(sheenLuma, 1e-4));

        // Floor is the most reflective surface in the room.
        float surfaceGloss = lerp(0.55, 1.0, floorness);

        float3 contribution = sheen * sheenStrength * headroom * surfaceGloss;

        // Hard ceiling so no single pixel can be driven to pure white even if
        // every other term lines up badly.
        contribution = min(contribution, 0.8);

        // Screen blend, not additive. Reflected light should approach white
        // rather than shoot past it: a plain add at the strength this look
        // needs clips lamps into flat blobs and loses the streak shape.
        result = result + contribution - result * contribution;
    }

    // ================= B) FLOOR MIRROR =================
    if (mirrorOpacity > 0.001 && uv.y > horizon)
    {
        float below = uv.y - horizon;
        float span  = max(1.0 - horizon, 1e-4);
        float d     = saturate(below / span);   // 0 at horizon, 1 at bottom

        // Mirror about the horizon line.
        float srcY = horizon - below;

        // Two waves at incommensurate frequencies drifting opposite ways, so
        // the ripple never visibly loops. Frequency compresses towards the
        // horizon and amplitude grows towards the viewer - both perspective cues.
        float freqScale = lerp(2.4, 1.0, d);
        float phase = uv.y * waveFreq * freqScale;
        float wave = (sin(phase + Time * waveSpeed)
                    + sin(phase * 2.17 - Time * waveSpeed * 0.73) * 0.45) * waveAmp * d;

        float2 srcUv = float2(uv.x + wave, srcY + wave * 0.12);

        // Never sample at or below the horizon: reflecting the floor onto
        // itself is what produced the grey smear in the first version.
        srcUv.y = min(srcUv.y, horizon - 0.002);

        // Roughness grows with distance from the contact line. Sharpness pulls
        // the whole curve down, so a polished marble floor keeps detail while a
        // rough one still dissolves.
        float radius = blurAmount * InvSourceSize.y * (0.4 + d * 2.2)
                     * (1.0 - sharpness * 0.75);
        float2 radiusUv = float2(radius / max(Aspect, 1e-4), radius);

        float3 crisp = SampleSrc(srcUv);

        float3 mirror = crisp * 0.24;
        [unroll]
        for (int i = 0; i < 12; i++)
            mirror += SampleSrc(srcUv + kPoisson[i] * radiusUv) * (0.76 / 12.0);

        // Blend detail back in. A blur gather always loses high frequencies no
        // matter how small the radius, so recovering them from the unblurred
        // tap is what actually makes the reflection read as a sharp mirror
        // instead of a smudge.
        mirror = lerp(mirror, crisp, sharpness * 0.55);

        // Fresnel: grazing angles near the horizon reflect most.
        float fresnel = pow(saturate(1.0 - d), fresnelFall);

        // Bright things reflect, dark geometry barely does.
        float luma = Luma(max(mirror, 0.0));
        float brightness = lerp(1.0, smoothstep(0.02, 0.65, luma), 0.55);

        // Fade where the mirrored source runs off the top of the frame.
        float sourceValid = smoothstep(0.0, 0.06, srcUv.y);

        float alpha = saturate(mirrorOpacity * fresnel * brightness * sourceValid);

        mirror *= lerp(1.0, 0.72, d);   // reflected light loses energy
        result = lerp(result, mirror + (result - base), alpha);
    }

    return float4(max(result, 0.0), 1.0);
}
