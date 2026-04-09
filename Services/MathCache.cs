using System.Collections.Concurrent;
using System.Drawing;
using AngleSharp.Dom;
using CSharpMath.SkiaSharp;
using SkiaSharp;

namespace html_to_pdf_aspose.Services;

/// <summary>
/// Pre-measures all LaTeX formulas in parallel, caches results for layout + render.
/// Eliminates duplicate MathPainter creation (was: once in layout, once in render).
/// </summary>
public class MathCache
{
    private readonly Dictionary<string, MathMeasurement> _cache = new();

    /// <summary>
    /// Scan the DOM for all math-tex spans, extract unique LaTeX, measure all in parallel.
    /// </summary>
    public void PreMeasure(IDocument document, float defaultFontSize)
    {
        var mathSpans = document.QuerySelectorAll("span.math-tex");
        var uniqueLatex = new HashSet<string>();

        foreach (var span in mathSpans)
        {
            var latex = ExtractLatex(span.TextContent);
            if (!string.IsNullOrWhiteSpace(latex))
                uniqueLatex.Add(latex);
        }

        // Measure all unique formulas sequentially (CSharpMath MathPainter is not thread-safe).
        // Only measure inline size; display size measured on-demand.
        foreach (var latex in uniqueLatex)
        {
            MeasureAndCache(latex, defaultFontSize, false);
        }
    }

    public MathMeasurement? Get(string latex, float fontSize, bool isDisplay)
    {
        var key = CacheKey(latex, fontSize, isDisplay);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        // Fallback: measure on demand if not pre-cached (different font size)
        return MeasureAndCache(latex, fontSize, isDisplay);
    }

    private MathMeasurement? MeasureAndCache(string latex, float fontSize, bool isDisplay)
    {
        var key = CacheKey(latex, fontSize, isDisplay);
        if (_cache.TryGetValue(key, out var existing))
            return existing;

        var measurement = CreateMeasurement(latex, fontSize, isDisplay);
        _cache[key] = measurement;
        return measurement;
    }

    private static MathMeasurement CreateMeasurement(string latex, float fontSize, bool isDisplay)
    {
            var painter = new MathPainter
            {
                LaTeX = latex,
                FontSize = fontSize,
                AntiAlias = true,
                DisplayErrorInline = false,
                LineStyle = isDisplay
                    ? CSharpMath.Atom.LineStyle.Display
                    : CSharpMath.Atom.LineStyle.Text
            };

            if (painter.ErrorMessage != null)
            {
                return new MathMeasurement
                {
                    HasError = true,
                    ErrorMessage = painter.ErrorMessage
                };
            }

            var bounds = painter.Measure();
            return new MathMeasurement
            {
                Width = bounds.Width - bounds.X,
                Height = bounds.Height,
                BoundsX = bounds.X,
                BoundsY = bounds.Y
            };
    }

    private static string CacheKey(string latex, float fontSize, bool isDisplay)
        => $"{(isDisplay ? "D" : "I")}:{fontSize:F1}:{latex}";

    private static string ExtractLatex(string text)
    {
        text = text.Trim();
        if (text.StartsWith("\\(") && text.EndsWith("\\)"))
            text = text[2..^2];
        else if (text.StartsWith("\\[") && text.EndsWith("\\]"))
            text = text[2..^2];
        else if (text.StartsWith("$$") && text.EndsWith("$$"))
            text = text[2..^2];
        else if (text.StartsWith('$') && text.EndsWith('$') && text.Length > 1)
            text = text[1..^1];

        text = System.Net.WebUtility.HtmlDecode(text);
        text = LayoutEngine.SanitizeLatex(text);
        return text.Trim();
    }
}

public class MathMeasurement
{
    public float Width { get; init; }
    public float Height { get; init; }
    public float BoundsX { get; init; }
    public float BoundsY { get; init; }
    public bool HasError { get; init; }
    public string? ErrorMessage { get; init; }
}
