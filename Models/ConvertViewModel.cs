using HtmlToPdf.Services;

namespace HtmlToPdf.Models;

public class ConvertViewModel
{
    public string? HtmlContent { get; set; }
    public string? Url { get; set; }
    public IFormFile? HtmlFile { get; set; }
    public string ConversionSource { get; set; } = "html"; // html, file, url
    public PageSize PageSize { get; set; } = PageSize.A4;
    public bool Landscape { get; set; }
    public int MarginMm { get; set; } = 10;
    public string? ErrorMessage { get; set; }

    // Batch conversion
    public List<IFormFile>? HtmlFiles { get; set; }
}

public class BatchResultItem
{
    public string FileName { get; set; } = "";
    public long ElapsedMs { get; set; }
    public double SizeKb { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}
