# Third-party software

Markdown Viewer uses Markdig 1.6.0, by Alexandre Mutel, under the BSD 2-Clause license.
Source: https://github.com/xoofx/markdig
The complete license is included in `licenses/Markdig.txt`.

Markdown Viewer uses Microsoft.ML.Tokenizers 2.0.1 and Microsoft.ML.Tokenizers.Data.O200kBase 2.0.1 from ML.NET for local token counting. Both packages use the MIT license. Their [license](licenses/ML-NET-LICENSE.txt) and [third-party notices](licenses/ML-NET-THIRD-PARTY-NOTICES.txt) are included with the application. [ML.NET source](https://github.com/dotnet/machinelearning)

The tokenization packages depend on Google.Protobuf 3.30.2, distributed under the BSD 3-Clause license. The [license](licenses/Google-Protobuf-LICENSE.txt) is included with the application. [Protocol Buffers source](https://github.com/protocolbuffers/protobuf/tree/v30.2)

The File Open, Toc and Search toolbar icons are from Google's Material Symbols Outlined, under the Apache License 2.0.
Source: https://github.com/google/material-design-icons/tree/master/symbols
Only these three 24px, weight-400, unfilled SVG paths are included, converted to WPF geometries with their original coordinates preserved. No icon font is bundled.
The complete license is included in `licenses/Material-Symbols-LICENSE.txt`.

This framework-dependent build requires a separately installed Microsoft .NET Desktop Runtime 10 (x64). The runtime is not bundled with the application.
