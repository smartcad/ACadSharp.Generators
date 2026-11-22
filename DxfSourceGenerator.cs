using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;

namespace ACadSharp.Generators
{
	[Generator]
	public class DxfSourceGenerator : IIncrementalGenerator
	{
		private const string DxfNameAttributeFqn = "ACadSharp.Attributes.DxfNameAttribute";
		private const string DxfSubClassAttributeFqn = "ACadSharp.Attributes.DxfSubClassAttribute";
		private const string DxfCodeValueAttributeFqn = "ACadSharp.Attributes.DxfCodeValueAttribute";
		private const string DxfCollectionCodeValueAttributeFqn = "ACadSharp.Attributes.DxfCollectionCodeValueAttribute";
		private const string CadSystemVariableAttributeFqn = "ACadSharp.Attributes.CadSystemVariableAttribute";
		private const string CadObjectFqn = "ACadSharp.CadObject";
		private const string CadHeaderFqn = "ACadSharp.Header.CadHeader";
		private const string DimensionStyleFqn = "ACadSharp.Tables.DimensionStyle";

		public void Initialize(IncrementalGeneratorInitializationContext context)
		{
			var candidateTypes = context.SyntaxProvider.CreateSyntaxProvider(
				predicate: static (node, _) => node is ClassDeclarationSyntax c &&
					(c.AttributeLists.Count > 0 || c.Identifier.Text == "CadHeader"
						// Classes read through DxfClassMap without a class attribute (CadValue, TableEntity.Cell)
						|| c.Members.OfType<PropertyDeclarationSyntax>().Any(p => p.AttributeLists.Count > 0)),
				transform: static (ctx, ct) => GetCandidateTypeNameOrNull(ctx, ct))
				.Where(static name => name is not null);

			var compilationAndCandidates = candidateTypes.Collect().Combine(context.CompilationProvider);

			context.RegisterSourceOutput(compilationAndCandidates, static (spc, data) =>
			{
				Execute(spc, data.Left, data.Right);
			});
		}

		private static string GetCandidateTypeNameOrNull(GeneratorSyntaxContext ctx, CancellationToken ct)
		{
			if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is not INamedTypeSymbol symbol)
				return null;

			foreach (var attr in symbol.GetAttributes())
			{
				string fqn = attr.AttributeClass?.ToDisplayString();
				if (fqn == DxfNameAttributeFqn || fqn == DxfSubClassAttributeFqn)
					return GetMetadataName(symbol);
			}

			if (GetMetadataName(symbol) == CadHeaderFqn)
				return CadHeaderFqn;

			// A class whose own properties carry DXF codes: DxfClassMap.Create maps it (with a name given
			// by the caller when the class has no DxfSubClass marker).
			foreach (var property in symbol.GetMembers().OfType<IPropertySymbol>())
			{
				foreach (var attr in property.GetAttributes())
				{
					string fqn = attr.AttributeClass?.ToDisplayString();
					if (fqn == DxfCodeValueAttributeFqn || fqn == DxfCollectionCodeValueAttributeFqn)
						return GetMetadataName(symbol);
				}
			}

			return null;
		}

		private static string GetMetadataName(INamedTypeSymbol symbol)
		{
			// Nested types are Outer+Inner in metadata (TableEntity+Cell).
			string name = symbol.MetadataName;
			for (var containing = symbol.ContainingType; containing != null; containing = containing.ContainingType)
				name = containing.MetadataName + "+" + name;
			if (symbol.ContainingNamespace.IsGlobalNamespace)
				return name;
			return symbol.ContainingNamespace.ToDisplayString() + "." + name;
		}

