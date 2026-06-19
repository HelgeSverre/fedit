project := "src/Fedit/Fedit.fsproj"
tests := "tests/Fedit.Tests/Fedit.Tests.fsproj"
benchmarks := "benchmarks/Fedit.Benchmarks/Fedit.Benchmarks.fsproj"
solution := "Fedit.slnx"
dotnet := "PATH=\"$PWD/.dotnet:$PATH\" dotnet"

# Marketing site (Astro + bun) — see website/justfile
mod website

[private]
default:
    @just --list --list-submodules

# Watch and run.
[group('run')]
dev path=".":
    {{dotnet}} watch --project {{project}} run -- "{{path}}"

# Run the editor.
[group('run')]
run path=".":
    {{dotnet}} run --project {{project}} -- "{{path}}"

# Build the solution.
[group('build')]
build:
    {{dotnet}} build {{solution}}

# Remove build output.
[group('build')]
clean:
    {{dotnet}} clean {{project}}
    rm -rf bin obj

# Build the NativeAOT bundle for a RID: the AOT editor + the self-contained
# plugin host beside it (the editor spawns it; AOT has no JIT to load plugins).
# NativeAOT does not cross-compile across OS — run this on a matching-OS host.
[group('build')]
aot rid="osx-arm64" out="dist-aot":
    {{dotnet}} publish {{project}} -c Release -r {{rid}} -p:FeditAot=true -o {{out}} --nologo
    {{dotnet}} publish src/Fedit.PluginHost/Fedit.PluginHost.fsproj -c Release -r {{rid}} --self-contained -o {{out}} --nologo
    @echo "→ AOT bundle in {{out}}/ (fedit + Fedit.PluginHost)"

# Format sources (F# via fantomas, markdown via oxfmt).
[group('format')]
format:
    {{dotnet}} tool restore
    {{dotnet}} fantomas .
    bunx --bun oxfmt "**/*.md"

# Check formatting.
[group('format')]
lint:
    {{dotnet}} tool restore
    {{dotnet}} fantomas --check .
    bunx --bun oxfmt --check "**/*.md"

# Run tests.
[group('test')]
test:
    {{dotnet}} test {{tests}} --nologo

# Verify generated shell completions in Docker (nu/elvish/xonsh load + complete;
# bash/zsh/fish parse). Generates each script and mounts them into the harness.
[unix]
[group('test')]
test-completions:
    #!/usr/bin/env bash
    set -euo pipefail
    out=$(mktemp -d)
    {{dotnet}} build {{project}} --nologo >/dev/null
    for sh in bash zsh fish pwsh nu elvish xonsh yash murex; do
      case $sh in
        nu) f=fedit.nu ;; elvish) f=fedit.elv ;; xonsh) f=fedit.xsh ;; pwsh) f=fedit.ps1 ;;
        zsh) f=_fedit ;; fish) f=fedit.fish ;; yash) f=fedit.yash ;; murex) f=fedit.mx ;; *) f=fedit.$sh ;;
      esac
      {{dotnet}} run --project {{project}} --no-build -- completions "$sh" > "$out/$f"
    done
    docker build -t fedit-comp-test tests/completions
    docker run --rm -v "$out:/completions:ro" fedit-comp-test

# Generate HTML coverage report at coverage/index.html. Pass `open` to launch it.
[group('test')]
coverage open="":
    {{dotnet}} tool restore
    rm -rf coverage TestResults
    {{dotnet}} test {{tests}} --nologo --collect:"XPlat Code Coverage" --results-directory TestResults
    {{dotnet}} reportgenerator -reports:'TestResults/**/coverage.cobertura.xml' -targetdir:coverage -reporttypes:Html
    @if [ -n "{{open}}" ]; then open coverage/index.html; else echo "→ open coverage/index.html"; fi

# Pre-commit gate.
[group('test')]
check: lint build test

