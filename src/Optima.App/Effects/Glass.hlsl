// Optima glass: refraction, chromatic fringe, pointer-reactive specular and a rounded-rect
// mask, all in one ps_3_0 pass. The input is the already-blurred backdrop (WPF BlurEffect on
// the VisualBrush snapshot), rendered with a bleed margin of Inset px around the panel so the
// blur has real pixels to sample at the rim.
//
// Compile (Windows Kit fxc):
//   fxc /nologo /T ps_3_0 /E main /O3 /Fo Glass.ps Glass.hlsl

sampler2D Input : register(s0);

float2 Size     : register(c0);   // container size in DIPs (panel + 2 * Inset)
float  Inset    : register(c1);   // bleed margin in DIPs
float  Radius   : register(c2);   // corner radius in DIPs
float  Edge     : register(c3);   // refraction band width in DIPs
float  Refract  : register(c4);   // refraction strength in DIPs
float  Chroma   : register(c5);   // relative RGB spread of the refraction (0 = none)
float2 Light    : register(c6);   // pointer position in container DIPs
float  Specular : register(c7);   // rim highlight intensity
float4 Tint     : register(c8);   // straight (non-premultiplied) tint, alpha = strength

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 p = uv * Size;
    float2 c = Size * 0.5;
    float2 half = c - Inset;
    float2 rel = p - c;

    // Signed distance to the rounded rectangle (negative inside).
    float2 q = abs(rel) - (half - Radius);
    float2 qc = max(q, 0);
    float d = length(qc) + min(max(q.x, q.y), 0) - Radius;
    float mask = 1 - smoothstep(-0.75, 0.75, d);

    // Outward normal of the rounded rectangle.
    float2 n;
    if (q.x > 0 || q.y > 0)
    {
        n = normalize(qc + 1e-4);
    }
    else
    {
        n = (q.x > q.y) ? float2(1, 0) : float2(0, 1);
    }
    n *= sign(rel);

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

    // Specular rim that faces the light, a broad soft glow around the pointer, and a constant
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