		private static void Execute(SourceProductionContext spc, ImmutableArray<string> candidateNames, Compilation compilation)
		{
			if (candidateNames.IsDefaultOrEmpty) return;

			var distinctNames = candidateNames.Distinct().ToList();
			var dxfNameTypes = new List<INamedTypeSymbol>();
			INamedTypeSymbol headerType = null;
			var cadObjectSymbol = compilation.GetTypeByMetadataName(CadObjectFqn);
			var dimensionStyleSymbol = compilation.GetTypeByMetadataName(DimensionStyleFqn);
			var dxfNameAttrSymbol = compilation.GetTypeByMetadataName(DxfNameAttributeFqn);
			var dxfSubClassAttrSymbol = compilation.GetTypeByMetadataName(DxfSubClassAttributeFqn);
			var dxfCodeValueAttrSymbol = compilation.GetTypeByMetadataName(DxfCodeValueAttributeFqn);
			var dxfCollectionCodeValueAttrSymbol = compilation.GetTypeByMetadataName(DxfCollectionCodeValueAttributeFqn);
			var cadSystemVarAttrSymbol = compilation.GetTypeByMetadataName(CadSystemVariableAttributeFqn);

			if (cadObjectSymbol == null || dxfNameAttrSymbol == null) return;

			var candidateSymbols = new List<INamedTypeSymbol>();
			foreach (var name in distinctNames)
			{
				var symbol = compilation.GetTypeByMetadataName(name);
				if (symbol == null) continue;
				candidateSymbols.Add(symbol);

				if (name == CadHeaderFqn)
				{
					headerType = symbol;
					continue;
				}

				if (HasAttribute(symbol, dxfNameAttrSymbol))
					dxfNameTypes.Add(symbol);
			}

			// Collect types that also have a concrete DxfSubClass marker (for DxfClassMap.Create<T> replacement)
			var subclassTypes = new List<INamedTypeSymbol>();
			if (dxfSubClassAttrSymbol != null)
			{
				foreach (var type in dxfNameTypes)
				{
					var subclassAttr = GetAttribute(type, dxfSubClassAttrSymbol);
					if (subclassAttr != null && subclassAttr.ConstructorArguments.Length > 0
						&& subclassAttr.ConstructorArguments[0].Value is string sMark && sMark != null)
					{
						subclassTypes.Add(type);
					}
				}
			}

			// DxfClassMap.Create also maps classes without DxfName: a DxfSubClass marker of their own, or
			// DXF codes on their properties (CadValue, TableEntity.Cell, read with a caller-given name).
			var classMapTypes = new List<INamedTypeSymbol>(subclassTypes);
			foreach (var type in candidateSymbols)
			{
				if (type.IsGenericType || type.IsStatic || classMapTypes.Contains(type, SymbolEqualityComparer.Default))
					continue;
				var subclassAttr = dxfSubClassAttrSymbol != null ? GetAttribute(type, dxfSubClassAttrSymbol) : null;
				if (subclassAttr != null || GetDxfProperties(type, dxfCodeValueAttrSymbol, dxfCollectionCodeValueAttrSymbol).Count > 0)
					classMapTypes.Add(type);
			}

			// DxfMap.Create is also asked for base types the readers use for a record whose concrete type
			// is decided while reading (POLYLINE and VERTEX ask for Entity). Every CadObject-derived class
			// with a DxfSubClass attribute gets a map, built from its hierarchy like the others.
			var mapTypes = new List<INamedTypeSymbol>(dxfNameTypes);
			foreach (var type in candidateSymbols)
			{
				if (type.IsGenericType || type.IsStatic || mapTypes.Contains(type, SymbolEqualityComparer.Default))
					continue;
				if (dxfSubClassAttrSymbol == null || GetAttribute(type, dxfSubClassAttrSymbol) == null)
					continue;
				for (var t = type.BaseType; t != null; t = t.BaseType)
				{
					if (SymbolEqualityComparer.Default.Equals(t, cadObjectSymbol))
					{
						mapTypes.Add(type);
						break;
					}
				}
			}

			var sb = new StringBuilder(64 * 1024);
			sb.AppendLine("// <auto-generated/>");
			sb.AppendLine("#nullable disable");
			sb.AppendLine("#pragma warning disable CS0612 // Suppress obsolete warnings in generated code");
			sb.AppendLine("using System;");
			sb.AppendLine("using System.Collections.Generic;");
			sb.AppendLine("using ACadSharp.Attributes;");
			sb.AppendLine();
			sb.AppendLine("namespace ACadSharp");
			sb.AppendLine("{");
			sb.AppendLine("    internal static class DxfMetadataRegistry");
			sb.AppendLine("    {");

			// Generate map factories dictionary
			sb.AppendLine("        private static readonly Dictionary<Type, Func<DxfMap>> _mapBuilders = new Dictionary<Type, Func<DxfMap>>");
			sb.AppendLine("        {");
			foreach (var type in mapTypes)
			{
				string fqn = type.ToDisplayString();
				string safeName = fqn.Replace(".", "_").Replace("<", "_").Replace(">", "_").Replace(",", "_");
				sb.AppendLine($"            {{ typeof({fqn}), BuildMap_{safeName} }},");
			}
			sb.AppendLine("        };");
			sb.AppendLine();

			// TryCreateMap method
			sb.AppendLine("        public static bool TryCreateMap(Type type, out DxfMap map)");
			sb.AppendLine("        {");
			sb.AppendLine("            if (_mapBuilders.TryGetValue(type, out var builder))");
			sb.AppendLine("            {");
			sb.AppendLine("                map = builder();");
			sb.AppendLine("                return true;");
			sb.AppendLine("            }");
			sb.AppendLine("            map = null;");
			sb.AppendLine("            return false;");
			sb.AppendLine("        }");
			sb.AppendLine();

			// Class map builders (for DxfClassMap.Create<T> replacement)
			sb.AppendLine("        private static readonly Dictionary<Type, Func<DxfClassMap>> _classMapBuilders = new Dictionary<Type, Func<DxfClassMap>>");
			sb.AppendLine("        {");
			foreach (var type in classMapTypes)
			{
				string fqn2 = type.ToDisplayString();
				string safeName2 = fqn2.Replace(".", "_").Replace("<", "_").Replace(">", "_").Replace(",", "_");
				sb.AppendLine($"            {{ typeof({fqn2}), BuildClassMap_{safeName2} }},");
			}
			sb.AppendLine("        };");
			sb.AppendLine();
			// Types with a DxfSubClass attribute (whatever its marker): DxfClassMap.Create needs a name
			// from the caller for any other type, as the reflection mapping did.
			sb.AppendLine("        private static readonly HashSet<Type> _subClassAttributeTypes = new HashSet<Type>");
			sb.AppendLine("        {");
			foreach (var type in classMapTypes)
			{
				if (dxfSubClassAttrSymbol != null && GetAttribute(type, dxfSubClassAttrSymbol) != null)
					sb.AppendLine($"            typeof({type.ToDisplayString()}),");
			}
			sb.AppendLine("        };");
			sb.AppendLine();
			sb.AppendLine("        public static bool HasSubClassAttribute(Type type) => _subClassAttributeTypes.Contains(type);");
			sb.AppendLine();
			sb.AppendLine("        public static bool TryGetClassMap(Type type, out DxfClassMap classMap)");
			sb.AppendLine("        {");
			sb.AppendLine("            if (_classMapBuilders.TryGetValue(type, out var builder))");
			sb.AppendLine("            {");
			sb.AppendLine("                classMap = builder();");
			sb.AppendLine("                return true;");
			sb.AppendLine("            }");
			sb.AppendLine("            classMap = null;");
			sb.AppendLine("            return false;");
			sb.AppendLine("        }");
			sb.AppendLine();

			// Generate individual builder methods
			foreach (var type in mapTypes)
			{
				GenerateMapBuilder(sb, type, cadObjectSymbol, dimensionStyleSymbol,
					dxfNameAttrSymbol, dxfSubClassAttrSymbol, dxfCodeValueAttrSymbol,
					dxfCollectionCodeValueAttrSymbol);
			}

			// Generate per-type class map builders (mirrors DxfClassMap.Create<T> logic)
			foreach (var type in classMapTypes)
			{
				GenerateClassMapBuilder(sb, type, dxfSubClassAttrSymbol,
					dxfCodeValueAttrSymbol, dxfCollectionCodeValueAttrSymbol, cadObjectSymbol);
			}

			// Generate header variable map
			if (headerType != null && cadSystemVarAttrSymbol != null)
			{
				GenerateHeaderMap(sb, headerType, cadSystemVarAttrSymbol);
			}

			sb.AppendLine("    }");
			sb.AppendLine("}");
			sb.AppendLine("#pragma warning restore CS0612");

			spc.AddSource("DxfMetadataRegistry.g.cs", sb.ToString());
		}

