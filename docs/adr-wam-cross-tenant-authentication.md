# ADR: Per-cluster WAM authentication

Status: Accepted

## Context

`kusto` currently acquires every token through `DefaultAzureCredential`. In a
multi-account Windows session this can silently select the active Azure CLI
identity, even when a saved cluster is accessible only through a different
work account.

The motivating case is:

- Cluster: `https://cross-tenant.eastus2.kusto.windows.net`
- Database: `Telemetry`
- Account: `user@contoso.com`
- Tenant: `11111111-1111-1111-1111-111111111111`

The account is registered with Windows, but the current CLI authenticated as
a different active Azure CLI account and received HTTP 403.

A working spike established that:

1. The cluster's unauthenticated auth metadata advertises its Kusto public
   client ID and token resource.
2. `Azure.Identity.Broker` can open the Windows account picker and acquire a
   token for the configured secondary work account.
3. `TokenCachePersistenceOptions` plus a serialized `AuthenticationRecord`
   allow a fresh process to reuse that account silently.
4. The resulting token successfully queries the target database.
5. Self-contained `win-arm64` and `win-x64` Native AOT builds succeed with
   warnings treated as errors. They package the required broker runtimes and
   perform the same silent query successfully.

## Decision

Add an explicit per-cluster WAM authentication mode while preserving
`DefaultAzureCredential` as the default.

Each saved cluster may optionally contain:

```json
{
  "authentication": {
    "mode": "wam",
    "tenantId": "11111111-1111-1111-1111-111111111111",
    "account": "user@contoso.com"
  }
}
```

WAM-enabled clusters use this flow:

1. `kusto cluster login <cluster> --tenant <id> --account <upn>` explicitly
   bootstraps the account through the native Windows account picker.
2. The CLI reads `/v1/rest/auth/metadata` from the target cluster and uses the
   advertised Kusto client ID and resource scope.
3. Azure Identity stores tokens in its OS-protected persistent cache.
4. The CLI serializes the non-secret `AuthenticationRecord` under the user's
   `.kusto` directory so future processes select the exact account.
5. Query commands disable automatic interaction and acquire tokens silently.

If silent WAM acquisition fails, the command fails with instructions to rerun
`cluster login`. It must not fall back to Azure CLI or another credential.

Clusters without WAM configuration retain the existing
`DefaultAzureCredential` behavior.

### Runtime seam

`ResolvedCluster` will carry the saved cluster's authentication descriptor.
`ITokenProvider` and `IKustoService` will receive that resolved context rather
than only a URL string. This makes the authentication choice explicit and
prevents a failed config lookup from silently reverting to default auth.

The composition root constructs one routing token provider. Each request
passes a `ResolvedCluster`; the router delegates to separate WAM and default
provider implementations. The WAM implementation is not a
`ChainedTokenCredential` and never invokes `DefaultAzureCredential`.

### Kusto auth metadata

WAM mode initially supports Windows Azure Public Cloud clusters on HTTPS
`*.kusto.windows.net` endpoints.

The login flow reads the cluster's `/v1/rest/auth/metadata` endpoint and
validates:

- the login endpoint matches the Azure Public Cloud authority;
- the service resource is `https://kusto.kusto.windows.net`;
- the advertised client ID is a valid GUID;
- the request remains on the normalized configured cluster origin.

The advertised Kusto client ID is used instead of coupling the CLI to the
Azure CLI application ID.

### Local account state

Authentication records are stored under the config directory's `auth`
subdirectory. Filenames are SHA-256 hashes of the normalized cluster URL,
tenant ID, configured account, client ID, and resource; raw account names are
not used in paths.

The record is treated as integrity-sensitive even though it is not a secret.
Writes are atomic, paths must remain within the intended auth directory, and
the saved record is validated against the configured tenant/account/client
before every use.

The Azure Identity token cache uses a deterministic profile-specific name and
platform-protected persistence. `cluster logout <cluster>` deletes the CLI's
authentication record. The broker or operating system may retain its own
account/token state.

## Security properties

- Access and refresh tokens may be persisted only by Azure Identity's
  platform-protected token cache. They are never written to CLI config,
  authentication-record files, logs, command output, or unprotected storage.
- The serialized authentication record contains account-selection metadata,
  not tokens.
- The returned tenant, username, home account ID, authority, and client ID are
  validated before saving and before silent reuse.
- Token claims are checked locally for the configured tenant and expected
  Kusto audience without logging the token.
- A WAM-configured cluster never falls back to another identity.
- Interactive authentication occurs only through the explicit `cluster login`
  command, never unexpectedly during a query.
- WAM-specific failures instruct users to run `cluster login`; they never
  recommend `az login`.

## Platform behavior

WAM mode is Windows-only. On macOS and Linux, login or use returns an
actionable unsupported-platform error before broker initialization. Existing
authentication remains cross-platform.

The Native AOT release matrix must continue to publish successfully. Windows
release assets must require the RID-specific broker runtime
(`msalruntime.dll` for x64 or `msalruntime_arm64.dll` for arm64) in package
validation.
Non-Windows assets must remain functional and must not initialize WAM.

The accepted package combination is `Azure.Identity` 1.18.0 with
`Azure.Identity.Broker` 1.3.1. Both Windows RIDs were published with
`TreatWarningsAsErrors=true`; no broker trim/AOT warnings were emitted.

The broker account picker uses the console window handle when present, then
the foreground window as a fallback. Native calls use source-generated
`LibraryImport`.

## Alternatives considered

### Continue using `DefaultAzureCredential`

Rejected. It selected the active Azure CLI account and cannot reliably bind a
saved cluster to a secondary Windows account.

### Invoke the personal `WamToken.exe` helper

Rejected as the product design. It is not part of the CLI distribution, would
introduce an external token handoff, and its original
`OperatingSystemAccount` flow cannot select a secondary account.

### Use direct MSAL broker APIs

Viable, and proven by the first spike, but it requires the CLI to implement
account-record and token-cache persistence itself. `Azure.Identity.Broker`
provides the same broker support through the existing `TokenCredential`
abstraction and has a supported authentication-record pattern.

## Consequences

- Saved cluster configuration gains optional authentication metadata.
- The runtime gains explicit cluster-authentication and authentication-record
  services.
- Resolved connection context, service calls, and token acquisition carry the
  configured auth descriptor.
- The CLI gains `cluster login` and `cluster logout` commands.
- A small amount of account metadata is stored locally.
- Source-generated JSON metadata must include the new config and auth-metadata
  types.
- Tests need seams for auth metadata, broker credentials, token claims, record
  storage, platform detection, and window-handle selection.

## Rollout

1. Implement and test WAM support in `kusto`.
2. Validate Windows x64 and arm64 Native AOT publication, package contents,
   and live cross-tenant cluster access.
3. Confirm existing default-auth clusters are unchanged.
4. Add optional tenant/account metadata to KustoMagic datasource definitions
   and route those datasources through the configured `kusto` CLI identity.

## Acceptance criteria

After one native WAM account selection for `user@contoso.com`,
`kusto.exe` can query the configured cross-tenant cluster silently from a fresh process
without `az login`. Tokens are persisted only in the platform-protected Azure
Identity cache and are never placed in CLI-owned records, config, logs, or
output. WAM clusters never fall back to another identity, and existing
default-auth clusters continue to work unchanged. Missing or stale WAM state
fails without UI and directs the user to `cluster login`. Windows Native AOT
packages contain the broker runtime, and non-Windows builds remain valid.
