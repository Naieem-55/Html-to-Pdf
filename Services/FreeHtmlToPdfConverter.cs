using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using CSharpMath.SkiaSharp;
using SkiaSharp;

namespace html_to_pdf_aspose.Services;

public class FreeHtmlToPdfConverter
{
    private static readonly ILogger<FreeHtmlToPdfConverter> _logger =
        LoggerFactory.Create(b => b.AddConsole()).CreateLogger<FreeHtmlToPdfConverter>();

    public byte[] ConvertFromHtmlString(string htmlContent, PdfPageSettings? settings = null)
    {
        settings ??= new PdfPageSettings();
        var pageSize = GetPageSizePoints(settings);
        var margin = settings.MarginMm * 2.83465f;

        var sw = Stopwatch.StartNew();

        var document = ParseHtml(htmlContent).GetAwaiter().GetResult();
        var parseTime = sw.ElapsedMilliseconds;

        // Pre-measure all LaTeX formulas in parallel (uses all CPU cores)
        var mathCache = new MathCache();
        var defaultFontSize = ParseBodyFontSize(document);
        mathCache.PreMeasure(document, defaultFontSize);
        var mathPreMeasureTime = sw.ElapsedMilliseconds - parseTime;

        var layoutBoxes = LayoutDocument(document, pageSize, margin, mathCache);
        var layoutTime = sw.ElapsedMilliseconds - parseTime - mathPreMeasureTime;

        var pdf = RenderToPdf(layoutBoxes, pageSize, margin, mathCache);
        var renderTime = sw.ElapsedMilliseconds - parseTime - mathPreMeasureTime - layoutTime;

        _logger.LogInformation(
            "Breakdown: parse={ParseMs}ms, mathPreMeasure={MathPreMs}ms, layout={LayoutMs}ms ({BoxCount} boxes), render={RenderMs}ms, total={TotalMs}ms",
            parseTime, mathPreMeasureTime, layoutTime, layoutBoxes.Count, renderTime, sw.ElapsedMilliseconds);

        return pdf;
    }

