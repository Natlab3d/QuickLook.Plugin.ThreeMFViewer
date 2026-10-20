// Bambu-aware 3MF scene loader: joins standard 3MF geometry with
// Bambu Studio's private color metadata (extruder -> filament colour).
// Large meshes are stream-parsed and spatially simplified for preview when needed.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Xml;
using System.Xml.Linq;

namespace QuickLook.Plugin.ThreeMFViewer
{
    internal class ScenePart
    {
        public MeshGeometry3D Mesh;
        public Matrix3D Transform;        // full placement (build item * component)
        public int Filament = 1;          // 1-based index into filament colours
        public int Plate;                 // Bambu plate number, 0 if unknown
    }

    internal sealed class MeshSet
    {
        private readonly Dictionary<int, Dictionary<Point3D, int>> _vertexLookup =
            new Dictionary<int, Dictionary<Point3D, int>>();

        public readonly Dictionary<int, MeshGeometry3D> ByFilament =
            new Dictionary<int, MeshGeometry3D>();
        public bool Prepared;

        public void AddTriangle(int filament, Point3D a, Point3D b, Point3D c)
        {
            if (!ByFilament.TryGetValue(filament, out var mesh))
            {
                ByFilament[filament] = mesh = new MeshGeometry3D();
                _vertexLookup[filament] = new Dictionary<Point3D, int>();
            }

            var lookup = _vertexLookup[filament];
            mesh.TriangleIndices.Add(GetVertex(mesh, lookup, a));
            mesh.TriangleIndices.Add(GetVertex(mesh, lookup, b));
            mesh.TriangleIndices.Add(GetVertex(mesh, lookup, c));
        }

        private static int GetVertex(MeshGeometry3D mesh, Dictionary<Point3D, int> lookup, Point3D point)
        {
            if (lookup.TryGetValue(point, out var index))
                return index;
            index = mesh.Positions.Count;
            mesh.Positions.Add(point);
            lookup.Add(point, index);
            return index;
        }
    }

    internal class ThreeMFScene
    {
        private const int MaxTrisPerFile = 900_000;  // rendered triangles cap per mesh file

        public List<ScenePart> Parts = new List<ScenePart>();
        public int PartCount;
        public string[] FilamentColours = Array.Empty<string>();
        public byte[] EmbeddedThumbnail;
        public string Title, Designer;
        public bool Simplified;
        public readonly List<string> DebugLog = new List<string>();
        public Point3D Center = new Point3D();   // bounding centre after layout
        public double Radius = 1;                // bounding radius after layout

        public static ThreeMFScene Load(string path)
        {
            var scene = new ThreeMFScene();

            using (var zip = ZipFile.OpenRead(path))
            {
                var mainEntry = zip.GetEntry("3D/3dmodel.model")
                                ?? zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("3dmodel.model", StringComparison.OrdinalIgnoreCase));
                if (mainEntry == null)
                    throw new IOException("not a 3MF package (no 3D model)");

                ReadBambuMetadata(zip, scene);
                scene.EmbeddedThumbnail = ReadEmbeddedThumbnail(zip);

                var cache = new Dictionary<string, Dictionary<int, MeshSet>>();

                var mainDoc = LoadEntryXml(mainEntry);

                // friendly title / designer for the window title bar
                foreach (var md in mainDoc.Descendants().Where(e => e.Name.LocalName == "metadata"))
                {
                    var name = md.Attribute("name")?.Value;
                    if (name == "Title") scene.Title = Clean(md.Value);
                    else if (name == "Designer") scene.Designer = Clean(md.Value);
                }

                // collect build items: (object id, placement matrix)
                var items = new List<BuildItem>();
                foreach (var build in mainDoc.Descendants().Where(e => e.Name.LocalName == "build"))
                    foreach (var item in build.Elements().Where(el => el.Name.LocalName == "item"))
                        items.Add(new BuildItem
                        {
                            ObjId = int.Parse(item.Attribute("objectid").Value),
                            Mat = ParseMatrix(item.Attribute("transform")?.Value ?? "1 0 0 0 1 0 0 0 1 0 0 0"),
                        });

                foreach (var item in items)
                    AddObject(mainDoc, cache, zip, item.ObjId, item.Mat, scene);

                LayoutClusters(scene);

                var bounds = BoundsOf(scene.Parts);
                scene.Center = new Point3D((bounds.MinX + bounds.MaxX) / 2,
                                           (bounds.MinY + bounds.MaxY) / 2,
                                           (bounds.MinZ + bounds.MaxZ) / 2);
                scene.Radius = Math.Max(1e-3,
                    Math.Max(Math.Max(bounds.MaxX - bounds.MinX, bounds.MaxY - bounds.MinY),
                             bounds.MaxZ - bounds.MinZ) / 2);

                scene.DebugLog.Add($"build items: {items.Count}");
            }

