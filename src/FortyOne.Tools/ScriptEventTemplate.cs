// 此文件的所有注释都是给维护此事件系统的人看的！
// 所有封闭构造类型都是由Source Generator生成的，大多数情况下你完全不需要碰这一部分
// 如果你不需要维护此事件调度系统，只关心怎么使用事件系统
// 此文档的所有注释以及代码都不需要阅读（当然，除非你在Debug :>）

// ReSharper disable StaticMemberInGenericType
// ReSharper disable ConvertToPrimaryConstructor
// ReSharper disable ArrangeAccessorOwnerBody

using Godot;
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace FortyOne.Tools;

#nullable enable

public interface ISubscribableEventArgs<TSender, TArgs>
	where TSender : GodotObject
	where TArgs : struct, ISubscribableEventArgs<TSender, TArgs>
{
	public TSender? Publisher { get; set; }
	public GodotObject Subscriber { get; set; }

	public static abstract EventBus<TSender, TArgs>.Descriptor Desc { get; }
}

public interface IPublishableEventArgs<TSender, TArgs> : ISubscribableEventArgs<TSender, TArgs>
	where TSender : GodotObject
	where TArgs : struct, IPublishableEventArgs<TSender, TArgs>
{}


// Publish支持下文链式调用：
// - A.Publish(args);
// - A.From(sender).Publish(args);
// - A.To(receiver).Publish(args);
// - A.To(receiver1).To(receiver2).(To...).Publish(args)
// - A.From(sender).To(receiver).Publish(args);
// - A.From(sender).To(receiver1).To(receiver2).(To...).Publish(args)
// Subscribe支持以下链式调用：
// - B.To(receiver).Subscribe(callback);
// - B.From(sender).To(receiver).Subscribe(callback);
// - B.From(sender1).From(sender2).(From...).To(receiver).Subscribe(callback)
// Note: Subscribe必须带receiver（也就是调用时传this）

// 类内定义：
// public static FromFirst<TSender> From(TSender sender) => new FromFirst<TSender>(sender);
// public static ToFirst<TSender> To(GodotObject receiver) => new ToFirst<TSender>(null, receiver);
// public static void Publish(TArgs args) => EventBus<TSender, TArgs>.ExecutePublish(null, null, args);
public readonly ref struct FromFirst<TSender>(TSender sender) where TSender : GodotObject
{
	public FromChain<TSender> From(TSender sender2) => sender == sender2 
		? new FromChain<TSender>([sender])
		: new FromChain<TSender>([sender, sender2]);
	public ToFirst<TSender> To(GodotObject receiver) => new ToFirst<TSender>(sender, receiver);
	public void Publish<TArgs>(TArgs args) where TArgs : struct, IPublishableEventArgs<TSender, TArgs>
		=> EventBus<TSender, TArgs>.ExecutePublishSingle(sender, null, args);
}

public readonly ref struct FromChain<TSender>(List<TSender> senders)
	where TSender : GodotObject
{
	public FromChain<TSender> From(TSender sender)
	{
		// ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
		// 防止有人通过null!强行传null
		// 如果sender确实需要null就别用From
		if (sender is not null && !senders.Contains(sender))
		{
			senders.Add(sender);
		}
		return new FromChain<TSender>(senders);
	}
	public FromEnd<TSender> To(GodotObject receiver) => new FromEnd<TSender>(senders, receiver);
}

public readonly ref struct FromEnd<TSender>(List<TSender> senders, GodotObject receiver) where TSender : GodotObject
{
	public EventSubscriptionToken Subscribe<TArgs>(Action<TArgs> callback)
		where TArgs : struct, ISubscribableEventArgs<TSender, TArgs> 
		=> EventBus<TSender, TArgs>.RegisterSubscription(senders, receiver, callback);
}