    private static float ParseBodyFontSize(IDocument document)
    {
        var body = document.Body;
        if (body == null) return 12f;
        var style = body.GetAttribute("style");
        if (style == null) return 12f;
        foreach (var decl in style.Split(';'))
        {
            var parts = decl.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Equals("font-size", StringComparison.OrdinalIgnoreCase))
                return ParseMmOrPx(parts[1], 12f);
        }
        return 12f;
    }

    private static float ParseMmOrPx(string val, float fallback)
    {
        val = val.Trim();
        if (val.EndsWith("mm") && float.TryParse(val[..^2], out var mm)) return mm * 2.83465f;
        if (val.EndsWith("px") && float.TryParse(val[..^2], out var px)) return px;
        if (val.EndsWith("pt") && float.TryParse(val[..^2], out var pt)) return pt * 1.333f;
        return float.TryParse(val, out var v) ? v : fallback;
    }

    public byte[] ConvertFromFile(string filePath, PdfPageSettings? settings = null)
    {
        var html = File.ReadAllText(filePath);
        return ConvertFromHtmlString(html, settings);
    }

    public byte[] ConvertFromUrl(string url, PdfPageSettings? settings = null)
    {
        settings ??= new PdfPageSettings();
        var pageSize = GetPageSizePoints(settings);
        var margin = settings.MarginMm * 2.83465f;

        var document = ParseUrl(url).GetAwaiter().GetResult();
        var mathCache = new MathCache();
        mathCache.PreMeasure(document, ParseBodyFontSize(document));
        var layoutBoxes = LayoutDocument(document, pageSize, margin, mathCache);

        return RenderToPdf(layoutBoxes, pageSize, margin, mathCache);
    }

    private static async Task<IDocument> ParseHtml(string html)
    {
        var config = Configuration.Default.WithCss();
        var context = BrowsingContext.New(config);
        return await context.OpenAsync(req => req.Content(html));
    }

    private static async Task<IDocument> ParseUrl(string url)
    {
        var config = Configuration.Default.WithDefaultLoader().WithCss();
        var context = BrowsingContext.New(config);
        return await context.OpenAsync(url);
    }

    private static SKSize GetPageSizePoints(PdfPageSettings settings)
    {
        var (w, h) = settings.PageSize switch
        {
            PageSize.A3 => (842f, 1191f),
            PageSize.A5 => (420f, 595f),
            PageSize.Letter => (612f, 792f),
            PageSize.Legal => (612f, 1008f),
            _ => (595f, 842f)
        };
        return settings.Landscape ? new SKSize(h, w) : new SKSize(w, h);
    }

    private List<LayoutBox> LayoutDocument(IDocument document, SKSize pageSize, float margin, MathCache mathCache)
    {
        var contentWidth = pageSize.Width - margin * 2;
        var engine = new LayoutEngine(contentWidth, margin, mathCache);

        var body = document.Body;
        if (body == null) return engine.Boxes;

        engine.LayoutElement(body);

        _logger.LogInformation(
            "  Layout breakdown: styleResolve={StyleMs}ms ({StyleCount} calls), displayNone={DnMs}ms ({DnCount} calls), mathMeasure={MathMs}ms ({MathCount} formulas), textMeasure={TextMs}ms, fontFallback={FontMs}ms",
            engine.StyleResolveMs, engine.StyleResolveCount,
            engine.DisplayNoneMs, engine.DisplayNoneCount,
            engine.MathMeasureMs, engine.MathCount,
            engine.TextMeasureMs, engine.FontFallbackMs);

        return engine.Boxes;
    }

    // --- PDF Renderer ---

    private byte[] RenderToPdf(List<LayoutBox> boxes, SKSize pageSize, float margin, MathCache mathCache)
    {
        using var memStream = new MemoryStream();
        using var wstream = new SKManagedWStream(memStream);

        var metadata = new SKDocumentPdfMetadata
        {
            Title = "Converted PDF",
            Creation = DateTime.Now,
            RasterDpi = 300,
            EncodingQuality = 100
        };

        using var pdfDoc = SKDocument.CreatePdf(wstream, metadata);

        var contentHeight = pageSize.Height - margin * 2;
        var pages = PaginateBoxes(boxes, contentHeight, margin);

        foreach (var page in pages)
        {
            using var canvas = pdfDoc.BeginPage(pageSize.Width, pageSize.Height);
            foreach (var box in page)
            {
                RenderBox(canvas, box, mathCache);
            }
            pdfDoc.EndPage();
        }

        pdfDoc.Close();
        wstream.Flush();
        return memStream.ToArray();
    }

    private List<List<LayoutBox>> PaginateBoxes(List<LayoutBox> boxes, float contentHeight, float margin)
    {
        var pages = new List<List<LayoutBox>>();
        var currentPage = new List<LayoutBox>();
        var pageBottom = margin + contentHeight;
        var pageIndex = 0;

        foreach (var box in boxes)
        {
            if (box.Y + box.Height > pageBottom && currentPage.Count > 0)
            {
                pages.Add(currentPage);
                currentPage = new List<LayoutBox>();
                pageIndex++;
                pageBottom = margin + contentHeight + (pageIndex * (contentHeight + margin * 2));
            }

            var adjustedBox = box with
            {
                Y = box.Y - pageIndex * (contentHeight + margin * 2) + (pageIndex > 0 ? margin : 0)
            };
            currentPage.Add(adjustedBox);
        }

        if (currentPage.Count > 0)
            pages.Add(currentPage);

        if (pages.Count == 0)
            pages.Add(new List<LayoutBox>());

        return pages;
    }

    private void RenderBox(SKCanvas canvas, LayoutBox box, MathCache mathCache)
    {
        // Background
        if (box.BackgroundColor != SKColor.Empty && box.BackgroundColor != SKColors.Transparent)
        {
            using var bgPaint = new SKPaint
            {
                Color = box.BackgroundColor,
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            canvas.DrawRect(box.X, box.Y, box.Width, box.Height, bgPaint);
        }

        // Border
        if (box.BorderWidth > 0 && box.BorderColor != SKColor.Empty)
        {
            using var borderPaint = new SKPaint
            {
                Color = box.BorderColor,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = box.BorderWidth,
                IsAntialias = true
            };
            canvas.DrawRect(box.X, box.Y, box.Width, box.Height, borderPaint);
        }

        // Text
        if (!string.IsNullOrEmpty(box.Text))
        {
            var typeface = FontCache.Instance.ResolveForText(box.Text, box.FontFamily ?? "Arial", box.Bold, box.Italic);
            using var font = new SKFont(typeface, box.FontSize);
            using var paint = new SKPaint
            {
                Color = box.TextColor,
                IsAntialias = true
            };

            var textX = box.X;
            if (box.TextAlign == TextAlign.Center)
            {
                var textWidth = font.MeasureText(box.Text);
                textX = box.X + (box.Width - textWidth) / 2;
            }
            else if (box.TextAlign == TextAlign.Right)
            {
                var textWidth = font.MeasureText(box.Text);
                textX = box.X + box.Width - textWidth;
            }

            canvas.DrawText(box.Text, textX, box.Y + box.FontSize, font, paint);
        }

        // LaTeX math
        if (!string.IsNullOrEmpty(box.LaTeX))
        {
            var cached = mathCache.Get(box.LaTeX, box.FontSize, box.IsDisplayMath);

            if (cached == null || cached.HasError)
            {
                // Fallback: render LaTeX source as plain text
                var fallbackTf = FontCache.Instance.GetTypeface("Arial", false, false);
                using var fallbackFont = new SKFont(fallbackTf, box.FontSize * 0.8f);
                using var fallbackPaint = new SKPaint
                {
                    Color = box.TextColor,
                    IsAntialias = true
                };
                canvas.DrawText(box.LaTeX, box.X, box.Y + box.FontSize, fallbackFont, fallbackPaint);
            }
            else
            {
                // Create painter only for drawing (measurement already cached)
                var painter = new MathPainter
                {
                    LaTeX = box.LaTeX,
                    FontSize = box.FontSize,
                    TextColor = box.TextColor,
                    AntiAlias = true,
                    DisplayErrorInline = false,
                    LineStyle = box.IsDisplayMath
                        ? CSharpMath.Atom.LineStyle.Display
                        : CSharpMath.Atom.LineStyle.Text
                };
                var drawY = box.Y + box.FontSize;
                painter.Draw(canvas, box.X - cached.BoundsX, drawY);
            }
        }

        // Horizontal rule
        if (box.IsHr)
        {
            using var hrPaint = new SKPaint
            {
                Color = box.BorderColor != SKColor.Empty ? box.BorderColor : new SKColor(200, 200, 200),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                IsAntialias = true
            };
            var hrY = box.Y + box.Height / 2;
            canvas.DrawLine(box.X, hrY, box.X + box.Width, hrY, hrPaint);
        }
    }

}

