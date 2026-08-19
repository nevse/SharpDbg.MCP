# Build Configuration Reference

This document describes the build configuration for the DotnetDebugger.Mcp repository.

## Centralized Package Management

This repository uses [Central Package Management (CPM)](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management) to manage NuGet package versions centrally.

### How It Works

- **Directory.Packages.props** - Defines all package versions in one place
- **Project files** - Reference packages without specifying versions

### Directory.Packages.props

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>

  <ItemGroup>
    <!-- All package versions defined here -->
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.1" />
    <PackageVersion Include="ModelContextProtocol" Version="0.5.0-preview.1" />
    <PackageVersion Include="Microsoft.VisualStudio.Shared.VSCodeDebugProtocol" Version="18.0.10427.1" />
    <PackageVersion Include="Markdig" Version="0.44.0" />
    <PackageVersion Include="MSTest" Version="4.0.2" />
  </ItemGroup>
</Project>
```

The debugger itself is not among them. It is [clrdbg](https://github.com/JaneySprings/clrdbg), which
publishes no package: the repository carries it as a git submodule and builds it from source, as
described under [The Debug Adapter Build](#the-debug-adapter-build). The
`Microsoft.VisualStudio.Shared.VSCodeDebugProtocol` package provides the Debug Adapter Protocol types
the server uses to talk to it.

### Project File Pattern

In project files (.csproj), packages are referenced WITHOUT version attributes:

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Extensions.Hosting" />
  <PackageReference Include="ModelContextProtocol" />
</ItemGroup>
```

The version is automatically resolved from Directory.Packages.props.

### Adding New Packages

1. Add version to `Directory.Packages.props`:
   ```xml
   <PackageVersion Include="NewPackage" Version="1.0.0" />
   ```

2. Reference in project file without version:
   ```xml
   <PackageReference Include="NewPackage" />
   ```

### Updating Package Versions

Update the version in **one place** - Directory.Packages.props:

```xml
<PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.2" />
```

All projects will use the updated version after restore.

## Common Build Properties

**Directory.Build.props** defines properties shared across all projects:

### Compilation Settings
- **TargetFramework**: net10.0
- **LangVersion**: latest
- **ImplicitUsings**: enabled
- **Nullable**: enabled

### Code Quality
- **EnforceCodeStyleInBuild**: true
- Follows .editorconfig rules

### Metadata
- **Authors**: SharpDbg MCP Contributors
- **License**: MIT
- **Copyright**: Copyright © 2026

### Per-Project Properties

Projects can override or add properties as needed:

**SharpDbg.MCP.csproj** (Main project):
```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
</PropertyGroup>
```

**SharpDbg.MCP.Tests.csproj** (Test project):
```xml
<PropertyGroup>
  <IsPackable>false</IsPackable>
  <IsTestProject>true</IsTestProject>
</PropertyGroup>
```

## The Debug Adapter Build

The debugger is [clrdbg](https://github.com/JaneySprings/clrdbg), a git submodule at
`external/clrdbg`. Because it publishes no package, the checkout is the dependency and the build
compiles it. Clone this repository with `git clone --recurse-submodules`, or run
`git submodule update --init --recursive` in an existing clone. A build without the submodule fails
with an error that says which path is missing and how to get it.

The server starts the adapter as a child process, `dotnet <output directory>/clrdbg/clrdbg.dll`, and
speaks the Debug Adapter Protocol over its standard input and output, so the adapter is a binary the
build has to produce rather than a reference it can link. Running the debugger in a process of its own
means a native crash inside `libmscordbi` takes down the adapter rather than the server, and on macOS
the `dotnet` muxer carries the entitlement a debugger needs.

### BuildClrdbgAdapter

**Directory.Build.targets** defines the `BuildClrdbgAdapter` target. It runs before `Build` in every
project that sets `NeedsClrdbgAdapter`, which is the server and the test project. The target builds
the adapter from the clrdbg checkout and copies the output to `$(OutDir)clrdbg/`, where the server looks
for it. The adapter is built framework-dependent, and that is what lets one build serve every
platform: its `runtimes/` folder carries the native DbgShim assets for each supported runtime
identifier.

### ClrdbgSourcePath

`ClrdbgSourcePath`, defined in **Directory.Build.props**, says which clrdbg checkout to build. It
defaults to the `external/clrdbg` submodule, which is what CI builds. To build against a clone of your
own, such as a fork carrying a debugger fix, override it with an environment variable of the same name.
That applies to every command in the shell:

```bash
export ClrdbgSourcePath=/path/to/clrdbg
dotnet build
```

It can also be passed to a single command:

```bash
dotnet build -p:ClrdbgSourcePath=/path/to/clrdbg
```

### PackClrdbgAdapter

**SharpDbg.MCP.csproj** defines the `PackClrdbgAdapter` target, which puts the adapter in the package
under `tools/net10.0/any/clrdbg/`, beside the server that starts it. The native DbgShim assets under
`runtimes/` cover every supported platform, so a single package works everywhere.

## Build Commands

```bash
# Restore packages (reads Directory.Packages.props)
dotnet restore

# Build all projects
dotnet build

# Run tests
dotnet test

# Build specific project
dotnet build src/SharpDbg.MCP/SharpDbg.MCP.csproj

# Using helper scripts
./scripts/build-and-test.sh
```

## Solution Structure

```
SharpDbg.MCP/
├── Directory.Build.props         # Common build properties
├── Directory.Packages.props       # Centralized package versions
├── Directory.Build.targets        # Builds the debug adapter from clrdbg
├── SharpDbg.MCP.slnx             # Solution file (XML format)
├── .editorconfig                  # Code style rules
├── .gitignore                     # Git ignore patterns
├── external/
│   └── clrdbg/                   # Debugger submodule, built from source
├── src/
│   └── SharpDbg.MCP/             # Main project
└── tests/
    └── SharpDbg.MCP.Tests/       # Test project
```

## Benefits of This Approach

1. **Single Source of Truth** - All versions in one file
2. **Consistency** - All projects use same package versions
3. **Easy Updates** - Update one place, affects all projects
4. **Reduced Duplication** - Common properties defined once
5. **Better IDE Support** - Modern tooling understands CPM

## References

- [Central Package Management Documentation](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management)
- [Customize Your Build (Directory.Build.props)](https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-your-build)
- [MSBuild Properties Reference](https://learn.microsoft.com/en-us/visualstudio/msbuild/common-msbuild-project-properties)
