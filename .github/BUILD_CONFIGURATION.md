# Build Configuration Reference

This document describes the build configuration for the SharpDbg MCP Server repository.

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
    <PackageVersion Include="Markdig" Version="0.38.0" />
    <PackageVersion Include="MSTest" Version="4.0.1" />
  </ItemGroup>
</Project>
```

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
├── Directory.Build.targets        # Custom build targets (if needed)
├── SharpDbg.MCP.slnx             # Solution file (XML format)
├── .editorconfig                  # Code style rules
├── .gitignore                     # Git ignore patterns
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
