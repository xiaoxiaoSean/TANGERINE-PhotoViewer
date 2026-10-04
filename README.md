TANGERINE-PhotoViewer(TPV)

TANGERINE-PhotoViewer(TPV) is a photo viewer which based on Dotnet10.It can be used on Windows.
It supports following formats,and we use some solutions and nuget to view the photo:


| Format    | Registered extensions          | Decoder                                                       |
| --------- | ------------------------------ | ------------------------------------------------------------- |
| JPEG      | `.jpg`, `.jpeg`, `.jfif`       | NetVips; Magick.NET fallback                                  |
| PNG       | `.png`                         | NetVips; Magick.NET fallback                                  |
| GIF       | `.gif`                         | NetVips; Magick.NET fallback. Frame export uses Magick.NET    |
| BMP       | `.bmp`, `.dib`                 | NetVips; Magick.NET fallback                                  |
| TIFF      | `.tif`, `.tiff`                | NetVips; Magick.NET fallback                                  |
| WebP      | `.webp`                        | NetVips; Magick.NET fallback                                  |
| AVIF      | `.avif`                        | NetVips; Magick.NET fallback                                  |
| HEIF      | `.heic`, `.heif`               | NetVips; Magick.NET fallback                                  |
| JPEG 2000 | `.jp2`, `.j2k`                 | NetVips; Magick.NET fallback                                  |
| Photoshop | `.psd`                         | NetVips; Magick.NET fallback with content-based PSD detection |
| TGA       | `.tga`                         | NetVips; Magick.NET fallback with content-based TGA detection |
| OpenEXR   | `.exr`                         | NetVips; Magick.NET fallback                                  |
| Radiance  | `.hdr`                         | NetVips; Magick.NET fallback                                  |
| SVG       | `.svg`                         | NetVips; Magick.NET fallback                                  |
| Netpbm    | `.pbm`, `.pgm`, `.ppm`, `.pnm` | NetVips; Magick.NET fallback                                  |
| PCX       | `.pcx`                         | NetVips; Magick.NET fallback                                  |
| DDS       | `.dds`                         | NetVips; Magick.NET fallback                                  |
| Icon      | `.ico`                         | NetVips; Magick.NET fallback                                  |
