using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace _0023iae;

/// <summary>
/// 取代 GAME 树 <c>-iae\-hae.cs</c> (Lidgren 的 NetBitVector, 82 行)。
///
/// 唯一改动在索引器的 setter。反编译产物是:
///
///     set { Set(num, flag); }        // CS0103: num / flag 都不存在
///
/// ILSpy 没能还原索引器 setter 的参数名 —— 索引参数是 <c>P_0</c>, 值参数是隐式的
/// <c>value</c>, 它却沿用了 <c>Set(int, bool)</c> 方法体里的局部变量名。
/// 对照同一个索引器的 getter (<c>return Get(P_0);</c>) 可知正确写法是
/// <c>Set(P_0, value)</c> —— 无歧义。
///
/// 另一处与原文的差异: 类型声明从 internal 改成 public。清洗脚本会把整棵树的
/// internal 一律放宽成 public (原因见 tools\sanitize_decompiled.py), 而这个文件
/// 绕过了脚本, 所以要手动跟上 —— 否则它会成为唯一一个"不够 public"的类型,
/// 让引用它的 public 字段触发 CS0052。
///
/// 其余部分逐字照抄, 未作任何改动。重新反编译后请核对本文件是否还需要。
/// </summary>
public sealed class _0023hae
{
	private readonly int _0023cae;

	private readonly int[] _0023dae;

	private int _0023eae;

	[IndexerName("Bit")]
	public bool this[int P_0]
	{
		get
		{
			return Get(P_0);
		}
		set
		{
			Set(P_0, value);   // ← 唯一的修正, 原文是 Set(num, flag)
		}
	}

	public _0023hae(int P_0)
	{
		_0023cae = P_0;
		_0023dae = new int[(P_0 + 31) / 32];
	}

	public int _0023SS()
	{
		return _0023eae;
	}

	public bool Get(int P_0)
	{
		return (_0023dae[P_0 / 32] & (1 << P_0 % 32)) != 0;
	}

	public void Set(int P_0, bool P_1)
	{
		int num = P_0 / 32;
		if (P_1)
		{
			if ((_0023dae[num] & (1 << P_0 % 32)) == 0)
			{
				_0023eae++;
			}
			_0023dae[num] |= 1 << P_0 % 32;
		}
		else
		{
			if ((_0023dae[num] & (1 << P_0 % 32)) != 0)
			{
				_0023eae--;
			}
			_0023dae[num] &= ~(1 << P_0 % 32);
		}
	}

	public void Clear()
	{
		Array.Clear(_0023dae, 0, _0023dae.Length);
		_0023eae = 0;
	}

	public override string ToString()
	{
		StringBuilder stringBuilder = new StringBuilder(_0023cae + 2);
		stringBuilder.Append('[');
		for (int i = 0; i < _0023cae; i++)
		{
			stringBuilder.Append(Get(_0023cae - i - 1) ? '1' : '0');
		}
		stringBuilder.Append(']');
		return stringBuilder.ToString();
	}
}
