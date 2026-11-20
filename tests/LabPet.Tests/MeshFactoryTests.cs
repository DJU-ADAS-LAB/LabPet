using LabPet.Client.Rendering;
using LabPet.Shared.Models;
using Xunit;

namespace LabPet.Tests;

public sealed class MeshFactoryTests
{
    [Theory]
    [InlineData(PetSpeciesIds.MochiSpirit, "default", "none", "none")]
    [InlineData(PetSpeciesIds.ChibiCat, "happy", "headset", "halo_ring")]
    [InlineData(PetSpeciesIds.FluffyCloud, "sparkle", "santa_hat", "music_notes")]
    [InlineData(PetSpeciesIds.FluffyPuppy, "default", "crown", "snowflakes")]
    public void BuildPetRig_ProducesValidLowToMidPolyMeshOnStaThread(
        string speciesId,
        string expressionId,
        string accessoryId,
        string auraId)
    {
        var triangleCount = 0;
        var hasRoot = false;
        Exception? threadEx = null;

        var staThread = new Thread(() =>
        {
            try
            {
                var factory = new ProceduralPetMeshFactory();
                var rig = factory.BuildPetRig(new PetAppearance
                {
                    SpeciesId = speciesId,
                    ExpressionId = expressionId,
                    HeadAccessoryId = accessoryId,
                    AuraEffectId = auraId
                });

                hasRoot = rig.RootModel.Children.Count > 0;
                triangleCount = rig.TotalTriangleCount;
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();

        Assert.Null(threadEx);
        Assert.True(hasRoot);
        Assert.InRange(triangleCount, 500, 15000);
    }
}
