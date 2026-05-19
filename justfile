project := "src/Fedit/Fedit.fsproj"
tests := "tests/Fedit.Tests/Fedit.Tests.fsproj"
solution := "Fedit.slnx"
dotnet := "PATH=\"$PWD/.dotnet:$PATH\" dotnet"

[private]
default:
    @just --list

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

# Format sources.
[group('format')]
format:
    {{dotnet}} tool restore
    {{dotnet}} fantomas .

# Check formatting.
[group('format')]
lint:
    {{dotnet}} tool restore
    {{dotnet}} fantomas --check .

# Run tests.
[group('test')]
test:
    {{dotnet}} test {{tests}} --nologo

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
