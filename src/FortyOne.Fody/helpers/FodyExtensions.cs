using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Linq;

namespace FortyOne.Fody.Helpers;

public static class FodyExtensions
{
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

	public class ModuleWeaverHelper(ModuleDefinition moduleDefinition, TypeDefinition godotObjectType, TypeDefinition fodyHelpersMethods)
	{
		private MethodDefinition OverrideBaseMethod(MethodDefinition baseMethod)
		{
			MethodDefinition synthesizedMethod = new MethodDefinition(baseMethod.Name, FilterAttributesForOverride(baseMethod.Attributes), moduleDefinition.ImportReference(baseMethod.ReturnType));
			foreach (ParameterDefinition baseNotificationParameter in baseMethod.Parameters)
			{
				synthesizedMethod.Parameters.Add(new ParameterDefinition(
					baseNotificationParameter.Name,
					baseNotificationParameter.Attributes,
					moduleDefinition.ImportReference(baseNotificationParameter.ParameterType))
				);
			}

			MethodReference baseMethodRef = moduleDefinition.ImportReference(baseMethod);
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
	
		private MethodDefinition OverrideMethodIfNotExists(TypeDefinition currentType, Func<MethodDefinition, bool> filter)
		{
			MethodDefinition baseMethod = godotObjectType.Methods.FirstOrDefault(filter);
			if (baseMethod is null) throw new Exception($"The baseMethod does not found.");
		
			MethodDefinition overrideMethod = currentType.Methods.FirstOrDefault(filter);
			if (overrideMethod is not null) return overrideMethod;
	
			MethodDefinition synthesizedNotification = OverrideBaseMethod(baseMethod);
			currentType.Methods.Add(synthesizedNotification);
			overrideMethod = synthesizedNotification;
			return overrideMethod;
		}

		public void OverrideAndInsertBefore(TypeDefinition currentType, Func<MethodDefinition, bool> filter, Action<MethodPrologue> insert)
		{
			using MethodPrologue prologue = new MethodPrologue(OverrideMethodIfNotExists(currentType, filter));
			insert.Invoke(prologue);
		}

		
		public MethodDefinition GetTargetClassMethod(TypeDefinition targetClass, string methodAttrName)
			=> targetClass.Methods.SingleOrDefault(method => method.CustomAttributes.Any(attr => attr.AttributeType.Name == methodAttrName));
		
		public MethodReference GetHelperMethod(string methodName)
			=> moduleDefinition.ImportReference(fodyHelpersMethods.Methods.FirstOrDefault(m => m.Name == methodName));

		public MethodReference GetActionConstructor(Type actionType)
			=> moduleDefinition.ImportReference(actionType.GetConstructors().First());
	}
	
	/// <summary>
	/// 只适用于类的继承判定，不能用于接口的继承判定
	/// </summary>
	public static bool IsInheritsFrom(this TypeDefinition currentClass, string baseClassFullName)
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

	public static bool MethodSignature(this MethodDefinition method, string name, string[] parameterTypeFullName)
		=> method.Name == name 
		   && method.Parameters.Count == parameterTypeFullName.Length
		   && method.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameterTypeFullName);
}
