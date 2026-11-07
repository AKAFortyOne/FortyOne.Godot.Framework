using FortyOne.SourceGenerators.Helpers;
using Microsoft.CodeAnalysis;
using SourceGenerator.Helpers;
using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace FortyOne.SourceGenerators
{
	[Generator]
	public class ScriptSceneNodeTreeGenerator : IIncrementalGenerator
	{
		public void Initialize(IncrementalGeneratorInitializationContext context)
		{
			// if (!System.Diagnostics.Debugger.IsAttached)
			// 	System.Diagnostics.Debugger.Launch();
			
			var projectDirProvider = context.AnalyzerConfigOptionsProvider.Select((options, _) => options.GlobalOptions.TryGetValue("build_property.projectdir", out var projectDir) ? projectDir : string.Empty);
		
			var sceneFiles = context.AdditionalTextsProvider
				.Where(static file => file.Path.EndsWith(".tscn"));
			
			context.RegisterSourceOutput(sceneFiles.Collect().Combine(projectDirProvider), (sourceProductionContext, source) => {
				var allScenes = source.Left;
				var projectDir = source.Right;
				
				IndentedStringBuilder resourceBuilder = new IndentedStringBuilder();
				BuildSceneNodeTree(resourceBuilder, allScenes, projectDir);
				sourceProductionContext.CreateNewFile("ScriptSceneNodeTree.g.cs", resourceBuilder);
			});
		}

		private static void BuildSceneNodeTree(IndentedStringBuilder stringBuilder, ImmutableArray<AdditionalText> allScenes, string projectDir)
		{
			var regex = new Regex(@"\[node name=""(?<name>[^""]+)""(?: type=""(?<type>[^""]+)"")?(?: parent=""(?<parent>[^""]+)"")?[^\]]*\](?=(?:\r?\n(?!\[).*)*?\r?\nunique_name_in_owner\s*=\s*true)", RegexOptions.Compiled);
			
			foreach (var scene in allScenes)
			{
				var tscnContent = scene.GetText();
				if (tscnContent is null) continue;
				var matches = regex.Matches(tscnContent.ToString());

				stringBuilder.AppendLine($"// {scene.Path}");
				foreach (Match match in matches)
				{
					stringBuilder.AppendLine($"// {match.Value}");
				}
				stringBuilder.AppendLine();
			}
		}
	}
}
