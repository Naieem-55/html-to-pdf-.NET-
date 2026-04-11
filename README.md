# HTML to PDF Converter (.NET)

A high-performance HTML to PDF converter built with .NET 8 — no browser engine, no WebKit, no Chromium. Converts HTML with CSS styling, LaTeX math formulas, and Bengali/Indic text to vector PDF with automatic two-column exam paper layout.

## Architecture

```
HTML string
  -> AngleSharp              (parse HTML into DOM tree)
  -> OverrideBodyFontSize    (apply data-textfontsize/data-eqfontsize scaling)
  -> StyleSheetParser        (extract <style> class rules + descendant selectors)
  -> MathCache               (pre-measure all LaTeX formulas)
  -> LayoutEngine            (block/inline/float/inline-block/two-column flow)
  -> SkiaSharp + HarfBuzz    (render text with complex script shaping)
  -> CSharpMath              (render LaTeX math formulas)
  -> SKDocument              (vector PDF output)
```

## Tech Stack

| Package | Version | License | Role |
|---------|---------|---------|------|
| [AngleSharp](https://github.com/AngleSharp/AngleSharp) | 0.17.1 | MIT | HTML/DOM parser |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | 3.119.2 | MIT | PDF rendering via `SKDocument`/`SKCanvas` |
| [SkiaSharp.HarfBuzz](https://github.com/mono/SkiaSharp) | 3.119.2 | MIT | Complex text shaping (Bengali conjuncts, ligatures) |
| [CSharpMath.SkiaSharp](https://github.com/verybadcat/CSharpMath) | 1.0.0-pre.1 | MIT | LaTeX math formula rendering |

All dependencies are MIT-licensed. No commercial licenses required.

## Features

- **No browser engine** — fully native .NET pipeline, no Chromium/WebKit/Puppeteer
- **Two-column exam paper layout** — auto-detects `.questionPages` container and arranges questions in two columns with divider line
- **LaTeX math rendering** — `<span class="math-tex">\(...\)</span>`, `$...$`, `$$...$$` with fractions, matrices, integrals, Greek letters
- **Bengali/Indic text shaping** — HarfBuzz OpenType GSUB/GPOS for correct conjuncts, vowel signs, and ligatures
- **CSS layout support** — `float:left`, `display:inline-block`, `width:%`, `page-break-before:always`, margin collapsing, shorthand `margin`/`padding`
- **Stylesheet parsing** — extracts class rules AND descendant selectors (`.parent .child`) from `<style>` blocks
- **Font scaling** — reads `data-textfontsize` and `data-eqfontsize` body attributes for compact exam paper density
- **Inline image rendering** — decodes and renders `data:image/png;base64,...` images inline with text
- **Unicode normalization** — typographic quotes (`''""`), dashes (`–—`), and NBSP auto-converted to ASCII
- **Font fallback** — automatic Arial -> Nirmala UI for Bengali/Indic scripts
- **Batch conversion** — convert multiple HTML files in parallel using all CPU cores, output as ZIP
- **Four input modes** — HTML editor, single file upload, URL, or batch file upload
- **Configurable output** — page size (A4/A3/A5/Letter/Legal), margins, landscape orientation
- **Vector PDF** — text remains selectable, math renders as vector graphics

## Performance

Benchmarked with a 100-question engineering exam paper (Bengali + English + LaTeX):

| Metric | Value |
|--------|-------|
| Total conversion time | **~775ms** |
| Parse (AngleSharp) | 60ms |
| Math pre-measure (532 formulas) | 178ms |
| Layout (2594 boxes) | 82ms |
| PDF render (3 pages) | 455ms |
| Output | 3 pages, two-column layout |

### Optimization journey

| Version | Time | Speedup | Key change |
|---------|------|---------|------------|
| Initial | 12,935ms | 1x | `GetComputedStyle` per element |
| + Font caching | 11,929ms | 1.1x | Cache `SKTypeface` instances |
| + Merge IsDisplayNone | 5,940ms | 2.2x | Single `GetComputedStyle` per element |
| + Inline style parser | 1,002ms | 12.9x | Replace CSS engine with string split |
| + Math cache | 972ms | 13.3x | Pre-measure formulas, no duplicate work |
| + Drop AngleSharp.Css | 967ms | 13.4x | Remove CSS engine initialization |
| + StyleSheet + HarfBuzz | 528ms | 24.5x | Class rules + shaped text measurement |
| + Two-column layout | **775ms** | **16.7x** | Exam paper format, font scaling |

## Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- Windows (for Nirmala UI Bengali font) — Linux/macOS work with different fallback fonts

### Run

```bash
git clone https://github.com/Naieem-55/html-to-pdf-.NET-.git
cd html-to-pdf-.NET-
dotnet run
```

Navigate to `http://localhost:5204/Pdf` in your browser.

### Usage

**Web UI** — three modes:
- **Single File** — paste HTML, upload a file, or enter a URL
- **Batch Convert** — upload multiple HTML files, converts in parallel, downloads ZIP

**Programmatic**:

```csharp
var converter = new FreeHtmlToPdfConverter();

// From HTML string
byte[] pdf = converter.ConvertFromHtmlString("<h1>Hello</h1><p>$E=mc^2$</p>");

// From file
byte[] pdf = converter.ConvertFromFile("input.html");

// With settings
byte[] pdf = converter.ConvertFromHtmlString(html, new PdfPageSettings
{
    PageSize = PageSize.A4,
    Landscape = false,
    MarginMm = 6
});

// Batch conversion (parallel, uses all CPU cores)
Parallel.ForEach(Directory.GetFiles("input/", "*.html"), file =>
{
    var conv = new FreeHtmlToPdfConverter();
    File.WriteAllBytes(
        Path.ChangeExtension(file, ".pdf"),
        conv.ConvertFromFile(file));
});
```

## Exam Paper Format

When the HTML contains a `div.questionPages` container, the converter automatically:

1. Lays out all `.question` elements in **two columns** with a vertical divider
2. Reads `data-textfontsize` and `data-eqfontsize` from `<body>` for compact font sizing
3. Scales all inline `font-size` values proportionally
4. Fills left column first, then right column, then next page
5. Accounts for banner height on the first page

Target: ~50 questions per page for a typical MCQ exam paper.

## LaTeX Support

Math formulas detected from `<span class="math-tex">\(...\)</span>` (MathJax/KaTeX HTML) and `$...$` / `$$...$$` in text.

| Category | Examples |
|----------|---------|
| Fractions | `\frac{a}{b}` |
| Matrices | `\begin{bmatrix}a&b\\c&d\end{bmatrix}` |
| Integrals | `\int_0^\infty`, `\iint`, `\oint` |
| Summations | `\sum_{k=1}^{n}`, `\prod` |
| Greek | `\alpha`, `\beta`, `\gamma`, `\Omega` |
| Roots | `\sqrt{x}`, `\sqrt[3]{x}` |
| Limits | `\lim_{x\to 0}` |
| Accents | `\hat{x}`, `\vec{v}`, `\dot{x}` |
| Environments | `pmatrix`, `bmatrix`, `vmatrix`, `cases` |

Unsupported commands (`\underset`, `\overset`, `\operatorname`) are automatically converted to compatible equivalents.

## CSS Layout Support

| Feature | Status |
|---------|--------|
| `float: left` + `width: %` | Supported (side-by-side columns) |
| `display: inline-block` + `width: %` | Supported (MCQ option grid) |
| `page-break-before: always` | Supported |
| `clear: both` | Supported |
| Margin collapsing | Supported |
| Shorthand `margin` / `padding` | Supported |
| Inline styles | Supported (fast string-split parser) |
| `<style>` class rules | Supported (simple + descendant selectors) |
| Nested float isolation | Supported |
| `height`, `border-left` | Supported |
| `data-textfontsize` / `data-eqfontsize` | Supported (body attributes) |

## Project Structure

```
html-to-pdf-.NET/
  Controllers/
    PdfController.cs            # Single + batch endpoints, timing logs
  Models/
    ConvertViewModel.cs         # Form model, batch result
  Services/
    FreeHtmlToPdfConverter.cs   # Core pipeline: parse -> layout -> render
    FontCache.cs                # Typeface/font caching + Bengali fallback
    MathCache.cs                # LaTeX pre-measurement cache
    StyleSheetParser.cs         # Lightweight <style> parser (class + descendant)
  Views/
    Pdf/Index.cshtml            # Web UI: single + batch tabs
    Pdf/_PdfSettings.cshtml     # Shared page settings partial
  plan.md                       # Optimization roadmap
  full_cpu_usage.md             # CPU parallelization analysis
  question_format.md            # Exam paper layout plan
```

## Known Limitations

- CSharpMath doesn't support `\newcommand`, `\def`, or `\text{...$...$}`
- Two-column layout only triggers for `.questionPages` container
- External image URLs (`http://...`) not fetched — only `data:image/...;base64` and `[alt]` placeholder

## License

    MIT