# Micro benchmarks (BenchmarkDotNet, ShortRun, in-process). Full run ~4 min;
# filter to a class, e.g. `just bench '*PieceTable*'` (~1 min). Results land
# in BenchmarkDotNet.Artifacts/ (gitignored). See docs/benchmarks.md.
[group('bench')]
bench filter="*":
    {{dotnet}} run --project {{benchmarks}} -c Release -p:SelfContained=false -- --filter "{{filter}}"

# Manual harness: full-frame render pipeline + tree-sitter parse (~1-2 min).
# Scope: empty for both, or `frames` / `parse`.
[group('bench')]
bench-manual scope="":
    {{dotnet}} run --project {{benchmarks}} -c Release -p:SelfContained=false -- manual {{scope}}

# Publish and install fedit.
[unix]
[group('install')]
install dest="~/.local/bin":
    {{dotnet}} publish {{project}} -c Release -o bin/dist
    mkdir -p {{dest}}
    install -m 0755 bin/dist/fedit {{dest}}/fedit
    install -m 0644 bin/dist/Fedit.PluginApi.dll {{dest}}/Fedit.PluginApi.dll
    # Plugin builds and tree-sitter grammars resolve sidecars via
    # AppContext.BaseDirectory. Ship them next to the binary.
    rm -rf {{dest}}/runtimes
    cp -R bin/dist/runtimes {{dest}}/runtimes
    @echo "Installed fedit to {{dest}}/fedit"
    @echo "Ensure {{dest}} is on your PATH."
    @case "$(basename "${SHELL:-}")" in \
       zsh|bash|fish) {{dest}}/fedit completions "$(basename "$SHELL")" --install ;; \
       *) echo "Run 'fedit completions <zsh|bash|fish> --install' for shell completions." ;; \
     esac

# Publish and install fedit.
[windows]
[group('install')]
install dest="%LOCALAPPDATA%\\Programs\\fedit":
    dotnet publish {{project}} -c Release -r win-x64 -o bin\dist
    if not exist "{{dest}}" mkdir "{{dest}}"
    copy /Y bin\dist\fedit.exe "{{dest}}\fedit.exe"
    copy /Y bin\dist\Fedit.PluginApi.dll "{{dest}}\Fedit.PluginApi.dll"
    if exist "{{dest}}\runtimes" rmdir /S /Q "{{dest}}\runtimes"
    xcopy /E /I /Y bin\dist\runtimes "{{dest}}\runtimes"
    @echo Installed fedit to {{dest}}\fedit.exe
    @echo Ensure {{dest}} is on your PATH.
    @echo Shell completions are not generated for PowerShell yet.

# Uninstall fedit.
[unix]
[group('install')]
uninstall dest="~/.local/bin":
    rm -f {{dest}}/fedit
    @echo "Removed {{dest}}/fedit"

# Uninstall fedit.
[windows]
[group('install')]
uninstall dest="%LOCALAPPDATA%\\Programs\\fedit":
    if exist "{{dest}}\fedit.exe" del /Q "{{dest}}\fedit.exe"
    @echo Removed {{dest}}\fedit.exe