public readonly ref struct ToFirst<TSender>(TSender? sender, GodotObject receiver) where TSender : GodotObject
{
	public ToChain<TSender> To(GodotObject receiver2) 
		=> receiver == receiver2
			? new ToChain<TSender>(sender, [receiver])
			: new ToChain<TSender>(sender, [receiver, receiver2]);
	public void Publish<TArgs>(TArgs args) where TArgs : struct, IPublishableEventArgs<TSender, TArgs>
		=> EventBus<TSender, TArgs>.ExecutePublishSingle(sender, receiver, args);
	public EventSubscriptionToken Subscribe<TArgs>(Action<TArgs> callback) where TArgs : struct, ISubscribableEventArgs<TSender, TArgs>
		=> sender is not null 
			? EventBus<TSender, TArgs>.RegisterSubscription([sender], receiver, callback)
			: EventBus<TSender, TArgs>.RegisterSubscription(null, receiver, callback);
}

public readonly ref struct ToChain<TSender>(TSender? sender, List<GodotObject> receivers) where TSender : GodotObject
{
	public ToChain<TSender> To(GodotObject receiver)
	{
		// ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
		// 防止有人通过null!强行传null
		// 如果receiver确实需要null就别用To
		if (receiver != null && !receivers.Contains(receiver))
		{
			receivers.Add(receiver);
		}
		return new ToChain<TSender>(sender, receivers);
	}
	public void Publish<TArgs>(TArgs args)
		where TArgs : struct, IPublishableEventArgs<TSender, TArgs> 
		=> EventBus<TSender, TArgs>.ExecutePublishMultiple(sender, receivers, args);
}

public class SubscribeFailedException(string message) : Exception($"[Subscribe] {message}");
public class PublishFailedException(string message) : Exception($"[Publish] {message}");


