using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace DIE.ServerConsole
{
    /// <summary>碰本机环境的几件小事: 设置文件放哪、自己在哪、打开文件夹 / 网页。</summary>
    internal static class Platform
    {
        public static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DIE Reborn",
            "server-console.json");

        public static string ExeDir => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        /// <summary>可能的 Steam 安装目录 (注册表里登记的, 加上默认位置), 和登陆器的找法一样。</summary>
        public static List<string> SteamDirs()
        {
            List<string> found = new List<string>();

            AddSteam(found, Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
            AddSteam(found, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            AddSteam(found, Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
            found.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));

            return found.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
        }

        private static void AddSteam(List<string> found, RegistryKey hive, string path, string value)
        {
            try
            {
                using (RegistryKey key = hive.OpenSubKey(path))
                {
                    if (key?.GetValue(value) is string text && text.Length > 0)
                    {
                        found.Add(text.Replace('/', '\\'));
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException
                                           || ex is UnauthorizedAccessException || ex is IOException)
            {
            }
        }

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
