#!/bin/sh
set -eu

# Run from the repository directory even when invoked from another location.
SCRIPT_DIR=$(dirname -- "$0")
CDPATH= cd -- "$SCRIPT_DIR"

command -v git >/dev/null 2>&1 || { printf '%s\n' 'git is required.' >&2; exit 1; }
command -v docker >/dev/null 2>&1 || { printf '%s\n' 'docker is required.' >&2; exit 1; }
docker compose version >/dev/null

printf '%s\n' 'Pulling latest code...'
git pull --ff-only

printf '%s\n' 'Building and starting containers...'
docker compose up -d --build

# Cleanup only runs after pull, build, and container startup have succeeded.
# These commands clean unused resources across the entire Docker daemon.
printf '%s\n' 'Cleaning unused Docker build cache...'
docker builder prune --all --force

printf '%s\n' 'Cleaning unused Docker images...'
docker image prune --all --force

docker compose ps
printf '%s\n' 'Update complete.'
