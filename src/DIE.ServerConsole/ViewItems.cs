using System.Windows;
using System.Windows.Media;

namespace DIE.ServerConsole
{
    /// <summary>界面上一行行的东西 (表格行、检查项、日志行)。都是只读的快照, 刷新时整批换掉。</summary>
    internal static class Palette
    {
        public static Brush Get(string key) => (Brush)Application.Current.Resources[key];

        public static Brush Alive => Get("Alive");
        public static Brush Dead => Get("Dead");
        public static Brush Hazard => Get("Hazard");
        public static Brush Warn => Get("Warn");
        public static Brush Faint => Get("TextFaint");
        public static Brush Soft => Get("TextSoft");
        public static Brush Strong => Get("TextStrong");
    }

    internal sealed class CheckItem
    {
        public CheckItem(Brush brush, string text)
        {
            Brush = brush;
            Text = text;
        }

        public Brush Brush { get; }
        public string Text { get; }
    }

    internal sealed class MatchItem
    {
        public string Port { get; set; }
        public string Mode { get; set; }
        public string Map { get; set; }
        public string Players { get; set; }
        public string State { get; set; }
        public Brush StateBrush { get; set; }
        public string Age { get; set; }
        public string Load { get; set; }
        public Brush LoadBrush { get; set; }
        public string Frame { get; set; }
    }

    internal sealed class PlayerItem
    {
        public string Name { get; set; }
        public string Where { get; set; }
        public Brush WhereBrush { get; set; }
        public string Online { get; set; }
        public string SteamId { get; set; }
        public string Address { get; set; }
    }

    internal sealed class LogItem
    {
        public LogItem(Core.LogLine line)
        {
            Line = line;
            Brush = line.IsError ? Palette.Dead
                : line.IsWarning ? Palette.Warn
                : line.IsDebug ? Palette.Faint
                : Palette.Strong;
        }

        public Core.LogLine Line { get; }
        public string Time => Line.Time;
        public string Level => Line.Level;
        public string Text => Line.Text;
        public Brush Brush { get; }
    }
}