// --- Layout Box Record ---

public record LayoutBox
{
    public float X { get; init; }
    public float Y { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
    public string? Text { get; init; }
    public float FontSize { get; init; } = 12;
    public string? FontFamily { get; init; } = "Arial";
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public SKColor TextColor { get; init; } = SKColors.Black;
    public SKColor BackgroundColor { get; init; } = SKColors.Transparent;
    public SKColor BorderColor { get; init; } = SKColor.Empty;
    public float BorderWidth { get; init; }
    public TextAlign TextAlign { get; init; } = TextAlign.Left;
    public bool IsHr { get; init; }
    public string? LaTeX { get; init; }
    public bool IsDisplayMath { get; init; }
}

public enum TextAlign { Left, Center, Right }

// --- Layout Engine ---

public class LayoutEngine
{
    private readonly float _contentWidth;
    private readonly float _marginLeft;
    private float _cursorY;

    public List<LayoutBox> Boxes { get; } = new();

    // Inline accumulation state
    private float _inlineX;
    private bool _inInline;
    private float _inlineMaxHeight;

    // Inherited style state
    private readonly Stack<InheritedStyle> _styleStack = new();

    // Math cache (pre-measured in parallel)
    private readonly MathCache _mathCache;

    // Timing counters
    public long MathMeasureMs { get; private set; }
    public long StyleResolveMs { get; private set; }
    public long FontFallbackMs { get; private set; }
    public long TextMeasureMs { get; private set; }
    public long DisplayNoneMs { get; private set; }
    public int MathCount { get; private set; }
    public int FontFallbackCount { get; private set; }
    public int StyleResolveCount { get; private set; }
    public int DisplayNoneCount { get; private set; }

    public LayoutEngine(float contentWidth, float marginLeft, MathCache mathCache)
    {
        _contentWidth = contentWidth;
        _marginLeft = marginLeft;
        _cursorY = marginLeft;
        _inlineX = marginLeft;
        _mathCache = mathCache;
        _styleStack.Push(new InheritedStyle());
    }

    public void LayoutElement(IElement element)
    {
        var tagName = element.TagName.ToUpperInvariant();

        // Skip invisible elements — no style computation needed
        if (tagName is "SCRIPT" or "STYLE" or "META" or "LINK" or "HEAD" or "TITLE" or "NOSCRIPT")
            return;

        var swStyle = Stopwatch.StartNew();
        var style = ResolveStyle(element);
        StyleResolveMs += swStyle.ElapsedMilliseconds;
        StyleResolveCount++;

        if (style.IsHidden)
            return;

        _styleStack.Push(style);

        // Handle <span class="math-tex"> as inline LaTeX
        if (IsMathTexElement(element))
        {
            var latex = ExtractLatex(element.TextContent);
            if (!string.IsNullOrWhiteSpace(latex))
            {
                EmitInlineMath(latex, style);
            }
            _styleStack.Pop();
            return;
        }

        var isBlock = IsBlockElement(tagName);

        if (isBlock)
        {
            FlushInline();
            ApplyMarginTop(style);
        }

        switch (tagName)
        {
            case "BR":
                FlushInline();
                break;

            case "HR":
                FlushInline();
                _cursorY += 4;
                Boxes.Add(new LayoutBox
                {
                    X = _marginLeft,
                    Y = _cursorY,
                    Width = _contentWidth,
                    Height = 8,
                    IsHr = true,
                    BorderColor = new SKColor(200, 200, 200)
                });
                _cursorY += 12;
                break;

            case "IMG":
                var alt = element.GetAttribute("alt") ?? "[image]";
                EmitPlainText($"[{alt}]", style);
                break;

            case "TABLE":
                FlushInline();
                LayoutTable(element, style);
                break;

            case "UL":
            case "OL":
                FlushInline();
                LayoutList(element, style, tagName == "OL");
                break;

            default:
                ProcessChildNodes(element, style);
                break;
        }

        if (isBlock)
        {
            FlushInline();
            ApplyMarginBottom(style);
        }

        _styleStack.Pop();
    }

