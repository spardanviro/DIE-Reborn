using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace DIE.Server.Data;

/// <summary>
/// 把另一个账号库 (本机开发服的) 里的账号**按 Steam ID 合并**进这个库 (云上的), 不碰别人。
///
/// ## 为什么不能整库覆盖
///
/// 服务器包的 <c>-IncludeDatabase</c> 是拿本机的 die_reborn.db 整个替换云上那份 —— 云上朋友们的账号会一起没。
/// 这里只动"源库里有 Steam ID 的那几个人":
///
///   云上已经有这个 Steam ID   用源库的进度**覆盖**这个人 (账号字段 + 货币 + 背包 + 角色); user_id 不变
///   云上没有                 新建一个账号, 进度照搬
///   源库里 steam_id 为空的     跳过 (早期测试账号, 认不出是谁)
///
/// 不搬的: 会话 / 匹配票 / 对局记录 / 离局惩罚 —— 那些是"在哪台服上发生过什么", 不是账号进度。
///
/// ## 列按两边都有的来
///
/// 两个库可能差一两个版本的表结构 (<c>Schema.sql</c> 只增不删列)。每张表取两边列名的交集搬,
/// 目标库先跑一遍 <see cref="AccountStore.ApplySchema"/> 补齐新列。
///
/// **必须先停服务端** —— 服务端在内存里缓存着账号, 边跑边改会被它写回去。
/// </summary>
public static class AccountImporter
{
    /// <summary>挂在账号下、按 user_id 整体替换的表。唯一物品表按 instance_id 顺序插回 (背包顺序靠它)。</summary>
    private static readonly string[] PerUserTables =
    {
        "currencies",
        "characters",
        "inventory_stackables",
        "inventory_uniques",
        "inventory_gadgets",
    };

    /// <summary>每个账号的处理结果。</summary>
    public sealed record Result(ulong SteamId, string Name, long SourceUserId, long TargetUserId, bool Created,
                                IReadOnlyDictionary<string, int> Rows);

    /// <param name="targetPath">要合并进去的库 (云上的)。</param>
    /// <param name="sourcePath">带着进度的库 (本机的)。</param>
    /// <param name="dryRun">只算不写 (事务回滚)。</param>
    public static List<Result> Import(string targetPath, string sourcePath, bool dryRun)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("找不到要导入的账号库", sourcePath);
        }

        if (Path.GetFullPath(targetPath).Equals(Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("源库和目标库是同一个文件");
        }

        List<Result> results = new List<Result>();

        using SqliteConnection db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = targetPath,
            Mode = SqliteOpenMode.ReadWriteCreate,

            // 不进连接池 —— 这条连接上 ATTACH 了源库, 回池再被别人拿到就是 "database src is already in use"
            Pooling = false,
        }.ToString());
        db.Open();

        Exec(db, "PRAGMA foreign_keys = ON");
        using (SqliteCommand attach = db.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE $path AS src";
            attach.Parameters.AddWithValue("$path", sourcePath);
            attach.ExecuteNonQuery();
        }

        using SqliteTransaction tx = db.BeginTransaction();

        List<string> accountColumns = SharedColumns(db, tx, "accounts")
            .Where(c => c != "user_id")
            .ToList();

        List<(long UserId, long SteamId, string Name)> sources = new List<(long, long, string)>();
        using (SqliteCommand list = Command(db, tx,
                   "SELECT user_id, steam_id, profile_name FROM src.accounts WHERE steam_id IS NOT NULL ORDER BY user_id"))
        using (SqliteDataReader reader = list.ExecuteReader())
        {
            while (reader.Read())
            {
                sources.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)));
            }
        }

        foreach ((long sourceId, long steamId, string name) in sources)
        {
            long? existing = Scalar(db, tx, "SELECT user_id FROM main.accounts WHERE steam_id = $steam",
                ("$steam", steamId));

            long targetId;
            bool created = existing == null;
            string columns = string.Join(", ", accountColumns);

            if (created)
            {
                Exec(db, tx, $"INSERT INTO main.accounts ({columns}) SELECT {columns} FROM src.accounts WHERE user_id = $src",
                    ("$src", sourceId));
                targetId = Scalar(db, tx, "SELECT last_insert_rowid()") ?? throw new InvalidOperationException("新建账号失败");
            }
            else
            {
                targetId = existing.Value;
                string assignments = string.Join(", ", accountColumns
                    .Where(c => c != "steam_id")
                    .Select(c => $"{c} = (SELECT {c} FROM src.accounts WHERE user_id = $src)"));
                Exec(db, tx, $"UPDATE main.accounts SET {assignments} WHERE user_id = $dst",
                    ("$src", sourceId), ("$dst", targetId));
            }

            Dictionary<string, int> rows = new Dictionary<string, int>();

            foreach (string table in PerUserTables)
            {
                if (!TableExists(db, tx, "main", table) || !TableExists(db, tx, "src", table))
                {
                    continue;
                }

                List<string> shared = SharedColumns(db, tx, table)
                    .Where(c => c != "user_id" && c != "instance_id")
                    .ToList();

                Exec(db, tx, $"DELETE FROM main.{table} WHERE user_id = $dst", ("$dst", targetId));

                string cols = string.Join(", ", shared);
                string order = SharedColumns(db, tx, table).Contains("instance_id") ? " ORDER BY instance_id" : string.Empty;
                rows[table] = Exec(db, tx,
                    $"INSERT INTO main.{table} (user_id{(cols.Length > 0 ? ", " + cols : "")}) "
                    + $"SELECT $dst{(cols.Length > 0 ? ", " + cols : "")} FROM src.{table} WHERE user_id = $src{order}",
                    ("$src", sourceId), ("$dst", targetId));
            }

            results.Add(new Result((ulong)steamId, name, sourceId, targetId, created, rows));
        }

        if (dryRun)
        {
            tx.Rollback();
        }
        else
        {
            tx.Commit();
        }

        return results;
    }

    private static bool TableExists(SqliteConnection db, SqliteTransaction tx, string schema, string table) =>
        Scalar(db, tx, $"SELECT COUNT(*) FROM {schema}.sqlite_master WHERE type = 'table' AND name = $name",
            ("$name", table)) > 0;

    /// <summary>两边都有的列 (按目标库的顺序)。</summary>
    private static List<string> SharedColumns(SqliteConnection db, SqliteTransaction tx, string table)
    {
        List<string> Columns(string schema)
        {
            List<string> names = new List<string>();
            using SqliteCommand cmd = Command(db, tx, $"PRAGMA {schema}.table_info({table})");
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                names.Add(reader.GetString(1));
            }

            return names;
        }

        HashSet<string> source = new HashSet<string>(Columns("src"), StringComparer.OrdinalIgnoreCase);
        return Columns("main").Where(source.Contains).ToList();
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction tx, string sql,
                                         params (string Name, object Value)[] parameters)
    {
        SqliteCommand cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach ((string n, object v) in parameters)
        {
            cmd.Parameters.AddWithValue(n, v);
        }

        return cmd;
    }

    private static int Exec(SqliteConnection db, SqliteTransaction tx, string sql,
                            params (string Name, object Value)[] parameters)
    {
        using SqliteCommand cmd = Command(db, tx, sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long? Scalar(SqliteConnection db, SqliteTransaction tx, string sql,
                                params (string Name, object Value)[] parameters)
    {
        using SqliteCommand cmd = Command(db, tx, sql, parameters);
        object value = cmd.ExecuteScalar();
        return value == null || value is DBNull ? null : Convert.ToInt64(value);
    }
}