# Download highlights.scm query files for all bundled languages from
# their official tree-sitter grammar repositories.
[group('build')]
download-queries:
    #!/usr/bin/env bash
    set -euo pipefail
    mkdir -p src/Fedit/Resources/queries
    # Simple bundled grammars — standard path layout.
    langs=(javascript typescript python json c-sharp go rust html css c php)
    for lang in "${langs[@]}"; do
      dest="src/Fedit/Resources/queries/$lang/highlights.scm"
      mkdir -p "$(dirname "$dest")"
      url="https://raw.githubusercontent.com/tree-sitter/tree-sitter-$lang/master/queries/highlights.scm"
      if curl -fsSL "$url" -o "$dest"; then
        echo "Downloaded $lang"
      else
        echo "WARNING: failed to download $lang from $url"
      fi
    done
    # TSX lives in the tree-sitter-typescript repo under queries/ (shared with TS).
    # We maintain a hand-written tsx/highlights.scm with JSX additions, so skip auto-download.
    # Vendored grammars — copy from submodule.
    cp vendor/tree-sitter-markdown/tree-sitter-markdown/queries/highlights.scm src/Fedit/Resources/queries/markdown/highlights.scm && echo "Copied markdown"
    cp vendor/tree-sitter-xml/queries/xml/highlights.scm                        src/Fedit/Resources/queries/xml/highlights.scm      && echo "Copied xml"
    cp vendor/tree-sitter-dart/queries/highlights.scm                           src/Fedit/Resources/queries/dart/highlights.scm     && echo "Copied dart"
    cp vendor/tree-sitter-just/queries/just/highlights.scm                      src/Fedit/Resources/queries/just/highlights.scm     && echo "Copied just"
    cp vendor/tree-sitter-make/queries/highlights.scm                           src/Fedit/Resources/queries/make/highlights.scm     && echo "Copied make"
    cp vendor/tree-sitter-astro/queries/highlights.scm                          src/Fedit/Resources/queries/astro/highlights.scm    && echo "Copied astro"
    cp vendor/tree-sitter-toml/queries/highlights.scm                           src/Fedit/Resources/queries/toml/highlights.scm     && echo "Copied toml"
    cp vendor/tree-sitter-sema/queries/highlights.scm                          src/Fedit/Resources/queries/sema/highlights.scm    && echo "Copied sema"
    cp vendor/tree-sitter-rescript/queries/highlights.scm                      src/Fedit/Resources/queries/rescript/highlights.scm && echo "Copied rescript"
    cp vendor/tree-sitter-zig/queries/highlights.scm                           src/Fedit/Resources/queries/zig/highlights.scm      && echo "Copied zig"
    # tree-sitter-applescript does not ship a highlights query; fedit maintains
    # src/Fedit/Resources/queries/applescript/highlights.scm.

# Build all native grammars for the host machine (F# + all vendored).
# Run this once after a fresh clone or after updating a grammar submodule.
[group('build')]
build-grammars:
    #!/usr/bin/env bash
    set -euo pipefail
    src="vendor/tree-sitter-fsharp/fsharp/src"
    case "$(uname -s)/$(uname -m)" in
        Darwin/arm64)  rid="osx-arm64";   ext="dylib" ;;
        Darwin/x86_64) rid="osx-x64";     ext="dylib" ;;
        Linux/x86_64)  rid="linux-x64";   ext="so" ;;
        Linux/aarch64) rid="linux-arm64"; ext="so" ;;
        *) echo "Unknown host: $(uname -s)/$(uname -m)" >&2; exit 1 ;;
    esac
    dest="src/Fedit/runtimes/$rid/native"
    mkdir -p "$dest"
    extra=""; [ -f "$src/scanner.c" ] && extra="$src/scanner.c"
    clang -O2 -shared -fPIC -I "$src" -o "$dest/libtree-sitter-fsharp.$ext" "$src/parser.c" $extra
    echo "fsharp OK"
    for entry in \
        "markdown|vendor/tree-sitter-markdown/tree-sitter-markdown/src|parser.c scanner.c" \
        "xml|vendor/tree-sitter-xml/xml/src|parser.c scanner.c" \
        "dart|vendor/tree-sitter-dart/src|parser.c scanner.c" \
        "just|vendor/tree-sitter-just/src|parser.c scanner.c" \
        "make|vendor/tree-sitter-make/src|parser.c" \
        "astro|vendor/tree-sitter-astro/src|parser.c scanner.c" \
        "toml|vendor/tree-sitter-toml/src|parser.c scanner.c" \
        "sema|vendor/tree-sitter-sema/src|parser.c scanner.c" \
        "applescript|vendor/tree-sitter-applescript/src|parser.c scanner.c" \
        "rescript|vendor/tree-sitter-rescript/src|parser.c scanner.c" \
        "zig|vendor/tree-sitter-zig/src|parser.c"; do
      IFS='|' read -r name srcdir files <<< "$entry"
      srcs=""; for f in $files; do [ -f "$srcdir/$f" ] && srcs="$srcs $srcdir/$f"; done
      clang -O2 -shared -fPIC -I "$srcdir" -o "$dest/libtree-sitter-$name.$ext" $srcs
      echo "$name OK"
    done