[EditorBrowsable(EditorBrowsableState.Never)]
public static class EventBus<TSender, TArgs>
	where TSender : GodotObject
	where TArgs : struct, ISubscribableEventArgs<TSender, TArgs>
{
	public readonly struct Descriptor
	{
		public StringName SignalName { get; }
		
		/// <summary>
		/// 当接收到Godot发来的信号时调用<br/>
		/// 参数列表是：<c>originalSignalArgs...(Godot的信号的参数列表), emitter(信号的发送者)</c><br/>
		/// 事实意义上只应该有一条语句，也就是：<br/>
		///  <c>EventBus&lt;TSender, TArgs&gt;.Relay.OnEmitted(Desc!, source, new Args(...));</c><br/>
		/// 当该信号被Godot emit时，会调用此方法用于将信号传递给C#端
		/// </summary>
		public Callable? OnNativeSignalReceived { get; }
		
		// Godot原生信号，无pub，只需要sub时，使用该构造函数
		public Descriptor(
			StringName signalName,
			Callable onNativeSignalReceived)
		{
			SignalName = signalName;
			OnNativeSignalReceived = onNativeSignalReceived;
		}
		
		// C#内部互通的信号
		public Descriptor(StringName signalName)
		{
			SignalName = signalName;
		}
	}

	private static class Subscriptions
	{
		/// <summary>
		/// 此处的订阅架构是这样的：
		///  一个receiver对唯一的一个Receiver订阅集合
		///  在这个集合内部存放着弱引用的receiver便于检查存活性与有效性，方便获取并使用
		///  同时有一个唯一的无sender的Callback槽位和一个带sender的Callback的槽位的集合
		///  (因为是按信号生成封闭构造类型，所以一个信号在一个实例上只能有一次订阅)
		///  sender可能有多个，所以每一个sender对应一个集合，内部存放弱引用的sender与对应的callback
		/// </summary>
		private class ReceiverSubscriptions
		{
			private class SenderSubscriptions
			{
				private readonly WeakReference<TSender> _sender;
				private Action<TArgs>? _callback;
				
				public bool IsEmpty => _callback is null;
				
				public SenderSubscriptions(TSender sender) => _sender = new WeakReference<TSender>(sender);

				#region Callback
				public void AddCallback(Action<TArgs> callback) => _callback += callback;
				
				public bool IsCallbackDuplicate(Action<TArgs> callback) => _callback.ContainsAction(callback);
				
				public void InvokeCallback(TArgs args) => _callback?.Invoke(args);

				public void RemoveCallback(Action<TArgs> callback) => _callback -= callback;
				#endregion
				
				public void DisconnectSignal()
				{
					if (!_sender.TryGetValid(out var sender)) return;
					NativeConnect.TryDisconnectGodotNative(sender, TArgs.Desc.SignalName, TArgs.Desc.OnNativeSignalReceived);
				}
			}
			private readonly Dictionary<ulong, SenderSubscriptions> _senderSubsMap = [];
	
			private readonly WeakReference<GodotObject> _receiver;
			private Action<TArgs>? _anySenderCallback;
			
			public bool IsEmpty => (_anySenderCallback is null && _senderSubsMap.Count == 0);

			public ReceiverSubscriptions(GodotObject receiver) => _receiver = new WeakReference<GodotObject>(receiver);

			#region Callback
			public void AddCallbackTargeted(TSender sender, ulong senderInstanceId, Action<TArgs> callback) 
				=> _senderSubsMap.GetOrAdd(senderInstanceId, _ => new SenderSubscriptions(sender)).AddCallback(callback);
			
			public void AddCallbackGlobal(Action<TArgs> callback) => _anySenderCallback += callback;

			public bool FullCheckCallbackDuplicateTargeted(ulong senderInstanceId, Action<TArgs> callback)
				=> _anySenderCallback.ContainsAction(callback)
				   || (_senderSubsMap.TryGetValue(senderInstanceId, out var senderSubscriptions)
					   && senderSubscriptions.IsCallbackDuplicate(callback));
			
			public bool FullCheckCallbackDuplicateGlobal(Action<TArgs> callback)
			{
				if (_anySenderCallback.ContainsAction(callback)) return true;
				foreach (var senderSubscriptions in _senderSubsMap.Values)
				{
					if (senderSubscriptions.IsCallbackDuplicate(callback)) return true;
				}
				return false;
			}
			
			// 这里非常重要，不能做任何简化和时序逻辑调整
			// 正常情况下如果不存快照直接使用callback，不论怎么调整逻辑（先全局后定向或先定向后全局）
			// 只要之前先者内部注册了后者的callback，就会发生“击穿”，导致先者内部注册的callback在本次Publish里被立刻触发。
			// 为了防止击穿，这里必须先存快照。因为拿不到senderSubscriptions的callback
			// 所以这里通过时序绕过了senderSubscriptions的callback保存快照，让定向callback先执行，此时唯一可能的击穿问题是：定向callback内部订阅了全局callback
			// 此时因为最开始保存了快照，所以这样就可以彻底避免击穿问题。
			public void ExecuteCallback(ulong senderInstanceId, TArgs args)
			{
				if (!_receiver.TryGetValid(out var subscriber)) return;
				args.Subscriber = subscriber;
				
				var anySenderCallback = _anySenderCallback;
				
				if (senderInstanceId != 0 && _senderSubsMap.TryGetValue(senderInstanceId, out var senderSubscriptions))
				{
					senderSubscriptions.InvokeCallback(args);
				}
				anySenderCallback?.Invoke(args);
			}
			
			public void RemoveCallbackTargeted(ulong senderInstanceId, Action<TArgs> callback)
			{
				if (!_senderSubsMap.TryGetValue(senderInstanceId, out var senderSubscriptions)) return;
				senderSubscriptions.RemoveCallback(callback);
			}
			
			public void RemoveCallbackGlobal(Action<TArgs> callback) => _anySenderCallback -= callback;
			#endregion

			#region Sender
			public bool IsSenderEmpty(ulong senderInstanceId) 
				=> _senderSubsMap.TryGetValue(senderInstanceId, out var senderSubscriptions)
				   && senderSubscriptions.IsEmpty;

			public void DisconnectSignal(ulong senderInstanceId)
			{
				if (!_senderSubsMap.TryGetValue(senderInstanceId, out var senderSubscriptions)) return;
				senderSubscriptions.DisconnectSignal();
			}
			
			public void RemoveSender(ulong senderInstanceId) => _senderSubsMap.Remove(senderInstanceId);
			#endregion
		}

		public static class SubscriptionsMapping
		{
			private readonly static Dictionary<ulong, ReceiverSubscriptions> ReceiverRegistry = [];
			private readonly static Dictionary<ulong, HashSet<ulong>> ReceiverToSendersIndex = [];
			private readonly static Dictionary<ulong, HashSet<ulong>> SenderToReceiversIndex = [];
			private readonly static HashSet<ulong> GlobalBroadcastReceiverIds = [];

			public static int GlobalBroadcastCount => GlobalBroadcastReceiverIds.Count;

			// 提升遍历性能同时避免边遍历边修改引发的报错
			private static class GlobalBroadcastSnapshot
			{
				public static bool NeedsRebuild = true;
				
				private static ReceiverSubscriptions[] _receiversArray = [];
				private static int _nestingDepth;
				
				private static void TrySyncSnapshot()
				{
					if (!NeedsRebuild) return;

					if (_nestingDepth > 0)
					{
						_receiversArray = [.. ReceiverRegistry.Values];
					}
					else
					{
						if (_receiversArray.Length < ReceiverRegistry.Count)
						{
							_receiversArray = new ReceiverSubscriptions[Math.Max(16, ReceiverRegistry.Count * 2)];
						}
				
						ReceiverRegistry.Values.CopyTo(_receiversArray, 0);
					}
					NeedsRebuild = false;
				}

				public static BroadcastScope EnterScope()
				{
					TrySyncSnapshot();
					return new BroadcastScope(_receiversArray);	
				}
				
				public readonly ref struct BroadcastScope
				{
					public readonly ReceiverSubscriptions[] ReceiversArray;

					public BroadcastScope(ReceiverSubscriptions[] receiversArray)
					{
						ReceiversArray = receiversArray;
						_nestingDepth++;
					}

					public void Dispose()
					{
						_nestingDepth--;	
					}
				}
				
				public static void TryClearSnapshot()
				{
					if (_nestingDepth == 0)
					{
						Array.Clear(_receiversArray, 0, _receiversArray.Length);
					}
					NeedsRebuild = true;
				}
			}
			
			public static void PublishTargeted(ulong senderInstanceId, ulong receiverInstanceId, TArgs args)
			{
				if (!ReceiverRegistry.TryGetValue(receiverInstanceId, out var receiverSubscriptions)) return;
				receiverSubscriptions.ExecuteCallback(senderInstanceId, args);
			}

			public static void PublishGlobal(ulong senderInstanceId, TArgs args)
			{
				using var snapshot = GlobalBroadcastSnapshot.EnterScope();
				
				var receiverArray = snapshot.ReceiversArray;
				int count = ReceiverRegistry.Count;
				for (int i = 0; i < count; i++) 
				{
					receiverArray[i].ExecuteCallback(senderInstanceId, args);
				}
			}
			
			#region Subscribe
			public static void SubscribeTargeted(TSender sender, ulong senderInstanceId, GodotObject receiver, ulong receiverInstanceId, Action<TArgs> callback)
			{
				GlobalBroadcastSnapshot.NeedsRebuild = true;
				ReceiverRegistry.GetOrAdd(receiverInstanceId, _ => new ReceiverSubscriptions(receiver)).AddCallbackTargeted(sender, senderInstanceId, callback);
				
				SenderToReceiversIndex.TryAdd(senderInstanceId, []);
				ReceiverToSendersIndex.TryAdd(receiverInstanceId, []);
				SenderToReceiversIndex[senderInstanceId].Add(receiverInstanceId);
				ReceiverToSendersIndex[receiverInstanceId].Add(senderInstanceId);

				NativeConnect.TryConnectGodotNative(sender, TArgs.Desc.SignalName, TArgs.Desc.OnNativeSignalReceived);
				
				EventLifecycleManager.RegisterInstanceCleanupTask(senderInstanceId, UnregisterSender);
				EventLifecycleManager.RegisterInstanceCleanupTask(receiverInstanceId, UnregisterReceiver);
			}

			public static void SubscribeGlobal(GodotObject receiver, ulong receiverInstanceId, Action<TArgs> callback)
			{
				GlobalBroadcastSnapshot.NeedsRebuild = true;
				ReceiverRegistry.GetOrAdd(receiverInstanceId, _ => new ReceiverSubscriptions(receiver)).AddCallbackGlobal(callback);
				
				if (TArgs.Desc.OnNativeSignalReceived is null) return;
				
				if (GlobalBroadcastCount == 0)
				{
					// 当没有任何接收全局广播的Sub且这是第一个接收全局广播的Sub时，对之前的所有的TSender的实例做一次连接。
					if (ScriptInstanceRegistry.TryQuery<TSender>(out var senderInstances))
					{
						foreach (ScriptInstanceRegistry.InstanceHandle senderInstanceHandle in senderInstances)
						{
							NativeConnect.TryConnectGodotNative(senderInstanceHandle.AsInstance<TSender>(), TArgs.Desc.SignalName, TArgs.Desc.OnNativeSignalReceived);
						}
					}
				}
				
				GlobalBroadcastReceiverIds.Add(receiverInstanceId);
				
				EventLifecycleManager.RegisterInstanceCleanupTask(receiverInstanceId, UnregisterReceiver);
			}
			#endregion

			public static bool FullCheckCallbackDuplicateTargeted(ulong senderInstanceId, ulong receiverInstanceId, Action<TArgs> callback)
				=> ReceiverRegistry.TryGetValue(receiverInstanceId, out var receiverSubscriptions)
				   && receiverSubscriptions.FullCheckCallbackDuplicateTargeted(senderInstanceId, callback);
			
			public static bool FullCheckCallbackDuplicateGlobal(ulong receiverInstanceId, Action<TArgs> callback)
				=> ReceiverRegistry.TryGetValue(receiverInstanceId, out var receiverSubscriptions)
				   && receiverSubscriptions.FullCheckCallbackDuplicateGlobal(callback);
			
			#region Unsubscribe
			public static void UnsubscribeTargeted(ulong senderInstanceId, ulong receiverInstanceId, Action<TArgs> callback)
			{
				if (!ReceiverRegistry.TryGetValue(receiverInstanceId, out var currentReceiverSubscriptions)) return;
				currentReceiverSubscriptions.RemoveCallbackTargeted(senderInstanceId, callback);
				
				if (!currentReceiverSubscriptions.IsSenderEmpty(senderInstanceId)) return;
				CleanMapping(senderInstanceId, receiverInstanceId, false);
			}
			
			public static void UnsubscribeGlobal(ulong receiverInstanceId, Action<TArgs> callback)
			{
				if (!ReceiverRegistry.TryGetValue(receiverInstanceId, out var currentReceiverSubscriptions)) return;
				currentReceiverSubscriptions.RemoveCallbackGlobal(callback);
				
				if (ReceiverRegistry.TryGetValue(receiverInstanceId, out var receiverSubscriptions) && receiverSubscriptions.IsEmpty)
				{
					ReceiverRegistry.Remove(receiverInstanceId);
					GlobalBroadcastSnapshot.NeedsRebuild = true;
				}

				if (TArgs.Desc.OnNativeSignalReceived is null) return;
				GlobalBroadcastReceiverIds.Remove(receiverInstanceId);

				if (GlobalBroadcastCount != 0 || !ScriptInstanceRegistry.TryQuery<TSender>(out var senderInstances)) return;
				foreach (var senderInstanceHandle in senderInstances)
				{
					if (SenderToReceiversIndex.ContainsKey(senderInstanceHandle.AsId())) continue;
					NativeConnect.TryDisconnectGodotNative(senderInstanceHandle.AsInstance<TSender>(), TArgs.Desc.SignalName, TArgs.Desc.OnNativeSignalReceived);
				}
			}
			#endregion

			#region UnregisterInstance
			private static void CleanMapping(ulong senderInstanceId, ulong receiverInstanceId, bool forceRemove)
			{
				// 清理索引数据+空壳
				if (ReceiverToSendersIndex.TryGetValue(receiverInstanceId, out var senderInstanceIdSet))
				{
					senderInstanceIdSet.Remove(senderInstanceId);
					if (senderInstanceIdSet.Count == 0)
					{
						ReceiverToSendersIndex.Remove(receiverInstanceId);
					}
				}
				
				if (SenderToReceiversIndex.TryGetValue(senderInstanceId, out var receiverInstanceIdSet))
				{
					receiverInstanceIdSet.Remove(receiverInstanceId);
					if (receiverInstanceIdSet.Count == 0)
					{
						SenderToReceiversIndex.Remove(senderInstanceId);
					}
				}
				
				// 清理receiver数据+空壳
				if (ReceiverRegistry.TryGetValue(receiverInstanceId, out var receiverSubscriptions))
				{
					if (GlobalBroadcastCount == 0 && !SenderToReceiversIndex.ContainsKey(senderInstanceId))
					{
						receiverSubscriptions.DisconnectSignal(senderInstanceId);
					}
					
					if (forceRemove || receiverSubscriptions.IsSenderEmpty(senderInstanceId))
					{
						receiverSubscriptions.RemoveSender(senderInstanceId);
					}
					
					if (receiverSubscriptions.IsEmpty)
					{
						ReceiverRegistry.Remove(receiverInstanceId);
						GlobalBroadcastSnapshot.NeedsRebuild = true;
					}
				}
			}
			
			private static void UnregisterReceiver(ulong receiverInstanceId)
			{
				GlobalBroadcastReceiverIds.Remove(receiverInstanceId);

				if (ReceiverToSendersIndex.Remove(receiverInstanceId, out var senderInstanceIdSet))
				{
					foreach (ulong senderInstanceId in senderInstanceIdSet)
					{
						CleanMapping(senderInstanceId, receiverInstanceId, true);
					}
				}
				
				ReceiverRegistry.Remove(receiverInstanceId);
				GlobalBroadcastSnapshot.TryClearSnapshot();
			}
			
			private static void UnregisterSender(ulong senderInstanceId)
			{
				if (!SenderToReceiversIndex.Remove(senderInstanceId, out var receiverInstanceIdSet)) return;
				
				foreach (ulong receiverInstanceId in receiverInstanceIdSet)
				{
					CleanMapping(senderInstanceId, receiverInstanceId, true);
				}
			}
			#endregion
		}
	}

	public static bool HasAnyGlobalBroadcast => Subscriptions.SubscriptionsMapping.GlobalBroadcastCount != 0;
	
	#region Publish
	public static class Relay
	{
		// Godot -> C#: godot发送的信号，C#的sub也能接收到。
		public static void OnEmitted(GodotObject? source, TArgs args)
		{
			ExecutePublishSingle(source as TSender, null, args);
		}
	}
	
	private static void DispatchSinglePrecheck(TSender? sender, ulong senderInstanceId, GodotObject? receiver, ulong receiverInstanceId)
	{
		bool isSenderInvalid = sender is not null && senderInstanceId == 0;
		bool isReceiverInvalid = receiver is not null && receiverInstanceId == 0;
		
		if (isSenderInvalid)
		{
			throw new PublishFailedException($"The sender instance is already invalid! Context: [InMethod: Publish.DispatchLocal]");
		}
		
		if (isReceiverInvalid)
		{
			throw new PublishFailedException($"The receiver instance is already invalid! Context: [InMethod: Publish.DispatchLocal]");
		}
	}
	
	[EditorBrowsable(EditorBrowsableState.Never)]
	public static void ExecutePublishSingle(TSender? sender, GodotObject? receiver, TArgs args)
	{
		var senderInstanceId = sender.GetValidInstanceIdOrZero();
		var receiverInstanceId = receiver.GetValidInstanceIdOrZero();
		args.Publisher = sender;
		
		DispatchSinglePrecheck(sender, senderInstanceId, receiver, receiverInstanceId);
		
		if (receiver is not null)
		{
			Subscriptions.SubscriptionsMapping.PublishTargeted(senderInstanceId, receiverInstanceId, args);
		}
		else
		{
			Subscriptions.SubscriptionsMapping.PublishGlobal(senderInstanceId, args);
		}
	}
	
	private static ulong[] DispatchMultiplePrecheck(TSender? sender, ulong? senderInstanceId, List<GodotObject>? receivers)
	{
		bool isSenderInvalid = sender is not null && senderInstanceId == 0;
		
		if (isSenderInvalid)
		{
			throw new PublishFailedException($"The sender instance is already invalid! Context: [InMethod: Publish.DispatchLocal]");
		}

		if (receivers is not null)
		{
			ulong[] receiverInstanceIds = new ulong[receivers.Count];
			for (int i = 0; i < receivers.Count; i++)
			{
				var receiver = receivers[i];
				receiverInstanceIds[i] = receiver.GetValidInstanceIdOrZero();
				
				if (receiver is null)
				{
					throw new PublishFailedException("The one of the receives is null. The parameter 'receiver' you input CANNOT be null.");
				}
				
				if (receiverInstanceIds[i] == 0)
				{
					throw new PublishFailedException($"The receiver instance is already invalid! Context: [InMethod: Publish.DispatchLocal]");
				}
			}
			return receiverInstanceIds;
		}
		return [];
	}
	
	internal static void ExecutePublishMultiple(TSender? sender, List<GodotObject>? receivers, TArgs args)
	{
		var senderInstanceId = sender.GetValidInstanceIdOrZero();
		var receiverInstanceIds = DispatchMultiplePrecheck(sender, senderInstanceId, receivers);
		args.Publisher = sender;

		if (receivers is not null)
		{
			foreach (ulong receiverInstanceId in receiverInstanceIds)
			{
				Subscriptions.SubscriptionsMapping.PublishTargeted(senderInstanceId, receiverInstanceId, args);
			}
		}
		else
		{
			Subscriptions.SubscriptionsMapping.PublishGlobal(senderInstanceId, args);
		}
	}
	#endregion

	/// <param name="senders">接收来自哪些sender的信号</param>
	/// <param name="receiver">此处只允许传递this！传递除this外的任何参数都可能导致意外的问题！</param>
	/// <param name="callback">参数列表为(GameEvents? sender, GodotObject? receiver, HotbarSelectedChangedEventArgs args)的回调函数</param>
	/// <returns>一次性订阅Token，可用于手动取消订阅。用后即焚。</returns>
	/// <exception cref="SubscribeFailedException">当传入的sender或receiver非null但是无效时抛出</exception>
	internal static EventSubscriptionToken RegisterSubscription(List<TSender>? senders, GodotObject receiver, Action<TArgs> callback)
	{
		var receiverInstanceId = receiver.GetValidInstanceIdOrZero();
		bool isReceiverInvalid = (receiverInstanceId == 0);

		if (isReceiverInvalid)
		{
			throw new SubscribeFailedException($"The receiver instance is already invalid! Context: [callback: {callback.Method.Name}]");
		}
		
		List<ulong>? sendersTuple = null;
		if (senders is not null)
		{
			ulong[] senderInstanceIds = new ulong[senders.Count];
			for (int i = 0; i < senders.Count; i++)
			{
				var sender = senders[i];
				senderInstanceIds[i] = sender.GetValidInstanceIdOrZero();
				if (sender is null)
				{
					throw new SubscribeFailedException("The one of the senders is null. The parameter 'sender' you input CANNOT be null.");
				}
				
				if (senderInstanceIds[i] == 0)
				{
					throw new SubscribeFailedException($"The sender instance is already invalid! Context: [callback: {callback.Method.Name}]");
				}

				if (Subscriptions.SubscriptionsMapping.FullCheckCallbackDuplicateTargeted(senderInstanceIds[i], receiverInstanceId, callback))
				{
					throw new ArgumentException("The targeted callback has ALREADY registered! You cannot register with the SAME sender and receiver.");
				}
			}

			sendersTuple ??= [];
			for (int i = 0; i < senders.Count; i++)
			{
				sendersTuple.Add(senderInstanceIds[i]);
				Subscriptions.SubscriptionsMapping.SubscribeTargeted(senders[i], senderInstanceIds[i], receiver, receiverInstanceId, callback);
			}
		}
		else
		{
			if (Subscriptions.SubscriptionsMapping.FullCheckCallbackDuplicateGlobal(receiverInstanceId, callback))
			{
				throw new ArgumentException("The global callback has ALREADY registered! You cannot register with the SAME sender and receiver.");
			}
			
			Subscriptions.SubscriptionsMapping.SubscribeGlobal(receiver, receiverInstanceId, callback);
		}

		// 避免token拷贝导致闭包状态逃逸，误杀新订阅
		bool isTokenDispose = false;
		return new EventSubscriptionToken(UnsubscribeAction);
		
		// 这里的UnsubscribeAction是供开发者端手动取消订阅用的，不是自动销毁时会使用的逻辑
		void UnsubscribeAction()
		{
			if (isTokenDispose) return;
			isTokenDispose = true;

			if (senders is not null)
			{
				if (sendersTuple is null || sendersTuple.Count == 0) return;
				foreach (ulong senderInstanceId in sendersTuple)
				{
					Subscriptions.SubscriptionsMapping.UnsubscribeTargeted(senderInstanceId, receiverInstanceId, callback);
				}
			}
			else
			{
				Subscriptions.SubscriptionsMapping.UnsubscribeGlobal(receiverInstanceId, callback);
			}
		}
	}
}


