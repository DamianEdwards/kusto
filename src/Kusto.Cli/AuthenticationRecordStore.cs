using System.Security.Cryptography;
using System.Text;
using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace Kusto.Cli;

/// <summary>
/// Identifies a persisted <see cref="AuthenticationRecord"/> by the normalized tuple that
/// binds it to a specific cluster, tenant, account, client, and resource. The filename is a
/// SHA-256 over that tuple so no raw UPN ever appears on disk. The same fields are used to
/// validate record content on both save and reuse (the hash binds the key, not the payload).
/// </summary>
internal sealed class AuthenticationRecordKey
{
    private AuthenticationRecordKey(
        string clusterUrl,
        string tenantId,
        string account,
        string clientId,
        string resource,
        string loginHost,
        string fileName)
    {
        ClusterUrl = clusterUrl;
        TenantId = tenantId;
        Account = account;
        ClientId = clientId;
        Resource = resource;
        LoginHost = loginHost;
        FileName = fileName;
    }

    public string ClusterUrl { get; }
    public string TenantId { get; }
    public string Account { get; }
    public string ClientId { get; }
    public string Resource { get; }
    public string LoginHost { get; }
    public string FileName { get; }

    public static AuthenticationRecordKey Create(
        string clusterUrl,
        string tenantId,
        string account,
        string clientId,
        string resource,
        string loginHost)
    {
        var normalizedClusterUrl = ClusterUtilities.NormalizeClusterUrl(clusterUrl).ToLowerInvariant();
        var normalizedTenant = tenantId.Trim().ToLowerInvariant();
        var normalizedAccount = account.Trim().ToLowerInvariant();
        var normalizedClientId = clientId.Trim().ToLowerInvariant();
        var normalizedResource = resource.Trim().TrimEnd('/').ToLowerInvariant();

        var material = string.Join(
            '\n',
            normalizedClusterUrl,
            normalizedTenant,
            normalizedAccount);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        var fileName = $"{Convert.ToHexStringLower(hash)}.json";

        return new AuthenticationRecordKey(
            normalizedClusterUrl,
            normalizedTenant,
            normalizedAccount,
            normalizedClientId,
            normalizedResource,
            loginHost.Trim().ToLowerInvariant(),
            fileName);
    }
}

/// <summary>
/// Persists broker <see cref="AuthenticationRecord"/> values below the effective config
/// directory in an <c>auth</c> subdirectory. Writes are atomic and path-safe, and records
/// are integrity-validated against their key before both save and reuse. Never stores tokens.
/// </summary>
internal interface IAuthenticationRecordStore
{
    Task SaveAsync(AuthenticationRecordKey key, AuthenticationRecord record, CancellationToken cancellationToken);
    Task<AuthenticationRecord?> TryLoadAsync(AuthenticationRecordKey key, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(
        string clusterUrl,
        string tenantId,
        string account,
        CancellationToken cancellationToken);
}

internal sealed class AuthenticationRecordStore(string authDirectory, ILogger logger) : IAuthenticationRecordStore
{
    private readonly string _authDirectory = Path.GetFullPath(authDirectory);
    private readonly ILogger _logger = logger;

    public async Task SaveAsync(AuthenticationRecordKey key, AuthenticationRecord record, CancellationToken cancellationToken)
    {
        if (!TryValidate(key, record, out var reason))
        {
            throw new UserFacingException($"Refusing to persist an authentication record that does not match the cluster configuration ({reason}).");
        }

        Directory.CreateDirectory(_authDirectory);
        var destinationPath = ResolveRecordPath(key);
        var tempPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";

        await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await record.SerializeAsync(stream, cancellationToken);
        }

        File.Move(tempPath, destinationPath, overwrite: true);
    }

    public async Task<AuthenticationRecord?> TryLoadAsync(AuthenticationRecordKey key, CancellationToken cancellationToken)
    {
        var recordPath = ResolveRecordPath(key);
        if (!File.Exists(recordPath))
        {
            return null;
        }

        AuthenticationRecord record;
        try
        {
            await using var stream = File.OpenRead(recordPath);
            record = await AuthenticationRecord.DeserializeAsync(stream, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or FormatException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Stored authentication record could not be read; treating it as absent.");
            return null;
        }

        if (!TryValidate(key, record, out var reason))
        {
            _logger.LogDebug("Stored authentication record failed integrity validation ({Reason}); treating it as absent.", reason);
            return null;
        }

        return record;
    }

    public Task<bool> DeleteAsync(
        string clusterUrl,
        string tenantId,
        string account,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var recordPath = ResolveRecordPath(
            AuthenticationRecordKey.Create(
                clusterUrl,
                tenantId,
                account,
                clientId: string.Empty,
                resource: string.Empty,
                loginHost: string.Empty));
        if (!File.Exists(recordPath))
        {
            return Task.FromResult(false);
        }

        File.Delete(recordPath);
        return Task.FromResult(true);
    }

    private string ResolveRecordPath(AuthenticationRecordKey key)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_authDirectory, key.FileName));
        var root = _authDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var expectedPrefix = root + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            throw new UserFacingException("Resolved authentication record path escaped the auth directory.");
        }

        return fullPath;
    }

    private static bool TryValidate(AuthenticationRecordKey key, AuthenticationRecord record, out string reason)
    {
        if (!string.Equals(record.Username?.Trim(), key.Account, StringComparison.OrdinalIgnoreCase))
        {
            reason = "account mismatch";
            return false;
        }

        if (!string.Equals(record.TenantId?.Trim(), key.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "tenant mismatch";
            return false;
        }

        if (!string.Equals(record.ClientId?.Trim(), key.ClientId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "client id mismatch";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(record.Authority) &&
            !string.Equals(record.Authority.Trim(), key.LoginHost, StringComparison.OrdinalIgnoreCase))
        {
            reason = "authority mismatch";
            return false;
        }

        if (string.IsNullOrWhiteSpace(record.HomeAccountId))
        {
            reason = "missing home account";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
