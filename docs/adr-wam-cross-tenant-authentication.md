# WAM cross-tenant authentication

`kusto` supports per-cluster authentication through Windows Account Manager
(WAM). This lets a saved cluster use a specific Windows work account and Entra
tenant instead of whichever identity `DefaultAzureCredential` would otherwise
select.

WAM authentication is opt-in for each cluster. Clusters without a WAM
configuration continue to use `DefaultAzureCredential`.

## Requirements and supported endpoints

WAM authentication requires:

- Windows.
- An interactive console session for the initial sign-in.
- A work account registered with Windows and authorized for the target Kusto
  cluster and database.
- The account's Entra tenant GUID and user principal name (UPN).
- An HTTPS Azure Public Cloud cluster whose hostname ends in
  `*.kusto.windows.net`.

Sovereign-cloud endpoints and non-Windows platforms are not supported by the
WAM authentication path.

## Configure and sign in

Configure the cluster with its tenant and work account:

```powershell
kusto cluster add cross-tenant https://cross-tenant.eastus2.kusto.windows.net `
  --auth wam `
  --tenant 11111111-1111-1111-1111-111111111111 `
  --account user@contoso.com
```

The tenant must be the Entra tenant for the configured work account. Both
`--tenant` and `--account` are required when adding a WAM cluster.

Sign in explicitly:

```powershell
kusto cluster login cross-tenant
```

`cluster login` opens the Windows account picker, using the configured account
as the login hint. The selected account and tenant must match the cluster
configuration.

The account binding can be set or changed during login:

```powershell
kusto cluster login cross-tenant `
  --tenant 22222222-2222-2222-2222-222222222222 `
  --account other-user@contoso.com
```

Successful overrides are saved in the cluster configuration for future
logins.

## Query and command authentication

After login, commands for the cluster acquire tokens silently:

```powershell
kusto query "MyTable | take 5" `
  --cluster cross-tenant `
  --database Telemetry
```

Authentication routing is based on the resolved cluster configuration:

- WAM-configured clusters use the WAM token provider.
- Clusters with no authentication descriptor, or with `mode: default`, use
  `DefaultAzureCredential`.
- WAM failures never fall back to `DefaultAzureCredential` or another account.

Query-time WAM authentication disables automatic interaction. A query never
opens sign-in UI. If the saved sign-in is missing, stale, or unusable, the
command fails with guidance to run `kusto cluster login <cluster>`.

## Cluster authentication metadata

The CLI reads the cluster's unauthenticated
`/v1/rest/auth/metadata` endpoint during login and token acquisition. It uses
the advertised Kusto public client application ID rather than a client ID
owned by the CLI.

Before using the metadata, the CLI verifies that:

- The configured cluster uses HTTPS.
- The hostname ends in `*.kusto.windows.net`.
- The metadata response is served directly by the configured cluster origin;
  redirects are not followed.
- The login endpoint is `https://login.microsoftonline.com`.
- The Kusto resource is `https://kusto.kusto.windows.net`.
- The advertised client application ID is a valid GUID.

Tokens are requested for the advertised Kusto resource using its `/.default`
scope.

## Configuration and local state

A WAM-enabled saved cluster contains an authentication descriptor:

```json
{
  "name": "cross-tenant",
  "url": "https://cross-tenant.eastus2.kusto.windows.net",
  "authentication": {
    "mode": "wam",
    "tenantId": "11111111-1111-1111-1111-111111111111",
    "account": "user@contoso.com"
  }
}
```

The descriptor contains account-selection metadata only. It does not contain
access tokens, refresh tokens, passwords, or other credentials.

WAM state is scoped to the effective CLI configuration directory:

- Broker tokens are stored by Azure Identity in an OS-protected persistent
  token cache.
- The cache name is derived from a SHA-256 hash of the configuration directory,
  so separate CLI profiles use separate caches.
- The serialized Azure Identity `AuthenticationRecord` is stored in the
  configuration directory's `auth` subdirectory.
- The record filename is a SHA-256 hash of the normalized cluster URL, tenant
  ID, and account, so the account name is not exposed in the filename.
- Authentication-record writes are atomic and constrained to the `auth`
  directory.

Before saving or reusing a record, the CLI validates its account, tenant,
client application ID, authority, and home-account identity. Acquired token
claims are also checked for the configured tenant and expected Kusto audience.

## Sign out

Remove the CLI's saved authentication record while retaining the cluster
configuration:

```powershell
kusto cluster logout cross-tenant
```

The Windows broker and OS-protected token cache may retain their own account
and token state. A later `cluster login` can therefore complete without asking
for credentials again, depending on Windows broker state and tenant policy.

## Failure behavior

- Running WAM commands on a non-Windows platform fails before broker
  initialization.
- Login requires a usable parent window handle so the account picker can be
  anchored to an interactive console.
- An account, tenant, client ID, authority, token tenant, or token audience
  mismatch is rejected.
- Missing or stale local sign-in state fails without UI and directs the user
  to run `cluster login`.
- If Kusto rejects an authenticated request, the error identifies the
  configured work account and advises verifying cluster/database access before
  signing in again.

## Native packaging

Windows Native AOT packages include the RID-specific broker runtime:

- `msalruntime.dll` for `win-x64`.
- `msalruntime_arm64.dll` for `win-arm64`.

Package validation requires the appropriate runtime in each Windows archive.
Non-Windows builds do not initialize the WAM broker.
