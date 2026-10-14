using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using DIE.ServerConsole.Core;

namespace DIE.ServerConsole
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 语言: 上次选的; 没选过就按系统语言
            Lang.Use(ConsoleSettings.Load(Platform.SettingsPath).Language,
                CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

            DispatcherUnhandledException += OnUnhandled;
            base.OnStartup(e);
        }

        /// <summary>界面线程上没接住的异常: 说清楚出了什么事, 不让窗口直接消失 (服务端不受影响)。</summary>
        private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(Lang.T("dialog.crash", e.Exception.Message), Lang.T("window.title"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
