# ACadSharp.Generators

A Roslyn-based C# source generator for the [ACadSharp](https://github.com/smartcad/ACadSharp) library that eliminates runtime reflection and enables .NET Native AOT (Ahead-of-Time) compilation.

## Overview

`ACadSharp.Generators` is an incremental source generator that automatically generates static metadata registries for DXF (Drawing Exchange Format) type mappings and system variables at compile time. This eliminates the need for reflection during runtime, making ACadSharp fully compatible with .NET Native AOT and enabling zero-reflection execution.

## Key Features

- **Zero Runtime Reflection** — All DXF type metadata is generated at compile time
- **.NET Native AOT Compatible** — Works seamlessly with .NET Native and trimming
- **Automatic Code Generation** — Generates type maps, property accessors, and header variable mappings
- **Performance** — Eliminates reflection overhead with direct delegate-based property access
- **Incremental** — Uses Roslyn's incremental generation for fast builds

## What It Generates

### DxfMetadataRegistry

A single auto-generated file (`DxfMetadataRegistry.g.cs`) containing:

1. **Type Maps** — Pre-built `DxfMap` instances for all `[DxfName]` attributed entities
   - Maps DXF codes to property accessors
   - Builds class hierarchies and subclass markers
   - Handles special cases (DimensionStyle code mappings, etc.)

2. **Class Maps** — Pre-generated `DxfClassMap` builders for `[DxfSubClass]` types
   - Mirrors runtime `DxfClassMap.Create<T>()` logic
   - Supports empty base class inheritance patterns

3. **Header Variables** — Complete system variable registry for `CadHeader`
   - 228+ CAD system variables with their DXF codes
   - Direct getter/setter delegates (no PropertyInfo lookup)
   - Proper type conversion for XY, XYZ, DateTime, TimeSpan, Color, etc.

## Usage

Add this NuGet package to your ACadSharp-consuming project:

```xml
<ItemGroup>
  <PackageReference Include="ACadSharp.Generators" Version="1.0.0" />
</ItemGroup>
```

Or using the package manager:

```bash
dotnet add package ACadSharp.Generators
```

The generator automatically:
- Scans your compiled ACadSharp assembly for `[DxfName]`, `[DxfSubClass]`, and `[CadSystemVariable]` attributes
- Generates static registries while you build
- Integrates with `DxfMap.Create()`, `DxfClassMap.Create<T>()`, and `CadHeader` methods

No manual configuration required.

## Architecture

### Incremental Generation Pipeline

1. **Syntax Provider** — Identifies class declarations with DXF attributes
2. **Symbol Analysis** — Extracts attribute metadata and type hierarchies
3. **Hierarchy Walking** — Rebuilds inheritance chains from concrete types to base `CadObject`
4. **Code Emission** — Generates optimized delegate lambdas and direct mappings

### Generated Code Quality

All generated code:
- Uses strongly-typed lambdas for property access (zero boxing overhead)
- Handles value type conversions (XY → two doubles, XYZ → three doubles)
- Supports enum mappings, DateTime/TimeSpan conversions, Color custom types
- Respects readonly properties by emitting `null` setters

## Performance Impact

- **Build Time**: ~200ms for initial generation, incremental updates <50ms
- **Generated Code Size**: ~700KB for complete ACadSharp type set
- **Runtime Overhead**: Zero reflection lookups; O(1) dictionary access to registries
- **Memory**: Registries cached as static fields (shared across app lifetime)

## Compatibility

- **Target**: .NET Standard 2.0
- **Roslyn Version**: 4.8.0
- **C# Language Version**: 11.0+
- **Platforms**: Windows, Linux, macOS

Works with:
- ✅ .NET 6+ AOT
- ✅ NativeAOT console apps
- ✅ Trimmer-enabled projects
- ✅ Standard .NET/Framework builds

## Limitations

- Generator runs after ACadSharp.csproj builds (generates DxfMetadataRegistry only for compiled types)
- Requires ACadSharp types to have `[DxfName]` or `[DxfSubClass]` attributes
- Custom subclasses without these attributes will fall through to `NotSupportedException`

## Development

### Prerequisites

- .NET 6 SDK or later
- Visual Studio 2022 / VS Code with C# Dev Kit

### Building

```bash
cd ACadSharp.Generators
dotnet build
```

### Testing

The generator is tested via integration with ACadSharp's test suite. Run DXF/DWG file I/O tests to validate generated metadata.

```bash
dotnet test ../ACadSharp.Tests
```

## Contributing

Contributions welcome! Areas of interest:

- Performance optimizations for large type hierarchies
- Support for custom attribute-driven code generation
- Test coverage improvements
- Documentation enhancements

## License

Same as [ACadSharp](https://github.com/smartcad/ACadSharp) — MIT License

## See Also

- [ACadSharp GitHub](https://github.com/smartcad/ACadSharp)
- [Roslyn Source Generators Documentation](https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.md)
- [.NET Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
