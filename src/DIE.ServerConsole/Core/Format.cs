using System;
using System.Globalization;

namespace DIE.ServerConsole.Core
{
    /// <summary>把服务端报上来的代号翻成给人看的字 (跟着当前语言)。</summary>
    public static class Format
    {
        /// <summary>时长: "45 秒" / "12 分" / "3 小时 05 分" / "2 天 4 小时"。</summary>
        public static string Duration(long seconds)
        {
            if (seconds < 0)
            {
                seconds = 0;
            }

            long days = seconds / 86400;
            long hours = seconds % 86400 / 3600;
            long minutes = seconds % 3600 / 60;

            if (days > 0)
            {
                return Lang.T("time.dh", days, hours);
            }

            if (hours > 0)
            {
                return Lang.T("time.hm", hours, minutes.ToString("00", CultureInfo.InvariantCulture));
            }

            return minutes > 0 ? Lang.T("time.m", minutes) : Lang.T("time.s", seconds);
        }

        /// <summary>队列 (服务端的 QueueType 名) -&gt; 模式名。认不出的原样显示。</summary>
        public static string Mode(string queue)
        {
            switch (queue)
            {
                case "Tutorial": return Lang.T("mode.tutorial");
                case "ResistanceEasy": return Lang.T("mode.practice");
                case "RaidMission": return Lang.T("mode.crossroads");
                case "Scavenger": return Lang.T("mode.scavenger");
                default: return queue ?? string.Empty;
            }
        }

        /// <summary>十字路口的难度档 (0 简单 … 4 极难); 别的模式不显示。</summary>
        public static string Difficulty(string queue, int difficulty)
        {
            if (queue != "RaidMission")
            {
                return string.Empty;
            }

            string key = "difficulty." + Math.Max(0, Math.Min(4, difficulty)).ToString(CultureInfo.InvariantCulture);
            return Lang.T(key);
        }

        public static string State(string state)
        {
            switch (state)
            {
                case "loading":
                case "waiting":
                case "running":
                case "over":
                case "error":
                    return Lang.T("state." + state);
                default:
                    return state ?? string.Empty;
            }
        }

        /// <summary>玩家在哪: 大厅 / 排队 / 对局 (端口)。</summary>
        public static string Where(string where, int port)
        {
            switch (where)
            {
                case "match": return Lang.T("where.match", port);
                case "queue": return Lang.T("where.queue");
                default: return Lang.T("where.lobby");
            }
        }

        public static string Percent(double value) =>
            double.IsNaN(value) ? "–" : value.ToString("0", CultureInfo.InvariantCulture) + "%";
    }
}
