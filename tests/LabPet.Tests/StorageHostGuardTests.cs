using System;
using System.Reflection;
using LabPet.Client.Storage;
using Xunit;

namespace LabPet.Tests;

public sealed class StorageHostGuardTests
{
    [Fact]
    public void PublicConstructor_RejectsExternalReflectionHost()
    {
        var type = typeof(JsonAppDataStore);
        var constructor = Assert.Single(type.GetConstructors());
        Assert.Single(constructor.GetParameters());
        var error = Assert.Throws<TargetInvocationException>(() =>
            constructor.Invoke(new object[] { new StateMigrator() }));
        Assert.IsType<UnauthorizedAccessException>(error.InnerException);
    }

    [Fact]
    public void CustomDirectoryConstructor_IsNotPublic()
    {
        Assert.DoesNotContain(typeof(JsonAppDataStore).GetConstructors(),
            constructor => constructor.GetParameters().Length != 1);
    }
}
