using System;
using Serilog;
using ConductorCrafting;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace DIE.Server.Data;

/// <summary>一条账号记录, 只含 LoginDataMessage 真正要用到的字段。</summary>
public sealed class Account
{
    public ulong UserId;
    public long? SteamId;
    public string ProfileName = "Survivor";
    public long CreatedUtcTicks;
    public bool FirstLogin;
    public int Language;
    public byte VanityIcon;
    public uint StoryMapXp;
    public byte StoryMapVersion;
    public byte[] StoryMapData = Array.Empty<byte>();
    public byte[] CounterData = Array.Empty<byte>();
    public byte CrossroadProgress;
    public uint UnboundXp;
    public long RegularBoost;
    public long PremiumBoost;
    public int UnlockedDlc;
    public bool IsExpoUser;
    public int BanReason;
    public ushort BannedDays;

    public int Gold;
    public int Silver;
    public int CharacterPoints;
    public int ResearchPoints;
}

/// <summary>
/// 账号持久化。裸 ADO + Microsoft.Data.Sqlite, 没有 ORM。
///
/// 身份解析刻意收敛到 <see cref="ResolveOrCreate"/> 这一个方法后面:
/// Phase 1 因为 Steam 加密 app ticket 无法解密, 固定返回 user_id = 1;
/// Phase 4 换成"从客户端 patch 上报的 SteamID 查表"时, 只改这一个方法, 其余零改动。
/// </summary>
public sealed class AccountStore
{
    private readonly string _connectionString;
    private readonly CraftingCatalog _catalog;

