# HtmlToPdf

A native PDF rendering engine built with **ASP.NET Core 8**, **SkiaSharp**, and **AngleSharp** — no browser engine, no headless Chrome, no external dependencies.

Converts HTML (with CSS styling and LaTeX math) directly to PDF using a custom layout engine and SkiaSharp's PDF backend.

## Features

- **Native PDF Rendering** — No Chromium, Puppeteer, or wkhtmltopdf. Pure C# rendering pipeline using SkiaSharp.
- **LaTeX Math Support** — Inline (`$...$`) and display (`$$...$$`) math equations rendered via CSharpMath.
- **CSS Styling** — Supports common CSS properties including fonts, colors, backgrounds, borders, margins, padding, and table styling.
- **Multiple Input Sources** — Convert from raw HTML, file upload, or URL.
- **Batch Conversion** — Upload multiple HTML files and receive a ZIP archive of PDFs. Converts files in parallel across CPU cores.
- **Page Settings** — Configurable page size (A4, Letter, Legal, A3, A5), orientation (portrait/landscape), and margins.
- **Bengali/Unicode Support** — Full Unicode text rendering with HarfBuzz shaping for complex scripts.
- **Tables** — HTML table rendering with borders, cell padding, header styling, and column width calculation.
- **Font Caching** — Efficient font resolution and caching for fast repeated renders.

## Tech Stack

| Component | Library |
|-----------|---------|
| Web Framework | ASP.NET Core 8 |
| HTML Parsing | [AngleSharp](https://github.com/AngleSharp/AngleSharp) |
| PDF Rendering | [SkiaSharp](https://github.com/mono/SkiaSharp) |
| Text Shaping | [SkiaSharp.HarfBuzz](https://github.com/mono/SkiaSharp) |
| Math Rendering | [CSharpMath.SkiaSharp](https://github.com/verybadcat/CSharpMath) |

## Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Run

```bash
git clone https://github.com/Naieem-55/Html-to-Pdf.git
cd Html-to-Pdf
dotnet run
```

The application will start at `https://localhost:5001` (or the port shown in the console).

### Usage

1. Open the web UI in your browser.
2. Choose an input method:
   - **HTML Editor** — Paste or write HTML directly.
   - **File Upload** — Upload an `.html` file.
   - **URL** — Enter a webpage URL.
3. Configure page settings (size, orientation, margins).
4. Click **Convert** to generate and download the PDF.

For batch conversion, upload multiple HTML files to receive a ZIP of all converted PDFs.

## Architecture

```
HtmlToPdf/
├── Controllers/
│   ├── HomeController.cs          # Landing page
│   └── PdfController.cs           # Conversion endpoints (single + batch)
├── Services/
│   ├── FreeHtmlToPdfConverter.cs   # Core conversion pipeline
│   ├── StyleSheetParser.cs        # CSS parsing and resolution
│   ├── FontCache.cs               # Font loading and caching
│   └── MathCache.cs               # LaTeX math pre-measurement and caching
├── Models/
│   └── ConvertViewModel.cs        # Request/response models
├── Views/
│   ├── Pdf/Index.cshtml           # Conversion UI
│   └── Home/Index.cshtml          # Home page
└── wwwroot/                       # Static assets
```

### Rendering Pipeline

1. **Parse** — HTML is parsed into a DOM using AngleSharp.
2. **Style Resolution** — CSS rules (both `<style>` blocks and inline styles) are parsed and resolved.
3. **Math Pre-measurement** — LaTeX expressions are detected, parsed, and pre-measured for layout.
4. **Layout** — A custom layout engine calculates positions for all elements across pages, handling page breaks, floats, and inline/block flow.
5. **Render** — SkiaSharp renders the layout to a multi-page PDF document.

## API

### POST `/Pdf/Convert`

Converts a single HTML source to PDF.

| Parameter | Type | Description |
|-----------|------|-------------|
| `ConversionSource` | `string` | `"html"`, `"file"`, or `"url"` |
| `HtmlContent` | `string` | Raw HTML (when source is `"html"`) |
| `HtmlFile` | `IFormFile` | Uploaded HTML file (when source is `"file"`) |
| `Url` | `string` | Webpage URL (when source is `"url"`) |
| `PageSize` | `enum` | `A4`, `Letter`, `Legal`, `A3`, `A5` |
| `Landscape` | `bool` | Landscape orientation |
| `MarginMm` | `int` | Page margin in millimeters |

Returns the generated PDF file.

### POST `/Pdf/BatchConvert`

Uploads multiple HTML files and returns a ZIP archive containing all converted PDFs. Files are processed in parallel.

## License

This project is open source. See the repository for license details.
