using LabPet.Client.Updates;
using Velopack;
using Xunit;

namespace LabPet.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task CheckOnly_InstallDownloadsAndRestartsOnceAcrossConcurrentRequests()
    {
        var releaseDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new TestUpdateBackend { Download = ct => releaseDownload.Task.WaitAsync(ct) };
        await using var updates = new UpdateService(backend);
        var restarts = 0;
        updates.RestartRequested += (_, _) => restarts++;

        await updates.CheckForUpdatesAsync();
        await updates.CheckAsync(silentAutoApply: false);
        await updates.CheckAsync(silentAutoApply: false);

        Assert.Equal(UpdatePhase.Available, updates.State.Phase);
        Assert.Equal(3, backend.Checks);
        Assert.Equal(0, backend.Downloads);
        Assert.Equal(0, backend.ApplyAttempts);

        var install = updates.InstallUpdateAsync();
        Assert.Equal(UpdatePhase.Downloading, updates.State.Phase);
        var duplicate = updates.InstallUpdateAsync();
        releaseDownload.SetResult();
        await Task.WhenAll(install, duplicate);

        Assert.Equal(UpdatePhase.Applying, updates.State.Phase);
        Assert.Equal(1, backend.Downloads);
        Assert.Equal(1, backend.ApplyAttempts);
        Assert.Equal(1, restarts);
        Assert.False(backend.AppliedSilently);
    }

    [Fact]
    public async Task InstallFailure_RetainsRetryAndReusesSuccessfulDownload()
    {
        var backend = new TestUpdateBackend
        {
            Download = _ => Task.FromException(new System.IO.IOException("패키지 요청 실패")),
            ApplyError = new InvalidOperationException("업데이트 실행 실패")
        };
        await using var updates = new UpdateService(backend);
        var restarts = 0;
        updates.RestartRequested += (_, _) => restarts++;

        await updates.CheckForUpdatesAsync();
        await updates.InstallUpdateAsync();
        Assert.Equal(UpdatePhase.Available, updates.State.Phase);
        Assert.Contains("다운로드 실패", updates.State.Message);
        Assert.Equal(0, backend.ApplyAttempts);
        Assert.Equal(0, restarts);

        backend.Download = _ => Task.CompletedTask;
        await updates.InstallUpdateAsync();
        Assert.Equal(UpdatePhase.Ready, updates.State.Phase);
        Assert.Contains("업데이트 적용 실패", updates.State.Message);
        Assert.Equal(0, restarts);

        backend.ApplyError = null;
        await updates.InstallUpdateAsync();
        Assert.Equal(UpdatePhase.Applying, updates.State.Phase);
        Assert.Equal(2, backend.Downloads);
        Assert.Equal(2, backend.ApplyAttempts);
        Assert.Equal(1, restarts);
    }

    [Fact]
    public async Task CachedPackage_IsOfferedWithoutAnotherDownloadOrAutomaticRestart()
    {
        var backend = new TestUpdateBackend();
        backend.PendingUpdate = backend.Available!.TargetFullRelease;
        await using var updates = new UpdateService(backend);

        await updates.CheckForUpdatesAsync();
        Assert.Equal(UpdatePhase.Ready, updates.State.Phase);
        Assert.Equal(0, backend.Checks);
        Assert.Equal(0, backend.Downloads);
        Assert.Equal(0, backend.ApplyAttempts);

        await updates.InstallUpdateAsync();
        Assert.Equal(0, backend.Downloads);
        Assert.Equal(1, backend.ApplyAttempts);
    }

    [Fact]
    public async Task WindowsStartup_DownloadsAndRestartsSilently()
    {
        var backend = new TestUpdateBackend();
        await using var updates = new UpdateService(backend);

        await updates.CheckAsync(silentAutoApply: true);

        Assert.Equal(UpdatePhase.Applying, updates.State.Phase);
        Assert.Null(updates.State.Version);
        Assert.Equal(1, backend.Downloads);
        Assert.Equal(1, backend.ApplyAttempts);
        Assert.True(backend.AppliedSilently);
    }
}

internal sealed class TestUpdateBackend : UpdateService.IUpdateBackend
{
    public bool IsInstalled { get; set; } = true;
    public VelopackAsset? PendingUpdate { get; set; }
    public UpdateInfo? Available { get; set; } = new(
        new VelopackAsset { Version = SemanticVersion.Parse("0.4.0") }, false, null, []);
    public Func<CancellationToken, Task> Download { get; set; } = _ => Task.CompletedTask;
    public Exception? ApplyError { get; set; }
    public int Checks { get; private set; }
    public int Downloads { get; private set; }
    public int ApplyAttempts { get; private set; }
    public bool AppliedSilently { get; private set; }

    public Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        Checks++;
        return Task.FromResult(Available);
    }

    public Task DownloadUpdatesAsync(UpdateInfo update, CancellationToken ct)
    {
        Downloads++;
        return Download(ct);
    }

    public void ScheduleApplyAndRestart(VelopackAsset update, bool silent)
    {
        ApplyAttempts++;
        AppliedSilently = silent;
        if (ApplyError is not null)
        {
            throw ApplyError;
        }
    }
}
