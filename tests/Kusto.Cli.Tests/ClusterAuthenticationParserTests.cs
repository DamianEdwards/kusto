namespace Kusto.Cli.Tests;

public sealed class ClusterAuthenticationParserTests
{
    [Fact]
    public void ParseForAdd_DefaultMode_ReturnsNull()
    {
        Assert.Null(ClusterAuthenticationParser.ParseForAdd(null, null, null));
        Assert.Null(ClusterAuthenticationParser.ParseForAdd("default", null, null));
        Assert.Null(ClusterAuthenticationParser.ParseForAdd("  DEFAULT  ", null, null));
    }

    [Fact]
    public void ParseForAdd_DefaultModeWithTenantOrAccount_Throws()
    {
        Assert.Throws<UserFacingException>(() => ClusterAuthenticationParser.ParseForAdd("default", WamTestSupport.TenantId, null));
        Assert.Throws<UserFacingException>(() => ClusterAuthenticationParser.ParseForAdd(null, null, WamTestSupport.Account));
    }

    [Fact]
    public void ParseForAdd_Wam_ReturnsNormalizedDescriptor()
    {
        var result = ClusterAuthenticationParser.ParseForAdd("WAM", $"  {WamTestSupport.TenantId}  ", $"  {WamTestSupport.Account}  ");

        Assert.NotNull(result);
        Assert.Equal(ClusterAuthenticationModes.Wam, result!.Mode);
        Assert.Equal(WamTestSupport.TenantId, result.TenantId);
        Assert.Equal(WamTestSupport.Account, result.Account);
    }

    [Fact]
    public void ParseForAdd_WamMissingTenantOrAccount_Throws()
    {
        Assert.Throws<UserFacingException>(() => ClusterAuthenticationParser.ParseForAdd("wam", null, WamTestSupport.Account));
        Assert.Throws<UserFacingException>(() => ClusterAuthenticationParser.ParseForAdd("wam", WamTestSupport.TenantId, null));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("contoso.onmicrosoft.com")]
    public void ParseForAdd_WamNonGuidTenant_Throws(string tenant)
    {
        Assert.Throws<UserFacingException>(() => ClusterAuthenticationParser.ParseForAdd("wam", tenant, WamTestSupport.Account));
    }

    [Theory]
    [InlineData("noatsign")]
    [InlineData("two@at@signs.com")]
    [InlineData("@leading.com")]
    [InlineData("trailing@")]
    public void ParseForAdd_WamInvalidAccount_Throws(string account)
    {
        Assert.Throws<UserFacingException>(() => ClusterAuthenticationParser.ParseForAdd("wam", WamTestSupport.TenantId, account));
    }

    [Fact]
    public void ParseForAdd_UnknownMode_Throws()
    {
        Assert.Throws<UserFacingException>(() => ClusterAuthenticationParser.ParseForAdd("saml", WamTestSupport.TenantId, WamTestSupport.Account));
    }

    [Fact]
    public void ResolveForLogin_UsesExistingWhenNoOverrides()
    {
        var existing = new ClusterAuthentication
        {
            Mode = ClusterAuthenticationModes.Wam,
            TenantId = WamTestSupport.TenantId,
            Account = WamTestSupport.Account
        };

        var result = ClusterAuthenticationParser.ResolveForLogin(existing, null, null);

        Assert.Equal(ClusterAuthenticationModes.Wam, result.Mode);
        Assert.Equal(WamTestSupport.TenantId, result.TenantId);
        Assert.Equal(WamTestSupport.Account, result.Account);
    }

    [Fact]
    public void ResolveForLogin_OverridesLayerOverExisting()
    {
        var existing = new ClusterAuthentication
        {
            Mode = ClusterAuthenticationModes.Wam,
            TenantId = WamTestSupport.TenantId,
            Account = "old@contoso.com"
        };

        const string newTenant = "11111111-1111-1111-1111-111111111111";
        var result = ClusterAuthenticationParser.ResolveForLogin(existing, newTenant, "new@contoso.com");

        Assert.Equal(newTenant, result.TenantId);
        Assert.Equal("new@contoso.com", result.Account);
    }

    [Fact]
    public void ResolveForLogin_NoExistingAndNoOverrides_Throws()
    {
        Assert.Throws<UserFacingException>(() => ClusterAuthenticationParser.ResolveForLogin(null, null, null));
    }

    [Fact]
    public void ResolveForLogin_DefaultExistingWithoutOverrides_Throws()
    {
        var existingDefault = new ClusterAuthentication { Mode = ClusterAuthenticationModes.Default };
        Assert.Throws<UserFacingException>(() => ClusterAuthenticationParser.ResolveForLogin(existingDefault, null, null));
    }

    [Fact]
    public void ResolveForLogin_InvalidOverride_Throws()
    {
        Assert.Throws<UserFacingException>(() =>
            ClusterAuthenticationParser.ResolveForLogin(null, "bad-tenant", WamTestSupport.Account));
    }
}
