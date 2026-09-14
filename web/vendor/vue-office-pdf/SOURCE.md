# vue-office PDF source adaptation

- Source archive: `D:/Temp/vue-office源码2024-12-30.zip`
- Archive SHA-256: `ef8bb8ac7a02281aec026f0e6d822732d3439cecec8b282cde62f62833618a79`
- Extracted source: `third_party/vue-office-source-2024-12-30/core/packages/js-pdf/src/main.js`
- Upstream package: `@js-preview/pdf` 2.0.10
- Upstream repository: <https://github.com/501351981/vue-office>
- Upstream snapshot `gitHead`: `d20568113bec480f6ca72924f6d0c1e3b0f1fe15`
- License: MIT; preserved in `core/LICENSE`

The archive was supplied and authorized by the user for this project. The archive's
paid-source distribution notes are retained with the local extraction; this work
does not publish that source package publicly.

The local `src/main.ts` keeps the upstream renderer's imperative scrolling wrapper,
absolute page canvases, visible-range calculation, and virtual canvas mounting. It
replaces the embedded PDF.js/data-script/unpkg loader with a per-instance injected
`openPdf` function backed by the application's installed PDF.js engine. It also adds
per-page dimensions, cumulative mixed-size page layout, bounded canvas backing stores,
an expanded horizontal scroll surface for wide zoom, render cancellation, stale-result
guards, resize-aware virtual mounting, loading-task destruction, and complete
observer/listener/timer cleanup. The upstream download helper was omitted
because this viewer intentionally exposes preview controls only.

Scroll sizing uses the inner scrollport width and a stable scrollbar gutter, avoiding
horizontal overflow caused only by the vertical scrollbar. Truly oversized zoomed
pages retain horizontal scrolling.
