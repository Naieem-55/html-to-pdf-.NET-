# HTML to PDF Converter (.NET)

A high-performance HTML to PDF converter built with .NET 8 — no browser engine, no WebKit, no Chromium. Converts HTML with CSS styling, LaTeX math formulas, and Bengali/Indic text to vector PDF.

## Architecture

```
HTML string
  -> AngleSharp              (parse HTML into DOM tree)
  -> StyleSheetParser        (extract <style> class rules — lightweight, no CSS engine)
  -> MathCache               (pre-measure all LaTeX formulas)
  -> LayoutEngine            (block/inline/float/inline-block flow, word wrap)
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
- **LaTeX math rendering** — `<span class="math-tex">\(...\)</span>`, `$...$`, `$$...$$` with fractions, matrices, integrals, Greek letters
- **Bengali/Indic text shaping** — HarfBuzz OpenType GSUB/GPOS for correct conjuncts, vowel signs, and ligatures
- **CSS layout support** — `float:left`, `display:inline-block`, `width:%`, `page-break-before:always`, margin collapsing
- **Stylesheet parsing** — extracts class rules from `<style>` blocks without a CSS engine
- **Font fallback** — automatic Arial -> Nirmala UI for non-Latin scripts
- **Batch conversion** — convert multiple HTML files in parallel using all CPU cores, output as ZIP
- **Four input modes** — HTML editor, single file upload, URL, or batch file upload
- **Configurable output** — page size (A4/A3/A5/Letter/Legal), margins, landscape orientation
- **Vector PDF** — text remains selectable, math renders as vector graphics

## Performance

Benchmarked with a 426-formula engineering exam paper (Bengali + English + LaTeX):

| Metric | Value |
|--------|-------|
| Total conversion time | **~528ms** |
| Parse (AngleSharp) | 58ms |
| Math pre-measure (261 unique formulas) | 174ms |
| Layout (2592 boxes) | 43ms |
| PDF render (11 pages) | 253ms |
| Output PDF size | 3.5 MB |

### Optimization journey

| Version | Time | Speedup | Key change |
|---------|------|---------|------------|
| Initial | 12,935ms | 1x | `GetComputedStyle` per element |
| + Font caching | 11,929ms | 1.1x | Cache `SKTypeface` instances |
| + Merge IsDisplayNone | 5,940ms | 2.2x | Single `GetComputedStyle` per element |
| + Inline style parser | 1,002ms | 12.9x | Replace CSS engine with string split |
| + Math cache | 972ms | 13.3x | Pre-measure formulas, no duplicate work |
| + Drop AngleSharp.Css | 967ms | 13.4x | Remove CSS engine initialization |
| + StyleSheet parser + HarfBuzz | **528ms** | **24.5x** | Class rules + shaped text measurement |

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

**Web UI**: Two tabs:
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
    MarginMm = 10
});

// Batch conversion (parallel, uses all CPU cores)
var htmlFiles = Directory.GetFiles("input/", "*.html");
Parallel.ForEach(htmlFiles, file =>
{
    var conv = new FreeHtmlToPdfConverter();
    var pdf = conv.ConvertFromFile(file);
    File.WriteAllBytes(Path.ChangeExtension(file, ".pdf"), pdf);
});
```

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

| Feature | Status | Implementation |
|---------|--------|---------------|
| `float: left` + `width: %` | Supported | Side-by-side columns (question numbers, option labels) |
| `display: inline-block` + `width: %` | Supported | Grid layout (4 MCQ options per row) |
| `page-break-before: always` | Supported | Force new page |
| `clear: both` | Supported | Reset float context |
| Margin collapsing | Supported | `max(prevBottom, currTop)` |
| Inline styles | Supported | Fast string-split parser |
| `<style>` class rules | Supported | StyleSheetParser (regex-based) |
| Nested floats | Supported | Isolated float state per nesting level |

## Project Structure

```
html-to-pdf-.NET-/
  Controllers/
    PdfController.cs            # Single + batch convert endpoints, timing logs
  Models/
    ConvertViewModel.cs         # Form model, batch result item
  Services/
    FreeHtmlToPdfConverter.cs   # Core pipeline: parse -> layout -> render
    FontCache.cs                # Typeface/font/shaper caching + fallback
    MathCache.cs                # LaTeX pre-measurement cache
    StyleSheetParser.cs         # Lightweight <style> block parser
  Views/
    Pdf/Index.cshtml            # Web UI: single + batch tabs
    Pdf/_PdfSettings.cshtml     # Shared page settings partial
  plan.md                       # Optimization roadmap
  full_cpu_usage.md             # CPU parallelization analysis
```

## Known Limitations

- Bengali line-breaking uses space-only splitting (UAX #14 not implemented)
- No CSS `flexbox` or `grid` layout
- No JavaScript execution
- No image embedding (shows `[alt text]` placeholder)
- CSharpMath doesn't support `\newcommand`, `\def`, or `\text{...$...$}`

## License

MIT
