# Optimization & Accuracy Plan

## Current Performance (baseline)

| Phase | Time | % |
|-------|------|---|
| mathPreMeasure | 294ms | 30% |
| render | 288ms | 30% |
| parse | 249ms | 26% |
| layout | 136ms | 14% |
| **total** | **972ms** | |

PDF: 4.1MB, 2992 boxes, 532 math formulas

---

## SPEED: Remaining Optimizations

### P0 — Render phase (288ms → ~100ms target)

**Problem**: `RenderBox` creates **new MathPainter per math box** during render (line 281). Same formula parsed + laid out again just to call `.Draw()`. Also creates new `SKPaint` and `SKFont` for every single box (~20K object allocations total).

**Fixes**:

1. **Cache SKPicture in MathCache** — Record the MathPainter draw output as `SKPicture` during pre-measure. Render phase then calls `canvas.DrawPicture()` (one native call) instead of creating MathPainter + parsing LaTeX again.
   ```
   PreMeasure: MathPainter → SKPictureRecorder → SKPicture (cached)
   Render:     canvas.DrawPicture(cached, x, y)
   ```

2. **Reuse SKPaint across boxes** — Create 3 paint objects once before the page loop (text, background, border) and change `.Color` per box instead of `new SKPaint` per box.

3. **Pool SKFont by (family, size)** — Cache `SKFont` instances in FontCache so the same font+size pair isn't recreated for every text box.

### P1 — Parse phase (249ms → ~50ms target)

**Problem**: `AngleSharp.Css` engine initializes even though `GetComputedStyle` is only called once (for `<body>`). The CSS engine parses all `<style>` blocks and builds CSSOM on every request.

**Fixes**:

1. **Drop AngleSharp.Css** — Replace the one `GetComputedStyle(<body>)` call with manual parsing of body's inline `style` attribute + the `<style>` block's `body { }` rule via regex. Remove the `AngleSharp.Css` NuGet package entirely.

2. **Pre-compile regex** — `NormalizeWhitespace` uses `Regex.Replace` which compiles on each call. Already using `[GeneratedRegex]` or `RegexOptions.Compiled` but verify.

### P2 — Math pre-measure (294ms → ~150ms target)

**Problem**: 261 unique formulas × MathPainter creation + Measure(). CSharpMath is not thread-safe so can't parallelize.

**Fixes**:

1. **Skip duplicate formulas across sizes** — Currently only inline size is pre-measured. Display size (~20% of formulas) measured on-demand. This is already optimal.

2. **Lazy display-math** — Only create display-size measurement when first requested (already implemented).

### P3 — PDF file size (4.1MB → ~1-2MB target)

**Problem**: `RasterDpi=300` and `EncodingQuality=100` inflate the PDF. Each math formula is drawn as separate PDF operators instead of reused XObjects.

**Fixes**:

1. **Lower RasterDpi to 150** — Pure vector content (text + math) doesn't benefit from 300 DPI. Only affects rasterized fallbacks.

2. **Lower EncodingQuality to 80** — Adequate for text documents.

3. **SKPicture reuse** — When the same formula appears multiple times, drawing the cached SKPicture emits fewer PDF operators than re-drawing via MathPainter.

---

## ACCURACY: Layout Improvements

### P0 — CSS `float` layout

**Problem**: The HTML uses `float: left; width: 7%` for question numbers and `float: left; width: 93%` for question bodies. Currently ignored — everything stacks vertically.

**Fix**: Detect `float: left` in inline style parser. Track float columns:
- When `float: left` + `width: XX%` is found, compute X offset from percentage
- Accumulate floated elements side-by-side until `clear: both`
- Reset to full width after `clear: both`

**Impact**: Question numbers (01, 02...) will appear left-aligned with question text beside them, matching the original HTML layout.

### P1 — CSS `display: inline-block` for options

**Problem**: MCQ options use `display: inline-block; width: 23.5%` to lay out 4 options in a row. Currently each option appears on its own line.

**Fix**: Detect `display: inline-block` + `width: XX%` in style parser. Lay out elements horizontally within the available width, wrapping to next row when exceeded.

**Impact**: Options (a)(b)(c)(d) will appear in a 4-column grid instead of stacked vertically.

### P2 — CSS `width: XX%` parsing

**Problem**: `ParsePxValue` only handles px, pt, em, rem, mm. Percentage widths are ignored.

**Fix**: Add `%` handling in `ParsePxValue` or a separate `ParseWidth` method that takes parent width as context. Return `parentWidth * percentage / 100`.

### P3 — `page-break-before: always`

**Problem**: The HTML uses `.nextPage { page-break-before: always }` but the engine ignores it. Multi-page documents don't break where the HTML expects.

**Fix**: Detect `page-break-before: always` in inline style parser. When found, force `_cursorY` to the next page boundary.

### P4 — Margin collapsing

**Problem**: Adjacent vertical margins stack fully instead of collapsing (CSS spec: adjacent margins should collapse to the larger of the two).

**Fix**: Track the last margin-bottom. When applying margin-top, use `max(lastMarginBottom, currentMarginTop)` instead of summing both.

### P5 — Bengali text shaping

**Problem**: Bengali conjuncts (যুক্তাক্ষর) may not render correctly because SkiaSharp doesn't do complex text shaping (OpenType GSUB/GPOS). Characters appear but ligatures and vowel sign positioning may be wrong.

**Fix**: Use HarfBuzzSharp (MIT, works with SkiaSharp) for text shaping. `HarfBuzzSharp` processes the text through OpenType tables to produce correct glyph sequences for complex scripts.

---

## Priority Order

| # | Task | Speed Impact | Accuracy Impact | Effort |
|---|------|-------------|-----------------|--------|
| 1 | Cache SKPicture for math render | -150ms render | — | Medium |
| 2 | Reuse SKPaint/SKFont in render | -50ms render | — | Low |
| 3 | Drop AngleSharp.Css | -200ms parse | — | Medium |
| 4 | CSS float layout | — | Major | High |
| 5 | CSS inline-block + width% | — | Major | High |
| 6 | page-break-before | — | Medium | Low |
| 7 | Lower DPI/quality | -PDF size 50% | — | Trivial |
| 8 | Margin collapsing | — | Minor | Medium |
| 9 | HarfBuzzSharp text shaping | — | Bengali fix | Medium |

## Target after all optimizations

| Metric | Current | Target |
|--------|---------|--------|
| Total time | 972ms | ~400-500ms |
| PDF size | 4.1MB | ~1.5-2MB |
| Float layout | No | Yes |
| Inline-block | No | Yes |
| Page breaks | No | Yes |
| Bengali shaping | Partial | Full |
