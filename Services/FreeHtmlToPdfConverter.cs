using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using CSharpMath.SkiaSharp;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace HtmlToPdf.Services;

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

        var stylesheet = StyleSheetParser.Parse(document);
        var (textFontSize, eqFontSize) = ParseBodyFontSizes(document, stylesheet);
        OverrideBodyFontSize(document, textFontSize);
        var mathCache = new MathCache();
        mathCache.PreMeasure(document, eqFontSize);
        var mathPreMeasureTime = sw.ElapsedMilliseconds - parseTime;

        var layoutBoxes = LayoutDocument(document, pageSize, margin, mathCache, stylesheet, textFontSize, eqFontSize);
        var layoutTime = sw.ElapsedMilliseconds - parseTime - mathPreMeasureTime;

        var pdf = RenderToPdf(layoutBoxes, pageSize, margin, mathCache);
        var renderTime = sw.ElapsedMilliseconds - parseTime - mathPreMeasureTime - layoutTime;

        _logger.LogInformation(
            "Breakdown: parse={ParseMs}ms, mathPreMeasure={MathPreMs}ms, layout={LayoutMs}ms ({BoxCount} boxes), render={RenderMs}ms, total={TotalMs}ms",
            parseTime, mathPreMeasureTime, layoutTime, layoutBoxes.Count, renderTime, sw.ElapsedMilliseconds);

        return pdf;
    }

    private static (float textSize, float eqSize) ParseBodyFontSizes(IDocument document, StyleSheetParser stylesheet)
    {
        // Check stylesheet body rule
        var bodyRule = stylesheet.GetTagRules("body");
        float fontSize = 12f;
        if (bodyRule != null)
        {
            foreach (var decl in bodyRule.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = decl.Split(':', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && parts[0].Equals("font-size", StringComparison.OrdinalIgnoreCase))
                    fontSize = LayoutEngine.ParsePxValue(parts[1], 12f);
            }
        }
        // Inline style overrides
        var body = document.Body;
        if (body == null) return (fontSize, fontSize);
        var style = body.GetAttribute("style");
        if (style != null)
        {
            foreach (var decl in style.Split(';'))
            {
                var parts = decl.Split(':', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && parts[0].Equals("font-size", StringComparison.OrdinalIgnoreCase))
                    fontSize = LayoutEngine.ParsePxValue(parts[1], fontSize);
            }
        }

        // data-textfontsize and data-eqfontsize override
        // These values are in mm but the rendering convention treats them closer to pt
        // to achieve compact exam paper layout matching the target PDF output
        var textSize = fontSize;
        var eqSize = fontSize;
        var dataText = body.GetAttribute("data-textfontsize");
        if (dataText != null && float.TryParse(dataText, System.Globalization.CultureInfo.InvariantCulture, out var dtVal))
            textSize = dtVal * 1.47f; // matches target PDF density (3.8 * 1.47 ≈ 5.6pt)

        var dataEq = body.GetAttribute("data-eqfontsize");
        if (dataEq != null && float.TryParse(dataEq, System.Globalization.CultureInfo.InvariantCulture, out var deVal))
            eqSize = deVal * 1.47f;

        // If data attributes not set, use CSS font-size
        if (dataText == null) textSize = fontSize;
        if (dataEq == null) eqSize = fontSize;

        return (textSize, eqSize);
    }

    /// <summary>
    /// Replace body's inline font-size with the data-textfontsize value
    /// so that style="font-size:5mm" doesn't override the compact data attribute.
    /// </summary>
    private static void OverrideBodyFontSize(IDocument document, float textFontSize)
    {
        var body = document.Body;
        if (body == null || textFontSize >= 12f) return;

        // Scale ratio: target font size / original body font size (5mm = 14.2pt)
        var ratio = textFontSize / 14.2f;

        // Override body font-size
        var existingStyle = body.GetAttribute("style") ?? "";
        var cleaned = Regex.Replace(existingStyle, @"font-size\s*:[^;]+;?", "");
        body.SetAttribute("style", $"font-size:{textFontSize:F1}pt;{cleaned}");

        // Scale inline font-size values in child elements (banner, subtitles, etc.)
        foreach (var el in body.QuerySelectorAll("[style]"))
        {
            var style = el.GetAttribute("style") ?? "";
            if (!style.Contains("font-size")) continue;

            var scaled = Regex.Replace(style, @"font-size\s*:\s*([\d.]+)\s*(mm|pt)", m =>
            {
                if (float.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var val))
                {
                    var ptVal = m.Groups[2].Value == "mm" ? val * 2.83465f : val;
                    return $"font-size:{ptVal * ratio:F1}pt";
                }
                return m.Value;
            });
            el.SetAttribute("style", scaled);
        }
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
        var stylesheet = StyleSheetParser.Parse(document);
        var (textFontSize, eqFontSize) = ParseBodyFontSizes(document, stylesheet);
        OverrideBodyFontSize(document, textFontSize);
        var mathCache = new MathCache();
        mathCache.PreMeasure(document, eqFontSize);
        var layoutBoxes = LayoutDocument(document, pageSize, margin, mathCache, stylesheet, textFontSize, eqFontSize);

        return RenderToPdf(layoutBoxes, pageSize, margin, mathCache);
    }

    private static async Task<IDocument> ParseHtml(string html)
    {
        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        return await context.OpenAsync(req => req.Content(html));
    }

    private static async Task<IDocument> ParseUrl(string url)
    {
        var config = Configuration.Default.WithDefaultLoader();
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

    private List<LayoutBox> LayoutDocument(IDocument document, SKSize pageSize, float margin, MathCache mathCache, StyleSheetParser stylesheet, float textFontSize, float eqFontSize)
    {
        var contentWidth = pageSize.Width - margin * 2;
        var engine = new LayoutEngine(contentWidth, margin, mathCache, pageSize.Height, stylesheet, textFontSize, eqFontSize);

        var body = document.Body;
        if (body == null) return engine.Boxes;

        engine.LayoutElement(body);
        return engine.Boxes;
    }

    // --- PDF Renderer (P0: reuse SKPaint, SKPicture for math, P3: lower DPI) ---

    private byte[] RenderToPdf(List<LayoutBox> boxes, SKSize pageSize, float margin, MathCache mathCache)
    {
        using var memStream = new MemoryStream();
        using var wstream = new SKManagedWStream(memStream);

        // P3: Lower DPI/quality for smaller PDF
        var metadata = new SKDocumentPdfMetadata
        {
            Title = "Converted PDF",
            Creation = DateTime.Now,
            RasterDpi = 150,
            EncodingQuality = 80
        };

        using var pdfDoc = SKDocument.CreatePdf(wstream, metadata);

        var contentHeight = pageSize.Height - margin * 2;
        var pages = PaginateBoxes(boxes, contentHeight, margin);

        // P0: Reuse paint objects across all boxes
        using var bgPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var borderPaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
        using var textPaint = new SKPaint { IsAntialias = true };
        using var hrPaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };

        long mathDrawMs = 0, textDrawMs = 0;
        int mathDrawCount = 0, textDrawCount = 0;
        var swR = new Stopwatch();

        foreach (var page in pages)
        {
            using var canvas = pdfDoc.BeginPage(pageSize.Width, pageSize.Height);
            foreach (var box in page)
            {
                if (!string.IsNullOrEmpty(box.LaTeX)) { swR.Restart(); }
                else if (!string.IsNullOrEmpty(box.Text)) { swR.Restart(); }

                RenderBox(canvas, box, mathCache, bgPaint, borderPaint, textPaint, hrPaint);

                if (!string.IsNullOrEmpty(box.LaTeX)) { mathDrawMs += swR.ElapsedMilliseconds; mathDrawCount++; }
                else if (!string.IsNullOrEmpty(box.Text)) { textDrawMs += swR.ElapsedMilliseconds; textDrawCount++; }
            }
            pdfDoc.EndPage();
        }

        _logger.LogInformation(
            "  Render breakdown: mathDraw={MathMs}ms ({MathCount} boxes), textDraw={TextMs}ms ({TextCount} boxes), pages={PageCount}",
            mathDrawMs, mathDrawCount, textDrawMs, textDrawCount, pages.Count);

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

    private void RenderBox(SKCanvas canvas, LayoutBox box, MathCache mathCache,
        SKPaint bgPaint, SKPaint borderPaint, SKPaint textPaint, SKPaint hrPaint)
    {
        // Background
        if (box.BackgroundColor != SKColor.Empty && box.BackgroundColor != SKColors.Transparent)
        {
            bgPaint.Color = box.BackgroundColor;
            canvas.DrawRect(box.X, box.Y, box.Width, box.Height, bgPaint);
        }

        // Border
        if (box.BorderWidth > 0 && box.BorderColor != SKColor.Empty)
        {
            borderPaint.Color = box.BorderColor;
            borderPaint.StrokeWidth = box.BorderWidth;
            canvas.DrawRect(box.X, box.Y, box.Width, box.Height, borderPaint);
        }

        // Text — use HarfBuzz shaping for correct Bengali/Indic rendering
        if (!string.IsNullOrEmpty(box.Text))
        {
            var typeface = FontCache.Instance.ResolveForText(box.Text, box.FontFamily ?? "Arial", box.Bold, box.Italic);
            textPaint.Color = box.TextColor;
            textPaint.Typeface = typeface;
            textPaint.TextSize = box.FontSize;

            var textX = box.X;
            if (box.TextAlign == TextAlign.Center)
            {
                var textWidth = textPaint.MeasureText(box.Text);
                textX = box.X + (box.Width - textWidth) / 2;
            }
            else if (box.TextAlign == TextAlign.Right)
            {
                var textWidth = textPaint.MeasureText(box.Text);
                textX = box.X + box.Width - textWidth;
            }

            // Check if text has non-Latin characters that need shaping
            bool needsShaping = false;
            foreach (var ch in box.Text)
            {
                if (ch > 127) { needsShaping = true; break; }
            }

            if (needsShaping)
            {
                // Create shaper from the EXACT typeface used for rendering
                // to ensure glyph ID consistency
                using var shaper = new SkiaSharp.HarfBuzz.SKShaper(typeface);
                canvas.DrawShapedText(shaper, box.Text, textX, box.Y + box.FontSize, textPaint);
            }
            else
            {
                canvas.DrawText(box.Text, textX, box.Y + box.FontSize, new SKFont(typeface, box.FontSize), textPaint);
            }
        }

        // LaTeX math — cached bounds, MathPainter only for draw
        if (!string.IsNullOrEmpty(box.LaTeX))
        {
            var cached = mathCache.Get(box.LaTeX, box.FontSize, box.IsDisplayMath);

            if (cached != null && !cached.HasError && cached.Width > 0)
            {
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
                painter.Draw(canvas, box.X - cached.BoundsX, box.Y + box.FontSize);
            }
            else
            {
                var font = FontCache.Instance.GetFont("Arial", box.FontSize * 0.8f, false, false);
                textPaint.Color = box.TextColor;
                canvas.DrawText(box.LaTeX, box.X, box.Y + box.FontSize, font, textPaint);
            }
        }

        // Horizontal rule
        if (box.IsHr)
        {
            hrPaint.Color = box.BorderColor != SKColor.Empty ? box.BorderColor : new SKColor(200, 200, 200);
            var hrY = box.Y + box.Height / 2;
            canvas.DrawLine(box.X, hrY, box.X + box.Width, hrY, hrPaint);
        }

        // Inline image (data:image/png;base64)
        if (box.ImageData != null)
        {
            using var bitmap = SKBitmap.Decode(box.ImageData);
            if (bitmap != null)
            {
                var dest = new SKRect(box.X, box.Y, box.X + box.Width, box.Y + box.Height);
                canvas.DrawBitmap(bitmap, dest);
            }
        }

        // Border-left line (column divider)
        if (box.BorderLeftLine)
        {
            borderPaint.Color = box.BorderColor != SKColor.Empty ? box.BorderColor : SKColors.Black;
            borderPaint.StrokeWidth = box.BorderWidth > 0 ? box.BorderWidth : 1;
            canvas.DrawLine(box.X, box.Y, box.X, box.Y + box.Height, borderPaint);
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
    public bool BorderLeftLine { get; init; }
    public byte[]? ImageData { get; init; }
}

public enum TextAlign { Left, Center, Right }

// --- Layout Engine (with float, inline-block, page-break, margin collapsing) ---

public class LayoutEngine
{
    private float _contentWidth;
    private float _marginLeft;
    private readonly float _pageHeight;
    private float _cursorY;

    public List<LayoutBox> Boxes { get; } = new();

    // Inline state
    private float _inlineX;
    private bool _inInline;
    private float _inlineMaxHeight;

    // Inline-line alignment tracking (for text-align: center / right)
    private int _inlineLineStartIdx;
    private float _inlineLineLeftEdge;
    private float _inlineLineAvailableWidth;
    private TextAlign _inlineLineAlignment;

    // Float state (P0 accuracy: CSS float layout)
    private float _floatX;
    private float _floatRowY;
    private bool _inFloatRow;

    // Margin collapsing (P4 accuracy)
    private float _lastMarginBottom;

    private readonly Stack<InheritedStyle> _styleStack = new();
    private readonly MathCache _mathCache;
    private readonly StyleSheetParser _stylesheet;
    private readonly float _textFontSize;
    private readonly float _eqFontSize;

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public LayoutEngine(float contentWidth, float marginLeft, MathCache mathCache, float pageHeight, StyleSheetParser stylesheet, float textFontSize = 12f, float eqFontSize = 12f)
    {
        _contentWidth = contentWidth;
        _marginLeft = marginLeft;
        _cursorY = marginLeft;
        _inlineX = marginLeft;
        _floatX = marginLeft;
        _pageHeight = pageHeight;
        _mathCache = mathCache;
        _stylesheet = stylesheet;
        _textFontSize = textFontSize;
        _eqFontSize = eqFontSize;
        // Use compact line height when font size is small (exam paper mode)
        var lh = textFontSize < 8f ? 1.05f : 1.2f;
        _styleStack.Push(new InheritedStyle { FontSize = textFontSize, LineHeight = lh });
    }

    public void LayoutElement(IElement element)
    {
        var tagName = element.TagName.ToUpperInvariant();

        if (tagName is "SCRIPT" or "STYLE" or "META" or "LINK" or "HEAD" or "TITLE" or "NOSCRIPT")
            return;

        var style = ResolveStyle(element);

        if (style.IsHidden)
            return;

        // P3 accuracy: page-break-before
        if (style.PageBreakBefore)
        {
            FlushInline();
            FlushFloatRow();
            var contentHeight = _pageHeight - _marginLeft * 2;
            var currentPage = (int)((_cursorY - _marginLeft) / contentHeight);
            _cursorY = _marginLeft + (currentPage + 1) * (contentHeight + _marginLeft * 2);
        }

        _styleStack.Push(style);

        // Handle <span class="math-tex"> as inline LaTeX
        if (IsMathTexElement(element))
        {
            var latex = ExtractLatex(element.TextContent);
            if (!string.IsNullOrWhiteSpace(latex))
            {
                // Use eqFontSize if set, overriding CSS .math-tex font-size
                var mathStyle = _eqFontSize > 0 && _eqFontSize < style.FontSize
                    ? style with { FontSize = _eqFontSize }
                    : style;
                EmitInlineMath(latex, mathStyle);
            }
            _styleStack.Pop();
            return;
        }

        // Auto two-column layout for questionPages container
        if (element.ClassList.Contains("questionPages"))
        {
            FlushInline();
            LayoutTwoColumnQuestions(element, style);
            _styleStack.Pop();
            return;
        }

        // P0 accuracy: CSS clear:both
        if (style.ClearBoth)
        {
            FlushInline();
            FlushFloatRow();
        }

        // P0 accuracy: CSS float:left with width
        if (style.FloatLeft && style.WidthPercent > 0)
        {
            FlushInline();
            LayoutFloatedElement(element, style);
            _styleStack.Pop();
            return;
        }

        // P1 accuracy: display:inline-block with width
        if (style.DisplayInlineBlock && style.WidthPercent > 0)
        {
            LayoutInlineBlockElement(element, style);
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
                // Skip BR between inline-block siblings (e.g., <br/> between <li> items)
                if (_inlineBlockX > _marginLeft)
                    break;
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
                EmitImage(element, style);
                break;

            case "TABLE":
                FlushInline();
                LayoutTable(element, style);
                break;

            case "UL":
            case "OL":
                FlushInline();
                // Check if list items use inline-block layout (e.g., .list-group-item)
                // If so, skip bullet generation and let normal layout handle them
                if (HasInlineBlockChildren(element))
                {
                    _inlineBlockX = _marginLeft;
                    _inlineBlockRowMaxHeight = 0;
                    ProcessChildNodes(element, style);
                    // Flush final inline-block row
                    if (_inlineBlockX > _marginLeft)
                    {
                        _cursorY = _inlineBlockRowY + _inlineBlockRowMaxHeight;
                        _inlineBlockX = _marginLeft;
                    }
                }
                else
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

    // --- Two-column question layout ---

    private void LayoutTwoColumnQuestions(IElement container, InheritedStyle style)
    {
        // Collect all .question elements
        var questions = container.QuerySelectorAll(".question").ToList();
        if (questions.Count == 0)
        {
            ProcessChildNodes(container, style);
            return;
        }

        var pageMargin = _marginLeft;
        var columnGap = 8f;
        var columnWidth = (_contentWidth - columnGap) / 2f;
        // First page has less space (banner already occupies top area)
        var firstPageContentHeight = _pageHeight - _cursorY - pageMargin;
        var fullPageContentHeight = _pageHeight - pageMargin * 2;

        // Layout each question into temporary boxes to measure heights
        var questionBoxGroups = new List<(List<LayoutBox> boxes, float height)>();

        // Use compact style for two-column mode
        var compactStyle = style with { LineHeight = 1.1f, MarginBottom = 0f, MarginTop = 0f };
        float totalHeight = 0;

        foreach (var q in questions)
        {
            var subEngine = new LayoutEngine(columnWidth, 0, _mathCache, _pageHeight, _stylesheet, _textFontSize, _eqFontSize);
            subEngine._styleStack.Push(compactStyle);
            subEngine.LayoutElement(q);

            var height = subEngine._cursorY + 1f;
            totalHeight += height;
            questionBoxGroups.Add((subEngine.Boxes, height));
        }

        var avgHeight = totalHeight / questions.Count;
        System.Console.WriteLine($"  [TwoCol] {questions.Count} questions, avgHeight={avgHeight:F1}pt, totalHeight={totalHeight:F0}pt, firstPageCap={firstPageContentHeight:F0}pt, fullPageCap={fullPageContentHeight:F0}pt");

        // Place questions into two columns, filling left then right, page by page
        var colX = new[] { _marginLeft, _marginLeft + columnWidth + columnGap };
        var colY = new[] { _cursorY, _cursorY };
        int col = 0;
        var pageStartY = _cursorY;
        var currentPageHeight = firstPageContentHeight;
        int pageNum = 0;

        foreach (var (qBoxes, qHeight) in questionBoxGroups)
        {
            // Check if question fits in current column
            if (colY[col] + qHeight > pageStartY + currentPageHeight)
            {
                if (col == 0)
                {
                    // Move to right column
                    col = 1;
                    colY[1] = pageStartY;
                }
                else
                {
                    // Both columns full — emit column divider and start new page
                    var dividerX = _marginLeft + columnWidth + columnGap / 2;
                    Boxes.Add(new LayoutBox
                    {
                        X = dividerX, Y = pageStartY, Width = 0,
                        Height = currentPageHeight,
                        BorderLeftLine = true,
                        BorderColor = SKColors.Black, BorderWidth = 0.5f
                    });

                    pageNum++;
                    pageStartY += currentPageHeight + pageMargin * 2;
                    currentPageHeight = fullPageContentHeight; // subsequent pages use full height
                    col = 0;
                    colY[0] = pageStartY;
                    colY[1] = pageStartY;
                }

                // Re-check after switch
                if (colY[col] + qHeight > pageStartY + currentPageHeight && col == 0)
                {
                    col = 1;
                    colY[1] = pageStartY;
                }
            }

            var offsetX = colX[col];
            var offsetY = colY[col];

            foreach (var box in qBoxes)
            {
                Boxes.Add(box with
                {
                    X = box.X + offsetX,
                    Y = box.Y + offsetY
                });
            }

            colY[col] += qHeight;
        }

        // Final column divider
        var lastDividerX = _marginLeft + columnWidth + columnGap / 2;
        var lastPageUsed = Math.Max(colY[0], colY[1]) - pageStartY;
        if (lastPageUsed > 0)
        {
            Boxes.Add(new LayoutBox
            {
                X = lastDividerX, Y = pageStartY, Width = 0,
                Height = Math.Min(lastPageUsed, currentPageHeight),
                BorderLeftLine = true,
                BorderColor = SKColors.Black, BorderWidth = 0.5f
            });
        }

        _cursorY = Math.Max(colY[0], colY[1]);
    }

    // --- Float layout (P0 accuracy) ---

    private void LayoutFloatedElement(IElement element, InheritedStyle style)
    {
        var floatWidth = _contentWidth * style.WidthPercent / 100f;
        // Subtract margin-right from effective width
        var effectiveWidth = floatWidth - (style.MarginRight > 0 ? style.MarginRight : 0);

        if (!_inFloatRow)
        {
            _floatX = _marginLeft;
            _floatRowY = _cursorY;
            _inFloatRow = true;
        }

        // Emit border-left line if set (column divider)
        if (style.BorderLeftLine)
        {
            Boxes.Add(new LayoutBox
            {
                X = _floatX,
                Y = _floatRowY,
                Width = 0,
                Height = style.Height > 0 ? style.Height : (_pageHeight - _floatRowY - _marginLeft),
                BorderLeftLine = true,
                BorderColor = SKColors.Black,
                BorderWidth = 1
            });
        }

        // Save ALL state — float state must be isolated per nesting level
        var savedX = _inlineX;
        var savedCursorY = _cursorY;
        var savedContentWidth = _contentWidth;
        var savedMarginLeft = _marginLeft;
        var savedFloatX = _floatX;
        var savedFloatRowY = _floatRowY;
        var savedInFloatRow = _inFloatRow;

        _cursorY = _floatRowY + style.MarginTop;
        _inlineX = _floatX + style.PaddingLeft;
        _inFloatRow = false;

        SetContentContext(_floatX + style.PaddingLeft, effectiveWidth - style.PaddingLeft - style.PaddingRight);

        _styleStack.Push(style);
        ProcessChildNodes(element, style);
        FlushInline();
        _styleStack.Pop();

        var floatBottom = _cursorY;

        // Restore parent float state and advance
        SetContentContext(savedMarginLeft, savedContentWidth);
        _floatX = savedFloatX + floatWidth;
        _floatRowY = savedFloatRowY;
        _inFloatRow = savedInFloatRow;
        _cursorY = Math.Max(savedCursorY, floatBottom);
    }

    private void FlushFloatRow()
    {
        if (_inFloatRow)
        {
            _floatX = _marginLeft;
            _inFloatRow = false;
        }
    }

    private void SetContentContext(float marginLeft, float contentWidth)
    {
        _marginLeft = marginLeft;
        _contentWidth = contentWidth;
    }

    // --- Inline-block layout (P1 accuracy) ---

    private float _inlineBlockX;
    private float _inlineBlockRowY;
    private float _inlineBlockRowMaxHeight;

    private void LayoutInlineBlockElement(IElement element, InheritedStyle style)
    {
        var blockWidth = _contentWidth * style.WidthPercent / 100f;
        var rightEdge = _marginLeft + _contentWidth;

        // Wrap to next row if needed
        if (_inlineBlockX + blockWidth > rightEdge + 1f)
        {
            _cursorY = _inlineBlockRowY + _inlineBlockRowMaxHeight;
            _inlineBlockX = _marginLeft;
            _inlineBlockRowMaxHeight = 0;
        }

        if (_inlineBlockX <= _marginLeft)
        {
            _inlineBlockRowY = _cursorY;
            _inlineBlockX = _marginLeft;
            _inlineBlockRowMaxHeight = 0;
        }

        // Save ALL state — isolate children's float/inline from parent
        var savedCursorY = _cursorY;
        var savedMarginLeft = _marginLeft;
        var savedContentWidth = _contentWidth;
        var savedFloatX = _floatX;
        var savedFloatRowY = _floatRowY;
        var savedInFloatRow = _inFloatRow;

        _cursorY = _inlineBlockRowY;
        _inlineX = _inlineBlockX;
        _inFloatRow = false; // Children start with fresh float state

        SetContentContext(_inlineBlockX, blockWidth);

        _styleStack.Push(style);
        ProcessChildNodes(element, style);
        FlushInline();
        FlushFloatRow();
        _styleStack.Pop();

        var blockHeight = _cursorY - _inlineBlockRowY;
        _inlineBlockRowMaxHeight = Math.Max(_inlineBlockRowMaxHeight, blockHeight);

        // Restore parent state
        SetContentContext(savedMarginLeft, savedContentWidth);
        _floatX = savedFloatX;
        _floatRowY = savedFloatRowY;
        _inFloatRow = savedInFloatRow;
        _inlineBlockX += blockWidth;
        _cursorY = Math.Max(savedCursorY, _inlineBlockRowY + _inlineBlockRowMaxHeight);
    }

    private void ProcessChildNodes(IElement element, InheritedStyle style)
    {
        foreach (var child in element.ChildNodes)
        {
            if (child is IText textNode)
            {
                var text = NormalizeWhitespace(textNode.Data);
                if (!string.IsNullOrEmpty(text))
                    EmitTextWithLatex(text, style);
            }
            else if (child is IElement childElement)
            {
                LayoutElement(childElement);
            }
        }
    }

    // --- LaTeX ---

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

    private static readonly Regex UndersetLimRegex = new(
        @"\\underset\{([^}]*)\}\{\\?lim\}", RegexOptions.Compiled);
    private static readonly Regex UndersetRegex = new(
        @"\\underset\{([^}]*)\}\{([^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex OversetRegex = new(
        @"\\overset\{([^}]*)\}\{([^}]*)\}", RegexOptions.Compiled);

    // CSharpMath 1.0.0-pre.1 has a matrix column-alignment bug: when two or more
    // columns both contain cells of different widths (e.g., [44 -1 / -31 1]) the
    // right-hand content drifts past the column boundary, causing the last column
    // to overlap the closing delimiter. Padding each row with a trailing empty
    // cell forces CSharpMath to recompute column widths correctly.
    private static readonly Regex MatrixEnvRegex = new(
        @"\\begin\{(bmatrix|pmatrix|vmatrix|Bmatrix|Vmatrix|matrix)\}(.*?)\\end\{\1\}",
        RegexOptions.Compiled | RegexOptions.Singleline);

    internal static string SanitizeLatex(string latex)
    {
        latex = UndersetLimRegex.Replace(latex, @"\lim_{$1}");
        latex = UndersetRegex.Replace(latex, @"{$2}_{$1}");
        latex = OversetRegex.Replace(latex, @"{$2}^{$1}");
        latex = latex.Replace("\\operatorname", "\\mathrm");
        latex = latex.Replace("\\displaystyle", "");
        latex = PadMatrixColumns(latex);
        return latex;
    }

    private static string PadMatrixColumns(string latex)
    {
        return MatrixEnvRegex.Replace(latex, m =>
        {
            var env = m.Groups[1].Value;
            var body = m.Groups[2].Value;
            // Split body on \\, the row separator. Append & to each non-empty row.
            var rows = body.Split(new[] { "\\\\" }, StringSplitOptions.None);
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                if (!string.IsNullOrWhiteSpace(row))
                    rows[i] = row + "&";
            }
            var newBody = string.Join("\\\\", rows);
            return $"\\begin{{{env}}}{newBody}\\end{{{env}}}";
        });
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

    // --- Text ---

    // --- Image Emission ---

    private void EmitImage(IElement element, InheritedStyle style)
    {
        var src = element.GetAttribute("src");
        if (string.IsNullOrEmpty(src))
        {
            var alt = element.GetAttribute("alt");
            if (!string.IsNullOrEmpty(alt)) EmitPlainText($"[{alt}]", style);
            return;
        }

        // Handle data:image/png;base64,... URLs
        if (src.StartsWith("data:image/"))
        {
            var commaIdx = src.IndexOf(',');
            if (commaIdx < 0) return;

            try
            {
                var base64 = src[(commaIdx + 1)..];
                var bytes = Convert.FromBase64String(base64);
                using var bitmap = SKBitmap.Decode(bytes);
                if (bitmap == null) return;

                // Scale image to fit inline, matching font height
                var maxHeight = style.FontSize * style.LineHeight * 1.5f;
                var scale = maxHeight / bitmap.Height;
                var imgWidth = bitmap.Width * scale;
                var imgHeight = bitmap.Height * scale;

                if (!_inInline)
                {
                    _inlineX = _marginLeft + style.PaddingLeft;
                    _inInline = true;
                    _inlineMaxHeight = 0;
                    BeginInlineLine(style);
                }

                var rightEdge = _marginLeft + _contentWidth;
                if (_inlineX + imgWidth > rightEdge && _inlineX > _marginLeft + style.PaddingLeft)
                {
                    ApplyInlineLineAlignment();
                    _cursorY += Math.Max(style.FontSize * style.LineHeight, _inlineMaxHeight);
                    _inlineX = _marginLeft + style.PaddingLeft;
                    _inlineMaxHeight = 0;
                    BeginInlineLine(style);
                }

                _inlineMaxHeight = Math.Max(_inlineMaxHeight, imgHeight);

                Boxes.Add(new LayoutBox
                {
                    X = _inlineX,
                    Y = _cursorY,
                    Width = imgWidth,
                    Height = imgHeight,
                    ImageData = bytes
                });

                _inlineX += imgWidth + 2;
            }
            catch
            {
                var alt = element.GetAttribute("alt");
                if (!string.IsNullOrEmpty(alt)) EmitPlainText($"[{alt}]", style);
            }
        }
        else
        {
            var alt = element.GetAttribute("alt") ?? "image";
            if (!string.IsNullOrEmpty(alt)) EmitPlainText($"[{alt}]", style);
        }
    }

    private void EmitPlainText(string text, InheritedStyle style)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return;

        var leftEdge = _marginLeft + style.PaddingLeft;
        var rightEdge = _marginLeft + _contentWidth;
        var lineHeight = style.FontSize * style.LineHeight;

        if (!_inInline)
        {
            _inlineX = leftEdge;
            _inInline = true;
            _inlineMaxHeight = 0;
            BeginInlineLine(style);
        }

        // Use HarfBuzz for measurement — matches shaped render widths exactly
        var typeface = FontCache.Instance.ResolveForText(text, style.FontFamily, style.Bold, style.Italic);
        var resolvedFamily = typeface.FamilyName;
        using var font = new SKFont(typeface, style.FontSize);
        var shaper = FontCache.Instance.GetShaper(typeface);

        var spaceResult = shaper.Shape(" ", font);
        var spaceWidth = spaceResult.Width > 0 ? spaceResult.Width : font.MeasureText(" ");

        foreach (var word in words)
        {
            var shapedResult = shaper.Shape(word, font);
            var wordWidth = shapedResult.Width > 0 ? shapedResult.Width : font.MeasureText(word);

            if (_inlineX + wordWidth > rightEdge && _inlineX > leftEdge)
            {
                // Soft wrap: shift the completed line, then start a new one
                ApplyInlineLineAlignment();
                _cursorY += Math.Max(lineHeight, _inlineMaxHeight);
                _inlineX = leftEdge;
                _inlineMaxHeight = 0;
                BeginInlineLine(style);
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
                TextAlign = TextAlign.Left
            });

            _inlineX += wordWidth + spaceWidth;
        }
    }

    // --- Math ---

    private void EmitInlineMath(string latex, InheritedStyle style)
    {
        var cached = _mathCache.Get(latex, style.FontSize, false);

        if (cached == null || cached.HasError || cached.Width <= 0)
        {
            EmitPlainText(latex, style);
            return;
        }

        var mathWidth = cached.Width;
        // CSharpMath bounds.Y is the top-of-ink relative to baseline (negative when ink extends
        // above baseline, which is the normal case). Height is the full ink box height.
        var mathAscent = Math.Max(0f, -cached.BoundsY);
        var mathDescent = Math.Max(0f, cached.Height + cached.BoundsY);
        var mathHeight = Math.Max(cached.Height, style.FontSize * style.LineHeight);

        if (!_inInline)
        {
            _inlineX = _marginLeft + style.PaddingLeft;
            _inInline = true;
            _inlineMaxHeight = 0;
            BeginInlineLine(style);
        }

        var rightEdge = _marginLeft + _contentWidth;
        if (_inlineX + mathWidth > rightEdge && _inlineX > _marginLeft + style.PaddingLeft)
        {
            ApplyInlineLineAlignment();
            _cursorY += Math.Max(style.FontSize * style.LineHeight, _inlineMaxHeight);
            _inlineX = _marginLeft + style.PaddingLeft;
            _inlineMaxHeight = 0;
            BeginInlineLine(style);
        }

        // When the math starts a line (fresh line or after wrap) and its ascent is taller than
        // the surrounding font, push box.Y down so the visual top sits at _cursorY instead of
        // intruding into the line above. Draw uses baseline = box.Y + FontSize, and math top =
        // baseline - ascent, so box.Y + FontSize - ascent = _cursorY solves to this offset.
        var atLineStart = _inlineX <= _marginLeft + style.PaddingLeft + 0.5f;
        var boxY = _cursorY;
        if (atLineStart && mathAscent > style.FontSize)
        {
            boxY = _cursorY + (mathAscent - style.FontSize);
        }

        // Visual extent from _cursorY down to the math's bottom edge
        var visualHeight = (boxY - _cursorY) + style.FontSize + mathDescent;
        _inlineMaxHeight = Math.Max(_inlineMaxHeight, Math.Max(mathHeight, visualHeight));

        Boxes.Add(new LayoutBox
        {
            X = _inlineX,
            Y = boxY,
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

        var displayFontSize = style.FontSize * 1.05f;
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
            ApplyInlineLineAlignment();
            var style = _styleStack.Peek();
            var lineHeight = Math.Max(style.FontSize * style.LineHeight, _inlineMaxHeight);
            _cursorY += lineHeight;
            _inlineX = _marginLeft;
            _inInline = false;
            _inlineMaxHeight = 0;
        }
    }

    /// <summary>
    /// Begin tracking a new inline line for text-align purposes. Called when an inline
    /// emission starts (transition from !_inInline to _inInline) or after a soft line wrap.
    /// </summary>
    private void BeginInlineLine(InheritedStyle style)
    {
        _inlineLineStartIdx = Boxes.Count;
        _inlineLineLeftEdge = _marginLeft + style.PaddingLeft;
        _inlineLineAvailableWidth = _contentWidth - style.PaddingLeft;
        _inlineLineAlignment = style.TextAlign;
    }

    /// <summary>
    /// If the current inline line has center/right alignment, shift all boxes added
    /// since BeginInlineLine by the appropriate offset. Called from FlushInline and
    /// from soft-wrap points within inline emitters.
    /// </summary>
    private void ApplyInlineLineAlignment()
    {
        if (_inlineLineAlignment == TextAlign.Left) return;
        if (_inlineLineStartIdx >= Boxes.Count) return;

        float maxX = 0;
        for (int i = _inlineLineStartIdx; i < Boxes.Count; i++)
        {
            var b = Boxes[i];
            if (b.X + b.Width > maxX) maxX = b.X + b.Width;
        }
        var lineWidth = maxX - _inlineLineLeftEdge;
        if (lineWidth <= 0) return;

        float offset = _inlineLineAlignment == TextAlign.Center
            ? (_inlineLineAvailableWidth - lineWidth) / 2f
            : _inlineLineAvailableWidth - lineWidth;
        if (offset <= 0.5f) return;

        for (int i = _inlineLineStartIdx; i < Boxes.Count; i++)
        {
            Boxes[i] = Boxes[i] with { X = Boxes[i].X + offset };
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
                        X = cellX, Y = _cursorY, Width = colWidth, Height = rowHeight,
                        BackgroundColor = bgColor
                    });
                }

                Boxes.Add(new LayoutBox
                {
                    X = cellX, Y = _cursorY, Width = colWidth, Height = rowHeight,
                    BorderColor = new SKColor(189, 195, 199), BorderWidth = 0.5f
                });

                var text = cell.TextContent.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    var fontSize = isHeader ? cellStyle.FontSize : parentStyle.FontSize;
                    Boxes.Add(new LayoutBox
                    {
                        X = cellX + 5,
                        Y = _cursorY + (rowHeight - fontSize) / 2,
                        Width = colWidth - 10, Height = fontSize,
                        Text = text, FontSize = fontSize,
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

    private bool HasInlineBlockChildren(IElement element)
    {
        var firstLi = element.QuerySelector(":scope > li");
        if (firstLi == null) return false;
        var liStyle = ResolveStyle(firstLi);
        return liStyle.DisplayInlineBlock && liStyle.WidthPercent > 0;
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
            var font = FontCache.Instance.GetFont(style.FontFamily, style.FontSize, style.Bold, style.Italic);
            var bulletWidth = font.MeasureText(bullet);

            Boxes.Add(new LayoutBox
            {
                X = _marginLeft + indent, Y = _cursorY,
                Width = bulletWidth, Height = style.FontSize * style.LineHeight,
                Text = bullet, FontSize = style.FontSize,
                FontFamily = style.FontFamily, TextColor = style.TextColor
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

    private InheritedStyle ResolveStyle(IElement element)
    {
        var parent = _styleStack.Peek();
        var style = parent with { FloatLeft = false, ClearBoth = false, DisplayInlineBlock = false,
                                   WidthPercent = 0, PageBreakBefore = false, Height = 0,
                                   BorderLeftLine = false, MarginRight = 0 };

        var tagName = element.TagName.ToUpperInvariant();

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

        // Apply stylesheet tag rules (e.g., p { margin:0 }, body { font-size:5mm })
        var tagRules = _stylesheet.GetTagRules(tagName);
        if (tagRules != null)
            style = ApplyInlineStyle(style, tagRules);

        // Apply stylesheet class rules (e.g., .optionNumbering { float:left; width:24% })
        var classRules = _stylesheet.GetClassRules(element);
        if (classRules != null)
            style = ApplyInlineStyle(style, classRules);

        // Inline style overrides everything (highest specificity)
        var inlineStyle = element.GetAttribute("style");
        if (!string.IsNullOrEmpty(inlineStyle))
            style = ApplyInlineStyle(style, inlineStyle);

        // Scale down any font-size that exceeds body text size
        // (CSS class rules like .banner{font-size:4mm}=11.3pt need scaling)
        if (_textFontSize < 10f && style.FontSize > _textFontSize * 2f)
        {
            var ratio = _textFontSize / 14.2f;
            style = style with { FontSize = style.FontSize * ratio };
        }

        return style;
    }

    private static InheritedStyle ApplyInlineStyle(InheritedStyle style, string inlineStyle)
    {
        foreach (var declaration in inlineStyle.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = declaration.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;

            var prop = parts[0].ToLowerInvariant();
            var val = parts[1].Trim();

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
                // P1 accuracy: inline-block
                "display" when val == "inline-block" => style with { DisplayInlineBlock = true },
                // P0 accuracy: float
                "float" when val == "left" => style with { FloatLeft = true },
                // P0 accuracy: clear
                "clear" when val == "both" => style with { ClearBoth = true },
                // P2 accuracy: width %
                "width" when val.EndsWith('%') => style with
                {
                    WidthPercent = float.TryParse(val[..^1], out var wp) ? wp : 0
                },
                // Shorthand margin/padding
                "margin" => ParseShorthandMargin(style, val),
                "padding" => ParseShorthandPadding(style, val),
                // P3 accuracy: page-break
                "page-break-before" when val == "always" => style with { PageBreakBefore = true },
                // Height constraint
                "height" => style with { Height = ParsePxValue(val, 0) },
                // Margin-right
                "margin-right" => style with { MarginRight = ParsePxValue(val, style.MarginRight) },
                // Padding-right
                "padding-right" => style with { PaddingRight = ParsePxValue(val, style.PaddingRight) },
                // Border-left (column divider)
                "border-left" when val.Contains("solid") => style with { BorderLeftLine = true },
                _ => style
            };
        }
        return style;
    }

    private static InheritedStyle ParseShorthandMargin(InheritedStyle style, string val)
    {
        var parts = val.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            1 => style with { MarginTop = ParsePxValue(parts[0], 0), MarginBottom = ParsePxValue(parts[0], 0) },
            2 => style with { MarginTop = ParsePxValue(parts[0], 0), MarginBottom = ParsePxValue(parts[0], 0) },
            _ => style with { MarginTop = ParsePxValue(parts[0], 0), MarginBottom = ParsePxValue(parts.Length > 2 ? parts[2] : parts[0], 0) }
        };
    }

    private static InheritedStyle ParseShorthandPadding(InheritedStyle style, string val)
    {
        var parts = val.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            1 => style with { PaddingTop = ParsePxValue(parts[0], 0), PaddingBottom = ParsePxValue(parts[0], 0), PaddingLeft = ParsePxValue(parts[0], 0) },
            2 => style with { PaddingTop = ParsePxValue(parts[0], 0), PaddingBottom = ParsePxValue(parts[0], 0), PaddingLeft = ParsePxValue(parts[1], 0) },
            _ => style with { PaddingTop = ParsePxValue(parts[0], 0), PaddingBottom = ParsePxValue(parts.Length > 2 ? parts[2] : parts[0], 0), PaddingLeft = ParsePxValue(parts.Length > 3 ? parts[3] : parts[1], 0) }
        };
    }

    private static InheritedStyle TryApplyColor(InheritedStyle style, string val, bool isBackground)
    {
        var parsed = ParseCssColor(val);
        if (!parsed.HasValue) return style;
        return isBackground
            ? style with { BackgroundColor = parsed.Value }
            : style with { TextColor = parsed.Value };
    }

    // P4 accuracy: margin collapsing
    private void ApplyMarginTop(InheritedStyle style)
    {
        var effectiveMargin = Math.Max(style.MarginTop, _lastMarginBottom) - _lastMarginBottom;
        _cursorY += Math.Max(0, effectiveMargin);
        _lastMarginBottom = 0;

        if (style.BackgroundColor != SKColors.Transparent && style.PaddingTop > 0)
        {
            Boxes.Add(new LayoutBox
            {
                X = _marginLeft, Y = _cursorY,
                Width = _contentWidth, Height = style.PaddingTop,
                BackgroundColor = style.BackgroundColor
            });
        }
        _cursorY += style.PaddingTop;
    }

    private void ApplyMarginBottom(InheritedStyle style)
    {
        _cursorY += style.PaddingBottom;
        _lastMarginBottom = style.MarginBottom;
        _cursorY += style.MarginBottom;
    }

    // --- Helpers ---

    private static bool IsBlockElement(string tag) => tag is
        "DIV" or "P" or "H1" or "H2" or "H3" or "H4" or "H5" or "H6" or
        "SECTION" or "ARTICLE" or "MAIN" or "HEADER" or "FOOTER" or "NAV" or
        "ASIDE" or "BLOCKQUOTE" or "PRE" or "FIGURE" or "FIGCAPTION" or
        "ADDRESS" or "DETAILS" or "SUMMARY" or "FORM" or "FIELDSET" or
        "DL" or "DD" or "DT" or "LI";

    internal static float ParsePxValue(string value, float fallback)
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
        text = WhitespaceRegex.Replace(text, " ");
        // Normalize typographic quotes and special punctuation to ASCII
        text = text.Replace('\u2018', '\'')  // left single quote → '
                   .Replace('\u2019', '\'')  // right single quote → '
                   .Replace('\u201C', '"')   // left double quote → "
                   .Replace('\u201D', '"')   // right double quote → "
                   .Replace('\u2013', '-')   // en dash → -
                   .Replace('\u2014', '-')   // em dash → -
                   .Replace('\u00A0', ' ');  // non-breaking space → space
        return text;
    }
}

// --- Page Settings ---

public class PdfPageSettings
{
    public PageSize PageSize { get; set; } = PageSize.A4;
    public bool Landscape { get; set; }
    public int MarginMm { get; set; } = 10;
}

public enum PageSize { A3, A4, A5, Letter, Legal }

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
    // Layout properties
    public bool FloatLeft { get; init; }
    public bool ClearBoth { get; init; }
    public bool DisplayInlineBlock { get; init; }
    public float WidthPercent { get; init; }
    public bool PageBreakBefore { get; init; }
    public float Height { get; init; } // fixed height (0 = auto)
    public float MarginRight { get; init; }
    public bool BorderLeftLine { get; init; }
    public float PaddingRight { get; init; }
}
