using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

public class DoublyLinkedList<T> : IEnumerable<T>, IEnumerable
{
	public struct CLDIterator(DoublyLinkedListNode<T> node) : IEnumerator<T>, IDisposable, IEnumerator
	{
		private DoublyLinkedListNode<T> last = null;

		private DoublyLinkedListNode<T> next = node;

		// [DIE_Reborn FIXUP] ILSpy 原样输出的是 `start = next`, 但字段初始值设定项
		// 不能引用另一个实例字段 (CS0236) —— 这是反编译产物, 不是合法 C#。
		// 上一行 `next = node`, 所以 `start = node` 与原意等价, 语义零变化。
		private DoublyLinkedListNode<T> start = node;

		public T Current => next.Value;

		object IEnumerator.Current => Current;

		public bool MoveNext()
		{
			if ((last = next) == null)
			{
				return false;
			}
			next = next.forward();
			return next != null;
		}

		public void Reset()
		{
			last = null;
			next = start;
		}

		public void Dispose()
		{
		}
	}

	private const long serialVersionUID = 876323262645176354L;

	private readonly DoublyLinkedListNode<T> header;

	private readonly DoublyLinkedListNode<T> trailer;

	public int Count
	{
		get
		{
			long num = 0L;
			for (DoublyLinkedListNode<T> doublyLinkedListNode = header.forward(); doublyLinkedListNode != null; doublyLinkedListNode = doublyLinkedListNode.forward())
			{
				num++;
			}
			if (num < int.MaxValue)
			{
				return (int)num;
			}
			return int.MaxValue;
		}
	}

	private static bool usable(DoublyLinkedListNode<T> n)
	{
		if (n != null)
		{
			return !n.isSpecial();
		}
		return false;
	}

	private static void checkNullArg(object v)
	{
		if (v == null)
		{
			throw new NullReferenceException();
		}
	}

	private T screenNullResult(T v)
	{
		if (v == null)
		{
			throw new InvalidOperationException();
		}
		return v;
	}

	private List<T> toArrayList()
	{
		List<T> list = new List<T>();
		for (DoublyLinkedListNode<T> doublyLinkedListNode = header.forward(); doublyLinkedListNode != null; doublyLinkedListNode = doublyLinkedListNode.forward())
		{
			list.Add(doublyLinkedListNode.Value);
		}
		return list;
	}

	public DoublyLinkedList()
	{
		DoublyLinkedListNode<T> doublyLinkedListNode = new DoublyLinkedListNode<T>(default(T), null, null);
		DoublyLinkedListNode<T> next = new DoublyLinkedListNode<T>(default(T), null, doublyLinkedListNode);
		doublyLinkedListNode.setNext(next);
		header = doublyLinkedListNode;
		trailer = next;
	}

	public DoublyLinkedList(IEnumerable<T> c)
		: this()
	{
		addAll(c);
	}

	public void addFirst(T o)
	{
		checkNullArg(o);
		while (header.append(o) == null)
		{
		}
	}

	public DoublyLinkedListNode<T> AddValue(T o)
	{
		checkNullArg(o);
		DoublyLinkedListNode<T> result;
		while ((result = header.append(o)) == null)
		{
		}
		return result;
	}

	public DoublyLinkedListNode<T> AddLast(T o)
	{
		checkNullArg(o);
		DoublyLinkedListNode<T> result;
		while ((result = trailer.prepend(o)) == null)
		{
		}
		return result;
	}

	public bool offerFirst(T o)
	{
		addFirst(o);
		return true;
	}

	public bool offerLast(T o)
	{
		AddLast(o);
		return true;
	}

	public T peekFirst()
	{
		DoublyLinkedListNode<T> doublyLinkedListNode = header.successor();
		if (doublyLinkedListNode != null)
		{
			return doublyLinkedListNode.Value;
		}
		return default(T);
	}

	public T peekLast()
	{
		DoublyLinkedListNode<T> doublyLinkedListNode = trailer.predecessor();
		if (doublyLinkedListNode != null)
		{
			return doublyLinkedListNode.Value;
		}
		return default(T);
	}

	public T getFirst()
	{
		return screenNullResult(peekFirst());
	}

	public T getLast()
	{
		return screenNullResult(peekLast());
	}

	public T pollFirst()
	{
		DoublyLinkedListNode<T> doublyLinkedListNode;
		do
		{
			doublyLinkedListNode = header.successor();
			if (!usable(doublyLinkedListNode))
			{
				return default(T);
			}
		}
		while (!doublyLinkedListNode.delete());
		return doublyLinkedListNode.Value;
	}

