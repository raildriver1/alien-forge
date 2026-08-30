using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using AlienForge.Core;
using AlienForge.Core.Imaging;
using AlienForge.Core.Mesh;
using CATHODE;

namespace AlienForge.App;

/// <summary>
/// Turns decoded submeshes into a WPF 3D model, textured where possible.
/// </summary>
public static class MeshPreview
{
    public sealed class Built
    {
        public required Model3DGroup Model { get; init; }
        public required Rect3D Bounds { get; init; }
        public int Parts { get; init; }
        public int Vertices { get; init; }
        public int Triangles { get; init; }
        public int Textured { get; init; }
        public List<string> Notes { get; } = new();

        /// <summary>
        /// Decoded submeshes paired with the objects drawing them, so animation
        /// playback can push new vertex positions into the same objects instead of
        /// rebuilding the scene every frame, and so single parts can be shown or
        /// hidden without decoding anything again.
        /// </summary>
        public List<PreviewPart> Parts3D { get; } = new();

        /// <summary>The group the parts are added to, for toggling what is drawn.</summary>
        public required Model3DGroup Geometry { get; init; }
    }

    /// <summary>One drawable submesh, with everything needed to inspect or hide it.</summary>
    public sealed class PreviewPart
    {
        public required DecodedMesh Mesh { get; init; }
        public required MeshGeometry3D Geometry { get; init; }
        public required GeometryModel3D Drawing { get; init; }

        /// <summary>The CATHODE material, for showing how the surface is put together.</summary>
        public Materials.Material? Material { get; init; }

        /// <summary>Whether a texture was found and applied.</summary>
        public bool Textured { get; init; }

        public bool Visible { get; set; } = true;

        /// <summary>Short label for a list: part name, vertices and triangles.</summary>
        public string Display
            => $"{Mesh.Name}  ·  {Mesh.VertexCount} верт., {Mesh.TriangleCount} трис." +
               $"{(Mesh.IsSkinned ? ", со скином" : "")}";

        public override string ToString() => Display;
    }

