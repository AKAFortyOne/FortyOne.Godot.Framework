// ReSharper disable MemberCanBePrivate.Local
// ReSharper disable LoopCanBeConvertedToQuery

using Godot;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

#pragma warning disable CS0618 // Type or member is obsolete

namespace FortyOne.Tools;

// 事件系统和全局实例注册表，同样都支持开发者在任何地方任何时候发起事件/接收事件或修改实例/获取实例。
// TODO:时光机日志回溯系统
// 核心记录
// （事件系统）UTC标准时间戳，哪一个类的哪一个实例在什么时候pub了什么事件，传递了什么参数。
// （实例表）UTC标准时间戳，哪一个类的哪一个实例在什么时候对哪一个类的什么实例进行了修改/调用了函数，传递了什么参数。
// 【暂缓】这个其实不是很着急。

public static class ScriptInstanceRegistry
{
	[EditorBrowsable(EditorBrowsableState.Never)]
	[Obsolete("Internal class. You should NOT use it directly in normal scenarios. Directly use may cause unexpected results.")]
	internal class TypeBucket(Type currentType, bool isExact)
	{
		internal readonly Type CurrentType = currentType;
		internal readonly bool IsExact = isExact;
		internal uint ModificationVersion;
		
		private readonly List<(ulong InstanceId, WeakReference<GodotObject> InstanceWeakRef)> _activeInstances = [];
		private readonly Dictionary<int, int> _indexBySlot = [];

		internal void Add<T>(int slot, ulong instanceId, T instance) where T : GodotObject
		{
			_activeInstances.Add((instanceId, new WeakReference<GodotObject>(instance)));
			_indexBySlot.Add(slot, _activeInstances.Count - 1);
			ModificationVersion++;
		}
		
		internal void Remove(int slot)
		{
			if (!_indexBySlot.Remove(slot, out int index)) return;

			int lastIndex = _activeInstances.Count - 1;
			if (index < lastIndex)
			{
				var lastItem = _activeInstances[lastIndex];
				_activeInstances[index] = lastItem;
				_indexBySlot[ExtractSlotIndex(lastItem.InstanceId)] = index;
			}

			_activeInstances.RemoveAt(lastIndex);
			ModificationVersion++;
		}
		
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal bool Contains(int slot) => _indexBySlot.TryGetValue(slot, out _);
		
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal bool TryGetInstance(int slot, out ulong instanceId, [MaybeNullWhen(false)] out GodotObject instance)
		{
			instanceId = 0;
			instance = null;
			if (!_indexBySlot.TryGetValue(slot, out int index)) return false;
			
			(instanceId, var instanceWeakRef) = _activeInstances[index];
			return instanceWeakRef.TryGetValid(out instance);
		}

		#region BucketEnumerator
		internal BucketEnumerator GetEnumerator() => new BucketEnumerator(this);
		internal ref struct BucketEnumerator
		{
			internal bool IsInitialized => _currentBucket is not null;
			
			private readonly TypeBucket _currentBucket;
			private int _candidateIndex;
			
			internal (ulong instanceId, GodotObject instance) Current
			{
				get;
				private set;
			} = default!;
			
			internal BucketEnumerator(TypeBucket currentBucket)
			{
				_currentBucket = currentBucket;
				_candidateIndex = -1;
			}

			internal bool MoveNext()
			{
				while (true)
				{
					_candidateIndex++;
					if (_candidateIndex >= _currentBucket._activeInstances.Count) break;
					
					(ulong instanceId, var instanceWeakRef) = _currentBucket._activeInstances[_candidateIndex];
					if (!instanceWeakRef.TryGetValid(out var instance)) continue;
					Current = (instanceId, instance);
					return true;
				}
				Current = default!;
				return false;
			}
		}
		#endregion
	}
	
	/// <summary>
	/// 精确查询用，TypeBucket里只包含当前类的实例，不包含子类的实例
	/// </summary>
	private readonly static Dictionary<Type, TypeBucket> ExactBucketByType = [];
	
	/// <summary>
	/// 多态查询用，TypeBucket里包含了当前类及其子类的实例
	/// </summary>
	private readonly static Dictionary<Type, TypeBucket> BucketByType = [];
	
