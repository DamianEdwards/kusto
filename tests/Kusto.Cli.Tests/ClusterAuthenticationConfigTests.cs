using System.Text.Json;

namespace Kusto.Cli.Tests;

public sealed class ClusterAuthenticationConfigTests
{
    private static KustoConfig RoundTrip(KustoConfig config)
    {
        var json = JsonSerializer.Serialize(config, KustoJsonSerializerContext.Default.KustoConfig);
        return JsonSerializer.Deserialize(json, KustoJsonSerializerContext.Default.KustoConfig)!;
    }

    [Fact]
    public void RoundTrip_WamCluster_PreservesNestedAuthentication()
    {
        var config = new KustoConfig
        {
            Clusters =
            [
                new KnownCluster
                {
                    Name = "cross-tenant",
                    Url = WamTestSupport.ClusterUrl,
                    Authentication = new ClusterAuthentication
                    {
                        Mode = ClusterAuthenticationModes.Wam,
                        TenantId = WamTestSupport.TenantId,
                        Account = WamTestSupport.Account
                    }
                }
            ]
        };

        var roundTripped = RoundTrip(config);

        var cluster = Assert.Single(roundTripped.Clusters);
        Assert.NotNull(cluster.Authentication);
        Assert.Equal(ClusterAuthenticationModes.Wam, cluster.Authentication!.Mode);
        Assert.Equal(WamTestSupport.TenantId, cluster.Authentication.TenantId);
        Assert.Equal(WamTestSupport.Account, cluster.Authentication.Account);
    }

    [Fact]
    public void Serialize_WamCluster_EmitsAuthenticationObject()
    {
        var config = new KustoConfig
        {
            Clusters =
            [
                new KnownCluster
                {
                    Name = "cross-tenant",
                    Url = WamTestSupport.ClusterUrl,
                    Authentication = new ClusterAuthentication
                    {
                        Mode = ClusterAuthenticationModes.Wam,
                        TenantId = WamTestSupport.TenantId,
                        Account = WamTestSupport.Account
                    }
                }
            ]
        };

        var json = JsonSerializer.Serialize(config, KustoJsonSerializerContext.Default.KustoConfig);

        Assert.Contains("\"authentication\"", json);
        Assert.Contains("\"mode\": \"wam\"", json);
        Assert.Contains("\"tenantId\"", json);
        Assert.Contains("\"account\"", json);
    }

    [Fact]
    public void Serialize_DefaultCluster_OmitsAuthentication()
    {
        var config = new KustoConfig
        {
            Clusters = [new KnownCluster { Name = "help", Url = "https://help.kusto.windows.net" }]
        };

        var json = JsonSerializer.Serialize(config, KustoJsonSerializerContext.Default.KustoConfig);

        Assert.DoesNotContain("\"authentication\"", json);
    }

    [Fact]
    public void AuthenticationModeDisplayName_PreservesUnknownMode()
    {
        var authentication = new ClusterAuthentication { Mode = "future-mode" };

        Assert.Equal(
            "future-mode",
            ClusterAuthenticationModes.GetDisplayName(authentication));
        Assert.Equal(
            ClusterAuthenticationModes.Default,
            ClusterAuthenticationModes.GetDisplayName(null));
    }

    [Fact]
    public void NormalizeConfig_DropsExplicitDefaultAuthentication()
    {
        var config = new KustoConfig
        {
            Clusters =
            [
                new KnownCluster
                {
                    Name = "help",
                    Url = "https://help.kusto.windows.net",
                    Authentication = new ClusterAuthentication { Mode = ClusterAuthenticationModes.Default }
                }
            ]
        };

        var normalized = ClusterUtilities.NormalizeConfig(config);

        Assert.Null(Assert.Single(normalized.Clusters).Authentication);
    }

    [Fact]
    public void NormalizeConfig_NormalizesWamAuthentication()
    {
        var config = new KustoConfig
        {
            Clusters =
            [
                new KnownCluster
                {
                    Name = "cross-tenant",
                    Url = WamTestSupport.ClusterUrl,
                    Authentication = new ClusterAuthentication
                    {
                        Mode = "WAM",
                        TenantId = $"  {WamTestSupport.TenantId}  ",
                        Account = $"  {WamTestSupport.Account}  "
                    }
                }
            ]
        };

        var normalized = ClusterUtilities.NormalizeConfig(config);
        var auth = Assert.Single(normalized.Clusters).Authentication;

        Assert.NotNull(auth);
        Assert.Equal(ClusterAuthenticationModes.Wam, auth!.Mode);
        Assert.Equal(WamTestSupport.TenantId, auth.TenantId);
        Assert.Equal(WamTestSupport.Account, auth.Account);
    }

    [Fact]
    public void NormalizeConfig_RejectsUnknownAuthenticationMode()
    {
        var config = new KustoConfig
        {
            Clusters =
            [
                new KnownCluster
                {
                    Name = "cross-tenant",
                    Url = WamTestSupport.ClusterUrl,
                    Authentication = new ClusterAuthentication { Mode = "typo" }
                }
            ]
        };

        Assert.Throws<UserFacingException>(() => ClusterUtilities.NormalizeConfig(config));
    }

    [Fact]
    public void NormalizeConfig_RejectsIncompleteWamAuthentication()
    {
        var config = new KustoConfig
        {
            Clusters =
            [
                new KnownCluster
                {
                    Name = "cross-tenant",
                    Url = WamTestSupport.ClusterUrl,
                    Authentication = new ClusterAuthentication
                    {
                        Mode = ClusterAuthenticationModes.Wam,
                        TenantId = WamTestSupport.TenantId
                    }
                }
            ]
        };

        Assert.Throws<UserFacingException>(() => ClusterUtilities.NormalizeConfig(config));
    }

