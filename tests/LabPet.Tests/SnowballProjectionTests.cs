using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using LabPet.Client.Views;
using Xunit;

namespace LabPet.Tests;

public sealed class SnowballProjectionTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Projection_AppliesModelVisualAndParentTransformsOnce(double scale)
        => RunSta(() =>
        {
            var viewport = CreateViewport(scale);
            var model = new Model3DGroup { Transform = new TranslateTransform3D(0.5, 0.25, 0) };
            var visual = new ModelVisual3D
            {
                Content = model,
                Transform = new TranslateTransform3D(0.25, 0, 0)
            };
            var parent = new ModelVisual3D { Transform = new TranslateTransform3D(0.5, 0, 0) };
            parent.Children.Add(visual);
            viewport.Children.Add(parent);
            Assert.True(PetDisplayLayoutHelper.TryProjectPointToViewport(
                viewport, visual, new Point3D(0.25, 0.25, 0), out var point));
            Assert.Equal(350 * scale, point.X, 6);
            Assert.Equal(50 * scale, point.Y, 6);
        });

    [Fact]
    public void Projection_RejectsDetachedMissingCameraAndNonFiniteCoordinates()
        => RunSta(() =>
        {
            var viewport = CreateViewport(1);
            var visual = new ModelVisual3D { Content = new Model3DGroup() };
            Assert.False(PetDisplayLayoutHelper.TryProjectPointToViewport(viewport, visual, default, out _));
            viewport.Children.Add(visual);
            Assert.True(PetDisplayLayoutHelper.TryProjectPointToViewport(viewport, visual, default, out var center));
            Assert.Equal(200, center.X, 6);
            Assert.Equal(100, center.Y, 6);
            foreach (var point in new[] { new Point3D(double.NaN, 0, 0), new Point3D(0, double.PositiveInfinity, 0), new Point3D(0, 0, double.NegativeInfinity) })
            {
                Assert.False(PetDisplayLayoutHelper.TryProjectPointToViewport(viewport, visual, point, out var rejected));
                Assert.Equal(default(Point), rejected);
            }
            viewport.Camera = null;
            Assert.False(PetDisplayLayoutHelper.TryProjectPointToViewport(viewport, visual, default, out _));
        });

    private static Viewport3D CreateViewport(double scale)
    {
        var viewport = new Viewport3D
        {
            Width = 400 * scale,
            Height = 200 * scale,
            Camera = new OrthographicCamera(new Point3D(0, 0, 10), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 4)
        };
        viewport.Measure(new Size(viewport.Width, viewport.Height));
        viewport.Arrange(new Rect(0, 0, viewport.Width, viewport.Height));
        return viewport;
    }

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
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF projection test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
