# Large Image Decode Fallback

## Goal

Images larger than 100 MiB, or whose decoded raster exceeds 32 million pixels, must never enter the ordinary full-image bitmap path merely because their loader cannot read an arbitrary rectangle. Format selection uses file signatures and decoder metadata, not file extensions. A visible image must have a bounded display bitmap even when the source file contains billions of pixels.

## Decode paths

1. `ImageLoader.Load` reads dimensions before allocating a full bitmap. A large Photoshop PSD/PSB with a supported merged composite uses `PsdCompositeReader`; the reader understands raw, PackBits, ZIP, and ZIP with prediction. Raw/PackBits rows can be addressed by offset. ZIP rows are inflated sequentially and discarded after their viewport samples are accumulated.
2. Other large sources use a libvips sequential loader with `memory: false`. The first preview is resized to display dimensions in a sequential pipeline. A later visible rectangle uses native `PARTIAL` access when available; otherwise `LoadSequentialRegion` opens a new sequential stream, crops the source rectangle, resizes the crop, and rotates only that bounded result.
3. If libvips cannot decode a supported non-PSD image, Magick.NET is attempted with capped pixel-cache heap, mapped-cache, and disk resources. The fallback still depends on the individual decoder and can fail when its working storage exceeds the cap. This path must report the actual decoder error and stagecode, rather than silently presenting a stale preview.

Sequential formats cannot provide instantaneous random access without an index: a request near the bottom may need a scan from the beginning. A whole-image fit preview may also scan all source rows, though it does not retain a full raster. A codec or native delegate may allocate memory outside the pixel-cache limits. The application cannot promise that every arbitrarily large or corrupt image opens with finite time and storage.

## Cancellation and state

Long image work runs asynchronously with cancellation tokens. PSD decoding checks cancellation while reading rows. The libvips pipeline attaches progress and kill signaling. A canceled operation must not replace the current image after its generation changes. Native library calls that do not honor cancellation immediately may finish their current call before returning; this limitation is surfaced rather than described as a forced kill.

## Calling sequence

`MainWindow.OpenAsync` calls `ImageLoader.Load` on a worker task. `QueueRender` requests a new region after zoom or pan. `RenderAsync` computes the visible source rectangle and calls `ImageLoader.LoadRegionParallelAsync`; that method selects PSD, native partial, sequential, or resource-limited Magick decoding. At minimum zoom, `RefreshPreviewAsync` asks `ImageLoader.LoadPreview` for a display-sized image. `Stop_Click` cancels the active operation and leaves the previously displayed content in place.
