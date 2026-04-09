# HTML to PDF Converter (.NET)

A high-performance HTML to PDF converter built with .NET 8 — no browser engine, no WebKit, no Chromium. Converts HTML with CSS styling and LaTeX math formulas to vector PDF using a fully managed pipeline.

## Architecture

```
HTML input
  -> AngleSharp         (parse HTML into DOM tree)
  -> Inline Style Parser (extract CSS properties — no slow GetComputedStyle)
  -> Layout Engine       (block/inline flow, word wrap, font fallback)
  -> CSharpMath          (measure & render LaTeX from <span class="math-tex">)
  -> SkiaSharp           (SKDocument -> vector PDF with selectable text)
  -> PDF output
```

## Tech Stack

| Package | Version | License | Role |
|---------|---------|---------|------|
| [AngleSharp](https://github.com/AngleSharp/AngleSharp) | 0.17.1 | MIT | HTML/DOM parser |
| [AngleSharp.Css](https://github.com/AgleSharp/AngleSharp.Css) | 0.17.0 | MIT | CSS engine (body-level styles) |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | 3.119.2 | MIT | PDF rendering via `SKDocument`/`SKCanvas` |
| [CSharpMath.SkiaSharp](https://github.com/verybadcat/CSharpMath) | 1.0.0-pre.1 | MIT | LaTeX math formula rendering |

All dependencies are MIT-licensed. No commercial licenses required.

## Features

- **No browser engine** — pure C# pipeline, no Chromium/WebKit/Puppeteer dependency
- **LaTeX math rendering** — supports `<span class="math-tex">\(...\)</span>` notation with fractions, matrices, integrals, Greek letters, and more
- **Bengali/Indic font fallback** — automatic detection of non-Latin text with fallback to Nirmala UI via `SKFontManager.MatchCharacter()`
- **Three input modes** — HTML string, file upload, or URL
- **Configurable output** — page size (A4/A3/A5/Letter/Legal), margins, landscape orientation
- **Vector PDF** — text remains selectable, math renders as vector graphics
- **Performance optimized** — font caching, math pre-measurement, inline style parser

## Performance

Benchmarked with a 426-formula engineering exam paper (Bengali + English + LaTeX):

| Metric | Value |
|--------|-------|
| Total conversion time | **~970ms** |
| Parse (AngleSharp) | 249ms |
| Math pre-measure (532 formulas) | 294ms |
| Layout (2992 boxes) | 136ms |
| PDF render | 288ms |
| Output PDF size | 4.1 MB |

### Optimization history

| Version | Time | Change |
|---------|------|--------|
| Initial | 12,935ms | Baseline with `GetComputedStyle` per element |
| + Font caching | 11,929ms | Cache `SKTypeface` instances |
| + Skip `IsDisplayNone` | 5,940ms | Merge display check into style resolution |
| + Inline style parser | 1,002ms | Replace `GetComputedStyle` with string split parser |
| + Math cache | 972ms | Pre-measure formulas, eliminate duplicate work |

## Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- Windows (for Nirmala UI Bengali font fallback) — Linux/macOS work with different fallback fonts

### Run

```bash
git clone https://github.com/Naieem-55/html-to-pdf-.NET-.git
cd html-to-pdf-.NET-
dotnet run
```

Navigate to `https://localhost:<port>/Pdf` in your browser.

### Usage

**Web UI**: Paste HTML, upload a `.html` file, or enter a URL. Configure page size, margins, and orientation. Click "Convert to PDF".

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
```

## LaTeX Support

Math formulas are detected from `<span class="math-tex">\(...\)</span>` elements (common in MathJax/KaTeX-based HTML) and from `$...$` / `$$...$$` delimiters in text.

Supported LaTeX:

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

## Project Structure

```
html-to-pdf-.NET-/
  Controllers/
    PdfController.cs          # Web API: convert endpoint with timing logs
  Models/
    ConvertViewModel.cs       # Form model: source, page settings
  Services/
    FreeHtmlToPdfConverter.cs  # Core: parse -> layout -> render pipeline
    FontCache.cs              # Typeface caching + Bengali fallback
    MathCache.cs              # LaTeX pre-measurement cache
  Views/
    Pdf/Index.cshtml           # Web UI: HTML editor, file upload, URL input
  plan.md                      # Future optimization roadmap
```

## Roadmap

See [plan.md](plan.md) for the full optimization and accuracy roadmap. Key items:

**Speed** (target: ~400-500ms)
- Cache `SKPicture` for math render — avoid recreating `MathPainter` during PDF draw
- Reuse `SKPaint`/`SKFont` across render loop — reduce 20K+ object allocations
- Drop `AngleSharp.Css` dependency — replace with regex-based `<style>` parser

**Layout accuracy**
- CSS `float: left` + `width: %` — side-by-side question numbering
- CSS `display: inline-block` — MCQ options in grid layout
- `page-break-before: always` — correct multi-page breaks
- HarfBuzzSharp integration — proper Bengali conjunct rendering

## License

MIT
