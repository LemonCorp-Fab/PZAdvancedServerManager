set windows-shell := ["powershell.exe", "-NoLogo", "-NoProfile", "-Command"]

solution := "PZAdvancedServerManager.sln"
configuration := env_var_or_default("CONFIGURATION", "Release")
publish_dir := env_var_or_default("PUBLISH_DIR", "publish")
host_runtime := if os() == "windows" { "win-x64" } else { "linux-x64" }

# List all available recipes.
default:
    @just --list

# Restore NuGet dependencies.
restore:
    dotnet restore {{ solution }}

# Build the complete solution. Extra arguments are forwarded to dotnet build.
build *args: restore
    dotnet build {{ solution }} --configuration {{ configuration }} --no-restore {{ args }}

# Run the complete test suite. Extra arguments are forwarded to dotnet test.
test *args: restore
    dotnet test {{ solution }} --configuration {{ configuration }} --no-restore {{ args }}

# Verify formatting without changing files.
format-check: restore
    dotnet format {{ solution }} --verify-no-changes --no-restore

# Format the solution in place.
format: restore
    dotnet format {{ solution }} --no-restore

# Run formatting, build, and tests as a local CI check.
check: format-check build test

# Remove normal .NET build outputs for the selected configuration.
clean:
    dotnet clean {{ solution }} --configuration {{ configuration }}

# Clean and rebuild the complete solution.
rebuild: clean build

# Run the local web UI and open the default browser.
run-ui *args:
    dotnet run --project src/PZAdvancedServerManager.App --configuration {{ configuration }} -- --open-browser {{ args }}

# Run the local web UI without opening a browser.
run-ui-headless *args:
    dotnet run --project src/PZAdvancedServerManager.App --configuration {{ configuration }} -- {{ args }}

# Run the web UI with hot reload and open the default browser.
watch-ui *args:
    dotnet watch --project src/PZAdvancedServerManager.App run --configuration {{ configuration }} -- --open-browser {{ args }}

# Run any CLI command, for example: just run-cli scan --json
run-cli *args:
    dotnet run --project src/PZAdvancedServerManager.Cli --configuration {{ configuration }} -- {{ args }}

# Scan local Project Zomboid installations and mods.
scan *args:
    dotnet run --project src/PZAdvancedServerManager.Cli --configuration {{ configuration }} -- scan {{ args }}

# List saved PZASM projects.
projects *args:
    dotnet run --project src/PZAdvancedServerManager.Cli --configuration {{ configuration }} -- projects {{ args }}

# Run the headless automation daemon.
automation interval="30" *args:
    dotnet run --project src/PZAdvancedServerManager.Cli --configuration {{ configuration }} -- automation run --interval {{ interval }} {{ args }}

# Publish self-contained UI and CLI artifacts for a runtime.
publish runtime=host_runtime:
    dotnet publish src/PZAdvancedServerManager.App --configuration {{ configuration }} --runtime {{ runtime }} --self-contained true -p:PublishSingleFile=true --output {{ publish_dir }}/{{ runtime }}/ui
    dotnet publish src/PZAdvancedServerManager.Cli --configuration {{ configuration }} --runtime {{ runtime }} --self-contained true -p:PublishSingleFile=true --output {{ publish_dir }}/{{ runtime }}/cli

# Publish self-contained Windows x64 artifacts.
publish-win:
    just publish win-x64

# Publish self-contained Linux x64 artifacts.
publish-linux:
    just publish linux-x64
    {{ if os() == "windows" { "New-Item -ItemType Directory -Force -Path '" + publish_dir + "/linux-x64/systemd' | Out-Null" } else { "mkdir -p '" + publish_dir + "/linux-x64/systemd'" } }}
    {{ if os() == "windows" { "Copy-Item -Path 'deploy/systemd/*' -Destination '" + publish_dir + "/linux-x64/systemd' -Recurse -Force" } else { "cp -R deploy/systemd/. '" + publish_dir + "/linux-x64/systemd'" } }}

# Publish both Windows x64 and Linux x64 artifacts.
publish-all: publish-win publish-linux

# Run checks and publish every supported runtime.
release: check publish-all

# Build the production Linux container.
docker-build:
    docker compose build

# Start the container stack in the background.
docker-up:
    docker compose -f compose.yaml -f compose.local.yaml up --detach --build

# Stop the container stack without deleting persistent data.
docker-down:
    docker compose -f compose.yaml -f compose.local.yaml down

# Follow manager container logs.
docker-logs:
    docker compose -f compose.yaml -f compose.local.yaml logs --follow manager

# Validate the Compose model and build the container image.
docker-check:
    docker compose -f compose.yaml -f compose.local.yaml config --quiet
    docker compose -f compose.yaml -f compose.local.yaml build