    private void ProcessChildNodes(IElement element, InheritedStyle style)
    {
        foreach (var child in element.ChildNodes)
        {
            if (child is IText textNode)
            {
                var text = NormalizeWhitespace(textNode.Data);
                if (!string.IsNullOrEmpty(text))
                {
                    EmitTextWithLatex(text, style);
                }
            }
            else if (child is IElement childElement)
            {
                LayoutElement(childElement);
            }
        }
    }

    // --- LaTeX Detection ---

    private static bool IsMathTexElement(IElement element)
    {
        return element.TagName.Equals("SPAN", StringComparison.OrdinalIgnoreCase)
               && element.ClassList.Contains("math-tex");
    }

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

        text = WebUtility.HtmlDecode(text);
        text = SanitizeLatex(text);
        return text.Trim();
    }

    // Convert unsupported LaTeX commands to CSharpMath-compatible equivalents
    private static readonly Regex UndersetLimRegex = new(
        @"\\underset\{([^}]*)\}\{\\?lim\}",
        RegexOptions.Compiled);

    private static readonly Regex UndersetRegex = new(
        @"\\underset\{([^}]*)\}\{([^}]*)\}",
        RegexOptions.Compiled);

    private static readonly Regex OversetRegex = new(
        @"\\overset\{([^}]*)\}\{([^}]*)\}",
        RegexOptions.Compiled);

    internal static string SanitizeLatex(string latex)
    {
        // \underset{x\rightarrow0}{lim} -> \lim_{x\rightarrow0}
        latex = UndersetLimRegex.Replace(latex, @"\lim_{$1}");

        // \underset{...}{X} -> X_{...}
        latex = UndersetRegex.Replace(latex, @"{$2}_{$1}");

        // \overset{...}{X} -> X^{...}
        latex = OversetRegex.Replace(latex, @"{$2}^{$1}");

        // \operatorname{...} -> \mathrm{...}
        latex = latex.Replace("\\operatorname", "\\mathrm");

        // \displaystyle -> (just remove, CSharpMath uses LineStyle instead)
        latex = latex.Replace("\\displaystyle", "");

        // \text{} -> \mathrm{}  (CSharpMath supports \text in some contexts)
        // Keep \text as-is since CSharpMath may handle it

        return latex;
    }

    private static readonly Regex LatexPattern = new(
        @"(\$\$.+?\$\$|\$[^$]+?\$|\\\[.+?\\\]|\\\(.+?\\\))",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private void EmitTextWithLatex(string text, InheritedStyle style)
    {
        var segments = LatexPattern.Split(text);

        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment)) continue;

