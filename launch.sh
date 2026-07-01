#!/bin/bash

restart_code=101
reload_code=102
prebuilt_code=103

force_build=false

# Parse arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        -f|--force-build)
            force_build=true
            shift
            ;;
        *)
            echo "Usage: $0 [-f|--force-build]"
            exit 1
            ;;
    esac
done

project_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
slot_dir="$project_dir/build"
active_slot_file="$slot_dir/active_slot"

# Handle interrupt signals gracefully
trap 'echo "[launch] Interrupted, exiting"; exit 0' INT TERM

# Read active slot (default A)
read_active_slot() {
    if [ -f "$active_slot_file" ]; then
        cat "$active_slot_file" | tr -d '[:space:]'
    else
        echo "A"
    fi
}

# Get slot path
slot_path() {
    echo "$slot_dir/slot_$(echo "$1" | tr '[:upper:]' '[:lower:]')"
}

cd "$project_dir"

# Bootstrap: build active slot if it doesn't exist or force build
active=$(read_active_slot)
active_path=$(slot_path "$active")

if [ "$force_build" = true ]; then
    echo "[launch] Force build: rebuilding slot $active..."
    mkdir -p "$slot_dir"
    if ! bash build.sh "$active_path"; then
        echo "[launch] Force build failed, exiting"
        exit 1
    fi
    echo -n "$active" > "$active_slot_file"
elif [ ! -f "$active_path/MerryBot" ]; then
    echo "[launch] First boot: building slot $active..."
    mkdir -p "$slot_dir"
    if ! bash build.sh "$active_path"; then
        echo "[launch] Initial build failed, exiting"
        exit 1
    fi
    echo -n "$active" > "$active_slot_file"
fi

# Main loop
while true; do
    active=$(read_active_slot)
    active_path=$(slot_path "$active")

    if [ ! -f "$active_path/MerryBot" ]; then
        echo "[launch] Slot $active not built, building..."
        if ! bash build.sh "$active_path"; then
            echo "[launch] Build failed for slot $active, exiting"
            exit 1
        fi
    fi

    echo "[launch] Starting from slot $active ($active_path)..."
    cd "$active_path"

    ./MerryBot
    exit_code=$?

    cd "$project_dir"

    if [ $exit_code -eq $prebuilt_code ]; then
        # PREBUILT: C# app already built to inactive slot and updated active_slot file
        new_active=$(read_active_slot)
        echo "[launch] Prebuilt detected, switching to slot $new_active..."
        continue

    elif [ $exit_code -eq $restart_code ]; then
        # RESTART: recompile current slot and restart
        echo "[launch] Restart requested, rebuilding slot $active..."
        sleep 1
        if ! bash build.sh "$active_path"; then
            echo "[launch] Rebuild failed for slot $active, exiting"
            exit 1
        fi
        continue

    elif [ $exit_code -eq $reload_code ]; then
        # RELOAD: restart without recompiling
        echo "[launch] Reload requested, restarting..."
        sleep 1
        continue

    else
        echo "[launch] Exit code $exit_code, exiting"
        exit 0
    fi
done
