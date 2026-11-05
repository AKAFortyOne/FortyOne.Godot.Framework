// 对继承自GodotObject的类执行自动注册

using FortyOne.SourceGenerators.Helpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SourceGenerator.Helpers;
using System.Collections.Immutable;
using System.Text;

namespace FortyOne.SourceGenerators
{
	[Generator]
	public class ScriptAutoRegisterGenerator : IIncrementalGenerator
	{
		private record ClassInfo(string NamespaceName, string ClassName);

		public void Initialize(IncrementalGeneratorInitializationContext context)
		{
			// if (!System.Diagnostics.Debugger.IsAttached)
			// 	System.Diagnostics.Debugger.Launch();
			
			var rawClassInfos = context.SyntaxProvider.CreateSyntaxProvider(
				predicate: (syntaxNode, _) => syntaxNode is ClassDeclarationSyntax,
				transform: (syntaxContext, _) => {
					if (syntaxContext.SemanticModel.GetDeclaredSymbol(syntaxContext.Node) is not INamedTypeSymbol classSymbol || !IsInheritGodotObject(classSymbol)) return null;
					return new ClassInfo(classSymbol.ContainingNamespace.ToDisplayString(), classSymbol.Name);
				}
			);
			
			context.RegisterSourceOutput(rawClassInfos.Collect(), (sourceProductionContext, rawClassInfosBatch) => {
				var validClassInfos = Enumerable.OfType<ClassInfo>(rawClassInfosBatch).ToImmutableArray();

				if (!validClassInfos.Any())
				{
					GeneratorHelper.ReportDiagnostic(sourceProductionContext,
						"SG002", "No class that inherits from GodotObject found", 
						"No class that inherits from GodotObject were collected",
						DiagnosticSeverity.Warning);
					return;
				}
				
				
				IndentedStringBuilder sourceCode = new IndentedStringBuilder();
				var groupClasses = validClassInfos.GroupBy(info => info.NamespaceName);

				foreach (var groupClass in groupClasses)
				{
					string uniqueHint = $"{groupClass.First().NamespaceName}_ScriptAutoRegister.g.cs";
					BuildAutoRegister(sourceCode, groupClass);
					sourceProductionContext.AddSource(uniqueHint, SourceText.From(sourceCode.ToString(),  Encoding.UTF8));
					sourceCode.Clear();
				}
			});
		}

		private static bool IsInheritGodotObject(INamedTypeSymbol classSymbol)
		{
			var currentBaseType = classSymbol.BaseType;
			
			while (currentBaseType != null)
			{
				if (currentBaseType.ToDisplayString() == "Godot.GodotObject") return true;
				currentBaseType = currentBaseType.BaseType;
			}

			return false;
		}

		private static void BuildAutoRegister(IndentedStringBuilder stringBuilder, IGrouping<string, ClassInfo> groupClass)
		{
			stringBuilder
				.AppendLine("using FortyOne.Tools;")
				.AppendLine()
				.AppendLine("#pragma warning disable CS0618 // Type or member is obsolete")
				.AppendLine();

			stringBuilder.AppendLine($"namespace {groupClass.First().NamespaceName}");
			using (stringBuilder.CodeBlock())
			{
				foreach (var classInfo in groupClass)
				{
					stringBuilder
						.AppendLine("[Lifecycle.Watcher]")
						.AppendLine($"public partial class {classInfo.ClassName}");
					using (stringBuilder.CodeBlock())
					{
						stringBuilder
							.AppendLine("[Lifecycle.ConstructCallback]")
							.AppendLine($"private static void ConstructorCallback({classInfo.ClassName} instance, ulong instanceId)");
						using (stringBuilder.CodeBlock())
						{
							stringBuilder
								.AppendLine("ScriptInstanceRegistry.Register(instance, instanceId);")
								
								.AppendLine("EventLifecycleManager.SetupInstanceEventBindings(instance);");
						}
						stringBuilder.AppendLine();
						
						stringBuilder
							.AppendLine("[Lifecycle.PredeleteCallback]")
							.AppendLine($"private static void PredeleteCallback(ulong instanceId)");
						using (stringBuilder.CodeBlock())
						{
							stringBuilder.AppendLine("if (ScriptInstanceRegistry.Unregister(instanceId))");
							using (stringBuilder.CodeBlock())
							{
								stringBuilder.AppendLine("EventLifecycleManager.ExecuteAndRemoveCleanupTasks(instanceId);");
							}
						}
					}
					stringBuilder.AppendLine();
				}
			}
		}
	}
}
