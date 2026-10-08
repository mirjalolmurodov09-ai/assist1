#!/usr/bin/env bash
# Builds and tests everything that can be built on any OS (core, mock Teacher, tests, simulator).
# The WPF app and the installer are Windows-only: use build/publish.ps1 on Windows (or the GitHub Actions workflow).
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build src/StudentAgent.Core -c Release
dotnet build tools/AgentSimulator -c Release
dotnet test tests/ClassroomControl.Tests -c Release
