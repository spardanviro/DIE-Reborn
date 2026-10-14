using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace DIE.Server.Match;

/// <summary>
/// 战斗服泵线程的分段计时。
///
/// ## 为什么要有它
///
/// 云上报过"三个人也会 CPU 突然 100%、游戏卡一下"。泵线程一轮里要做的事很多
/// (收包、刷怪、十几个 AI 脑子、原厂模拟、给每个人组帧), 光看任务管理器分不出
/// 是哪一段。这里把每一段的耗时记下来, 定期汇总一行, 慢帧当场报出来是哪几段慢。
///
/// ## 开销
///
/// 一段两次 <see cref="Stopwatch.GetTimestamp"/> (几十纳秒), 没有分配。
/// 只在泵线程上用, 不加锁。
/// </summary>
public sealed class FrameProfiler
{
    private readonly List<string> _names = new List<string>();
    private readonly List<long> _total = new List<long>();
    private readonly List<long> _worst = new List<long>();
    private readonly List<long> _inFrame = new List<long>();

    private long _frameStart;
    private long _frameTotal;
    private long _frameWorst;
    private int _frames;
    private int _slowFrames;

    /// <summary>超过它的一帧算慢帧 (逻辑帧 30Hz = 33ms 预算)。</summary>
    public double SlowFrameMs { get; set; } = 33.0;

    /// <summary>慢帧当场的分段明细 (最近一次)。没有慢帧时为 null。</summary>
    public string LastSlowFrame { get; private set; }

    /// <summary>这一窗口里超过 <see cref="SlowFrameMs"/> 的帧数。</summary>
    public int SlowFrames => _slowFrames;

    public int Frames => _frames;

    /// <summary>上一个汇总窗口里泵线程忙的比例 (%) —— 控制台的"负载"。还没汇总过为 0。</summary>
    public double LastBusyPercent { get; private set; }

    /// <summary>上一个汇总窗口的平均每帧毫秒。</summary>
    public double LastAverageMs { get; private set; }

    /// <summary>上一个汇总窗口里最慢一帧的毫秒。</summary>
    public double LastWorstMs { get; private set; }

    /// <summary>登记一个分段, 返回它的编号。同名只登记一次。</summary>
    public int Register(string name)
    {
        int index = _names.IndexOf(name);
        if (index >= 0)
        {
            return index;
        }

        _names.Add(name);
        _total.Add(0);
        _worst.Add(0);
        _inFrame.Add(0);
        return _names.Count - 1;
    }

    /// <summary>计时一段: <c>using (profiler.Measure(id)) { ... }</c>。</summary>
    public Scope Measure(int section) => new Scope(this, section, Stopwatch.GetTimestamp());

    /// <summary>同上, 但没挂计时器 (<paramref name="profiler"/> 为 null) 时什么都不做。</summary>
    public static Scope Measure(FrameProfiler profiler, int section) =>
        profiler == null ? default : new Scope(profiler, section, Stopwatch.GetTimestamp());

    /// <summary>
    /// 分段打点: 把 <paramref name="since"/> 到现在记到 <paramref name="section"/> 上,
    /// 再把 <paramref name="since"/> 拨到现在 —— 一长串顺序调用里逐段插一行即可,
    /// 不用改成嵌套的 using。
    /// </summary>
    public void Lap(int section, ref long since)
    {
        long now = Stopwatch.GetTimestamp();
        Add(section, now - since);
        since = now;
    }

    public void Add(int section, long ticks)
    {
        _total[section] += ticks;
        _inFrame[section] += ticks;
        if (ticks > _worst[section])
        {
            _worst[section] = ticks;
        }
    }

    public void BeginFrame()
    {
        _frameStart = Stopwatch.GetTimestamp();
        for (int i = 0; i < _inFrame.Count; i++)
        {
            _inFrame[i] = 0;
        }
    }

    public void EndFrame()
    {
        long ticks = Stopwatch.GetTimestamp() - _frameStart;
        _frames++;
        _frameTotal += ticks;
        if (ticks > _frameWorst)
        {
            _frameWorst = ticks;
        }

        if (Ms(ticks) >= SlowFrameMs)
        {
            _slowFrames++;
            LastSlowFrame = $"{Ms(ticks):F1}ms = " + Describe(_inFrame, 4);
        }
    }

    /// <summary>
    /// 汇总这一窗口并清零。格式: 平均每帧耗时 / 最慢一帧 / 各段按总耗时排序。
    /// </summary>
    public string Flush(double windowSeconds)
    {
        if (_frames == 0)
        {
            return null;
        }

        StringBuilder text = new StringBuilder();
        double busy = Ms(_frameTotal) / (windowSeconds * 1000.0) * 100.0;
        LastBusyPercent = busy;
        LastAverageMs = Ms(_frameTotal) / _frames;
        LastWorstMs = Ms(_frameWorst);
        text.Append($"{_frames} 帧 平均 {Ms(_frameTotal) / _frames:F2}ms 最慢 {Ms(_frameWorst):F1}ms "
                    + $"泵线程忙 {busy:F0}% 慢帧 {_slowFrames}");
        text.Append(" | ");
        text.Append(Describe(_total, 8, _frames, _worst));

        for (int i = 0; i < _total.Count; i++)
        {
            _total[i] = 0;
            _worst[i] = 0;
        }

        _frames = 0;
        _frameTotal = 0;
        _frameWorst = 0;
        _slowFrames = 0;
        return text.ToString();
    }

    /// <summary>当前窗口里某一段的累计毫秒 (压测工具读)。</summary>
    public IReadOnlyDictionary<string, double> Totals() =>
        _names.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => Ms(_total[x.i]));

    private string Describe(List<long> ticks, int top, int perFrames = 0, List<long> worst = null)
    {
        IEnumerable<int> order = Enumerable.Range(0, ticks.Count)
            .Where(i => ticks[i] > 0)
            .OrderByDescending(i => ticks[i])
            .Take(top);

        return string.Join(" ", order.Select(i =>
        {
            string head = $"{_names[i]}={(perFrames > 0 ? Ms(ticks[i]) / perFrames : Ms(ticks[i])):F2}";
            return worst == null ? head : $"{head}(峰{Ms(worst[i]):F1})";
        }));
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public readonly ref struct Scope
    {
        private readonly FrameProfiler _owner;
        private readonly int _section;
        private readonly long _start;

        public Scope(FrameProfiler owner, int section, long start)
        {
            _owner = owner;
            _section = section;
            _start = start;
        }

        public void Dispose() => _owner?.Add(_section, Stopwatch.GetTimestamp() - _start);
    }
}
