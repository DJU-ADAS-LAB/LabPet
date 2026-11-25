using System.Numerics;
using System.Windows.Media.Media3D;
using Quaternion = System.Numerics.Quaternion;

namespace LabPet.Client.Rendering;

/// <summary>얼굴과 소켓은 머리의 강체 변환을 공유하고 가중치가 걸린 표면만 CPU에서 변형한다.</summary>
internal sealed class PetSkinInstance(CharacterSkin asset)
{
    private const float Radians = MathF.PI / 180;
    private const float AuthoredTransitionSeconds = 0.24f;
    private readonly Matrix4x4[] _palette = new Matrix4x4[asset.Bones.Length];
    private readonly Quaternion[] _rotations = Enumerable.Repeat(Quaternion.Identity, asset.Bones.Length).ToArray();
    private readonly Vector3[] _boneOffsets = new Vector3[asset.Bones.Length];
    private readonly Vector3[] _targetBoneOffsets = new Vector3[asset.Bones.Length];
    private readonly float[] _lag = new float[asset.Bones.Length];
    private readonly float[] _velocity = new float[asset.Bones.Length];
    private readonly List<SurfaceInstance> _surfaces = [];
    private readonly bool[] _changed = new bool[asset.Bones.Length];
    private readonly Quaternion[] _ik = new Quaternion[asset.Bones.Length];
    private readonly float[] _ikWeights = new float[asset.Bones.Length];

    internal MatrixTransform3D HeadSocket { get; } = new();
    private string _clip = "";
    private float _time;
    private float? _lastProgress;
    private float _transitionElapsed = AuthoredTransitionSeconds;
    private readonly AuthoredPetClips.BonePose[] _authoredPose = asset.Clips is null ? [] : new AuthoredPetClips.BonePose[asset.Bones.Length];
    private readonly AuthoredPetClips.BonePose[] _transitionStart = asset.Clips is null ? [] : new AuthoredPetClips.BonePose[asset.Bones.Length];
    private readonly Matrix4x4[] _world = new Matrix4x4[asset.Bones.Length];

    internal CharacterSkin Asset => asset;
    internal bool IsAuthoredTransitioning => _transitionElapsed < AuthoredTransitionSeconds;

    internal void Bind(ObjSubMesh part, GeometryModel3D model) =>
        _surfaces.Add(new(model, part.Geometry, asset.Surfaces[part]));

