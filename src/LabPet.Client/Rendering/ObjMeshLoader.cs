using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using WpfMeshGeometry3D = System.Windows.Media.Media3D.MeshGeometry3D;

namespace LabPet.Client.Rendering;

/// <summary>
/// 동결(Frozen)된 OBJ 서브메시 한 개(논리 파츠 이름 + 재질 슬롯 이름 + WPF MeshGeometry3D).
/// </summary>
public sealed class ObjSubMesh
{
    public required string PartName { get; init; }
    public required string MaterialSlot { get; init; }
    public required WpfMeshGeometry3D Geometry { get; init; }
    public int TriangleCount => Geometry.TriangleIndices.Count / 3;
}

/// <summary>
/// 파싱·동결이 완료되어 여러 펫 인스턴스 간 안전하게 공유되는 OBJ 에셋.
/// </summary>
public sealed class ObjMeshAsset
{
    public required string SourcePath { get; init; }
    public required IReadOnlyList<ObjSubMesh> SubMeshes { get; init; }
    public required int TotalTriangleCount { get; init; }
}

/// <summary>
/// Blender에서 내보낸 정적 Wavefront OBJ(.obj)를 파싱해 서브메시 단위로 리매핑·법선 보완·Freeze() 후
/// WeakReference 캐시로 지연 로드 및 메모리 해제를 지원하는 경량 로더.
/// </summary>
public sealed class ObjMeshLoader
{
    private const int MaxCachedAssets = 48;
    private readonly Dictionary<string, WeakReference<ObjMeshAsset>> _assetCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConditionalWeakTable<WpfMeshGeometry3D, ObjMeshAsset> _geometryAssetHolders = new();
    private readonly object _cacheLock = new();