    public AccountStore(string databasePath, CraftingCatalog catalog = null)
    {
        _catalog = catalog;
        string dir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    private SqliteConnection Open()
    {
        SqliteConnection conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>幂等建表。每次启动都跑, schema 里全是 IF NOT EXISTS。</summary>
    public void ApplySchema(string schemaSqlPath)
    {
        string sql = File.ReadAllText(schemaSqlPath);
        using SqliteConnection conn = Open();

        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        Migrate(conn);
    }

    /// <summary>
    /// 已经存在的库跟上 schema 的改动。
    ///
    /// ## 为什么光改 Schema.sql 不够
    ///
    /// 建表语句全是 <c>CREATE TABLE IF NOT EXISTS</c> —— 对**新**库正确, 对
    /// 已经建好的库则完全没有作用。玩家机器上那一份是从 Phase 1 一路用下来的,
    /// 我们后来改的表结构它一个都没吃到。
    ///
    /// 这一点是实测撞出来的, 而且症状离病因很远: 玩家报"商店点购买一直转圈",
    /// 服务端日志只有入口那一条 "购买请求: 1 档", 之后再无输出。真正发生的是
    /// <c>AddStackable</c> 的 <c>ON CONFLICT(user_id, item_id, type)</c> 在旧库上
    /// 抛 "ON CONFLICT clause does not match any PRIMARY KEY or UNIQUE constraint"
    /// (旧主键只有两列), 而这个异常被原厂 StunTCP 吞掉了 —— 详见
    /// <c>RequestServer.OnClientError</c>。
    ///
    /// ## 没上版本号表
    ///
    /// 每条迁移自己**检查现状**再决定做不做 (查 <c>PRAGMA table_info</c> 而不是
    /// 查一个 schema_version)。库只有几张表、迁移屈指可数, 这样比维护版本号简单,
    /// 而且对"手工动过库"的情况更稳。
    /// </summary>
    private static void Migrate(SqliteConnection conn)
    {
        FixStackablePrimaryKey(conn);
        SwapMutatedStarters(conn);

        // 必须排在 FixStackablePrimaryKey 之后 —— 它要用三列主键做 ON CONFLICT。
        RestoreBoundCollectibles(conn);
        CollapseCollectibleStacks(conn);

        // 注: 2026-09-11 有一版本机钥匙签名给 accounts 加过 identity_key 列, 当天就换成了
        // Steam 核验。跑过那一版的旧库里还留着这一列 (全 NULL), 没人读写, 不影响, 不专门删。
    }

    /// <summary>
    /// 把堆起来的**收藏品**收拢成一张, 多出来的那几张按原厂折算补成账号经验。
    ///
    /// ## 为什么库里会有第二张
    ///
    /// 图纸 / 装置蓝图 / 改装图是"有或没有", 原厂开到重复的会当场折成货币
    /// (<c>RewardManager.AddDuplicatedCurrencies</c>) —— 一个账号**永远不该有
    /// amount 大于 1 的收藏品行**。我们一直没实现那条, 所以重复的就原样堆着,
    /// 玩家既没拿到折算的经验, 库里也留下了原版不存在的状态 (开发库上实测有
    /// <c>Design 1084 amount=2</c>)。
    ///
    /// 折算现在实现了 (见 <see cref="DuplicateCollectibles"/>), 这里补上历史账:
    /// 多出来的每一张按原厂的表折成账号经验, 行收拢到 1。
    ///
    /// 幂等 —— 跑完之后没有 amount 大于 1 的收藏品行了, 再跑一次什么都不做。
    /// </summary>
    private static void CollapseCollectibleStacks(SqliteConnection conn)
    {
        if (!CraftingManager.HasLoadedData)
        {
            return;
        }

        List<(long UserId, ushort ItemId, int Type, int Extra)> stacked =
            new List<(long, ushort, int, int)>();

        using (SqliteCommand read = conn.CreateCommand())
        {
            read.CommandText = """
                SELECT user_id, item_id, type, amount
                  FROM inventory_stackables
                 WHERE amount > 1 AND type IN ($schematic, $gadget, $design)
                """;
            read.Parameters.AddWithValue("$schematic", (int)StackableType.Schematic);
            read.Parameters.AddWithValue("$gadget", (int)StackableType.GadgetBlueprint);
            read.Parameters.AddWithValue("$design", (int)StackableType.Design);

            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read())
            {
                stacked.Add((reader.GetInt64(0), (ushort)reader.GetInt64(1),
                    reader.GetInt32(2), reader.GetInt32(3) - 1));
            }
        }

        if (stacked.Count == 0)
        {
            return;
        }

        Dictionary<long, long> xpPerUser = new Dictionary<long, long>();

        foreach ((long userId, ushort itemId, int type, int extra) in stacked)
        {
            long xp = (long)DuplicateCollectibles.For((StackableType)type, itemId).AccountXP * extra;
            xpPerUser[userId] = xpPerUser.TryGetValue(userId, out long already) ? already + xp : xp;
        }

        using SqliteTransaction tx = conn.BeginTransaction();

        using (SqliteCommand collapse = conn.CreateCommand())
        {
            collapse.Transaction = tx;
            collapse.CommandText = """
                UPDATE inventory_stackables SET amount = 1
                 WHERE amount > 1 AND type IN ($schematic, $gadget, $design)
                """;
            collapse.Parameters.AddWithValue("$schematic", (int)StackableType.Schematic);
            collapse.Parameters.AddWithValue("$gadget", (int)StackableType.GadgetBlueprint);
            collapse.Parameters.AddWithValue("$design", (int)StackableType.Design);
            collapse.ExecuteNonQuery();
        }

        using (SqliteCommand award = conn.CreateCommand())
        {
            award.Transaction = tx;
            award.CommandText = """
                UPDATE accounts
                   SET story_map_xp = MIN(story_map_xp + $xp, $cap)
                 WHERE user_id = $id
                """;

            SqliteParameter xpParam = award.Parameters.Add("$xp", SqliteType.Integer);
            award.Parameters.AddWithValue("$cap", (long)uint.MaxValue);
            SqliteParameter idParam = award.Parameters.Add("$id", SqliteType.Integer);

            foreach ((long userId, long xp) in xpPerUser)
            {
                if (xp <= 0)
                {
                    continue;
                }

                xpParam.Value = xp;
                idParam.Value = userId;
                award.ExecuteNonQuery();
            }
        }

        tx.Commit();

        Log.Information("收拢了 {Rows} 行堆起来的收藏品, 补发账号经验 {Xp}",
            stacked.Count, xpPerUser.Values.Sum());
    }
    /// <summary>
    /// 把装在武器上的改装图、以及造过的武器图纸**补回收藏**。
    ///
    /// ## 为什么要补
    ///
    /// 2026-09-22 之前我们把这两样当消耗品扣掉了 (装一张少一张、造一把少一张图纸)。
    /// 原厂它们是收藏品, 判据见 <c>MenuHandlers.OnAddDesignsToWeapon</c> 那一大段。
    /// 现在不扣了, 但已经扣掉的还在库里对不上 —— 玩家的表现是**已镶嵌的槽位全是空的**,
    /// 因为客户端画已装的图要先在背包里找得到它 (<c>InventoryManager.GetDesign</c>)。
    ///
    /// ## 判据是武器自己身上的槽
    ///
    /// 一件武器的 <c>Slots</c> 里写着它镶了哪几张图 —— 那就是"这个账号确实拥有过
    /// 这张图"的铁证, 不需要翻日志也不需要猜。图纸同理: 手上有这件武器就说明
    /// 图纸拥有过 (初始武器除外, 那两把是白送的, 客户端也不给它们建图纸条目)。
    ///
    /// 补的是"有没有"而不是数量 —— 已经有的一律不动 (<c>DO NOTHING</c>),
    /// 所以跑多少次结果都一样, 也不会凭空给玩家刷出第二张。
    /// </summary>
    private static void RestoreBoundCollectibles(SqliteConnection conn)
    {
        // 图鉴没加载就判不了"是不是初始武器", 也解不了武器 blob —— 跳过。
        // 看的是 CraftingManager 这个全局静态而不是我们那层包装的标志位:
        // 数据装没装好是它说了算 (<see cref="CraftingCatalog.Load"/> 灌的就是它)。
        if (!CraftingManager.HasLoadedData)
        {
            return;
        }

        List<(long UserId, ushort ItemId, int Type)> missing =
            new List<(long, ushort, int)>();

        using (SqliteCommand read = conn.CreateCommand())
        {
            read.CommandText = "SELECT user_id, blob FROM inventory_uniques";
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read())
            {
                long userId = reader.GetInt64(0);
                if (reader.GetValue(1) is not byte[] blob || blob.Length == 0)
                {
                    continue;
                }

                ConductorCrafting.Unique weapon;
                try
                {
                    weapon = CraftingCatalog.FromBlob(blob);
                }
                catch (IOException)
                {
                    continue;   // 坏掉的一行不该拦住开服
                }

                foreach (UniqueSlot slot in CraftingCatalog.DecodeSlots(weapon.Slots))
                {
                    missing.Add((userId, slot.PerkID, (int)StackableType.Design));
                }

                // 初始武器 (Paddle / ScrapShotgun) 是白送的, 没有图纸这一说
                if (CraftingManager.GetWeaponSchematic(weapon.SchematicID) != null
                    && !CraftingManager.IsDefaultWeapon(weapon.SchematicID))
                {
                    missing.Add((userId, weapon.SchematicID, (int)StackableType.Schematic));
                }
            }
        }

        if (missing.Count == 0)
        {
            return;
        }

        int restored = 0;

        using SqliteTransaction tx = conn.BeginTransaction();
        using (SqliteCommand insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO inventory_stackables (user_id, item_id, type, amount)
                VALUES ($uid, $iid, $type, 1)
                ON CONFLICT(user_id, item_id, type) DO NOTHING
                """;

            SqliteParameter uid = insert.Parameters.Add("$uid", SqliteType.Integer);
            SqliteParameter iid = insert.Parameters.Add("$iid", SqliteType.Integer);
            SqliteParameter type = insert.Parameters.Add("$type", SqliteType.Integer);

            foreach ((long userId, ushort itemId, int stackableType) in missing.Distinct())
            {
                uid.Value = userId;
                iid.Value = (long)itemId;
                type.Value = stackableType;
                restored += insert.ExecuteNonQuery();
            }
        }

        tx.Commit();

        if (restored > 0)
        {
            Log.Information("补回了 {Count} 件被误当消耗品扣掉的收藏 (改装图 / 武器图纸)", restored);
        }
    }
    /// <summary>
    /// 老账号的初始角色从变异版换成普通版。
    ///
    /// 早先 <see cref="CharacterCatalog.Starter"/> 误取了 BergMutated / IsysMutated / AmberMutated
    /// (见那边的说明), 每个老账号手上都是这三个。
    ///
    /// ## 判据: 三个变异版都有、三个普通版一个都没有
    ///
    /// 那正是"只拿了误发的初始角色"的样子 —— 变异版买不了第二次 (已拥有), 所以这三个一定是误发的;
    /// 而只要手上有任何一个普通版, 就说明账号已经是新规则建的、或者自己买过, 不动。
    /// 这样不用版本号, 跑多少次都一样; 导入账号 (<see cref="AccountImporter"/>) 之后再跑一遍也能把
    /// 从旧库搬过来的账号换好。剧情节点 (blob 另一半) 原样保留。
    /// </summary>
    private static void SwapMutatedStarters(SqliteConnection conn)
    {
        List<(long UserId, byte[] Blob)> accounts = new List<(long, byte[])>();

        // 很老的库 (测试里模拟的 Phase 1 库) 还没有这一列 —— 没有就没有要换的
        using (SqliteCommand columns = conn.CreateCommand())
        {
            columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('accounts') WHERE name = 'story_map_data'";
            if (Convert.ToInt64(columns.ExecuteScalar()) == 0)
            {
                return;
            }
        }

        using (SqliteCommand read = conn.CreateCommand())
        {
            read.CommandText = "SELECT user_id, story_map_data FROM accounts WHERE story_map_data IS NOT NULL";
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetValue(1) is byte[] blob && blob.Length > 0)
                {
                    accounts.Add((reader.GetInt64(0), blob));
                }
            }
        }

        foreach ((long userId, byte[] blob) in accounts)
        {
            var (owned, nodes) = CharacterCatalog.Read(blob);
            HashSet<CharacterEnum> set = new HashSet<CharacterEnum>(owned);

            if (!CharacterCatalog.MistakenStarter.All(set.Contains) || CharacterCatalog.Starter.Any(set.Contains))
            {
                continue;
            }

            foreach (CharacterEnum mutated in CharacterCatalog.MistakenStarter)
            {
                set.Remove(mutated);
            }

            foreach (CharacterEnum survivor in CharacterCatalog.Starter)
            {
                set.Add(survivor);
            }

            using SqliteCommand write = conn.CreateCommand();
            write.CommandText = "UPDATE accounts SET story_map_data = $blob WHERE user_id = $id";
            write.Parameters.AddWithValue("$id", userId);
            write.Parameters.AddWithValue("$blob", CharacterCatalog.BuildStoryMapData(set, nodes));
            write.ExecuteNonQuery();

            Log.Information("账号 {UserId} 的初始角色从变异版换成普通版 (Berg/Isys/Amber Survivor)", userId);
        }
    }

    /// <summary>按 SteamID 找已有账号, 不创建、不改昵称。没有返回 null。</summary>
    public Account FindBySteamId(ulong steamId)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT user_id FROM accounts WHERE steam_id = $steam";
        cmd.Parameters.AddWithValue("$steam", (long)steamId);
        object userId = cmd.ExecuteScalar();
        return userId == null ? null : Load((ulong)Convert.ToInt64(userId));
    }

    /// <summary>未验证身份的人进的共享账号 (没装插件 / Steam 核验被拒)。</summary>
    public Account SharedAccount(string profileName, int languageFromClient) =>
        ResolveOrCreate((ulong?)null, profileName, languageFromClient);

    /// <summary>
    /// <c>inventory_stackables</c> 的主键补上 <c>type</c>。
    ///
    /// 旧主键是 <c>(user_id, item_id)</c>。零件和消耗品各自从 1 开始编号, 于是
    /// "第 5 号零件"和"第 5 号消耗品"会互相覆盖 —— 这是当初加 <c>type</c> 列的原因,
    /// 但主键漏了。
    ///
    /// 合并时按 <c>(user_id, item_id, type)</c> 求和: 旧库里这一组本来就唯一,
    /// SUM 只是防手工改过库的情况, 不会凭空多出数量。
    /// </summary>
    private static void FixStackablePrimaryKey(SqliteConnection conn)
    {
        using (SqliteCommand probe = conn.CreateCommand())
        {
            probe.CommandText = "PRAGMA table_info(inventory_stackables)";

            bool tableExists = false;
            bool typeIsInKey = false;

            using SqliteDataReader r = probe.ExecuteReader();
            while (r.Read())
            {
                tableExists = true;

                if (r.GetString(1) == "type" && r.GetInt32(5) != 0)
                {
                    typeIsInKey = true;
                }
            }

            if (!tableExists || typeIsInKey)
            {
                return;
            }
        }

        Log.Warning("正在迁移 inventory_stackables: 主键补上 type 列");

        using SqliteCommand migrate = conn.CreateCommand();
        migrate.CommandText = """
            PRAGMA foreign_keys = OFF;
            BEGIN;
            ALTER TABLE inventory_stackables RENAME TO inventory_stackables_old;
            CREATE TABLE inventory_stackables (
                user_id INTEGER NOT NULL REFERENCES accounts(user_id) ON DELETE CASCADE,
                item_id INTEGER NOT NULL,
                type    INTEGER NOT NULL DEFAULT 0,
                amount  INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (user_id, item_id, type)
            );
            INSERT INTO inventory_stackables (user_id, item_id, type, amount)
                 SELECT user_id, item_id, type, SUM(amount)
                   FROM inventory_stackables_old
               GROUP BY user_id, item_id, type;
            DROP TABLE inventory_stackables_old;
            COMMIT;
            PRAGMA foreign_keys = ON;
            """;
        migrate.ExecuteNonQuery();
    }

    /// <summary>
    /// 没有 SteamID 时使用的共享账号 —— 也就是 DIE.Diagnostics 插件未安装的情况。
    /// 保留它是为了向后兼容: 没装插件的客户端仍然能登录, 只是所有人共用一个账号。
    /// </summary>
    private const ulong FallbackUserId = 1UL;

    /// <summary>
    /// 身份解析。
    ///
    /// 客户端发来的是 ISteamUser::GetEncryptedAppTicket 的产物, 解密需要该 App 的
    /// Steam 私钥, 我们拿不到 —— 所以协议本身**无法**告诉我们玩家是谁。
    ///
    /// 解决办法是让客户端自己上报: DIE.Diagnostics 插件在 GameLink.LoginRequest 的
    /// 发送端把昵称改写成 "&lt;steamid&gt;|&lt;原昵称&gt;"。这里把它拆开。
    ///
    /// 没有前缀 (未装插件) 就退回共享账号, 新旧客户端可以混用。
    ///
    /// ⚠️ 这个重载**不核验身份**, 只拆昵称 —— 登录路径先让 Steam 核验
    /// (<c>LoginHandlers.ResolveIdentity</c>), 通过了才拿 SteamID 调下面那个重载。
    /// </summary>
    public Account ResolveOrCreate(string profileNameFromClient, int languageFromClient)
    {
        (ulong? steamId, string profileName) = ParseTaggedName(profileNameFromClient);
        return ResolveOrCreate(steamId, profileName, languageFromClient);
    }

    public Account ResolveOrCreate(ulong? steamId, string profileName, int languageFromClient)
    {
        if (string.IsNullOrEmpty(profileName))
        {
            profileName = "Survivor";
        }

        using SqliteConnection conn = Open();
        using SqliteTransaction tx = conn.BeginTransaction();

        ulong userId = steamId.HasValue
            ? ResolveBySteamId(conn, tx, steamId.Value)
            : FallbackUserId;

        using (SqliteCommand insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO accounts (user_id, steam_id, profile_name, created_utc, first_login, language)
                VALUES ($id, $steam, $name, $created, 1, $lang)
                ON CONFLICT(user_id) DO UPDATE SET
                    profile_name   = $name,
                    language       = $lang,
                    last_login_utc = $created
                """;
            insert.Parameters.AddWithValue("$id", (long)userId);
            insert.Parameters.AddWithValue("$steam", steamId.HasValue ? (object)(long)steamId.Value : DBNull.Value);
            insert.Parameters.AddWithValue("$name", profileName);
            insert.Parameters.AddWithValue("$created", DateTime.UtcNow.Ticks);
            insert.Parameters.AddWithValue("$lang", languageFromClient);
            insert.ExecuteNonQuery();
        }

        // 新账号的四种货币都从 0 起。
        //
        // 早先这里种过 1000 金 / 50000 银, 那是商店还空着时为了"看得出 UI 有没有
        // 对上"塞的调试数据。现在掉落和商店都通了, 留着等于开局就把货架买空 ——
        // 玩家实测后要求删掉。
        //
        // 行本身还是要建的: 客户端读余额时四种都要有, 缺行会被当成读不到。
        using (SqliteCommand seed = conn.CreateCommand())
        {
            seed.Transaction = tx;
            seed.CommandText = """
                INSERT INTO currencies (user_id, kind, amount) VALUES
                    ($id, 0, 0), ($id, 1, 0), ($id, 2, 0), ($id, 3, 0)
                ON CONFLICT(user_id, kind) DO NOTHING
                """;
            seed.Parameters.AddWithValue("$id", (long)userId);
            seed.ExecuteNonQuery();
        }

        tx.Commit();

        EnsureStarterInventory(userId);
        EnsureCharacters(userId);
        return Load(userId);
    }

