using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using SourceGenerator.Helpers;
using System.Collections.Immutable;
using System.Text;

// 避免record报错：
// The predefined type 'System.Runtime.CompilerServices.IsExternalInit' must be defined or imported in order to declare init-only setter.
namespace System.Runtime.CompilerServices
{
	internal static class IsExternalInit { }
}

namespace FortyOne.SourceGenerators.Helpers
{
	public static class GeneratorHelper
	{
		public readonly record struct ParsedParameter(string OriginalText, string TypeName, string ParameterName, bool IsFirst);
		
		public static ImmutableArray<ParsedParameter> ProcessParameters(ImmutableArray<string> originalParameters)
		{
			List<ParsedParameter> parsedParameters = [];
			for (int i = 0; i < originalParameters.Length; i++)
			{
				string originalText = originalParameters[i];
				var parts = originalText.Split(' ');
				parsedParameters.Add(new ParsedParameter(originalText, parts.First(), parts.Last(), i == 0));
			}
			return parsedParameters.ToImmutableArray();
		}
	
		public static string GetParametersList(ImmutableArray<ParsedParameter> parameters, bool withType = true)
		{
			string result = string.Empty;
			if (parameters.Length <= 0) return result;

			foreach (var parameter in parameters)
			{
				if (!parameter.IsFirst)
				{
					result += ", ";
				}
				
				if (withType)
				{
					result += parameter.OriginalText;
				}
				else
				{
					result += parameter.ParameterName;
				}
			}

			return result;
		}

		public static string FirstToUpper(string original)
		{
			char firstUpper = char.ToUpperInvariant(original[0]);
			string restStr = original.Substring(1, original.Length - 1);
			return firstUpper + restStr;
		}

		public static void CreateNewFile(this SourceProductionContext sourceProductionContext, string fileName, IndentedStringBuilder stringBuilder)
			=> sourceProductionContext.AddSource(fileName, SourceText.From(stringBuilder.ToString(),  Encoding.UTF8));
		
		public static void ReportDiagnostic(
			SourceProductionContext sourceProductionContext,
			string id,
			string title,
			string messageFormat,
			DiagnosticSeverity location
		)
		{
			sourceProductionContext.ReportDiagnostic(
				Diagnostic.Create(new DiagnosticDescriptor(
						id, title, messageFormat, "FortyOne.SourceGenerators", location, true),
					Location.None)
			);
		}
		
		public static TValue GetOrAdd<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, Func<TKey, TValue> valueFactory) where TKey : notnull
		{
			if (dictionary.TryGetValue(key, out var value)) return value;
			
			value = valueFactory(key);
			dictionary.Add(key, value);
			return value;
		}
	}
}