#!/usr/bin/env bash
# Render the homebrew formula from scripts/fedit.rb.tmpl using release artifacts.
#
# Usage:
#   render-formula.sh <version> <release-dir>
#
# Expects <release-dir> to contain fedit-<triple>.sha256 files for all 4
# unix triples (mac arm + intel, linux arm + intel). The windows .sha256 is
# not consumed by the formula (homebrew is unix-only) but may exist alongside.
#
# Writes the rendered formula to stdout.

set -euo pipefail

VERSION="${1:?usage: render-formula.sh <version> <release-dir>}"
DIR="${2:?usage: render-formula.sh <version> <release-dir>}"
HERE="$(cd "$(dirname "$0")" && pwd)"
TMPL="${HERE}/fedit.rb.tmpl"

if [[ ! -f "${TMPL}" ]]; then
  echo "missing template: ${TMPL}" >&2
  exit 1
fi

read_sha() {
  local triple="$1"
  local f="${DIR}/fedit-${triple}.sha256"
  if [[ ! -f "${f}" ]]; then
    echo "missing sha file: ${f}" >&2
    exit 1
  fi
  tr -d '[:space:]' < "${f}"
}

SHA_OSX_ARM=$(read_sha aarch64-apple-darwin)
SHA_OSX_X64=$(read_sha x86_64-apple-darwin)
SHA_LINUX_ARM=$(read_sha aarch64-unknown-linux-gnu)
SHA_LINUX_X64=$(read_sha x86_64-unknown-linux-gnu)

sed \
  -e "s|{{VERSION}}|${VERSION}|g" \
  -e "s|{{SHA_OSX_ARM}}|${SHA_OSX_ARM}|g" \
  -e "s|{{SHA_OSX_X64}}|${SHA_OSX_X64}|g" \
  -e "s|{{SHA_LINUX_ARM}}|${SHA_LINUX_ARM}|g" \
  -e "s|{{SHA_LINUX_X64}}|${SHA_LINUX_X64}|g" \
  "${TMPL}"
