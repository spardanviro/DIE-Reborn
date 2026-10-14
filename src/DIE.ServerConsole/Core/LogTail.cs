using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DIE.ServerConsole.Core
{
    /// <summary>服务端日志里的一行 (异常堆栈那种续行并进上一行的级别)。</summary>
    public sealed class LogLine
    {
        public LogLine(string time, string level, string text)
        {
            Time = time;
            Level = level;
            Text = text;
        }

        /// <summary>HH:mm:ss (续行为空)。</summary>
        public string Time { get; }

        /// <summary>DBG / INF / WRN / ERR / FTL。</summary>
        public string Level { get; }

        public string Text { get; }

        public bool IsWarning => Level == "WRN";

        public bool IsError => Level == "ERR" || Level == "FTL";

        public bool IsDebug => Level == "DBG" || Level == "VRB";
    }

    /// <summary>
    /// 跟着读服务端的日志文件 (<c>logs\die-server-yyyyMMdd.log</c>, 每天一个)。
    ///
    /// 不读服务端进程的标准输出, 而是读文件: 服务端不是控制台起的 (比如用「启动服务器.cmd」开的)
    /// 也能看; 控制台关了再开也接得上。Serilog 写文件时允许别人读, 这里以共享读写方式打开。
    ///
    /// 第一次只读最后 <see cref="InitialBytes"/> 字节; 之后每次 <see cref="Poll"/> 读新增的部分。
    /// 过了零点换成新的一天的文件, 自动跟过去。
    /// </summary>
    public sealed class LogTail
    {
        public const int InitialBytes = 256 * 1024;

        private static readonly Regex Head = new Regex(
            @"^\d{4}-\d{2}-\d{2} (\d{2}:\d{2}:\d{2})(?:\.\d+)? \[([A-Z]{3})\] (.*)$");

        private readonly string _directory;
        private string _file;
        private long _position;
        private string _partial = string.Empty;
        private string _lastLevel = "INF";
        private bool _skipToLineStart;

        public LogTail(string logDirectory)
        {
            _directory = logDirectory;
        }

        /// <summary>正在读的文件 (还没找到为 null)。</summary>
        public string CurrentFile => _file;

        /// <summary>读新增的行。没有日志文件 / 没有新内容返回空列表。</summary>
        public List<LogLine> Poll()
        {
            List<LogLine> lines = new List<LogLine>();

            string newest = NewestFile();
            if (newest == null)
            {
                return lines;
            }

            if (!string.Equals(newest, _file, StringComparison.OrdinalIgnoreCase))
            {
                bool first = _file == null;
                _file = newest;
                _partial = string.Empty;
                _position = 0;

                if (first)
                {
                    long length = new FileInfo(newest).Length;
                    _position = Math.Max(0, length - InitialBytes);
                    _skipToLineStart = _position > 0;     // 从中间开始读的话, 第一行是半截的
                }
            }

            string chunk = ReadFrom(_file);
            if (chunk.Length == 0)
            {
                return lines;
            }

            string text = _partial + chunk;
            if (_skipToLineStart)
            {
                int firstBreak = text.IndexOf('\n');
                if (firstBreak < 0)
                {
                    return lines;
                }

                text = text.Substring(firstBreak + 1);
                _skipToLineStart = false;
            }
            int lastBreak = text.LastIndexOf('\n');
            if (lastBreak < 0)
            {
                _partial = text;
                return lines;
            }

            _partial = text.Substring(lastBreak + 1);

            foreach (string raw in text.Substring(0, lastBreak).Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0)
                {
                    continue;
                }

                lines.Add(Parse(line));
            }

            return lines;
        }

        /// <summary>一行日志 -&gt; 时间 / 级别 / 正文; 不是行首格式的 (堆栈续行) 跟上一行的级别。</summary>
        public LogLine Parse(string line)
        {
            Match head = Head.Match(line);
            if (!head.Success)
            {
                return new LogLine(string.Empty, _lastLevel, line);
            }

            _lastLevel = head.Groups[2].Value;
            return new LogLine(head.Groups[1].Value, _lastLevel, head.Groups[3].Value);
        }

        private string ReadFrom(string file)
        {
            try
            {
                using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length < _position)
                    {
                        _position = 0;      // 文件被截短 / 换掉了: 从头读
                        _partial = string.Empty;
                    }

                    stream.Seek(_position, SeekOrigin.Begin);
                    byte[] buffer = new byte[stream.Length - _position];
                    int read = 0;
                    while (read < buffer.Length)
                    {
                        int got = stream.Read(buffer, read, buffer.Length - read);
                        if (got <= 0)
                        {
                            break;
                        }

                        read += got;
                    }

                    // 只交出到最后一个完整 UTF-8 字符为止 (汉字 3 字节, 可能正好被截在中间)
                    int usable = CompleteUtf8Length(buffer, read);
                    _position += usable;
                    return Encoding.UTF8.GetString(buffer, 0, usable);
                }
            }
            catch (IOException)
            {
                return string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }

        /// <summary>前 <paramref name="count"/> 个字节里, 到最后一个完整 UTF-8 字符为止有多长。</summary>
        public static int CompleteUtf8Length(byte[] bytes, int count)
        {
            int i = count - 1;
            int back = 0;

            // 往回找最后一个字符的首字节 (最多 3 个续字节)
            while (i >= 0 && back < 4 && (bytes[i] & 0xC0) == 0x80)
            {
                i--;
                back++;
            }

            if (i < 0)
            {
                return count;
            }

            byte lead = bytes[i];
            int need = lead >= 0xF0 ? 4 : lead >= 0xE0 ? 3 : lead >= 0xC0 ? 2 : 1;
            return count - i >= need ? count : i;
        }

        private string NewestFile()
        {
            try
            {
                if (!Directory.Exists(_directory))
                {
                    return null;
                }

                return Directory.GetFiles(_directory, "die-server-*.log")
                    .OrderByDescending(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }
            catch (IOException)
            {
                return _file;
            }
            catch (UnauthorizedAccessException)
            {
                return _file;
            }
        }
    }
}
