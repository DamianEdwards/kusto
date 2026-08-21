using Azure.Identity;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kusto.Cli.Tests;

public sealed class AuthenticationRecordStoreTests : IDisposable
{
    private readonly string _authDirectory;

    public AuthenticationRecordStoreTests()
    {
        _authDirectory = Path.Combine(AppContext.BaseDirectory, "wam-record-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_authDirectory))
            {
                Directory.Delete(_authDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private AuthenticationRecordStore CreateStore() => new(_authDirectory, NullLogger.Instance);

    private static AuthenticationRecordKey CreateKey(string account = WamTestSupport.Account) =>
        AuthenticationRecordKey.Create(
            WamTestSupport.ClusterUrl,
            WamTestSupport.TenantId,
            account,
            WamTestSupport.ClientId,
            WamTestSupport.Resource,
            WamTestSupport.LoginHost);

    [Fact]
    public void FileName_IsSha256HexWithNoRawUpn()
    {
        var key = CreateKey();

        Assert.EndsWith(".json", key.FileName);
        var hex = key.FileName[..^5];
        Assert.Equal(64, hex.Length);
        Assert.All(hex, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c)));
        Assert.DoesNotContain("githubazure", key.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, key.FileName);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, key.FileName);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsRecord()
    {
        var store = CreateStore();
        var key = CreateKey();
        var record = WamTestSupport.CreateRecord();

        await store.SaveAsync(key, record, CancellationToken.None);
        var loaded = await store.TryLoadAsync(key, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(WamTestSupport.Account, loaded!.Username);
        Assert.Equal(WamTestSupport.TenantId, loaded.TenantId);
    }

    [Fact]
    public async Task TryLoad_MissingRecord_ReturnsNull()
    {
        var store = CreateStore();
        Assert.Null(await store.TryLoadAsync(CreateKey(), CancellationToken.None));
    }

    [Fact]
    public async Task Save_RecordNotMatchingKey_Throws()
    {
        var store = CreateStore();
        var key = CreateKey(account: "someone-else@contoso.com");
        var record = WamTestSupport.CreateRecord(); // username = default account, mismatched to key

        await Assert.ThrowsAsync<UserFacingException>(() => store.SaveAsync(key, record, CancellationToken.None));
    }

    [Fact]
    public async Task TryLoad_TamperedRecord_ReturnsNull()
    {
        var store = CreateStore();
        var key = CreateKey();
        await store.SaveAsync(key, WamTestSupport.CreateRecord(), CancellationToken.None);

        // Overwrite the persisted file with a record whose content no longer matches the key.
        var recordPath = Path.Combine(_authDirectory, key.FileName);
        var tampered = WamTestSupport.CreateRecord(username: "attacker@evil.com");
        await using (var stream = File.Create(recordPath))
        {
            await tampered.SerializeAsync(stream, CancellationToken.None);
        }

        Assert.Null(await store.TryLoadAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_RemovesRecord()
    {
        var store = CreateStore();
        var key = CreateKey();
        await store.SaveAsync(key, WamTestSupport.CreateRecord(), CancellationToken.None);

        Assert.True(await store.DeleteAsync(
            key.ClusterUrl,
            key.TenantId,
            key.Account,
            CancellationToken.None));
        Assert.Null(await store.TryLoadAsync(key, CancellationToken.None));
        Assert.False(await store.DeleteAsync(
            key.ClusterUrl,
            key.TenantId,
            key.Account,
            CancellationToken.None));
    }

    [Fact]
    public void Key_DiffersByAccount()
    {
        var a = CreateKey();
        var b = CreateKey(account: "another@contoso.com");

        Assert.NotEqual(a.FileName, b.FileName);
    }
}
