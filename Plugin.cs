using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Threading;
using System.Threading.Tasks;
using HelixToolkit.Wpf;
using QuickLook.Common.Plugin;

namespace QuickLook.Plugin.ThreeMFViewer
{
    public class Plugin : IViewer
    {
        private int _viewGeneration;

        // QuickLook's own working set before our preview started, so a big preview is
        // trimmed back to what this machine normally uses, not to a fixed number.
        private static long _baselineWs;
        private static volatile bool _previewOpen;
        private const long Slack = 40L * 1024 * 1024;   // extra memory left resident above that

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSizeEx(IntPtr process, IntPtr min, IntPtr max, uint flags);
        // Higher than baseline viewers (priority 0): on QL 4.5 the built-in
        // HelixViewer cannot load assimp.dll for .3mf, so we should win the routing.
        public int Priority => 10;

        // ---------------- trap log -------------------------------------------
        // Appends diagnostic lines to %TEMP%\QuickLook.ThreeMFViewer.trap.log so
        // failures and stolen mouse events can be diagnosed from the user's side.
        // Quietly no-ops in the worst case; it must never crash the host.
        private static int _trapLines;
        private static readonly object TrapLock = new object();

        internal static void Trap(string msg)
        {
            try
            {
                if (_trapLines > 2000) return;
                lock (TrapLock)
                {
                    var dir = Environment.GetEnvironmentVariable("TEMP");
                    var file = Path.Combine(string.IsNullOrEmpty(dir) ? @"C:\Temp" : dir,
                        "QuickLook.ThreeMFViewer.trap.log");

                    try
                    {
                        if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024)
                            File.Delete(file);   // fresh run, fresh log
                    }
                    catch { /* keep going */ }

                    File.AppendAllText(file, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
                    _trapLines++;
                }
            }
            catch { /* never crash the host */ }
        }

