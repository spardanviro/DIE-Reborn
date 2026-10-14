using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace DIE.ServerConsole
{
    /// <summary>碰本机环境的几件小事: 设置文件放哪、自己在哪、打开文件夹 / 网页。</summary>
    internal static class Platform
    {
        public static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DIE Reborn",
            "server-console.json");

        public static string ExeDir => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        public static void OpenFolder(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Win32Exception)
            {
            }
        }

        public static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Win32Exception)
            {
            }
        }
    }
}