	private static TypeBucket[]?[] _registeredBucketsBySlot = new TypeBucket[]?[256];
	/// <summary>
	/// 哪怕这个缓存只在Register里使用一次，也绝对不能删掉这个缓存。<br/>
	/// 因为类型数量是固定的，但是实例数量是不定的。<br/>
	/// 缓存后注册新实例时拿继承链只需要消耗一次且只需要17ns，但如果不缓存，+20ns/继承层/实例注册。（基准平均数据，只作对比用）<br/>
	///
	/// CachedRegisteredBuckets[Type][0]：精确查询用，是ExactBucketByType[当前类]的引用<br/>
	/// CachedRegisteredBuckets[Type][1~n]：多态查询用，是BucketByType[当前类及其所有基类和接口]的引用<br/>
	/// </summary>
	private readonly static Dictionary<Type, TypeBucket[]> CachedRegisteredBuckets = [];

	/// <summary>
	/// bit:0~23 slot, bit:24~62 validator, bit:63 RefCounted flag<br/>
	/// 取前24位作为slot标记，24~62位防槽位重用<br/>
	/// 详见 Godot 4.7.2 object.h:874-893<br/>
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static int ExtractSlotIndex(ulong instanceId) => (int)(instanceId & 0xFFFFFFUL);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static ulong ExtractValidator(ulong instanceId) => (instanceId >> 24) & 0x7FFFFFFFFFUL;

	// 这部分的注册与注销是给Fody和Source Generator使用的入口，正常情况下开发开发者不能使用。
	// 实例自动注册与注销的覆盖范围包括继承自GodotObject的所有类（包括RefCounted及其子类）
	#region Register/Unregister
	[EditorBrowsable(EditorBrowsableState.Never)]
	[Obsolete("Internal Method. Instance registering is automatically completed. You should NOT use this method manually. Directly use may cause unexpected results.")]
	public static void Register<T>(T instance, ulong instanceId) where T : GodotObject
	{
		if (instanceId == 0) return;

		int slot = ExtractSlotIndex(instanceId);
		if (slot >= _registeredBucketsBySlot.Length)
		{
			int capacity = _registeredBucketsBySlot.Length;
			while (capacity <= slot)
			{
				capacity <<= 1;
			}
			
			Array.Resize(ref _registeredBucketsBySlot, capacity);
		}
		
		if (slot < _registeredBucketsBySlot.Length && _registeredBucketsBySlot[slot] is not null) return;

		var currentType = instance.GetType();
		var chain = CachedRegisteredBuckets.GetOrAdd(currentType, type => {
			var interfaces = type.GetInterfaces();
			var builder = new List<TypeBucket>(12)
			{
				ExactBucketByType.GetOrAdd(type, _ => new TypeBucket(type, true))
			};
			for (Type? currentType1 = type; currentType1 is not null && currentType1 != typeof(object); currentType1 = currentType1.BaseType)
			{
				builder.Add(BucketByType.GetOrAdd(currentType1, currentType2 => new TypeBucket(currentType2, false)));
			}
			foreach (Type interface1 in interfaces)
			{
				builder.Add(BucketByType.GetOrAdd(interface1, _ => new TypeBucket(interface1, false)));
			}
			return [.. builder];
		});
		_registeredBucketsBySlot[slot] = chain;

		foreach (TypeBucket bucket in chain)
		{
			bucket.Add(slot, instanceId, instance);
		}
	}
	
	/// <returns>true为成功注销，false则之前已经注销或本次注销操作无效</returns>
	[EditorBrowsable(EditorBrowsableState.Never)]
	[Obsolete("Internal Method. Instance unregistering is automatically completed. You should NOT use this method manually. Directly use may cause unexpected results.")]
	public static bool Unregister(ulong instanceId)
	{
		if (instanceId == 0) return false;

		int slot = ExtractSlotIndex(instanceId);
		if (slot >= _registeredBucketsBySlot.Length) return false;

		var chain = _registeredBucketsBySlot[slot];
		if (chain is null) return false;
		
		foreach (TypeBucket bucket in chain)
		{
			bucket.Remove(slot);
		}
		
		_registeredBucketsBySlot[slot] = null;
		return true;
	}
	#endregion

