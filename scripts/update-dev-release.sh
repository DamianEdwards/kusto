#!/usr/bin/env bash

set -euo pipefail

VERSION="$1"
BUNDLE_DIR="$2"
DESCRIPTION="${3:-Development channel build published from CI.}"
TAG="v${VERSION}"

NOTES=$(cat <<EOF
## Development Channel

**Version:** ${VERSION}
**Commit:** ${GITHUB_SHA}
**Build:** [#${GITHUB_RUN_NUMBER}](${GITHUB_SERVER_URL}/${GITHUB_REPOSITORY}/actions/runs/${GITHUB_RUN_ID})

${DESCRIPTION}

- Dev installs always verify SHA-256 checksums.
- Dev archives are not attested; installers visibly skip provenance verification.
EOF
)

if gh release view "$TAG" --repo "$GITHUB_REPOSITORY" &>/dev/null; then
  tag_sha=$(gh api "repos/${GITHUB_REPOSITORY}/git/ref/tags/${TAG}" --jq '.object.sha')
  tag_type=$(gh api "repos/${GITHUB_REPOSITORY}/git/ref/tags/${TAG}" --jq '.object.type')
  if [[ "$tag_type" == "tag" ]]; then
    tag_sha=$(gh api "repos/${GITHUB_REPOSITORY}/git/tags/${tag_sha}" --jq '.object.sha')
  fi
  if [[ "$tag_sha" != "$GITHUB_SHA" ]]; then
    echo "::error::Dev release ${TAG} already targets ${tag_sha}, not ${GITHUB_SHA}; refusing to mutate an immutable version."
    exit 1
  fi

  gh release edit "$TAG" --repo "$GITHUB_REPOSITORY" --prerelease --latest=false --title "$TAG" --notes "$NOTES"
  gh release upload "$TAG" --repo "$GITHUB_REPOSITORY" "${BUNDLE_DIR}"/* --clobber
else
  gh release create "$TAG" \
    --repo "$GITHUB_REPOSITORY" \
    --target "$GITHUB_SHA" \
    --prerelease \
    --latest=false \
    --title "$TAG" \
    --notes "$NOTES" \
    "${BUNDLE_DIR}"/*
fi
