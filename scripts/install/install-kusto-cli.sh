#!/usr/bin/env bash
# Linux and macOS installer for kusto.
# curl -fsSL https://raw.githubusercontent.com/DamianEdwards/kusto-cli/install-scripts/install.sh | bash

set -euo pipefail

QUALITY=Stable
FORCE=false
TARGET_PATH="${HOME}/.kusto/bin"
UPDATE_PATH=true
REPOSITORY="DamianEdwards/kusto-cli"
SKIP_PROVENANCE=false
VERBOSE=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        --quality)
            [[ $# -ge 2 ]] || { echo "Error: --quality requires a value." >&2; exit 1; }
            QUALITY="$2"; shift 2 ;;
        --force) FORCE=true; shift ;;
        --target-path)
            [[ $# -ge 2 ]] || { echo "Error: --target-path requires a value." >&2; exit 1; }
            TARGET_PATH="$2"; shift 2 ;;
        --no-update-path) UPDATE_PATH=false; shift ;;
        --repository)
            [[ $# -ge 2 ]] || { echo "Error: --repository requires a value." >&2; exit 1; }
            REPOSITORY="$2"; shift 2 ;;
        --skip-provenance) SKIP_PROVENANCE=true; shift ;;
        --verbose) VERBOSE=true; shift ;;
        -h|--help)
            cat <<'HELP'
install-kusto-cli.sh — Linux and macOS installer for kusto

Options:
  --quality <Dev|PreRelease|Stable>  Release quality (default: Stable)
  --force                            Skip the Dev confirmation prompt
  --target-path <path>               Install directory (default: ~/.kusto/bin)
  --no-update-path                   Do not update the shell profile PATH
  --repository <owner/repo>          GitHub repository
  --skip-provenance                  Explicitly skip archive attestation verification
  --verbose                          Enable verbose diagnostics
HELP
            exit 0 ;;
        *) echo "Error: Unknown option '$1'." >&2; exit 1 ;;
    esac
done

case "$QUALITY" in
    Dev|PreRelease|Stable) ;;
    *) echo "Error: --quality must be Dev, PreRelease, or Stable." >&2; exit 1 ;;
esac

log_verbose() {
    if [[ "$VERBOSE" == true ]]; then echo "[verbose] $*" >&2; fi
}

die() {
    echo "Error: $*" >&2
    exit 1
}

status_step() {
    local message="$1"
    shift
    printf '%s... ' "$message"
    "$@"
    echo "done"
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || die "'$1' is required but was not found on PATH."
}

print_cosign_install_instructions() {
    if [[ "$(uname -s)" == Darwin ]] && command -v brew >/dev/null 2>&1; then
        echo "Install cosign with: brew install cosign" >&2
    else
        echo "Install cosign from https://docs.sigstore.dev/cosign/system_config/installation/" >&2
    fi
    echo "Or explicitly rerun with --skip-provenance." >&2
}

assert_cosign_available() {
    if ! command -v cosign >/dev/null 2>&1; then
        print_cosign_install_instructions
        die "cosign is required for artifact attestation verification."
    fi
}

invoke_github_api() {
    local uri="$1"
    local response_file
    response_file=$(mktemp)
    local token="${GITHUB_TOKEN:-${GH_TOKEN:-}}"
    local curl_args=(-sS -L -H "Accept: application/vnd.github+json" -H "X-GitHub-Api-Version: 2022-11-28" -A "kusto-cli-install-script")
    if [[ -n "$token" ]]; then curl_args+=(-H "Authorization: Bearer ${token}"); fi

    local status
    status=$(curl "${curl_args[@]}" -o "$response_file" -w "%{http_code}" "$uri") || {
        local code=$?
        rm -f "$response_file"
        die "GitHub API request failed for '$uri' (curl exit ${code})."
    }
    local body
    body=$(cat "$response_file")
    rm -f "$response_file"
    if [[ "$status" == 404 ]]; then echo ""; return; fi
    [[ "$status" =~ ^2[0-9][0-9]$ ]] || die "GitHub API request failed for '$uri' (HTTP ${status}): ${body}"
    echo "$body"
}

