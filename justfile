project := "src/Fedit/Fedit.fsproj"
tests := "tests/Fedit.Tests/Fedit.Tests.fsproj"
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

# Format sources (F# via fantomas, markdown via prettier).
[group('format')]
format:
    {{dotnet}} tool restore
    {{dotnet}} fantomas .
    bunx --bun prettier --write "**/*.md"

# Check formatting.
[group('format')]
lint:
    {{dotnet}} tool restore
    {{dotnet}} fantomas --check .
    bunx --bun prettier --check "**/*.md"

# Run tests.
[group('test')]
test:
    {{dotnet}} test {{tests}} --nologo

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

# Publish and install fedit.
[unix]
[group('install')]
install dest="~/.local/bin":
    {{dotnet}} publish {{project}} -c Release -o bin/dist
    mkdir -p {{dest}}
    install -m 0755 bin/dist/fedit {{dest}}/fedit
    @echo "Installed fedit to {{dest}}/fedit"
    @echo "Ensure {{dest}} is on your PATH."

# Publish and install fedit.
[windows]
[group('install')]
install dest="%LOCALAPPDATA%\\Programs\\fedit":
    dotnet publish {{project}} -c Release -r win-x64 -o bin\dist
    if not exist "{{dest}}" mkdir "{{dest}}"
    copy /Y bin\dist\fedit.exe "{{dest}}\fedit.exe"
    @echo Installed fedit to {{dest}}\fedit.exe
    @echo Ensure {{dest}} is on your PATH.

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

# Build the tree-sitter-fsharp shared library for the host machine.
# Sources are pre-generated in vendor/tree-sitter-fsharp/fsharp/src/, so no
# `tree-sitter generate` step is needed.
[group('build')]
build-grammar:
    #!/usr/bin/env bash
    set -euo pipefail
    src="vendor/tree-sitter-fsharp/fsharp/src"
    case "$(uname -s)/$(uname -m)" in
        Darwin/arm64)  rid="osx-arm64";   out="libtree-sitter-fsharp.dylib" ;;
        Darwin/x86_64) rid="osx-x64";     out="libtree-sitter-fsharp.dylib" ;;
        Linux/x86_64)  rid="linux-x64";   out="libtree-sitter-fsharp.so" ;;
        Linux/aarch64) rid="linux-arm64"; out="libtree-sitter-fsharp.so" ;;
        *) echo "Unknown host: $(uname -s)/$(uname -m)" >&2; exit 1 ;;
    esac
    dest="src/Fedit/runtimes/$rid/native"
    mkdir -p "$dest"
    extra=""
    [ -f "$src/scanner.c" ] && extra="$src/scanner.c"
    clang -O2 -shared -fPIC -I "$src" -o "$dest/$out" "$src/parser.c" $extra
    echo "Built $dest/$out"

# Cross-build the F# grammar for every shipped RID. Requires zig (brew install zig).
[group('build')]
build-grammar-osx-arm64:
    mkdir -p src/Fedit/runtimes/osx-arm64/native
    clang -O2 -shared -fPIC -target arm64-apple-macos11 \
        -I vendor/tree-sitter-fsharp/fsharp/src \
        -o src/Fedit/runtimes/osx-arm64/native/libtree-sitter-fsharp.dylib \
        vendor/tree-sitter-fsharp/fsharp/src/parser.c vendor/tree-sitter-fsharp/fsharp/src/scanner.c

[group('build')]
build-grammar-osx-x64:
    mkdir -p src/Fedit/runtimes/osx-x64/native
    clang -O2 -shared -fPIC -target x86_64-apple-macos10.15 \
        -I vendor/tree-sitter-fsharp/fsharp/src \
        -o src/Fedit/runtimes/osx-x64/native/libtree-sitter-fsharp.dylib \
        vendor/tree-sitter-fsharp/fsharp/src/parser.c vendor/tree-sitter-fsharp/fsharp/src/scanner.c

[group('build')]
build-grammar-linux-x64:
    mkdir -p src/Fedit/runtimes/linux-x64/native
    zig cc -O2 -shared -fPIC -target x86_64-linux-gnu \
        -I vendor/tree-sitter-fsharp/fsharp/src \
        -o src/Fedit/runtimes/linux-x64/native/libtree-sitter-fsharp.so \
        vendor/tree-sitter-fsharp/fsharp/src/parser.c vendor/tree-sitter-fsharp/fsharp/src/scanner.c

[group('build')]
build-grammar-linux-arm64:
    mkdir -p src/Fedit/runtimes/linux-arm64/native
    zig cc -O2 -shared -fPIC -target aarch64-linux-gnu \
        -I vendor/tree-sitter-fsharp/fsharp/src \
        -o src/Fedit/runtimes/linux-arm64/native/libtree-sitter-fsharp.so \
        vendor/tree-sitter-fsharp/fsharp/src/parser.c vendor/tree-sitter-fsharp/fsharp/src/scanner.c

[group('build')]
build-grammar-win-x64:
    mkdir -p src/Fedit/runtimes/win-x64/native
    zig cc -O2 -shared -target x86_64-windows-gnu \
        -I vendor/tree-sitter-fsharp/fsharp/src \
        -o src/Fedit/runtimes/win-x64/native/tree-sitter-fsharp.dll \
        vendor/tree-sitter-fsharp/fsharp/src/parser.c vendor/tree-sitter-fsharp/fsharp/src/scanner.c

[group('build')]
build-grammars-all: build-grammar-osx-arm64 build-grammar-osx-x64 build-grammar-linux-x64 build-grammar-linux-arm64 build-grammar-win-x64

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