            if (LatexPattern.IsMatch(segment))
            {
                var latex = ExtractLatex(segment);
                if (segment.StartsWith("$$") || segment.StartsWith("\\["))
                    EmitDisplayMath(latex, style);
                else
                    EmitInlineMath(latex, style);
            }
            else
            {
                EmitPlainText(segment, style);
            }
        }
    }

    // --- Text Emission with Font Fallback ---

    private void EmitPlainText(string text, InheritedStyle style)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return;

        var rightEdge = _marginLeft + _contentWidth;
        var lineHeight = style.FontSize * style.LineHeight;

        if (!_inInline)
        {
            _inlineX = _marginLeft + style.PaddingLeft;
            _inInline = true;
            _inlineMaxHeight = 0;
        }

        // Resolve typeface once for the whole text run (check first non-ASCII char)
        var swText = Stopwatch.StartNew();
        var typeface = FontCache.Instance.ResolveForText(text, style.FontFamily, style.Bold, style.Italic);
        var resolvedFamily = typeface.FamilyName;
        using var font = new SKFont(typeface, style.FontSize);
        var spaceWidth = font.MeasureText(" ");
        TextMeasureMs += swText.ElapsedMilliseconds;

        foreach (var word in words)
        {
            var wordWidth = font.MeasureText(word);

            // Word wrap
            if (_inlineX + wordWidth > rightEdge && _inlineX > _marginLeft + style.PaddingLeft)
            {
                _cursorY += Math.Max(lineHeight, _inlineMaxHeight);
                _inlineX = _marginLeft + style.PaddingLeft;
                _inlineMaxHeight = 0;
            }

            _inlineMaxHeight = Math.Max(_inlineMaxHeight, lineHeight);

            Boxes.Add(new LayoutBox
            {
                X = _inlineX,
                Y = _cursorY,
                Width = wordWidth,
                Height = lineHeight,
                Text = word,
                FontSize = style.FontSize,
                FontFamily = resolvedFamily,
                Bold = style.Bold,
                Italic = style.Italic,
                TextColor = style.TextColor,
                TextAlign = style.TextAlign
            });

            _inlineX += wordWidth + spaceWidth;
        }
    }

    // --- Math Emission ---

    private void EmitInlineMath(string latex, InheritedStyle style)
    {
        var swMath = Stopwatch.StartNew();
        var cached = _mathCache.Get(latex, style.FontSize, false);
        MathMeasureMs += swMath.ElapsedMilliseconds;
        MathCount++;

        if (cached == null || cached.HasError || cached.Width <= 0)
        {
            EmitPlainText(latex, style);
            return;
        }

        var mathWidth = cached.Width;
        var mathHeight = Math.Max(cached.Height, style.FontSize * style.LineHeight);

        if (!_inInline)
        {
            _inlineX = _marginLeft + style.PaddingLeft;
            _inInline = true;
            _inlineMaxHeight = 0;
        }

        var rightEdge = _marginLeft + _contentWidth;
        if (_inlineX + mathWidth > rightEdge && _inlineX > _marginLeft + style.PaddingLeft)
        {
            _cursorY += Math.Max(style.FontSize * style.LineHeight, _inlineMaxHeight);
            _inlineX = _marginLeft + style.PaddingLeft;
            _inlineMaxHeight = 0;
        }

        _inlineMaxHeight = Math.Max(_inlineMaxHeight, mathHeight);

        Boxes.Add(new LayoutBox
        {
            X = _inlineX,
            Y = _cursorY,
            Width = mathWidth,
            Height = mathHeight,
            LaTeX = latex,
            IsDisplayMath = false,
            FontSize = style.FontSize,
            TextColor = style.TextColor
        });

        _inlineX += mathWidth + 3;
    }

    private void EmitDisplayMath(string latex, InheritedStyle style)
    {
        FlushInline();

        var displayFontSize = style.FontSize * 1.2f;
        var cached = _mathCache.Get(latex, displayFontSize, true);

        if (cached == null || cached.HasError || cached.Width <= 0)
        {
            EmitPlainText(latex, style);
            return;
        }

        var mathWidth = cached.Width;
        var mathHeight = Math.Max(cached.Height, displayFontSize * 1.5f);

        _cursorY += 6;

        var centerX = _marginLeft + (_contentWidth - mathWidth) / 2;

        Boxes.Add(new LayoutBox
        {
            X = centerX,
            Y = _cursorY,
            Width = mathWidth,
            Height = mathHeight,
            LaTeX = latex,
            IsDisplayMath = true,
            FontSize = displayFontSize,
            TextColor = style.TextColor
        });

        _cursorY += mathHeight + 6;
    }

    private void FlushInline()
    {
        if (_inInline)
        {
            var style = _styleStack.Peek();
            var lineHeight = Math.Max(style.FontSize * style.LineHeight, _inlineMaxHeight);
            _cursorY += lineHeight;
            _inlineX = _marginLeft;
            _inInline = false;
            _inlineMaxHeight = 0;
        }
    }

    // --- Table ---

    private void LayoutTable(IElement table, InheritedStyle parentStyle)
    {
        var rows = table.QuerySelectorAll("tr").ToList();
        if (rows.Count == 0) return;

        var maxCols = rows.Max(r => r.Children.Length);
        if (maxCols == 0) return;

        var colWidth = _contentWidth / maxCols;
        var rowHeight = parentStyle.FontSize * 1.8f;

        foreach (var row in rows)
        {
            var cells = row.Children.ToList();
            var isHeader = cells.Any(c => c.TagName.Equals("TH", StringComparison.OrdinalIgnoreCase));

            for (int col = 0; col < cells.Count && col < maxCols; col++)
            {
                var cell = cells[col];
                var cellStyle = ResolveStyle(cell);
                var cellX = _marginLeft + col * colWidth;

                var bgColor = isHeader
                    ? (cellStyle.BackgroundColor != SKColors.Transparent ? cellStyle.BackgroundColor : new SKColor(52, 152, 219))
                    : cellStyle.BackgroundColor;

                if (bgColor != SKColors.Transparent)
                {
                    Boxes.Add(new LayoutBox
                    {
                        X = cellX,
                        Y = _cursorY,
                        Width = colWidth,
                        Height = rowHeight,
                        BackgroundColor = bgColor
                    });
                }

                Boxes.Add(new LayoutBox
                {
                    X = cellX,
                    Y = _cursorY,
                    Width = colWidth,
                    Height = rowHeight,
                    BorderColor = new SKColor(189, 195, 199),
                    BorderWidth = 0.5f
                });

                var text = cell.TextContent.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    var fontSize = isHeader ? cellStyle.FontSize : parentStyle.FontSize;
                    Boxes.Add(new LayoutBox
                    {
                        X = cellX + 5,
                        Y = _cursorY + (rowHeight - fontSize) / 2,
                        Width = colWidth - 10,
                        Height = fontSize,
                        Text = text,
                        FontSize = fontSize,
                        FontFamily = cellStyle.FontFamily,
                        Bold = isHeader || cellStyle.Bold,
                        Italic = cellStyle.Italic,
                        TextColor = isHeader ? SKColors.White : cellStyle.TextColor,
                        TextAlign = cellStyle.TextAlign
                    });
                }
            }

            _cursorY += rowHeight;
        }

        _cursorY += 4;
    }

    // --- List ---

    private void LayoutList(IElement list, InheritedStyle parentStyle, bool ordered)
    {
        var items = list.QuerySelectorAll(":scope > li").ToList();
        var indent = 20f;
        var counter = 1;

        foreach (var item in items)
        {
            var bullet = ordered ? $"{counter}. " : "\u2022 ";
            counter++;

            var style = ResolveStyle(item);

            var typeface = FontCache.Instance.ResolveForText(bullet, style.FontFamily, style.Bold, style.Italic);
            using var font = new SKFont(typeface, style.FontSize);
            var bulletWidth = font.MeasureText(bullet);

            Boxes.Add(new LayoutBox
            {
                X = _marginLeft + indent,
                Y = _cursorY,
                Width = bulletWidth,
                Height = style.FontSize * style.LineHeight,
                Text = bullet,
                FontSize = style.FontSize,
                FontFamily = style.FontFamily,
                TextColor = style.TextColor
            });

            _inlineX = _marginLeft + indent + bulletWidth;
            _inInline = true;
            _inlineMaxHeight = 0;

            _styleStack.Push(style);
            ProcessChildNodes(item, style with { PaddingLeft = indent + bulletWidth });
            _styleStack.Pop();
            FlushInline();
        }
    }

    // --- Style Resolution ---

    // Cache: skip GetComputedStyle for elements with no styling
    private IWindow? _cachedWindow;
    private bool _windowResolved;

    private ICssStyleDeclaration? GetComputedStyleCached(IElement element)
    {
        // Only call GetComputedStyle if element has style attr, classes, or is styled by tag
        if (!_windowResolved)
        {
            _cachedWindow = element.Owner?.DefaultView;
            _windowResolved = true;
        }
        if (_cachedWindow == null) return null;

        try
        {
            return _cachedWindow.GetComputedStyle(element);
        }
        catch { return null; }
    }

    private InheritedStyle ResolveStyle(IElement element)
    {
        var parent = _styleStack.Peek();
        var style = parent with { };

        var tagName = element.TagName.ToUpperInvariant();

        // Apply tag defaults (no GetComputedStyle needed)
        style = tagName switch
        {
            "H1" => style with { FontSize = 28, Bold = true, MarginTop = 16, MarginBottom = 10 },
            "H2" => style with { FontSize = 22, Bold = true, MarginTop = 14, MarginBottom = 8 },
            "H3" => style with { FontSize = 18, Bold = true, MarginTop = 12, MarginBottom = 6 },
            "H4" => style with { FontSize = 15, Bold = true, MarginTop = 10, MarginBottom = 4 },
            "H5" => style with { FontSize = 13, Bold = true, MarginTop = 8, MarginBottom = 4 },
            "H6" => style with { FontSize = 11, Bold = true, MarginTop = 8, MarginBottom = 4 },
            "P" => style with { MarginTop = 2, MarginBottom = 2 },
            "STRONG" or "B" => style with { Bold = true },
            "EM" or "I" => style with { Italic = true },
            "CODE" or "PRE" => style with { FontFamily = "Courier New", BackgroundColor = new SKColor(245, 245, 245) },
            "BLOCKQUOTE" => style with { PaddingLeft = 20, TextColor = new SKColor(100, 100, 100) },
            _ => style
        };

        // Fast path: parse inline style attribute directly instead of GetComputedStyle
        var inlineStyle = element.GetAttribute("style");
        if (!string.IsNullOrEmpty(inlineStyle))
        {
            style = ApplyInlineStyle(style, inlineStyle);
        }

        // Only use GetComputedStyle for the <body> tag (to pick up body-level font-size)
        // Skip for all other elements — inline styles + tag defaults cover visual properties.
        // The HTML's CSS classes mostly set layout props (float, width, overflow) we don't use.
        if (tagName == "BODY")
        {
            var computed = GetComputedStyleCached(element);
            if (computed != null)
            {
                style = ApplyComputedStyle(style, computed);
            }
        }

        return style;
    }

    private static InheritedStyle ApplyInlineStyle(InheritedStyle style, string inlineStyle)
    {
        // Fast inline style parser — avoids GetComputedStyle entirely
        foreach (var declaration in inlineStyle.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = declaration.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;

            var prop = parts[0].ToLowerInvariant();
            var val = parts[1];

            style = prop switch
            {
                "font-size" => style with { FontSize = ParsePxValue(val, style.FontSize) },
                "font-weight" when val is "bold" or "700" or "800" or "900"
                    => style with { Bold = true },
                "font-style" when val is "italic" or "oblique"
                    => style with { Italic = true },
                "color" => TryApplyColor(style, val, false),
                "background-color" or "background" => TryApplyColor(style, val, true),
                "font-family" => style with { FontFamily = val.Split(',')[0].Trim().Trim('\'', '"') },
                "text-align" => val switch
                {
                    "center" => style with { TextAlign = TextAlign.Center },
                    "right" => style with { TextAlign = TextAlign.Right },
                    _ => style
                },
                "margin-top" => style with { MarginTop = ParsePxValue(val, style.MarginTop) },
                "margin-bottom" => style with { MarginBottom = ParsePxValue(val, style.MarginBottom) },
                "padding-top" => style with { PaddingTop = ParsePxValue(val, style.PaddingTop) },
                "padding-bottom" => style with { PaddingBottom = ParsePxValue(val, style.PaddingBottom) },
                "padding-left" => style with { PaddingLeft = ParsePxValue(val, style.PaddingLeft) },
                "display" when val == "none" => style with { IsHidden = true },
                _ => style
            };
        }
        return style;
    }

    private static InheritedStyle TryApplyColor(InheritedStyle style, string val, bool isBackground)
    {
        var parsed = ParseCssColor(val);
        if (!parsed.HasValue) return style;
        return isBackground
            ? style with { BackgroundColor = parsed.Value }
            : style with { TextColor = parsed.Value };
    }

    private static InheritedStyle ApplyComputedStyle(InheritedStyle style, ICssStyleDeclaration computed)
    {
        var display = computed.GetPropertyValue("display");
        if (display == "none")
            return style with { IsHidden = true };

        var fontSize = computed.GetPropertyValue("font-size");
        if (!string.IsNullOrEmpty(fontSize))
            style = style with { FontSize = ParsePxValue(fontSize, style.FontSize) };

        var fontWeight = computed.GetPropertyValue("font-weight");
        if (!string.IsNullOrEmpty(fontWeight))
            style = style with { Bold = fontWeight is "bold" or "700" or "800" or "900" };

        var fontStyle = computed.GetPropertyValue("font-style");
        if (fontStyle == "italic" || fontStyle == "oblique")
            style = style with { Italic = true };

        var color = computed.GetPropertyValue("color");
        if (!string.IsNullOrEmpty(color))
        {
            var parsed = ParseCssColor(color);
            if (parsed.HasValue) style = style with { TextColor = parsed.Value };
        }

        var bgColor = computed.GetPropertyValue("background-color");
        if (!string.IsNullOrEmpty(bgColor))
        {
            var parsed = ParseCssColor(bgColor);
            if (parsed.HasValue) style = style with { BackgroundColor = parsed.Value };
        }

        var fontFamily = computed.GetPropertyValue("font-family");
        if (!string.IsNullOrEmpty(fontFamily))
        {
            var family = fontFamily.Split(',')[0].Trim().Trim('\'', '"');
            style = style with { FontFamily = family };
        }

        var textAlign = computed.GetPropertyValue("text-align");
        style = textAlign switch
        {
            "center" => style with { TextAlign = TextAlign.Center },
            "right" => style with { TextAlign = TextAlign.Right },
            _ => style
        };

        var marginTop = computed.GetPropertyValue("margin-top");
        if (!string.IsNullOrEmpty(marginTop))
            style = style with { MarginTop = ParsePxValue(marginTop, style.MarginTop) };

        var marginBottom = computed.GetPropertyValue("margin-bottom");
        if (!string.IsNullOrEmpty(marginBottom))
            style = style with { MarginBottom = ParsePxValue(marginBottom, style.MarginBottom) };

        var paddingTop = computed.GetPropertyValue("padding-top");
        if (!string.IsNullOrEmpty(paddingTop))
            style = style with { PaddingTop = ParsePxValue(paddingTop, style.PaddingTop) };

        var paddingBottom = computed.GetPropertyValue("padding-bottom");
        if (!string.IsNullOrEmpty(paddingBottom))
            style = style with { PaddingBottom = ParsePxValue(paddingBottom, style.PaddingBottom) };

        var paddingLeft = computed.GetPropertyValue("padding-left");
        if (!string.IsNullOrEmpty(paddingLeft))
            style = style with { PaddingLeft = ParsePxValue(paddingLeft, style.PaddingLeft) };

        var borderWidth = computed.GetPropertyValue("border-width");
        if (!string.IsNullOrEmpty(borderWidth) && borderWidth != "0px")
            style = style with { BorderWidth = ParsePxValue(borderWidth, 0) };

        var borderColor = computed.GetPropertyValue("border-color");
        if (!string.IsNullOrEmpty(borderColor))
        {
            var parsed = ParseCssColor(borderColor);
            if (parsed.HasValue) style = style with { BorderColor = parsed.Value };
        }

        return style;
    }

    private void ApplyMarginTop(InheritedStyle style)
    {
        _cursorY += style.MarginTop;
        if (style.BackgroundColor != SKColors.Transparent && style.PaddingTop > 0)
        {
            Boxes.Add(new LayoutBox
            {
                X = _marginLeft,
                Y = _cursorY,
                Width = _contentWidth,
                Height = style.PaddingTop,
                BackgroundColor = style.BackgroundColor
            });
        }
        _cursorY += style.PaddingTop;
    }

    private void ApplyMarginBottom(InheritedStyle style)
    {
        _cursorY += style.PaddingBottom + style.MarginBottom;
    }

    // --- Helpers ---

    private static bool IsBlockElement(string tag) => tag is
        "DIV" or "P" or "H1" or "H2" or "H3" or "H4" or "H5" or "H6" or
        "SECTION" or "ARTICLE" or "MAIN" or "HEADER" or "FOOTER" or "NAV" or
        "ASIDE" or "BLOCKQUOTE" or "PRE" or "FIGURE" or "FIGCAPTION" or
        "ADDRESS" or "DETAILS" or "SUMMARY" or "FORM" or "FIELDSET" or
        "DL" or "DD" or "DT" or "LI";

    private static float ParsePxValue(string value, float fallback)
    {
        value = value.Trim();
        if (value.EndsWith("px"))
            value = value[..^2];
        else if (value.EndsWith("pt"))
        {
            value = value[..^2];
            if (float.TryParse(value, out var pt)) return pt * 1.333f;
        }
        else if (value.EndsWith("em"))
        {
            value = value[..^2];
            if (float.TryParse(value, out var em)) return em * fallback;
        }
        else if (value.EndsWith("rem"))
        {
            value = value[..^3];
            if (float.TryParse(value, out var rem)) return rem * 16;
        }
        else if (value.EndsWith("mm"))
        {
            value = value[..^2];
            if (float.TryParse(value, out var mm)) return mm * 2.83465f;
        }

        return float.TryParse(value, out var px) ? px : fallback;
    }

    private static SKColor? ParseCssColor(string color)
    {
        color = color.Trim();

        if (color.StartsWith('#') && SKColor.TryParse(color, out var hex))
            return hex;

        if (color.StartsWith("rgb"))
        {
            var parts = color.Replace("rgba(", "").Replace("rgb(", "").Replace(")", "")
                .Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 3
                && byte.TryParse(parts[0], out var r)
                && byte.TryParse(parts[1], out var g)
                && byte.TryParse(parts[2], out var b))
            {
                byte a = 255;
                if (parts.Length >= 4 && float.TryParse(parts[3], out var af))
                    a = (byte)(af * 255);
                if (a == 0) return SKColors.Transparent;
                return new SKColor(r, g, b, a);
            }
        }

        return color.ToLowerInvariant() switch
        {
            "transparent" => SKColors.Transparent,
            "black" => SKColors.Black,
            "white" => SKColors.White,
            "red" => SKColors.Red,
            "green" => SKColors.Green,
            "blue" => SKColors.Blue,
            "gray" or "grey" => SKColors.Gray,
            "yellow" => SKColors.Yellow,
            "orange" => SKColors.Orange,
            _ => null
        };
    }

    private static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return Regex.Replace(text, @"\s+", " ");
    }
}

// --- Page Settings ---

public class PdfPageSettings
{
    public PageSize PageSize { get; set; } = PageSize.A4;
    public bool Landscape { get; set; }
    public int MarginMm { get; set; } = 10;
}

public enum PageSize
{
    A3,
    A4,
    A5,
    Letter,
    Legal
}

// --- Inherited Style ---

public record InheritedStyle
{
    public float FontSize { get; init; } = 12;
    public string FontFamily { get; init; } = "Arial";
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public float LineHeight { get; init; } = 1.4f;
    public SKColor TextColor { get; init; } = SKColors.Black;
    public SKColor BackgroundColor { get; init; } = SKColors.Transparent;
    public SKColor BorderColor { get; init; } = SKColor.Empty;
    public float BorderWidth { get; init; }
    public TextAlign TextAlign { get; init; } = TextAlign.Left;
    public float MarginTop { get; init; }
    public float MarginBottom { get; init; }
    public float PaddingTop { get; init; }
    public float PaddingBottom { get; init; }
    public float PaddingLeft { get; init; }
    public bool IsHidden { get; init; }
}