public static partial class EventLifecycleManager
{
	private readonly record struct NativeSignalBindBlueprint(StringName SignalName, Func<bool> HasAnyGlobalBroadcast, Callable? OnNativeSignalReceived);
	private readonly static Dictionary<ulong, List<Action<ulong>>> InstanceCleanupTasksMap = [];
	
	public static void SetupInstanceEventBindings(GodotObject instance)
	{
		Type? type = instance.GetType();
		while (type is not null && type != typeof(object))
		{
			if (NativeSignalBlueprintsByType.TryGetValue(type, out var entries))
			{
				foreach (var e in entries)
				{
					if (e.HasAnyGlobalBroadcast())
					{
						NativeConnect.TryConnectGodotNative(instance, e.SignalName, e.OnNativeSignalReceived);
					}
				}
			}
				
			type = type.BaseType;
		}
	}

	public static void RegisterInstanceCleanupTask(ulong instanceId, Action<ulong> action) 
		=> InstanceCleanupTasksMap.GetOrAdd(instanceId, _ => []).Add(action);

	public static void ExecuteAndRemoveCleanupTasks(ulong instanceId)
	{
		if (!InstanceCleanupTasksMap.Remove(instanceId, out var actions)) return;
		foreach (var action in actions)
		{
			action.Invoke(instanceId);
		}
	}
}
