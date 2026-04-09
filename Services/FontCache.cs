using System.Collections.Concurrent;
using SkiaSharp;

namespace html_to_pdf_aspose.Services;

/// <summary>
/// Caches SKTypeface instances to avoid repeated expensive SKTypeface.FromFamilyName() calls.
/// Also caches character→typeface fallback lookups.
/// </summary>
public sealed class FontCache
{
    public static readonly FontCache Instance = new();

    // Cache typefaces by (family, weight, slant)
    private readonly ConcurrentDictionary<(string family, int weight, int slant), SKTypeface> _typefaceCache = new();

    // Cache character→resolved family name for font fallback
    private readonly ConcurrentDictionary<(char ch, int weight, int slant), SKTypeface> _charFallbackCache = new();

    // Cache font metrics: (family, size) → spaceWidth
    private readonly ConcurrentDictionary<(string family, float size), float> _spaceWidthCache = new();

    public SKTypeface GetTypeface(string family, bool bold, bool italic)
    {
        var weight = bold ? (int)SKFontStyleWeight.Bold : (int)SKFontStyleWeight.Normal;
        var slant = italic ? (int)SKFontStyleSlant.Italic : (int)SKFontStyleSlant.Upright;
        var key = (family, weight, slant);

        return _typefaceCache.GetOrAdd(key, k =>
            SKTypeface.FromFamilyName(k.family, (SKFontStyleWeight)k.weight, SKFontStyleWidth.Normal, (SKFontStyleSlant)k.slant));
    }

    public SKTypeface ResolveForText(string text, string fontFamily, bool bold, bool italic)
    {
        var primary = GetTypeface(fontFamily, bold, italic);

        // Find first non-ASCII character
        foreach (var ch in text)
        {
            if (ch > 127)
            {
                if (primary.ContainsGlyph(ch))
                    return primary;

                return ResolveForChar(ch, bold, italic);
            }
        }

        return primary;
    }

    private SKTypeface ResolveForChar(char ch, bool bold, bool italic)
    {
        var weight = bold ? (int)SKFontStyleWeight.Bold : (int)SKFontStyleWeight.Normal;
        var slant = italic ? (int)SKFontStyleSlant.Italic : (int)SKFontStyleSlant.Upright;
        var key = (ch, weight, slant);

        return _charFallbackCache.GetOrAdd(key, k =>
        {
            // Try Nirmala UI first (Bengali/Indic)
            var nirmala = GetTypeface("Nirmala UI", bold, italic);
            if (nirmala.ContainsGlyph(k.ch))
                return nirmala;

            // SKFontManager match
            var fallback = SKFontManager.Default.MatchCharacter(k.ch);
            if (fallback != null)
                return fallback;

            // Give up — return Nirmala UI anyway
            return nirmala;
        });
    }

    public float GetSpaceWidth(string fontFamily, float fontSize, bool bold, bool italic)
    {
        var key = (fontFamily, fontSize);
        return _spaceWidthCache.GetOrAdd(key, _ =>
        {
            using var font = new SKFont(GetTypeface(fontFamily, bold, italic), fontSize);
            return font.MeasureText(" ");
        });
    }
}
