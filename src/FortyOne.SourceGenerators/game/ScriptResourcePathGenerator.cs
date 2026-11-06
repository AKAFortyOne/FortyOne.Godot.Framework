using FortyOne.SourceGenerators.Helpers;
using Microsoft.CodeAnalysis;
using SourceGenerator.Helpers;
using System.Collections.Immutable;

namespace FortyOne.SourceGenerators
{
	[Generator]
	public class ScriptResourcePathGenerator : IIncrementalGenerator
	{
		public void Initialize(IncrementalGeneratorInitializationContext context)
		{
			// if (!System.Diagnostics.Debugger.IsAttached)
			// 	System.Diagnostics.Debugger.Launch();
			
			var projectDirProvider = context.AnalyzerConfigOptionsProvider.Select((options, _) => options.GlobalOptions.TryGetValue("build_property.projectdir", out var projectDir) ? projectDir : string.Empty);
		
			var resourceFiles = context.AdditionalTextsProvider
				.Where(static file => file.Path.EndsWith(".tres"));
			
			context.RegisterSourceOutput(resourceFiles.Collect().Combine(projectDirProvider), (sourceProductionContext, source) => {
				var allResources = source.Left;
				var projectDir = source.Right;

				IndentedStringBuilder resourceBuilder = new IndentedStringBuilder();
				BuildResourcePaths(resourceBuilder, allResources, projectDir);
				sourceProductionContext.CreateNewFile("ScriptResourcePaths.g.cs", resourceBuilder);
			});
		}

		private class DirectoryNode(string name)
		{
			private string Name { get; } = name;

			private Dictionary<string, DirectoryNode> SubDirectories { get; } = [];
			private List<(string FieldName, string ResPath)> Files { get; } = [];
			
			public static DirectoryNode CreateDirectoryTree(ImmutableArray<AdditionalText> allResources, string projectDir)
			{
				var rootNode = new DirectoryNode("GodotResources");

				foreach (var file in allResources)
				{
					var path = file.Path.Replace(projectDir, string.Empty).Replace('\\', '/');
					if (path.EndsWith("/"))
					{
						path = path.Substring(0, path.Length - 1);
					}
					string resourcePath = "res://" + path;
					var parts = path.Split('/');
				
					var currentNode = rootNode;
					for (int i = 0; i < parts.Length - 1; i++)
					{
						string dirName = GeneratorHelper.FirstToUpper(parts[i]);
						currentNode = currentNode.SubDirectories.GetOrAdd(dirName, _ => new DirectoryNode(dirName));
					}

					string fileName = parts.Last().Replace(".tres", string.Empty);
					string fieldName = GeneratorHelper.FirstToUpper(fileName);
					currentNode.Files.Add((fieldName, resourcePath));
				}
				return rootNode;
			}
		
			public static void RenderNode(IndentedStringBuilder stringBuilder, DirectoryNode node)
			{
				stringBuilder.AppendLine($"public static partial class {node.Name}");
				using (stringBuilder.CodeBlock())
				{
					foreach (var file in node.Files)
					{
						stringBuilder.AppendLine($"public const string {file.FieldName} = \"{file.ResPath}\";");
					}
					if (node.Files.Count > 0 && node.SubDirectories.Values.Count > 0)
					{
						stringBuilder.AppendLine();
					}

					foreach (var subNode in node.SubDirectories.Values)
					{
						RenderNode(stringBuilder, subNode);
					}
				}
			}
		}
		
		private static void BuildResourcePaths(IndentedStringBuilder stringBuilder, ImmutableArray<AdditionalText> allResources, string projectDir)
		{
			var rootNode = DirectoryNode.CreateDirectoryTree(allResources, projectDir);
			
			stringBuilder.AppendLine("namespace FortyOne.Tools");
			using (stringBuilder.CodeBlock())
			{
				DirectoryNode.RenderNode(stringBuilder, rootNode);
			}
		}
	}
}