		private static void GenerateMapBuilder(
			StringBuilder sb,
			INamedTypeSymbol typeSymbol,
			INamedTypeSymbol cadObjectSymbol,
			INamedTypeSymbol dimensionStyleSymbol,
			INamedTypeSymbol dxfNameAttrSymbol,
			INamedTypeSymbol dxfSubClassAttrSymbol,
			INamedTypeSymbol dxfCodeValueAttrSymbol,
			INamedTypeSymbol dxfCollectionCodeValueAttrSymbol)
		{
			string fqn = typeSymbol.ToDisplayString();
			string safeName = fqn.Replace(".", "_").Replace("<", "_").Replace(">", "_").Replace(",", "_");
			string dxfName = GetAttributeStringArg(typeSymbol, dxfNameAttrSymbol, 0);

			sb.AppendLine($"        private static DxfMap BuildMap_{safeName}()");
			sb.AppendLine("        {");
			sb.AppendLine("            var map = new DxfMap();");
			if (dxfName != null)
				sb.AppendLine($"            map.Name = \"{EscapeString(dxfName)}\";");
			else
				sb.AppendLine("            map.Name = null;");

			// Walk the type hierarchy from concrete type to CadObject
			// Collect levels: (type, subclassMarker, isEmpty, properties)
			var hierarchy = new List<HierarchyLevel>();
			bool isDimensionStyle = false;

			for (var t = typeSymbol; t != null; t = t.BaseType)
			{
				if (dimensionStyleSymbol != null && SymbolEqualityComparer.Default.Equals(t, dimensionStyleSymbol))
					isDimensionStyle = true;

				bool isCadObject = SymbolEqualityComparer.Default.Equals(t, cadObjectSymbol);

				string subclassMarker = null;
				bool isEmpty = false;
				var subclassAttr = GetAttribute(t, dxfSubClassAttrSymbol);
				if (subclassAttr != null)
				{
					subclassMarker = subclassAttr.ConstructorArguments.Length > 0
						? subclassAttr.ConstructorArguments[0].Value as string
						: null;
					isEmpty = subclassAttr.ConstructorArguments.Length > 1
						&& subclassAttr.ConstructorArguments[1].Value is true;
				}

				var properties = GetDxfProperties(t, dxfCodeValueAttrSymbol, dxfCollectionCodeValueAttrSymbol);

				hierarchy.Add(new HierarchyLevel
				{
					Type = t,
					SubclassMarker = subclassMarker,
					IsEmpty = isEmpty,
					IsCadObject = isCadObject,
					Properties = properties
				});

				if (isCadObject) break;
			}

			// Now generate the code that mirrors DxfMap.Create behavior.
			// The runtime code walks from concrete to CadObject, building SubClasses
			// in reverse order, then reverses them.
			// We'll do the same: walk from concrete to CadObject, track subclass maps.

			// Track subclass maps created during the walk (in walk order = concrete first)
			var subclassNames = new List<string>(); // names in creation order

			for (int i = 0; i < hierarchy.Count; i++)
			{
				var level = hierarchy[i];

				if (level.IsCadObject)
				{
					// CadObject: add properties directly to map
					GeneratePropertiesForTarget(sb, level, "map", cadObjectSymbol, dxfCodeValueAttrSymbol);
				}
				else if (level.IsEmpty)
				{
					// IsEmpty subclass, named or not ([DxfSubClass(null, true)] bases such as CadImageBase):
					// its properties go to the LAST created subclass map, as DxfMap.Create did by reflection
					if (subclassNames.Count > 0)
					{
						string lastVar = SubclassVarName(subclassNames.Count - 1);
						GeneratePropertiesForTarget(sb, level, lastVar, cadObjectSymbol, dxfCodeValueAttrSymbol);
					}

					// Add an empty subclass placeholder when the level has a marker of its own
					if (level.SubclassMarker != null)
					{
						string scVar = SubclassVarName(subclassNames.Count);
						sb.AppendLine($"            var {scVar} = new DxfClassMap(\"{EscapeString(level.SubclassMarker)}\");");
						subclassNames.Add(level.SubclassMarker);
					}
				}
				else if (level.SubclassMarker != null)
				{
					// Non-empty subclass: create a new DxfClassMap and add properties
					string scVar = SubclassVarName(subclassNames.Count);
					sb.AppendLine($"            var {scVar} = new DxfClassMap();");
					sb.AppendLine($"            {scVar}.Name = \"{EscapeString(level.SubclassMarker)}\";");
					GeneratePropertiesForTarget(sb, level, scVar, cadObjectSymbol, dxfCodeValueAttrSymbol);
					subclassNames.Add(level.SubclassMarker);
				}
				// If no subclass attribute and not CadObject, skip (properties are on the type
				// but there's no subclass marker — shouldn't normally happen)
			}

			// Add SubClasses in final order (base first), avoiding a generated LINQ reverse/copy.
			for (int i = subclassNames.Count - 1; i >= 0; i--)
			{
				string scVar = SubclassVarName(i);
				sb.AppendLine($"            map.SubClasses[\"{EscapeString(subclassNames[i])}\"] = {scVar};");
			}

			// DimensionStyle special case
			if (isDimensionStyle)
			{
				sb.AppendLine("            if (map.DxfProperties.ContainsKey(5))");
				sb.AppendLine("            {");
				sb.AppendLine("                map.DxfProperties[105] = map.DxfProperties[5];");
				sb.AppendLine("                map.DxfProperties.Remove(5);");
				sb.AppendLine("            }");
			}

			sb.AppendLine("            return map;");
			sb.AppendLine("        }");
			sb.AppendLine();
		}

