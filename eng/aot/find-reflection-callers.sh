#!/usr/bin/env bash
# Usage: eng/aot/find-reflection-callers.sh <publish-dir> <type-name-substring>
# Reads assembly metadata (TypeRef, MemberRef, TypeSpec, base types); no string scan.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
dotnet run "$here/find-reflection-callers.cs" -- "$1" "$2"