    /// <summary>
    /// SteamID → user_id。已知的直接返回, 新面孔分配一个新号。
    ///
    /// user_id 刻意不直接用 SteamID: 它是 ulong, 而 SQLite 的 INTEGER 是有符号 64 位,
    /// 大于 long.MaxValue 的 SteamID 会溢出。用自增小整数, SteamID 只作为索引列。
    /// </summary>
    private static ulong ResolveBySteamId(SqliteConnection conn, SqliteTransaction tx, ulong steamId)
    {
        using (SqliteCommand find = conn.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = "SELECT user_id FROM accounts WHERE steam_id = $steam";
            find.Parameters.AddWithValue("$steam", (long)steamId);

            object existing = find.ExecuteScalar();
            if (existing != null)
            {
                return (ulong)Convert.ToInt64(existing);
            }
        }

        using SqliteCommand next = conn.CreateCommand();
        next.Transaction = tx;
        // 从 2 开始, 把 1 留给未装插件的共享账号
        next.CommandText = "SELECT COALESCE(MAX(user_id), 1) + 1 FROM accounts";
        return (ulong)Convert.ToInt64(next.ExecuteScalar());
    }

    /// <summary>
    /// 拆开 DIE.Diagnostics 插件写入的 "&lt;17位SteamID&gt;|&lt;原昵称&gt;"。
    /// 格式不匹配就原样返回 —— 未装插件的客户端照常工作。
    /// </summary>
    public static (ulong? SteamId, string ProfileName) ParseTaggedName(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return (null, raw);
        }

        int bar = raw.IndexOf('|');
        if (bar != 17)
        {
            return (null, raw);
        }