		private static void GeneratePropertiesForTarget(
			StringBuilder sb,
			HierarchyLevel level,
			string targetVar,
			INamedTypeSymbol cadObjectSymbol,
			INamedTypeSymbol dxfCodeValueAttrSymbol)
		{
			string declaringTypeFqn = level.Type.ToDisplayString();

			foreach (var prop in level.Properties)
			{
				// Generate each DxfProperty with code -> property mapping
				foreach (int code in prop.Codes)
				{
					string propKey = $"{targetVar}.DxfProperties";

					// Build the DxfCodeValueAttribute constructor call
					string attrCtor;
					if (prop.ReferenceType != 0)
						attrCtor = $"new DxfCodeValueAttribute((DxfReferenceType){prop.ReferenceType}, {string.Join(", ", prop.Codes)})";
					else
						attrCtor = $"new DxfCodeValueAttribute({string.Join(", ", prop.Codes)})";

					// Collection codes
					string collectionCodesExpr = "null";
					if (prop.CollectionCodes != null && prop.CollectionCodes.Length > 0)
					{
						collectionCodesExpr = "new DxfCode[] { " +
							string.Join(", ", prop.CollectionCodes.Select(c => $"(DxfCode){c}")) + " }";
					}

					// Property type
					string propTypeFqn = prop.PropertyType.ToDisplayString();

					// Getter
					string getterExpr = $"o => (({declaringTypeFqn})o).{prop.Name}";

					// Setter
					string setterExpr;
					if (prop.HasSetter)
					{
						// For value types, we need to cast the value appropriately
						string castExpr = GetCastExpression(prop.PropertyType, propTypeFqn);
						setterExpr = $"(o, v) => (({declaringTypeFqn})o).{prop.Name} = {castExpr}";
					}
					else
					{
						setterExpr = "null";
					}

					sb.AppendLine($"            if (!{propKey}.ContainsKey({code}))");
					sb.AppendLine($"                {propKey}[{code}] = new DxfProperty({code}, {attrCtor}, typeof({propTypeFqn}), \"{EscapeString(prop.Name)}\", {getterExpr}, {setterExpr}, {collectionCodesExpr});");
				}
			}
		}

