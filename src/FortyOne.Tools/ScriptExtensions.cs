using Godot;
using Godot.NativeInterop;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace FortyOne.Tools
{
	public static class FortyOneExtensions
	{
		public static TValue GetOrAdd<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, Func<TKey, TValue> valueFactory) where TKey : notnull
		{
			if (dictionary.TryGetValue(key, out var value)) return value;
			
			value = valueFactory(key);
			dictionary.Add(key, value);
			return value;
		}

		public static bool TryGetValid<T>(this WeakReference<T> weak, [MaybeNullWhen(false)] out T value) where T : GodotObject
		{
			if (weak.TryGetTarget(out var receiverNode) && GodotObject.IsInstanceValid(receiverNode))
			{
				value = receiverNode;
				return true;
			}
			value = null;
			return false;
		}

		public static ulong GetValidInstanceIdOrZero(this GodotObject? godotObject) => GodotObject.IsInstanceValid(godotObject) ? godotObject.GetInstanceId() : 0;
		
		public static bool ContainsAction<T>(this Action<T>? slot, Action<T> callback)
		{
			if (slot is null) return false;
			
			foreach (Delegate d in slot.GetInvocationList())
			{
				if (d.Target == callback.Target && d.Method == callback.Method)
				{
					return true;
				}
			}
			return false;
		}
	}

	public static class NativeConnect
	{
		public static void TryConnectGodotNative(GodotObject sender, StringName signalName, Callable? onNativeSignalReceived)
		{
			if (onNativeSignalReceived is null ||
			    !GodotObject.IsInstanceValid(sender) ||
			    !sender.HasSignal(signalName) ||
			    sender.IsConnected(signalName, onNativeSignalReceived.Value)) return;
			sender.Connect(signalName, onNativeSignalReceived.Value, (uint)GodotObject.ConnectFlags.AppendSourceObject);
		}

		public static void TryDisconnectGodotNative(GodotObject sender, StringName signalName, Callable? onNativeSignalReceived)
		{
			if (onNativeSignalReceived is null ||
			    !GodotObject.IsInstanceValid(sender) ||
			    !sender.HasSignal(signalName) ||
			    !sender.IsConnected(signalName, onNativeSignalReceived.Value)) return;
			sender.Disconnect(signalName, onNativeSignalReceived.Value);
		}
	}
	
	public struct EventSubscriptionToken(Action unsubscribeAction) : IDisposable
	{
		public readonly bool IsValid => _unsubscribeAction is not null;
		private Action? _unsubscribeAction = unsubscribeAction;

		public void Dispose()
		{
			Interlocked.Exchange(ref _unsubscribeAction, null)?.Invoke();
		}
	}

	public static class Lifecycle
	{
		[AttributeUsage(AttributeTargets.Class, Inherited = false)]
		public sealed class WatcherAttribute : Attribute { }

		/// <summary>
		/// 每个实例在被创建时通知一次
		/// </summary>
		[AttributeUsage(AttributeTargets.Method, Inherited = false)]
		public sealed class ConstructCallbackAttribute : Attribute { }
		
		/// <summary>
		/// 实例销毁前通知一次
		/// </summary>
		[AttributeUsage(AttributeTargets.Method, Inherited = false)]
		public sealed class PredeleteCallbackAttribute : Attribute { }
	}
}


// 下文方法是专门为Fody提供的，主要用于编译时织入，简化Fody的Opcodes部分的逻辑。
namespace FortyOne.Fody
{
	public class Helpers
	{
		// 模仿真实的MethodName的结构
		private class MethodName : global::Godot.Node.MethodName {
			
			/// <summary>
			/// Cached name for the '_Notification' method.
			/// </summary>
			public new readonly static StringName _Notification = "_Notification";
		}

		#region ConstructorCallback
		public static void ConstructorCallbackHelper(GodotObject instance, Action<object, ulong> constructorCallback)
		{
			if (GodotObject.IsInstanceValid(instance))
			{
				constructorCallback.Invoke(instance, instance.GetInstanceId());
			}
		}
		#endregion

		public static bool BeforeHasGodotClassMethodCheck(in godot_string_name method)
		{
			return (method == MethodName._Notification);
		}
		
		#region PredeleteCallback
		public static bool BeforeNotificationCheck(GodotObject instance, int what)
		{
			if (!GodotObject.IsInstanceValid(instance)) return false;
			if (what == GodotObject.NotificationPredelete) return true;
			return false;
		}
		
		public static bool BeforeDisposeCheck(GodotObject instance)
		{
			return GodotObject.IsInstanceValid(instance);
		}

		public static void PredeleteCallbackHelper(GodotObject instance, Action<ulong> predeleteCallback)
		{
			predeleteCallback.Invoke(instance.GetInstanceId());
		}
		#endregion
	}
}
