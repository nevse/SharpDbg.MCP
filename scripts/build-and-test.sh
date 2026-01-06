#!/bin/bash
# Build and test SharpDbg MCP Server
# Usage: ./scripts/build-and-test.sh

set -e  # Exit on error

echo "=== Building SharpDbg MCP Server ==="
dotnet build src/SharpDbg.MCP/SharpDbg.MCP.csproj

echo ""
echo "=== Running Tests ==="
dotnet test tests/SharpDbg.MCP.Tests/SharpDbg.MCP.Tests.csproj

echo ""
echo "✅ Build and test complete!"
