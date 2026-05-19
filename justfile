project := "fedit.fsproj"
dotnet := "PATH=\"$PWD/.dotnet:$PATH\" dotnet"

# Show available recipes.
default:
    @just --list

# Start the app under dotnet watch. Pass a workspace path with `just dev path/to/project`.
dev path=".":
    {{dotnet}} watch --project {{project}} run -- "{{path}}"

# Build the F# project.
build:
    {{dotnet}} build {{project}}

# Run the editor. Pass a workspace path with `just run path/to/project`.
run path=".":
    {{dotnet}} run --project {{project}} -- "{{path}}"

# Remove compiled output.
clean:
    {{dotnet}} clean {{project}}
    rm -rf bin obj

# Install fedit to a local bin directory as a self-contained single-file binary.
install dest="~/.local/bin":
    {{dotnet}} publish {{project}} -c Release -p:PublishSingleFile=true --self-contained true -o bin/dist
    mkdir -p {{dest}}
    install -m 0755 bin/dist/fedit {{dest}}/fedit
    @echo "Installed fedit to {{dest}}/fedit"
    @echo "Ensure {{dest}} is on your PATH."

# Remove a previously installed fedit binary.
uninstall dest="~/.local/bin":
    rm -f {{dest}}/fedit
    @echo "Removed {{dest}}/fedit"