	public T pollLast()
	{
		DoublyLinkedListNode<T> doublyLinkedListNode;
		do
		{
			doublyLinkedListNode = trailer.predecessor();
			if (!usable(doublyLinkedListNode))
			{
				return default(T);
			}
		}
		while (!doublyLinkedListNode.delete());
		return doublyLinkedListNode.Value;
	}

	public T removeFirst()
	{
		return screenNullResult(pollFirst());
	}

	public T removeLast()
	{
		return screenNullResult(pollLast());
	}

	public bool offer(T e)
	{
		return offerLast(e);
	}

	public DoublyLinkedListNode<T> Insert(T toInsert, DoublyLinkedListNode<T> insertPosition)
	{
		return insertPosition.append(toInsert);
	}

	public T poll()
	{
		return pollFirst();
	}

	public T peek()
	{
		return peekFirst();
	}

	public T element()
	{
		return getFirst();
	}

	public void push(T e)
	{
		addFirst(e);
	}

	public T pop()
	{
		return removeFirst();
	}

	public bool removeFirstOccurrence(T o, Func<T, T, bool> comparer)
	{
		checkNullArg(o);
		DoublyLinkedListNode<T> doublyLinkedListNode;
		do
		{
			doublyLinkedListNode = header.forward();
			while (true)
			{
				if (doublyLinkedListNode == null)
				{
					return false;
				}
				if (comparer(doublyLinkedListNode.Value, o))
				{
					break;
				}
				doublyLinkedListNode = doublyLinkedListNode.forward();
			}
		}
		while (!doublyLinkedListNode.delete());
		return true;
	}

	public bool removeLastOccurrence(T o, Func<T, T, bool> comparer)
	{
		checkNullArg(o);
		while (true)
		{
			DoublyLinkedListNode<T> doublyLinkedListNode = trailer;
			while (true)
			{
				DoublyLinkedListNode<T> doublyLinkedListNode2 = doublyLinkedListNode.back();
				if (doublyLinkedListNode.isDeleted() || (doublyLinkedListNode2 != null && doublyLinkedListNode2.successor() != doublyLinkedListNode))
				{
					break;
				}
				if (doublyLinkedListNode2 == null)
				{
					return false;
				}
				if (comparer(o, doublyLinkedListNode2.Value))
				{
					if (!doublyLinkedListNode2.delete())
					{
						break;
					}
					return true;
				}
				doublyLinkedListNode = doublyLinkedListNode2;
			}
		}
	}

	public bool contains(T o, Func<T, T, bool> comparer)
	{
		if (o == null)
		{
			return false;
		}
		for (DoublyLinkedListNode<T> doublyLinkedListNode = header.forward(); doublyLinkedListNode != null; doublyLinkedListNode = doublyLinkedListNode.forward())
		{
			if (comparer(o, doublyLinkedListNode.Value))
			{
				return true;
			}
		}
		return false;
	}

	public bool isEmpty()
	{
		return !usable(header.successor());
	}

	public bool removeValue(T o, Func<T, T, bool> comparer)
	{
		return removeFirstOccurrence(o, comparer);
	}

	public bool Remove(ref DoublyLinkedListNode<T> node)
	{
		DoublyLinkedListNode<T> doublyLinkedListNode = node;
		if (doublyLinkedListNode == null)
		{
			return false;
		}
		if (Interlocked.CompareExchange(ref node, null, doublyLinkedListNode) != doublyLinkedListNode)
		{
			return false;
		}
		bool result = doublyLinkedListNode.delete();
		node = null;
		return result;
	}

	public bool addAll(IEnumerable<T> c)
	{
		IEnumerator<T> enumerator = c.GetEnumerator();
		if (!enumerator.MoveNext())
		{
			return false;
		}
		do
		{
			AddLast(enumerator.Current);
		}
		while (enumerator.MoveNext());
		return true;
	}

	public void clear()
	{
		while (pollFirst() != null)
		{
		}
	}

	public T[] toArray()
	{
		return toArrayList().ToArray();
	}

	public CLDIterator iterator()
	{
		return new CLDIterator(header);
	}

	public IEnumerator<T> GetEnumerator()
	{
		return iterator();
	}

	IEnumerator IEnumerable.GetEnumerator()
	{
		return GetEnumerator();
	}
}
