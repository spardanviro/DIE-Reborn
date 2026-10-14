using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using StunCore.Concurrency;
using StunCore.Formatting;

// [DIE_Reborn FIXUP] 本文件是 StunCore_SourceCode\StunCore\StunLog.cs 的副本。
// 唯一改动见下方 "FIXUP" 注释处 (PerformanceCounter)。
// 重新反编译 StunCore.dll 后需要重新套用该改动。

namespace StunCore;

public static class StunLog
{
	private class LogThreadStaticData
	{
		private int _ThreadID;

		public BufferManager.AllocatedBuffer _WriteBuffer;

		public int _WriteBufferIndex;

		public char[] _EscapeBuffer;

		public int _WriteBufferLock;

		public long _LastFlushTick;

		public LogThreadStaticData()
		{
			_ThreadID = Thread.CurrentThread.ManagedThreadId;
			WeakReference[] threadStaticRefs;
			WeakReference[] array;
			do
			{
				threadStaticRefs = _ThreadStaticRefs;
				int num = ((threadStaticRefs == null) ? 1 : (threadStaticRefs.Length + 1));
				array = new WeakReference[num];
				if (threadStaticRefs != null)
				{
					Array.Copy(threadStaticRefs, array, threadStaticRefs.Length);
				}
				array[array.Length - 1] = new WeakReference(this);
			}
			while (Interlocked.CompareExchange(ref _ThreadStaticRefs, array, threadStaticRefs) != threadStaticRefs);
		}

		~LogThreadStaticData()
		{
			if (_ThreadID != _FlushThread.ManagedThreadId)
			{
				FlushThreadStaticBuffer(this);
				try
				{
					RemoveThreadDataRef(this);
					return;
				}
				catch (Exception ex)
				{
					Console.WriteLine(ex.ToString());
					return;
				}
			}
			WeakReference[] threadStaticRefs = _ThreadStaticRefs;
			int i = 0;
			for (; i < threadStaticRefs.Length; i++)
			{
				try
				{
					if (threadStaticRefs[i] != null && (threadStaticRefs[i].Target is LogThreadStaticData logThreadStaticData && logThreadStaticData != this))
					{
						FlushThreadStaticBuffer(logThreadStaticData);
					}
				}
				catch (Exception)
				{
				}
			}
			List<Stream> list = LogThread_Flush();
			foreach (Stream item in list)
			{
				try
				{
					if (item.CanWrite)
					{
						item.Flush();
					}
				}
				catch (Exception)
				{
				}
			}
		}
	}

	private struct FlushBuffer(BufferManager.AllocatedBuffer buf, int byteCount)
	{
		public readonly BufferManager.AllocatedBuffer Buffer = buf;

		public readonly int ByteCount = byteCount;

		public void Free()
		{
			Buffer.Free();
		}
	}

	private const int WRITE_BUFFER_CHUNK_SIZE = 10240;

	private const int WRITE_BUFFER_CHUNK_COUNT = 100;

	private const int MAX_THREAD_WRITE_BUFFERS = 100;

	[ThreadStatic]
	private static LogThreadStaticData _ThreadStaticData;

	private static BufferManager _BufferManager;

	private static readonly Thread _FlushThread;

	private static AutoResetEvent _ThreadWriteEvent;

	private static ConcurrentQueue<FlushBuffer> _BuffersToFlush;

	private static volatile bool _ExitWriteThread;

	private static volatile List<Stream> _OutputStreams;

	private static readonly string LOGENTRY_TYPESTRING_ERROR;

	private static readonly string LOGENTRY_TYPESTRING_LOG;

	private static readonly string LOGENTRY_TYPESTRING_WARNING;

	private static readonly string LOGENTRY_HEADER_PRETIME;

	private static readonly string LOGENTRY_HEADER_POSTTIME;

	private static readonly string LOGENTRY_HEADER_POSTID;

	private static WeakReference[] _ThreadStaticRefs;

	public static int Errors;

	public static int Warnings;

	public static int Logs;

	[ThreadStatic]
	public static string LogString;

	public static LogLevel LogLevel;

	private static BufferManager.AllocatedBuffer _WriteBuffer
	{
		get
		{
			return _ThreadStaticData._WriteBuffer;
		}
		set
		{
			_ThreadStaticData._WriteBuffer = value;
		}
	}

	private static int _WriteBufferIndex
	{
		get
		{
			return _ThreadStaticData._WriteBufferIndex;
		}
		set
		{
			_ThreadStaticData._WriteBufferIndex = value;
		}
	}

	private static char[] _EscapeBuffer
	{
		get
		{
			return _ThreadStaticData._EscapeBuffer;
		}
		set
		{
			_ThreadStaticData._EscapeBuffer = value;
		}
	}

	public static int NumberOfOutputStreams => _OutputStreams.Count;

	public static event Action<Exception> OnFlushError;