        // Keep the user's file path and profile folder out of the log.
        private static string Scrub(string text, string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path)) text = text.Replace(path, "<file>");
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(profile)) text = text.Replace(profile, "<user folder>");
            }
            catch { /* never crash the host */ }
            return text;
        }

        // ---------------- orbit around model pivot ----------------------------
        // Rotate the camera around the scene center while keeping that point
        // under the cursor's pivot, like the standard 3D preview interaction.
        private static Vector3D Rotate(Vector3D vector, Quaternion rotation)
        {
            var transform = new RotateTransform3D(new QuaternionRotation3D(rotation));
            return transform.Transform(vector);
        }

        private static BitmapImage DecodeThumbnail(byte[] data)
        {
            using (var stream = new MemoryStream(data, false))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }

        // Slide the camera parallel to the screen so the model follows the
        // pointer without changing its orientation or zoom.
        private static void Pan(HelixViewport3D vp, double modelRadius, double dx, double dy)
        {
            try
            {
                var cam = vp.Camera as PerspectiveCamera;
                if (cam == null) { Trap("pan: camera is not a PerspectiveCamera"); return; }

                var look = cam.LookDirection;
                if (look.Length < 1e-9) return;
                look.Normalize();
                var right = Vector3D.CrossProduct(look, cam.UpDirection);
                if (right.Length < 1e-9) return;
                right.Normalize();
                var up = Vector3D.CrossProduct(right, look);
                up.Normalize();

                var worldPerPixel = 2.0 * modelRadius / Math.Max(64.0, vp.ActualHeight);
                cam.Position += (-dx * right + dy * up) * worldPerPixel;
            }
            catch (Exception e)
            {
                Trap("pan exception: " + e.Message);
            }
        }

        private static void Orbit(HelixViewport3D vp, double dx, double dy)
        {
            try
            {
                var cam = vp.Camera as PerspectiveCamera;
                if (cam == null) { Trap("orbit: camera is not a PerspectiveCamera"); return; }

                // Pivot on the current look target so a previous pan is kept.
                var pivot = cam.Position + cam.LookDirection;
                var offset = cam.Position - pivot;
                var up = cam.UpDirection;
                if (offset.Length < 1e-9 || up.Length < 1e-9) return;
                up.Normalize();

                // Horizontal motion yaws around camera-up; vertical motion
                // pitches around camera-right. Both keep the scene center fixed.
                var yaw = new Quaternion(up, -dx * 0.35);
                offset = Rotate(offset, yaw);
                up = Rotate(up, yaw);

                var look = -offset;
                look.Normalize();
                var right = Vector3D.CrossProduct(look, up);
                if (right.Length < 1e-9) return;
                right.Normalize();
                // Screen Y grows downward; negate it so dragging upward rotates
                // the model upward in the expected direction.
                var pitch = new Quaternion(right, -dy * 0.35);
                offset = Rotate(offset, pitch);
                up = Rotate(up, pitch);

                cam.Position = pivot + offset;
                cam.LookDirection = pivot - cam.Position;
                cam.UpDirection = up;
            }
            catch (Exception e)
            {
                Trap("orbit exception: " + e.Message);
            }
        }

        public void Init()
        {
            NoteBaseline();   // QuickLook's size at startup, before any preview window exists
        }

        // Keep the lowest working set seen: that is how big this machine's QuickLook is when idle.
        private static void NoteBaseline()
        {
            var ws = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
            if (_baselineWs == 0 || ws < _baselineWs) _baselineWs = ws;
        }

        public bool CanHandle(string path)
        {
            if (Directory.Exists(path))
                return false;

            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".3mf" && ext != ".bambu" && ext != ".bambustudio")
                return false;

            // Header check: ZIP local-file signature, so a renamed non-archive won't be claimed.
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var b = new byte[4];
                    if (fs.Read(b, 0, 4) < 4)
                        return false;
                    return b[0] == 0x50 && b[1] == 0x4B && (b[2] == 0x03 || b[2] == 0x05 || b[2] == 0x07);
                }
            }
            catch
            {
                return false;
            }
        }

        public void Prepare(string path, ContextObject context)
        {
            context.PreferredSize = new Size { Width = 760, Height = 560 };
        }

        public void View(string path, ContextObject context)
        {
            var generation = Interlocked.Increment(ref _viewGeneration);
            var dispatcher = Application.Current.Dispatcher;
            if (!_previewOpen) // otherwise a previous preview of ours is still being cleaned up
            {
                NoteBaseline();
                _previewOpen = true;
            }
            context.IsBusy = true;
            context.Title = Path.GetFileName(path);
            Trap($"===== View {Path.GetExtension(path)} ({new FileInfo(path).Length / 1024.0 / 1024.0:0.#} MB)");

            // Parsing and mesh preparation are CPU-heavy. Keep them off QuickLook's UI
            // thread so large 3MFs do not make the whole preview appear hung.
            // Show a live count while loading so a big file does not look stuck.
            var progress = new LoadProgress();
            var loading = new TextBlock
            {
                Margin = new Thickness(16),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                FontSize = 16,
                Text = "Loading…",
            };
            var timer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background, dispatcher)
            { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (s, e) =>
            {
                if (generation != Volatile.Read(ref _viewGeneration)) { timer.Stop(); return; }
                var n = progress.Triangles;
                loading.Text = n > 0
                    ? $"Loading… {n:N0} triangles read\n{progress.Phase}"
                    : $"Loading… {progress.Phase}";
                // The busy indicator may cover the view, so show the count in the title too.
                context.Title = Path.GetFileName(path) + (n > 0 ? $"   —   loading {n:N0} triangles" : "   —   loading…");
            };
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (generation != Volatile.Read(ref _viewGeneration)) return;
                context.ViewerContent = loading;
                timer.Start();
            }));

            Task.Run(() => ThreeMFScene.Load(path, progress)).ContinueWith(task =>
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    timer.Stop();
                    // A newer preview may have replaced this one while loading.
                    if (generation != Volatile.Read(ref _viewGeneration)) return;
                    if (task.IsFaulted)
                    {
                        var error = task.Exception?.GetBaseException() ?? new Exception("Unknown load error.");
                        Trap("VIEW EXCEPTION: " + Scrub(error.ToString(), path));
                        context.ViewerContent = new TextBlock
                        {
                            Margin = new Thickness(16),
                            Text = "3MF preview failed:\n" + error.Message,
                        };
                        context.IsBusy = false;
                        return;
                    }
                    ShowScene(path, context, task.Result);
                }));
            }, TaskScheduler.Default);
        }

        private void ShowScene(string path, ContextObject context, ThreeMFScene scene)
        {
            try
            {
                Trap($"parse: parts={scene.PartCount} renderMeshes={scene.Parts.Count} palette={scene.FilamentColours.Length} simplified={scene.Simplified} center={scene.Center:0} radius={scene.Radius:0}");

                // info bar, docked to the window bottom so it can never be pushed away
                var info = new TextBlock
                {
                    Margin = new Thickness(10, 6, 10, 8),
                    TextWrapping = TextWrapping.Wrap,
                    Background = Brushes.Transparent,
                };

                if (scene.Parts.Count > 0)
                {
                    Trap("branch: 3D viewport");
                    // ---- colored 3D view, transparent over the system backdrop ----
                    var viewport = new HelixViewport3D
                    {
                        ZoomExtentsWhenLoaded = true,
                        Background = Brushes.Transparent,
                        ShowViewCube = false,          // the square/ring widget bottom-right
                        ShowCoordinateSystem = false,
                    };

                    // --- left-drag orbits and right-drag pans; capture input so
                    //     QuickLook's window drag does not take over ---
                    var rotating = false;
                    var panning = false;
                    var last = default(Point);

                    viewport.PreviewMouseLeftButtonDown += (s, e) =>
                    {
                        e.Handled = true;
                        rotating = true;
                        last = e.GetPosition(viewport);
                        Mouse.Capture(viewport);
                    };
                    viewport.PreviewMouseMove += (s, e) =>
                    {
                        if ((!rotating && !panning) || Mouse.Captured != viewport) return;
                        var p = e.GetPosition(viewport);
                        var dx = p.X - last.X;
                        var dy = p.Y - last.Y;
                        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return;
                        last = p;
                        if (rotating) Orbit(viewport, dx, dy);
                        else Pan(viewport, scene.Radius, dx, dy);
                    };
                    viewport.PreviewMouseLeftButtonUp += (s, e) =>
                    {
                        if (rotating)
                        {
                            rotating = false;
                            if (Mouse.Captured == viewport) Mouse.Capture(null);
                            e.Handled = true;
                        }
                    };
                    viewport.PreviewMouseRightButtonDown += (s, e) =>
                    {
                        e.Handled = true;
                        panning = true;
                        last = e.GetPosition(viewport);
                        Mouse.Capture(viewport);
                    };
                    viewport.PreviewMouseRightButtonUp += (s, e) =>
                    {
                        if (panning)
                        {
                            panning = false;
                            if (Mouse.Captured == viewport) Mouse.Capture(null);
                            e.Handled = true;
                        }
                    };
                    viewport.LostMouseCapture += (s, e) =>
                    {
                        rotating = false;
                        panning = false;
                    };

                    var lights = new DefaultLights();
                    viewport.Children.Add(lights);

                    foreach (var part in scene.Parts)
                    {
                        var model = new GeometryModel3D
                        {
                            Geometry = part.Mesh,
                            Material = new DiffuseMaterial(scene.FilamentBrush(part.Filament)),
                            BackMaterial = new DiffuseMaterial(scene.FilamentBrush(part.Filament)),
                            Transform = new MatrixTransform3D(part.Transform),
                        };
                        var vis = new ModelVisual3D { Content = model };
                        viewport.Children.Add(vis);
                    }

                    info.Inlines.Add(new Run($"{scene.PartCount} part(s)" +
                                (scene.Simplified ? "   (simplified for preview)" : string.Empty)));
                    info.Inlines.Add(new Run("\nLeft-drag to rotate, right-drag to pan, scroll to zoom.")
                    {
                        Foreground = new SolidColorBrush(Color.FromRgb(176, 176, 176)),
                    });

                    var preview = new Grid { ClipToBounds = true };
                    preview.Children.Add(viewport);

                    BitmapImage thumbnail = null;
                    if (scene.EmbeddedThumbnail != null)
                    {
                        try
                        {
                            thumbnail = DecodeThumbnail(scene.EmbeddedThumbnail);
                        }
                        catch (Exception e)
                        {
                            Trap("embedded thumbnail decode failed: " + e.Message);
                        }
                    }

                    if (thumbnail != null)
                    {
                        var scale = new ScaleTransform(1, 1);
                        var imageView = new Image
                        {
                            Source = thumbnail,
                            Stretch = Stretch.Uniform,
                            RenderTransformOrigin = new Point(0.5, 0.5),
                            RenderTransform = scale,
                            Visibility = Visibility.Collapsed,
                        };
                        imageView.MouseWheel += (s, e) =>
                        {
                            var factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
                            var zoom = Math.Max(1.0, Math.Min(6.0, scale.ScaleX * factor));
                            scale.ScaleX = zoom;
                            scale.ScaleY = zoom;
                            e.Handled = true;
                        };

                        var toggleImage = new Button
                        {
                            Content = "Show image",
                            Padding = new Thickness(10, 5, 10, 5),
                            Margin = new Thickness(12),
                            HorizontalAlignment = HorizontalAlignment.Right,
                            VerticalAlignment = VerticalAlignment.Top,
                            Background = new SolidColorBrush(Color.FromArgb(220, 48, 48, 48)),
                            Foreground = Brushes.White,
                            BorderBrush = new SolidColorBrush(Color.FromRgb(110, 110, 110)),
                            BorderThickness = new Thickness(1),
                            Focusable = false,
                        };
                        bool showingImage = false;
                        var controlHint = (Run)info.Inlines.LastInline;
                        toggleImage.Click += (s, e) =>
                        {
                            showingImage = !showingImage;
                            viewport.Visibility = showingImage ? Visibility.Collapsed : Visibility.Visible;
                            imageView.Visibility = showingImage ? Visibility.Visible : Visibility.Collapsed;
                            toggleImage.Content = showingImage ? "Show 3D model" : "Show image";
                            controlHint.Text = showingImage
                                ? "\nImage view: scroll to zoom."
                                : "\nLeft-drag to rotate, right-drag to pan, scroll to zoom.";
                            if (!showingImage)
                            {
                                scale.ScaleX = 1;
                                scale.ScaleY = 1;
                            }
                        };

                        preview.Children.Add(imageView);
                        preview.Children.Add(toggleImage);
                    }

                    var dock = new DockPanel();
                    DockPanel.SetDock(info, Dock.Bottom);
                    dock.Children.Add(info);
                    dock.Children.Add(preview);
                    context.ViewerContent = dock;
                }
                else
                {
                    Trap("branch: no-picture notice");
                    // ---- no picture: the model IS the preview, never a fallback image ----
                    info.Text = "No renderable model found in this file.";
                    info.VerticalAlignment = VerticalAlignment.Bottom;

                    var dock = new DockPanel();
                    DockPanel.SetDock(info, Dock.Bottom);
                    dock.Children.Add(info);
                    context.ViewerContent = dock;
                }

                // Match QuickLook's built-in STL viewer: identify the local file
                // in the title bar rather than using embedded designer metadata.
                context.Title = Path.GetFileName(path);
            }
            catch (Exception e)
            {
                // Never crash the host process: degrade to an error panel.
                Trap("VIEW EXCEPTION: " + Scrub(e.ToString(), path));
                context.ViewerContent = new TextBlock
                {
                    Margin = new Thickness(16),
                    Text = "3MF preview failed:\n" + e.Message,
                };
                context.Title = Path.GetFileName(path);
            }
            finally
            {
                context.IsBusy = false;
            }
        }

        public void Cleanup()
        {
            var generation = Interlocked.Increment(ref _viewGeneration);
            // Once QuickLook has dropped the preview, release its large lists and 3D
            // render data now; an idle process may not collect them for a long time.
            Task.Delay(1000).ContinueWith(_ =>
            {
                if (generation != Volatile.Read(ref _viewGeneration)) return; // another preview started
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                // A hidden preview window gets no render pass, so WPF can keep the closed
                // preview's 3D buffers resident until the next preview is shown. If QuickLook
                // is far above the size it had before our preview, trim it back to about that
                // size (plus some slack): the most recently used pages stay resident, and
                // the cap is lifted at once.
                var self = System.Diagnostics.Process.GetCurrentProcess();
                var target = _baselineWs + Slack;
                if (self.WorkingSet64 > _baselineWs + 2 * Slack)
                {
                    SetProcessWorkingSetSizeEx(self.Handle, (IntPtr)Math.Min(16L * 1024 * 1024, target), (IntPtr)target, 0);
                    SetProcessWorkingSetSizeEx(self.Handle, (IntPtr)(200L * 1024), new IntPtr(IntPtr.Size == 8 ? 1L << 40 : int.MaxValue), 0);
                }
                _previewOpen = false;
            }, TaskScheduler.Default);
        }
    }
}
