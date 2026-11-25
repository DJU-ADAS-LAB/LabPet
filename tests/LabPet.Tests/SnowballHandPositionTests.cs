using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Media.Media3D;
using LabPet.Client.Rendering;
using LabPet.Shared.Models;
using Xunit;

namespace LabPet.Tests;

public sealed class SnowballHandPositionTests
{
    [Theory]
    [InlineData(PetSpeciesIds.ChibiCat)]
    [InlineData(PetSpeciesIds.FluffyPuppy)]
    [InlineData(PetSpeciesIds.FluffyCloud)]
    [InlineData(PetSpeciesIds.MochiSpirit)]
    [InlineData(PetSpeciesIds.Chiikawa)]
    [InlineData(PetSpeciesIds.Hachiware)]
    [InlineData(PetSpeciesIds.Usagi)]
    [InlineData(PetSpeciesIds.PinkReference)]
    public void HandTracksJointAndAppliesBodyTransformOnce(string species) => RunSta(() =>
    {
        var rig = new ProceduralPetMeshFactory().BuildPetRig(
            new PetAppearance { SpeciesId = species }, PetRigEffects.None);
        Assert.True(rig.TryGetRightHandPosition(out var neutral));
        if (rig.RightArmPitch is { } pitch) pitch.Angle = 45;
        else
        {
            Assert.NotNull(rig.RightPawTranslate);
            rig.RightPawTranslate!.OffsetY = 0.3;
        }
        Assert.True(rig.TryGetRightHandPosition(out var moved));
        Assert.True((moved - neutral).Length > 0.01);
        rig.BodyScale.ScaleX = 1.5;
        rig.BodyScale.ScaleY = 2;
        rig.BodyScale.ScaleZ = 0.75;
        rig.BodyTranslate.OffsetX = 3;
        rig.BodyTranslate.OffsetY = -2;
        rig.BodyTranslate.OffsetZ = 1;
        Assert.True(rig.TryGetRightHandPosition(out var transformed));
        Assert.Equal(moved.X * 1.5 + 3, transformed.X, 8);
        Assert.Equal(moved.Y * 2 - 2, transformed.Y, 8);
        Assert.Equal(moved.Z * 0.75 + 1, transformed.Z, 8);
        rig.BodyTranslate.OffsetX = double.NaN;
        Assert.False(rig.TryGetRightHandPosition(out var rejected));
        Assert.Equal(default(Point3D), rejected);
    });

    [Theory]
    [InlineData(PetSpeciesIds.FluffyCloud)]
    [InlineData(PetSpeciesIds.MochiSpirit)]
    public void HeadAttachedHandTracksHeadRotation(string species) => RunSta(() =>
    {
        var rig = new ProceduralPetMeshFactory().BuildPetRig(
            new PetAppearance { SpeciesId = species }, PetRigEffects.None);
        Assert.True(rig.TryGetRightHandPosition(out var before));
        rig.HeadYaw.Angle = 40;
        Assert.True(rig.TryGetRightHandPosition(out var after));
        Assert.True((after - before).Length > 0.1);
    });

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Hand position test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