		private static void GenerateClassMapBuilder(
			StringBuilder sb,
			INamedTypeSymbol typeSymbol,
			INamedTypeSymbol dxfSubClassAttrSymbol,
			INamedTypeSymbol dxfCodeValueAttrSymbol,
			INamedTypeSymbol dxfCollectionCodeValueAttrSymbol,
			INamedTypeSymbol cadObjectSymbol)
		{
			string fqn = typeSymbol.ToDisplayString();
			string safeName = fqn.Replace(".", "_").Replace("<", "_").Replace(">", "_").Replace(",", "_");

			var subclassAttr = GetAttribute(typeSymbol, dxfSubClassAttrSymbol);
			string marker = subclassAttr?.ConstructorArguments.Length > 0
				? subclassAttr.ConstructorArguments[0].Value as string
				: null;

			sb.AppendLine($"        private static DxfClassMap BuildClassMap_{safeName}()");
			sb.AppendLine("        {");
			sb.AppendLine("            var map = new DxfClassMap();");
			// The marker as DxfSubClassAttribute.ClassName gives it: null for [DxfSubClass(null, ...)].
			sb.AppendLine(marker == null
				? "            map.Name = null;"
				: $"            map.Name = \"{EscapeString(marker)}\";");

			// Add properties from the concrete type
			var level = new HierarchyLevel
			{
				Type = typeSymbol,
				SubclassMarker = marker,
				IsEmpty = false,
				IsCadObject = false,
				Properties = GetDxfProperties(typeSymbol, dxfCodeValueAttrSymbol, dxfCollectionCodeValueAttrSymbol)
			};
			GeneratePropertiesForTarget(sb, level, "map", cadObjectSymbol, dxfCodeValueAttrSymbol);

			// If direct base has IsEmpty attribute, mirror DxfClassMap.Create<T> behavior
			if (typeSymbol.BaseType != null)
			{
				var baseSubclassAttr = GetAttribute(typeSymbol.BaseType, dxfSubClassAttrSymbol);
				if (baseSubclassAttr != null)
				{
					bool baseIsEmpty = baseSubclassAttr.ConstructorArguments.Length > 1
						&& baseSubclassAttr.ConstructorArguments[1].Value is true;
					if (baseIsEmpty)
					{
						var baseLevel = new HierarchyLevel
						{
							Type = typeSymbol.BaseType,
							SubclassMarker = baseSubclassAttr.ConstructorArguments.Length > 0
								? baseSubclassAttr.ConstructorArguments[0].Value as string : null,
							IsEmpty = true,
							IsCadObject = false,
							Properties = GetDxfProperties(typeSymbol.BaseType, dxfCodeValueAttrSymbol, dxfCollectionCodeValueAttrSymbol)
						};
						GeneratePropertiesForTarget(sb, baseLevel, "map", cadObjectSymbol, dxfCodeValueAttrSymbol);
					}
				}
			}

			sb.AppendLine("            return map;");
			sb.AppendLine("        }");
			sb.AppendLine();
		}

