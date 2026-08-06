# SimpleIDE.Widgets

Reusable, toolkit-generic GTK# 3 control library split out of [SimpleIDE](https://github.com/jamesplotts/simpleide) - a lightweight VB.NET IDE for Linux. Everything here is Amiga-bevel "CustomDraw" widgets (Button, CheckBox, ComboBox, ListBox, Scrollbar, SpinButton, TextBox, TextOutput, ColorPicker), plus the theme/settings foundation they depend on (`ThemeManager`, `SettingsManager`, `EditorTheme`, `SyntaxColorSet`), and the litehtml-backed embedded HTML view (`CustomDrawHtmlView`) with its native P/Invoke interop.

None of this depends on SimpleIDE's own IDE-domain code (`ProjectManager`, `Editors`, `Dialogs`, etc.) - that's the point. A fork targeting a different platform (e.g. Windows) can reference just this assembly plus its own `SimpleIDE.<Backend>.vbproj` implementing [`IEmbeddedBrowserView`](Interfaces/IEmbeddedBrowserView.vb) against a native browser control (WebView2, CEF), without touching or understanding anything IDE-specific.

## Building

Requires .NET 8 SDK and GTK# 3 (`GtkSharp`/`CairoSharp` NuGet packages, restored automatically).

```bash
dotnet build SimpleIDE.Widgets.vbproj
```

### litehtml native shim (optional)

`CustomDrawHtmlView` renders HTML via a small C++ shim over [litehtml](https://github.com/litehtml/litehtml) (a git submodule here). Building the shim is optional - without it, `CustomDrawHtmlView`/`LiteHtmlDocumentHandle.IsAvailable` reports unavailable and callers fall back accordingly - but building it enables real HTML rendering:

```bash
git submodule update --init --recursive
./native/build-native.sh
```

This produces `native/build/lib/liblitehtml_shim.so`, which `SimpleIDE.Widgets.vbproj` copies into its own (and any consuming project's) output directory automatically.

## Used by

[SimpleIDE](https://github.com/jamesplotts/simpleide) consumes this via a sibling-directory `ProjectReference` - clone this repo as a sibling of `SimpleIDE`'s own checkout (i.e. both under the same parent directory) for `SimpleIDE.vbproj`'s `..\SimpleIDE.Widgets\SimpleIDE.Widgets.vbproj` reference to resolve.
