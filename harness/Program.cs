// Offline renderer: identical to the plugin's View() so we can validate the
// 3MF parse + coloring without a QuickLook round-trip.
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using QuickLook.Plugin.ThreeMFViewer;

namespace QL3MFHarness
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: QL3MFHarness <file.3mf> <out.png>");
                return 2;
            }

            var scene = ThreeMFScene.Load(args[0]);

            Console.WriteLine($"Title   : {scene.Title}  by {scene.Designer}");
            Console.WriteLine($"Filaments: {(scene.FilamentColours.Length == 0 ? "(none)" : string.Join(", ", scene.FilamentColours))}");
            Console.WriteLine($"Parts   : {scene.PartCount}   Render meshes: {scene.Parts.Count}   Simplified: {scene.Simplified}");
            foreach (var d in scene.DebugLog)
                Console.WriteLine("  DBG " + d);

            if (scene.Parts.Count == 0)
                return 1;

            foreach (var part in scene.Parts)
                Console.WriteLine(
                    $"  part fil={part.Filament} ({scene.FilamentBrushHex(part.Filament)}) " +
                    $"verts={part.Mesh.Positions.Count} tris={part.Mesh.TriangleIndices.Count / 3}");

            var ui = new System.Windows.Threading.DispatcherFrame();
            var t = new System.Threading.Thread(_ =>
            {
                try
                {
                    var vp = new HelixViewport3D
                    {
                        Width = 1100,
                        Height = 850,
                        Background = new SolidColorBrush(Color.FromRgb(0x24, 0x26, 0x2E)),
                    };
                    vp.Children.Add(new DefaultLights());

                    foreach (var part in scene.Parts)
                    {
                        var model = new GeometryModel3D
                        {
                            Geometry = part.Mesh,
                            Material = new DiffuseMaterial(scene.FilamentBrush(part.Filament)),
                            BackMaterial = new DiffuseMaterial(scene.FilamentBrush(part.Filament)),
                            Transform = new MatrixTransform3D(part.Transform),
                        };
                        vp.Children.Add(new ModelVisual3D { Content = model });
                    }

                    // explicit camera: Bambu-style isometric, derived from model bounds
                    double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
                    double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
                    foreach (var part in scene.Parts)
                    {
                        var b = part.Mesh.Bounds;
                        foreach (var p in new[]
                        {
                            new Point3D(b.X, b.Y, b.Z),
                            new Point3D(b.X + b.SizeX, b.Y, b.Z),
                            new Point3D(b.X, b.Y + b.SizeY, b.Z),
                            new Point3D(b.X, b.Y, b.Z + b.SizeZ),
                            new Point3D(b.X + b.SizeX, b.Y + b.SizeY, b.Z),
                            new Point3D(b.X + b.SizeX, b.Y, b.Z + b.SizeZ),
                            new Point3D(b.X, b.Y + b.SizeY, b.Z + b.SizeZ),
                            new Point3D(b.X + b.SizeX, b.Y + b.SizeY, b.Z + b.SizeZ),
                        })
                        {
                            var t = part.Transform.Transform(p);
                            minX = Math.Min(minX, t.X); minY = Math.Min(minY, t.Y); minZ = Math.Min(minZ, t.Z);
                            maxX = Math.Max(maxX, t.X); maxY = Math.Max(maxY, t.Y); maxZ = Math.Max(maxZ, t.Z);
                        }
                    }
                    var center = new Point3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
                    var radius = new[]
                    {
                        maxX - minX, maxY - minY, maxZ - minZ
                    }.Max() / 2 * 1.2;
                    var dir = new Vector3D(-0.8, -1.0, 0.4);
                    dir.Normalize();
                    var camPos = center - dir * (radius * 2.0);

                    vp.Camera = new PerspectiveCamera
                    {
                        Position = camPos,
                        LookDirection = center - camPos,
                        UpDirection = new Vector3D(0, 0, 1),
                        FieldOfView = 45,
                    };

                    vp.ApplyTemplate();
                    vp.Measure(new Size(1100, 850));
                    vp.Arrange(new Rect(0, 0, 1100, 850));
                    vp.UpdateLayout();

                    // real render pass: WPF 3D composes only inside a shown window,
                    // so we show one parked far off the desktop and grab it there
                    var win = new Window
                    {
                        Left = -3000,
                        Top = -3000,
                        Width = 1100,
                        Height = 850,
                        ShowInTaskbar = false,
                        ShowActivated = false,
                        WindowStyle = WindowStyle.None,
                        Content = vp,
                    };
                    win.Show();
                    for (var i = 0; i < 8; i++)
                        win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                    // let composition settle
                    for (var i = 0; i < 5; i++)
                        vp.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

                    var rtb = new RenderTargetBitmap(1100, 850, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(vp);

                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = File.Create(args[1]))
                        enc.Save(fs);
                    Console.WriteLine($"Wrote {args[1]}");
                    win.Close();
                }
                catch (Exception e)
                {
                    Console.WriteLine("RENDER FAIL: " + e);
                }
                ui.Continue = false;
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
            System.Windows.Threading.Dispatcher.PushFrame(ui);

            return File.Exists(args[1]) ? 0 : 1;
        }
    }
}