		private static void GenerateHeaderMap(			StringBuilder sb,
			INamedTypeSymbol headerType,
			INamedTypeSymbol cadSystemVarAttrSymbol)
		{
			sb.AppendLine("        private static Dictionary<string, CadSystemVariable> _headerMap;");
			sb.AppendLine();
			sb.AppendLine("        public static Dictionary<string, CadSystemVariable> GetHeaderMap()");
			sb.AppendLine("        {");
			sb.AppendLine("            return new Dictionary<string, CadSystemVariable>(getHeaderMap());");
			sb.AppendLine("        }");
			sb.AppendLine();
			sb.AppendLine("        internal static IReadOnlyDictionary<string, CadSystemVariable> GetHeaderMapInternal()");
			sb.AppendLine("        {");
			sb.AppendLine("            return getHeaderMap();");
			sb.AppendLine("        }");
			sb.AppendLine();
			sb.AppendLine("        private static Dictionary<string, CadSystemVariable> getHeaderMap()");
			sb.AppendLine("        {");
			sb.AppendLine("            if (_headerMap != null) return _headerMap;");
			sb.AppendLine("            var map = new Dictionary<string, CadSystemVariable>();");

			string headerFqn = headerType.ToDisplayString();

			foreach (var member in headerType.GetMembers().OfType<IPropertySymbol>())
			{
				var attr = GetAttribute(member, cadSystemVarAttrSymbol);
				if (attr == null) continue;

				// Extract attribute args
				string varName = null;
				bool isName = false;
				byte referenceType = 0;
				var codes = new List<int>();

				// The CadSystemVariableAttribute has several constructors:
				// (string, params int[])
				// (string, bool, params int[])
				// (string, params DxfCode[])
				// (DxfReferenceType, string, params int[])
				var ctorParams = attr.AttributeConstructor?.Parameters;
				var ctorArgs = attr.ConstructorArguments;

				if (ctorParams.HasValue && ctorArgs.Length > 0)
				{
					var parms = ctorParams.Value;
					// Determine which constructor was used based on the first parameter type
					string firstParamType = parms[0].Type.ToDisplayString();

					if (firstParamType == "ACadSharp.DxfReferenceType" && ctorArgs.Length >= 3)
					{
						// (DxfReferenceType, string, params int[])
						referenceType = Convert.ToByte(ctorArgs[0].Value);
						varName = ctorArgs[1].Value as string;
						foreach (var codeVal in ctorArgs[2].Values)
							codes.Add((int)codeVal.Value);
					}
					else if (firstParamType == "string")
					{
						varName = ctorArgs[0].Value as string;
						if (ctorArgs.Length >= 2)
						{
							string secondParamType = parms.Length > 1 ? parms[1].Type.ToDisplayString() : null;
							if (secondParamType == "bool" && ctorArgs.Length >= 3)
							{
								// (string, bool, params int[])
								isName = (bool)ctorArgs[1].Value;
								foreach (var codeVal in ctorArgs[2].Values)
									codes.Add((int)codeVal.Value);
							}
							else
							{
								// (string, params int[]) or (string, params DxfCode[])
								foreach (var codeVal in ctorArgs[1].Values)
									codes.Add((int)codeVal.Value);
							}
						}
					}
				}

				if (varName == null) continue;

				string propTypeFqn = member.Type.ToDisplayString();

				// Build CadSystemVariableAttribute constructor call
				string attrCtorExpr;
				string codesStr = string.Join(", ", codes);
				if (referenceType != 0)
					attrCtorExpr = $"new CadSystemVariableAttribute((DxfReferenceType){referenceType}, \"{EscapeString(varName)}\", {codesStr})";
				else if (isName)
					attrCtorExpr = $"new CadSystemVariableAttribute(\"{EscapeString(varName)}\", true, {codesStr})";
				else
					attrCtorExpr = $"new CadSystemVariableAttribute(\"{EscapeString(varName)}\", {codesStr})";

				// Getter
				string getterExpr = $"o => (({headerFqn})o).{member.Name}";

				// Setter
				string setterExpr;
				if (member.SetMethod != null)
				{
					string castExpr = GetCastExpression(member.Type, propTypeFqn);
					setterExpr = $"(o, v) => (({headerFqn})o).{member.Name} = {castExpr}";
				}
				else
				{
					setterExpr = "null";
				}

				sb.AppendLine($"            map[\"{EscapeString(varName)}\"] = new CadSystemVariable({attrCtorExpr}, typeof({propTypeFqn}), \"{EscapeString(member.Name)}\", {getterExpr}, {setterExpr});");
			}

			sb.AppendLine("            _headerMap = map;");
			sb.AppendLine("            return _headerMap;");
			sb.AppendLine("        }");
			sb.AppendLine();
		}