download_asset() {
    local url="$1"
    local output="$2"
    local token="${GITHUB_TOKEN:-${GH_TOKEN:-}}"
    local curl_args=(-fsSL -A "kusto-cli-install-script")
    if [[ -n "$token" ]]; then curl_args+=(-H "Authorization: Bearer ${token}"); fi
    curl "${curl_args[@]}" -o "$output" "$url" || die "Failed to download '$(basename "$output")'."
}

release_asset_url() {
    echo "$1" | jq -r --arg name "$2" '.assets[] | select(.name == $name) | .browser_download_url // empty' | head -n 1
}

has_release_asset() {
    [[ -n "$(release_asset_url "$1" "$2")" ]]
}

is_semver() {
    [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?(-[0-9A-Za-z.-]+)?$ ]]
}

compare_semver() {
    local left="$1" right="$2"
    local left_core="${left%%-*}" right_core="${right%%-*}"
    local left_pre="" right_pre=""
    [[ "$left" == *-* ]] && left_pre="${left#*-}"
    [[ "$right" == *-* ]] && right_pre="${right#*-}"
    local IFS=.
    local left_parts right_parts
    read -r -a left_parts <<< "$left_core"
    read -r -a right_parts <<< "$right_core"
    local index left_number right_number
    for index in 0 1 2 3; do
        left_number=$((10#${left_parts[$index]:-0}))
        right_number=$((10#${right_parts[$index]:-0}))
        if (( left_number > right_number )); then echo 1; return; fi
        if (( left_number < right_number )); then echo -1; return; fi
    done
    if [[ -z "$left_pre" && -z "$right_pre" ]]; then echo 0; return; fi
    if [[ -z "$left_pre" ]]; then echo 1; return; fi
    if [[ -z "$right_pre" ]]; then echo -1; return; fi

    local left_ids right_ids
    read -r -a left_ids <<< "$left_pre"
    read -r -a right_ids <<< "$right_pre"
    local max="${#left_ids[@]}"
    (( ${#right_ids[@]} > max )) && max="${#right_ids[@]}"
    for ((index=0; index<max; index++)); do
        (( index < ${#left_ids[@]} )) || { echo -1; return; }
        (( index < ${#right_ids[@]} )) || { echo 1; return; }
        local l="${left_ids[$index]}" r="${right_ids[$index]}"
        if [[ "$l" =~ ^[0-9]+$ && "$r" =~ ^[0-9]+$ ]]; then
            if (( 10#$l > 10#$r )); then echo 1; return; fi
            if (( 10#$l < 10#$r )); then echo -1; return; fi
        elif [[ "$l" =~ ^[0-9]+$ ]]; then
            echo -1; return
        elif [[ "$r" =~ ^[0-9]+$ ]]; then
            echo 1; return
        elif [[ "$l" > "$r" ]]; then
            echo 1; return
        elif [[ "$l" < "$r" ]]; then
            echo -1; return
        fi
    done
    echo 0
}

get_release_for_quality() {
    local repo="$1" quality="$2" asset_name="$3"
    local releases
    releases=$(invoke_github_api "https://api.github.com/repos/${repo}/releases?per_page=100")
    local count
    count=$(echo "$releases" | jq 'length')
    local best="" best_version="" legacy_dev=""
    local i
    for ((i=0; i<count; i++)); do
        local release tag prerelease draft
        release=$(echo "$releases" | jq -c ".[$i]")
        draft=$(echo "$release" | jq -r '.draft')
        [[ "$draft" == true ]] && continue
        tag=$(echo "$release" | jq -r '.tag_name')
        if [[ "$tag" == dev ]]; then
            if [[ "$quality" == Dev ]] && has_release_asset "$release" "$asset_name"; then legacy_dev="$release"; fi
            continue
        fi
        [[ "$tag" == install-scripts || "$tag" == install-scripts-v* ]] && continue
        has_release_asset "$release" "$asset_name" || continue
        prerelease=$(echo "$release" | jq -r '.prerelease')
        local is_dev=false matches=false
        [[ "$tag" == *".dev."* || "$tag" == *"-dev."* ]] && is_dev=true
        case "$quality" in
            Dev) [[ "$prerelease" == true && "$is_dev" == true ]] && matches=true ;;
            PreRelease) [[ "$is_dev" == false ]] && matches=true ;;
            Stable) [[ "$prerelease" == false ]] && matches=true ;;
        esac
        [[ "$matches" == true ]] || continue
        local version="${tag#v}"
        is_semver "$version" || continue
        if [[ -z "$best" || "$(compare_semver "$version" "$best_version")" == 1 ]]; then
            best="$release"
            best_version="$version"
        fi
    done

    if [[ -n "$best" ]]; then echo "$best"; return; fi
    if [[ "$quality" == Dev && -n "$legacy_dev" ]]; then
        log_verbose "Using the legacy standing dev release because no immutable SemVer dev release was found."
        echo "$legacy_dev"
        return
    fi
    die "No '$quality' release containing '$asset_name' was found in '$repo'."
}

compute_sha256() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | awk '{print tolower($1)}'
    else
        shasum -a 256 "$1" | awk '{print tolower($1)}'
    fi
}

get_expected_sha256() {
    local line
    line=$(grep -E "[[:space:]]\*?$(printf '%s' "$2" | sed 's/[][\\.^$*+?{}|()]/\\&/g')$" "$1" | head -n 1 || true)
    [[ -n "$line" ]] || die "checksums.txt did not contain an entry for '$2'."
    local hash
    hash=$(echo "$line" | awk '{print tolower($1)}')
    [[ "$hash" =~ ^[0-9a-f]{64}$ ]] || die "Invalid checksum entry for '$2'."
    echo "$hash"
}

assert_archive_integrity() {
    local archive="$1" asset="$2" checksums="$3" metadata="${4:-}"
    local expected actual
    expected=$(get_expected_sha256 "$checksums" "$asset")
    actual=$(compute_sha256 "$archive")
    [[ "$actual" == "$expected" ]] || die "SHA256 mismatch for '$asset'. Expected '$expected' but got '$actual'."
    if [[ -n "$metadata" && -f "$metadata" ]]; then
        local metadata_sha
        metadata_sha=$(jq -r --arg name "$asset" '.assets[] | select(.name == $name) | .sha256 // empty' "$metadata" | head -n 1 | tr '[:upper:]' '[:lower:]')
        [[ "$metadata_sha" == "$expected" ]] || die "release-metadata.json did not match checksums.txt for '$asset'."
    fi
}

escape_regex() {
    printf '%s' "$1" | sed -e 's/[][\\.^$*+?{}|()]/\\&/g'
}

assert_artifact_attestation() {
    local file_path="$1" repo="$2" expected_ref="$3" bundles_path="$4"
    [[ -s "$bundles_path" ]] ||
        die "Release does not contain a portable Sigstore attestation bundle."
    local repo_pattern ref_pattern identity_pattern
    repo_pattern=$(escape_regex "$repo")
    ref_pattern=$(escape_regex "$expected_ref")
    identity_pattern="^https://github\\.com/${repo_pattern}/\\.github/workflows/release\\.yml@${ref_pattern}$"
    local i=0 bundle_json last_error="No matching attestation bundle."
    while IFS= read -r bundle_json; do
        [[ -n "$bundle_json" ]] || continue
        local bundle="${file_path}.bundle.${i}.json"
        printf '%s\n' "$bundle_json" > "$bundle"
        local output=""
        if output=$(cosign verify-blob-attestation \
            --bundle "$bundle" \
            --new-bundle-format \
            --certificate-oidc-issuer "https://token.actions.githubusercontent.com" \
            --certificate-identity-regexp "$identity_pattern" \
            "$file_path" 2>&1); then
            rm -f "$bundle"
            log_verbose "$output"
            return
        fi
        last_error="$output"
        rm -f "$bundle"
        i=$((i + 1))
    done < <(jq -c '.' "$bundles_path")
    die "Artifact attestation verification failed: ${last_error}"
}

get_platform() {
    case "$(uname -s)" in
        Linux) echo linux ;;
        Darwin) echo osx ;;
        *) die "Only Linux and macOS are supported." ;;
    esac
}

get_architecture() {
    case "$(uname -m)" in
        x86_64|amd64) echo x64 ;;
        arm64|aarch64) echo arm64 ;;
        *) die "Only x64 and arm64 are supported." ;;
    esac
}

get_kusto_version() {
    local output version
    output=$("$1" --version 2>/dev/null) || die "Could not determine version for '$1'."
    version=$(echo "$output" | head -n 1 | grep -oE '[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?(-[0-9A-Za-z.-]+)?' | head -n 1 || true)
    [[ -n "$version" ]] || die "Could not parse version for '$1'."
    echo "$version"
}

assert_payload_complete() {
    local directory="$1" required
    for required in kusto payload-manifest.json; do
        [[ -f "${directory}/${required}" ]] || die "Downloaded archive is missing required payload '$required'."
    done
}

validate_payload_manifest() {
    local directory="$1"
    local manifest="${directory}/payload-manifest.json"
    jq -e '
        .files | type == "array" and length > 0 and
        all(.[];
            type == "string" and length > 0 and
            (startswith("/") | not) and
            (contains("\\") | not) and
            (test("(^|[/\\\\])\\.\\.($|[/\\\\])") | not))
    ' "$manifest" >/dev/null || die "payload-manifest.json contains invalid install-relative paths."

    local declared actual
    declared=$(jq -c '.files' "$manifest")
    actual=$(
        cd "$directory"
        find . -type f -print |
            sed 's#^\./##' |
            grep -v '^payload-manifest\.json$' |
            sort |
            jq -R . |
            jq -sc 'sort'
    )
    [[ "$declared" == "$actual" ]] ||
        die "payload-manifest.json does not exactly describe the extracted payload; the manifest itself must be excluded."
}

extract_archive_safely() {
    local archive="$1" destination="$2" entry normalized
    while IFS= read -r entry; do
        normalized="$entry"
        while [[ "$normalized" == ./* ]]; do normalized="${normalized#./}"; done
        [[ -z "$normalized" ]] && continue
        [[ "$normalized" != /* ]] ||
            die "Release archive contains absolute path '$entry'."
        [[ ! "/$normalized/" =~ /\.\./ ]] ||
            die "Release archive contains path traversal entry '$entry'."
    done < <(tar -tzf "$archive")

    if tar -tvzf "$archive" | awk '
        /^[lh]/ { found = 1 }
        END { exit found ? 0 : 1 }
    '; then
        die "Release archive contains symbolic or hard links, which are not supported."
    fi

    tar -xzf "$archive" -C "$destination"
}

install_payload() {
    local source="$1" destination="$2"
    local parent base staging previous
    parent=$(dirname "$destination")
    base=$(basename "$destination")
    mkdir -p "$parent"
    staging="${parent}/.${base}.new.$$"
    previous="${parent}/.${base}.old.$$"
    rm -rf "$staging" "$previous"
    mkdir -p "$staging"
    cp -R "${source}/." "$staging/"

    local -a new_files old_files managed_files
    new_files=()
    while IFS= read -r relative; do new_files+=("$relative"); done < <(jq -r '.files[]' "${staging}/payload-manifest.json")
    new_files+=(payload-manifest.json)
    old_files=(payload-manifest.json)
    if [[ -f "${destination}/payload-manifest.json" ]]; then
        jq -e '
            .files | type == "array" and
            all(.[];
                type == "string" and length > 0 and
                (startswith("/") | not) and
                (contains("\\") | not) and
                (test("(^|/)\\.\\.($|/)") | not))
        ' "${destination}/payload-manifest.json" >/dev/null ||
            die "The installed payload-manifest.json contains unsafe paths."
        while IFS= read -r relative; do old_files+=("$relative"); done < <(jq -r '.files[]' "${destination}/payload-manifest.json")
    fi
    managed_files=()
    while IFS= read -r relative; do managed_files+=("$relative"); done < <(printf '%s\n' "${new_files[@]}" "${old_files[@]}" | sort -u)

    mkdir -p "$destination" "$previous"
    local relative source_path destination_path backup_path
    for relative in "${managed_files[@]}"; do
        destination_path="${destination}/${relative}"
        [[ -f "$destination_path" ]] || continue
        backup_path="${previous}/${relative}"
        mkdir -p "$(dirname "$backup_path")"
        cp -p "$destination_path" "$backup_path" ||
            { rm -rf "$staging" "$previous"; die "Could not back up '$destination_path' before installation."; }
    done

    local install_failed=false
    for relative in "${managed_files[@]}"; do
        if ! rm -f "${destination}/${relative}"; then
            install_failed=true
            break
        fi
    done

    if [[ "$install_failed" == false ]]; then
        for relative in "${new_files[@]}"; do
            source_path="${staging}/${relative}"
            destination_path="${destination}/${relative}"
            mkdir -p "$(dirname "$destination_path")"
            if ! mv "$source_path" "$destination_path"; then
                install_failed=true
                break
            fi
        done
    fi

    if [[ "$install_failed" == false ]]; then
        rm -rf "$staging" "$previous"
        return
    fi

    for relative in "${managed_files[@]}"; do
        rm -f "${destination}/${relative}" || true
    done
    while IFS= read -r backup_path; do
        relative="${backup_path#${previous}/}"
        destination_path="${destination}/${relative}"
        mkdir -p "$(dirname "$destination_path")"
        cp -p "$backup_path" "$destination_path" ||
            die "Installation failed and rollback could not restore '$destination_path'."
    done < <(find "$previous" -type f -print)
    rm -rf "$staging" "$previous"
    die "Could not replace the kusto payload; the previous payload was restored."
}

get_shell_profile() {
    case "$(basename "${SHELL:-/bin/bash}")" in
        zsh) echo "${HOME}/.zshrc" ;;
        fish) echo "${HOME}/.config/fish/config.fish" ;;
        bash)
            if [[ -f "${HOME}/.bash_profile" ]]; then echo "${HOME}/.bash_profile"; else echo "${HOME}/.bashrc"; fi ;;
        *) echo "${HOME}/.profile" ;;
    esac
}

ensure_path() {
    local install_dir="$1" profile shell_name path_line
    profile=$(get_shell_profile)
    shell_name=$(basename "${SHELL:-/bin/bash}")
    if [[ -f "$profile" ]] && grep -Fq "$install_dir" "$profile"; then
        log_verbose "'$install_dir' is already configured in '$profile'."
    else
        mkdir -p "$(dirname "$profile")"
        if [[ "$shell_name" == fish ]]; then path_line="fish_add_path \"$install_dir\""; else path_line="export PATH=\"$install_dir:\$PATH\""; fi
        {
            echo ""
            echo "# Added by the kusto installer"
            echo "$path_line"
        } >> "$profile"
        echo "Added '$install_dir' to '$profile'."
    fi
    if ! echo ":${PATH}:" | grep -Fq ":${install_dir}:"; then
        echo "Open a new terminal or run: source \"$profile\""
    fi
}

configure_completion() {
    local command_path="$1" shell_name extension profile completion_path
    shell_name=$(basename "${SHELL:-/bin/bash}")
    case "$shell_name" in
        bash) extension=bash ;;
        zsh) extension=zsh ;;
        fish) extension=fish ;;
        *) log_verbose "Skipping completion setup for shell '$shell_name'."; return ;;
    esac
    completion_path="$(dirname "$command_path")/kusto-completions.${extension}"
    if ! "$command_path" completions script "$extension" > "$completion_path" 2>/dev/null || [[ ! -s "$completion_path" ]]; then
        rm -f "$completion_path"
        echo "Note: this kusto build does not expose automatic ${shell_name} completion setup."
        return
    fi
    profile=$(get_shell_profile)
    mkdir -p "$(dirname "$profile")"
    if [[ -f "$profile" ]] && grep -Fq "$completion_path" "$profile"; then
        echo "Updated ${shell_name} completion script at '$completion_path'."
        return
    fi
    {
        echo ""
        echo "# Added by the kusto installer for shell completion"
        if [[ "$shell_name" == fish ]]; then
            echo "test -f \"$completion_path\"; and source \"$completion_path\""
        else
            echo "[ -f \"$completion_path\" ] && . \"$completion_path\""
        fi
    } >> "$profile"
    echo "Enabled ${shell_name} completion in '$profile'."
}

install_kusto() {
    require_command curl
    require_command jq
    require_command tar
    local platform architecture rid asset release tag asset_url label
    platform=$(get_platform)
    architecture=$(get_architecture)
    rid="${platform}-${architecture}"
    asset="kusto-${rid}.tar.gz"
    release=$(get_release_for_quality "$REPOSITORY" "$QUALITY" "$asset")
    tag=$(echo "$release" | jq -r '.tag_name')
    asset_url=$(release_asset_url "$release" "$asset")
    label="${QUALITY} release (${tag})"

    if [[ "$QUALITY" != Dev && "$SKIP_PROVENANCE" != true ]]; then assert_cosign_available; fi
    if [[ "$QUALITY" == Dev && "$FORCE" != true ]]; then
        if [[ -e /dev/tty ]]; then
            printf "Dev installs verify checksums but skip provenance. Type YES to continue: "
            local answer
            read -r answer < /dev/tty
            [[ "$answer" == YES ]] || die "Installation canceled."
        else
            die "Dev quality requires --force when no interactive terminal is available."
        fi
    fi

    KUSTO_INSTALL_TEMP_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/kusto-install-XXXXXX")
    local temp="$KUSTO_INSTALL_TEMP_ROOT" archive="${KUSTO_INSTALL_TEMP_ROOT}/${asset}" extract="${KUSTO_INSTALL_TEMP_ROOT}/extract"
    cleanup() { rm -rf "${KUSTO_INSTALL_TEMP_ROOT:-}"; }
    trap cleanup EXIT

    status_step "Downloading ${label}" download_asset "$asset_url" "$archive"
    local checksums_url checksums metadata_url metadata=""
    checksums_url=$(release_asset_url "$release" checksums.txt)
    [[ -n "$checksums_url" ]] || die "Release '$tag' did not include checksums.txt."
    checksums="${temp}/checksums.txt"
    download_asset "$checksums_url" "$checksums"
    metadata_url=$(release_asset_url "$release" release-metadata.json)
    if [[ -n "$metadata_url" ]]; then
        metadata="${temp}/release-metadata.json"
        download_asset "$metadata_url" "$metadata"
    fi
    status_step "Verifying asset checksums" assert_archive_integrity "$archive" "$asset" "$checksums" "$metadata"

    if [[ "$QUALITY" == Dev ]]; then
        echo "Skipping provenance verification for the unattested development build; checksums were verified."
    elif [[ "$SKIP_PROVENANCE" == true ]]; then
        echo "Skipping provenance verification because --skip-provenance was explicitly supplied; checksums were verified."
    else
        local attestations_url attestations_path="${temp}/attestations.jsonl"
        attestations_url=$(release_asset_url "$release" attestations.jsonl)
        [[ -n "$attestations_url" ]] ||
            die "Release '$tag' did not include attestations.jsonl."
        download_asset "$attestations_url" "$attestations_path"
        status_step "Verifying archive provenance" assert_artifact_attestation "$archive" "$REPOSITORY" "refs/tags/${tag}" "$attestations_path"
    fi

    mkdir -p "$extract"
    extract_archive_safely "$archive" "$extract"
    assert_payload_complete "$extract"
    validate_payload_manifest "$extract"
    chmod +x "${extract}/kusto"
    local smoke_path="${temp}/chart-self-test.png"
    "${extract}/kusto" _diag chart-self-test --output "$smoke_path" >/dev/null ||
        die "The downloaded Kusto CLI failed its packaged chart check."
    [[ -s "$smoke_path" ]] ||
        die "The downloaded Kusto CLI did not produce its packaged chart check output."

    local parent install_directory destination downloaded_version installed_version should_install=true
    parent=$(dirname "$TARGET_PATH")
    mkdir -p "$parent"
    install_directory="$(cd "$parent" && pwd)/$(basename "$TARGET_PATH")"
    destination="${install_directory}/kusto"
    downloaded_version=$(get_kusto_version "${extract}/kusto")
    installed_version="$downloaded_version"
    if [[ -f "$destination" ]]; then
        local existing_version
        existing_version=$(get_kusto_version "$destination")
        if [[ "$(compare_semver "$downloaded_version" "$existing_version")" != 1 ]]; then
            should_install=false
            installed_version="$existing_version"
            echo "Existing kusto '$existing_version' is newer than or equal to '$downloaded_version'; skipping replacement."
        fi
    fi
    if [[ "$should_install" == true ]]; then
        status_step "Installing ${downloaded_version} to '${install_directory}'" install_payload "$extract" "$install_directory"
    fi
    if [[ "$UPDATE_PATH" == true ]]; then ensure_path "$install_directory"; else echo "Skipped PATH updates."; fi
    configure_completion "$destination"
    echo "kusto ${installed_version} is ready to use from '${install_directory}'."
}

install_kusto
