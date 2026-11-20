#!/bin/bash
# Builds and pushes the web version's container image (installer/container/build-container.ps1, needs pwsh and podman).
# Parameters are passed on, e.g.: ./BuildContainer.sh -NoPush    ./BuildContainer.sh -Brand inter
exec pwsh -NoProfile -File "$(dirname "$0")/installer/container/build-container.ps1" "$@"
