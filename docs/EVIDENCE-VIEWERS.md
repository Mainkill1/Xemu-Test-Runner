# View diagnostics without saving every artifact

Open **Evidence**, select a run, and select a file. `/results/view?run=RUN_ID&file=RELATIVE_PATH` opens its embedded read-only viewer. The Diagnostics page links to the active run's evidence. Saved files remain on the tester; the browser fetches only the selected preview. **Download original** is a separate explicit action.

## Supported views

| Artifact | Viewer |
| --- | --- |
| PNG, JPEG, GIF, WebP, BMP | Image with fit, actual-size, zoom, dimensions and scrolling. |
| CSV / TSV | Spreadsheet-style grid, sticky column/row headers, numeric sorting, text filtering, 50-row pages, delimiter selection and optional header row. |
| JSON / JSONL / NDJSON | Indented text with a raw/pretty toggle and optional line wrapping. |
| Logs, text, Markdown, TOML, INI, XML, YAML, HTML and SVG | Read-only text; scripts/markup are not executed. |
| Dumps, cores, ZIP and other binary artifacts | Explanation and explicit original download; use the generated text/JSON report for inline inspection. |

Click or keyboard-select a CSV cell to see its exact value. Enable **Exact numbers** to disable display rounding. Filtering and sorting apply to the loaded preview, not unseen rows elsewhere in a large file. CSV commas, quoted newlines and doubled quotes are parsed as fields; invalid CSV is shown as raw text with an explanation, not repaired silently. Formulas are never evaluated.

JSON formatting works on the original tokens rather than serializing parsed JavaScript numbers. It therefore retains large integer IDs, fractional digits, duplicate keys and string contents. Invalid or partial JSON is labeled and displayed as raw text. Pretty printing does not validate that a test passed.

## Two decimal places, without changing evidence

Human-facing numeric telemetry and Markdown reports use two decimal places. For example, `1.1557 ms / 0.23113999999999998% duty` displays as **1.16 ms / 0.23% duty**. CSV decimal cells are rounded for display; identifiers, leading-zero strings, integers and values outside JavaScript's safe integer magnitude stay unchanged.

Original CSV, JSON, measurements, comparison inputs, hashes, run IDs and counters are not rewritten. A value that displays as 0.00 can still be nonzero; select the cell, enable Exact numbers, or read original evidence when precision matters.

## Bounds and operation policy

Text/CSV/JSON previews read at most the first 1 MiB plus a sentinel byte; images are limited to 16 MiB. Larger text previews are explicitly partial. CSV stops after 10,000 data rows, 128 columns or a 64 KiB cell. A trailing incomplete record is omitted from a byte-truncated CSV preview. JSON pretty output is also bounded; excessive depth/expansion falls back to the original text. Binary artifacts are not fetched just to discover that no viewer exists.

The viewer uses the existing ranged artifact endpoint. It does not mount disks, unpack archives, invoke diagnostics, run tests or rewrite results. Existing path validation and benchmark bulk-transfer restrictions still apply. A refused read is shown as an error, without a shell fallback or automatic retry. A viewer page being reachable does not imply that artifact transfer is currently allowed.

The page has no third-party scripts, spreadsheets engine, CDN or new runtime dependency. Artifact content is written as text nodes or an allowed raster image blob, never inserted as HTML. Image blobs are released when replaced or when the page closes. Preview contents remain subject to browser memory and ordinary HTTP transfer; this avoids mandatory local-file downloads, not all network reads.

## Tests

`python scripts/check-viewers.py` runs Chromium fixtures for rounding, CSV escaping/sort/filter/paging, exact numbers, large JSON integers, image zoom, malformed/oversized files, untrusted content, policy errors and explicit-download behavior. `scripts/check-browser.py` covers the Home/Evidence/Diagnostics entry points. AgentChecks verifies the real HTTP page and that Markdown rounding leaves JSON/CSV precision intact.
