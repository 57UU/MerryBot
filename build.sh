#!/bin/bash
# build.sh <target_dir>
# Compiles MerryBot and copies output to <target_dir>
# Returns 0 on success, 1 on failure

set -e

# Shut down any leftover MSBuild/Roslyn workers from previous builds
# (NodeReuse leaves them around, and they hold file locks on .runtimeconfig.json)
dotnet build-server shutdown || true

if [ -z "$1" ]; then
    echo "Usage: build.sh <target_dir>"
    exit 1
fi

target_dir="$1"
project_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

case $(uname -m) in
    x86_64)
        runtime="linux-x64"
        ;;
    aarch64|arm64)
        runtime="linux-arm64"
        ;;
    *)
        echo "Unknown architecture: $(uname -m), defaulting to linux-arm64"
        runtime="linux-arm64"
        ;;
esac

cd "$project_dir"

start_time=$(date +%s)

# Restore all projects (per-project to avoid sln platform parsing issue with .NET 10)
echo "[build] Restoring for $runtime..."
dotnet restore HistoryWebFrontend/HistoryWebFrontend.csproj -r $runtime
dotnet restore MerryBot/MerryBot.csproj -r $runtime

# Publish HistoryWebFrontend to generate wwwroot
echo "[build] Publishing HistoryWebFrontend..."
dotnet publish HistoryWebFrontend/HistoryWebFrontend.csproj -c Release \
    -r $runtime \
    --self-contained false \
    --no-restore \
    -p:PublishTrimmed=false \
    -p:PublishSingleFile=false \
    -p:EnableCompressionInSingleFile=false \
    -p:PublishReadyToRun=false \
    -p:PublishAot=false \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -p:AppendRuntimeIdentifierToOutputPath=false

# Publish MerryBot with ReadyToRun
echo "[build] Publishing MerryBot..."
dotnet publish MerryBot/MerryBot.csproj -c Release \
    -r $runtime \
    --self-contained false \
    --no-restore \
    -p:PublishTrimmed=false \
    -p:TrimMode=link \
    -p:PublishSingleFile=false \
    -p:EnableCompressionInSingleFile=false \
    -p:PublishReadyToRun=false \
    -p:PublishAot=false \
    -p:DebugType=None \
    -p:DebugSymbols=true \
    -p:AppendRuntimeIdentifierToOutputPath=false

# Copy publish output to target slot directory
echo "[build] Copying to $target_dir..."
rm -rf "$target_dir"
mkdir -p "$target_dir"
cp -r MerryBot/bin/linux/Release/net10.0/$runtime/publish/* "$target_dir/"
cp -r HistoryWebFrontend/bin/linux/Release/net10.0/$runtime/publish/wwwroot "$target_dir/"

end_time=$(date +%s)
total_time=$((end_time - start_time))
minutes=$((total_time / 60))
seconds=$((total_time % 60))
echo "[build] Done! Total: ${minutes}m${seconds}s -> $target_dir"
