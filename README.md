<p align="center">
  <h1 align="center">HtmlToPdf</h1>
  <p align="center">
    A high-performance, native PDF rendering engine for .NET — no browser engine required.
    <br />
    <br />
    <a href="#getting-started"><strong>Get Started</strong></a>
    &nbsp;&middot;&nbsp;
    <a href="#features"><strong>Features</strong></a>
    &nbsp;&middot;&nbsp;
    <a href="#api-reference"><strong>API Reference</strong></a>
  </p>
</p>

<br />

## Overview

**HtmlToPdf** converts HTML documents — complete with CSS styling, images, and LaTeX math — directly into multi-page PDF files. Unlike tools that rely on headless browsers (Puppeteer, Playwright, wkhtmltopdf), this project uses a **custom-built layout engine** powered by SkiaSharp to render PDFs natively in C#.

This means:
- **Zero browser dependencies** — no Chromium downloads, no headless processes, no sandbox issues.
- **Low resource footprint** — runs efficiently without spawning external processes.
- **Full control** — every stage of the pipeline (parsing, layout, rendering) is written in C# and fully customizable.

<br />

## Features

### Core Rendering
- **Custom Layout Engine** — A purpose-built layout engine (~1800 lines) that handles block/inline flow, page breaks, margin collapsing, and element positioning across multiple pages.
- **CSS Support** — Parses `<style>` blocks and inline styles. Supports fonts, colors, backgrounds, borders, margins, padding, text alignment, and more.
- **Table Rendering** — Full HTML table support with borders, cell padding, header styling, `colspan`, and adaptive column width calculation.
- **Image Support** — Inline base64-encoded images (`data:image/png;base64,...`) rendered directly into the PDF.
- **Lists** — Ordered (`<ol>`) and unordered (`<ul>`) list rendering with proper bullet/number prefixes.
- **Horizontal Rules** — `<hr>` elements rendered as styled dividers.

### Math & Science
- **LaTeX Math** — Inline (`$...$`) and display (`$$...$$`) math expressions rendered using CSharpMath. Supports fractions, integrals, matrices, Greek letters, summations, and more.
- **Math Pre-measurement** — LaTeX expressions are parsed and measured before layout, ensuring accurate positioning alongside text content.

### Text & Internationalization
- **Unicode Support** — Full Unicode text rendering with HarfBuzz text shaping for complex scripts.
- **Bengali Script** — Native support for Bengali and other Indic scripts that require advanced shaping.
- **Font Caching** — Intelligent font resolution and caching system for fast repeated renders.

### Conversion Modes
- **HTML String** — Paste or programmatically send raw HTML.
- **File Upload** — Upload `.html`, `.htm`, or `.xhtml` files.
- **URL Fetching** — Provide a URL and the engine fetches and converts the page.
- **Batch Processing** — Upload multiple HTML files at once. Files are converted in parallel across all available CPU cores and returned as a ZIP archive.

### Page Configuration
- **Page Sizes** — A3, A4, A5, Letter, Legal
- **Orientation** — Portrait or Landscape
- **Margins** — Configurable margin in millimeters

<br />

## Tech Stack

