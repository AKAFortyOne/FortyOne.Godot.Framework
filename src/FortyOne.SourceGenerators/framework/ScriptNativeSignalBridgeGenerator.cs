using FortyOne.SourceGenerators.Helpers;
using Microsoft.CodeAnalysis;
using SourceGenerator.Helpers;
using System.Collections.Immutable;
using ParsedParameter = FortyOne.SourceGenerators.Helpers.GeneratorHelper.ParsedParameter;

namespace FortyOne.SourceGenerators
{
	[Generator]
	public class ScriptNativeSignalBridgeGenerator : IIncrementalGenerator
	{
		private readonly static object CacheLock = new object();
		private static string _cachedVersion = string.Empty;
		
		private readonly record struct SignalInfo(string SignalName, ImmutableArray<ParsedParameter> Parameters);
		private static ImmutableDictionary<string, ImmutableArray<SignalInfo>> _cachedSignals = ImmutableDictionary<string, ImmutableArray<SignalInfo>>.Empty;
		
		public void Initialize(IncrementalGeneratorInitializationContext context)
		{
			// if (!System.Diagnostics.Debugger.IsAttached)
			// 	System.Diagnostics.Debugger.Launch();
			
			var results = context.CompilationProvider.Select((compilation, cancellationToken) => {
				var assemblySymbol = compilation.SourceModule.ReferencedAssemblySymbols.FirstOrDefault(assemblySymbol => (assemblySymbol.Name == "GodotSharp"));
				if (assemblySymbol is null) return ImmutableDictionary<string, ImmutableArray<SignalInfo>>.Empty;
				
				var currentVersion = assemblySymbol.Identity.Version.ToString();
				lock (CacheLock)
				{
					if (_cachedVersion == currentVersion && !_cachedSignals.IsEmpty)
					{
						return _cachedSignals;
					}
				}
				_cachedSignals = _cachedSignals.Clear();

				var dictBuilder = new Dictionary<string, ImmutableArray<SignalInfo>>();
				var signalBuilder = ImmutableArray.CreateBuilder<SignalInfo>();
				var pending = new Queue<INamespaceSymbol>();
				pending.Enqueue(assemblySymbol.GlobalNamespace);
				while (pending.Count > 0)
				{
					cancellationToken.ThrowIfCancellationRequested();
					var ns = pending.Dequeue();

					foreach (var namedTypeSymbol in ns.GetTypeMembers())
					{
						if (namedTypeSymbol.IsStatic) continue;
						
						var signalNameType = namedTypeSymbol.GetTypeMembers("SignalName").FirstOrDefault();
						if (signalNameType is null) continue;

						foreach (var fieldSymbol in signalNameType.GetMembers().OfType<IFieldSymbol>())
						{
							var eventSymbol = namedTypeSymbol.GetMembers(fieldSymbol.Name).OfType<IEventSymbol>().FirstOrDefault();
							if (eventSymbol is null) continue;

							var parameterSymbols = (eventSymbol.Type as INamedTypeSymbol)?.DelegateInvokeMethod?.Parameters ?? ImmutableArray<IParameterSymbol>.Empty;
							var parameters = parameterSymbols.Select(parameterSymbol => parameterSymbol.ToDisplayString()).ToImmutableArray();
							signalBuilder.Add(new SignalInfo(eventSymbol.Name, GeneratorHelper.ProcessParameters(parameters)));
						}
						
						if (signalBuilder.Count > 0)
						{
							dictBuilder.Add(namedTypeSymbol.Name, signalBuilder.ToImmutable());
						}
						signalBuilder.Clear();
					}
					
					foreach (var child in ns.GetNamespaceMembers())
					{
						pending.Enqueue(child);
					}
				}

				var finalSignals = dictBuilder.ToImmutableDictionary();
				
				lock (CacheLock)
				{
					_cachedVersion = currentVersion;
					_cachedSignals = finalSignals;
				}
				
				return finalSignals;
			});
			
			context.RegisterSourceOutput(results, static (sourceProductionContext, signalInfos) => {
				IndentedStringBuilder scriptNativeSignalBridgeBuilder = new IndentedStringBuilder();
				BuildNativeSignalBridge(scriptNativeSignalBridgeBuilder, signalInfos);
				sourceProductionContext.CreateNewFile("ScriptNativeSignalBridge.g.cs", scriptNativeSignalBridgeBuilder);
				
				IndentedStringBuilder eventLifecycleManagerBuilder = new IndentedStringBuilder();
				BuildEventLifecycleManager(eventLifecycleManagerBuilder, signalInfos);
				sourceProductionContext.CreateNewFile("ScriptEventTemplate.native.g.cs", eventLifecycleManagerBuilder);
			});
		}

