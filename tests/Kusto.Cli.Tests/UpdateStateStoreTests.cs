using Microsoft.Extensions.Logging.Abstractions;

namespace Kusto.Cli.Tests;

public sealed class UpdateStateStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsUpdateState()
    {
        using var fixture = new StateStoreFixture();
        var expected = new UpdateState
        {
            Status = UpdateStatus.Staged,
            AvailableVersion = "1.2.3",
            ReleaseTag = "v1.2.3",
            StagedDirectory = @"C:\stage"
        };

        fixture.Store.Save(expected);
        var actual = fixture.Store.Load();

        Assert.Equal(UpdateStatus.Staged, actual.Status);
        Assert.Equal("1.2.3", actual.AvailableVersion);
        Assert.Equal("v1.2.3", actual.ReleaseTag);
        Assert.Equal(@"C:\stage", actual.StagedDirectory);
    }

    [Fact]
    public void TryAcquireLock_ExcludesConcurrentWriter()
    {
        using var fixture = new StateStoreFixture();
        var otherStore = new UpdateStateStore(
            NullLogger<UpdateStateStore>.Instance,
            fixture.StatePath,
            fixture.LockPath);

        Assert.True(fixture.Store.TryAcquireLock());
        Assert.False(otherStore.TryAcquireLock());

        fixture.Store.ReleaseLock();
        Assert.True(otherStore.TryAcquireLock());
        otherStore.ReleaseLock();
    }

    [Fact]
    public void Load_ReportsMalformedState()
    {
        using var fixture = new StateStoreFixture();
        Directory.CreateDirectory(fixture.Root);
        File.WriteAllText(fixture.StatePath, "{not-json");

        var exception = Assert.Throws<UserFacingException>(
            () => fixture.Store.Load());

        Assert.Contains("malformed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StateStoreFixture : IDisposable
    {
        public StateStoreFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"kusto-state-test-{Guid.NewGuid():N}");
            StatePath = Path.Combine(Root, "update-state.json");
            LockPath = Path.Combine(Root, ".update-lock");
            Store = new UpdateStateStore(
                NullLogger<UpdateStateStore>.Instance,
                StatePath,
                LockPath);
        }

        public string Root { get; }
        public string StatePath { get; }
        public string LockPath { get; }
        public UpdateStateStore Store { get; }

        public void Dispose()
        {
            Store.ReleaseLock();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