	#region Resolve
	public readonly struct InstanceHandle : IEquatable<InstanceHandle>
	{
		internal readonly int Slot;
		internal readonly ulong Validator;
		private readonly bool _flag;
		
		private readonly WeakReference<GodotObject> _instance;
		
		internal InstanceHandle(ulong instanceId, GodotObject instance)
		{
			Slot = ExtractSlotIndex(instanceId);
			Validator = ExtractValidator(instanceId);
			_flag = (instanceId >> 63) != 0;
			_instance = new WeakReference<GodotObject>(instance);
		}
		
		public bool Equals(InstanceHandle other) => Slot == other.Slot && Validator == other.Validator;
		public override bool Equals(object? obj) => obj is InstanceHandle other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(Slot, Validator);
		
		public ulong AsId()
		{
			ulong id = (uint)Slot & 0xFFFFFFUL;
			id |= (Validator & 0x7FFFFFFFFFUL) << 24;
			if (_flag)
			{
				id |= 1UL << 63;
			}
			
			if (Slot < _registeredBucketsBySlot.Length &&
			    _registeredBucketsBySlot[Slot] is { } chain &&
			    chain[0].TryGetInstance(Slot, out ulong instanceId, out _) &&
			    instanceId == id)
			{
				return id;
			}
			return 0;
		}
	
		public T AsInstance<T>() where T : class
			=> TryAsInstance<T>(out var inst)
				? inst
				: throw new ArgumentException($"[InstanceHandle.AsInstance] Type: {typeof(T).Name} Invalid instance handle");

		public bool TryAsInstance<T>([MaybeNullWhen(false)] out T instance) where T : class
		{
			if (_instance.TryGetValid(out var inst) &&
			    inst is T typed &&
			    Slot < _registeredBucketsBySlot.Length &&
			    _registeredBucketsBySlot[Slot] is { } chain &&
			    chain[0].TryGetInstance(Slot, out ulong instanceId, out _) &&
			    ExtractSlotIndex(instanceId) == Slot &&
			    ExtractValidator(instanceId) == Validator)
			{
				instance = typed;
				return true;
			}
			instance = null;
			return false;
		}
	}
	#endregion

	#region Query/TryQuery
	public static InstanceQuery Query<T>() where T : class 
		=> new InstanceQuery([BucketByType.GetOrAdd(typeof(T), _ => new TypeBucket(typeof(T), false))]);
	public static InstanceQuery QueryExact<T>() where T : class 
		=> new InstanceQuery([ExactBucketByType.GetOrAdd(typeof(T), _ => new TypeBucket(typeof(T), true))]);
	
	public static bool TryQuery<T>(out InstanceQuery query) where T : class
	{
		if (BucketByType.TryGetValue(typeof(T), out TypeBucket? bucket))
		{
			query = new InstanceQuery([bucket]);
			return true;
		}
		query = default;
		return false;
	}
	public static bool TryQueryExact<T>(out InstanceQuery query) where T : class
	{
		if (ExactBucketByType.TryGetValue(typeof(T), out TypeBucket? bucket))
		{
			query = new InstanceQuery([bucket]);
			return true;
		}
		query = default;
		return false;
	}
	#endregion
	
