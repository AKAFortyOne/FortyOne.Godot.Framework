// ReSharper disable InvertIf
// ReSharper disable ConvertToUsingDeclaration
// 禁用If转换警告和using转换警告以保留直观易读的代码结构

using Fody;
using FortyOne.Fody.Helpers;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FortyOne.Fody
{
	public class ModuleWeaver : BaseModuleWeaver
	{
		private const string WatcherAttributeName = "WatcherAttribute";
		private const string ConstructCallbackAttributeName = "ConstructCallbackAttribute";
		private const string PredeleteCallbackAttributeName = "PredeleteCallbackAttribute";

		private FodyExtensions.ModuleWeaverHelper _helper;
		
		private void WeaveConstructor(TypeDefinition currentType)
		{
			MethodDefinition constructCallback = _helper.GetTargetClassMethod(currentType, ConstructCallbackAttributeName);
			if (constructCallback is null) return;
					 
			var constructors = currentType.Methods.Where(m => m.IsConstructor && !m.IsStatic);
			foreach (MethodDefinition constructor in constructors)
			{
				// 跳过链式构造函数
				if (constructor.Body.Instructions.Any(i 
					    => i.OpCode == OpCodes.Call
                           && i.Operand is MethodReference{Name: ".ctor"} methodRef
                           && methodRef.DeclaringType == currentType))
				{
					continue;
				}
						
				var baseConstructorCall = constructor.Body.Instructions.FirstOrDefault(i 
					=> i.OpCode == OpCodes.Call
					   && i.Operand is MethodReference { Name: ".ctor" } baseCtorRef
					   && baseCtorRef.DeclaringType != currentType);
				if (baseConstructorCall is null) continue;
						
				using (MethodPrologue constructorInserter = new MethodPrologue(constructor, baseConstructorCall.Next))
				{
					constructorInserter
						.Insert(OpCodes.Ldarg_0)
						.Insert(OpCodes.Ldnull)
						.Insert(OpCodes.Ldftn, constructCallback)
						.Insert(OpCodes.Newobj, _helper.GetActionConstructor(typeof(Action<object, ulong>)))
						.Insert(OpCodes.Call, _helper.GetHelperMethod("ConstructorCallbackHelper"));
				}
			}
		}
		
		public override void Execute()
		{
			var watchedTypes = ModuleDefinition.GetTypes().Where(type => type.CustomAttributes.Any(attr => attr.AttributeType.Name == WatcherAttributeName));
			
			var fodyHelpersMethods = ModuleDefinition.AssemblyResolver
				.Resolve(ModuleDefinition.AssemblyReferences.First(asmRef => asmRef.Name == "FortyOne.Tools")).MainModule
				.GetTypes().SingleOrDefault(type => type.FullName == "FortyOne.Fody.Helpers");
			if (fodyHelpersMethods is null) WriteError($"The fodyHelpersMethods is null! You should figure out why this happened.");
			
			_helper = new FodyExtensions.ModuleWeaverHelper(ModuleDefinition, FindTypeDefinition("Godot.GodotObject"), fodyHelpersMethods);
			foreach (TypeDefinition watchedType in watchedTypes)
			{
				if (!watchedType.IsInheritsFrom("Godot.GodotObject")) continue;
				
				WeaveConstructor(watchedType);

				#region PredeleteCallback
				MethodDefinition predeleteCallback = _helper.GetTargetClassMethod(watchedType, PredeleteCallbackAttributeName);
				if (predeleteCallback is not null)
				{
					_helper.OverrideAndInsertBefore(watchedType,
						m => m.MethodSignature("_Notification", ["System.Int32"]),
						// 在已有/新建的notification前追加:
						// if (beforeNotificationCheck(this, what) == true)
						//  new Action<ulong>
						//  predeleteHelper(this, predeleteCallback)
						// continue origin code
						// 所有判定逻辑全部在BeforeNotificationCheck里面处理，不然用opcodes处理真的太复杂了
						insert => insert.Insert(OpCodes.Ldarg_0)
							.Insert(OpCodes.Ldarg_1)
							.Insert(OpCodes.Call, _helper.GetHelperMethod("BeforeNotificationCheck"))
							// 此时栈里只有一个返回的bool值
							.BranchToOrigin(OpCodes.Brfalse)
							.Insert(OpCodes.Ldarg_0)
							// 拿到predeleteCallback的实例并压入栈顶
							.Insert(OpCodes.Ldnull)
							.Insert(OpCodes.Ldftn, predeleteCallback)
							.Insert(OpCodes.Newobj, _helper.GetActionConstructor(typeof(Action<ulong>)))
							.Insert(OpCodes.Call, _helper.GetHelperMethod("PredeleteCallbackHelper")));
					
					
					_helper.OverrideAndInsertBefore(watchedType,
						m => m.MethodSignature("Dispose", ["System.Boolean"]),
						insert => insert.Insert(OpCodes.Ldarg_0)
							.Insert(OpCodes.Call, _helper.GetHelperMethod("BeforeDisposeCheck"))
							.BranchToOrigin(OpCodes.Brfalse)
							.Insert(OpCodes.Ldarg_0)
							.Insert(OpCodes.Ldnull)
							.Insert(OpCodes.Ldftn, predeleteCallback)
							.Insert(OpCodes.Newobj, _helper.GetActionConstructor(typeof(Action<ulong>)))
							.Insert(OpCodes.Call, _helper.GetHelperMethod("PredeleteCallbackHelper")));
					
					
					_helper.OverrideAndInsertBefore(watchedType,
						m => m.MethodSignature("HasGodotClassMethod", ["Godot.NativeInterop.godot_string_name& modreq(System.Runtime.InteropServices.InAttribute)"]),
						insert => insert.Insert(OpCodes.Ldarg_1)
							.Insert(OpCodes.Call, _helper.GetHelperMethod("BeforeHasGodotClassMethodCheck"))
							.BranchToOrigin(OpCodes.Brfalse)
							.Insert(OpCodes.Ldc_I4_1)
							.Insert(OpCodes.Ret));
				}
				#endregion
			}
		}

		public override IEnumerable<string> GetAssembliesForScanning()
		{
			yield return "GodotSharp";
			yield return "netstandard";
			yield return "mscorlib";
			yield return "System.Runtime";
			yield return "System.Collections";
		}
	}
}