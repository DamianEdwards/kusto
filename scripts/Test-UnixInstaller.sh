#!/usr/bin/env bash

set -euo pipefail

ROOT_PATH=$(cd "$(dirname "$0")/.." && pwd)
INSTALLER_PATH="${ROOT_PATH}/scripts/install/install-kusto-cli.sh"

# Load installer helpers without performing an installation.
. "$INSTALLER_PATH" --no-execute

fail() {
    echo "Error: $*" >&2
    exit 1
}

assert_contains() {
    local value="$1" expected="$2"
    [[ "$value" == *"$expected"* ]] ||
        fail "Expected '$value' to contain '$expected'."
}

temp_root=$(mktemp -d "${TMPDIR:-/tmp}/kusto-unix-installer-test-XXXXXX")
trap 'rm -rf "$temp_root"' EXIT
mock_bin="${temp_root}/bin"
empty_bin="${temp_root}/empty"
mkdir -p "$mock_bin" "$empty_bin"

cat > "${mock_bin}/cosign" <<'MOCK'
#!/usr/bin/env bash
printf 'GitVersion: v%s\n' "${MOCK_COSIGN_VERSION}"
MOCK
chmod +x "${mock_bin}/cosign"

if output=$(
    PATH="${empty_bin}:/usr/bin:/bin"
    export PATH
    assert_cosign_available 2>&1
); then
    fail "A missing cosign command should fail validation."
fi
assert_contains "$output" "cosign ${MINIMUM_COSIGN_VERSION} or newer is required"

if output=$(
    PATH="${mock_bin}:/usr/bin:/bin"
    MOCK_COSIGN_VERSION="2.3.0"
    export PATH MOCK_COSIGN_VERSION
    assert_cosign_available 2>&1
); then
    fail "An outdated cosign command should fail validation."
fi
assert_contains "$output" "cosign ${MINIMUM_COSIGN_VERSION} or newer is required, but 2.3.0 was found"

saved_path="$PATH"
PATH="${mock_bin}:/usr/bin:/bin"
export PATH

MOCK_COSIGN_VERSION="2.4.0"
export MOCK_COSIGN_VERSION
assert_cosign_available
[[ "$COSIGN_VERSION" == "2.4.0" ]] ||
    fail "Expected cosign 2.4.0 to pass validation."
[[ " ${COSIGN_VERIFY_ARGS[*]} " == *" --new-bundle-format "* ]] ||
    fail "Cosign 2.x must use --new-bundle-format."
[[ " ${COSIGN_VERIFY_ARGS[*]} " == *" --type slsaprovenance1 "* ]] ||
    fail "Cosign 2.x must select the SLSA v1 predicate."

MOCK_COSIGN_VERSION="3.1.3"
export MOCK_COSIGN_VERSION
assert_cosign_available
[[ "$COSIGN_VERSION" == "3.1.3" ]] ||
    fail "Expected cosign 3.1.3 to pass validation."
[[ " ${COSIGN_VERIFY_ARGS[*]} " != *" --new-bundle-format "* ]] ||
    fail "Cosign 3.x must not use the deprecated --new-bundle-format flag."
[[ " ${COSIGN_VERIFY_ARGS[*]} " == *" --type slsaprovenance1 "* ]] ||
    fail "Cosign 3.x must select the SLSA v1 predicate."

[[ "$(get_azure_cli_install_url osx)" == "https://learn.microsoft.com/cli/azure/install-azure-cli-macos" ]] ||
    fail "The macOS Azure CLI documentation URL is incorrect."
[[ "$(get_azure_cli_install_url linux)" == "https://learn.microsoft.com/cli/azure/install-azure-cli-linux" ]] ||
    fail "The Linux Azure CLI documentation URL is incorrect."

cat > "${mock_bin}/az" <<'MOCK'
#!/usr/bin/env bash
exit 0
MOCK
chmod +x "${mock_bin}/az"

PATH="$mock_bin"
[[ -z "$(print_azure_cli_guidance osx)" ]] ||
    fail "Azure CLI guidance should not be printed when az is available."

PATH="$empty_bin"
guidance=$(print_azure_cli_guidance linux)
assert_contains "$guidance" "Azure CLI was not found."
assert_contains "$guidance" "https://learn.microsoft.com/cli/azure/install-azure-cli-linux"

PATH="$saved_path"
export PATH

valid_payload=$(printf '%s' '{"_type":"https://in-toto.io/Statement/v1","predicateType":"https://slsa.dev/provenance/v1"}' |
    base64 |
    tr -d '\r\n')
invalid_payload=$(printf '%s' '{"_type":"https://in-toto.io/Statement/v1","predicateType":"https://example.com/custom"}' |
    base64 |
    tr -d '\r\n')
printf '{"dsseEnvelope":{"payload":"%s"}}\n' "$valid_payload" > "${temp_root}/valid-bundle.json"
printf '{"dsseEnvelope":{"payload":"%s"}}\n' "$invalid_payload" > "${temp_root}/invalid-bundle.json"

is_slsa_v1_provenance_bundle "${temp_root}/valid-bundle.json" ||
    fail "A signed SLSA v1 predicate should be accepted."
if is_slsa_v1_provenance_bundle "${temp_root}/invalid-bundle.json"; then
    fail "A non-SLSA predicate should be rejected."
fi

echo "Unix installer validation passed."
