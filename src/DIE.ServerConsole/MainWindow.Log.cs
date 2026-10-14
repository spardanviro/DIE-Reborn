using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using DIE.ServerConsole.Core;

namespace DIE.ServerConsole
{
    /// <summary>日志页: 跟着读服务端的日志文件, 按级别 / 关键字筛。</summary>
    public partial class MainWindow
    {
        /// <summary>界面里最多留这么多行 (再多列表就卡了; 完整的在文件里)。</summary>
        private const int MaxLogLines = 5000;

        private readonly ObservableCollection<LogItem> _log = new ObservableCollection<LogItem>();
        private ICollectionView _logView;
        private LogTail _tail;

        private void InitLog()
        {
            _logView = CollectionViewSource.GetDefaultView(_log);
            _logView.Filter = item => Visible((LogItem)item);
            LogList.ItemsSource = _logView;
        }

        private void ResetLog()
        {
            _log.Clear();
        }

        private void PollLog()
        {
            List<LogLine> lines = _tail.Poll();
            LogFileText.Text = _tail.CurrentFile ?? Lang.T("log.noFile", Path.Combine(_server.Directory, "logs"));

            if (lines.Count == 0)
            {
                return;
            }

            foreach (LogLine line in lines)
            {
                _log.Add(new LogItem(line));
            }

            while (_log.Count > MaxLogLines)
            {
                _log.RemoveAt(0);
            }

            if (LogFollow.IsChecked == true && PageLog.Visibility == Visibility.Visible)
            {
                ScrollLogToEnd();
            }
        }

        private bool Visible(LogItem item)
        {
            if (LogProblems.IsChecked == true && !(item.Line.IsWarning || item.Line.IsError))
            {
                return false;
            }

            if (LogInfo.IsChecked == true && item.Line.IsDebug)
            {
                return false;
            }

            string search = LogSearch.Text;
            return string.IsNullOrEmpty(search)
                   || item.Text.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void LogFilter_Changed(object sender, RoutedEventArgs e) => RefreshLogFilter();

        private void RefreshLogFilter()
        {
            if (_logView == null)
            {
                return;
            }

            _logView.Refresh();
            ScrollLogToEnd();
        }

        private void ScrollLogToEnd()
        {
            if (LogList.Items.Count > 0)
            {
                LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
            }
        }

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            string dir = Path.Combine(_server.Directory, "logs");
            Platform.OpenFolder(Directory.Exists(dir) ? dir : _server.Directory);
        }
    }
}
