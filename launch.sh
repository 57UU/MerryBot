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

# 默认数据目录：未显式设置 MERRY_BOT 时统一落在 build/data，避免随 AB 槽切换
if [ -z "${MERRY_BOT:-}" ]; then
    export MERRY_BOT="$project_dir/build/data"
fi
echo "[launch] Data dir: $MERRY_BOT"

# PID of the currently running MerryBot process.
child_pid=""
stopping=false
shutdown_grace_seconds=5

# Stop MerryBot before leaving the supervisor script.  MerryBot is launched as
# a background job and Bash may make background jobs ignore SIGINT, so the
# supervisor must explicitly send the termination signal to the child.
stop_child() {
    if [ "$stopping" = true ]; then
        return
    fi
    stopping=true

    echo "[launch] Interrupted, stopping MerryBot..."

    if [ -n "$child_pid" ]; then
        kill -TERM "$child_pid" 2>/dev/null || true

        # Do not wait indefinitely inside the signal handler.  A graceful
        # shutdown can get stuck while another host component is stopping;
        # force-kill the child after the short grace period in that case.
        for ((i = 0; i < shutdown_grace_seconds * 10; i++)); do
            if ! kill -0 "$child_pid" 2>/dev/null; then
                break
            fi
            sleep 0.1
        done
        if kill -0 "$child_pid" 2>/dev/null; then
            echo "[launch] MerryBot did not stop gracefully, forcing exit"
            kill -KILL "$child_pid" 2>/dev/null || true
        fi

        wait "$child_pid" 2>/dev/null || true
        child_pid=""
    fi

    echo "[launch] Exiting"
    exit 0
}

# Handle Ctrl+C and normal process termination.  MerryBot is started in the
# background below so Bash can run this trap immediately while it is running.
trap stop_child INT TERM

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

    ./MerryBot &
    child_pid=$!
    wait "$child_pid"
    exit_code=$?
    child_pid=""

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
