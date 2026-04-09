using System.Diagnostics;
using html_to_pdf_aspose.Models;
using html_to_pdf_aspose.Services;
using Microsoft.AspNetCore.Mvc;

namespace html_to_pdf_aspose.Controllers;

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
