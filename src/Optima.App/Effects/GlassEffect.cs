using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Optima.App.Effects;

/// <summary>
/// The glass pass (Effects/Glass.hlsl): rounded-rect mask, edge refraction with a chromatic
/// fringe, pointer-reactive specular and a tint, applied to an already-blurred backdrop.
/// Register numbers match the HLSL constants.
/// </summary>
public sealed class GlassEffect : ShaderEffect
{
    private static readonly PixelShader Shader = new()
    {
        UriSource = new Uri("pack://application:,,,/Optima;component/Effects/Glass.ps"),
    };

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty(nameof(Input), typeof(GlassEffect), 0);

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(Point), typeof(GlassEffect),
        new UIPropertyMetadata(new Point(200, 120), PixelShaderConstantCallback(0)));

    public static readonly DependencyProperty InsetProperty = DependencyProperty.Register(
        nameof(Inset), typeof(double), typeof(GlassEffect),
        new UIPropertyMetadata(0.0, PixelShaderConstantCallback(1)));

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(GlassEffect),
        new UIPropertyMetadata(16.0, PixelShaderConstantCallback(2)));

    public static readonly DependencyProperty EdgeProperty = DependencyProperty.Register(
        nameof(Edge), typeof(double), typeof(GlassEffect),
        new UIPropertyMetadata(28.0, PixelShaderConstantCallback(3)));

    public static readonly DependencyProperty RefractProperty = DependencyProperty.Register(
        nameof(Refract), typeof(double), typeof(GlassEffect),
        new UIPropertyMetadata(12.0, PixelShaderConstantCallback(4)));

    public static readonly DependencyProperty ChromaProperty = DependencyProperty.Register(
        nameof(Chroma), typeof(double), typeof(GlassEffect),
        new UIPropertyMetadata(0.35, PixelShaderConstantCallback(5)));

    public static readonly DependencyProperty LightProperty = DependencyProperty.Register(
        nameof(Light), typeof(Point), typeof(GlassEffect),
        new UIPropertyMetadata(new Point(-1000, -1000), PixelShaderConstantCallback(6)));

    public static readonly DependencyProperty SpecularProperty = DependencyProperty.Register(
        nameof(Specular), typeof(double), typeof(GlassEffect),
        new UIPropertyMetadata(0.9, PixelShaderConstantCallback(7)));

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Color), typeof(GlassEffect),
        new UIPropertyMetadata(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF), PixelShaderConstantCallback(8)));

    public GlassEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(SizeProperty);
        UpdateShaderValue(InsetProperty);
        UpdateShaderValue(RadiusProperty);
        UpdateShaderValue(EdgeProperty);
        UpdateShaderValue(RefractProperty);
        UpdateShaderValue(ChromaProperty);
        UpdateShaderValue(LightProperty);
        UpdateShaderValue(SpecularProperty);
        UpdateShaderValue(TintProperty);
    }

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public Point Size
    {
        get => (Point)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double Inset
    {
        get => (double)GetValue(InsetProperty);
        set => SetValue(InsetProperty, value);
    }

    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    public double Edge
    {
        get => (double)GetValue(EdgeProperty);
        set => SetValue(EdgeProperty, value);
    }

    public double Refract
    {
        get => (double)GetValue(RefractProperty);
        set => SetValue(RefractProperty, value);
    }

    public double Chroma
    {
        get => (double)GetValue(ChromaProperty);
        set => SetValue(ChromaProperty, value);
    }

    public Point Light
    {
        get => (Point)GetValue(LightProperty);
        set => SetValue(LightProperty, value);
    }

    public double Specular
    {
        get => (double)GetValue(SpecularProperty);
        set => SetValue(SpecularProperty, value);
    }

    public Color Tint
    {
        get => (Color)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }
}
