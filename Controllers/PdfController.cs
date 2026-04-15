using System.Diagnostics;
using HtmlToPdf.Models;
using HtmlToPdf.Services;
using Microsoft.AspNetCore.Mvc;

namespace HtmlToPdf.Controllers;

public class PdfController : Controller
{
    private readonly FreeHtmlToPdfConverter _converter;
    private readonly ILogger<PdfController> _logger;

    public PdfController(FreeHtmlToPdfConverter converter, ILogger<PdfController> logger)
    {
        _converter = converter;
        _logger = logger;
    }

    public IActionResult Index()
    {
        var model = new ConvertViewModel
        {
            HtmlContent = SampleHtml
        };
        return View(model);
    }

    [HttpPost]
    [RequestSizeLimit(50 * 1024 * 1024)]
    public async Task<IActionResult> Convert(ConvertViewModel model)
    {
        var settings = new PdfPageSettings
        {
            PageSize = model.PageSize,
            Landscape = model.Landscape,
            MarginMm = model.MarginMm
        };

        try
        {
            byte[] pdfBytes;
            string fileName;
            var sw = Stopwatch.StartNew();

            switch (model.ConversionSource)
            {
                case "file" when model.HtmlFile is { Length: > 0 }:
                    var tempPath = Path.Combine(Path.GetTempPath(), $"upload_{Guid.NewGuid():N}.html");
                    try
                    {
                        await using (var stream = System.IO.File.Create(tempPath))
                        {
                            await model.HtmlFile.CopyToAsync(stream);
                        }
                        pdfBytes = _converter.ConvertFromFile(tempPath, settings);
                        fileName = Path.GetFileNameWithoutExtension(model.HtmlFile.FileName) + ".pdf";
                    }
                    finally
                    {
                        try { System.IO.File.Delete(tempPath); } catch { }
                    }
                    break;

                case "url" when !string.IsNullOrWhiteSpace(model.Url):
                    pdfBytes = _converter.ConvertFromUrl(model.Url.Trim(), settings);
                    fileName = "webpage.pdf";
                    break;

                case "html" when !string.IsNullOrWhiteSpace(model.HtmlContent):
                    pdfBytes = _converter.ConvertFromHtmlString(model.HtmlContent, settings);
                    fileName = "converted.pdf";
                    break;

                default:
                    model.ErrorMessage = "Please provide HTML content, upload a file, or enter a URL.";
                    return View("Index", model);
            }

            sw.Stop();
            var sizeKb = pdfBytes.Length / 1024.0;
            _logger.LogInformation(
                "PDF conversion completed: source={Source}, time={ElapsedMs}ms, size={SizeKb:F1}KB, pages={PageSize}, landscape={Landscape}",
                model.ConversionSource, sw.ElapsedMilliseconds, sizeKb, model.PageSize, model.Landscape);

            return File(pdfBytes, "application/pdf", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PDF conversion failed: source={Source}", model.ConversionSource);
            model.ErrorMessage = $"Conversion failed: {ex.Message}";
            return View("Index", model);
        }
    }

    [HttpPost]
    [RequestSizeLimit(200 * 1024 * 1024)]
    public async Task<IActionResult> BatchConvert(ConvertViewModel model)
    {
        if (model.HtmlFiles == null || model.HtmlFiles.Count == 0)
        {
            model.ErrorMessage = "Please upload one or more HTML files.";
            return View("Index", model);
        }

        var settings = new PdfPageSettings
        {
            PageSize = model.PageSize,
            Landscape = model.Landscape,
            MarginMm = model.MarginMm
        };

        var totalSw = Stopwatch.StartNew();

        // Save uploaded files to temp
        var tempFiles = new List<(string tempPath, string originalName)>();
        foreach (var file in model.HtmlFiles)
        {
            if (file.Length == 0) continue;
            var tempPath = Path.Combine(Path.GetTempPath(), $"batch_{Guid.NewGuid():N}.html");
            await using (var stream = System.IO.File.Create(tempPath))
            {
                await file.CopyToAsync(stream);
            }
            tempFiles.Add((tempPath, file.FileName));
        }

        // Convert all files in parallel — each core processes a different document
        var results = new BatchResultItem[tempFiles.Count];
        var pdfOutputs = new byte[tempFiles.Count][];

        Parallel.For(0, tempFiles.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            i =>
            {
                var (tempPath, originalName) = tempFiles[i];
                var sw = Stopwatch.StartNew();
                try
                {
                    var converter = new FreeHtmlToPdfConverter();
                    var pdf = converter.ConvertFromFile(tempPath, settings);
                    sw.Stop();
                    pdfOutputs[i] = pdf;
                    results[i] = new BatchResultItem
                    {
                        FileName = Path.GetFileNameWithoutExtension(originalName) + ".pdf",
                        ElapsedMs = sw.ElapsedMilliseconds,
                        SizeKb = pdf.Length / 1024.0,
                        Success = true
                    };
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    pdfOutputs[i] = Array.Empty<byte>();
                    results[i] = new BatchResultItem
                    {
                        FileName = originalName,
                        ElapsedMs = sw.ElapsedMilliseconds,
                        Success = false,
                        Error = ex.Message
                    };
                }
                finally
                {
                    try { System.IO.File.Delete(tempPath); } catch { }
                }
            });

        totalSw.Stop();

        var successCount = results.Count(r => r.Success);
        _logger.LogInformation(
            "Batch conversion: {Success}/{Total} files, total={TotalMs}ms, cores={Cores}",
            successCount, results.Length, totalSw.ElapsedMilliseconds, Environment.ProcessorCount);

        // Package all PDFs into a ZIP
        using var zipStream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            for (int i = 0; i < results.Length; i++)
            {
                if (!results[i].Success || pdfOutputs[i].Length == 0) continue;
                var entry = archive.CreateEntry(results[i].FileName, System.IO.Compression.CompressionLevel.Fastest);
                using var entryStream = entry.Open();
                entryStream.Write(pdfOutputs[i]);
            }
        }

        zipStream.Seek(0, SeekOrigin.Begin);
        return File(zipStream.ToArray(), "application/zip", "converted_pdfs.zip");
    }