    [Fact]
    public void NormalizeConfig_LoadModePreservesUnknownAuthenticationForRecovery()
    {
        var config = new KustoConfig
        {
            Clusters =
            [
                new KnownCluster
                {
                    Name = "future-cluster",
                    Url = "https://future.kusto.windows.net",
                    Authentication = new ClusterAuthentication
                    {
                        Mode = "future-mode",
                        TenantId = "future-tenant",
                        Account = "future-account"
                    }
                }
            ]
        };

        var normalized = ClusterUtilities.NormalizeConfig(
            config,
            validateAuthentication: false);
        var authentication = Assert.Single(normalized.Clusters).Authentication;

        Assert.NotNull(authentication);
        Assert.Equal("future-mode", authentication!.Mode);
        Assert.Equal("future-tenant", authentication.TenantId);
        Assert.Equal("future-account", authentication.Account);
    }

    [Fact]
    public async Task FileConfigStore_UnknownAuthenticationCanBeLoadedAndRemoved()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "kusto-config-tests",
            Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(directory, "config.json");
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(
                configPath,
                """
                {
                  "clusters": [
                    {
                      "name": "future-cluster",
                      "url": "https://future.kusto.windows.net",
                      "authentication": {
                        "mode": "future-mode",
                        "tenantId": "future-tenant",
                        "account": "future-account"
                      }
                    }
                  ]
                }
                """);
            var store = new FileConfigStore(configPath);

            var config = await store.LoadAsync(CancellationToken.None);
            config.Clusters.Clear();
            await store.SaveAsync(config, CancellationToken.None);

            var reloaded = await store.LoadAsync(CancellationToken.None);
            Assert.Empty(reloaded.Clusters);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FileConfigStore_UnknownAuthenticationSurvivesUnrelatedSave()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "kusto-config-tests",
            Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(directory, "config.json");
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(
                configPath,
                """
                {
                  "clusters": [
                    {
                      "name": "future-cluster",
                      "url": "https://future.kusto.windows.net",
                      "authentication": {
                        "mode": "future-mode",
                        "tenantId": "future-tenant",
                        "account": "future-account",
                        "futureOptions": {
                          "enabled": true,
                          "scopes": ["query", "management"]
                        }
                      }
                    }
                  ]
                }
                """);
            var store = new FileConfigStore(configPath);

            var config = await store.LoadAsync(CancellationToken.None);
            config.DefaultClusterUrl = "https://help.kusto.windows.net";
            await store.SaveAsync(config, CancellationToken.None);

            var reloaded = await store.LoadAsync(CancellationToken.None);
            var authentication = Assert.Single(reloaded.Clusters).Authentication;
            Assert.Equal(
                "https://help.kusto.windows.net",
                reloaded.DefaultClusterUrl);
            Assert.NotNull(authentication);
            Assert.Equal("future-mode", authentication!.Mode);
            Assert.Equal("future-tenant", authentication.TenantId);
            Assert.Equal("future-account", authentication.Account);
            var futureOptions = authentication.ExtensionData!["futureOptions"];
            Assert.True(futureOptions.GetProperty("enabled").GetBoolean());
            Assert.Equal(
                ["query", "management"],
                futureOptions.GetProperty("scopes")
                    .EnumerateArray()
                    .Select(value => value.GetString()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ResolveCluster_KnownWamCluster_PropagatesAuthentication()
    {
        var config = new KustoConfig
        {
            Clusters =
            [
                new KnownCluster
                {
                    Name = "cross-tenant",
                    Url = WamTestSupport.ClusterUrl,
                    Authentication = new ClusterAuthentication
                    {
                        Mode = ClusterAuthenticationModes.Wam,
                        TenantId = WamTestSupport.TenantId,
                        Account = WamTestSupport.Account
                    }
                }
            ]
        };

        var resolver = new KustoConnectionResolver();
        var resolved = resolver.ResolveCluster(config, "cross-tenant");

        Assert.True(ClusterAuthenticationModes.IsWam(resolved.Authentication));
        Assert.Equal(WamTestSupport.TenantId, resolved.Authentication.TenantId);
        Assert.Equal(WamTestSupport.Account, resolved.Authentication.Account);
    }

    [Fact]
    public void ResolveCluster_DefaultCluster_ResolvesToDefaultAuthentication()
    {
        var config = new KustoConfig
        {
            Clusters = [new KnownCluster { Name = "help", Url = "https://help.kusto.windows.net" }]
        };

        var resolver = new KustoConnectionResolver();
        var resolved = resolver.ResolveCluster(config, "help");

        Assert.False(ClusterAuthenticationModes.IsWam(resolved.Authentication));
        Assert.Equal(ClusterAuthenticationModes.Default, resolved.Authentication.Mode);
    }

    [Fact]
    public void ResolveClusterForUrl_KnownWamCluster_PropagatesAuthentication()
    {
        var config = new KustoConfig
        {
            Clusters =
            [
                new KnownCluster
                {
                    Name = "cross-tenant",
                    Url = WamTestSupport.ClusterUrl,
                    Authentication = new ClusterAuthentication
                    {
                        Mode = ClusterAuthenticationModes.Wam,
                        TenantId = WamTestSupport.TenantId,
                        Account = WamTestSupport.Account
                    }
                }
            ]
        };

        var resolved = ClusterUtilities.ResolveClusterForUrl(config, WamTestSupport.ClusterUrl);

        Assert.True(ClusterAuthenticationModes.IsWam(resolved.Authentication));
        Assert.Equal("cross-tenant", resolved.Name);
    }
}
