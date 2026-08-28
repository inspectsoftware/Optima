using System.Xml.Linq;
using Optima.Core.Models;

namespace Optima.Driver;

/// <summary>
/// Reads and edits the Virtual Display Driver settings XML (vdd_settings.xml) non-destructively:
/// existing nodes, comments and options are preserved; we only add resolutions / refresh rates
/// that a requested mode needs. Pure XML in/out, unit-testable without the driver.
/// </summary>
public sealed class VddSettingsDocument
{
    private readonly XDocument _document;

    private VddSettingsDocument(XDocument document)
    {
        _document = document;
    }

    public static VddSettingsDocument Parse(string xml) => new(XDocument.Parse(xml, LoadOptions.PreserveWhitespace));

    public static VddSettingsDocument Load(string path) => new(XDocument.Load(path, LoadOptions.PreserveWhitespace));

    public string ToXmlString() => _document.Declaration is { } declaration
        ? declaration + Environment.NewLine + _document
        : _document.ToString();

    public void Save(string path) => File.WriteAllText(path, ToXmlString());

    private XElement Root => _document.Root ?? throw new InvalidDataException("vdd_settings.xml has no root element.");

    public int MonitorCount
    {
        get => int.TryParse(Root.Element("monitors")?.Element("count")?.Value, out var count) ? count : 1;
    }

    public string GpuFriendlyName
        => Root.Element("gpu")?.Element("friendlyname")?.Value?.Trim() ?? "default";

    public void SetGpuFriendlyName(string name)
    {
        var gpu = GetOrAdd(Root, "gpu");
        GetOrAdd(gpu, "friendlyname").Value = name;
    }

    /// <summary>Global refresh rates, replicated by the driver across all resolutions.</summary>
    public IReadOnlyList<int> GlobalRefreshRates
        => Root.Element("global")?.Elements("g_refresh_rate")
            .Select(e => int.TryParse(e.Value, out var r) ? r : 0)
            .Where(r => r > 0)
            .ToList() ?? [];

    public IReadOnlyList<(int Width, int Height, int RefreshRate)> Resolutions
        => Root.Element("resolutions")?.Elements("resolution")
            .Select(r => (
                Width: int.TryParse(r.Element("width")?.Value, out var w) ? w : 0,
                Height: int.TryParse(r.Element("height")?.Value, out var h) ? h : 0,
                RefreshRate: int.TryParse(r.Element("refresh_rate")?.Value, out var f) ? f : 0))
            .Where(r => r.Width > 0 && r.Height > 0)
            .ToList() ?? [];

    /// <summary>
    /// Every mode the driver will advertise: each listed resolution × (its own refresh rate + all
    /// global refresh rates). Bogus placeholder rates (e.g. 999 or 9999) are filtered by IsValid.
    /// </summary>
    public IReadOnlyList<DisplayMode> GetAdvertisedModes()
    {
        var modes = new HashSet<DisplayMode>();
        var globals = GlobalRefreshRates;
        foreach (var (width, height, refresh) in Resolutions)
        {
            var own = new DisplayMode(width, height, refresh);
            if (own.IsValid)
            {
                modes.Add(own);
            }
            foreach (var g in globals)
            {
                var mode = new DisplayMode(width, height, g);
                if (mode.IsValid)
                {
                    modes.Add(mode);
                }
            }
        }
        return modes.OrderByDescending(m => m.Width).ThenByDescending(m => m.RefreshRate).ToList();
    }

    /// <summary>Ensures the driver will advertise the given mode. Returns true when the XML changed.</summary>
    public bool EnsureMode(DisplayMode mode)
    {
        if (!mode.IsValid)
        {
            throw new ArgumentException($"Invalid display mode {mode}.", nameof(mode));
        }

        var changed = false;
        var resolutions = GetOrAdd(Root, "resolutions");

        var hasResolution = Resolutions.Any(r => r.Width == mode.Width && r.Height == mode.Height);
        if (!hasResolution)
        {
            resolutions.Add(new XElement("resolution",
                new XElement("width", mode.Width),
                new XElement("height", mode.Height),
                new XElement("refresh_rate", mode.RefreshRate)));
            changed = true;
        }

        if (!GetAdvertisedModes().Contains(mode))
        {
            // Resolution exists but not at this refresh rate, so add a global refresh rate,
            // which the driver replicates to every resolution.
            var global = GetOrAdd(Root, "global");
            global.Add(new XElement("g_refresh_rate", mode.RefreshRate));
            changed = true;
        }

        return changed;
    }

    private static XElement GetOrAdd(XElement parent, string name)
    {
        var element = parent.Element(name);
        if (element is null)
        {
            element = new XElement(name);
            parent.Add(element);
        }
        return element;
    }
}
