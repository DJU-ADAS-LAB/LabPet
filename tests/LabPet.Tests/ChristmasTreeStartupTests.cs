using System;
using System.Threading;
using LabPet.Client.SystemIntegration;
using Xunit;

namespace LabPet.Tests;

[CollectionDefinition("Christmas startup environment", DisableParallelization = true)]
public sealed class ChristmasStartupEnvironmentCollection { }

[Collection("Christmas startup environment")]
public sealed class ChristmasTreeStartupTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StartupSignal_SetsOnlyMatchingParentEvent(bool success)
    {
        const string variable = "LABPET_CHRISTMAS_STARTUP";
        var previous = Environment.GetEnvironmentVariable(variable);
        var name = "Local\\LabPet.Christmas.Startup." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".Ready");
        using var failed = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".Failed");
        try
        {
            Environment.SetEnvironmentVariable(variable, name);
            if (success) ChristmasTreeStartup.SignalReady();
            else ChristmasTreeStartup.SignalFailure();
            Assert.Equal(success, ready.WaitOne(0));
            Assert.Equal(!success, failed.WaitOne(0));
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Local\\Unrelated.Event")]
    [InlineData("Local\\LabPet.Christmas.Startup.invalid")]
    public void StartupSignal_MissingOrInvalidParentDoesNotCrash(string? name)
    {
        const string variable = "LABPET_CHRISTMAS_STARTUP";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, name);
            ChristmasTreeStartup.SignalReady();
            ChristmasTreeStartup.SignalFailure();
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    [Fact]
    public void StartupSignal_ParentAlreadyClosedDoesNotCrash()
    {
        const string variable = "LABPET_CHRISTMAS_STARTUP";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable,
                "Local\\LabPet.Christmas.Startup." + Guid.NewGuid().ToString("N"));
            ChristmasTreeStartup.SignalReady();
            ChristmasTreeStartup.SignalFailure();
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }
}