    /// <summary>
    /// Builds a preview for one model. Only LOD 0 by default, and collision hulls
    /// are left out: they are separate LODs named COL_* and would sit as a grey
    /// shell over the real mesh.
    /// </summary>
    public static Built Build(AssetWorkspace ws, Models.CS2 model, int lodIndex = 0,
        bool includeCollision = false)
    {
        var group = new Model3DGroup();
        var textureCache = new Dictionary<Textures.TEX4, ImageSource?>();
        int parts = 0, vertices = 0, triangles = 0, textured = 0;
        var notes = new List<string>();
        var built3D = new List<PreviewPart>();

        for (int ci = 0; ci < model.Components.Count; ci++)
        {
            var component = model.Components[ci];
            for (int li = 0; li < component.LODs.Count; li++)
            {
                if (li != lodIndex)
                    continue;
                var lod = component.LODs[li];
                if (!includeCollision && IsCollision(lod.Name))
                    continue;

                for (int si = 0; si < lod.Submeshes.Count; si++)
                {
                    var decoded = MeshDecoder.Decode(model, ci, li, si);
                    if (decoded is null || decoded.VertexCount == 0 || decoded.Indices.Length < 3)
                        continue;

                    var geometry = BuildGeometry(decoded);
                    var submesh = lod.Submeshes[si];
                    ImageSource? diffuse = ResolveDiffuse(submesh.Material, textureCache, notes);

                    Material material = diffuse is not null
                        ? new DiffuseMaterial(new ImageBrush(diffuse)
                        {
                            ViewportUnits = BrushMappingMode.Absolute,
                            TileMode = TileMode.Tile,
                        })
                        : new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x9A, 0x9C, 0xA0)));

                    if (diffuse is not null)
                        textured++;

                    var drawing = new GeometryModel3D
                    {
                        Geometry = geometry,
                        Material = material,
                        BackMaterial = material,
                    };
                    group.Children.Add(drawing);
                    built3D.Add(new PreviewPart
                    {
                        Mesh = decoded,
                        Geometry = geometry,
                        Drawing = drawing,
                        Material = submesh.Material,
                        Textured = diffuse is not null,
                    });

                    parts++;
                    vertices += decoded.VertexCount;
                    triangles += decoded.TriangleCount;
                }
            }
        }

        // The game's geometry is Z-up; WPF's camera work below assumes Y-up.
        group.Transform = new RotateTransform3D(
            new AxisAngleRotation3D(new Vector3D(1, 0, 0), -90));

        var built = new Built
        {
            Model = group,
            Geometry = group,
            Bounds = group.Children.Count > 0 ? group.Bounds : new Rect3D(0, 0, 0, 1, 1, 1),
            Parts = parts,
            Vertices = vertices,
            Triangles = triangles,
            Textured = textured,
        };
        built.Notes.AddRange(notes);
        built.Parts3D.AddRange(built3D);
        return built;
    }

    private static MeshGeometry3D BuildGeometry(DecodedMesh mesh)
    {
        var positions = new Point3DCollection(mesh.VertexCount);
        foreach (var p in mesh.Positions)
            positions.Add(new Point3D(p.X, p.Y, p.Z));

        var geometry = new MeshGeometry3D { Positions = positions };

        if (mesh.Normals is { Length: > 0 } normals && normals.Length == mesh.VertexCount)
        {
            var vectors = new Vector3DCollection(normals.Length);
            foreach (var n in normals)
                vectors.Add(new Vector3D(n.X, n.Y, n.Z));
            geometry.Normals = vectors;
        }

        if (mesh.Uv0 is { Length: > 0 } uv && uv.Length == mesh.VertexCount)
        {
            var coords = new PointCollection(uv.Length);
            foreach (var t in uv)
                coords.Add(new System.Windows.Point(t.X, t.Y));
            geometry.TextureCoordinates = coords;
        }

        var indices = new Int32Collection(mesh.Indices.Length);
        foreach (int i in mesh.Indices)
            indices.Add(i);
        geometry.TriangleIndices = indices;

        return geometry;
    }

    private static ImageSource? ResolveDiffuse(Materials.Material? material,
        Dictionary<Textures.TEX4, ImageSource?> cache, List<string> notes)
    {
        if (material is null)
            return null;

        Textures.TEX4? pick = null;
        foreach (var ptr in material.TextureReferences)
        {
            var tex = ptr?.Texture;
            if (tex is null)
                continue;
            string name = tex.Name ?? string.Empty;
            if (name.Contains("[d]", StringComparison.OrdinalIgnoreCase))
            {
                pick = tex;
                break;
            }
        }
        if (pick is null)
            return null;
        if (cache.TryGetValue(pick, out var cached))
            return cached;

        ImageSource? image = null;
        var result = TextureDecoder.Decode(pick);
        if (result.Ok)
            image = ToBitmap(result.Image!);
        else
            notes.Add($"текстура '{pick.Name}': {result.Error}");

        cache[pick] = image;
        return image;
    }

    /// <summary>Wraps decoded RGBA pixels as a WPF bitmap.</summary>
    public static BitmapSource ToBitmap(RgbaImage image)
    {
        var bitmap = BitmapSource.Create(
            image.Width, image.Height, 96, 96,
            PixelFormats.Bgra32, null,
            SwapToBgra(image), image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] SwapToBgra(RgbaImage image)
    {
        var output = new byte[image.Pixels.Length];
        for (int i = 0; i < output.Length; i += 4)
        {
            output[i + 0] = image.Pixels[i + 2];
            output[i + 1] = image.Pixels[i + 1];
            output[i + 2] = image.Pixels[i + 0];
            output[i + 3] = image.Pixels[i + 3];
        }
        return output;
    }

    private static bool IsCollision(string? lodName)
        => lodName is not null
           && (lodName.StartsWith("COL_", StringComparison.OrdinalIgnoreCase)
               || lodName.Contains("_COL", StringComparison.OrdinalIgnoreCase));
}