            return scene;
        }

        private static byte[] ReadEmbeddedThumbnail(ZipArchive zip)
        {
            // Prefer Bambu Studio's medium-resolution preview when present. The
            // small thumbnail is only 240x180 in many projects and looks blurry
            // when enlarged to fill the QuickLook image view.
            var preferredPaths = new[]
            {
                "Auxiliaries/.thumbnails/thumbnail_middle.png",
                "Metadata/plate_1.png",
                "Auxiliaries/.thumbnails/thumbnail_3mf.png",
                "Metadata/thumbnail.png",
                "3D/thumbnail.png",
            };

            foreach (var path in preferredPaths)
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    string.Equals(e.FullName.TrimStart('/'), path, StringComparison.OrdinalIgnoreCase));
                if (entry == null || entry.Length == 0 || entry.Length > 20 * 1024 * 1024)
                    continue;

                using (var input = entry.Open())
                using (var output = new MemoryStream())
                {
                    input.CopyTo(output);
                    return output.ToArray();
                }
            }

            return null;
        }

        private static void ReadBambuMetadata(ZipArchive zip, ThreeMFScene scene)
        {
            var ps = zip.GetEntry("Metadata/project_settings.config");
            if (ps != null)
            {
                var text = ReadEntry(ps);
                var m = Regex.Match(text, @"""filament_colour""\s*:\s*\[(?<c>[^\]]*)\]");
                if (m.Success)
                    scene.FilamentColours = m.Groups["c"].Value.Split(',')
                        .Select(s => s.Trim().Trim('"'))
                        .Where(s => s.Length > 0)
                        .ToArray();
            }

            var ms = zip.GetEntry("Metadata/model_settings.config");
            if (ms != null)
            {
                var text = ReadEntry(ms);

                var rx = new Regex(@"<object id=""(?<id>\d+)""(?si).*?(?=<object id=""|</config|</plate)");
                foreach (Match om in rx.Matches(text))
                {
                    var me = Regex.Match(om.Value, @"key=""extruder"" value=""(?<n>\d+)""");
                    if (me.Success)
                        scene._extruderByObject[int.Parse(om.Groups["id"].Value)] =
                            int.Parse(me.Groups["n"].Value);
                }

                // Bambu stores overrides for individual components as <part id="...">
                // under their parent object. Component IDs repeat between objects, so
                // retain both IDs when looking up the override in AddObject.
                try
                {
                    var settings = XDocument.Parse(text);
                    foreach (var objectEl in settings.Descendants().Where(e => e.Name.LocalName == "object"))
                    {
                        if (!int.TryParse(objectEl.Attribute("id")?.Value, out var objectId))
                            continue;

                        foreach (var partEl in objectEl.Elements().Where(e => e.Name.LocalName == "part"))
                        {
                            if (!int.TryParse(partEl.Attribute("id")?.Value, out var partId))
                                continue;

                            var subtype = (string)partEl.Attribute("subtype");
                            if (IsNonModelSubtype(subtype))
                                scene._nonModelParts.Add(PartKey(objectId, partId));

                            var extruder = partEl.Elements()
                                .Where(e => e.Name.LocalName == "metadata" &&
                                            (string)e.Attribute("key") == "extruder")
                                .Select(e => (string)e.Attribute("value"))
                                .FirstOrDefault();
                            if (int.TryParse(extruder, out var partExtruder))
                                scene._extruderByPart[PartKey(objectId, partId)] = Math.Max(1, partExtruder);
                        }

                        var parts = objectEl.Elements().Where(e => e.Name.LocalName == "part").ToList();
                        if (parts.Count > 0 && parts.All(p => IsNonModelSubtype((string)p.Attribute("subtype"))))
                            scene._nonModelObjects.Add(objectId);
                    }
                }
                catch (Exception ex)
                {
                    scene.DebugLog.Add($"Could not read Bambu per-part extruders: {ex.Message}");
                }

                // plates: <plate> blocks listing member objects via model_instance/object_id
                var rxPlate = new Regex(@"<plate>(?si).*?</plate>");
                var plateIndex = 0;
                foreach (Match pm in rxPlate.Matches(text))
                {
                    plateIndex++;
                    var rxObj = new Regex(@"key=""object_id"" value=""(?<id>\d+)""");
                    foreach (Match om2 in rxObj.Matches(pm.Value))
                        scene._plateByObject[int.Parse(om2.Groups["id"].Value)] = plateIndex;
                }
            }
        }

        internal readonly Dictionary<int, int> _extruderByObject = new Dictionary<int, int>();
        internal readonly Dictionary<string, int> _extruderByPart = new Dictionary<string, int>();
        internal readonly HashSet<string> _nonModelParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<int> _nonModelObjects = new HashSet<int>();
        internal readonly Dictionary<int, int> _plateByObject = new Dictionary<int, int>();

        private static string PartKey(int objectId, int partId) => objectId + ":" + partId;

        private static bool IsNonModelSubtype(string subtype)
            => string.Equals(subtype, "negative_part", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(subtype, "modifier_part", StringComparison.OrdinalIgnoreCase);

        // Arrange the model: plates in a tidy row, everything resting on Z = 0.
        private static void LayoutClusters(ThreeMFScene scene)
        {
            if (scene.Parts.Count == 0) return;

            var groups = scene.Parts.GroupBy(p => p.Plate).OrderBy(g => g.Key).ToList();

            // line up each plate cluster left-to-right with a small gap,
            // and stack them on a common centre line in Y
            var centers = groups.Select(g => BoundsOf(g)).Select(b => (b.MinY + b.MaxY) / 2).ToList();
            var targetY = centers.Average();

            var cursor = 0.0;
            foreach (var g in groups)
            {
                var b = BoundsOf(g);
                var dx = cursor - b.MinX;
                var dyC = targetY - (b.MinY + b.MaxY) / 2;
                var shift = new Matrix3D(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, dx, dyC, 0, 1);
                foreach (var part in g)
                    part.Transform = Transform(part.Transform, shift);
                cursor = b.MaxX + dx + 4;
            }

            // drop onto the ground plane and center vertically
            var all = BoundsOf(scene.Parts);
            var dz = -all.MinZ;
            var dy = -(all.MinY + all.MaxY) / 2;
            var ground = new Matrix3D(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, dy, dz, 1);
            foreach (var part in scene.Parts)
                part.Transform = Transform(part.Transform, ground);

            foreach (var g in groups)
            {
                var b = BoundsOf(g);
                scene.DebugLog.Add($"cluster plate={g.Key} parts={g.Count()} X[{b.MinX:0}..{b.MaxX:0}] Y[{b.MinY:0}..{b.MaxY:0}] Z[{b.MinZ:0}..{b.MaxZ:0}]");
            }
        }

        private static Matrix3D Transform(Matrix3D a, Matrix3D b) => a * b;

        private sealed class Bounds
        {
            public double MinX, MaxX, MinY, MaxY, MinZ, MaxZ;
        }

        private static Bounds BoundsOf(IEnumerable<ScenePart> parts)
        {
            var b = new Bounds { MinX = double.MaxValue, MinY = double.MaxValue, MinZ = double.MaxValue, MaxX = double.MinValue, MaxY = double.MinValue, MaxZ = double.MinValue };
            foreach (var part in parts)
            {
                var r = part.Mesh.Bounds;
                foreach (var corner in new[]
                {
                    new Point3D(r.X, r.Y, r.Z),
                    new Point3D(r.X + r.SizeX, r.Y, r.Z),
                    new Point3D(r.X, r.Y + r.SizeY, r.Z),
                    new Point3D(r.X, r.Y, r.Z + r.SizeZ),
                    new Point3D(r.X + r.SizeX, r.Y + r.SizeY, r.Z),
                    new Point3D(r.X + r.SizeX, r.Y, r.Z + r.SizeZ),
                    new Point3D(r.X, r.Y + r.SizeY, r.Z + r.SizeZ),
                    new Point3D(r.X + r.SizeX, r.Y + r.SizeY, r.Z + r.SizeZ),
                })
                {
                    var t = part.Transform.Transform(corner);
                    b.MinX = Math.Min(b.MinX, t.X); b.MaxX = Math.Max(b.MaxX, t.X);
                    b.MinY = Math.Min(b.MinY, t.Y); b.MaxY = Math.Max(b.MaxY, t.Y);
                    b.MinZ = Math.Min(b.MinZ, t.Z); b.MaxZ = Math.Max(b.MaxZ, t.Z);
                }
            }
            return b;
        }

        private static void AddObject(XDocument mainDoc,
            Dictionary<string, Dictionary<int, MeshSet>> cache,
            ZipArchive zip, int objId, Matrix3D mat, ThreeMFScene scene)
        {
            if (scene._nonModelObjects.Contains(objId))
                return;

            var objEl = mainDoc.Descendants().Where(e => e.Name.LocalName == "object")
                .FirstOrDefault(o => int.TryParse(o.Attribute("id")?.Value, out var i) && i == objId);
            if (objEl == null) return;

            var fil = scene._extruderByObject.TryGetValue(objId, out var n) ? Math.Max(1, n) : 1;
            var plate = scene._plateByObject.TryGetValue(objId, out var pl) ? pl : 0;

            var comps = objEl.Elements().FirstOrDefault(e => e.Name.LocalName == "components");
            if (comps != null)
            {
                foreach (var comp in comps.Elements().Where(e => e.Name.LocalName == "component"))
                {
                    var localId = int.Parse(comp.Attribute("objectid").Value);
                    // Negative volumes and modifiers affect slicing but are not
                    // visible printable geometry, so omit them from the preview.
                    if (scene._nonModelParts.Contains(PartKey(objId, localId)))
                        continue;
                    var path = comp.Attributes().FirstOrDefault(a => a.Name.LocalName == "path")?.Value;
                    // WPF Matrix3D transforms row vectors: apply the component's local
                    // placement first, then the parent build-item placement.
                    var compMat = ParseMatrix(comp.Attribute("transform")?.Value ?? "1 0 0 0 1 0 0 0 1 0 0 0") * mat;
                    var partFilament = scene._extruderByPart.TryGetValue(PartKey(objId, localId), out var partExtruder)
                        ? partExtruder
                        : fil;

                    if (path == null)
                    {
                        // same-file reference (valid 3MF, rare in Bambu exports)
                        var subObj = mainDoc.Descendants().Where(e => e.Name.LocalName == "object")
                            .FirstOrDefault(o => int.TryParse(o.Attribute("id")?.Value, out var i) && i == localId);
                        var inline = subObj == null ? null : ReadMeshInline(subObj, partFilament, scene);
                        AddMeshSet(scene, inline, compMat, partFilament, plate);
                        continue;
                    }

                    if (!cache.TryGetValue(path, out var fileMeshes))
                        cache[path] = fileMeshes = LoadMeshFile(zip, path, scene);

                    scene.DebugLog.Add($"comp path={path} localId={localId} fileMeshes=[{(string.Join(",", fileMeshes.Keys))}] found={fileMeshes.ContainsKey(localId)}");

                    if (fileMeshes.TryGetValue(localId, out var meshSet) && meshSet != null)
                        AddMeshSet(scene, meshSet, compMat, partFilament, plate);
                }
            }
            else
            {
                var inline = ReadMeshInline(objEl, fil, scene);
                AddMeshSet(scene, inline, mat, fil, plate);
            }
        }

        private static void AddMeshSet(ThreeMFScene scene, MeshSet meshSet, Matrix3D transform, int fallbackFilament, int plate)
        {
            if (meshSet == null)
                return;

            if (!meshSet.Prepared)
            {
                ReduceMeshSet(meshSet, MaxTrisPerFile, scene);
                foreach (var mesh in meshSet.ByFilament.Values)
                {
                    AddCreaseAwareNormals(mesh);
                    if (mesh.CanFreeze) mesh.Freeze();
                }
                meshSet.Prepared = true;
            }
            var added = false;
            foreach (var pair in meshSet.ByFilament)
            {
                var mesh = pair.Value;
                if (mesh.Positions.Count == 0 || mesh.TriangleIndices.Count == 0)
                    continue;
                scene.Parts.Add(new ScenePart
                {
                    Mesh = mesh,
                    Transform = transform,
                    Filament = pair.Key > 0 ? pair.Key : fallbackFilament,
                    Plate = plate,
                });
                added = true;
            }
            if (added) scene.PartCount++;
        }

        private struct GridKey : IEquatable<GridKey>
        {
            public long X, Y, Z;
            public bool Equals(GridKey other) => X == other.X && Y == other.Y && Z == other.Z;
            public override bool Equals(object obj) => obj is GridKey other && Equals(other);
            public override int GetHashCode() => unchecked((X.GetHashCode() * 397 ^ Y.GetHashCode()) * 397 ^ Z.GetHashCode());
        }

        // If a mesh exceeds the preview budget, merge nearby vertices and keep
        // every surviving local face. Sparse every-Nth-face sampling creates
        // disconnected shards and long spikes.
        private static void ReduceMeshSet(MeshSet meshSet, int triangleLimit, ThreeMFScene scene)
        {
            var totalTriangles = meshSet.ByFilament.Values.Sum(m => m.TriangleIndices.Count / 3);
            if (totalTriangles <= triangleLimit) return;

            var minX = double.MaxValue; var minY = double.MaxValue; var minZ = double.MaxValue;
            var maxX = double.MinValue; var maxY = double.MinValue; var maxZ = double.MinValue;
            foreach (var mesh in meshSet.ByFilament.Values)
                foreach (var p in mesh.Positions)
                {
                    minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                    minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                    minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
                }
            if (minX == double.MaxValue) return;
            var maxDimension = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
            if (maxDimension <= 1e-12) return;

            var cellSize = maxDimension / Math.Sqrt(triangleLimit / 2.0);
            for (var attempt = 0; attempt < 8 && totalTriangles > triangleLimit; attempt++, cellSize *= 1.5)
            {
                foreach (var filament in meshSet.ByFilament.Keys.ToList())
                    meshSet.ByFilament[filament] = ClusterVertices(meshSet.ByFilament[filament], cellSize, minX, minY, minZ);
                totalTriangles = meshSet.ByFilament.Values.Sum(m => m.TriangleIndices.Count / 3);
            }
            scene.Simplified = true;
        }

        private static MeshGeometry3D ClusterVertices(MeshGeometry3D source, double cellSize,
            double minX, double minY, double minZ)
        {
            var result = new MeshGeometry3D();
            var remap = new int[source.Positions.Count];
            var lookup = new Dictionary<GridKey, int>();
            for (var i = 0; i < source.Positions.Count; i++)
            {
                var p = source.Positions[i];
                var key = new GridKey
                {
                    X = (long)Math.Floor((p.X - minX) / cellSize),
                    Y = (long)Math.Floor((p.Y - minY) / cellSize),
                    Z = (long)Math.Floor((p.Z - minZ) / cellSize),
                };
                if (!lookup.TryGetValue(key, out remap[i]))
                {
                    remap[i] = result.Positions.Count;
                    lookup.Add(key, remap[i]);
                    // Use the cell center so adjacent filament meshes collapse a
                    // shared boundary vertex to the same location.
                    result.Positions.Add(new Point3D(
                        minX + (key.X + 0.5) * cellSize,
                        minY + (key.Y + 0.5) * cellSize,
                        minZ + (key.Z + 0.5) * cellSize));
                }
            }

            for (var i = 0; i + 2 < source.TriangleIndices.Count; i += 3)
            {
                var a = source.TriangleIndices[i]; var b = source.TriangleIndices[i + 1]; var c = source.TriangleIndices[i + 2];
                if (a < 0 || b < 0 || c < 0 || a >= remap.Length || b >= remap.Length || c >= remap.Length) continue;
                a = remap[a]; b = remap[b]; c = remap[c];
                if (a == b || b == c || c == a) continue;
                result.TriangleIndices.Add(a); result.TriangleIndices.Add(b); result.TriangleIndices.Add(c);
            }
            return result;
        }

        private static Dictionary<int, MeshSet> LoadMeshFile(ZipArchive zip, string path, ThreeMFScene scene)
        {
            var res = new Dictionary<int, MeshSet>();
            var entryName = path.TrimStart('/');
            var entry = zip.GetEntry(entryName)
                        ?? zip.Entries.FirstOrDefault(e => e.FullName.Equals(entryName, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return res;

            using (var stream = entry.Open())
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = true,
                IgnoreWhitespace = true,
            }))
            {
                int currentObjectId = -1;
                MeshSet meshSet = null;
                var positions = new List<Point3D>();

                while (reader.Read())
                {
                    // save a completed mesh when its </mesh> arrives
                    if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "mesh" && meshSet != null)
                    {
                        if (currentObjectId >= 0 && meshSet.ByFilament.Count > 0)
                            res[currentObjectId] = meshSet;
                        meshSet = null;
                        positions = new List<Point3D>();
                        continue;
                    }

                    if (reader.NodeType != XmlNodeType.Element) continue;

                    switch (reader.LocalName)
                    {
                        case "object":
                            currentObjectId = int.TryParse(reader.GetAttribute("id"), out var oid) ? oid : -1;
                            meshSet = null;
                            break;

                        case "mesh":
                            meshSet = new MeshSet();
                            break;

                        case "vertex" when meshSet != null:
                            positions.Add(new Point3D(
                                Double(reader.GetAttribute("x")),
                                Double(reader.GetAttribute("y")),
                                Double(reader.GetAttribute("z"))));
                            break;

                        case "triangle" when meshSet != null:
                            AddTriangle(meshSet, positions,
                                (int)Double(reader.GetAttribute("v1")),
                                (int)Double(reader.GetAttribute("v2")),
                                (int)Double(reader.GetAttribute("v3")),
                                GetPaintAttribute(reader), 0, scene.FilamentColours.Length);
                            break;
                    }
                }
            }

            return res;
        }

        private static MeshSet ReadMeshInline(XElement objEl, int fallbackFilament, ThreeMFScene scene)
        {
            var meshEl = objEl.Elements().FirstOrDefault(e => e.Name.LocalName == "mesh");
            if (meshEl == null) return null;

            var meshSet = new MeshSet();
            var verts = meshEl.Elements().FirstOrDefault(e => e.Name.LocalName == "vertices");
            var positions = new List<Point3D>();
            if (verts != null)
                foreach (var v in verts.Elements().Where(e => e.Name.LocalName == "vertex"))
                    positions.Add(new Point3D(Double(v.Attribute("x")?.Value), Double(v.Attribute("y")?.Value), Double(v.Attribute("z")?.Value)));

            var tris = meshEl.Elements().FirstOrDefault(e => e.Name.LocalName == "triangles");
            if (tris != null)
                foreach (var t in tris.Elements().Where(e => e.Name.LocalName == "triangle"))
                    AddTriangle(meshSet, positions,
                        (int)Double(AttributeValue(t, "v1")),
                        (int)Double(AttributeValue(t, "v2")),
                        (int)Double(AttributeValue(t, "v3")),
                        PaintAttribute(t), fallbackFilament, scene.FilamentColours.Length);
            return meshSet;
        }

        private sealed class PaintNode
        {
            public int SplitSides;
            public int SpecialSide;
            public int Filament;
            public PaintNode[] Children;
        }

        private static string GetPaintAttribute(XmlReader reader)
        {
            if (!reader.HasAttributes) return null;
            string value = null;
            while (reader.MoveToNextAttribute())
            {
                if (reader.LocalName == "paint_color" || reader.LocalName == "mmu_segmentation")
                {
                    value = reader.Value;
                    break;
                }
            }
            reader.MoveToElement();
            return value;
        }

        private static string PaintAttribute(XElement element)
            => element.Attributes().FirstOrDefault(a => a.Name.LocalName == "paint_color" ||
                                                        a.Name.LocalName == "mmu_segmentation")?.Value;

        private static string AttributeValue(XElement element, string name)
            => element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

        private static void AddTriangle(MeshSet meshSet, IList<Point3D> positions,
            int a, int b, int c, string paintData, int fallbackFilament, int paletteCount)
        {
            if (a < 0 || b < 0 || c < 0 || a >= positions.Count || b >= positions.Count || c >= positions.Count)
                return;

            var triangle = new[] { positions[a], positions[b], positions[c] };
            var tree = DecodePaintTree(paintData);
            if (tree == null)
            {
                meshSet.AddTriangle(fallbackFilament, triangle[0], triangle[1], triangle[2]);
                return;
            }
            EmitPaintedTriangle(meshSet, tree, triangle, fallbackFilament, paletteCount, 0);
        }

        // Bambu/Prusa encode facet painting as a reversed hex string containing
        // little-endian 4-bit TriangleSelector records, one subdivision tree per face.
        private static PaintNode DecodePaintTree(string data)
        {
            if (string.IsNullOrEmpty(data)) return null;
            var nibbles = new List<int>(data.Length);
            for (var i = data.Length - 1; i >= 0; i--)
            {
                var digit = data[i];
                int value;
                if (digit >= '0' && digit <= '9') value = digit - '0';
                else if (digit >= 'A' && digit <= 'F') value = digit - 'A' + 10;
                else if (digit >= 'a' && digit <= 'f') value = digit - 'a' + 10;
                else return null;
                nibbles.Add(value);
            }

            var cursor = 0;
            var nodeCount = 0;
            if (!TryReadPaintNode(nibbles, ref cursor, 0, ref nodeCount, out var root) || cursor != nibbles.Count)
                return null;
            return root;
        }

        private static bool TryReadPaintNode(IList<int> nibbles, ref int cursor, int depth,
            ref int nodeCount, out PaintNode node)
        {
            node = null;
            if (depth > 64 || ++nodeCount > 20000 || cursor >= nibbles.Count)
                return false;

            var code = nibbles[cursor++];
            var splitSides = code & 0x3;
            node = new PaintNode { SplitSides = splitSides, SpecialSide = code >> 2 };
            if (splitSides == 0)
            {
                var state = 0;
                if ((code & 0xC) == 0xC)
                {
                    if (cursor >= nibbles.Count) return false;
                    var next = nibbles[cursor++];
                    var extra = 0;
                    while (next == 0xF)
                    {
                        if (++extra > 32 || cursor >= nibbles.Count) return false;
                        next = nibbles[cursor++];
                    }
                    state = next + 15 * extra + 3;
                }
                else
                {
                    state = code >> 2;
                }
                node.Filament = state;
                return true;
            }

            if ((splitSides == 3 && node.SpecialSide != 0) ||
                (splitSides < 3 && node.SpecialSide > 2))
                return false;

            var childCount = splitSides + 1;
            node.Children = new PaintNode[childCount];
            // TriangleSelector writes child subtrees in reverse order.
            for (var child = childCount - 1; child >= 0; child--)
                if (!TryReadPaintNode(nibbles, ref cursor, depth + 1, ref nodeCount, out node.Children[child]))
                    return false;
            return true;
        }

        private static void EmitPaintedTriangle(MeshSet meshSet, PaintNode node, Point3D[] triangle,
            int fallbackFilament, int paletteCount, int depth)
        {
            if (node == null || node.Children == null || depth > 64)
            {
                var filament = node?.Filament ?? 0;
                if (filament <= 0 || (paletteCount > 0 && filament > paletteCount)) filament = fallbackFilament;
                meshSet.AddTriangle(filament, triangle[0], triangle[1], triangle[2]);
                return;
            }

            var a = triangle[node.SpecialSide % 3];
            var b = triangle[(node.SpecialSide + 1) % 3];
            var c = triangle[(node.SpecialSide + 2) % 3];
            var ab = Midpoint(a, b);
            var bc = Midpoint(b, c);
            var ca = Midpoint(c, a);
            Point3D[][] children;
            switch (node.SplitSides)
            {
                case 1:
                    children = new[] { new[] { a, b, bc }, new[] { bc, c, a } };
                    break;
                case 2:
                    children = new[] { new[] { a, ab, ca }, new[] { ab, b, ca }, new[] { b, c, ca } };
                    break;
                default:
                    children = new[] { new[] { a, ab, ca }, new[] { ab, b, bc }, new[] { bc, c, ca }, new[] { ab, bc, ca } };
                    break;
            }
            for (var i = 0; i < children.Length && i < node.Children.Length; i++)
                EmitPaintedTriangle(meshSet, node.Children[i], children[i], fallbackFilament, paletteCount, depth + 1);
        }

        private static Point3D Midpoint(Point3D a, Point3D b)
            => new Point3D((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);

        private sealed class NormalCluster
        {
            public Vector3D Seed;
            public Vector3D Sum;
            public int NewIndex;
        }

        // 3MF stores indexed triangles but not smooth vertex normals. Build a
        // shaded mesh by averaging neighboring face normals, splitting vertices
        // at sharp creases so embossed edges and medal rims stay defined.
        private static void AddCreaseAwareNormals(MeshGeometry3D mesh)
        {
            if (mesh == null || mesh.Positions.Count == 0 || mesh.TriangleIndices.Count < 3)
                return;

            const double creaseCosine = 0.5; // preserve edges sharper than 60 degrees
            var clustersByVertex = new List<NormalCluster>[mesh.Positions.Count];
            var smoothed = new MeshGeometry3D();
            var sourceIndices = mesh.TriangleIndices;

            for (var i = 0; i + 2 < sourceIndices.Count; i += 3)
            {
                var ids = new[] { sourceIndices[i], sourceIndices[i + 1], sourceIndices[i + 2] };
                if (ids.Any(id => id < 0 || id >= mesh.Positions.Count))
                    continue;

                var a = mesh.Positions[ids[0]];
                var b = mesh.Positions[ids[1]];
                var c = mesh.Positions[ids[2]];
                var faceNormal = Vector3D.CrossProduct(b - a, c - a);
                if (faceNormal.LengthSquared < 1e-20)
                    faceNormal = new Vector3D(0, 0, 1);
                else
                    faceNormal.Normalize();

                for (var corner = 0; corner < 3; corner++)
                {
                    var vertexId = ids[corner];
                    var clusters = clustersByVertex[vertexId] ?? (clustersByVertex[vertexId] = new List<NormalCluster>());
                    var cluster = clusters.FirstOrDefault(candidate => Vector3D.DotProduct(candidate.Seed, faceNormal) >= creaseCosine);
                    if (cluster == null)
                    {
                        cluster = new NormalCluster
                        {
                            Seed = faceNormal,
                            NewIndex = smoothed.Positions.Count,
                        };
                        clusters.Add(cluster);
                        smoothed.Positions.Add(mesh.Positions[vertexId]);
                        smoothed.Normals.Add(new Vector3D());
                    }

                    cluster.Sum += faceNormal;
                    smoothed.TriangleIndices.Add(cluster.NewIndex);
                }
            }

            for (var vertexId = 0; vertexId < clustersByVertex.Length; vertexId++)
            {
                var clusters = clustersByVertex[vertexId];
                if (clusters == null) continue;
                foreach (var cluster in clusters)
                {
                    var normal = cluster.Sum;
                    if (normal.LengthSquared < 1e-20)
                        normal = cluster.Seed;
                    else
                        normal.Normalize();
                    smoothed.Normals[cluster.NewIndex] = normal;
                }
            }

            mesh.Positions = smoothed.Positions;
            mesh.Normals = smoothed.Normals;
            mesh.TriangleIndices = smoothed.TriangleIndices;
        }

        private static double Double(string s)
            => s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

        // 3MF matrix = "m00 m01 m02 m10 m11 m12 m20 m21 m22 tx ty tz" (row-major 3x4)
        private static Matrix3D ParseMatrix(string s)
        {
            try
            {
                var v = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray();
                if (v.Length < 12) return Matrix3D.Identity;
                return new Matrix3D(v[0], v[3], v[6], 0, v[1], v[4], v[7], 0, v[2], v[5], v[8], 0, v[9], v[10], v[11], 1);
            }
            catch { return Matrix3D.Identity; }
        }

        private static XDocument LoadEntryXml(ZipArchiveEntry entry)
        {
            using (var s = entry.Open())
            {
                try { return XDocument.Load(s, LoadOptions.None); }
                catch { return new XDocument(new XElement("model")); }
            }
        }

        private static string ReadEntry(ZipArchiveEntry entry)
        {
            using (var r = new StreamReader(entry.Open()))
                return r.ReadToEnd();
        }

        private static string Clean(string s)
            => s == null ? null : Regex.Replace(s, "&[a-z]+;", " ").Trim();

        public string FilamentBrushHex(int n)
        {
            if (FilamentColours.Length == 0) return null;
            return n >= 1 && n <= FilamentColours.Length ? FilamentColours[n - 1] : null;
        }

        public Brush FilamentBrush(int n)
        {
            string hex = FilamentBrushHex(n);
            try { return hex != null && hex.Length > 0 ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) : Brushes.Silver; }
            catch { return Brushes.Silver; }
        }

        private sealed class BuildItem
        {
            public int ObjId;
            public Matrix3D Mat;
        }
    }
}
