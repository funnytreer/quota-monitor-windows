using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;

namespace QuotaMonitor;

internal static class ServiceMark
{
    // These bundled Lobe Icons SVGs each contain one currentColor path on a 24 × 24 viewBox.
    // Keep the source viewport, including its padding, when scaling the mark for the UI.
    private static readonly Geometry OpenAI = Load("openai");
    private static readonly Geometry Gemini = Load("gemini");
    private static readonly Geometry Claude = Load("claude");
    private static readonly Geometry Grok = Load("grok");
    private static readonly Geometry Antigravity = Load("antigravity");

    private static Geometry Load(string name)
    {
        using var stream = typeof(ServiceMark).Assembly.GetManifestResourceStream($"QuotaMonitor.Brands.{name}.svg")
            ?? throw new FileNotFoundException($"Missing embedded brand icon: {name}");
        var svg = XElement.Load(stream);
        var path = svg.Elements(svg.Name.Namespace + "path").Single();
        return Visuals.Frozen(Geometry.Parse((string?)path.Attribute("d")
            ?? throw new InvalidDataException($"Missing SVG path: {name}")));
    }

    public static Viewbox Create(Service service, Brush ink, double size, string? bucketKey = null)
    {
        var geometry = (service, bucketKey) switch
        {
            (Service.Antigravity, "ag.claude.7d") => Claude,
            (Service.Antigravity, "ag.gemini.7d") or (Service.Gemini, _) => Gemini,
            (Service.Antigravity, _) => Antigravity,
            (Service.ChatGPT, _) => OpenAI,
            (Service.Grok, _) => Grok,
            _ => throw new ArgumentOutOfRangeException(nameof(service))
        };
        var viewport = new Canvas { Width = 24, Height = 24 };
        viewport.Children.Add(new System.Windows.Shapes.Path { Data = geometry, Fill = ink, Stretch = Stretch.None });
        return new Viewbox
        {
            Width = size, Height = size, Child = viewport, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center
        };
    }
}
