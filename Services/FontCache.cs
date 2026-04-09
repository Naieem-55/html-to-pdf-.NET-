using System.Collections.Concurrent;
using SkiaSharp;

namespace html_to_pdf_aspose.Services;

public sealed class FontCache
{
    public static readonly FontCache Instance = new();

    private readonly ConcurrentDictionary<(string family, int weight, int slant), SKTypeface> _typefaceCache = new();
    private readonly ConcurrentDictionary<(char ch, int weight, int slant), SKTypeface> _charFallbackCache = new();
    private readonly ConcurrentDictionary<(string family, float size, int weight, int slant), SKFont> _fontCache = new();

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

    /// <summary>
    /// Get a pooled SKFont instance. DO NOT dispose — owned by the cache.
    /// </summary>
    public SKFont GetFont(string family, float size, bool bold, bool italic)
    {
        var weight = bold ? (int)SKFontStyleWeight.Bold : (int)SKFontStyleWeight.Normal;
        var slant = italic ? (int)SKFontStyleSlant.Italic : (int)SKFontStyleSlant.Upright;
        var key = (family, size, weight, slant);

        return _fontCache.GetOrAdd(key, k =>
            new SKFont(GetTypeface(k.family, bold, italic), k.size));
    }

    /// <summary>
    /// Get a pooled SKFont with automatic font fallback for non-Latin text.
    /// </summary>
    public SKFont GetFontForText(string text, string fontFamily, float size, bool bold, bool italic)
    {
        var typeface = ResolveForText(text, fontFamily, bold, italic);
        var weight = bold ? (int)SKFontStyleWeight.Bold : (int)SKFontStyleWeight.Normal;
        var slant = italic ? (int)SKFontStyleSlant.Italic : (int)SKFontStyleSlant.Upright;
        var key = (typeface.FamilyName, size, weight, slant);

        return _fontCache.GetOrAdd(key, _ => new SKFont(typeface, size));
    }

    private SKTypeface ResolveForChar(char ch, bool bold, bool italic)
    {
        var weight = bold ? (int)SKFontStyleWeight.Bold : (int)SKFontStyleWeight.Normal;
        var slant = italic ? (int)SKFontStyleSlant.Italic : (int)SKFontStyleSlant.Upright;
        var key = (ch, weight, slant);

        return _charFallbackCache.GetOrAdd(key, k =>
        {
            var nirmala = GetTypeface("Nirmala UI", bold, italic);
            if (nirmala.ContainsGlyph(k.ch))
                return nirmala;

            var fallback = SKFontManager.Default.MatchCharacter(k.ch);
            if (fallback != null)
                return fallback;

            return nirmala;
        });
    }
}