        if (!ulong.TryParse(raw.Substring(0, bar), out ulong steamId) || steamId == 0UL)
        {
            return (null, raw);
        }

        return (steamId, raw.Substring(bar + 1));
    }

    /// <summary>
    /// 给还没有任何武器的账号发一套初始装备。
    ///
    /// 不发的话背包是空的, 客户端在开教程时会抛异常并打
    /// "TUTORIAL: Could not equip defaul Melee Weapon!" (Matchmaker.cs:119)。
    ///
    /// 只在武器数为 0 时发放, 所以重复登录不会刷出多余物品; 玩家把武器全卖了
    /// 之后重登会补发一套 —— 对私服来说这是可接受的行为。
    /// </summary>
    private void EnsureStarterInventory(ulong userId)
    {
        if (_catalog == null || !_catalog.IsLoaded)
        {
            return;
        }

        using SqliteConnection conn = Open();

        using (SqliteCommand count = conn.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM inventory_uniques WHERE user_id = $id";
            count.Parameters.AddWithValue("$id", (long)userId);
            if (Convert.ToInt64(count.ExecuteScalar()) > 0)
            {
                return;
            }
        }

        IReadOnlyList<ConductorCrafting.Unique> starters = _catalog.CreateStarterWeapons(userId);
        if (starters.Count == 0)
        {
            return;
        }

        using SqliteTransaction tx = conn.BeginTransaction();
        foreach (ConductorCrafting.Unique weapon in starters)
        {
            using SqliteCommand insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO inventory_uniques (user_id, blob) VALUES ($id, $blob)";
            insert.Parameters.AddWithValue("$id", (long)userId);
            insert.Parameters.AddWithValue("$blob", CraftingCatalog.ToBlob(weapon));
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// 保证账号至少拥有一批角色。
    ///
    /// ## 为什么必须发
    ///
    /// 角色所有权编码在 <c>story_map_data</c> blob 里 (详见 <see cref="CharacterCatalog"/>)。
    /// blob 为空时客户端解不出任何角色, 大厅的 SELECT CHARACTER 全是灰格子,
    /// READY 点不动 —— **除了不要求角色的新手任务, 所有模式都进不去**。
    ///
    /// 官方服 2015 年关停, 玩家原有的解锁数据只存在于 Stunlock 的账号服务器上,
    /// 已经不可能取回 (对局记录和排行榜能看到, 是因为那些托管在 Steam 上)。
    /// 所以私服只能重新发放。
    ///
    /// ## 为什么是全部角色
    ///
    /// 这是个可以随时改的产品决定, 不是技术约束。当前发放 <c>[Hide]</c> 之外的
    /// 全部角色, 理由是: 私服没有内购, 逐个解锁只是徒增门槛; 而且原本的解锁进度
    /// 已经无从考证, 发一部分不比发全部更"还原"。
    ///
    /// 要改成解锁子集, 换掉传给 BuildStoryMapData 的集合即可。
    ///
    /// ## 只在空的时候发
    ///
    /// 和 <see cref="EnsureStarterInventory"/> 一样是幂等的 —— 已经有角色就不动,
    /// 免得覆盖掉玩家后续的角色经验值。
    /// </summary>
    private void EnsureCharacters(ulong userId)
    {
        using SqliteConnection conn = Open();

        byte[] existing;
        using (SqliteCommand read = conn.CreateCommand())
        {
            read.CommandText = "SELECT story_map_data FROM accounts WHERE user_id = $id";
            read.Parameters.AddWithValue("$id", (long)userId);
            object value = read.ExecuteScalar();
            existing = value as byte[];
        }

        // ## 只补初始角色, 而且**只增不减**
        //
        // 早先这里发的是全部 Grantable, 名单不一致就整个重写。玩家把商店改成
        // "角色用角色点买"之后那样不行了 —— 货架上摆的全是你已经有的。
        //
        // 现在是并集: 已有的一个不动 (买来的角色不能被这一步抹掉), 缺的初始
        // 角色补上。老账号因此不会掉东西。
        // blob 的**两半一起读**: 角色和剧情节点存在同一段字节里, 只读一半就重写
        // 会把另一半清空 —— 早先这里就是这么把新手任务进度抹掉的。
        var (existingOwned, nodes) = CharacterCatalog.Read(existing);

        var owned = new HashSet<CharacterEnum>(existingOwned);
        int before = owned.Count;

        foreach (CharacterEnum starter in CharacterCatalog.Starter)
        {
            owned.Add(starter);
        }

        if (owned.Count == before && existing != null && existing.Length > 0)
        {
            return;
        }

        byte[] blob = CharacterCatalog.BuildStoryMapData(owned, nodes);

        using SqliteCommand write = conn.CreateCommand();
        write.CommandText =
            "UPDATE accounts SET story_map_data = $blob, story_map_version = 1 WHERE user_id = $id";
        write.Parameters.AddWithValue("$id", (long)userId);
        write.Parameters.AddWithValue("$blob", blob);
        write.ExecuteNonQuery();

        Log.Information("账号 {UserId} 角色名单 {Count} 个 ({Bytes}B story_map_data)",
            userId, owned.Count, blob.Length);
    }

    /// <summary>
    /// 解锁一个角色 —— 商店买角色时用。
    ///
    /// 角色所有权编码在 <c>story_map_data</c> blob 里 (详见
    /// <see cref="CharacterCatalog"/>), 所以是"读出来、加一个、写回去"。
    ///
    /// 幂等: 已经有了就返回 false 且不写库 —— 调用方据此拒绝重复购买,
    /// 免得玩家花了钱什么也没多。
    /// </summary>
    /// <returns>真的加上了返回 true; 本来就有返回 false。</returns>
    public bool UnlockCharacter(ulong userId, CharacterEnum character)
    {
        using SqliteConnection conn = Open();

        byte[] existing;
        using (SqliteCommand read = conn.CreateCommand())
        {
            read.CommandText = "SELECT story_map_data FROM accounts WHERE user_id = $id";
            read.Parameters.AddWithValue("$id", (long)userId);
            existing = read.ExecuteScalar() as byte[];
        }

        // 两半一起读 —— 节点那一半要原样写回去, 否则买个角色就把新手任务进度
        // 抹掉了 (详见 CharacterCatalog.Read)。
        var (existingOwned, nodes) = CharacterCatalog.Read(existing);

        var owned = new HashSet<CharacterEnum>(existingOwned);

        if (!owned.Add(character))
        {
            return false;
        }

        byte[] blob = CharacterCatalog.BuildStoryMapData(owned, nodes);

        using SqliteCommand write = conn.CreateCommand();
        write.CommandText =
            "UPDATE accounts SET story_map_data = $blob, story_map_version = 1 WHERE user_id = $id";
        write.Parameters.AddWithValue("$id", (long)userId);
        write.Parameters.AddWithValue("$blob", blob);
        write.ExecuteNonQuery();

        Log.Information("账号 {UserId} 解锁角色 {Character} (共 {Count} 个)",
            userId, character, owned.Count);

        return true;
    }

    /// <summary>
    /// 剧情地图上已经解锁的节点 —— **新手任务的进度就是它**。
    /// </summary>
    public IReadOnlyList<StoryMapUnlockData> LoadUnlockedNodes(ulong userId)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand read = conn.CreateCommand();

        read.CommandText = "SELECT story_map_data FROM accounts WHERE user_id = $id";
        read.Parameters.AddWithValue("$id", (long)userId);

        return CharacterCatalog.ReadUnlockedNodes(read.ExecuteScalar() as byte[]);
    }

    /// <summary>
    /// 十字路口难度解锁往前推一步。
    ///
    /// ## 这一列以前只有人读没有人写
    ///
    /// <c>crossroad_progress</c> 一直在库里, <c>AccountMapper</c> 也把它下发了,
    /// 但全代码树**一处 UPDATE 都没有** —— 局末推进那一半在服务端, 随服务端丢了。
    /// 玩家打多少局, 大厅那排锁上都是 "(0/3) Completed"。
    ///
    /// 进度是**一个累计数**, 档位和"这档做了几局"都是从它算的, 所以这里只管 +1,
    /// 升档是自然发生的。判据和上限见 <see cref="Game.CrossroadUnlock"/>。
    /// </summary>
    /// <returns>推完之后的进度。本来就满了就返回原值。</returns>
    public byte AdvanceCrossroadProgress(ulong userId)
    {
        if (userId == 0)
        {
            return 0;
        }

        using SqliteConnection conn = Open();

        int current;
        using (SqliteCommand read = conn.CreateCommand())
        {
            read.CommandText = "SELECT crossroad_progress FROM accounts WHERE user_id = $id";
            read.Parameters.AddWithValue("$id", (long)userId);

            object value = read.ExecuteScalar();
            if (value == null || value == DBNull.Value)
            {
                return 0;
            }

            current = Convert.ToInt32(value);
        }

        int next = Game.CrossroadUnlock.Advance(current);

        if (next == current)
        {
            return (byte)current;
        }

        using SqliteCommand write = conn.CreateCommand();
        write.CommandText =
            "UPDATE accounts SET crossroad_progress = $progress WHERE user_id = $id";
        write.Parameters.AddWithValue("$progress", next);
        write.Parameters.AddWithValue("$id", (long)userId);
        write.ExecuteNonQuery();

        return (byte)next;
    }

    /// <summary>这个人现在的十字路口解锁进度 (累计步数)。</summary>
    public byte CrossroadProgressOf(ulong userId)
    {
        if (userId == 0)
        {
            return 0;
        }

        using SqliteConnection conn = Open();
        using SqliteCommand read = conn.CreateCommand();

        read.CommandText = "SELECT crossroad_progress FROM accounts WHERE user_id = $id";
        read.Parameters.AddWithValue("$id", (long)userId);

        object value = read.ExecuteScalar();
        return value == null || value == DBNull.Value ? (byte)0 : (byte)Convert.ToInt32(value);
    }

    /// <summary>
    /// 把一个剧情地图节点往前推一次。
    ///
    /// ## 为什么是"推一次"而不是"设成已解锁"
    ///
    /// 客户端判已解锁的条件是
    /// <c>UnlockedTimes &gt;= StoryMapData.Data.GetNode(id).Unlocks</c>,
    /// 而 <c>Unlocks</c> 不都是 1 —— 节点 11 (<c>StarterQuestPlay3Matches</c>)
    /// 是 **3**, 也就是"打满三局"。所以这里累加, 由调用方决定推几次。
    ///
    /// 上限也交给调用方 (<paramref name="maxTimes"/>): 推过头没有坏处, 但
    /// <c>UnlockedTimes</c> 是 byte, 一直加会绕回 0 把进度清掉。
    /// </summary>
    /// <returns>推完之后这个节点的次数。本来就到顶了就返回原值。</returns>
    public byte AdvanceStoryMapNode(ulong userId, int nodeId, byte maxTimes = 1)
    {
        using SqliteConnection conn = Open();

        byte[] existing;
        using (SqliteCommand read = conn.CreateCommand())
        {
            read.CommandText = "SELECT story_map_data FROM accounts WHERE user_id = $id";
            read.Parameters.AddWithValue("$id", (long)userId);
            existing = read.ExecuteScalar() as byte[];
        }

        var (owned, existingNodes) = CharacterCatalog.Read(existing);

        List<StoryMapUnlockData> nodes = existingNodes.ToList();
        int at = nodes.FindIndex(n => n.NodeID == nodeId);

        byte times;

        if (at < 0)
        {
            times = 1;
            nodes.Add(new StoryMapUnlockData
            {
                NodeID = nodeId,
                UnlockedTimes = times,

                // 空数组而不是 null —— 原厂的 Serialize 对 null 有兜底
                // (写个 0), 但读回来的一致性更省心。
                RewardUnlockChoices = Array.Empty<byte>(),
            });
        }
        else if (nodes[at].UnlockedTimes >= maxTimes)
        {
            return nodes[at].UnlockedTimes;
        }
        else
        {
            times = (byte)(nodes[at].UnlockedTimes + 1);

            StoryMapUnlockData updated = nodes[at];
            updated.UnlockedTimes = times;
            updated.RewardUnlockChoices ??= Array.Empty<byte>();
            nodes[at] = updated;
        }

        byte[] blob = CharacterCatalog.BuildStoryMapData(owned, nodes);

        using SqliteCommand write = conn.CreateCommand();
        write.CommandText =
            "UPDATE accounts SET story_map_data = $blob, story_map_version = 1 WHERE user_id = $id";
        write.Parameters.AddWithValue("$id", (long)userId);
        write.Parameters.AddWithValue("$blob", blob);
        write.ExecuteNonQuery();

        Log.Information("账号 {UserId} 剧情节点 {NodeId} 推进到 {Times}/{Max}",
            userId, nodeId, times, maxTimes);

        return times;
    }

    /// <summary>读出账号的全部武器, 已从 blob 还原成协议结构。</summary>
    public IReadOnlyList<ConductorCrafting.Unique> LoadUniques(ulong userId)
    {
        List<ConductorCrafting.Unique> uniques = new List<ConductorCrafting.Unique>();
        foreach (byte[] blob in LoadUniqueBlobs(userId))
        {
            uniques.Add(CraftingCatalog.FromBlob(blob));
        }
        return uniques;
    }

    /// <summary>
    /// 给账号加经验, 返回加完之后的总量。
    ///
    /// ## 为什么写进 <c>story_map_xp</c>
    ///
    /// 账号等级是从它算的 —— <c>MatchHandlers</c> 开局时用
    /// <c>AccountLevelHelpers.ExperienceToLevel(account.StoryMapXp)</c> 换算
    /// 出等级传给战斗服 (那个等级决定技能栏里有几个技能)。
    /// 也就是说这一个字段就是"账号经验", 加在这里, 下一局开局就生效。
    ///
    /// <c>unbound_xp</c> 是另一回事 (角色间可自由分配的那份), 不动它。
    ///
    /// ## 上溢
    ///
    /// 列是 INTEGER (SQLite 里是 64 位), 而 <see cref="Account.StoryMapXp"/>
    /// 是 uint。SQL 那边加完可能超过 uint.MaxValue, 读回来强转会绕回去,
    /// 所以在 SQL 里就用 <c>MIN(...)</c> 封顶。正常玩一辈子也到不了,
    /// 但不封的话一次异常调用就能把账号等级打成 1 级。
    /// </summary>
    /// <returns>加完之后的总经验。账号不存在时返回 0 且不做任何事。</returns>
    public uint AddStoryMapXp(ulong userId, uint amount)
    {
        if (amount == 0)
        {
            return Load(userId).StoryMapXp;
        }

        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();

        cmd.CommandText = """
            UPDATE accounts
               SET story_map_xp = MIN(story_map_xp + $amount, $cap)
             WHERE user_id = $id
            RETURNING story_map_xp
            """;
        cmd.Parameters.AddWithValue("$amount", (long)amount);
        cmd.Parameters.AddWithValue("$cap", (long)uint.MaxValue);
        cmd.Parameters.AddWithValue("$id", (long)userId);

        object result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? 0u : (uint)Convert.ToInt64(result);
    }

    /// <summary>
    /// 把一件装置存进背包。和 <see cref="AddUnique"/> 同一个形状。
    /// </summary>
    public void AddGadget(ulong userId, ConductorCrafting.Gadget gadget)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand insert = conn.CreateCommand();

        insert.CommandText = "INSERT INTO inventory_gadgets (user_id, blob) VALUES ($id, $blob)";
        insert.Parameters.AddWithValue("$id", (long)userId);
        insert.Parameters.AddWithValue("$blob", CraftingCatalog.ToBlob(gadget));
        insert.ExecuteNonQuery();
    }

    /// <summary>读出账号的全部装置, 已从 blob 还原成协议结构。</summary>
    public IReadOnlyList<ConductorCrafting.Gadget> LoadGadgets(ulong userId)
    {
        List<ConductorCrafting.Gadget> gadgets = new List<ConductorCrafting.Gadget>();

        foreach (byte[] blob in LoadGadgetBlobs(userId))
        {
            gadgets.Add(CraftingCatalog.GadgetFromBlob(blob));
        }

        return gadgets;
    }

    /// <summary>读一个账号的全部可堆叠物 (蓝图/零件/消耗品/宝箱/改装图)。</summary>
    public IReadOnlyList<ConductorCrafting.Stackable> LoadStackables(ulong userId)
    {
        List<ConductorCrafting.Stackable> items = new List<ConductorCrafting.Stackable>();

        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();

        cmd.CommandText = """
            SELECT item_id, type, amount FROM inventory_stackables
             WHERE user_id = $id AND amount > 0
            """;
        cmd.Parameters.AddWithValue("$id", (long)userId);

        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            items.Add(new ConductorCrafting.Stackable(
                id: (ushort)r.GetInt32(0),
                type: (ConductorCrafting.StackableType)r.GetInt32(1),
                userID: userId,
                amount: (ushort)r.GetInt32(2)));
        }

        return items;
    }

    /// <summary>
    /// 加 (或扣) 一种可堆叠物, 返回加完之后的数量。
    ///
    /// 扣不出负数 (<c>MAX(..., 0)</c>)。数量归零的行会被删掉 —— 客户端背包里
    /// 不该出现"零个零件"那种条目。
    /// </summary>
    public int AddStackable(ulong userId, ushort itemId,
                            ConductorCrafting.StackableType type, int delta)
    {
        using SqliteConnection conn = Open();

        int amount;

        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO inventory_stackables (user_id, item_id, type, amount)
                VALUES ($id, $item, $type, MAX($delta, 0))
                ON CONFLICT(user_id, item_id, type) DO UPDATE
                    SET amount = MAX(amount + $delta, 0)
                RETURNING amount
                """;
            cmd.Parameters.AddWithValue("$id", (long)userId);
            cmd.Parameters.AddWithValue("$item", (int)itemId);
            cmd.Parameters.AddWithValue("$type", (int)type);
            cmd.Parameters.AddWithValue("$delta", delta);

            object result = cmd.ExecuteScalar();
            amount = result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
        }

        if (amount == 0)
        {
            using SqliteCommand prune = conn.CreateCommand();
            prune.CommandText = """
                DELETE FROM inventory_stackables
                 WHERE user_id = $id AND item_id = $item AND type = $type
                """;
            prune.Parameters.AddWithValue("$id", (long)userId);
            prune.Parameters.AddWithValue("$item", (int)itemId);
            prune.Parameters.AddWithValue("$type", (int)type);
            prune.ExecuteNonQuery();
        }

        return amount;
    }

    /// <summary>某个账号有几个这东西。</summary>
    public int CountStackable(ulong userId, ushort itemId,
                              ConductorCrafting.StackableType type)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();

        cmd.CommandText = """
            SELECT amount FROM inventory_stackables
             WHERE user_id = $id AND item_id = $item AND type = $type
            """;
        cmd.Parameters.AddWithValue("$id", (long)userId);
        cmd.Parameters.AddWithValue("$item", (int)itemId);
        cmd.Parameters.AddWithValue("$type", (int)type);

        object result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
    }

    /// <summary>
    /// 一次性扣掉一批可堆叠物 —— **要么全扣, 要么一个不动**。
    ///
    /// 合成要吃掉一张蓝图和若干零件, 中间任何一样不够都不能扣, 否则玩家的
    /// 材料会凭空少掉一半而东西没造出来。所以先在事务里全查一遍再扣。
    /// </summary>
    /// <returns>全部够且扣掉了返回 true; 任何一样不够返回 false 且不改动。</returns>
    public bool TryConsumeStackables(
        ulong userId,
        IReadOnlyList<(ushort ItemId, ConductorCrafting.StackableType Type, int Amount)> wanted)
    {
        if (wanted == null || wanted.Count == 0)
        {
            return true;
        }

        // ## 同一样东西出现两次要先合并
        //
        // 下面是"先全查一遍再全扣一遍"。同一个 (item, type) 出现两条时, 两条都
        // 是拿**扣之前**的余额去比的 —— 手上只有 1 张改装图, 却要装进两个槽,
        // 两条 (design, 1) 各自都能过 `amount >= 1`, 然后接连各扣 1, 余额变成
        // -1, 最后被 prune 那句当成空堆删掉。玩家白得一次安装, 图还没了。
        //
        // 合并成一条 (design, 2) 之后, 检查那一步才是拿真实需求量去比的。
        List<(ushort ItemId, ConductorCrafting.StackableType Type, int Amount)> merged =
            wanted
                .GroupBy(w => (w.ItemId, w.Type))
                .Select(g => (g.Key.ItemId, g.Key.Type, g.Sum(w => w.Amount)))
                .ToList();

        using SqliteConnection conn = Open();
        using SqliteTransaction tx = conn.BeginTransaction();

        foreach ((ushort itemId, ConductorCrafting.StackableType type, int need) in merged)
        {
            using SqliteCommand check = conn.CreateCommand();
            check.Transaction = tx;
            check.CommandText = """
                SELECT amount FROM inventory_stackables
                 WHERE user_id = $id AND item_id = $item AND type = $type
                """;
            check.Parameters.AddWithValue("$id", (long)userId);
            check.Parameters.AddWithValue("$item", (int)itemId);
            check.Parameters.AddWithValue("$type", (int)type);

            object have = check.ExecuteScalar();
            int amount = have == null || have == DBNull.Value ? 0 : Convert.ToInt32(have);

            if (amount < need)
            {
                tx.Rollback();
                return false;
            }
        }

        foreach ((ushort itemId, ConductorCrafting.StackableType type, int need) in merged)
        {
            using SqliteCommand spend = conn.CreateCommand();
            spend.Transaction = tx;
            spend.CommandText = """
                UPDATE inventory_stackables SET amount = amount - $need
                 WHERE user_id = $id AND item_id = $item AND type = $type
                """;
            spend.Parameters.AddWithValue("$id", (long)userId);
            spend.Parameters.AddWithValue("$item", (int)itemId);
            spend.Parameters.AddWithValue("$type", (int)type);
            spend.Parameters.AddWithValue("$need", need);
            spend.ExecuteNonQuery();
        }

        using (SqliteCommand prune = conn.CreateCommand())
        {
            prune.Transaction = tx;
            prune.CommandText =
                "DELETE FROM inventory_stackables WHERE user_id = $id AND amount <= 0";
            prune.Parameters.AddWithValue("$id", (long)userId);
            prune.ExecuteNonQuery();
        }

        tx.Commit();
        return true;
    }

    /// <summary>
    /// 花钱 —— **全额扣得动才扣, 扣不动一分不动**。
    ///
    /// ## 为什么不能用 <see cref="AddCurrency"/> 传负数
    ///
    /// 那一句是 <c>MAX(amount + $delta, 0)</c> —— 扣超了自动停在 0 而不是失败。
    /// 于是"先查余额, 再扣钱"这个写法有个真实的漏洞:
    ///
    ///     查余额  (够)
    ///     ...     &lt;- 这中间余额被别处扣走了
    ///     扣钱    (扣到 0 就停, **不报错**)
    ///
    /// 结果是玩家照样拿到东西, 只付了手上剩的那点。而"别处"是真实存在的:
    /// 局末结算在战斗服线程上写货币 (<c>RewardLedger.Grant</c>), 大厅这边
    /// 同时可以买东西、合成、装改装图。
    ///
    /// 这个方法把"查"和"扣"合成一条 SQL:
    ///
    ///     UPDATE ... SET amount = amount - $n WHERE ... AND amount &gt;= $n
    ///
    /// 条件不成立就是 0 行受影响, 一分钱不动。
    ///
    /// ## 多种货币一起扣
    ///
    /// 包在一个事务里 —— 银币扣成了但角色点不够的话整笔回滚。商店一单同时
    /// 收两种货币, 少了这一条就会出现"钱扣了货没发"。
    /// </summary>
    /// <param name="costs">要扣的几种货币和数额。数额 &lt;= 0 的跳过。</param>
    /// <returns>全扣成了返回 true; 任何一种不够都返回 false 且什么都没动。</returns>
    public bool TrySpend(ulong userId, IReadOnlyList<(CurrencyKind Kind, int Amount)> costs)
    {
        if (costs == null || costs.Count == 0)
        {
            return true;
        }

        using SqliteConnection conn = Open();
        using SqliteTransaction tx = conn.BeginTransaction();

        foreach ((CurrencyKind kind, int amount) in costs)
        {
            if (amount <= 0)
            {
                continue;
            }

            using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            // 行不存在时也是 0 行受影响 —— 正是我们要的 (没有这种货币 = 没有钱)。
            cmd.CommandText = """
                UPDATE currencies
                   SET amount = amount - $amount
                 WHERE user_id = $id AND kind = $kind AND amount >= $amount
                """;
            cmd.Parameters.AddWithValue("$id", (long)userId);
            cmd.Parameters.AddWithValue("$kind", (int)kind);
            cmd.Parameters.AddWithValue("$amount", amount);

            if (cmd.ExecuteNonQuery() == 0)
            {
                tx.Rollback();
                return false;
            }
        }

        tx.Commit();
        return true;
    }

    /// <summary>一种货币的简写。</summary>
    public bool TrySpend(ulong userId, CurrencyKind kind, int amount) =>
        TrySpend(userId, new[] { (kind, amount) });

    /// <summary>
    /// 退钱 —— 扣完之后那一步失败时用。
    ///
    /// 和 <see cref="TrySpend"/> 不对称是故意的: 加钱不会失败, 不需要条件。
    /// </summary>
    public void Refund(ulong userId, CurrencyKind kind, int amount)
    {
        if (amount > 0)
        {
            AddCurrency(userId, kind, amount);
        }
    }

    /// <summary>货币种类的列编码 —— 和 <c>Schema.sql</c> 里 currencies.kind 的注释一致。</summary>
    public enum CurrencyKind
    {
        Gold = 0,
        Silver = 1,
        CharacterPoints = 2,
        ResearchPoints = 3,
    }

    /// <summary>
    /// 给账号加 (或扣) 货币, 返回加完之后的余额。
    ///
    /// <paramref name="delta"/> 可以是负数 —— 购买就是这么扣的。**扣不出负数**:
    /// SQL 里用 <c>MAX(..., 0)</c> 兜底。真正的"余额够不够"判断在调用方
    /// (<c>ShopHandlers</c>), 那里要在扣之前就拒绝, 而不是扣成 0 才发现。
    ///
    /// 账号还没有这一行时按 0 起算 (<c>INSERT ... ON CONFLICT</c>)。
    /// </summary>
    public int AddCurrency(ulong userId, CurrencyKind kind, int delta)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();

        cmd.CommandText = """
            INSERT INTO currencies (user_id, kind, amount)
            VALUES ($id, $kind, MAX($delta, 0))
            ON CONFLICT(user_id, kind) DO UPDATE
                SET amount = MAX(amount + $delta, 0)
            RETURNING amount
            """;
        cmd.Parameters.AddWithValue("$id", (long)userId);
        cmd.Parameters.AddWithValue("$kind", (int)kind);
        cmd.Parameters.AddWithValue("$delta", delta);

        object result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
    }

    /// <summary>
    /// 把一件武器存进背包。
    ///
    /// blob 格式和发初始装备那条路完全一致 (<c>CraftingCatalog.ToBlob</c>) ——
    /// 商店买来的和开局送的在库里没有区别。
    /// </summary>
    /// <summary>
    /// 写回保底计数器 (<c>AccountCounter</c> 那一排)。
    ///
    /// ## 它是干什么的
    ///
    /// 原厂用它做"连续 N 局没掉就必掉": 每一档掉落各记一个"连续多少局没出",
    /// 掉出来就归零, 没掉就加一。曲线上的 <c>GuaranteedLootPoints[计数]</c>
    /// 是额外加到概率上的补偿。
    ///
    /// 数是 <c>RewardManager.CalculateCounters</c> 自己算的, 我们只负责存进
    /// <c>counter_data</c> 再原样喂回去。列早就有了 (登录时还会下发给客户端,
    /// 见 <c>AccountMapper</c>), 只是一直没人写。
    ///
    /// ## byte 会绕回去
    ///
    /// 原厂那句是 <c>counters[i]++</c>, 没有封顶 —— 连续 256 局没掉的话计数
    /// 会绕回 0, 保底跟着重来。对我们配了保底的那几档无所谓 (它们的补偿在
    /// 几十局内就拉满了, 到不了 255), 但配新档时要留意。
    /// </summary>
    /// <summary>上一次拿到拾荒每日首胜的时刻 (UTC); 从没拿过返回 null。</summary>
    public DateTime? LastScavengerFirstWinUtc(ulong userId)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_win_utc FROM scavenger_first_wins WHERE user_id = $id";
        cmd.Parameters.AddWithValue("$id", (long)userId);
        return cmd.ExecuteScalar() is long ticks ? new DateTime(ticks, DateTimeKind.Utc) : null;
    }

    /// <summary>记下这一刻拿了拾荒每日首胜。</summary>
    public void MarkScavengerFirstWin(ulong userId, DateTime nowUtc)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO scavenger_first_wins (user_id, last_win_utc) VALUES ($id, $now)
            ON CONFLICT(user_id) DO UPDATE SET last_win_utc = excluded.last_win_utc
            """;
        cmd.Parameters.AddWithValue("$id", (long)userId);
        cmd.Parameters.AddWithValue("$now", nowUtc.Ticks);
        cmd.ExecuteNonQuery();
    }

    public void SaveCounters(ulong userId, byte[] counters)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();

        cmd.CommandText = "UPDATE accounts SET counter_data = $blob WHERE user_id = $id";
        cmd.Parameters.AddWithValue("$blob", counters ?? Array.Empty<byte>());
        cmd.Parameters.AddWithValue("$id", (long)userId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 按 GUID 找账号里的一件武器。找不到返回 null。
    ///
    /// GUID 是客户端唯一能指认某一件武器的东西 —— 装改装图、拆解、装备都靠它。
    /// 库里存的是整块 blob, 所以只能全读出来逐个比 —— 一个账号几十件武器,
    /// 为此建一列索引不划算。
    /// </summary>
    public (long InstanceId, ConductorCrafting.Unique Weapon)? FindUnique(
        ulong userId, byte[] guid)
    {
        if (guid == null || guid.Length == 0)
        {
            return null;
        }

        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT instance_id, blob FROM inventory_uniques WHERE user_id = $id";
        cmd.Parameters.AddWithValue("$id", (long)userId);

        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            ConductorCrafting.Unique weapon = CraftingCatalog.FromBlob((byte[])r[1]);

            // 用原厂的 GuidEquals 而不是 SequenceEqual —— 它对 null 和长度不等
            // 都有定义, 而我们这边两种都可能出现 (老数据 / 客户端乱发)。
            if (weapon.GuidEquals(new ConductorCrafting.Unique { Guid = guid }))
            {
                return (r.GetInt64(0), weapon);
            }
        }

        return null;
    }

    /// <summary>
    /// 整件覆盖一件武器 —— 装了改装图、掉了耐久之后写回。
    ///
    /// 按 <paramref name="instanceId"/> 定位而不是按 GUID: 找的时候已经拿到了
    /// 主键, 再按 blob 内容找一遍既慢又多一次"万一 GUID 撞了"的风险。
    /// </summary>
    public void UpdateUnique(long instanceId, ConductorCrafting.Unique weapon)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();

        cmd.CommandText = "UPDATE inventory_uniques SET blob = $blob WHERE instance_id = $iid";
        cmd.Parameters.AddWithValue("$blob", CraftingCatalog.ToBlob(weapon));
        cmd.Parameters.AddWithValue("$iid", instanceId);
        cmd.ExecuteNonQuery();
    }

    public void AddUnique(ulong userId, ConductorCrafting.Unique weapon)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand insert = conn.CreateCommand();

        insert.CommandText = "INSERT INTO inventory_uniques (user_id, blob) VALUES ($id, $blob)";
        insert.Parameters.AddWithValue("$id", (long)userId);
        insert.Parameters.AddWithValue("$blob", CraftingCatalog.ToBlob(weapon));
        insert.ExecuteNonQuery();
    }

    /// <summary>账号的名字和 Steam ID (控制台的玩家列表用); 没有这个账号返回 null。</summary>
    public (string Name, long? SteamId)? Identity(ulong userId)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT profile_name, steam_id FROM accounts WHERE user_id = $id";
        cmd.Parameters.AddWithValue("$id", (long)userId);

        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read())
        {
            return null;
        }

        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1));
    }

    /// <summary>
    /// 把整个库在线备份到 <paramref name="destinationPath"/> (SQLite 的 backup API ——
    /// 开着服、有人在写也能拿到一份一致的库, 不像直接拷文件可能拷到写了一半的)。
    /// </summary>
    public void BackupTo(string destinationPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using SqliteConnection source = Open();
        using SqliteConnection target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        target.Open();
        source.BackupDatabase(target);
    }

    public Account Load(ulong userId)
    {
        using SqliteConnection conn = Open();

        Account account = new Account { UserId = userId };

        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT steam_id, profile_name, created_utc, first_login, language, vanity_icon,
                       story_map_xp, story_map_version, story_map_data, counter_data,
                       crossroad_progress, unbound_xp, regular_boost, premium_boost,
                       unlocked_dlc, is_expo_user, ban_reason, banned_days
                FROM accounts WHERE user_id = $id
                """;
            cmd.Parameters.AddWithValue("$id", (long)userId);

            using SqliteDataReader r = cmd.ExecuteReader();
            if (!r.Read())
            {
                throw new InvalidOperationException($"账号 {userId} 不存在");
            }

            account.SteamId = r.IsDBNull(0) ? null : r.GetInt64(0);
            account.ProfileName = r.GetString(1);
            account.CreatedUtcTicks = r.GetInt64(2);
            account.FirstLogin = r.GetInt64(3) != 0;
            account.Language = r.GetInt32(4);
            account.VanityIcon = (byte)r.GetInt32(5);
            account.StoryMapXp = (uint)r.GetInt64(6);
            account.StoryMapVersion = (byte)r.GetInt32(7);
            account.StoryMapData = r.IsDBNull(8) ? Array.Empty<byte>() : (byte[])r[8];
            account.CounterData = r.IsDBNull(9) ? Array.Empty<byte>() : (byte[])r[9];
            account.CrossroadProgress = (byte)r.GetInt32(10);
            account.UnboundXp = (uint)r.GetInt64(11);
            account.RegularBoost = r.GetInt64(12);
            account.PremiumBoost = r.GetInt64(13);
            account.UnlockedDlc = r.GetInt32(14);
            account.IsExpoUser = r.GetInt64(15) != 0;
            account.BanReason = r.GetInt32(16);
            account.BannedDays = (ushort)r.GetInt32(17);
        }

        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT kind, amount FROM currencies WHERE user_id = $id";
            cmd.Parameters.AddWithValue("$id", (long)userId);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                int amount = r.GetInt32(1);
                switch (r.GetInt32(0))
                {
                    case 0: account.Gold = amount; break;
                    case 1: account.Silver = amount; break;
                    case 2: account.CharacterPoints = amount; break;
                    case 3: account.ResearchPoints = amount; break;
                }
            }
        }

        return account;
    }

    /// <summary>登录成功后不再是首次登录。</summary>
    public void ClearFirstLogin(ulong userId)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE accounts SET first_login = 0 WHERE user_id = $id";
        cmd.Parameters.AddWithValue("$id", (long)userId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>签发一个会话, 返回 (authTicket, sessionId)。两者对客户端都是不透明字节。</summary>
    public (byte[] AuthTicket, byte[] SessionId) IssueSession(ulong userId, string remoteIp, TimeSpan lifetime)
    {
        byte[] authTicket = new byte[32];
        byte[] sessionId = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(authTicket);
        System.Security.Cryptography.RandomNumberGenerator.Fill(sessionId);

        DateTime now = DateTime.UtcNow;

        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (session_id, user_id, auth_ticket, issued_utc, expires_utc, remote_ip)
            VALUES ($sid, $uid, $ticket, $issued, $expires, $ip)
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$uid", (long)userId);
        cmd.Parameters.AddWithValue("$ticket", authTicket);
        cmd.Parameters.AddWithValue("$issued", now.Ticks);
        cmd.Parameters.AddWithValue("$expires", now.Add(lifetime).Ticks);
        cmd.Parameters.AddWithValue("$ip", (object)remoteIp ?? DBNull.Value);
        cmd.ExecuteNonQuery();

        return (authTicket, sessionId);
    }

    /// <summary>重连路径 AuthRequest(1) 用: 凭我们自己签发的 ticket 找回账号。</summary>
    /// <summary>
    /// 删掉过期超过 <paramref name="keepExpiredFor"/> 的会话, 返回删了几条。
    /// 每次登录都插一行, 以前从不清理 (2026-09-17 审校)。过期后留一段再删, 查日志时还对得上。
    /// </summary>
    public int PurgeExpiredSessions(DateTime nowUtc, TimeSpan keepExpiredFor)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE expires_utc < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", (nowUtc - keepExpiredFor).Ticks);
        return cmd.ExecuteNonQuery();
    }

    public ulong? ResolveByAuthTicket(byte[] authTicket)
    {
        if (authTicket == null || authTicket.Length == 0)
        {
            return null;
        }

        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT user_id FROM sessions WHERE auth_ticket = $t AND expires_utc > $now";
        cmd.Parameters.AddWithValue("$t", authTicket);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);

        object result = cmd.ExecuteScalar();
        return result == null ? null : (ulong)Convert.ToInt64(result);
    }

    public IReadOnlyList<byte[]> LoadUniqueBlobs(ulong userId) => LoadBlobs("inventory_uniques", userId);

    public IReadOnlyList<byte[]> LoadGadgetBlobs(ulong userId) => LoadBlobs("inventory_gadgets", userId);

    private IReadOnlyList<byte[]> LoadBlobs(string table, ulong userId)
    {
        List<byte[]> blobs = new List<byte[]>();
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        // 表名不来自外部输入, 是本类里的常量, 不存在注入面
        cmd.CommandText = $"SELECT blob FROM {table} WHERE user_id = $id ORDER BY instance_id";
        cmd.Parameters.AddWithValue("$id", (long)userId);
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            blobs.Add((byte[])r[0]);
        }
        return blobs;
    }
}
