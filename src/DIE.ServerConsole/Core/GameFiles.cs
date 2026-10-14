using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DIE.ServerConsole.Core
{
    /// <summary>
    /// 开服要的、但不能放进公开发布包的那几样东西, 以及从哪里补齐它们。
    ///
    /// ## 为什么发布包里没有
    ///
    /// GitHub Releases 上的服务器包是公开的, 不能带游戏的文件, 也不能带 Valve 的 Steam 客户端库
    /// (玩家 2026-10-14 定)。缺的这几样由控制台在开服的机器上补:
    ///
    /// | 缺的                         | 从哪来                                                         |
    /// |------------------------------|----------------------------------------------------------------|
    /// | gamedata\ (地图、缓存数据)    | 本机 Steam 里装的游戏; 或者从别的电脑拷来的游戏文件夹           |
    /// | data\GameCraftingData.xml    | 同上, 从游戏的 resources.assets 里提取 (物品图鉴)              |
    /// | steamclient64.dll 等三个      | 本机的 Steam 安装目录; 没装 Steam 的 (云服务器) 用 Valve 官方的 SteamCMD |
    ///
    /// 这里只有判断和文件操作, 下载 SteamCMD 在界面层 (<c>SteamCmd</c>)。
    /// </summary>
    public static class GameFiles
    {
        public const string SteamAppId = "222900";
        public const string GameExe = "Dead Island Epidemic.exe";
        public const string GameDataFolder = "gamedata";
        public const string CraftingFile = "GameCraftingData.xml";

        /// <summary>服务端读的游戏数据 (见服务端 GameDataLoader)。</summary>
        public static readonly string[] DataFiles = { "cachedata.sexp", "cacheattributs.sexp" };

        public const string MapsFolder = "Maps";

        /// <summary>没装 Steam 的机器上, steam_api64 要从程序旁边加载这三个。</summary>
        public static readonly string[] SteamClientFiles = { "steamclient64.dll", "tier0_s64.dll", "vstdlib_s64.dll" };

        /// <summary>物品图鉴藏在这个 Unity 资源文件里 (相对游戏目录)。</summary>
        public static readonly string ResourcesAssets =
            Path.Combine("Dead Island Epidemic - Crib_Data", "resources.assets");

        private const string DefaultInstallDir = "Dead Island Epidemic";

        public sealed class Status
        {
            public bool GameData { get; set; }
            public bool Crafting { get; set; }
            public bool SteamClient { get; set; }

            public bool Ready => GameData && Crafting && SteamClient;
        }

        /// <remarks>
        /// Steam 客户端库一律要求放在服务端旁边: 装着 Steam 的机器上 steam_api64 也能去 Steam 目录找,
        /// 但那要求 Steam 正开着 —— 服务器上不该依赖这个。本机有 Steam 时从它的目录拷一份就是。
        /// </remarks>
        public static Status Check(string serverDir)
        {
            return new Status
            {
                GameData = HasGameData(Path.Combine(serverDir, GameDataFolder)),
                Crafting = File.Exists(Path.Combine(serverDir, "data", CraftingFile)),
                SteamClient = HasAll(serverDir, SteamClientFiles),
            };
        }

        /// <summary>这个文件夹里有没有服务端要的游戏数据 (cachedata.sexp 等 + Maps\)。</summary>
        public static bool HasGameData(string directory) =>
            HasAll(directory, DataFiles) && SafeDirectoryExists(Path.Combine(directory ?? string.Empty, MapsFolder));

        /// <summary>能从这个文件夹导入: 有游戏数据, 也有藏着物品图鉴的 resources.assets。</summary>
        public static bool CanImportFrom(string gameDir) =>
            HasGameData(gameDir) && SafeFileExists(Path.Combine(gameDir ?? string.Empty, ResourcesAssets));

        /// <summary>
        /// 从游戏目录把数据拷进 <c>gamedata\</c>, 再提取物品图鉴到 <c>data\</c>。重复做没关系 (覆盖)。
        /// </summary>
        public static void ImportFromGame(string gameDir, string serverDir)
        {
            if (!CanImportFrom(gameDir))
            {
                throw new InvalidOperationException(Lang.T("files.notGameDir", gameDir));
            }

            string target = Path.Combine(serverDir, GameDataFolder);
            Directory.CreateDirectory(target);

            foreach (string file in DataFiles)
            {
                File.Copy(Path.Combine(gameDir, file), Path.Combine(target, file), overwrite: true);
            }

            CopyDirectory(Path.Combine(gameDir, MapsFolder), Path.Combine(target, MapsFolder));

            byte[] xml = ExtractCrafting(File.ReadAllBytes(Path.Combine(gameDir, ResourcesAssets)));
            string data = Path.Combine(serverDir, "data");
            Directory.CreateDirectory(data);
            File.WriteAllBytes(Path.Combine(data, CraftingFile), xml);
        }

        /// <summary>从 Steam 安装目录 (或 SteamCMD 目录) 把三个客户端库拷到服务端旁边。缺任何一个返回 false。</summary>
        public static bool CopySteamClient(string fromDir, string serverDir)
        {
            if (!HasAll(fromDir, SteamClientFiles))
            {
                return false;
            }

            foreach (string file in SteamClientFiles)
            {
                File.Copy(Path.Combine(fromDir, file), Path.Combine(serverDir, file), overwrite: true);
            }

            return true;
        }

        // ==================================================================
        // 物品图鉴: 从 resources.assets 切出 GameCraftingData 这个 TextAsset
        // (和 tools\extract_crafting_data.py 同一个做法)
        // ==================================================================

        private static readonly byte[] CraftingAssetName = Encoding.ASCII.GetBytes("GameCraftingData");

        /// <summary>
        /// Unity 4.6 的 TextAsset 布局: int32 名字长度 | 名字 | 补齐到 4 字节 | int32 正文长度 | 正文。
        /// 找到名字、核对前面的长度、读出正文, 能解析成 XML 才算对。找不到抛异常。
        /// </summary>
        public static byte[] ExtractCrafting(byte[] assets)
        {
            byte[] name = CraftingAssetName;

            for (int at = IndexOf(assets, name, 0); at >= 0; at = IndexOf(assets, name, at + 1))
            {
                if (at < 4 || BitConverter.ToInt32(assets, at - 4) != name.Length)
                {
                    continue;      // 类型树里的同名字符串, 不是对象本身
                }

                int pos = at + name.Length;
                pos += (4 - pos % 4) % 4;
                if (pos + 4 > assets.Length)
                {
                    continue;
                }

                int length = BitConverter.ToInt32(assets, pos);
                pos += 4;
                if (length <= 0 || pos + length > assets.Length)
                {
                    continue;
                }

                byte[] payload = new byte[length];
                Buffer.BlockCopy(assets, pos, payload, 0, length);

                if (IsXml(payload))
                {
                    return payload;
                }
            }

            throw new InvalidDataException(Lang.T("files.noCrafting"));
        }

        private static bool IsXml(byte[] payload)
        {
            try
            {
                XmlDocument document = new XmlDocument { XmlResolver = null };
                using (MemoryStream stream = new MemoryStream(payload))
                using (XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
                       {
                           DtdProcessing = DtdProcessing.Prohibit,
                           XmlResolver = null,
                       }))
                {
                    document.Load(reader);
                }

                return document.DocumentElement != null;
            }
            catch (XmlException)
            {
                return false;
            }
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start)
        {
            for (int i = start; i <= haystack.Length - needle.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j])
                {
                    j++;
                }

                if (j == needle.Length)
                {
                    return i;
                }
            }

            return -1;
        }

        // ==================================================================
        // 在 Steam 库里找游戏 (和登陆器 GameLocator 同一个找法)
        // ==================================================================

        public static bool IsGameDir(string directory) =>
            !string.IsNullOrWhiteSpace(directory) && SafeFileExists(Path.Combine(directory, GameExe));

        /// <summary>在每个 Steam 安装目录的每个库里找游戏目录; 找不到返回 null。</summary>
        public static string FindGame(IEnumerable<string> steamDirs)
        {
            foreach (string steam in steamDirs ?? new string[0])
            {
                foreach (string library in Libraries(steam))
                {
                    string candidate = Path.Combine(library, "steamapps", "common", InstallDir(library));
                    if (IsGameDir(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
            }

            return null;
        }

        public static List<string> Libraries(string steamDir)
        {
            List<string> libraries = new List<string>();
            if (string.IsNullOrWhiteSpace(steamDir))
            {
                return libraries;
            }

            libraries.Add(steamDir);

            try
            {
                string vdf = Path.Combine(steamDir, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    foreach (Match path in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    {
                        string library = path.Groups[1].Value.Replace("\\\\", "\\");
                        if (!libraries.Exists(known =>
                                string.Equals(known, library, StringComparison.OrdinalIgnoreCase)))
                        {
                            libraries.Add(library);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                                           || ex is ArgumentException)
            {
            }

            return libraries;
        }

        private static string InstallDir(string library)
        {
            try
            {
                string manifest = Path.Combine(library, "steamapps", "appmanifest_" + SteamAppId + ".acf");
                if (File.Exists(manifest))
                {
                    Match found = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
                    if (found.Success)
                    {
                        return found.Groups[1].Value;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                                           || ex is ArgumentException)
            {
            }

            return DefaultInstallDir;
        }

        // ==================================================================

        private static bool HasAll(string directory, IEnumerable<string> files)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            foreach (string file in files)
            {
                if (!SafeFileExists(Path.Combine(directory, file)))
                {
                    return false;
                }
            }

            return true;
        }

        private static void CopyDirectory(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from))
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
            }

            foreach (string sub in Directory.GetDirectories(from))
            {
                CopyDirectory(sub, Path.Combine(to, Path.GetFileName(sub)));
            }
        }

        private static bool SafeFileExists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static bool SafeDirectoryExists(string path)
        {
            try
            {
                return Directory.Exists(path);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
