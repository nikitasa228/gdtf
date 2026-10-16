#!/bin/bash
# Installs the Candela toolchain in Claude Code cloud sessions: .NET 8 SDK, Mesa lavapipe (software Vulkan, so the
# renderer and its tests run without a GPU), Vulkan tools + validation layers, shader tools, Xvfb + GLFW (editor
# screenshots). Then restores NuGet packages so build/test work offline-fast. Idempotent.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

SUDO=""
if [ "$(id -u)" -ne 0 ]; then SUDO="sudo"; fi
export DEBIAN_FRONTEND=noninteractive

packages=(dotnet-sdk-8.0 mesa-vulkan-drivers vulkan-tools vulkan-validationlayers glslang-tools spirv-tools xvfb libglfw3 libsdl2-2.0-0)
missing=()
for p in "${packages[@]}"; do
  dpkg -s "$p" >/dev/null 2>&1 || missing+=("$p")
done
if [ ${#missing[@]} -gt 0 ]; then
  echo "Installing: ${missing[*]}" >&2
  $SUDO apt-get update -qq >&2
  $SUDO apt-get install -y -qq "${missing[@]}" >&2
fi

if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  {
    echo 'export DOTNET_CLI_TELEMETRY_OPTOUT=1'
    echo 'export DOTNET_NOLOGO=1'
    echo 'export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1'
  } >> "$CLAUDE_ENV_FILE"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

cd "${CLAUDE_PROJECT_DIR:-$(dirname "$0")/../..}"
if [ -f Candela.sln ]; then
  dotnet restore Candela.sln -v q >&2
fi