    public bool TryLoad(string filePath, out ObjMeshAsset? asset, double unitScale = 1.0, Point3D originOffset = default)
    {
        asset = null;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch
        {
            return false;
        }

        var cacheKey = $"{fullPath}|{unitScale:F4}|{originOffset.X:F4},{originOffset.Y:F4},{originOffset.Z:F4}";
        lock (_cacheLock)
        {
            if (_assetCache.TryGetValue(cacheKey, out var weak) && weak.TryGetTarget(out var cached))
            {
                asset = cached;
                return true;
            }
        }

        ObjMeshAsset? parsed;
        try
        {
            parsed = ParseObjFile(fullPath, unitScale, originOffset);
        }
        catch
        {
            return false;
        }

        if (parsed is null || parsed.SubMeshes.Count == 0 || parsed.TotalTriangleCount == 0)
        {
            return false;
        }

        lock (_cacheLock)
        {
            if (_assetCache.TryGetValue(cacheKey, out var weak) && weak.TryGetTarget(out var existing))
            {
                asset = existing;
                return true;
            }

            if (_assetCache.Count >= MaxCachedAssets)
            {
                var deadKeys = _assetCache
                    .Where(kv => !kv.Value.TryGetTarget(out _))
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var key in deadKeys)
                {
                    _assetCache.Remove(key);
                }

                if (_assetCache.Count >= MaxCachedAssets)
                {
                    _assetCache.Remove(_assetCache.Keys.First());
                }
            }

            foreach (var subMesh in parsed.SubMeshes)
            {
                _geometryAssetHolders.AddOrUpdate(subMesh.Geometry, parsed);
            }

            _assetCache[cacheKey] = new WeakReference<ObjMeshAsset>(parsed);
            asset = parsed;
            return true;
        }
    }

    private sealed class SubMeshBuilder
    {
        public required string PartName { get; init; }
        public required string MaterialSlot { get; init; }
        public List<Point3D> Positions { get; } = [];
        public List<Vector3D> Normals { get; } = [];
        public List<int> Indices { get; } = [];
        public Dictionary<(int PosIdx, int NormIdx), int> VertexMap { get; } = [];
        public bool HasAllNormals { get; set; } = true;
    }

    private sealed class PartMaterialComparer : IEqualityComparer<(string Part, string Mat)>
    {
        public static readonly PartMaterialComparer Instance = new();
        public bool Equals((string Part, string Mat) x, (string Part, string Mat) y)
            => string.Equals(x.Part, y.Part, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(x.Mat, y.Mat, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Part, string Mat) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Part),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Mat));
    }

    private static ObjMeshAsset? ParseObjFile(string fullPath, double unitScale, Point3D originOffset)
    {
        var globalPositions = new List<Point3D>(2048);
        var globalNormals = new List<Vector3D>(2048);
        var subMeshOrder = new List<SubMeshBuilder>();
        var subMeshLookup = new Dictionary<(string Part, string Mat), SubMeshBuilder>(PartMaterialComparer.Instance);

        var currentPart = "Body";
        var currentMaterial = "body";
        var hasExplicitTransform = Math.Abs(unitScale - 1.0) > 1e-6 ||
                                   Math.Abs(originOffset.X) > 1e-6 ||
                                   Math.Abs(originOffset.Y) > 1e-6 ||
                                   Math.Abs(originOffset.Z) > 1e-6;

        SubMeshBuilder GetOrCreateBuilder()
        {
            var key = (currentPart, currentMaterial);
            if (!subMeshLookup.TryGetValue(key, out var builder))
            {
                builder = new SubMeshBuilder
                {
                    PartName = currentPart,
                    MaterialSlot = currentMaterial
                };
                subMeshLookup[key] = builder;
                subMeshOrder.Add(builder);
            }

            return builder;
        }

        Span<int> triPos = stackalloc int[32];
        Span<int> triNorm = stackalloc int[32];

        foreach (var rawLine in File.ReadLines(fullPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line.StartsWith("v ", StringComparison.Ordinal))
            {
                var parts = SplitWhitespace(line.AsSpan(2), out var count, 3);
                if (count < 3 ||
                    !TryParseFloat(parts[0], out var x) ||
                    !TryParseFloat(parts[1], out var y) ||
                    !TryParseFloat(parts[2], out var z))
                {
                    return null;
                }

                if (hasExplicitTransform)
                {
                    globalPositions.Add(new Point3D(
                        x * unitScale + originOffset.X,
                        y * unitScale + originOffset.Y,
                        z * unitScale + originOffset.Z));
                }
                else
                {
                    globalPositions.Add(new Point3D(x, y, z));
                }
            }
            else if (line.StartsWith("vn ", StringComparison.Ordinal))
            {
                var parts = SplitWhitespace(line.AsSpan(3), out var count, 3);
                if (count < 3 ||
                    !TryParseFloat(parts[0], out var nx) ||
                    !TryParseFloat(parts[1], out var ny) ||
                    !TryParseFloat(parts[2], out var nz))
                {
                    return null;
                }

                var v = new Vector3D(nx, ny, nz);
                if (v.LengthSquared > 1e-12)
                {
                    v.Normalize();
                }

                globalNormals.Add(v);
            }
            else if (line.StartsWith("o ", StringComparison.Ordinal) || line.StartsWith("g ", StringComparison.Ordinal))
            {
                var name = line[2..].Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    currentPart = NormalizeBlenderObjectName(name);
                }
            }
            else if (line.StartsWith("usemtl ", StringComparison.Ordinal))
            {
                var mat = line[7..].Trim();
                if (!string.IsNullOrWhiteSpace(mat))
                {
                    currentMaterial = NormalizeMaterialSlot(mat);
                }
            }
            else if (line.StartsWith("f ", StringComparison.Ordinal))
            {
                var tokens = line[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 3)
                {
                    return null;
                }

                var posIdxList = tokens.Length <= 32 ? triPos[..tokens.Length] : new int[tokens.Length];
                var normIdxList = tokens.Length <= 32 ? triNorm[..tokens.Length] : new int[tokens.Length];

                for (var i = 0; i < tokens.Length; i++)
                {
                    if (!TryParseFaceVertex(tokens[i], globalPositions.Count, globalNormals.Count, out var pIdx, out var nIdx))
                    {
                        return null;
                    }

                    posIdxList[i] = pIdx;
                    normIdxList[i] = nIdx;
                }

                var builder = GetOrCreateBuilder();
                var v0 = ResolveSubMeshVertex(builder, globalPositions, globalNormals, posIdxList[0], normIdxList[0]);
                for (var k = 1; k < tokens.Length - 1; k++)
                {
                    var v1 = ResolveSubMeshVertex(builder, globalPositions, globalNormals, posIdxList[k], normIdxList[k]);
                    var v2 = ResolveSubMeshVertex(builder, globalPositions, globalNormals, posIdxList[k + 1], normIdxList[k + 1]);
                    builder.Indices.Add(v0);
                    builder.Indices.Add(v1);
                    builder.Indices.Add(v2);
                }
            }
        }

        if (globalPositions.Count == 0 || subMeshOrder.Count == 0)
        {
            return null;
        }

        var result = new List<ObjSubMesh>(subMeshOrder.Count);
        var totalTris = 0;
        foreach (var b in subMeshOrder)
        {
            if (b.Indices.Count < 3)
            {
                continue;
            }

            if (!b.HasAllNormals || b.Normals.Count != b.Positions.Count)
            {
                ComputeSmoothVertexNormals(b.Positions, b.Indices, b.Normals);
            }

            var mesh = new WpfMeshGeometry3D
            {
                Positions = new Point3DCollection(b.Positions),
                Normals = new Vector3DCollection(b.Normals),
                TriangleIndices = new Int32Collection(b.Indices)
            };
            mesh.Freeze();

            totalTris += b.Indices.Count / 3;
            result.Add(new ObjSubMesh
            {
                PartName = b.PartName,
                MaterialSlot = b.MaterialSlot,
                Geometry = mesh
            });
        }

        return result.Count > 0
            ? new ObjMeshAsset
            {
                SourcePath = fullPath,
                SubMeshes = result,
                TotalTriangleCount = totalTris
            }
            : null;
    }

    private static string NormalizeBlenderObjectName(string name)
    {
        // Blender는 "Body_Mesh" 또는 "LeftEar.001" 형태를 붙일 수 있으므로 논리 접두사 보존 및 점(.) 접미사 정리
        var dotIdx = name.IndexOf('.');
        return dotIdx > 0 ? name[..dotIdx] : name;
    }

    private static string NormalizeMaterialSlot(string mat)
    {
        var dotIdx = mat.IndexOf('.');
        return dotIdx > 0 ? mat[..dotIdx] : mat;
    }

    private static int ResolveSubMeshVertex(
        SubMeshBuilder builder,
        List<Point3D> globalPositions,
        List<Vector3D> globalNormals,
        int posIdx,
        int normIdx)
    {
        var key = (posIdx, normIdx);
        if (builder.VertexMap.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var nextIdx = builder.Positions.Count;
        builder.Positions.Add(globalPositions[posIdx]);
        if (normIdx >= 0 && normIdx < globalNormals.Count)
        {
            builder.Normals.Add(globalNormals[normIdx]);
        }
        else
        {
            builder.HasAllNormals = false;
            builder.Normals.Add(new Vector3D(0, 1, 0));
        }

        builder.VertexMap[key] = nextIdx;
        return nextIdx;
    }

    private static void ComputeSmoothVertexNormals(List<Point3D> positions, List<int> indices, List<Vector3D> normals)
    {
        normals.Clear();
        for (var i = 0; i < positions.Count; i++)
        {
            normals.Add(default);
        }

        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            var i0 = indices[i];
            var i1 = indices[i + 1];
            var i2 = indices[i + 2];
            var p0 = positions[i0];
            var p1 = positions[i1];
            var p2 = positions[i2];
            var faceNormal = Vector3D.CrossProduct(p1 - p0, p2 - p0);
            normals[i0] += faceNormal;
            normals[i1] += faceNormal;
            normals[i2] += faceNormal;
        }

        for (var i = 0; i < normals.Count; i++)
        {
            var n = normals[i];
            if (n.LengthSquared > 1e-12)
            {
                n.Normalize();
                normals[i] = n;
            }
            else
            {
                normals[i] = new Vector3D(0, 1, 0);
            }
        }
    }

    private static bool TryParseFaceVertex(string token, int posCount, int normCount, out int posIdx, out int normIdx)
    {
        posIdx = -1;
        normIdx = -1;

        var firstSlash = token.IndexOf('/');
        var posSpan = firstSlash >= 0 ? token.AsSpan(0, firstSlash) : token.AsSpan();
        if (!int.TryParse(posSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawPos))
        {
            return false;
        }

        posIdx = rawPos > 0 ? rawPos - 1 : posCount + rawPos;
        if (posIdx < 0 || posIdx >= posCount)
        {
            return false;
        }

        if (firstSlash >= 0)
        {
            var secondSlash = token.IndexOf('/', firstSlash + 1);
            if (secondSlash >= 0 && secondSlash + 1 < token.Length)
            {
                if (int.TryParse(token.AsSpan(secondSlash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawNorm))
                {
                    var resolvedNorm = rawNorm > 0 ? rawNorm - 1 : normCount + rawNorm;
                    if (resolvedNorm >= 0 && resolvedNorm < normCount)
                    {
                        normIdx = resolvedNorm;
                    }
                }
            }
        }

        return true;
    }

    private static double[] SplitWhitespace(ReadOnlySpan<char> span, out int count, int maxCount)
    {
        var result = new double[maxCount];
        count = 0;
        var i = 0;
        while (i < span.Length && count < maxCount)
        {
            while (i < span.Length && char.IsWhiteSpace(span[i]))
            {
                i++;
            }

            if (i >= span.Length)
            {
                break;
            }

            var start = i;
            while (i < span.Length && !char.IsWhiteSpace(span[i]))
            {
                i++;
            }

            if (double.TryParse(span[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
            {
                result[count++] = val;
            }
            else
            {
                count = 0;
                return result;
            }
        }

        return result;
    }

    private static bool TryParseFloat(double v, out double result)
    {
        result = v;
        return double.IsFinite(v);
    }
}
