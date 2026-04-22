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
