using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using SharpGLTF.Schema2;

namespace LabPet.Client.Rendering;

internal sealed class AuthoredPetClips
{
    internal static readonly Matrix4x4 Fit = Matrix4x4.CreateScale(.73f) * Matrix4x4.CreateTranslation(0, -.94f, 0);
    private readonly Dictionary<string, (float Duration, Matrix4x4[][] Frames)> _clips = [];
    private readonly (Node Joint, Matrix4x4 InverseBindMatrix)[] _bones;
    private readonly int[] _parents;
    internal Dictionary<string, System.Windows.Media.Media3D.Material> Materials { get; } = [];

    internal static PetModelMetadata Metadata() => new()
    {
        HeadPivot = new(0,.075,0), HeadSocket = new(0,1.23,0), HeadRadiusX = .73,
        NameplateAnchor = new(0,-.94,.18)
    };

    internal AuthoredPetClips(ModelRoot model, Skin skin)
    {
        _bones = Enumerable.Range(0, skin.JointsCount).Select(skin.GetJoint).ToArray();
        _parents = _bones.Select(b => Array.FindIndex(_bones, p => p.Joint == b.Joint.VisualParent)).ToArray();
        foreach (var clip in model.LogicalAnimations)
        {
            int steps = Math.Max(1, (int)Math.Round(clip.Duration * 30));
            var frames = new Matrix4x4[steps+1][];
            for (int i=0; i<=steps; i++)
                frames[i] = _bones.Select(b => b.Joint.GetLocalTransform(clip, clip.Duration*i/steps).Matrix).ToArray();
            _clips.Add(clip.Name, (clip.Duration, frames));
        }
        foreach (var material in model.LogicalMaterials)
        {
            var channel = material.FindChannel("BaseColor")!.Value;
            Brush brush;
            if (channel.Texture?.PrimaryImage is { } image)
            {
                using var stream = image.Content.Open();
                var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                brush = new ImageBrush(bitmap);
            }
            else
            {
                var color = channel.Color;
                brush = new SolidColorBrush(Color.FromScRgb(color.W,color.X,color.Y,color.Z));
            }
            var diffuse = new DiffuseMaterial(brush);
            diffuse.Freeze();
            Materials.Add(material.Name, diffuse);
        }
    }

    internal void Apply(string name, float time, PetRigHandle rig, float typing, float left, float right,
        Matrix4x4[] world, Matrix4x4[] palette, MatrixTransform3D head)
    {
        var clip = _clips[name];
        var position = time % clip.Duration / clip.Duration * (clip.Frames.Length-1);
        var frame = Math.Min((int)position,clip.Frames.Length-2);
        for (int i=0; i<_bones.Length; i++)
        {
            var local = Matrix4x4.Lerp(clip.Frames[frame][i],clip.Frames[frame+1][i],position-frame);
            var bone = _bones[i];
            if (bone.Joint.Name == "Head")
                local = Matrix4x4.CreateFromYawPitchRoll((float)rig.HeadYaw.Angle*MathF.PI/180,
                    (float)rig.HeadPitch.Angle*MathF.PI/180,(float)rig.HeadRoll.Angle*MathF.PI/180) * local;
            if (bone.Joint.Name is "Wing.L" or "Wing.R")
                local = Matrix4x4.CreateRotationX(typing * -.48f + (bone.Joint.Name == "Wing.L" ? left : right)*.42f) * local;
            world[i] = _parents[i] < 0 ? local : local * world[_parents[i]];
            palette[i] = bone.InverseBindMatrix * world[i] * Fit;
            if (bone.Joint.Name == "Head")
            {
                Matrix4x4.Invert(Fit,out var inverse);
                var m = inverse * palette[i];
                head.Matrix = new(m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44);
            }
        }
    }
}
