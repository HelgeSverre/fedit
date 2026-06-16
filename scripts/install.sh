#!/bin/sh
# fedit installer — detects your OS/CPU, downloads the matching release
# tarball, verifies its checksum, and installs the self-contained binary
# together with its Fedit.PluginApi.dll sidecar and tree-sitter runtimes
# (both must sit next to the binary, so a launcher shim points at them).
#
# Usage:
#   curl -fsSL https://fedit.dev/install.sh | sh
#
# Environment overrides:
#   FEDIT_VERSION   version to install, e.g. 1.3.0 (default: latest)
#   FEDIT_BIN_DIR   where the `fedit` launcher lands (default: ~/.local/bin)
#   FEDIT_LIB_DIR   where the bundle lands (default: ${XDG_DATA_HOME:-~/.local/share}/fedit)
set -eu

REPO="HelgeSverre/fedit"
VERSION="${FEDIT_VERSION:-latest}"
BIN_DIR="${FEDIT_BIN_DIR:-$HOME/.local/bin}"
LIB_DIR="${FEDIT_LIB_DIR:-${XDG_DATA_HOME:-$HOME/.local/share}/fedit}"

err() {
	printf 'fedit install: %s\n' "$1" >&2
	exit 1
}
info() { printf '%s\n' "$1"; }

# --- detect platform → release triple ------------------------------------
os="$(uname -s)"
arch="$(uname -m)"

case "$os" in
Darwin) plat="apple-darwin" ;;
Linux) plat="unknown-linux-gnu" ;;
*) err "unsupported OS '$os'. Windows: download the .zip from https://github.com/$REPO/releases/latest" ;;
esac

case "$arch" in
arm64 | aarch64) cpu="aarch64" ;;
x86_64 | amd64) cpu="x86_64" ;;
*) err "unsupported CPU architecture '$arch'" ;;
esac

triple="${cpu}-${plat}"
asset="fedit-${triple}.tar.xz"

if [ "$VERSION" = latest ]; then
	base="https://github.com/$REPO/releases/latest/download"
else
	base="https://github.com/$REPO/releases/download/v${VERSION#v}"
fi

# --- tooling -------------------------------------------------------------
if command -v curl >/dev/null 2>&1; then
	dl() { curl -fsSL "$1" -o "$2"; }
elif command -v wget >/dev/null 2>&1; then
	dl() { wget -qO "$2" "$1"; }
else
	err "need curl or wget on PATH"
fi
command -v tar >/dev/null 2>&1 || err "need tar on PATH"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT INT TERM

# --- download + verify ---------------------------------------------------
info "fedit install: fetching $asset ($VERSION) for $os/$arch"
dl "$base/$asset" "$tmp/$asset" || err "download failed: $base/$asset"

if dl "$base/fedit-${triple}.sha256" "$tmp/sum" 2>/dev/null; then
	want="$(tr -d '[:space:]' <"$tmp/sum")"
	if command -v sha256sum >/dev/null 2>&1; then
		got="$(sha256sum "$tmp/$asset" | cut -d' ' -f1)"
	elif command -v shasum >/dev/null 2>&1; then
		got="$(shasum -a 256 "$tmp/$asset" | cut -d' ' -f1)"
	else
		got=""
	fi
	if [ -n "$got" ]; then
		[ "$got" = "$want" ] || err "checksum mismatch (expected $want, got $got)"
		info "fedit install: checksum verified"
	fi
fi

# --- install -------------------------------------------------------------
info "fedit install: unpacking into $LIB_DIR"
rm -rf "$LIB_DIR"
mkdir -p "$LIB_DIR"
tar -xJf "$tmp/$asset" -C "$LIB_DIR"
[ -f "$LIB_DIR/fedit" ] || err "archive did not contain a fedit binary"
chmod +x "$LIB_DIR/fedit"

# Launcher shim: exec the real binary so AppContext.BaseDirectory stays in
# LIB_DIR, where the PluginApi sidecar and tree-sitter runtimes live.
mkdir -p "$BIN_DIR"
cat >"$BIN_DIR/fedit" <<EOF
#!/bin/sh
exec "$LIB_DIR/fedit" "\$@"
EOF
chmod +x "$BIN_DIR/fedit"

info "fedit install: installed $("$LIB_DIR/fedit" --version 2>/dev/null || echo fedit) to $BIN_DIR/fedit"

# --- best-effort shell completions ---------------------------------------
case "$(basename "${SHELL:-}")" in
bash | zsh | fish)
	if "$BIN_DIR/fedit" completions "$(basename "$SHELL")" --install >/dev/null 2>&1; then
		info "fedit install: installed $(basename "$SHELL") completions"
	fi
	;;
esac

# --- PATH hint -----------------------------------------------------------
case ":$PATH:" in
*":$BIN_DIR:"*) ;;
*)
	info ""
	info "Add $BIN_DIR to your PATH, e.g.:"
	info "  export PATH=\"$BIN_DIR:\$PATH\""
	;;
esac

info "Done. Launch with: fedit ."