		#region Helpers

		private static bool HasAttribute(INamedTypeSymbol type, INamedTypeSymbol attrType)
		{
			return type.GetAttributes().Any(a =>
				SymbolEqualityComparer.Default.Equals(a.AttributeClass, attrType));
		}

		private static AttributeData GetAttribute(ISymbol symbol, INamedTypeSymbol attrType)
		{
			return symbol.GetAttributes().FirstOrDefault(a =>
				SymbolEqualityComparer.Default.Equals(a.AttributeClass, attrType));
		}

		private static string GetAttributeStringArg(INamedTypeSymbol type, INamedTypeSymbol attrType, int argIndex)
		{
			var attr = GetAttribute(type, attrType);
			if (attr == null || attr.ConstructorArguments.Length <= argIndex)
				return null;
			return attr.ConstructorArguments[argIndex].Value as string;
		}

		private static List<PropertyModel> GetDxfProperties(
			INamedTypeSymbol type,
			INamedTypeSymbol dxfCodeValueAttrSymbol,
			INamedTypeSymbol dxfCollectionCodeValueAttrSymbol)
		{
			var result = new List<PropertyModel>();

			foreach (var member in type.GetMembers().OfType<IPropertySymbol>())
			{
				if (member.IsStatic) continue;
				if (member.DeclaredAccessibility != Accessibility.Public) continue;
				// Only consider properties declared on this exact type (DeclaredOnly equivalent)
				if (!SymbolEqualityComparer.Default.Equals(member.ContainingType, type)) continue;

				var attr = GetAttribute(member, dxfCodeValueAttrSymbol);
				if (attr == null) continue;

				byte refType = 0;
				var codes = new List<int>();

				// Parse the constructor args
				var ctorParamsNullable = attr.AttributeConstructor?.Parameters;
				if (!ctorParamsNullable.HasValue) continue;
				var ctorParams = ctorParamsNullable.Value;

				if (ctorParams.Length >= 1 && ctorParams[0].Type.ToDisplayString() == "ACadSharp.DxfReferenceType")
				{
					// (DxfReferenceType, params int[])
					refType = Convert.ToByte(attr.ConstructorArguments[0].Value);
					if (attr.ConstructorArguments.Length > 1)
					{
						foreach (var v in attr.ConstructorArguments[1].Values)
							codes.Add((int)v.Value);
					}
				}
				else
				{
					// (params int[])
					if (attr.ConstructorArguments.Length > 0)
					{
						foreach (var v in attr.ConstructorArguments[0].Values)
							codes.Add((int)v.Value);
					}
				}

				// Collection codes
				int[] collectionCodes = null;
				var collAttr = GetAttribute(member, dxfCollectionCodeValueAttrSymbol);
				if (collAttr != null)
				{
					var collCodes = new List<int>();
					var collCtorParamsNullable = collAttr.AttributeConstructor?.Parameters;
					if (collCtorParamsNullable.HasValue && collCtorParamsNullable.Value.Length >= 1)
					{
						var collCtorParams = collCtorParamsNullable.Value;
						if (collCtorParams[0].Type.ToDisplayString() == "ACadSharp.DxfReferenceType" && collAttr.ConstructorArguments.Length > 1)
						{
							foreach (var v in collAttr.ConstructorArguments[1].Values)
								collCodes.Add((int)v.Value);
						}
						else
						{
							foreach (var v in collAttr.ConstructorArguments[0].Values)
								collCodes.Add((int)v.Value);
						}
					}
					collectionCodes = collCodes.ToArray();
				}

				bool hasSetter = member.SetMethod != null &&
					(member.SetMethod.DeclaredAccessibility == Accessibility.Public ||
					 member.SetMethod.DeclaredAccessibility == Accessibility.Internal ||
					 member.SetMethod.DeclaredAccessibility == Accessibility.Protected ||
					 member.SetMethod.DeclaredAccessibility == Accessibility.ProtectedOrInternal);

				result.Add(new PropertyModel
				{
					Name = member.Name,
					PropertyType = member.Type,
					Codes = codes.ToArray(),
					ReferenceType = refType,
					CollectionCodes = collectionCodes,
					HasSetter = hasSetter
				});
			}

			return result;
		}

		private static string GetCastExpression(ITypeSymbol type, string typeFqn)
		{
			// Generate the cast expression for the setter value parameter
			if (type.TypeKind == TypeKind.Enum)
				return $"({typeFqn})v";
			if (type.IsValueType)
				return $"({typeFqn})v";
			return $"({typeFqn})v";
		}

		private static string SubclassVarName(int index) => $"sc{index}";

		private static string EscapeString(string s)
		{
			if (s == null) return "";
			return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
		}

		#endregion

		#region Data models

		private class HierarchyLevel
		{
			public INamedTypeSymbol Type;
			public string SubclassMarker;
			public bool IsEmpty;
			public bool IsCadObject;
			public List<PropertyModel> Properties;
		}

		private class PropertyModel
		{
			public string Name;
			public ITypeSymbol PropertyType;
			public int[] Codes;
			public byte ReferenceType;
			public int[] CollectionCodes;
			public bool HasSetter;
		}

		#endregion
	}
}