    internal void Update(PetRigHandle rig, float dt, float stride, float typing, float leftStrike, float rightStrike,
        float waveTime, float step, string clip = "idle", float? progress = null)
    {
        if (asset.Clips is { } authored)
        {
            UpdateAuthoredPose(authored, clip, dt, progress);
            authored.Apply(_authoredPose, rig, typing, leftStrike, rightStrike, _world, _palette, HeadSocket);
            Array.Fill(_changed, true);
            foreach (var surface in _surfaces) surface.Apply(_palette, _changed);
            return;
        }
        var body = rig.BodyTransform?.Value ?? Matrix3D.Identity;
        var inverseBody = body;
        if (typing > 0.005f || step > 0.005f) inverseBody.Invert();
        var kb = rig.TypingKeyboard;
        Array.Clear(_ikWeights);
        Array.Clear(_targetBoneOffsets);
        for (int i = 0; i < asset.Bones.Length; i++)
        {
            var tip = asset.Bones[i];
            if (tip.Parent < 0) continue;
            var upper = asset.Bones[tip.Parent];
            var leftSide = tip.Name.StartsWith("Left", StringComparison.Ordinal);
            Vector3 goal;
            float layer;
            float tipPitchRad = 0;
            if (tip.Name is "LeftPawTip" or "RightPawTip")
            {
                if (typing < 0.005f) continue;
                float tap = leftSide ? leftStrike : rightStrike;
                float keyX = (leftSide ? -1f : 1f) * kb.KeyReachX;
                float lift = (tap + 0.92f) * 0.072f;
                float pressZ = tap < 0 ? 0.035f : -0.02f;
                var world = new Point3D(
                    keyX,
                    kb.KeyTopY + 0.015f + lift,
                    kb.CenterZ - 0.06f + pressZ);
                var local = inverseBody.Transform(world);
                goal = new((float)local.X, (float)local.Y, (float)local.Z);
                tipPitchRad = -tap * 12f * Radians;
                layer = typing;
            }
            else if (tip.Name is "LeftArmTip" or "RightArmTip")
            {
                if (typing < 0.005f) continue;
                float tap = leftSide ? leftStrike : rightStrike;
                float keyX = (leftSide ? -1f : 1f) * MathF.Min(kb.KeyReachX, MathF.Abs(upper.Bind.Translation.X) * 0.72f);
                var armOffset = new Vector3(
                    (leftSide ? 1f : -1f) * kb.ArmInwardOffsetX * typing,
                    0f,
                    kb.ArmForwardOffsetZ * typing);
                _targetBoneOffsets[tip.Parent] = armOffset;

                float lift = (tap + 0.85f) * 0.055f;
                float pressZ = tap < 0 ? 0.03f : -0.015f;
                var world = new Point3D(
                    keyX,
                    kb.KeyTopY + 0.015f + lift,
                    kb.CenterZ - 0.06f + pressZ);
                var local = inverseBody.Transform(world);
                var kbLocal = new Vector3((float)local.X, (float)local.Y, (float)local.Z);
                var shoulder = upper.Bind.Translation + armOffset;
                var toKey = kbLocal - shoulder;
                var dist = toKey.Length();
                var aim = dist < 1e-6f ? Vector3.UnitZ : toKey / dist;
                var reach = Vector3.Distance(upper.Bind.Translation, tip.Bind.Translation)
                    + Vector3.Distance(tip.Bind.Translation, tip.End);
                if (dist <= reach * 0.96f)
                {
                    goal = kbLocal - armOffset;
                }
                else
                {
                    var strokeDir = Vector3.Normalize(new Vector3(aim.X, aim.Y + (tap + 0.15f) * 0.30f, aim.Z));
                    goal = upper.Bind.Translation + strokeDir * (reach * 0.94f);
                }

                tipPitchRad = -tap * 18f * Radians;
                layer = typing;
            }
            else if (tip.Name is "LeftFoot" or "RightFoot")
            {
                if (step < 0.005f) continue;
                var swing = (leftSide ? stride : -stride) / 22;
                goal = tip.End + new Vector3(0, Math.Max(0, swing) * 0.095f, swing * 0.10f);
                // 발바닥 높이를 그림자 평면에 맞추고 들고 있는 발에만 보폭만큼 높이를 더한다.
                var world = body.Transform(new Point3D(goal.X, goal.Y, goal.Z));
                world.Y = asset.Metadata.ShadowY + 0.025 + Math.Max(0, swing) * 0.095;
                var planted = inverseBody.Transform(world);
                goal = new((float)planted.X, (float)planted.Y, (float)planted.Z);
                layer = step;
            }
            else continue;
            SolveChain(upper.Bind.Translation, tip.Bind.Translation, tip.End, goal, tipPitchRad, out _ik[tip.Parent], out _ik[i]);
            _ikWeights[tip.Parent] = _ikWeights[i] = layer;
        }

        for (int i = 0; i < asset.Bones.Length; i++)
        {
            var bone = asset.Bones[i];
            bool left = bone.Name.StartsWith("Left", StringComparison.Ordinal);
            var ear = left ? rig.LeftEarRotation : rig.RightEarRotation;
            var arm = left ? rig.LeftArmRotation : rig.RightArmRotation;
            var pitch = left ? rig.LeftArmPitch : rig.RightArmPitch;
            var paw = left ? rig.LeftPawTranslate : rig.RightPawTranslate;
            var strike = left ? leftStrike : rightStrike;
            float x = 0, y = 0, z = 0;
            switch (bone.Name)
            {
                case "Head":
                    // 머리 강체와 동일하게 Y, X, Z 순서로 적용한다.
                    x = (float)rig.HeadPitch.Angle;
                    y = (float)rig.HeadYaw.Angle;
                    z = (float)rig.HeadRoll.Angle;
                    break;
                case "LeftEar": case "RightEar": z = (float)(ear?.Angle ?? 0); break;
                case "LeftEarTip": case "RightEarTip":
                    z = Spring(i, (float)(ear?.Angle ?? 0) * 0.45f, dt);
                    x = Math.Clamp((float)(rig.BodyTranslate.OffsetY * 55 - rig.HeadPitch.Angle * 0.2), -14, 14);
                    break;
                case "Tail": y = (float)(rig.TailRotation?.Angle ?? 0); break;
                case "TailTip": y = Spring(i, (float)(rig.TailRotation?.Angle ?? 0) * 0.7f, dt); break;
                case "LeftArm": case "RightArm":
                    x = (float)(pitch?.Angle ?? 0);
                    z = (float)(arm?.Angle ?? 0);
                    break;
                case "LeftArmTip": case "RightArmTip":
                    x = (float)(pitch?.Angle ?? 0) * 0.45f;
                    z = (float)(arm?.Angle ?? 0) * 0.35f;
                    break;
                case "LeftPaw": case "RightPaw":
                    x = -(float)((paw?.OffsetZ ?? 0) * 110 + (paw?.OffsetY ?? 0) * 40);
                    break;
                case "LeftPawTip": case "RightPawTip":
                    x = -typing * 14 - strike * 22;
                    break;
                case "LeftLeg": case "RightLeg": x = left ? stride : -stride; break;
            }

            var target = Quaternion.CreateFromAxisAngle(Vector3.UnitY, Math.Clamp(y, -55, 55) * Radians);
            target = Quaternion.Concatenate(target, Quaternion.CreateFromAxisAngle(Vector3.UnitX, Math.Clamp(x, -65, 65) * Radians));
            target = Quaternion.Concatenate(target, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Math.Clamp(z, -65, 65) * Radians));
            if (bone.Wave != null && waveTime >= 0 && bone.Parent >= 0)
            {
                var sampled = Matrix4x4.CreateFromQuaternion(bone.Wave.GetPoint(waveTime));
                var delta = bone.InverseBind * sampled * asset.Bones[bone.Parent].Bind;
                var wave = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(delta));
                target = Quaternion.Concatenate(target, wave);
            }
            if (_ikWeights[i] > 0) target = Quaternion.Slerp(target, _ik[i], _ikWeights[i]);
            var angle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(target.W), 0, 1));
            if (angle > bone.Limit * Radians)
                target = Quaternion.Slerp(Quaternion.Identity, target, bone.Limit * Radians / angle);
            var rotation = bone.Name == "Head" ? target : Quaternion.Slerp(_rotations[i], target, 1 - MathF.Exp(-26 * dt));
            _rotations[i] = rotation;
            var offset = Vector3.Lerp(_boneOffsets[i], _targetBoneOffsets[i], 1 - MathF.Exp(-22 * dt));
            if (offset.LengthSquared() < 1e-8f) offset = Vector3.Zero;
            _boneOffsets[i] = offset;
            // 바인드 공간에서 회전·미세 전방 보정한 뒤 상위 관절의 변형을 누적한다.
            var pivot = bone.Bind.Translation;
            var posed = bone.Bind * Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateFromQuaternion(rotation)
                * Matrix4x4.CreateTranslation(pivot + offset);
            var matrix = bone.InverseBind * posed;
            if (bone.Parent >= 0) matrix *= _palette[bone.Parent];
            _changed[i] = _palette[i] != matrix;
            _palette[i] = matrix;
        }
        foreach (var surface in _surfaces) surface.Apply(_palette, _changed);
    }

    private void UpdateAuthoredPose(AuthoredPetClips authored, string clip, float dt, float? progress)
    {
        if (_clip != clip || progress < _lastProgress)
        {
            if (_clip.Length > 0)
            {
                // 보간 중 다시 바뀌면 직전에 화면에 반영한 자세에서 이어 간다.
                _authoredPose.CopyTo(_transitionStart, 0);
                _transitionElapsed = 0;
            }

            _clip = clip;
            _time = 0;
        }
        else
        {
            _time += dt;
            _transitionElapsed = Math.Min(AuthoredTransitionSeconds, _transitionElapsed + dt);
        }

        _lastProgress = progress;
        authored.Sample(clip, _time, progress, _authoredPose);
        if (IsAuthoredTransitioning)
        {
            var weight = (float)PetMotionClips.SmoothStep(_transitionElapsed / AuthoredTransitionSeconds);
            for (var i = 0; i < _authoredPose.Length; i++)
            {
                _authoredPose[i] = AuthoredPetClips.BonePose.Blend(_transitionStart[i], _authoredPose[i], weight);
            }
        }
    }

    private static void SolveChain(Vector3 root, Vector3 hinge, Vector3 end, Vector3 goal, float tipPitchRad,
        out Quaternion upper, out Quaternion lower)
    {
        var a = Vector3.Distance(root, hinge);
        var b = Vector3.Distance(hinge, end);
        var reach = goal - root;
        var length = reach.Length();
        var direction = length < 0.0001f ? Vector3.UnitZ : reach / length;
        var distance = Math.Clamp(length, MathF.Abs(a - b) + 0.0001f, a + b - 0.0001f);
        var along = (a * a + distance * distance - b * b) / (2 * distance);
        var pole = -Vector3.UnitY;
        if (Math.Abs(Vector3.Dot(pole, direction)) > 0.95f) pole = -Vector3.UnitZ;
        var bend = Vector3.Normalize(pole - direction * Vector3.Dot(pole, direction));
        var elbow = root + along * direction + MathF.Sqrt(MathF.Max(0, a * a-along * along)) * bend;
        upper = Swing(hinge - root, elbow - root);
        var lowerInWorld = Swing(end - hinge, root + direction * distance - elbow);
        if (tipPitchRad != 0)
            lowerInWorld = Quaternion.Concatenate(lowerInWorld, Quaternion.CreateFromAxisAngle(Vector3.UnitX, tipPitchRad));
        lower = Quaternion.Concatenate(lowerInWorld, Quaternion.Inverse(upper));
    }

    private static Quaternion Swing(Vector3 from, Vector3 to)
    {
        from = Vector3.Normalize(from);
        to = Vector3.Normalize(to);
        var axis = Vector3.Cross(from, to);
        var cosine = Math.Clamp(Vector3.Dot(from, to), -1, 1);
        if (axis.LengthSquared() < 1e-9f) return Quaternion.Identity;
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), Math.Min(MathF.Acos(cosine), 1.45f));
    }

    private float Spring(int i, float target, float dt)
    {
        // 임계 감쇠 해석해를 사용해 유휴 프레임 간격에서도 오버슈트를 제한한다.
        const float stiffness = 12;
        var d = _lag[i] - target;
        var impulse = _velocity[i] + stiffness * d;
        var decay = MathF.Exp(-stiffness * dt);
        _lag[i] = target + (d + impulse * dt) * decay;
        _velocity[i] = (_velocity[i] - stiffness * impulse * dt) * decay;
        return _lag[i];
    }

    private sealed class SurfaceInstance(GeometryModel3D model, MeshGeometry3D rest, SkinSurface data)
    {
        private MeshGeometry3D? _mesh;
        private Point3DCollection? _positions;
        private Vector3DCollection? _normals;

        private readonly int[] _usedBones = UsedBones(data);

        private static int[] UsedBones(SkinSurface data)
        {
            var used = new HashSet<int>();
            for (int i = 0; i < data.Joints.Length; i++)
                for (int j = 0; j < 4; j++)
                    if (data.Weights[i][j] > 0) used.Add((int)data.Joints[i][j]);
            return used.ToArray();
        }

        internal void Apply(Matrix4x4[] palette, bool[] changed)
        {
            bool dirty = false;
            foreach (var index in _usedBones) dirty |= changed[index];
            if (!dirty) return;
            if (_mesh == null)
            {
                _positions = rest.Positions.Clone();
                _normals = rest.Normals.Clone();
                _mesh = new MeshGeometry3D { TriangleIndices = rest.TriangleIndices, TextureCoordinates = rest.TextureCoordinates };
                model.Geometry = _mesh;
            }
            _mesh.Positions = null;
            _mesh.Normals = null;
            for (int i = 0; i < data.Positions.Length; i++)
            {
                var p = Vector3.Zero;
                var n = Vector3.Zero;
                for (int j = 0; j < 4; j++)
                {
                    var weight = data.Weights[i][j];
                    if (weight == 0) continue;
                    ref var m = ref palette[(int)data.Joints[i][j]];
                    p += Vector3.Transform(data.Positions[i], m) * weight;
                    n += Vector3.TransformNormal(data.Normals[i], m) * weight;
                }
                n = Vector3.Normalize(n);
                _positions![i] = new(p.X, p.Y, p.Z);
                _normals![i] = new(n.X, n.Y, n.Z);
            }
            _mesh.Positions = _positions;
            _mesh.Normals = _normals;
        }
    }
}
