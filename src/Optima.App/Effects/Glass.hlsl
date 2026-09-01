// Optima glass: refraction, chromatic fringe, pointer-reactive specular and a shape mask,
// all in one ps_3_0 pass. The input is the (optionally blurred) backdrop snapshot rendered
// with a bleed margin of Inset px around the panel so the rim never samples empty pixels.
// The shape is a rounded rectangle when Chamfer is 0, otherwise the HUD chamfer: the
// top-left and bottom-right corners cut at 45 degrees.
//
// Compile (Windows Kit fxc):
//   fxc /nologo /T ps_3_0 /E main /O3 /Fo Glass.ps Glass.hlsl

sampler2D Input : register(s0);

float2 Size     : register(c0);   // container size in DIPs (panel + 2 * Inset)
float  Inset    : register(c1);   // bleed margin in DIPs
float  Radius   : register(c2);   // corner radius in DIPs (rounded mode)
float  Edge     : register(c3);   // refraction band width in DIPs
float  Refract  : register(c4);   // refraction strength in DIPs
float  Chroma   : register(c5);   // relative RGB spread of the refraction (0 = none)
float2 Light    : register(c6);   // pointer position in container DIPs
float  Specular : register(c7);   // rim highlight intensity
float4 Tint     : register(c8);   // straight (non-premultiplied) tint, alpha = strength
float  Chamfer  : register(c9);   // chamfer size in DIPs; > 0 selects the HUD shape

static const float InvSqrt2 = 0.70710678;

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 p = uv * Size;
    float2 c = Size * 0.5;
    float2 half = c - Inset;
    float2 rel = p - c;

    // Signed distance and outward normal of the panel shape (negative inside).
    float d;
    float2 n;
    if (Chamfer > 0)
    {
        float2 q = abs(rel) - half;
        float2 qc = max(q, 0);
        float dRect = length(qc) + min(max(q.x, q.y), 0);
        float2 nRect = (q.x > 0 || q.y > 0) ? normalize(qc + 1e-4) : ((q.x > q.y) ? float2(1, 0) : float2(0, 1));
        nRect *= sign(rel);
        // The two cut corners are half-planes at 45 degrees.
        float reach = half.x + half.y - Chamfer;
        float dTL = (-rel.x - rel.y - reach) * InvSqrt2;
        float dBR = (rel.x + rel.y - reach) * InvSqrt2;
        d = dRect;
        n = nRect;
        if (dTL > d) { d = dTL; n = float2(-InvSqrt2, -InvSqrt2); }
        if (dBR > d) { d = dBR; n = float2(InvSqrt2, InvSqrt2); }
    }
    else
    {
        float2 q = abs(rel) - (half - Radius);
        float2 qc = max(q, 0);
        d = length(qc) + min(max(q.x, q.y), 0) - Radius;
        n = (q.x > 0 || q.y > 0) ? normalize(qc + 1e-4) : ((q.x > q.y) ? float2(1, 0) : float2(0, 1));
        n *= sign(rel);
    }
    float mask = 1 - smoothstep(-0.75, 0.75, d);

    // Refraction: inside a band along the rim, sample content pulled in from the interior,
    // strongest at the edge. Each channel bends a little differently (chroma).
    float t = saturate(1 + d / Edge);
    float bend = Refract * t * t;
    float2 px = 1.0 / Size;
    float2 o = -n * bend * px;
    float r = tex2D(Input, uv + o * (1 + Chroma)).r;
    float g = tex2D(Input, uv + o).g;
    float b = tex2D(Input, uv + o * (1 - Chroma)).b;
    float3 col = float3(r, g, b);

    col = lerp(col, Tint.rgb, Tint.a);

    // Specular rim facing the light, a broad soft glow around the pointer, and a constant
    // top-edge sheen that sells thickness even when the pointer is elsewhere.
    float2 ld = Light - p;
    float2 l = normalize(ld + 1e-3);
    float rim = 1 - smoothstep(0, 1.6, abs(d + 0.9));
    float facing = saturate(dot(n, l) * 0.5 + 0.5);
    float spec = rim * pow(facing, 3) * Specular;
    float dist2 = dot(ld, ld);
    float glow = Specular * 0.08 * exp(-dist2 / (Size.x * Size.x * 0.10));
    float sheen = rim * saturate(-n.y) * 0.22;

    col += spec + glow + sheen;

    return float4(col * mask, mask);
}