    private const string SampleHtml = """
        <!DOCTYPE html>
        <html>
        <head>
            <style>
                body { font-family: Arial, sans-serif; margin: 40px; color: #333; }
                h1 { color: #2c3e50; border-bottom: 2px solid #3498db; padding-bottom: 10px; }
                .info { background: #ecf0f1; padding: 15px; border-radius: 5px; margin: 20px 0; }
                table { width: 100%; border-collapse: collapse; margin: 20px 0; }
                th, td { border: 1px solid #bdc3c7; padding: 10px; text-align: left; }
                th { background: #3498db; color: white; }
                tr:nth-child(even) { background: #f2f2f2; }
            </style>
        </head>
        <body>
            <h1>Sample PDF with LaTeX Math</h1>
            <div class="info">
                <p>This PDF was generated using <strong>AngleSharp + SkiaSharp + CSharpMath</strong> — free, no browser engine.</p>
            </div>

            <h2>Inline Math</h2>
            <p>The quadratic formula is $x = \frac{-b \pm \sqrt{b^2 - 4ac}}{2a}$ and it solves any quadratic equation.</p>
            <p>Euler's identity $e^{i\pi} + 1 = 0$ connects five fundamental constants.</p>

            <h2>Display Math</h2>
            <p>The Gaussian integral:</p>
            <p>$$\int_{-\infty}^{\infty} e^{-x^2} dx = \sqrt{\pi}$$</p>

            <p>A matrix example:</p>
            <p>$$\begin{pmatrix} a & b \\ c & d \end{pmatrix} \begin{pmatrix} x \\ y \end{pmatrix} = \begin{pmatrix} ax + by \\ cx + dy \end{pmatrix}$$</p>

            <p>Sum of a series:</p>
            <p>$$\sum_{k=1}^{n} k^2 = \frac{n(n+1)(2n+1)}{6}$$</p>

            <h2>Features Table</h2>
            <table>
                <tr><th>Feature</th><th>Status</th></tr>
                <tr><td>CSS3 Styling</td><td>Supported</td></tr>
                <tr><td>Tables</td><td>Supported</td></tr>
                <tr><td>LaTeX Math (inline)</td><td>Supported</td></tr>
                <tr><td>LaTeX Math (display)</td><td>Supported</td></tr>
                <tr><td>Greek Letters</td><td>$\alpha, \beta, \gamma, \delta$</td></tr>
            </table>
        </body>
        </html>
        """;
}