# Cross-build all grammars (F# + vendored) for every shipped RID. Requires zig.
[group('build')]
build-grammars-all: _build-grammar-osx-arm64 _build-grammar-osx-x64 _build-grammar-linux-x64 _build-grammar-linux-arm64 _build-grammar-win-x64

[private]
[group('build')]
_build-grammar-osx-arm64:
    #!/usr/bin/env bash
    set -euo pipefail
    rid=osx-arm64; dest=src/Fedit/runtimes/$rid/native; mkdir -p "$dest"
    clang -O2 -shared -fPIC -target arm64-apple-macos11 \
        -I vendor/tree-sitter-fsharp/fsharp/src \
        -o "$dest/libtree-sitter-fsharp.dylib" \
        vendor/tree-sitter-fsharp/fsharp/src/parser.c vendor/tree-sitter-fsharp/fsharp/src/scanner.c
    for entry in \
        "markdown|vendor/tree-sitter-markdown/tree-sitter-markdown/src|parser.c scanner.c" \
        "xml|vendor/tree-sitter-xml/xml/src|parser.c scanner.c" \
        "dart|vendor/tree-sitter-dart/src|parser.c scanner.c" \
        "just|vendor/tree-sitter-just/src|parser.c scanner.c" \
        "make|vendor/tree-sitter-make/src|parser.c" \
        "astro|vendor/tree-sitter-astro/src|parser.c scanner.c" \
        "toml|vendor/tree-sitter-toml/src|parser.c scanner.c" \
        "sema|vendor/tree-sitter-sema/src|parser.c scanner.c" \
        "applescript|vendor/tree-sitter-applescript/src|parser.c scanner.c" \
        "rescript|vendor/tree-sitter-rescript/src|parser.c scanner.c" \
        "zig|vendor/tree-sitter-zig/src|parser.c"; do
      IFS='|' read -r name srcdir files <<< "$entry"
      srcs=""; for f in $files; do [ -f "$srcdir/$f" ] && srcs="$srcs $srcdir/$f"; done
      clang -O2 -shared -fPIC -target arm64-apple-macos11 -I "$srcdir" -o "$dest/libtree-sitter-$name.dylib" $srcs
      echo "$name osx-arm64 OK"
    done

[private]
[group('build')]
_build-grammar-osx-x64:
    #!/usr/bin/env bash
    set -euo pipefail
    rid=osx-x64; dest=src/Fedit/runtimes/$rid/native; mkdir -p "$dest"
    clang -O2 -shared -fPIC -target x86_64-apple-macos10.15 \
        -I vendor/tree-sitter-fsharp/fsharp/src \
        -o "$dest/libtree-sitter-fsharp.dylib" \
        vendor/tree-sitter-fsharp/fsharp/src/parser.c vendor/tree-sitter-fsharp/fsharp/src/scanner.c
    for entry in \
        "markdown|vendor/tree-sitter-markdown/tree-sitter-markdown/src|parser.c scanner.c" \
        "xml|vendor/tree-sitter-xml/xml/src|parser.c scanner.c" \
        "dart|vendor/tree-sitter-dart/src|parser.c scanner.c" \
        "just|vendor/tree-sitter-just/src|parser.c scanner.c" \
        "make|vendor/tree-sitter-make/src|parser.c" \
        "astro|vendor/tree-sitter-astro/src|parser.c scanner.c" \
        "toml|vendor/tree-sitter-toml/src|parser.c scanner.c" \
        "sema|vendor/tree-sitter-sema/src|parser.c scanner.c" \
        "applescript|vendor/tree-sitter-applescript/src|parser.c scanner.c" \
        "rescript|vendor/tree-sitter-rescript/src|parser.c scanner.c" \
        "zig|vendor/tree-sitter-zig/src|parser.c"; do
      IFS='|' read -r name srcdir files <<< "$entry"
      srcs=""; for f in $files; do [ -f "$srcdir/$f" ] && srcs="$srcs $srcdir/$f"; done
      clang -O2 -shared -fPIC -target x86_64-apple-macos10.15 -I "$srcdir" -o "$dest/libtree-sitter-$name.dylib" $srcs
    done

