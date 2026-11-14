#!/usr/bin/env bash
# Usage: eng/aot/find-reflection-callers.sh <publish-dir> <type-name-substring>
# Reads assembly metadata (TypeRef, MemberRef, TypeSpec, base types); no string scan.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
if [ $# -lt 2 ]; then echo "Usage: $0 <publish-dir> <type-name-substring>" >&2; exit 2; fi
dotnet run "$here/find-reflection-callers.cs" -- "${1:-}" "${2:-}"
