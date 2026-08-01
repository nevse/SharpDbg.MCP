#!/bin/bash
# Run the integration tests: they attach a real debugger to a live child process.
# Usage: ./scripts/test-integration.sh
set -e

echo "=== Building solution ==="
dotnet build SharpDbg.MCP.slnx

echo ""
echo "=== Running integration tests ==="
dotnet test SharpDbg.MCP.slnx --no-build --filter "TestCategory=Integration" --verbosity normal