	public readonly struct InstanceQuery
	{
		private readonly TypeBucket[] _fromBuckets;
		private readonly TypeBucket[] _requireBuckets;
		private readonly TypeBucket[] _excludeBuckets;
		private readonly Func<GodotObject, bool>? _predicate;
		
		[EditorBrowsable(EditorBrowsableState.Never)]
		[Obsolete("Internal Constructor. Use 'ScriptInstanceRegistry.Query<T>()' instead. Directly use may cause unexpected results.")]
		internal InstanceQuery(
			TypeBucket[] fromBuckets,
			TypeBucket[]? requireBuckets = null,
			TypeBucket[]? excludeBuckets = null,
			Func<GodotObject, bool>? predicate = null)
		{
			_fromBuckets = fromBuckets;
			_requireBuckets = requireBuckets ?? [];
			_excludeBuckets = excludeBuckets ?? [];
			_predicate = predicate;
		}
		
		#region InstanceEnumerator
		public InstanceEnumerator GetEnumerator() => new InstanceEnumerator(this);
		public ref struct InstanceEnumerator
		{
			private readonly TypeBucket[] _fromBuckets;
			private readonly TypeBucket[] _requireBuckets;
			private readonly TypeBucket[] _excludeBuckets;
			private readonly Func<GodotObject, bool>? _predicate;
			
			private TypeBucket.BucketEnumerator _bucketEnumerator;
			private int _fromBucketsIndex;
			private uint _currentExpectedVersion;
			
			public InstanceHandle Current
			{
				get;
				private set;
			} = default;

			internal GodotObject CurrentInstance
			{
				get;
				private set;
			} = null!;
			
			internal InstanceEnumerator(InstanceQuery query)
			{
				_fromBuckets = query._fromBuckets;
				_requireBuckets = query._requireBuckets;
				_excludeBuckets = query._excludeBuckets;
				_predicate = query._predicate;

				_fromBucketsIndex = 0;
				_currentExpectedVersion = query._fromBuckets.Length >= 1 ? query._fromBuckets[0].ModificationVersion : 0;
			}
			
			public bool MoveNext()
			{
				while (_fromBucketsIndex < _fromBuckets.Length)
				{
					TypeBucket bucket = _fromBuckets[_fromBucketsIndex];

					if (_currentExpectedVersion != bucket.ModificationVersion)
					{
						throw new InvalidOperationException($"Bucket is modified during enumeration.");
					}

					if (!_bucketEnumerator.IsInitialized)
					{
						_bucketEnumerator = bucket.GetEnumerator();
					}

					while (_bucketEnumerator.MoveNext())
					{
						(ulong instanceId, GodotObject instance) = _bucketEnumerator.Current;
						int slot = ExtractSlotIndex(instanceId);

						if (_requireBuckets.Length != 0)
						{
							bool required = true;
							foreach (TypeBucket bucket1 in _requireBuckets)
							{
								if (bucket1.Contains(slot)) continue;
								required = false;
								break;
							}
							if (!required) continue;
						}

						if (_excludeBuckets.Length != 0)
						{
							bool excluded = false;
							foreach (var bucket1 in _excludeBuckets)
							{
								if (!bucket1.Contains(slot)) continue;
								excluded = true;
								break;
							}
							if (excluded) continue;
						}

						if (_fromBucketsIndex > 0)
						{
							bool alreadyYielded = false;
							for (int i = 0; i < _fromBucketsIndex; i++)
							{
								if (!_fromBuckets[i].Contains(slot)) continue;
								alreadyYielded = true;
								break;
							}
							if (alreadyYielded) continue;
						}
						
						if (_predicate is not null && !_predicate(instance)) continue;
						
						Current = new InstanceHandle(instanceId, instance);
						CurrentInstance = instance;
						return true;
					}
					
					_fromBucketsIndex++;
					if (_fromBucketsIndex < _fromBuckets.Length)
					{
						_currentExpectedVersion = _fromBuckets[_fromBucketsIndex].ModificationVersion;
					}
					
					_bucketEnumerator = new TypeBucket.BucketEnumerator(null!);
				}
				Current = default!;
				CurrentInstance = null!;
				return false;
			}
		}
		#endregion
		
		private static TypeBucket[] AddForUnion(TypeBucket[] list, TypeBucket bucket)
		{
			foreach (TypeBucket existing in list)
			{
				if (ReferenceEquals(bucket, existing) || !existing.IsExact && existing.CurrentType.IsAssignableFrom(bucket.CurrentType)) return list;
			}

			var result = new TypeBucket[list.Length + 1];
			int writeIndex = 0;
			foreach (var existing in list)
			{
				if (!bucket.IsExact && bucket.CurrentType.IsAssignableFrom(existing.CurrentType)) continue;
				
				result[writeIndex] = existing;
				writeIndex++;
			}
			result[writeIndex] = bucket;
			if (writeIndex == list.Length) return result;
			
			var trimmed = new TypeBucket[writeIndex + 1];
			Array.Copy(result, trimmed, writeIndex + 1);
			return trimmed;
		}

		private static TypeBucket[] AddForIntersection(TypeBucket[] list, TypeBucket bucket)
		{
			var requireBuckets = new TypeBucket[list.Length + 1];
			Array.Copy(list, requireBuckets, list.Length);
			requireBuckets[^1] = bucket;
			return requireBuckets;
		}

		// 无顺序按类型/接口粗筛：
		//  - From / FromExact: 并集
		//  - Required / RequiredExact: 交集
		//  - Exclude / ExcludeExact: 排除
		// 结尾不带Exact为多态过滤，过滤项为当前类及其子类和接口
		// 结尾带Exact为精确过滤，过滤项仅为当前类，不包含子类和接口
		// 
		// 之所以不用Include做并集主要是因为Include存在歧义。
		// Include可能作为Cpp风格的include并集，也可能作为C# LINQ风格的交集
		// 所以用更直观的From代替，表示增加来源。
		#region ECS-Style-Filter
		// 并集
		public InstanceQuery From<T1, T2, T3>()
			where T1 : class where T2 : class where T3 : class
			=> From<T1>().From<T2>().From<T3>();
		public InstanceQuery From<T1, T2>()
			where T1 : class where T2 : class
			=> From<T1>().From<T2>();
		public InstanceQuery From<T>() where T : class
			=> new InstanceQuery(AddForUnion(_fromBuckets, BucketByType.GetOrAdd(typeof(T), _ => new TypeBucket(typeof(T), false))),
				_requireBuckets,
				_excludeBuckets,
				_predicate);
		
		public InstanceQuery FromExact<T1, T2, T3>()
			where T1 : class where T2 : class where T3 : class
			=> FromExact<T1>().FromExact<T2>().FromExact<T3>();
		public InstanceQuery FromExact<T1, T2>()
			where T1 : class where T2 : class
			=> FromExact<T1>().FromExact<T2>();
		public InstanceQuery FromExact<T>() where T : class
			=> new InstanceQuery(AddForUnion(_fromBuckets, ExactBucketByType.GetOrAdd(typeof(T), _ => new TypeBucket(typeof(T), true))),
				_requireBuckets,
				_excludeBuckets,
				_predicate);

		// 交集
		public InstanceQuery Required<T1, T2, T3>()
			where T1 : class where T2 : class where T3 : class
			=> Required<T1>().Required<T2>().Required<T3>();
		public InstanceQuery Required<T1, T2>()
			where T1 : class where T2 : class
			=> Required<T1>().Required<T2>();
		public InstanceQuery Required<T>() where T : class
			=> new InstanceQuery(_fromBuckets,
				AddForIntersection(_requireBuckets, BucketByType.GetOrAdd(typeof(T), _ => new TypeBucket(typeof(T), false))),
				_excludeBuckets,
				_predicate);
		
		public InstanceQuery RequiredExact<T1, T2, T3>()
			where T1 : class where T2 : class where T3 : class
			=> RequiredExact<T1>().RequiredExact<T2>().RequiredExact<T3>();
		public InstanceQuery RequiredExact<T1, T2>()
			where T1 : class where T2 : class
			=> RequiredExact<T1>().RequiredExact<T2>();
		public InstanceQuery RequiredExact<T>() where T : class
			=> new InstanceQuery(_fromBuckets,
				AddForIntersection(_requireBuckets, ExactBucketByType.GetOrAdd(typeof(T), _ => new TypeBucket(typeof(T), true))),
				_excludeBuckets,
				_predicate);
		
		// 排除
		public InstanceQuery Exclude<T1, T2, T3>()
			where T1 : class where T2 : class where T3 : class
			=> Exclude<T1>().Exclude<T2>().Exclude<T3>();
		public InstanceQuery Exclude<T1, T2>()
			where T1 : class where T2 : class
			=> Exclude<T1>().Exclude<T2>();
		public InstanceQuery Exclude<T>() where T : class
			=> new InstanceQuery(_fromBuckets,
				_requireBuckets,
				AddForUnion(_excludeBuckets, BucketByType.GetOrAdd(typeof(T), _ => new TypeBucket(typeof(T), false))),
				_predicate);
		
		public InstanceQuery ExcludeExact<T1, T2, T3>()
			where T1 : class where T2 : class where T3 : class
			=> ExcludeExact<T1>().ExcludeExact<T2>().ExcludeExact<T3>();
		public InstanceQuery ExcludeExact<T1, T2>()
			where T1 : class where T2 : class
			=> ExcludeExact<T1>().ExcludeExact<T2>();
		public InstanceQuery ExcludeExact<T>() where T : class
			=> new InstanceQuery(_fromBuckets,
				_requireBuckets,
				AddForUnion(_excludeBuckets, ExactBucketByType.GetOrAdd(typeof(T), _ => new TypeBucket(typeof(T), true))),
				_predicate);
		#endregion
		
		public InstanceQuery Where(Func<GodotObject, bool> predicate1)
		{
			ArgumentNullException.ThrowIfNull(predicate1);
			if (_predicate is null) return new InstanceQuery(_fromBuckets, _requireBuckets, _excludeBuckets, predicate1);
			
			var existingPredicate = _predicate;
			return new InstanceQuery(_fromBuckets, _requireBuckets, _excludeBuckets, candidate => existingPredicate(candidate) && predicate1(candidate));
		}
		
		public bool Contains(InstanceHandle handle)
		{
			foreach (TypeBucket bucket in _fromBuckets)
			{
				if (!bucket.TryGetInstance(handle.Slot, out var instanceId, out var instance)) continue;
				
				foreach (var bucket1 in _requireBuckets)
				{
					if (!bucket1.Contains(handle.Slot)) return false;
				}

				foreach (var bucket1 in _excludeBuckets)
				{
					if (bucket1.Contains(handle.Slot)) return false;
				}
				
				if (ExtractValidator(instanceId) != handle.Validator) return false;
				return _predicate is null || _predicate(instance);
			}
			return false;
		}
		
		#region NoPredicate
		public int Count() => Count(static _ => true);
		public bool Any() => First(static _ => true, out _);
		public bool All() => All(static _ => true);
		public bool First(out GodotObject? instance) => First(static _ => true, out instance);
		public GodotObject Single() => Single(static _ => true);
		public bool TrySingle(out GodotObject? instance) => TrySingle(static _ => true, out instance);
		#endregion

		#region HasPredicate
		public int Count(Func<GodotObject, bool> predicate1)
		{
			ArgumentNullException.ThrowIfNull(predicate1);
			int matchCount = 0;
			var e = GetEnumerator();
			while (e.MoveNext())
			{
				if (predicate1(e.CurrentInstance))
				{
					matchCount++;
				}
			}
			return matchCount;
		}
		
		public bool First(Func<GodotObject, bool> predicate1, out GodotObject? instance)
		{
			ArgumentNullException.ThrowIfNull(predicate1);
			var e = GetEnumerator();
			while (e.MoveNext())
			{
				if (!predicate1(e.CurrentInstance)) continue;
				instance = e.CurrentInstance;
				return true;
			}
			instance = null;
			return false;
		}
		
		public bool Any(Func<GodotObject, bool> predicate1) => First(predicate1, out _);
		
		public bool All(Func<GodotObject, bool> predicate1)
		{
			ArgumentNullException.ThrowIfNull(predicate1);
			var e = GetEnumerator();
			while (e.MoveNext())
			{
				if (!predicate1(e.CurrentInstance))
				{
					return false;
				}
			}
			return true;
		}
		
		public GodotObject Single(Func<GodotObject, bool> predicate1)
		{
			ArgumentNullException.ThrowIfNull(predicate1);
			GodotObject? matchedInstance = null;
			bool hasFoundMatch = false;
			var e = GetEnumerator();
			while (e.MoveNext())
			{
				if (!predicate1(e.CurrentInstance)) continue;
				if (hasFoundMatch) throw new InvalidOperationException($"{nameof(GodotObject)}: more than one match");
				matchedInstance = e.CurrentInstance;
				hasFoundMatch = true;
			}
			return !hasFoundMatch ? throw new InvalidOperationException($"{nameof(GodotObject)}: no match") : matchedInstance!;
		}
		
		public bool TrySingle(Func<GodotObject, bool> predicate1, out GodotObject? instance)
		{
			ArgumentNullException.ThrowIfNull(predicate1);
			instance = null;
			var e = GetEnumerator();
			while (e.MoveNext())
			{
				if (!predicate1(e.CurrentInstance)) continue;
				if (instance is not null)
				{
					instance = null;
					return false;
				}
				instance = e.CurrentInstance;
			}
			return (instance is not null);
		}
		#endregion
	}
}