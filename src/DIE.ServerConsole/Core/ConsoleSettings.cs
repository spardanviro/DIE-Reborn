using System;
using System.IO;
using System.Runtime.Serialization;
using System.Text;

namespace DIE.ServerConsole.Core
{
    /// <summary>
    /// 控制台自己的设置 (不是服务端的): 存在 <c>%LocalAppData%\DIE Reborn\server-console.json</c>。
    ///
    /// 服务器名字、位置、公网地址这些属于服务端, 写在服务端旁边的文本文件里 (见 <see cref="ServerFiles"/>),
    /// 不放这里 —— 不用控制台、直接改文件的服主也照样生效。
    /// </summary>
    [DataContract]
    public sealed class ConsoleSettings
    {
        /// <summary>服务端所在的文件夹 (DIE.Server.exe 在里面)。空 = 控制台自己旁边。</summary>
        [DataMember(Name = "serverDir")] public string ServerDir { get; set; }

        [DataMember(Name = "lang")] public string Language { get; set; }

        /// <summary>打开控制台时自动开服。</summary>
        [DataMember(Name = "autoStart")] public bool AutoStart { get; set; }

        /// <summary>服务端意外退出 (崩溃) 时自动重开。</summary>
        [DataMember(Name = "autoRestart")] public bool? AutoRestartSetting { get; set; }

        /// <summary>每天自动备份账号库。</summary>
        [DataMember(Name = "dailyBackup")] public bool? DailyBackupSetting { get; set; }

        /// <summary>每天几点备份 (本地时间, 0-23)。</summary>
        [DataMember(Name = "backupHour")] public int? BackupHourSetting { get; set; }

        /// <summary>上次自动备份是哪一天 (yyyy-MM-dd), 一天只备一次。</summary>
        [DataMember(Name = "lastBackup")] public string LastBackupDay { get; set; }

        /// <summary>登记处 (外网探测问它)。空 = 默认的 DIE Reborn 服务器。</summary>
        [DataMember(Name = "registry")] public string Registry { get; set; }

        // DataContractJsonSerializer 不跑构造函数也不跑字段初始值, 所以"默认开"的项存成可空, 在这里给默认。

        public bool AutoRestart
        {
            get => AutoRestartSetting ?? true;
            set => AutoRestartSetting = value;
        }

        public bool DailyBackup
        {
            get => DailyBackupSetting ?? true;
            set => DailyBackupSetting = value;
        }

        public int BackupHour
        {
            get => Math.Max(0, Math.Min(23, BackupHourSetting ?? 4));
            set => BackupHourSetting = Math.Max(0, Math.Min(23, value));
        }

        public const string DefaultRegistry = "47.99.74.220";
        public const int RegistryPort = 8555;

        public string RegistryHost => string.IsNullOrWhiteSpace(Registry) ? DefaultRegistry : Registry.Trim();

        /// <summary>该不该做今天的自动备份: 开着、到点了、今天还没备过。</summary>
        public bool BackupDue(DateTime localNow) =>
            DailyBackup && localNow.Hour >= BackupHour && LastBackupDay != localNow.ToString("yyyy-MM-dd");

        public static ConsoleSettings Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    return Json.Read<ConsoleSettings>(File.ReadAllText(path, Encoding.UTF8)) ?? new ConsoleSettings();
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return new ConsoleSettings();
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, Json.Write(this), new UTF8Encoding(false));
        }
    }
}
