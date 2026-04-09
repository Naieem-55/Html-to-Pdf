using html_to_pdf_aspose.Services;

namespace html_to_pdf_aspose.Models;

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
}
