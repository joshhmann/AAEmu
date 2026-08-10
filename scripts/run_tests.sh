#!/bin/bash
# Canonical verify entry point for `hermes verify` (project-facts detection
# recognizes scripts/run_tests.sh). The real gate is scripts/gate.sh —
# Release build + in-game script compiler-check + full unit suite.
cd "$(dirname "$0")/.." || exit 1
exec ./scripts/gate.sh "$@"
