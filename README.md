# Markdown Viewer

A Windows app for reading local Markdown files, built with C#, WPF and Markdig. Open documents in tabs, navigate by headings and pick up where you left off. The interface is in English. The viewer does not modify Markdown files.

## Features

- Headings, lists, tables, quotes, task checkboxes, links and images.
- Selectable text, basic syntax highlighting and a **Copy** button on code blocks.
- Collapsible table of contents, heading anchors and relative links to Markdown files.
- Case-insensitive search with match highlighting and next/previous navigation.
- Light, dark and system themes; adjustable text size, reading width and outline width.
- Drag to reorder tabs; restore tab order, the active document and reading positions on launch.
- Automatic reload when files change in an external editor.

## Run

To run a published build, install **Microsoft .NET Desktop Runtime 10 (x64)**, extract the ZIP and run `MarkdownViewer.exe`. Keep the extracted files together: the runtime is not bundled. To create the ZIP from source, see [Build and publish](#build-and-publish).

Open files with **Ctrl+O**, drag them into the window or pass paths on the command line:

```powershell
.\MarkdownViewer.exe ".\README.md"
```

Opening a file again activates its existing tab. Launching the same copy of the app again sends files to the running window.

The **…** menu contains theme and reading-width settings, reload, **Show in File Explorer** and **About**.

## Keyboard shortcuts

| Action | Shortcut |
| --- | --- |
| Open files | `Ctrl+O` |
| Next / previous tab | `Ctrl+Tab` / `Ctrl+Shift+Tab` |
| Close tab | `Ctrl+W` |
| Quit application | `Ctrl+Q`, or `Esc` when search is closed |
| Toggle table of contents | `Ctrl+Shift+O` |
| Show / hide search | `Ctrl+F` |
| Close search | `Esc` |
| Next / previous match in the search field | `Enter` / `Shift+Enter` |
| Scroll by line | `Up` / `Down` |
| Scroll by page | `Page Up` / `Page Down` |
| Scroll down / up one page | `Space` / `Shift+Space` |
| Zoom in / out | `Ctrl+mouse wheel`, `Ctrl++` / `Ctrl+-` |
| Reset zoom | `Ctrl+0` |
| Reload file | `F5` |

When search is open, **Ctrl+F or Esc closes it** and returns focus to the document. Press Esc again to quit, or use Ctrl+Q to quit at any time. Native dialogs keep their standard keyboard behavior. Scrolling shortcuts leave text entry in the search field and navigation in the outline intact.

## Supported files and limits

The file picker includes `.md`, `.markdown`, `.mdown`, `.mkd` and `.txt`. Files are read as UTF-8, or UTF-16/UTF-32 when marked with a BOM. Non-UTF-8 files without a BOM fall back to Windows-1251. The status bar shows the encoding used.

- Maximum document size: **20 MiB**.
- Search returns up to 10,000 matches and highlights the first 100. All returned matches are accessible with next/previous controls; a `+` indicates more results beyond the limit.
- PNG, JPEG, GIF, BMP and TIFF images are supported. GIFs are static; SVG is not supported. Remote images load over HTTP or HTTPS.
- HTML is shown as text. Scripts and CSS are not executed. Mermaid, math rendering, editing and PDF export are not supported.
- Rendering may differ from GitHub. Syntax highlighting is basic; very large code blocks use plain text.
- Reload preserves the scroll offset, so large edits can change the content at that position. If a loaded file becomes unavailable, the last loaded version stays visible with a notice.

## Settings and sessions

Preferences, window layout, recent files and reading positions are saved automatically to `%LocalAppData%\MarkdownViewer\settings.json`. Set `MARKDOWNVIEWER_SETTINGS_DIR` before launch to use a different settings folder.

On launch, the previous session is restored before opening files passed on the command line. Unavailable files are skipped with a notice. Closing a tab removes it from the saved session; closing all tabs leaves the next launch empty.

## Open by double-click

In File Explorer, right-click a Markdown file, choose **Open with** and select `MarkdownViewer.exe`. You can then make it the default in Windows.

To add the viewer to **Open with** for the current user, run this optional script from the extracted application folder:

```powershell
.\Register-FileAssociation.ps1
```

From the source repository, run `.\scripts\Register-FileAssociation.ps1` after publishing. Both versions locate the published executable automatically; use `-ExecutablePath "C:\Apps\MarkdownViewer\MarkdownViewer.exe"` to specify another location. The script does not change the current default application.

## Build and publish

Requirements: Windows and a **.NET 10 SDK** compatible with [global.json](global.json). Running the app and smoke tests also requires the **.NET Desktop Runtime 10**. The first build needs NuGet access to restore dependencies.

Run from the repository root in PowerShell:

```powershell
.\scripts\build.ps1 -RunTests
.\scripts\publish.ps1
```

`build.ps1 -RunTests` builds the solution and runs the smoke tests. Omit `-RunTests` to build only. Test screenshots are written to `artifacts/qa/`.

`publish.ps1` creates the application folder and ZIP in `artifacts/`:

- `MarkdownViewer-win-x64-framework-dependent/`
- `MarkdownViewer-win-x64-framework-dependent.zip`

The published build targets Windows x64 and requires the separately installed runtime. Build output is ignored by Git.

Third-party notices and licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
