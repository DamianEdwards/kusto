using System.Runtime.InteropServices;

namespace Kusto.Cli.Tests;

public sealed class SchemaCacheSettingsResolverTests
{
    [Fact]
    public void Resolve_IsEnabledByDefault()
    {
        var resolver = new SchemaCacheSettingsResolver(
            getEnvironmentVariable: _ => null,
            getFolderPath: folder => folder switch
            {
                Environment.SpecialFolder.LocalApplicationData => @"C:\Users\me\AppData\Local",
                Environment.SpecialFolder.UserProfile => @"C:\Users\me",
                _ => string.Empty
            },
            getUserHomeDirectory: () => @"C:\Users\me",
            isOSPlatform: platform => platform == OSPlatform.Windows);

        var settings = resolver.Resolve(
            new KustoConfig(),
            "https://help.kusto.windows.net",
            "Samples");

        Assert.True(settings.Enabled);
        Assert.Equal(Path.GetFullPath(@"C:\Users\me\AppData\Local\kusto\schema-cache"), settings.CacheDirectory);
    }

    [Fact]
    public void Resolve_UsesWindowsLocalApplicationDataByDefault()
    {
        var resolver = new SchemaCacheSettingsResolver(
            getEnvironmentVariable: _ => null,
            getFolderPath: folder => folder switch
            {
                Environment.SpecialFolder.LocalApplicationData => @"C:\Users\me\AppData\Local",
                Environment.SpecialFolder.UserProfile => @"C:\Users\me",
                _ => string.Empty
            },
            getUserHomeDirectory: () => @"C:\Users\me",
            isOSPlatform: platform => platform == OSPlatform.Windows);

        var settings = resolver.Resolve(
            new KustoConfig
            {
                SchemaCache = new SchemaCacheConfig
                {
                    Enabled = true
                }
            },
            "https://help.kusto.windows.net",
            "Samples");

        Assert.True(settings.Enabled);
        Assert.Equal(Path.GetFullPath(@"C:\Users\me\AppData\Local\kusto\schema-cache"), settings.CacheDirectory);
        Assert.Equal(TimeSpan.FromSeconds(SchemaCacheSettingsResolver.DefaultTtlSeconds), settings.Ttl);
    }

    [Fact]
    public void Resolve_UsesXdgCacheHomeOnLinux()
    {
        var resolver = new SchemaCacheSettingsResolver(
            getEnvironmentVariable: name => name == "XDG_CACHE_HOME" ? @"C:\xdg-cache" : null,
            getFolderPath: _ => string.Empty,
            getUserHomeDirectory: () => @"C:\Users\me",
            isOSPlatform: _ => false);

        var settings = resolver.Resolve(
            new KustoConfig
            {
                SchemaCache = new SchemaCacheConfig
                {
                    Enabled = true
                }
            },
            "https://help.kusto.windows.net",
            "Samples");

        Assert.Equal(Path.GetFullPath(@"C:\xdg-cache\kusto\schema-cache"), settings.CacheDirectory);
    }

    [Fact]
    public void Resolve_UsesMatchingClusterDatabaseOverride()
    {
        var resolver = new SchemaCacheSettingsResolver(
            getEnvironmentVariable: _ => null,
            getFolderPath: _ => string.Empty,
            getUserHomeDirectory: () => @"C:\Users\me",
            isOSPlatform: _ => false);

        var settings = resolver.Resolve(
            new KustoConfig
            {
                SchemaCache = new SchemaCacheConfig
                {
                    Enabled = true,
                    TtlSeconds = 60,
                    Overrides =
                    [
                        new SchemaCacheOverride
                        {
                            ClusterUrl = "https://help.kusto.windows.net/",
                            Database = "Samples",
                            TtlSeconds = 600
                        }
                    ]
                }
            },
            "https://help.kusto.windows.net",
            "Samples");

        Assert.Equal(TimeSpan.FromSeconds(600), settings.Ttl);
    }

    [Fact]
    public void Resolve_UsesEnvironmentOverrides()
    {
        var environmentValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [SchemaCacheSettingsResolver.CacheEnabledEnvironmentVariable] = "true",
            [SchemaCacheSettingsResolver.CachePathEnvironmentVariable] = @"C:\cache\schemas",
            [SchemaCacheSettingsResolver.CacheTtlEnvironmentVariable] = "120"
        };
        var resolver = new SchemaCacheSettingsResolver(
            getEnvironmentVariable: name => environmentValues.TryGetValue(name, out var value) ? value : null,
            getFolderPath: _ => string.Empty,
            getUserHomeDirectory: () => @"C:\Users\me",
            isOSPlatform: platform => platform == OSPlatform.Windows);

        var settings = resolver.Resolve(
            new KustoConfig
            {
                SchemaCache = new SchemaCacheConfig
                {
                    Enabled = false,
                    Path = @"C:\ignored",
                    TtlSeconds = 30
                }
            },
            "https://help.kusto.windows.net",
            "Samples");

        Assert.True(settings.Enabled);
        Assert.Equal(Path.GetFullPath(@"C:\cache\schemas"), settings.CacheDirectory);
        Assert.Equal(TimeSpan.FromSeconds(120), settings.Ttl);
    }

    [Fact]
    public void Resolve_WhenDisabled_IgnoresInvalidEnvironmentOverrides()
    {
        var environmentValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [SchemaCacheSettingsResolver.CacheEnabledEnvironmentVariable] = "false",
            [SchemaCacheSettingsResolver.CachePathEnvironmentVariable] = "\"",
            [SchemaCacheSettingsResolver.CacheTtlEnvironmentVariable] = "not-a-number"
        };
        var resolver = new SchemaCacheSettingsResolver(
            getEnvironmentVariable: name => environmentValues.TryGetValue(name, out var value) ? value : null,
            getFolderPath: _ => string.Empty,
            getUserHomeDirectory: () => @"C:\Users\me",
            isOSPlatform: platform => platform == OSPlatform.Windows);

        var settings = resolver.Resolve(
            new KustoConfig(),
            "https://help.kusto.windows.net",
            "Samples");

        Assert.False(settings.Enabled);
        Assert.Equal(string.Empty, settings.CacheDirectory);
        Assert.Equal(TimeSpan.Zero, settings.Ttl);
    }
}