| Component | Library | Purpose |
|-----------|---------|---------|
| **Web Framework** | [ASP.NET Core 8](https://dotnet.microsoft.com/apps/aspnet) | HTTP server, MVC routing, file handling |
| **HTML Parsing** | [AngleSharp](https://github.com/AngleSharp/AngleSharp) | Standards-compliant HTML/CSS DOM parser |
| **PDF Rendering** | [SkiaSharp](https://github.com/mono/SkiaSharp) | 2D graphics engine with native PDF document backend |
| **Text Shaping** | [SkiaSharp.HarfBuzz](https://github.com/mono/SkiaSharp) | Complex script shaping (Bengali, Arabic, Devanagari, etc.) |
| **Math Rendering** | [CSharpMath.SkiaSharp](https://github.com/verybadcat/CSharpMath) | LaTeX math typesetting |

<br />

## Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later

### Installation

```bash
# Clone the repository
git clone https://github.com/Naieem-55/Html-to-Pdf.git
cd Html-to-Pdf

# Restore dependencies
dotnet restore

# Run the application
dotnet run
```

The application will start and display the URL in the console (typically `https://localhost:5001`).

### Quick Start

1. Open the web UI in your browser.
2. Choose an input method:

   | Tab | Description |
   |-----|-------------|
   | **HTML Editor** | Write or paste HTML directly into the editor |
   | **File Upload** | Upload a `.html` / `.htm` / `.xhtml` file |
   | **URL** | Enter a webpage URL to fetch and convert |

3. Adjust page settings (size, orientation, margins) as needed.
4. Click **Convert to PDF** to generate and download your file.

For bulk operations, switch to the **Batch Convert** tab, upload multiple files, and receive a ZIP archive of all converted PDFs.

<br />

## Architecture

### Project Structure

```
HtmlToPdf/
├── Controllers/
│   ├── HomeController.cs            # Landing page
│   └── PdfController.cs             # Single + batch conversion endpoints
├── Services/
│   ├── FreeHtmlToPdfConverter.cs     # Core rendering pipeline (layout engine + PDF renderer)
│   ├── StyleSheetParser.cs          # CSS rule parsing and cascade resolution
│   ├── FontCache.cs                 # System font discovery and caching
│   └── MathCache.cs                 # LaTeX expression pre-measurement and render cache
├── Models/
│   └── ConvertViewModel.cs          # View models and DTOs
├── Views/
│   ├── Pdf/
│   │   ├── Index.cshtml             # Converter UI (tabs, editor, settings)
│   │   └── _PdfSettings.cshtml      # Shared page settings partial
│   ├── Home/Index.cshtml            # Home page
│   └── Shared/                      # Layout and error views
├── wwwroot/                         # Static assets (CSS, JS, favicon)
└── Program.cs                       # Application entry point and DI configuration
```

### Rendering Pipeline

The conversion follows a five-stage pipeline:

```
HTML Input
    │
    ▼
┌─────────────────────────────────────────────────────────────┐
│  1. PARSE        │  HTML → DOM tree (AngleSharp)            │
├──────────────────┼──────────────────────────────────────────┤
│  2. STYLE        │  CSS rules + inline styles → resolved    │
│                  │  properties per element                  │
├──────────────────┼──────────────────────────────────────────┤
│  3. MATH         │  Detect $...$ and $$...$$ expressions,   │
│                  │  pre-measure dimensions (CSharpMath)     │
├──────────────────┼──────────────────────────────────────────┤
│  4. LAYOUT       │  Block/inline flow → positioned boxes    │
│                  │  with automatic page breaks              │
├──────────────────┼──────────────────────────────────────────┤
│  5. RENDER       │  Paint boxes to multi-page PDF           │
│                  │  (SkiaSharp PDF backend)                 │
└──────────────────┴──────────────────────────────────────────┘
    │
    ▼
PDF Output
```

<br />

## API Reference

### `POST /Pdf/Convert`

Converts a single HTML source to a PDF file.

**Content-Type:** `multipart/form-data`

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `ConversionSource` | `string` | `"html"` | Input mode: `"html"`, `"file"`, or `"url"` |
| `HtmlContent` | `string` | — | Raw HTML content (when source is `"html"`) |
| `HtmlFile` | `file` | — | HTML file upload (when source is `"file"`) |
| `Url` | `string` | — | Webpage URL (when source is `"url"`) |
| `PageSize` | `string` | `"A4"` | Page size: `A3`, `A4`, `A5`, `Letter`, `Legal` |
| `Landscape` | `bool` | `false` | Enable landscape orientation |
| `MarginMm` | `int` | `10` | Page margin in millimeters |

**Response:** `application/pdf` — the generated PDF file.

**Max request size:** 50 MB

---

### `POST /Pdf/BatchConvert`

Converts multiple HTML files in parallel and returns them as a ZIP archive.

**Content-Type:** `multipart/form-data`

| Parameter | Type | Description |
|-----------|------|-------------|
| `HtmlFiles` | `file[]` | Multiple HTML files to convert |
| `PageSize` | `string` | Page size (same options as above) |
| `Landscape` | `bool` | Enable landscape orientation |
| `MarginMm` | `int` | Page margin in millimeters |

**Response:** `application/zip` — ZIP archive containing all converted PDFs.

**Max request size:** 200 MB

<br />

## Supported HTML Elements

| Element | Support |
|---------|---------|
| Headings (`h1`–`h6`) | Full |
| Paragraphs (`p`, `div`, `span`) | Full |
| Text formatting (`b`, `strong`, `i`, `em`, `u`) | Full |
| Links (`a`) | Styled (rendered as text) |
| Tables (`table`, `tr`, `td`, `th`, `colspan`) | Full |
| Lists (`ol`, `ul`, `li`) | Full |
| Images (`img` with base64 `src`) | Full |
| Horizontal rules (`hr`) | Full |
| Line breaks (`br`) | Full |
| Blockquotes (`blockquote`) | Full |
| Inline math (`$...$`) | Full |
| Display math (`$$...$$`) | Full |

<br />

## Example

```html
<!DOCTYPE html>
<html>
<head>
    <style>
        body { font-family: Arial, sans-serif; margin: 40px; color: #333; }
        h1 { color: #2c3e50; border-bottom: 2px solid #3498db; padding-bottom: 10px; }
        table { width: 100%; border-collapse: collapse; margin: 20px 0; }
        th, td { border: 1px solid #bdc3c7; padding: 10px; text-align: left; }
        th { background: #3498db; color: white; }
    </style>
</head>
<body>
    <h1>Sample Document</h1>
    <p>The quadratic formula is $x = \frac{-b \pm \sqrt{b^2 - 4ac}}{2a}$</p>

    <p>$$\int_{-\infty}^{\infty} e^{-x^2} dx = \sqrt{\pi}$$</p>

    <table>
        <tr><th>Feature</th><th>Status</th></tr>
        <tr><td>CSS Styling</td><td>Supported</td></tr>
        <tr><td>LaTeX Math</td><td>Supported</td></tr>
        <tr><td>Tables</td><td>Supported</td></tr>
    </table>
</body>
</html>
```

<br />

## Contributing

Contributions are welcome. Please open an issue to discuss proposed changes before submitting a pull request.

## License

This project is open source. See the repository for license details.