[private]
[group('build')]
_build-grammar-linux-x64:
    #!/usr/bin/env bash
    set -euo pipefail
    rid=linux-x64; dest=src/Fedit/runtimes/$rid/native; mkdir -p "$dest"
    zig cc -O2 -shared -fPIC -target x86_64-linux-gnu \
        -I vendor/tree-sitter-fsharp/fsharp/src \
        -o "$dest/libtree-sitter-fsharp.so" \
        vendor/tree-sitter-fsharp/fsharp/src/parser.c vendor/tree-sitter-fsharp/fsharp/src/scanner.c
    for entry in \
        "markdown|vendor/tree-sitter-markdown/tree-sitter-markdown/src|parser.c scanner.c" \
        "xml|vendor/tree-sitter-xml/xml/src|parser.c scanner.c" \
        "dart|vendor/tree-sitter-dart/src|parser.c scanner.c" \
        "just|vendor/tree-sitter-just/src|parser.c scanner.c" \
        "make|vendor/tree-sitter-make/src|parser.c" \
        "astro|vendor/tree-sitter-astro/src|parser.c scanner.c" \
        "toml|vendor/tree-sitter-toml/src|parser.c scanner.c" \
        "sema|vendor/tree-sitter-sema/src|parser.c scanner.c" \
        "applescript|vendor/tree-sitter-applescript/src|parser.c scanner.c" \
        "rescript|vendor/tree-sitter-rescript/src|parser.c scanner.c" \
        "zig|vendor/tree-sitter-zig/src|parser.c"; do
      IFS='|' read -r name srcdir files <<< "$entry"
      srcs=""; for f in $files; do [ -f "$srcdir/$f" ] && srcs="$srcs $srcdir/$f"; done
      zig cc -O2 -UNDEBUG -shared -fPIC -target x86_64-linux-gnu -I "$srcdir" -o "$dest/libtree-sitter-$name.so" $srcs
    done

[private]
[group('build')]
_build-grammar-linux-arm64:
    #!/usr/bin/env bash
    set -euo pipefail
    rid=linux-arm64; dest=src/Fedit/runtimes/$rid/native; mkdir -p "$dest"
    zig cc -O2 -shared -fPIC -target aarch64-linux-gnu \
        -I vendor/tree-sitter-fsharp/fsharp/src \
        -o "$dest/libtree-sitter-fsharp.so" \
        vendor/tree-sitter-fsharp/fsharp/src/parser.c vendor/tree-sitter-fsharp/fsharp/src/scanner.c
    for entry in \
        "markdown|vendor/tree-sitter-markdown/tree-sitter-markdown/src|parser.c scanner.c" \
        "xml|vendor/tree-sitter-xml/xml/src|parser.c scanner.c" \
        "dart|vendor/tree-sitter-dart/src|parser.c scanner.c" \
        "just|vendor/tree-sitter-just/src|parser.c scanner.c" \
        "make|vendor/tree-sitter-make/src|parser.c" \
        "astro|vendor/tree-sitter-astro/src|parser.c scanner.c" \
        "toml|vendor/tree-sitter-toml/src|parser.c scanner.c" \
        "sema|vendor/tree-sitter-sema/src|parser.c scanner.c" \
        "applescript|vendor/tree-sitter-applescript/src|parser.c scanner.c" \
        "rescript|vendor/tree-sitter-rescript/src|parser.c scanner.c" \
        "zig|vendor/tree-sitter-zig/src|parser.c"; do
      IFS='|' read -r name srcdir files <<< "$entry"
      srcs=""; for f in $files; do [ -f "$srcdir/$f" ] && srcs="$srcs $srcdir/$f"; done
      zig cc -O2 -UNDEBUG -shared -fPIC -target aarch64-linux-gnu -I "$srcdir" -o "$dest/libtree-sitter-$name.so" $srcs
    done