	static StunLog()
	{
		_FlushThread = new Thread(LogThread);
		_ThreadWriteEvent = new AutoResetEvent(initialState: false);
		_BuffersToFlush = new ConcurrentQueue<FlushBuffer>();
		_ExitWriteThread = false;
		_OutputStreams = new List<Stream>();
		LOGENTRY_TYPESTRING_ERROR = "(err";
		LOGENTRY_TYPESTRING_LOG = "(log";
		LOGENTRY_TYPESTRING_WARNING = "(wrn";
		LOGENTRY_HEADER_PRETIME = "\t:t\t";
		LOGENTRY_HEADER_POSTTIME = "\n\t:id\t\"";
		LOGENTRY_HEADER_POSTID = "\"\n\t:data\t(";
		LogLevel = LogLevel.Normal;
		_FlushThread.Name = "StunLogThread";
		_FlushThread.IsBackground = true;
		_FlushThread.Start();
	}

	public static void AddOutputStream(Stream stream)
	{
		WriteHeaderToStream(stream, Assembly.GetCallingAssembly());
		stream.Flush();
		List<Stream> outputStreams = _OutputStreams;
		List<Stream> list = new List<Stream>(outputStreams);
		list.Add(stream);
		_OutputStreams = list;
	}

	private static void WriteHeaderToStream(Stream stream, Assembly callingAssembly)
	{
		StreamWriter streamWriter = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false), 1024);
		Func<string, string> func = (string str) => (str == null) ? string.Empty : ("\"" + str.Replace("\"", "\\\"").Replace("\\", "\\\\").Replace("(", "\\(") + "\"");
		Func<string, string> func2 = (string str) => (str == null) ? string.Empty : ("\n\t:" + str + "\t");
		Assembly assembly = Assembly.GetEntryAssembly();
		if (assembly == null)
		{
			assembly = callingAssembly;
		}
		AssemblyName name = assembly.GetName();
		DateTime utcNow = DateTime.UtcNow;
		bool flag = false;
		try
		{
			flag = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
		}
		catch (Exception)
		{
		}
		string environmentVariable = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
		long value = -1L;
		try
		{
			// [DIE_Reborn FIXUP] 原文为:
			//     using PerformanceCounter performanceCounter = new PerformanceCounter("Memory", "Available MBytes");
			//     value = performanceCounter.RawValue;
			// System.Diagnostics.PerformanceCounter 在 .NET Core 起已移出 BCL, 需额外 NuGet 包且仅限 Windows。
			// 这个值只用于启动横幅里打印一行"可用内存", 与协议无关, 故改用 BCL 内置的 GC 内存信息。
			value = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024L * 1024L);
		}
		catch
		{
		}
		long value2 = -1L;
		try
		{
			string pathRoot = Path.GetPathRoot(Environment.CurrentDirectory);
			DriveInfo driveInfo = new DriveInfo(pathRoot);
			value2 = driveInfo.AvailableFreeSpace;
		}
		catch (Exception)
		{
		}
		int value3 = -1;
		try
		{
			value3 = CultureInfo.CurrentCulture.KeyboardLayoutId;
		}
		catch
		{
		}
		string arg = string.Empty;
		try
		{
			arg = CultureInfo.CurrentCulture.ThreeLetterISOLanguageName;
		}
		catch
		{
		}
		string arg2 = string.Empty;
		try
		{
			arg2 = Environment.UserName;
		}
		catch
		{
		}
		string arg3 = string.Empty;
		try
		{
			arg3 = Environment.OSVersion.ToString();
		}
		catch
		{
		}
		string value4 = string.Empty;
		try
		{
			value4 = Process.GetCurrentProcess().Id.ToString();
		}
		catch
		{
		}
		streamWriter.Write("(header");
		streamWriter.Write(func2("app"));
		streamWriter.Write(func(name.FullName));
		streamWriter.Write(func2("ver"));
		streamWriter.Write(func(name.Version.ToString()));
		streamWriter.Write(func2("start-time"));
		streamWriter.Write(utcNow.Year.ToString("0000") + "-" + utcNow.Month.ToString("00") + "-" + utcNow.Day.ToString("00") + " " + utcNow.Hour.ToString("00") + ":" + utcNow.Minute.ToString("00") + ":" + utcNow.Second.ToString("00"));
		streamWriter.Write(func2("os"));
		streamWriter.Write(func(arg3));
		streamWriter.Write(func2("user"));
		streamWriter.Write(func(arg2));
		streamWriter.Write(func2("admin"));
		streamWriter.Write(func(flag.ToString()));
		streamWriter.Write(func2("locale"));
		streamWriter.Write(func(arg));
		streamWriter.Write(func2("cpu"));
		streamWriter.Write(func(environmentVariable));
		streamWriter.Write(func2("mem"));
		streamWriter.Write(value);
		streamWriter.Write(func2("free-storage"));
		streamWriter.Write(value2);
		streamWriter.Write(func2("keyboard-layout"));
		streamWriter.Write(value3);
		streamWriter.Write(func2("pid"));
		streamWriter.Write(value4);
		streamWriter.Write(")");
		streamWriter.Flush();
	}

	public static bool RemoveOutputStream(Stream stream)
	{
		List<Stream> outputStreams = _OutputStreams;
		List<Stream> list = new List<Stream>(outputStreams);
		bool flag = list.Remove(stream);
		if (flag)
		{
			_OutputStreams = list;
		}
		return flag;
	}

	public static void FlushThreadBuffer()
	{
		while (!TryFlushThreadBuffer(_ThreadStaticData, int.MaxValue))
		{
		}
	}

	private static void WriteEndLogEntry()
	{
		WriteString("))\r\n");
	}

	private static int MaxNumericSize<T0>(T0 data0) where T0 : struct
	{
		if (typeof(T0) == typeof(int) || typeof(T0) == typeof(uint))
		{
			return 16;
		}
		if (typeof(T0) == typeof(byte))
		{
			return 3;
		}
		if (typeof(T0) == typeof(sbyte))
		{
			return 4;
		}
		if (typeof(T0) == typeof(ushort))
		{
			return 5;
		}
		if (typeof(T0) == typeof(short))
		{
			return 6;
		}
		if (typeof(T0) == typeof(long) || typeof(T0) == typeof(ulong))
		{
			return 24;
		}
		if (typeof(T0) == typeof(float) || typeof(T0) == typeof(double))
		{
			return 32;
		}
		return 0;
	}

	private static int MaxObjectSize(object obj)
	{
		string text = obj.ToString();
		return GetEscapeLength(text) + text.Length;
	}

	private static void WriteObject(object data)
	{
		string text = data.ToString();
		if (double.TryParse(text, out var _) || ulong.TryParse(text, out var _))
		{
			WriteString(text);
		}
		else
		{
			WriteEscapedQuotedString(text, GetEscapeLength(text));
		}
	}

	private static void WriteData<T0>(T0 data0) where T0 : struct
	{
		if (typeof(T0) == typeof(int))
		{
			int value = (int)Convert.ToInt64(data0);
			_WriteBufferIndex += NumberFormatting.ToBuf(value, _WriteBuffer.Buffer, _WriteBufferIndex);
		}
		else if (typeof(T0) == typeof(short) || typeof(T0) == typeof(sbyte))
		{
			int value2 = Convert.ToInt32(data0);
			_WriteBufferIndex += NumberFormatting.ToBuf(value2, _WriteBuffer.Buffer, _WriteBufferIndex);
		}
		else if (typeof(T0) == typeof(uint) || typeof(T0) == typeof(ushort) || typeof(T0) == typeof(byte))
		{
			uint value3 = Convert.ToUInt32(data0);
			_WriteBufferIndex += NumberFormatting.ToBuf(value3, _WriteBuffer.Buffer, _WriteBufferIndex);
		}
		else if (typeof(T0) == typeof(long))
		{
			long value4 = Convert.ToInt64(data0);
			_WriteBufferIndex += NumberFormatting.ToBuf(value4, _WriteBuffer.Buffer, _WriteBufferIndex);
		}
		else if (typeof(T0) == typeof(ulong))
		{
			ulong value5 = Convert.ToUInt64(data0);
			_WriteBufferIndex += NumberFormatting.ToBuf(value5, _WriteBuffer.Buffer, _WriteBufferIndex);
		}
		else if (typeof(T0) == typeof(float) || typeof(T0) == typeof(double))
		{
			double value6 = Convert.ToDouble(data0);
			_WriteBufferIndex += NumberFormatting.ToBuf(value6, _WriteBuffer.Buffer, _WriteBufferIndex, 9);
		}
		else
		{
			StunLog.OnFlushError?.Invoke(new Exception("Unknown value type written"));
		}
	}

	private static void EnsureAndLockBuffer(string formatStr, int escapeLength, int numericArgsSize)
	{
		if (_ThreadStaticData == null)
		{
			_ThreadStaticData = new LogThreadStaticData();
			if (_BufferManager == null && Interlocked.CompareExchange(ref _BufferManager, new BufferManager(100, 10240, 100), null) == null)
			{
				_BufferManager.InitBuffer();
				_BufferManager.OnWarn += delegate(string str)
				{
					try
					{
						StunLog.OnFlushError?.Invoke(new Exception(str));
					}
					catch (Exception)
					{
					}
				};
			}
			Interlocked.Exchange(ref _ThreadStaticData._LastFlushTick, Environment.TickCount);
			GetNewWriteBuffer(_ThreadStaticData);
		}
		SpinWait spinWait = default(SpinWait);
		while (Interlocked.CompareExchange(ref _ThreadStaticData._WriteBufferLock, 1, 0) != 0)
		{
			spinWait.SpinOnce();
		}
		try
		{
			int num = _WriteBuffer.Count + _WriteBuffer.Offset - _WriteBufferIndex;
			int byteCount = Encoding.UTF8.GetByteCount(formatStr);
			int maxEntrySize = GetMaxEntrySize(byteCount + escapeLength, numericArgsSize);
			if (num < maxEntrySize)
			{
				FlushThreadStaticBuffer(_ThreadStaticData);
				_ThreadWriteEvent.Set();
				while (!GetNewWriteBuffer(_ThreadStaticData))
				{
					spinWait.SpinOnce();
				}
			}
			if (maxEntrySize >= 10240)
			{
				StunLog.OnFlushError?.Invoke(new Exception("Parameters could result in a string that was larger than a buffer."));
			}
		}
		catch (Exception)
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
			throw;
		}
	}

	private static int GetEscapeLength(string formatStr)
	{
		int num = 0;
		for (int i = 0; i < formatStr.Length; i++)
		{
			if (CharNeedsEscape(formatStr[i]))
			{
				num++;
			}
		}
		return num;
	}

	private static bool GetNewWriteBuffer(LogThreadStaticData tsData)
	{
		bool buffer = _BufferManager.GetBuffer(ref tsData._WriteBuffer);
		if (!buffer)
		{
			StunLog.OnFlushError?.Invoke(new Exception("Error: Unable to get a write buffer from BufferManager"));
		}
		else
		{
			tsData._WriteBufferIndex = tsData._WriteBuffer.Offset;
		}
		return buffer;
	}

	private static int GetMaxEntrySize(int maxFormatStrSize, int numericArgsSize)
	{
		return "(err\t:t\tYYYY-MM-DD HH:mm:SS\n\t:id\t\"\"\n\t:data\t())\r\n".Length + maxFormatStrSize + numericArgsSize;
	}

	private static void WriteLogEntryHeader(string logTypeString, string formatStr, int escapeLength)
	{
		WriteString(logTypeString);
		WriteString(LOGENTRY_HEADER_PRETIME);
		DateTime utcNow = DateTime.UtcNow;
		WritePaddedNumber(utcNow.Year, 4);
		_WriteBuffer.Buffer[_WriteBufferIndex++] = 45;
		WritePaddedNumber(utcNow.Month, 2);
		_WriteBuffer.Buffer[_WriteBufferIndex++] = 45;
		WritePaddedNumber(utcNow.Day, 2);
		_WriteBuffer.Buffer[_WriteBufferIndex++] = 32;
		WritePaddedNumber(utcNow.Hour, 2);
		_WriteBuffer.Buffer[_WriteBufferIndex++] = 58;
		WritePaddedNumber(utcNow.Minute, 2);
		_WriteBuffer.Buffer[_WriteBufferIndex++] = 58;
		WritePaddedNumber(utcNow.Second, 2);
		WriteString(LOGENTRY_HEADER_POSTTIME);
		if (LogString != null)
		{
			WriteString(LogString);
		}
		WriteEscapedString(formatStr, escapeLength);
		WriteString(LOGENTRY_HEADER_POSTID);
	}

	private static void FlushThreadStaticBuffer(LogThreadStaticData ts)
	{
		Interlocked.Exchange(ref ts._LastFlushTick, Environment.TickCount);
		FlushBuffer item = new FlushBuffer(ts._WriteBuffer, ts._WriteBufferIndex - ts._WriteBuffer.Offset);
		_BuffersToFlush.Enqueue(item);
	}

	private static void WriteEscapedString(string str, int escapeLength)
	{
		if (_EscapeBuffer == null)
		{
			_EscapeBuffer = new char[1024];
		}
		if (_EscapeBuffer.Length < str.Length + escapeLength)
		{
			_EscapeBuffer = new char[str.Length + escapeLength];
		}
		int charCount = 0;
		for (int i = 0; i < str.Length; i++)
		{
			if (CharNeedsEscape(str[i]))
			{
				_EscapeBuffer[charCount++] = '\\';
			}
			_EscapeBuffer[charCount++] = str[i];
		}
		_WriteBufferIndex += Encoding.UTF8.GetBytes(_EscapeBuffer, 0, charCount, _WriteBuffer.Buffer, _WriteBufferIndex);
	}

	private static void WriteString(string str)
	{
		_WriteBufferIndex += Encoding.UTF8.GetBytes(str, 0, str.Length, _WriteBuffer.Buffer, _WriteBufferIndex);
	}

	private static void WriteEscapedQuotedString(string str, int escapeLength)
	{
		_WriteBuffer.Buffer[_WriteBufferIndex++] = 34;
		WriteEscapedString(str, escapeLength);
		_WriteBuffer.Buffer[_WriteBufferIndex++] = 34;
	}

	private static bool CharNeedsEscape(char c)
	{
		if (c != '"' && c != '\\')
		{
			return c == '(';
		}
		return true;
	}

	private static void WritePaddedNumber(int num, int maxNumSize)
	{
		int num2 = NumberFormatting.ToBuf(num, _WriteBuffer.Buffer, _WriteBufferIndex);
		for (int num3 = maxNumSize - 1; num3 >= num2; num3--)
		{
			_WriteBuffer.Buffer[_WriteBufferIndex + num3] = _WriteBuffer.Buffer[_WriteBufferIndex + num3 - 1];
		}
		for (int i = 0; i < maxNumSize - num2; i++)
		{
			_WriteBuffer.Buffer[_WriteBufferIndex + i] = 48;
		}
		_WriteBufferIndex += maxNumSize;
	}

	private static void LogThread()
	{
		while (true)
		{
			try
			{
				List<Stream> list;
				do
				{
					_ThreadWriteEvent.WaitOne(1000);
					list = LogThread_Flush();
				}
				while (!_ExitWriteThread);
				foreach (Stream item in list)
				{
					try
					{
						if (item.CanWrite)
						{
							item.Flush();
						}
					}
					catch (Exception obj)
					{
						StunLog.OnFlushError?.Invoke(obj);
					}
				}
				break;
			}
			catch (Exception obj2)
			{
				try
				{
					StunLog.OnFlushError?.Invoke(obj2);
				}
				catch (Exception)
				{
				}
			}
		}
	}

	private static bool TryFlushThreadBuffer(LogThreadStaticData tsData, int maxSpinCount)
	{
		if (tsData == null || tsData._WriteBuffer.Buffer == null)
		{
			return true;
		}
		SpinWait spinWait = default(SpinWait);
		int num = 0;
		while (Interlocked.CompareExchange(ref tsData._WriteBufferLock, 1, 0) != 0)
		{
			spinWait.SpinOnce();
			num++;
			if (num >= maxSpinCount)
			{
				return false;
			}
		}
		try
		{
			FlushThreadBuffer_NOLOCK(tsData, ref spinWait);
			return true;
		}
		finally
		{
			Interlocked.Exchange(ref tsData._WriteBufferLock, 0);
		}
	}

	private static void FlushThreadBuffer_NOLOCK(LogThreadStaticData tsData, ref SpinWait spinWait)
	{
		if (tsData._WriteBufferIndex == tsData._WriteBuffer.Offset)
		{
			Interlocked.Exchange(ref tsData._LastFlushTick, Environment.TickCount);
			return;
		}
		FlushThreadStaticBuffer(tsData);
		_ThreadWriteEvent.Set();
		while (!GetNewWriteBuffer(tsData))
		{
			spinWait.SpinOnce();
		}
	}

	private static List<Stream> LogThread_Flush()
	{
		List<Stream> outputStreams = _OutputStreams;
		FlushBuffer result;
		while (_BuffersToFlush.TryDequeue(out result))
		{
			if (result.ByteCount > 0)
			{
				foreach (Stream item in outputStreams)
				{
					try
					{
						if (item.CanWrite)
						{
							item.Write(result.Buffer.Buffer, result.Buffer.Offset, result.ByteCount);
							item.Flush();
						}
						else
						{
							StunLog.OnFlushError?.Invoke(new Exception("Stream cannot be written to: " + item.ToString()));
							RemoveOutputStream(item);
						}
					}
					catch (Exception obj)
					{
						StunLog.OnFlushError?.Invoke(obj);
						RemoveOutputStream(item);
					}
				}
			}
			result.Free();
		}
		WeakReference[] threadStaticRefs = _ThreadStaticRefs;
		bool flag = false;
		if (threadStaticRefs != null)
		{
			for (int i = 0; i < threadStaticRefs.Length; i++)
			{
				if (threadStaticRefs[i] != null)
				{
					LogThreadStaticData logThreadStaticData = null;
					try
					{
						logThreadStaticData = (LogThreadStaticData)threadStaticRefs[i].Target;
					}
					catch (InvalidOperationException)
					{
					}
					catch (Exception obj2)
					{
						StunLog.OnFlushError?.Invoke(obj2);
					}
					if (logThreadStaticData != null && logThreadStaticData != _ThreadStaticData)
					{
						long num = Interlocked.Read(ref logThreadStaticData._LastFlushTick);
						long num2 = Environment.TickCount;
						if (Math.Sign(num) != Math.Sign(num2))
						{
							Interlocked.Exchange(ref logThreadStaticData._LastFlushTick, num2);
						}
						else if (num + 1000 < num2)
						{
							TryFlushThreadBuffer(logThreadStaticData, 1);
						}
					}
				}
				else
				{
					flag = true;
				}
			}
		}
		if (flag)
		{
			RemoveThreadDataRef(null);
		}
		return outputStreams;
	}

	public static bool WaitForBufferFlush(int msTimeout)
	{
		return SpinWait.SpinUntil(() => _BuffersToFlush.Count == 0, msTimeout);
	}

	private static void RemoveThreadDataRef(LogThreadStaticData tsData)
	{
		WeakReference[] threadStaticRefs;
		WeakReference[] value;
		do
		{
			threadStaticRefs = _ThreadStaticRefs;
			value = ((_ThreadStaticRefs != null) ? _ThreadStaticRefs.Where(delegate(WeakReference d)
			{
				try
				{
					return d.Target is LogThreadStaticData logThreadStaticData && logThreadStaticData != tsData;
				}
				catch
				{
					return false;
				}
			}).ToArray() : null);
		}
		while (Interlocked.CompareExchange(ref _ThreadStaticRefs, value, threadStaticRefs) != threadStaticRefs);
	}

	public static void KillWriteThread()
	{
		_ExitWriteThread = true;
		Thread.MemoryBarrier();
		_ThreadWriteEvent.Set();
	}

	public static void Error(string formatStr)
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			EnsureAndLockBuffer(formatStr, escapeLength, 0);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_ERROR, formatStr, escapeLength);
			WriteEndLogEntry();
			Errors++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Error<T0>(string formatStr, T0 data0) where T0 : struct
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			num += MaxNumericSize(data0);
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_ERROR, formatStr, escapeLength);
			WriteData(data0);
			WriteEndLogEntry();
			Errors++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Error<T0, T1>(string formatStr, T0 data0, T1 data1) where T0 : struct where T1 : struct
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			num += MaxNumericSize(data0);
			num += MaxNumericSize(data1);
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_ERROR, formatStr, escapeLength);
			WriteData(data0);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data1);
			WriteEndLogEntry();
			Errors++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Error<T0, T1, T2>(string formatStr, T0 data0, T1 data1, T2 data2) where T0 : struct where T1 : struct where T2 : struct
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			num += MaxNumericSize(data0);
			num += MaxNumericSize(data1);
			num += MaxNumericSize(data2);
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_ERROR, formatStr, escapeLength);
			WriteData(data0);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data1);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data2);
			WriteEndLogEntry();
			Errors++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Error<T0, T1, T2, T3>(string formatStr, T0 data0, T1 data1, T2 data2, T3 data3) where T0 : struct where T1 : struct where T2 : struct where T3 : struct
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			num += MaxNumericSize(data0);
			num += MaxNumericSize(data1);
			num += MaxNumericSize(data2);
			num += MaxNumericSize(data3);
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_ERROR, formatStr, escapeLength);
			WriteData(data0);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data1);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data2);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data3);
			WriteEndLogEntry();
			Errors++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Error<T0, T1, T2, T3, T4>(string formatStr, T0 data0, T1 data1, T2 data2, T3 data3, T4 data4) where T0 : struct where T1 : struct where T2 : struct where T3 : struct where T4 : struct
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			num += MaxNumericSize(data0);
			num += MaxNumericSize(data1);
			num += MaxNumericSize(data2);
			num += MaxNumericSize(data3);
			num += MaxNumericSize(data4);
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_ERROR, formatStr, escapeLength);
			WriteData(data0);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data1);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data2);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data3);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data4);
			WriteEndLogEntry();
			Errors++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Error<T0, T1, T2, T3, T4, T5>(string formatStr, T0 data0, T1 data1, T2 data2, T3 data3, T4 data4, T5 data5) where T0 : struct where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			num += MaxNumericSize(data0);
			num += MaxNumericSize(data1);
			num += MaxNumericSize(data2);
			num += MaxNumericSize(data3);
			num += MaxNumericSize(data4);
			num += MaxNumericSize(data5);
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_ERROR, formatStr, escapeLength);
			WriteData(data0);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data1);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data2);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data3);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data4);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data5);
			WriteEndLogEntry();
			Errors++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Error<T0, T1, T2, T3, T4, T5, T6>(string formatStr, T0 data0, T1 data1, T2 data2, T3 data3, T4 data4, T5 data5, T6 data6) where T0 : struct where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			num += MaxNumericSize(data0);
			num += MaxNumericSize(data1);
			num += MaxNumericSize(data2);
			num += MaxNumericSize(data3);
			num += MaxNumericSize(data4);
			num += MaxNumericSize(data5);
			num += MaxNumericSize(data6);
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_ERROR, formatStr, escapeLength);
			WriteData(data0);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data1);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data2);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data3);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data4);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data5);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data6);
			WriteEndLogEntry();
			Errors++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Log(string formatStr, params object[] objs)
	{
		if (objs.Length == 0)
		{
			Log(formatStr);
			return;
		}
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			for (int i = 0; i < objs.Length; i++)
			{
				num += MaxObjectSize(objs[i]);
			}
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_LOG, formatStr, escapeLength);
			for (int j = 0; j < objs.Length; j++)
			{
				WriteObject(objs[j]);
				if (j != objs.Length - 1)
				{
					_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
				}
			}
			WriteEndLogEntry();
			Logs++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Warn(string formatStr, params object[] objs)
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			for (int i = 0; i < objs.Length; i++)
			{
				num += MaxObjectSize(objs[i]);
			}
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_WARNING, formatStr, escapeLength);
			for (int j = 0; j < objs.Length; j++)
			{
				WriteObject(objs[j]);
				if (j != objs.Length - 1)
				{
					_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
				}
			}
			WriteEndLogEntry();
			Logs++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Error(string formatStr, params object[] objs)
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			for (int i = 0; i < objs.Length; i++)
			{
				num += MaxObjectSize(objs[i]);
			}
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_ERROR, formatStr, escapeLength);
			for (int j = 0; j < objs.Length; j++)
			{
				WriteObject(objs[j]);
				if (j != objs.Length - 1)
				{
					_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
				}
			}
			WriteEndLogEntry();
			Logs++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Log(string formatStr)
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			EnsureAndLockBuffer(formatStr, escapeLength, 0);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_LOG, formatStr, escapeLength);
			WriteEndLogEntry();
			Logs++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Log<T0>(string formatStr, T0 data0) where T0 : struct
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			num += MaxNumericSize(data0);
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_LOG, formatStr, escapeLength);
			WriteData(data0);
			WriteEndLogEntry();
			Logs++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Log<T0, T1>(string formatStr, T0 data0, T1 data1) where T0 : struct where T1 : struct
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			num += MaxNumericSize(data0);
			num += MaxNumericSize(data1);
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_LOG, formatStr, escapeLength);
			WriteData(data0);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data1);
			WriteEndLogEntry();
			Logs++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Log<T0, T1, T2>(string formatStr, T0 data0, T1 data1, T2 data2) where T0 : struct where T1 : struct where T2 : struct
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			num += MaxNumericSize(data0);
			num += MaxNumericSize(data1);
			num += MaxNumericSize(data2);
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_LOG, formatStr, escapeLength);
			WriteData(data0);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data1);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data2);
			WriteEndLogEntry();
			Logs++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Log<T0, T1, T2, T3>(string formatStr, T0 data0, T1 data1, T2 data2, T3 data3) where T0 : struct where T1 : struct where T2 : struct where T3 : struct
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			num += MaxNumericSize(data0);
			num += MaxNumericSize(data1);
			num += MaxNumericSize(data2);
			num += MaxNumericSize(data3);
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_LOG, formatStr, escapeLength);
			WriteData(data0);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data1);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data2);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data3);
			WriteEndLogEntry();
			Logs++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Log<T0, T1, T2, T3, T4>(string formatStr, T0 data0, T1 data1, T2 data2, T3 data3, T4 data4) where T0 : struct where T1 : struct where T2 : struct where T3 : struct where T4 : struct
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			num += MaxNumericSize(data0);
			num += MaxNumericSize(data1);
			num += MaxNumericSize(data2);
			num += MaxNumericSize(data3);
			num += MaxNumericSize(data4);
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_LOG, formatStr, escapeLength);
			WriteData(data0);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data1);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data2);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data3);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data4);
			WriteEndLogEntry();
			Logs++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Log<T0, T1, T2, T3, T4, T5>(string formatStr, T0 data0, T1 data1, T2 data2, T3 data3, T4 data4, T5 data5) where T0 : struct where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			num += MaxNumericSize(data0);
			num += MaxNumericSize(data1);
			num += MaxNumericSize(data2);
			num += MaxNumericSize(data3);
			num += MaxNumericSize(data4);
			num += MaxNumericSize(data5);
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_LOG, formatStr, escapeLength);
			WriteData(data0);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data1);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data2);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data3);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data4);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data5);
			WriteEndLogEntry();
			Logs++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Log<T0, T1, T2, T3, T4, T5, T6>(string formatStr, T0 data0, T1 data1, T2 data2, T3 data3, T4 data4, T5 data5, T6 data6) where T0 : struct where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			num += MaxNumericSize(data0);
			num += MaxNumericSize(data1);
			num += MaxNumericSize(data2);
			num += MaxNumericSize(data3);
			num += MaxNumericSize(data4);
			num += MaxNumericSize(data5);
			num += MaxNumericSize(data6);
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_LOG, formatStr, escapeLength);
			WriteData(data0);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data1);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data2);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data3);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data4);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data5);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data6);
			WriteEndLogEntry();
			Logs++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Warn(string formatStr)
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			EnsureAndLockBuffer(formatStr, escapeLength, 0);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_WARNING, formatStr, escapeLength);
			WriteEndLogEntry();
			Warnings++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Warn<T0>(string formatStr, T0 data0) where T0 : struct
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			num += MaxNumericSize(data0);
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_WARNING, formatStr, escapeLength);
			WriteData(data0);
			WriteEndLogEntry();
			Warnings++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Warn<T0, T1>(string formatStr, T0 data0, T1 data1) where T0 : struct where T1 : struct
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			num += MaxNumericSize(data0);
			num += MaxNumericSize(data1);
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_WARNING, formatStr, escapeLength);
			WriteData(data0);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data1);
			WriteEndLogEntry();
			Warnings++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Warn<T0, T1, T2>(string formatStr, T0 data0, T1 data1, T2 data2) where T0 : struct where T1 : struct where T2 : struct
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			num += MaxNumericSize(data0);
			num += MaxNumericSize(data1);
			num += MaxNumericSize(data2);
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_WARNING, formatStr, escapeLength);
			WriteData(data0);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data1);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data2);
			WriteEndLogEntry();
			Warnings++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Warn<T0, T1, T2, T3>(string formatStr, T0 data0, T1 data1, T2 data2, T3 data3) where T0 : struct where T1 : struct where T2 : struct where T3 : struct
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			num += MaxNumericSize(data0);
			num += MaxNumericSize(data1);
			num += MaxNumericSize(data2);
			num += MaxNumericSize(data3);
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_WARNING, formatStr, escapeLength);
			WriteData(data0);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data1);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data2);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data3);
			WriteEndLogEntry();
			Warnings++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Warn<T0, T1, T2, T3, T4>(string formatStr, T0 data0, T1 data1, T2 data2, T3 data3, T4 data4) where T0 : struct where T1 : struct where T2 : struct where T3 : struct where T4 : struct
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			num += MaxNumericSize(data0);
			num += MaxNumericSize(data1);
			num += MaxNumericSize(data2);
			num += MaxNumericSize(data3);
			num += MaxNumericSize(data4);
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_WARNING, formatStr, escapeLength);
			WriteData(data0);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data1);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data2);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data3);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data4);
			WriteEndLogEntry();
			Warnings++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Warn<T0, T1, T2, T3, T4, T5>(string formatStr, T0 data0, T1 data1, T2 data2, T3 data3, T4 data4, T5 data5) where T0 : struct where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			num += MaxNumericSize(data0);
			num += MaxNumericSize(data1);
			num += MaxNumericSize(data2);
			num += MaxNumericSize(data3);
			num += MaxNumericSize(data4);
			num += MaxNumericSize(data5);
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_WARNING, formatStr, escapeLength);
			WriteData(data0);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data1);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data2);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data3);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data4);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data5);
			WriteEndLogEntry();
			Warnings++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void Warn<T0, T1, T2, T3, T4, T5, T6>(string formatStr, T0 data0, T1 data1, T2 data2, T3 data3, T4 data4, T5 data5, T6 data6) where T0 : struct where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct
	{
		try
		{
			int escapeLength = GetEscapeLength(formatStr);
			int num = 0;
			num += MaxNumericSize(data0);
			num += MaxNumericSize(data1);
			num += MaxNumericSize(data2);
			num += MaxNumericSize(data3);
			num += MaxNumericSize(data4);
			num += MaxNumericSize(data5);
			num += MaxNumericSize(data6);
			EnsureAndLockBuffer(formatStr, escapeLength, num);
			WriteLogEntryHeader(LOGENTRY_TYPESTRING_WARNING, formatStr, escapeLength);
			WriteData(data0);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data1);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data2);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data3);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data4);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data5);
			_WriteBuffer.Buffer[_WriteBufferIndex++] = 9;
			WriteData(data6);
			WriteEndLogEntry();
			Warnings++;
		}
		finally
		{
			Interlocked.Exchange(ref _ThreadStaticData._WriteBufferLock, 0);
		}
		FlushThreadBuffer();
	}

	public static void InitializeFileLog(string identifier, string path = "Logs")
	{
		try
		{
			if (!Directory.Exists(path))
			{
				Directory.CreateDirectory(path);
			}
			string[] files = Directory.GetFiles(path, "Current - " + identifier + "*", SearchOption.AllDirectories);
			foreach (string text in files)
			{
				string directoryName = Path.GetDirectoryName(text);
				string fileName = Path.GetFileName(text);
				fileName = fileName.Replace("Current - ", "");
				string destFileName = Path.Combine(directoryName, fileName);
				try
				{
					File.Move(text, destFileName);
				}
				catch (Exception ex)
				{
					Error(ex.ToString());
				}
			}
			AddOutputStream(new StreamWriter(Path.Combine(path, "Current - " + identifier + " " + DateTime.UtcNow.ToString("MM-dd-yyyy HHmmss") + ".txt")).BaseStream);
		}
		catch (Exception ex2)
		{
			Error(ex2.ToString());
		}
	}

	public static void InitializeUnhandledExceptionLog()
	{
		AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
	}

	private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		Error("Fatal Unhandled exception! Exception: " + e.ToString() + ", " + e.ExceptionObject);
		FlushThreadBuffer();
		WaitForBufferFlush(1000);
	}

	public static void InitializeConsoleLog()
	{
		AddOutputStream(new ConsoleDebugStream());
	}
}
