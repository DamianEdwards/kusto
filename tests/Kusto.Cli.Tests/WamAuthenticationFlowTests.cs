using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kusto.Cli.Tests;

public sealed class WamTokenProviderTests
{
    private const string CacheName = "kusto-cli-test";

    private static WamTokenProvider Create(
        IPlatform platform,
        FakeMetadataProvider metadata,
        FakeAuthenticationRecordStore store,
        FakeBrokerCredentialFactory factory,
        nint handle = 0) =>
        new(platform, metadata, store, factory, new FakeWindowHandleProvider(handle), CacheName, NullLogger.Instance);

    [Fact]
    public async Task GetTokenAsync_NonWindows_ThrowsWithoutCallingBroker()
    {
        var factory = new FakeBrokerCredentialFactory();
        var provider = Create(new FakePlatform(false), new FakeMetadataProvider(), new FakeAuthenticationRecordStore(), factory);

        await Assert.ThrowsAsync<UserFacingException>(() =>
            provider.GetTokenAsync(WamTestSupport.WamCluster(), CancellationToken.None));
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task GetTokenAsync_ClusterNotConfiguredForWam_Throws()
    {
        var provider = Create(new FakePlatform(true), new FakeMetadataProvider(), new FakeAuthenticationRecordStore(), new FakeBrokerCredentialFactory());

        await Assert.ThrowsAsync<UserFacingException>(() =>
            provider.GetTokenAsync(new ResolvedCluster("help", "https://help.kusto.windows.net"), CancellationToken.None));
    }

    [Fact]
    public async Task GetTokenAsync_MissingRecord_ThrowsLoginRequired()
    {
        var factory = new FakeBrokerCredentialFactory();
        var provider = Create(new FakePlatform(true), new FakeMetadataProvider(), new FakeAuthenticationRecordStore(), factory);

        var ex = await Assert.ThrowsAsync<UserFacingException>(() =>
            provider.GetTokenAsync(WamTestSupport.WamCluster(), CancellationToken.None));

        Assert.Contains("cluster login", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task GetTokenAsync_BrokerRequiresAuthentication_ThrowsLoginRequiredNoFallback()
    {
        var store = new FakeAuthenticationRecordStore { RecordToReturn = WamTestSupport.CreateRecord() };
        var factory = new FakeBrokerCredentialFactory
        {
            OnGetToken = _ => throw new AuthenticationRequiredException("interaction required", new TokenRequestContext(["scope"]))
        };
        var provider = Create(new FakePlatform(true), new FakeMetadataProvider(), store, factory);

        var ex = await Assert.ThrowsAsync<UserFacingException>(() =>
            provider.GetTokenAsync(WamTestSupport.WamCluster(), CancellationToken.None));

        Assert.Contains("cluster login", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("az login", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetTokenAsync_BrokerCredentialUnavailable_ThrowsLoginRequired()
    {
        var store = new FakeAuthenticationRecordStore { RecordToReturn = WamTestSupport.CreateRecord() };
        var factory = new FakeBrokerCredentialFactory
        {
            OnGetToken = _ => throw new CredentialUnavailableException("no cached account")
        };
        var provider = Create(new FakePlatform(true), new FakeMetadataProvider(), store, factory);

        await Assert.ThrowsAsync<UserFacingException>(() =>
            provider.GetTokenAsync(WamTestSupport.WamCluster(), CancellationToken.None));
    }

    [Fact]
    public async Task GetTokenAsync_HappyPath_ReturnsTokenAndUsesSilentOptions()
    {
        var jwt = WamTestSupport.CreateJwt(WamTestSupport.TenantId, WamConstants.ExpectedResource);
        var store = new FakeAuthenticationRecordStore { RecordToReturn = WamTestSupport.CreateRecord() };
        var factory = new FakeBrokerCredentialFactory
        {
            OnGetToken = _ => new AccessToken(jwt, DateTimeOffset.UtcNow.AddHours(1))
        };
        var provider = Create(new FakePlatform(true), new FakeMetadataProvider(), store, factory);

        var token = await provider.GetTokenAsync(WamTestSupport.WamCluster(), CancellationToken.None);

        Assert.Equal(jwt, token);
        Assert.NotNull(factory.LastRequest);
        Assert.True(factory.LastRequest!.DisableAutomaticAuthentication);
        Assert.NotNull(factory.LastRequest.AuthenticationRecord);
        Assert.Equal(WamTestSupport.Account, factory.LastRequest.Account);
        Assert.Equal(CacheName, factory.LastRequest.TokenCacheName);
    }

    [Fact]
    public async Task GetTokenAsync_TokenBoundToWrongTenant_Throws()
    {
        var jwt = WamTestSupport.CreateJwt("22222222-2222-2222-2222-222222222222", WamConstants.ExpectedResource);
        var store = new FakeAuthenticationRecordStore { RecordToReturn = WamTestSupport.CreateRecord() };
        var factory = new FakeBrokerCredentialFactory
        {
            OnGetToken = _ => new AccessToken(jwt, DateTimeOffset.UtcNow.AddHours(1))
        };
        var provider = Create(new FakePlatform(true), new FakeMetadataProvider(), store, factory);

        await Assert.ThrowsAsync<UserFacingException>(() =>
            provider.GetTokenAsync(WamTestSupport.WamCluster(), CancellationToken.None));
    }
}

public sealed class WamClusterAuthenticatorTests
{
    private const string CacheName = "kusto-cli-test";

    private static WamClusterAuthenticator Create(
        IPlatform platform,
        FakeMetadataProvider metadata,
        FakeAuthenticationRecordStore store,
        FakeBrokerCredentialFactory factory,
        nint handle) =>
        new(platform, metadata, store, factory, new FakeWindowHandleProvider(handle), CacheName, NullLogger.Instance);

    private static FakeBrokerCredentialFactory HappyFactory() => new()
    {
        OnAuthenticate = _ => WamTestSupport.CreateRecord(),
        OnGetToken = _ => new AccessToken(
            WamTestSupport.CreateJwt(WamTestSupport.TenantId, WamConstants.ExpectedResource),
            DateTimeOffset.UtcNow.AddHours(1))
    };

    [Fact]
    public async Task LoginAsync_NonWindows_Throws()
    {
        var store = new FakeAuthenticationRecordStore();
        var authenticator = Create(new FakePlatform(false), new FakeMetadataProvider(), store, HappyFactory(), handle: 1);

        await Assert.ThrowsAsync<UserFacingException>(() =>
            authenticator.LoginAsync(WamTestSupport.WamCluster(), CancellationToken.None));
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task LoginAsync_ZeroWindowHandle_Throws()
    {
        var store = new FakeAuthenticationRecordStore();
        var authenticator = Create(new FakePlatform(true), new FakeMetadataProvider(), store, HappyFactory(), handle: 0);

        await Assert.ThrowsAsync<UserFacingException>(() =>
            authenticator.LoginAsync(WamTestSupport.WamCluster(), CancellationToken.None));
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task LoginAsync_AccountMismatch_ThrowsAndDoesNotSave()
    {
        var store = new FakeAuthenticationRecordStore();
        var factory = new FakeBrokerCredentialFactory
        {
            OnAuthenticate = _ => WamTestSupport.CreateRecord(username: "someone-else@contoso.com")
        };
        var authenticator = Create(new FakePlatform(true), new FakeMetadataProvider(), store, factory, handle: 1);

        await Assert.ThrowsAsync<UserFacingException>(() =>
            authenticator.LoginAsync(WamTestSupport.WamCluster(), CancellationToken.None));
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task LoginAsync_TenantMismatch_ThrowsAndDoesNotSave()
    {
        var store = new FakeAuthenticationRecordStore();
        var factory = new FakeBrokerCredentialFactory
        {
            OnAuthenticate = _ => WamTestSupport.CreateRecord(tenantId: "22222222-2222-2222-2222-222222222222")
        };
        var authenticator = Create(new FakePlatform(true), new FakeMetadataProvider(), store, factory, handle: 1);

        await Assert.ThrowsAsync<UserFacingException>(() =>
            authenticator.LoginAsync(WamTestSupport.WamCluster(), CancellationToken.None));
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task LoginAsync_HappyPath_SavesRecord()
    {
        var store = new FakeAuthenticationRecordStore();
        var factory = HappyFactory();
        var authenticator = Create(new FakePlatform(true), new FakeMetadataProvider(), store, factory, handle: 1);

        await authenticator.LoginAsync(WamTestSupport.WamCluster(), CancellationToken.None);

        Assert.Equal(1, store.SaveCount);
        Assert.NotNull(store.LastSavedKey);
        Assert.Equal(WamTestSupport.Account, factory.LastRequest?.Account);
    }

    [Fact]
    public async Task LogoutAsync_DeletesRecordForConfiguredCluster()
    {
        var store = new FakeAuthenticationRecordStore();
        var metadata = WamTestSupport.CreateMetadata();
        var key = AuthenticationRecordKey.Create(
            WamTestSupport.ClusterUrl,
            WamTestSupport.TenantId,
            WamTestSupport.Account,
            metadata.ClientId,
            metadata.Resource,
            metadata.LoginHost);
        await store.SaveAsync(key, WamTestSupport.CreateRecord(), CancellationToken.None);

        var authenticator = Create(new FakePlatform(true), new FakeMetadataProvider(metadata), store, new FakeBrokerCredentialFactory(), handle: 0);

        var deleted = await authenticator.LogoutAsync(WamTestSupport.WamCluster(), CancellationToken.None);

        Assert.True(deleted);
        Assert.Equal(1, store.DeleteCount);
    }

    [Fact]
    public async Task LogoutAsync_DoesNotRequireLiveClusterMetadata()
    {
        var store = new FakeAuthenticationRecordStore();
        var key = AuthenticationRecordKey.Create(
            WamTestSupport.ClusterUrl,
            WamTestSupport.TenantId,
            WamTestSupport.Account,
            WamTestSupport.ClientId,
            WamTestSupport.Resource,
            WamTestSupport.LoginHost);
        await store.SaveAsync(key, WamTestSupport.CreateRecord(), CancellationToken.None);
        var metadataProvider = new FakeMetadataProvider(
            _ => throw new InvalidOperationException("Logout must not fetch metadata."));
        var authenticator = Create(
            new FakePlatform(true),
            metadataProvider,
            store,
            new FakeBrokerCredentialFactory(),
            handle: 0);

        var deleted = await authenticator.LogoutAsync(
            WamTestSupport.WamCluster(),
            CancellationToken.None);

        Assert.True(deleted);
        Assert.Null(metadataProvider.LastClusterUrl);
    }

    [Fact]
    public async Task LogoutAsync_ClusterNotConfiguredForWam_Throws()
    {
        var authenticator = Create(new FakePlatform(true), new FakeMetadataProvider(), new FakeAuthenticationRecordStore(), new FakeBrokerCredentialFactory(), handle: 0);

        await Assert.ThrowsAsync<UserFacingException>(() =>
            authenticator.LogoutAsync(new ResolvedCluster("help", "https://help.kusto.windows.net"), CancellationToken.None));
    }
}
