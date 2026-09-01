using System.Text;
using Azure.Core;
using Azure.Identity;

namespace Kusto.Cli.Tests;

/// <summary>
/// Shared, UI-free doubles and factory helpers for the WAM authentication tests.
/// None of these touch the real broker, network, or file-backed token cache.
/// </summary>
internal static class WamTestSupport
{
    public const string ClientId = "db662dc1-0cfe-4e1c-a843-19a68e65be58";
    public const string TenantId = "11111111-1111-1111-1111-111111111111";
    public const string Account = "user@contoso.com";
    public const string LoginHost = "login.microsoftonline.com";
    public const string Resource = WamConstants.ExpectedResource;
    public const string ClusterUrl = "https://cross-tenant.eastus2.kusto.windows.net";

    public static AuthenticationRecord CreateRecord(
        string username = Account,
        string tenantId = TenantId,
        string clientId = ClientId,
        string authority = LoginHost,
        string homeAccountId = "00000000-0000-0000-0000-000000000000.11111111-1111-1111-1111-111111111111")
    {
        var json =
            $$"""
            {"username":"{{username}}","authority":"{{authority}}","homeAccountId":"{{homeAccountId}}","tenantId":"{{tenantId}}","clientId":"{{clientId}}","version":"1.0"}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return AuthenticationRecord.Deserialize(stream);
    }

    public static KustoAuthMetadataResult CreateMetadata(
        string clientId = ClientId,
        string resource = Resource,
        string loginHost = LoginHost) => new()
        {
            ClientId = clientId,
            Resource = resource,
            LoginEndpoint = WamConstants.ExpectedLoginEndpoint,
            LoginHost = loginHost,
            AuthorityHost = AzureAuthorityHosts.AzurePublicCloud
        };

    public static ResolvedCluster WamCluster(
        string? name = "cross-tenant",
        string url = ClusterUrl,
        string tenantId = TenantId,
        string account = Account) => new(
            name,
            url,
            new ClusterAuthentication { Mode = ClusterAuthenticationModes.Wam, TenantId = tenantId, Account = account });

    public static string CreateJwt(string tid, string aud)
    {
        static string Encode(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        var header = Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var payload = Encode($"{{\"tid\":\"{tid}\",\"aud\":\"{aud}\"}}");
        return $"{header}.{payload}.";
    }

    public static string CreateJwtWithArrayAudience(string tid, string aud)
    {
        static string Encode(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        var header = Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var payload = Encode($"{{\"tid\":\"{tid}\",\"aud\":[\"{aud}\",\"urn:extra\"]}}");
        return $"{header}.{payload}.";
    }
}

internal sealed class FakePlatform(bool isWindows) : IPlatform
{
    public bool IsWindows { get; } = isWindows;
}

internal sealed class FakeWindowHandleProvider(nint handle) : IWindowHandleProvider
{
    public nint GetParentWindowHandle() => handle;
}

internal sealed class FakeMetadataProvider : IKustoAuthMetadataProvider
{
    private readonly Func<string, KustoAuthMetadataResult> _factory;

    public FakeMetadataProvider(KustoAuthMetadataResult? result = null)
        => _factory = _ => result ?? WamTestSupport.CreateMetadata();

    public FakeMetadataProvider(Func<string, KustoAuthMetadataResult> factory)
        => _factory = factory;

    public string? LastClusterUrl { get; private set; }

    public Task<KustoAuthMetadataResult> GetAsync(string clusterUrl, CancellationToken cancellationToken)
    {
        LastClusterUrl = clusterUrl;
        return Task.FromResult(_factory(clusterUrl));
    }
}

internal sealed class FakeAuthenticationRecordStore : IAuthenticationRecordStore
{
    private readonly Dictionary<string, AuthenticationRecord> _records = new(StringComparer.Ordinal);

    public AuthenticationRecord? RecordToReturn { get; set; }
    public bool ThrowOnSave { get; set; }
    public int SaveCount { get; private set; }
    public int DeleteCount { get; private set; }
    public AuthenticationRecordKey? LastSavedKey { get; private set; }
    public AuthenticationRecordKey? LastDeletedKey { get; private set; }

    public Task SaveAsync(AuthenticationRecordKey key, AuthenticationRecord record, CancellationToken cancellationToken)
    {
        if (ThrowOnSave)
        {
            throw new UserFacingException("save rejected");
        }

        SaveCount++;
        LastSavedKey = key;
        _records[key.FileName] = record;
        return Task.CompletedTask;
    }

    public Task<AuthenticationRecord?> TryLoadAsync(AuthenticationRecordKey key, CancellationToken cancellationToken)
    {
        if (RecordToReturn is not null)
        {
            return Task.FromResult<AuthenticationRecord?>(RecordToReturn);
        }

        return Task.FromResult(_records.TryGetValue(key.FileName, out var record) ? record : null);
    }

    public Task<bool> DeleteAsync(
        string clusterUrl,
        string tenantId,
        string account,
        CancellationToken cancellationToken)
    {
        DeleteCount++;
        LastDeletedKey = AuthenticationRecordKey.Create(
            clusterUrl,
            tenantId,
            account,
            WamTestSupport.ClientId,
            WamTestSupport.Resource,
            WamTestSupport.LoginHost);
        var key = LastDeletedKey;
        return Task.FromResult(_records.Remove(key.FileName));
    }
}

internal sealed class FakeBrokerCredentialFactory : IWamBrokerCredentialFactory
{
    public Func<WamCredentialRequest, AccessToken>? OnGetToken { get; set; }
    public Func<WamCredentialRequest, AuthenticationRecord>? OnAuthenticate { get; set; }
    public WamCredentialRequest? LastRequest { get; private set; }
    public int CreateCount { get; private set; }

    public IWamBrokerCredential Create(WamCredentialRequest request)
    {
        CreateCount++;
        LastRequest = request;
        return new FakeCredential(request, OnGetToken, OnAuthenticate);
    }

    private sealed class FakeCredential(
        WamCredentialRequest request,
        Func<WamCredentialRequest, AccessToken>? onGetToken,
        Func<WamCredentialRequest, AuthenticationRecord>? onAuthenticate) : IWamBrokerCredential
    {
        public Task<AuthenticationRecord> AuthenticateAsync(TokenRequestContext context, CancellationToken cancellationToken)
        {
            if (onAuthenticate is null)
            {
                throw new InvalidOperationException("AuthenticateAsync was not expected.");
            }

            return Task.FromResult(onAuthenticate(request));
        }

        public ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken)
        {
            if (onGetToken is null)
            {
                throw new InvalidOperationException("GetTokenAsync was not expected.");
            }

            return new ValueTask<AccessToken>(onGetToken(request));
        }
    }
}

internal sealed class RecordingTokenProvider(string token) : ITokenProvider
{
    public int CallCount { get; private set; }
    public ResolvedCluster? LastCluster { get; private set; }

    public Task<string> GetTokenAsync(ResolvedCluster cluster, CancellationToken cancellationToken)
    {
        CallCount++;
        LastCluster = cluster;
        return Task.FromResult(token);
    }
}

internal sealed class ThrowingTokenProvider(Exception exception) : ITokenProvider
{
    public int CallCount { get; private set; }

    public Task<string> GetTokenAsync(ResolvedCluster cluster, CancellationToken cancellationToken)
    {
        CallCount++;
        throw exception;
    }
}
