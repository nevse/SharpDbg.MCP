#!/bin/bash
# Manually test the MCP server with debug logging
# Usage: ./scripts/test-server.sh

echo "=== Starting SharpDbg MCP Server (Debug Mode) ==="
echo "Press Ctrl+C to stop"
echo ""

export SHARPDBG_LOG_LEVEL="Debug"
export SHARPDBG_ENABLE_DIAGNOSTICS="true"

dotnet run --project src/SharpDbg.MCP/SharpDbg.MCP.csproj
