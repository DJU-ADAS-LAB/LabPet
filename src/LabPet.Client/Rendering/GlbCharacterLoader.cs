using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using SharpGLTF.Schema2;
using SharpGLTF.Animations;
using Quaternion = System.Numerics.Quaternion;

namespace LabPet.Client.Rendering;

internal sealed record SkinBone(string Name, int Parent, Matrix4x4 Bind, Matrix4x4 InverseBind, Vector3 End, float Limit,
    ICurveSampler<Quaternion>? Wave);
internal sealed record SkinSurface(Vector3[] Positions, Vector3[] Normals, Vector4[] Joints, Vector4[] Weights);
internal sealed record CharacterSkin(PetModelMetadata Metadata, ObjMeshAsset Mesh, SkinBone[] Bones,
    Dictionary<ObjSubMesh, SkinSurface> Surfaces, AuthoredPetClips? Clips = null);

internal sealed class GlbCharacterLoader
{
    private readonly Dictionary<string, WeakReference<CharacterSkin>> _cache = [];

    public CharacterSkin? Load(string path)
    {
        if (!File.Exists(path)) return null;
        lock (_cache)
        {
            if (_cache.TryGetValue(path, out var weak) && weak.TryGetTarget(out var cached)) return cached;
            try
            {
                var skin = Read(path);
                _cache[path] = new(skin);
                return skin;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                System.Diagnostics.Trace.TraceWarning($"캐릭터 GLB 로드 실패 ({path}): {error.Message}");
                return null;
            }
        }
    }

    private static CharacterSkin Read(string path)
    {
        var model = ModelRoot.Load(path);
        var skin = model.LogicalSkins.Single();
        var joints = Enumerable.Range(0, skin.JointsCount).Select(skin.GetJoint).ToArray();
        var authored = Path.GetFileName(Path.GetDirectoryName(path)) == LabPet.Shared.Models.PetSpeciesIds.Joey;
        var clips = authored ? new AuthoredPetClips(model, skin) : null;
        var wave = authored ? null : model.LogicalAnimations.Single(a => a.Name == "Wave");
        var bones = new SkinBone[joints.Length];
        for (int i = 0; i < joints.Length; i++)
        {
            var (node, inverse) = joints[i];
            int parent = Array.FindIndex(joints, joint => joint.Joint == node.VisualParent);
            if (parent >= i || !authored && !node.Name.StartsWith("Joint_", StringComparison.Ordinal) ||
                !Matrix4x4.Invert(inverse, out var bind) || !float.IsFinite(bind.GetDeterminant()))
                throw new InvalidDataException("관절 순서 또는 바인드 행렬 오류");
            if (authored)
            {
                bones[i] = new(node.Name, parent, bind, inverse, bind.Translation, 180, null);
                continue;
            }
            var end = node.Extras!["end"]!.Deserialize<float[]>()!;
            // 인사 클립의 마스크는 왼쪽 팔에만 적용한다.
            var sampler = node.Name is "Joint_LeftArm" or "Joint_LeftPaw"
                ? wave!.FindRotationChannel(node)?.GetRotationSampler()?.CreateCurveSampler(true) : null;
            bones[i] = new(node.Name[6..], parent, bind, inverse, new(end[0], end[1], end[2]),
                node.Extras["limit"]!.GetValue<float>(), sampler);
        }

        if (bones.Count(b => b.Parent < 0) != 1 || bones.Select(b => b.Name).Distinct().Count() != bones.Length ||
            !bones.Any(b => b.Name == "Head") || bones.Any(b => !float.IsFinite(b.End.LengthSquared()) ||
                !(b.Limit > 0 && b.Limit <= 180)))
            throw new InvalidDataException("관절 계층 또는 회전 한계 오류");

        foreach (var bone in bones.Where(b => b.Name is "LeftArm" or "RightArm" or "LeftPaw" or "RightPaw"))
        {
            if (!bones.Any(t => t.Name == bone.Name + "Tip" && t.Parent >= 0 &&
                bones[t.Parent] == bone && Vector3.Distance(t.End, t.Bind.Translation) > 0.001f &&
                Vector3.Distance(bone.Bind.Translation, t.Bind.Translation) > 0.001f))
                throw new InvalidDataException("손끝 관절 누락");
        }

        var metadata = authored ? AuthoredPetClips.Metadata() : JsonSerializer.Deserialize<PetModelMetadata>(
            model.LogicalNodes.Single(n => n.Name == "Skeleton").Extras!["metadata"]!.GetValue<string>(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("소켓 누락");
        var parts = new List<ObjSubMesh>();
        var surfaces = new Dictionary<ObjSubMesh, SkinSurface>();
        foreach (var node in model.LogicalNodes.Where(n => n.Mesh != null))
        {
            if (node.Skin != skin) throw new InvalidDataException("메시 스킨 누락");
            foreach (var primitive in node.Mesh!.Primitives)
            {
                var positions = primitive.GetVertexAccessor("POSITION").AsVector3Array().ToArray();
                var normals = primitive.GetVertexAccessor("NORMAL").AsVector3Array().ToArray();
                var weights = primitive.GetVertexAccessor("WEIGHTS_0").AsVector4Array().ToArray();
                var ids = primitive.GetVertexAccessor("JOINTS_0").AsVector4Array().ToArray();
                if (normals.Length != positions.Length || ids.Length != positions.Length || weights.Length != ids.Length)
                    throw new InvalidDataException("정점 데이터 개수 오류");
                bool rigidHead = true;
                for (int i = 0; i < weights.Length; i++)
                {
                    var w = weights[i];
                    if (!(Math.Abs(Vector4.Dot(w, Vector4.One) - 1) < 0.001f))
                        throw new InvalidDataException("가중치 합 오류");
                    for (int j = 0; j < 4; j++)
                    {
                        if (ids[i][j] < 0 || ids[i][j] >= bones.Length || w[j] < 0)
                            throw new InvalidDataException("관절 인덱스 또는 가중치 오류");
                        if (w[j] > 0 && bones[(int)ids[i][j]].Name != "Head") rigidHead = false;
                    }
                }
                var mesh = new MeshGeometry3D
                {
                    Positions = new(positions.Select(v => new Point3D(v.X, v.Y, v.Z))),
                    Normals = new(normals.Select(v => new Vector3D(v.X, v.Y, v.Z))),
                    TriangleIndices = new Int32Collection(primitive.GetIndices().Select(n => checked((int)n)))
                };
                if (authored)
                {
                    mesh.Positions = new(positions.Select(v => Vector3.Transform(v, AuthoredPetClips.Fit))
                        .Select(v => new Point3D(v.X, v.Y, v.Z)));
                    if (primitive.GetVertexAccessor("TEXCOORD_0") is { } uv)
                        mesh.TextureCoordinates = new(uv.AsVector2Array().Select(v => new System.Windows.Point(v.X, v.Y)));
                }
                mesh.Freeze();
                var part = new ObjSubMesh { PartName = node.Name, MaterialSlot = primitive.Material.Name, Geometry = mesh };
                parts.Add(part);
                if (authored || !rigidHead) surfaces.Add(part, new(positions, normals, ids, weights));
            }
        }
        if (!authored && !parts.Any(p => p.PartName == "Head") || surfaces.Count == 0)
            throw new InvalidDataException("머리 또는 변형 메시 누락");
        return new(metadata, new ObjMeshAsset { SourcePath = path, SubMeshes = parts,
            TotalTriangleCount = parts.Sum(p => p.TriangleCount) }, bones, surfaces, clips);
    }
}
