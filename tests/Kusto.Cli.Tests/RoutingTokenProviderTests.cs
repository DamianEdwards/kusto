namespace Kusto.Cli.Tests;

public sealed class RoutingTokenProviderTests
{
    [Fact]
    public async Task GetTokenAsync_DefaultCluster_UsesDefaultProviderOnly()
    {
        var defaultProvider = new RecordingTokenProvider("default-token");
        var wamProvider = new RecordingTokenProvider("wam-token");
        var router = new RoutingTokenProvider(defaultProvider, wamProvider);

        var token = await router.GetTokenAsync(new ResolvedCluster(null, "https://help.kusto.windows.net"), CancellationToken.None);

        Assert.Equal("default-token", token);
        Assert.Equal(1, defaultProvider.CallCount);
        Assert.Equal(0, wamProvider.CallCount);
    }

    [Fact]
    public async Task GetTokenAsync_WamCluster_UsesWamProviderOnly()
    {
        var defaultProvider = new RecordingTokenProvider("default-token");
        var wamProvider = new RecordingTokenProvider("wam-token");
        var router = new RoutingTokenProvider(defaultProvider, wamProvider);

        var token = await router.GetTokenAsync(WamTestSupport.WamCluster(), CancellationToken.None);

        Assert.Equal("wam-token", token);
        Assert.Equal(0, defaultProvider.CallCount);
        Assert.Equal(1, wamProvider.CallCount);
    }

    [Fact]
    public async Task GetTokenAsync_WamProviderThrows_PropagatesWithoutFallback()
    {
        var defaultProvider = new RecordingTokenProvider("default-token");
        var wamProvider = new ThrowingTokenProvider(new UserFacingException("login required"));
        var router = new RoutingTokenProvider(defaultProvider, wamProvider);

        await Assert.ThrowsAsync<UserFacingException>(() =>
            router.GetTokenAsync(WamTestSupport.WamCluster(), CancellationToken.None));

        Assert.Equal(0, defaultProvider.CallCount);
        Assert.Equal(1, wamProvider.CallCount);
    }

    [Fact]
    public async Task GetTokenAsync_UnknownAuthenticationMode_FailsWithoutCallingEitherProvider()
    {
        var defaultProvider = new RecordingTokenProvider("default-token");
        var wamProvider = new RecordingTokenProvider("wam-token");
        var router = new RoutingTokenProvider(defaultProvider, wamProvider);
        var cluster = new ResolvedCluster(
            "cross-tenant",
            WamTestSupport.ClusterUrl,
            new ClusterAuthentication { Mode = "typo" });

        await Assert.ThrowsAsync<UserFacingException>(() =>
            router.GetTokenAsync(cluster, CancellationToken.None));

        Assert.Equal(0, defaultProvider.CallCount);
        Assert.Equal(0, wamProvider.CallCount);
    }
}

public sealed class WamTokenCacheNameTests
{
    [Fact]
    public void Resolve_IsDeterministicForSameDirectory()
    {
        var a = WamTokenCacheName.Resolve(Path.Combine("C:", "Users", "someone", ".kusto"));
        var b = WamTokenCacheName.Resolve(Path.Combine("C:", "Users", "someone", ".kusto"));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Resolve_DiffersByProfileDirectory()
    {
        var a = WamTokenCacheName.Resolve(Path.Combine("C:", "Users", "someone", ".kusto"));
        var b = WamTokenCacheName.Resolve(Path.Combine("C:", "Users", "someone", ".kusto-alt"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Resolve_UsesStableProfilePrefix()
    {
        var name = WamTokenCacheName.Resolve(Path.Combine("C:", "Users", "someone", ".kusto"));

        Assert.StartsWith("kusto-cli-", name);
        Assert.DoesNotContain("someone", name);
    }
}

public sealed class JwtTokenClaimsTests
{
    [Fact]
    public void Validate_MatchingTenantAndResource_Succeeds()
    {
        var token = WamTestSupport.CreateJwt(WamTestSupport.TenantId, WamConstants.ExpectedResource);
        JwtTokenClaims.Validate(token, WamTestSupport.TenantId, WamConstants.ExpectedResource);
    }

    [Fact]
    public void Validate_ArrayAudience_UsesFirstEntry()
    {
        var token = WamTestSupport.CreateJwtWithArrayAudience(WamTestSupport.TenantId, WamConstants.ExpectedResource);
        JwtTokenClaims.Validate(token, WamTestSupport.TenantId, WamConstants.ExpectedResource);
    }

    [Fact]
    public void Validate_HostOnlyAudience_ToleratesBareHost()
    {
        var token = WamTestSupport.CreateJwt(WamTestSupport.TenantId, "https://kusto.kusto.windows.net/");
        JwtTokenClaims.Validate(token, WamTestSupport.TenantId, WamConstants.ExpectedResource);
    }

    [Fact]
    public void Validate_WrongTenant_Throws()
    {
        var token = WamTestSupport.CreateJwt("22222222-2222-2222-2222-222222222222", WamConstants.ExpectedResource);
        Assert.Throws<UserFacingException>(() =>
            JwtTokenClaims.Validate(token, WamTestSupport.TenantId, WamConstants.ExpectedResource));
    }

    [Fact]
    public void Validate_WrongAudience_Throws()
    {
        var token = WamTestSupport.CreateJwt(WamTestSupport.TenantId, "https://graph.microsoft.com");
        Assert.Throws<UserFacingException>(() =>
            JwtTokenClaims.Validate(token, WamTestSupport.TenantId, WamConstants.ExpectedResource));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    public void Validate_MalformedToken_Throws(string token)
    {
        Assert.Throws<UserFacingException>(() =>
            JwtTokenClaims.Validate(token, WamTestSupport.TenantId, WamConstants.ExpectedResource));
    }
}
