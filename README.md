# QuickLook.Plugin.ThreeMFViewer

A QuickLook viewer for local .3mf, .bambu, and .bambustudio projects. It loads model geometry and Bambu filament colours, including per-triangle paint regions, from the file and renders an interactive Helix viewport. Very dense meshes are simplified by merging nearby vertices while preserving local faces, rather than dropping regularly spaced triangles.

## Build

1. Open `QuickLook.Plugin.ThreeMFViewer.sln` in Visual Studio and build the **Release** configuration, or run `dotnet build QuickLook.Plugin.ThreeMFViewer.sln -c Release` from this directory.
2. Run `Scripts\pack-zip.ps1` from this directory to create `QuickLook.Plugin.ThreeMFViewer.qlplugin`.
3. In QuickLook, open the `.qlplugin` package and install it, then restart QuickLook.

The project targets .NET Framework 4.6.2 and uses WPF and HelixToolkit.Wpf.

## Controls

- Left-drag: rotate the model around its center
- Right-drag: pan the model across the viewport
- Scroll wheel: zoom
- When an embedded image exists, use **Show image** / **Show 3D model** to switch views; scroll to zoom the image.

## Offline harness

Build `harness\QL3MFHarness.csproj` and run `QL3MFHarness <file.3mf> <out.png>` to render a local test file to PNG.

## License

See `LICENSE.txt` for this plugin's license. `QuickLook.Common` includes its own license file.
