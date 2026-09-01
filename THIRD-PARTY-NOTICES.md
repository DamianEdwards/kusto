# Third-party notices

This product bundles software from the third-party packages listed below. Each
package is the property of its respective copyright holders and is distributed
under its own license. Licenses are reproduced under their canonical SPDX
identifiers; full text is available at the package's listed source URL.

This file is informational; the authoritative license text for each component
is the LICENSE file shipped with that component's NuGet package or source
distribution.

## Direct dependency

| Package | Version | License | Source |
|---|---|---|---|
| `NuGet.Versioning` | 7.3.x | MIT | https://github.com/NuGet/NuGet.Client |
| `ScottPlot` | 5.1.x | MIT | https://github.com/ScottPlot/ScottPlot |
| `Sigstore` | 0.5.x | Apache-2.0 | https://github.com/sigstore/sigstore-dotnet |

## Transitive native rendering stack

`ScottPlot` pulls in the SkiaSharp/HarfBuzz native rendering stack and a few
other utility packages. The relevant runtime dependencies that ship inside the
released archives are:

| Package | Version | License | Source |
|---|---|---|---|
| `SkiaSharp` | 3.x | MIT | https://github.com/mono/SkiaSharp |
| `SkiaSharp.NativeAssets.Win32` | 3.x | MIT | https://github.com/mono/SkiaSharp |
| `SkiaSharp.NativeAssets.Linux` | 3.x | MIT | https://github.com/mono/SkiaSharp |
| `SkiaSharp.NativeAssets.macOS` | 3.x | MIT | https://github.com/mono/SkiaSharp |
| `SkiaSharp.HarfBuzz` | 3.x | MIT | https://github.com/mono/SkiaSharp |
| `HarfBuzzSharp` | 7.x | MIT | https://github.com/mono/SkiaSharp |
| `HarfBuzzSharp.NativeAssets.*` | 7.x | MIT | https://github.com/mono/SkiaSharp |
| `QRCoder` | 1.x | MIT | https://github.com/codebude/QRCoder |
| `System.Drawing.Common` | 9.x | MIT | https://github.com/dotnet/runtime |

The native sidecar libraries shipped next to `kusto`/`kusto.exe` in the release
archives (e.g. `libSkiaSharp.dll`, `libHarfBuzzSharp.dll`, and their `.so` /
`.dylib` equivalents) come from the corresponding `SkiaSharp.NativeAssets.*` /
`HarfBuzzSharp.NativeAssets.*` packages and are covered by the MIT licenses
above.

## Transitive update-verification stack

| Package | Version | License | Source |
|---|---|---|---|
| `Tuf` | 0.5.x | Apache-2.0 | https://github.com/sigstore/sigstore-dotnet |
| `NSec.Cryptography` | 25.x | MIT | https://github.com/ektrah/nsec |
| `libsodium` | 1.0.20.x | ISC | https://github.com/jedisct1/libsodium |

The `libsodium.dll`, `libsodium.so`, and `libsodium.dylib` native sidecars are
included for local Sigstore verification and are covered by the ISC license.

## License summary

The bundled dependencies above use MIT, Apache-2.0, or ISC licenses. The MIT
license is reproduced below for convenience:

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```