[private]
[group('build')]
_build-grammar-win-x64:
    #!/usr/bin/env bash
    set -euo pipefail
    rid=win-x64; dest=src/Fedit/runtimes/$rid/native; mkdir -p "$dest"
    zig cc -O2 -shared -target x86_64-windows-gnu \
        -I vendor/tree-sitter-fsharp/fsharp/src \
        -o "$dest/tree-sitter-fsharp.dll" \
        vendor/tree-sitter-fsharp/fsharp/src/parser.c vendor/tree-sitter-fsharp/fsharp/src/scanner.c
    for entry in \
        "markdown|vendor/tree-sitter-markdown/tree-sitter-markdown/src|parser.c scanner.c" \
        "xml|vendor/tree-sitter-xml/xml/src|parser.c scanner.c" \
        "dart|vendor/tree-sitter-dart/src|parser.c scanner.c" \
        "just|vendor/tree-sitter-just/src|parser.c scanner.c" \
        "make|vendor/tree-sitter-make/src|parser.c" \
        "astro|vendor/tree-sitter-astro/src|parser.c scanner.c" \
        "toml|vendor/tree-sitter-toml/src|parser.c scanner.c" \
        "sema|vendor/tree-sitter-sema/src|parser.c scanner.c" \
        "applescript|vendor/tree-sitter-applescript/src|parser.c scanner.c" \
        "rescript|vendor/tree-sitter-rescript/src|parser.c scanner.c" \
        "zig|vendor/tree-sitter-zig/src|parser.c"; do
      IFS='|' read -r name srcdir files <<< "$entry"
      srcs=""; for f in $files; do [ -f "$srcdir/$f" ] && srcs="$srcs $srcdir/$f"; done
      zig cc -O2 -UNDEBUG -shared -target x86_64-windows-gnu -I "$srcdir" -o "$dest/tree-sitter-$name.dll" $srcs
    done

# Cut a release: tag and push. CI publishes binaries + updates the homebrew formula.
# Usage: just release 0.1.0
[group('release')]
release version:
    @if ! git diff-index --quiet HEAD --; then echo "✗ working tree is dirty, commit or stash first" >&2; exit 1; fi
    @if [ "$(git rev-parse --abbrev-ref HEAD)" != "main" ]; then echo "✗ not on main branch" >&2; exit 1; fi
    @echo "→ tagging v{{version}}"
    git tag -a "v{{version}}" -m "fedit v{{version}}"
    git push origin "v{{version}}"
    @echo "→ tag pushed. watch CI: https://github.com/HelgeSverre/fedit/actions"

# Dry-run the formula render locally (uses fake SHAs).
[group('release')]
release-formula-preview:
    #!/usr/bin/env bash
    set -euo pipefail
    tmp=$(mktemp -d)
    for t in aarch64-apple-darwin x86_64-apple-darwin aarch64-unknown-linux-gnu x86_64-unknown-linux-gnu; do
      printf '0000000000000000000000000000000000000000000000000000000000000000' > "$tmp/fedit-$t.sha256"
    done
    bash scripts/render-formula.sh 0.0.0-preview "$tmp"