		private static void BuildNativeSignalBridge(IndentedStringBuilder stringBuilder, ImmutableDictionary<string, ImmutableArray<SignalInfo>> signalInfos)
		{
			stringBuilder
				.AppendLine("using Godot;")
				.AppendLine("using System;")
				.AppendLine()
				.AppendLine("namespace FortyOne.Tools;")
				.AppendLine()
				.AppendLine("#nullable enable")
				.AppendLine("#pragma warning disable CS0618 // Type or member is obsolete");

			foreach (var signalInfo in signalInfos)
			{
				var className = signalInfo.Key;
				stringBuilder.AppendLine($"public static class {className}Signal");
				using (stringBuilder.CodeBlock())
				{
					stringBuilder
						.AppendLine($"public static FromFirst<Godot.{className}> From(Godot.{className} sender) => new FromFirst<Godot.{className}>(sender);")
						.AppendLine($"public static ToFirst<Godot.{className}> To(GodotObject receiver) => new ToFirst<Godot.{className}>(null, receiver);")
						.AppendLine();
					
					foreach (SignalInfo info in signalInfo.Value)
					{
						var signalName = info.SignalName.Replace('.', '_');
						stringBuilder.AppendLine($"public struct {signalName}EventArgs({GeneratorHelper.GetParametersList(info.Parameters)}) : ISubscribableEventArgs<Godot.{className}, {signalName}EventArgs>");
						using (stringBuilder.CodeBlock())
						{
							stringBuilder
								.AppendLine($"public static EventBus<Godot.{className}, {signalName}EventArgs>.Descriptor Desc => DescHolder.Value;")
								.AppendLine($"public Godot.{className}? Publisher {{ get; private set; }}")
								.AppendLine($"public GodotObject Subscriber {{ get; private set; }} = null!;")
								.AppendLine();

							stringBuilder.AppendLine("private static class DescHolder");
							using (stringBuilder.CodeBlock())
							{
								stringBuilder
									.AppendLine(
										$"public static EventBus<Godot.{className}, {signalName}EventArgs>.Descriptor Value {{ get; }} = new EventBus<Godot.{className}, {signalName}EventArgs>.Descriptor(");
								using (stringBuilder.Indent())
								{
									stringBuilder.AppendLine($"Godot.{className}.SignalName.{info.SignalName},");
									stringBuilder.Append($"Callable.From(static (");
									if (info.Parameters.Length > 0)
									{
										stringBuilder.Append($"{GeneratorHelper.GetParametersList(info.Parameters)}, ");
									}
									stringBuilder.AppendLine($"GodotObject source) => EventBus<Godot.{className}, {signalName}EventArgs>.Relay.OnEmitted(source, new {signalName}EventArgs({GeneratorHelper.GetParametersList(info.Parameters, false)})))");
								}
								stringBuilder.AppendLine(");");
							}
							stringBuilder.AppendLine();
							stringBuilder.AppendLine($"Godot.{className}? ISubscribableEventArgs<Godot.{className}, {signalName}EventArgs>.Publisher");
							using (stringBuilder.CodeBlock())
							{
								stringBuilder
									.AppendLine("get => Publisher;")
									.AppendLine("set => Publisher = value;");
							}
							stringBuilder.AppendLine();

							stringBuilder.AppendLine($"GodotObject ISubscribableEventArgs<Godot.{className}, {signalName}EventArgs>.Subscriber");
							using (stringBuilder.CodeBlock())
							{
								stringBuilder
									.AppendLine("get => Subscriber;")
									.AppendLine("set => Subscriber = value;");
							}
							stringBuilder.AppendLine();

							foreach (ParsedParameter parsedParameter in info.Parameters)
							{
								stringBuilder.AppendLine($"public readonly {parsedParameter.TypeName} {GeneratorHelper.FirstToUpper(parsedParameter.ParameterName)} = {parsedParameter.ParameterName};");
							}
						}
						stringBuilder.AppendLine().AppendLine();
						
						stringBuilder
							.AppendLine($"public static EventSubscriptionToken Subscribe(this ToFirst<Godot.{className}> chain, Action<{signalName}EventArgs> callback) => chain.Subscribe<{signalName}EventArgs>(callback);")
							.AppendLine($"public static EventSubscriptionToken Subscribe(this FromEnd<Godot.{className}> chain, Action<{signalName}EventArgs> callback) => chain.Subscribe<{signalName}EventArgs>(callback);")
							.AppendLine();
					}
				}
				stringBuilder.AppendLine();
			}
		}

		private static void BuildEventLifecycleManager(IndentedStringBuilder stringBuilder, ImmutableDictionary<string, ImmutableArray<SignalInfo>> signalInfos)
		{
			stringBuilder
				.AppendLine("using Godot;")
				.AppendLine("using System;")
				.AppendLine("using System.Collections.Generic;")
				.AppendLine()
				.AppendLine("namespace FortyOne.Tools;")
				.AppendLine()
				.AppendLine("#nullable enable")
				.AppendLine("#pragma warning disable CS0618 // Type or member is obsolete");

			stringBuilder.AppendLine("public static partial class EventLifecycleManager");
			using (stringBuilder.CodeBlock())
			{
				stringBuilder.AppendLine("private readonly static Dictionary<Type, NativeSignalBindBlueprint[]> NativeSignalBlueprintsByType = new Dictionary<Type, NativeSignalBindBlueprint[]>")
					.AppendLine("{");
				using (stringBuilder.Indent())
				{
					foreach (var signalInfo in signalInfos)
					{
						string className = signalInfo.Key;
						stringBuilder.AppendLine($"[typeof(Godot.{className})] = [");
						using (stringBuilder.Indent())
						{
							foreach (var info in signalInfo.Value)
							{
								var signalName = info.SignalName.Replace('.', '_');
								stringBuilder.AppendLine($"new NativeSignalBindBlueprint(Godot.{className}.SignalName.{signalName}, () => EventBus<Godot.{className}, {className}Signal.{signalName}EventArgs>.HasAnyGlobalBroadcast, {className}Signal.{signalName}EventArgs.Desc.OnNativeSignalReceived),");
							}
						}
						stringBuilder.AppendLine("],");
					}
				}
				stringBuilder.AppendLine("};");
			}
		}
	}
}
