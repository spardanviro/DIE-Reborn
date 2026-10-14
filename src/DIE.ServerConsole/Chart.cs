using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace DIE.ServerConsole
{
    /// <summary>
    /// 概览页的小折线图: 一条线 + 底下一片淡色, 纵轴从 0 到 (最大值和 <see cref="Floor"/> 里大的那个)。
    /// 只画最近的 <see cref="Capacity"/> 个点, 横轴等距 (每个点是一次刷新, 间隔固定)。
    /// </summary>
    public sealed class Chart : FrameworkElement
    {
        private readonly List<double> _points = new List<double>();

        /// <summary>最多存这么多点 (2 秒一个 = 30 分钟)。</summary>
        public int Capacity { get; set; } = 900;

        /// <summary>纵轴最小顶到多少 (人数图给 4, 一两个人时线不会顶到天花板)。</summary>
        public double Floor { get; set; } = 4;

        public Brush Line { get; set; } = new SolidColorBrush(Color.FromRgb(0xF5, 0xB8, 0x1F));

        public double Peak => _points.Count == 0 ? 0 : _points.Max();

        public void Add(double value)
        {
            _points.Add(Math.Max(0, double.IsNaN(value) ? 0 : value));
            if (_points.Count > Capacity)
            {
                _points.RemoveRange(0, _points.Count - Capacity);
            }

            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            Brush grid = new SolidColorBrush(Color.FromRgb(0x27, 0x30, 0x2B));
            Pen gridPen = new Pen(grid, 1);
            for (int i = 0; i <= 2; i++)
            {
                double y = Math.Round(height * i / 2.0) + 0.5;
                dc.DrawLine(gridPen, new Point(0, y), new Point(width, y));
            }

            double top = Math.Max(Floor, Peak) * 1.1;
            Typeface face = new Typeface("Segoe UI");
            dc.DrawText(new FormattedText(top.ToString("0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, face, 10, new SolidColorBrush(Color.FromRgb(0x5F, 0x6B, 0x64)), 1.0),
                new Point(2, 1));

            if (_points.Count < 2)
            {
                return;
            }

            double step = width / (Capacity - 1);
            double startX = width - step * (_points.Count - 1);

            StreamGeometry line = new StreamGeometry();
            StreamGeometry area = new StreamGeometry();

            using (StreamGeometryContext lineCtx = line.Open())
            using (StreamGeometryContext areaCtx = area.Open())
            {
                Point first = new Point(startX, Y(_points[0], top, height));
                lineCtx.BeginFigure(first, false, false);
                areaCtx.BeginFigure(new Point(startX, height), true, true);
                areaCtx.LineTo(first, false, false);

                for (int i = 1; i < _points.Count; i++)
                {
                    Point p = new Point(startX + step * i, Y(_points[i], top, height));
                    lineCtx.LineTo(p, true, false);
                    areaCtx.LineTo(p, false, false);
                }

                areaCtx.LineTo(new Point(width, height), false, false);
            }

            line.Freeze();
            area.Freeze();

            Brush fill = Line.Clone();
            fill.Opacity = 0.12;
            dc.DrawGeometry(fill, null, area);
            dc.DrawGeometry(null, new Pen(Line, 1.6) { LineJoin = PenLineJoin.Round }, line);
        }

        private static double Y(double value, double top, double height) =>
            height - (top <= 0 ? 0 : value / top * (height - 2)) - 1;
    }
}
