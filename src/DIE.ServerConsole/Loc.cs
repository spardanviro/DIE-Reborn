using System.Windows;
using System.Windows.Controls;
using DIE.ServerConsole.Core;

namespace DIE.ServerConsole
{
    /// <summary>
    /// 界面上固定的字: 在 XAML 里写 <c>l:Loc.Key="nav.overview"</c>, 从 <see cref="Lang"/> 取当前语言的那份。
    /// 换语言时 <see cref="Refresh"/> 把整棵树重填一遍。
    ///
    /// 认得的几种控件: TextBlock (Text)、按钮 / 单选 / 复选 (Content)、表格列 (Header)。
    /// </summary>
    public static class Loc
    {
        public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
            "Key", typeof(string), typeof(Loc), new PropertyMetadata(null, (target, _) => Apply(target)));

        public static string GetKey(DependencyObject target) => (string)target.GetValue(KeyProperty);

        public static void SetKey(DependencyObject target, string value) => target.SetValue(KeyProperty, value);

        private static void Apply(DependencyObject target)
        {
            string key = GetKey(target);
            if (key == null)
            {
                return;
            }

            string text = Lang.T(key);
            switch (target)
            {
                case TextBlock block:
                    block.Text = text;
                    break;
                case ContentControl control:
                    control.Content = text;
                    break;
                case DataGridColumn column:
                    column.Header = text;
                    break;
            }
        }

        /// <summary>把 <paramref name="root"/> 下所有带键的元素按当前语言重填。</summary>
        public static void Refresh(DependencyObject root)
        {
            Apply(root);

            if (root is DataGrid grid)
            {
                foreach (DataGridColumn column in grid.Columns)
                {
                    Apply(column);
                }
            }

            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is DependencyObject node)
                {
                    Refresh(node);
                }
            }
        }
    }
}
