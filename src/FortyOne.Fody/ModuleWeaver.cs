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
		
		public override void Execute()
		{
			var watchedTypes = ModuleDefinition.GetTypes().Where(type => type.CustomAttributes.Any(attr => attr.AttributeType.Name == WatcherAttributeName));
			foreach (TypeDefinition watchedType in watchedTypes)
			{
				if (!IsInheritsFrom(watchedType, "Godot.GodotObject")) continue;
				
				// 拿到需要在构造(new)后和销毁(Predelete)前执行的方法
				MethodDefinition constructCallback = watchedType.Methods.SingleOrDefault(method => method.CustomAttributes.Any(attr => attr.AttributeType.Name == ConstructCallbackAttributeName));
				MethodDefinition predeleteCallback = watchedType.Methods.SingleOrDefault(method => method.CustomAttributes.Any(attr => attr.AttributeType.Name == PredeleteCallbackAttributeName));

				TypeDefinition godotObjectType = FindTypeDefinition("Godot.GodotObject");
				AssemblyDefinition fortyOneToolsAssembly = ModuleDefinition.AssemblyResolver.Resolve(ModuleDefinition.AssemblyReferences.First(asmRef => asmRef.Name == "FortyOne.Tools"));
				TypeDefinition fodyHelpersMethods = fortyOneToolsAssembly.MainModule.GetTypes().SingleOrDefault(type => type.FullName == "FortyOne.Fody.Helpers");
				if (fodyHelpersMethods is null) WriteError($"The fodyHelpersMethods is null! You should figure out why this happened.");
				
				// 构造函数的baseConstructorCall之后调用constructCallback
				if (constructCallback is not null)
				{
					#region Constructor
					MethodReference actionConstructorRef = ModuleDefinition.ImportReference(typeof(Action<object, ulong>).GetConstructors().First());
					MethodReference constructorCallbackHelperMethodReference = ModuleDefinition.ImportReference(fodyHelpersMethods!.Methods.FirstOrDefault(m => (m.Name == "ConstructorCallbackHelper")));
					 
					var constructors = watchedType.Methods.Where(m => m.IsConstructor && !m.IsStatic);
					foreach (MethodDefinition constructor in constructors)
					{
						// 跳过链式构造函数
						if (constructor.Body.Instructions.Any(i =>
							    i.OpCode == OpCodes.Call && 
							    i.Operand is MethodReference{Name: ".ctor"} methodRef &&
							    methodRef.DeclaringType == watchedType))
						{
							continue;
						}
						
						Instruction baseConstructorCall = constructor.Body.Instructions.FirstOrDefault(i =>
							i.OpCode == OpCodes.Call &&
							i.Operand is MethodReference { Name: ".ctor" } baseCtorRef &&
							baseCtorRef.DeclaringType != watchedType);
						if (baseConstructorCall is null) continue;
						
						using (MethodPrologue constructorInserter = new MethodPrologue(constructor, baseConstructorCall.Next))
						{
							constructorInserter.Insert(OpCodes.Ldarg_0)
								.Insert(OpCodes.Ldnull)
								.Insert(OpCodes.Ldftn, constructCallback)
								.Insert(OpCodes.Newobj, actionConstructorRef)
								.Insert(OpCodes.Call, constructorCallbackHelperMethodReference);
						}
					}
					#endregion
				}
				
				
				// 销毁前调用predeleteCallback
				// 通过在Godot原生方法_Notification前插入代码
				// 实现不影响开发者正常重载_Notification的情况下，实现对一个类的实例的生命周期进行监听
				if (predeleteCallback is not null)
				{
					#region Notification
					MethodDefinition notificationOverride = watchedType.Methods.FirstOrDefault(m => (m.Name == "_Notification" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Int32"));
					
					// 开发者要是没重写Notification，直接无中生有! （剑来! （bushi
					if (notificationOverride is null)
					{
						// 这其实也有个问题，base的方法它是带有很多attribute的，但是重写不一定带。这一部分怎么权衡？
						MethodDefinition baseNotification = godotObjectType.Methods.FirstOrDefault(m => (m.Name == "_Notification" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Int32"));
						if (baseNotification is null) throw new Exception("The base _Notification does not found.");

						MethodDefinition synthesizedNotification = OverrideBaseMethod(baseNotification);
						watchedType.Methods.Add(synthesizedNotification);
						notificationOverride = synthesizedNotification;
					}
					
					// 在已有/新建的notification前追加:
					// if (beforeNotificationCheck(this, what) == true)
					//  new Action<ulong>
					//  predeleteHelper(this, predeleteCallback)
					// continue origin code
					// 所有判定逻辑全部在BeforeNotificationCheck里面处理，不然用opcodes处理真的太复杂了
					using (MethodPrologue notificationInserter = new MethodPrologue(notificationOverride))
					{
						// 把this和int what压入栈顶
						notificationInserter
							.Insert(OpCodes.Ldarg_0)
							.Insert(OpCodes.Ldarg_1);

						MethodReference actionConstructorRef = ModuleDefinition.ImportReference(typeof(Action<ulong>).GetConstructors().First());
						MethodReference beforeNotificationCheckMethodReference = ModuleDefinition.ImportReference(fodyHelpersMethods!.Methods.FirstOrDefault(m => (m.Name == "BeforeNotificationCheck")));
						
						// 调用辅助方法消耗栈内的参数
						// beforeNotificationCheck(this, what)
						notificationInserter.Insert(OpCodes.Call, beforeNotificationCheckMethodReference);
						// 此时栈里只有一个返回的bool值
						
						MethodReference predeleteCallbackHelperMethodReference = ModuleDefinition.ImportReference(fodyHelpersMethods!.Methods.FirstOrDefault(m => (m.Name == "PredeleteCallbackHelper")));
						
						notificationInserter.BranchToOrigin(OpCodes.Brfalse)
							.Insert(OpCodes.Ldarg_0);
						
						// 拿到predeleteCallback的实例并压入栈顶
						notificationInserter.Insert(OpCodes.Ldnull)
							.Insert(OpCodes.Ldftn, predeleteCallback)
							.Insert(OpCodes.Newobj, actionConstructorRef);
						
						notificationInserter.Insert(OpCodes.Call, predeleteCallbackHelperMethodReference);
					}
					#endregion

					#region HasClassMethod
					MethodDefinition hasClassMethodOverride = watchedType.Methods.FirstOrDefault(m => m.Name == "HasGodotClassMethod" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "Godot.NativeInterop.godot_string_name& modreq(System.Runtime.InteropServices.InAttribute)");
					
					// 让HasClassMethod在_Notification上一直返回true
					if (hasClassMethodOverride is null)
					{
						MethodDefinition baseHasGodotClassMethod = godotObjectType.Methods.FirstOrDefault(m => m.Name == "HasGodotClassMethod" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "Godot.NativeInterop.godot_string_name& modreq(System.Runtime.InteropServices.InAttribute)");
						if (baseHasGodotClassMethod is null) throw new Exception("The base HasGodotClassMethod does not found.");

						MethodDefinition synthesizedHasClassMethod = OverrideBaseMethod(baseHasGodotClassMethod);
						watchedType.Methods.Add(synthesizedHasClassMethod);
						hasClassMethodOverride = synthesizedHasClassMethod;
					}
					
					// if (BeforeHasGodotClassMethodCheck(method))
					//  return true;
					// continue origin code
					using (MethodPrologue hasGodotClassMethodPrologueInserter = new MethodPrologue(hasClassMethodOverride))
					{
						MethodReference beforeHasGodotClassMethodCheckMethodReference = ModuleDefinition.ImportReference(fodyHelpersMethods!.Methods.FirstOrDefault(m => (m.Name == "BeforeHasGodotClassMethodCheck")));
						
						// call BeforeHasGodotClassMethodCheck
						hasGodotClassMethodPrologueInserter.Insert(OpCodes.Ldarg_1)
							.Insert(OpCodes.Call, beforeHasGodotClassMethodCheckMethodReference);
						
						// if (返回的值 == true)
						//	return true
						hasGodotClassMethodPrologueInserter.BranchToOrigin(OpCodes.Brfalse)
							.Insert(OpCodes.Ldc_I4_1)
							.Insert(OpCodes.Ret);
					}
					#endregion

					#region Dispose
					MethodDefinition disposeOverride = watchedType.Methods.FirstOrDefault(m => (m.Name == "Dispose" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Boolean"));

					if (disposeOverride is null)
					{
						MethodDefinition baseDispose = godotObjectType.Methods.FirstOrDefault(m => (m.Name == "Dispose" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Boolean"));
						if (baseDispose is null) throw new Exception("The base Dispose does not found.");

						MethodDefinition synthesizedDispose = OverrideBaseMethod(baseDispose);
						watchedType.Methods.Add(synthesizedDispose);
						disposeOverride = synthesizedDispose;
					}

					// 代码逻辑和_Notification前插入Predelete完全一致
					using (MethodPrologue disposePrologueInserter = new MethodPrologue(disposeOverride))
					{
						disposePrologueInserter.Insert(OpCodes.Ldarg_0);

						MethodReference actionConstructorRef = ModuleDefinition.ImportReference(typeof(Action<ulong>).GetConstructors().First());
						MethodReference beforeDisposeCheckMethodReference = ModuleDefinition.ImportReference(fodyHelpersMethods!.Methods.FirstOrDefault(m => (m.Name == "BeforeDisposeCheck")));
						
						disposePrologueInserter.Insert(OpCodes.Call, beforeDisposeCheckMethodReference);
					
						MethodReference predeleteCallbackHelperMethodReference = ModuleDefinition.ImportReference(fodyHelpersMethods!.Methods.FirstOrDefault(m => (m.Name == "PredeleteCallbackHelper")));
					
						disposePrologueInserter.BranchToOrigin(OpCodes.Brfalse)
							.Insert(OpCodes.Ldarg_0);
					
						disposePrologueInserter.Insert(OpCodes.Ldnull)
							.Insert(OpCodes.Ldftn, predeleteCallback)
							.Insert(OpCodes.Newobj, actionConstructorRef);
					
						disposePrologueInserter.Insert(OpCodes.Call, predeleteCallbackHelperMethodReference);
					}
					#endregion
				}
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
		
		/// <summary>
		/// 对方法的Attributes进行过滤，避免复用原方法的Attributes时出错
		/// </summary>
		/// <param name="attributes"></param>
		/// <returns>过滤后的<c>MethodAttributes</c></returns>
		private static MethodAttributes FilterAttributesForOverride(MethodAttributes attributes)
		{
			// 剔除不需要的attributes
			attributes &= ~MethodAttributes.NewSlot;
			attributes &= ~MethodAttributes.Abstract;
			attributes &= ~MethodAttributes.PInvokeImpl;
			attributes &= ~MethodAttributes.UnmanagedExport;
			attributes &= ~MethodAttributes.CheckAccessOnOverride;
			
			// 增加重写所需的attributes
			attributes |= MethodAttributes.ReuseSlot;
			
			return attributes;
		}

		/// <param name="baseMethod">父类方法</param>
		/// <returns>新建的重写的空方法（包含了base.xxx和ret）</returns>
		private MethodDefinition OverrideBaseMethod(MethodDefinition baseMethod)
		{
			MethodDefinition synthesizedMethod = new MethodDefinition(baseMethod.Name,
				FilterAttributesForOverride(baseMethod.Attributes),
				ModuleDefinition.ImportReference(baseMethod.ReturnType)
			);
			foreach (ParameterDefinition baseNotificationParameter in baseMethod.Parameters)
			{
				synthesizedMethod.Parameters.Add(new ParameterDefinition(
					baseNotificationParameter.Name,
					baseNotificationParameter.Attributes,
					ModuleDefinition.ImportReference(baseNotificationParameter.ParameterType))
				);
			}

			MethodReference baseMethodRef = ModuleDefinition.ImportReference(baseMethod);
			synthesizedMethod.Overrides.Add(baseMethodRef);
			
			// base.xxxxx
			ILProcessor il = synthesizedMethod.Body.GetILProcessor();
			il.Emit(OpCodes.Ldarg_0);
			foreach (ParameterDefinition synthesizedMethodParameter in synthesizedMethod.Parameters)
			{
				il.Emit(OpCodes.Ldarg,  synthesizedMethodParameter);
			}
			il.Emit(OpCodes.Call, baseMethodRef);
			il.Emit(OpCodes.Ret);
			
			return synthesizedMethod;
		}
		
		/// <summary>
		/// 只适用于类的继承判定，不能用于接口的继承判定
		/// </summary>
		private bool IsInheritsFrom(TypeDefinition currentClass, string baseClassFullName)
		{
			if (currentClass is null) return false;

			TypeReference currentBase = currentClass;
			while (currentBase is not null)
			{
				if (currentBase.FullName == baseClassFullName || currentBase.GetElementType().FullName == baseClassFullName) return true;

				try
				{
					TypeDefinition resolvedType = currentBase.Resolve();
					currentBase = resolvedType.BaseType;
				}
				catch
				{
					break;
				}
			}
			return false;
		}
	}
}