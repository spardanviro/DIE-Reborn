-- DIE_Reborn 账号库
--
-- 启动时幂等执行 (全部 IF NOT EXISTS)。刻意从第一天就按多用户设计:
-- Phase 1 只有一个固定账号在跑, 但表结构不做单用户假设, 等 Phase 4 用
-- BepInEx patch 让客户端上报 SteamID 时无需迁移。
--
-- 为什么不用 EF Core: schema 小且手写, migration 机制纯属开销。

PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS accounts (
    user_id            INTEGER PRIMARY KEY,   -- 下发给客户端的 LoginDataMessage.UserID (ulong)
    steam_id           INTEGER UNIQUE,        -- 由客户端插件在 LoginRequest 的 name 字段里
                                              -- 以 "<steamid>|<昵称>" 形式自称 (ParseTaggedName),
                                              -- 再由 Steam 核验会话票据确认 (LoginHandlers.ResolveIdentity)。
                                              -- 共享账号 (没装插件 / 核验被拒) 这一列为 NULL。
    profile_name       TEXT    NOT NULL DEFAULT 'Survivor',
    created_utc        INTEGER NOT NULL,      -- DateTime.Ticks
    last_login_utc     INTEGER,
    first_login        INTEGER NOT NULL DEFAULT 1,
    language           INTEGER NOT NULL DEFAULT 0,
    vanity_icon        INTEGER NOT NULL DEFAULT 0,
    story_map_xp       INTEGER NOT NULL DEFAULT 0,
    story_map_version  INTEGER NOT NULL DEFAULT 0,
    story_map_data     BLOB,
    counter_data       BLOB,
    crossroad_progress INTEGER NOT NULL DEFAULT 0,
    unbound_xp         INTEGER NOT NULL DEFAULT 0,
    regular_boost      INTEGER NOT NULL DEFAULT 0,
    premium_boost      INTEGER NOT NULL DEFAULT 0,
    unlocked_dlc       INTEGER NOT NULL DEFAULT 0,
    is_expo_user       INTEGER NOT NULL DEFAULT 0,
    ban_reason         INTEGER NOT NULL DEFAULT 0,
    banned_days        INTEGER NOT NULL DEFAULT 0
);

-- 货币按种类一行, 对应 ConductorShop.CurrencyCollection 的四个字段
CREATE TABLE IF NOT EXISTS currencies (
    user_id INTEGER NOT NULL REFERENCES accounts(user_id) ON DELETE CASCADE,
    kind    INTEGER NOT NULL,   -- 0=Gold 1=Silver 2=CharacterPoints 3=ResearchPoints
    amount  INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (user_id, kind)
);

-- 可堆叠物: 蓝图 / 零件 / 消耗品 / 宝箱 / 改装图。
--
-- 和 uniques/gadgets 不同, 这些**不是实例**而是计数 —— 同一个 (id, type)
-- 只有一行, 买/开箱是加数, 合成是减数。所以展开成列而不是存 blob:
-- 合成时要按 id 查"我有几个", blob 做不到。
--
-- type 是 ConductorCrafting.StackableType (1=Consumable 2=Schematic 3=Part
-- 7=Design 10=Lockbox ...)。amount 恒 >= 0, 减到 0 的行会被删掉。
--
-- **主键必须带 type。** 只按 (user_id, item_id) 的话, 同一个数字 ID 在不同
-- 类型下会互相顶掉 —— 零件 5 号和消耗品 5 号是两样东西, 而 ID 空间各排各的。
CREATE TABLE IF NOT EXISTS inventory_stackables (
    user_id INTEGER NOT NULL REFERENCES accounts(user_id) ON DELETE CASCADE,
    item_id INTEGER NOT NULL,
    type    INTEGER NOT NULL DEFAULT 0,
    amount  INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (user_id, item_id, type)
);

-- Unique / Gadget 存序列化 blob 而非展开成列。
-- 它们是深层结构 (design 列表、词条、属性), Phase 1 从不检查内容,
-- blob 存储省掉了与 ConductorCrafting 内部结构保持同步的负担。
-- 将来需要按内容查询再 normalize。
CREATE TABLE IF NOT EXISTS inventory_uniques (
    instance_id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id     INTEGER NOT NULL REFERENCES accounts(user_id) ON DELETE CASCADE,
    blob        BLOB    NOT NULL
);

CREATE TABLE IF NOT EXISTS inventory_gadgets (
    instance_id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id     INTEGER NOT NULL REFERENCES accounts(user_id) ON DELETE CASCADE,
    blob        BLOB    NOT NULL
);

CREATE TABLE IF NOT EXISTS characters (
    user_id      INTEGER NOT NULL REFERENCES accounts(user_id) ON DELETE CASCADE,
    character_id INTEGER NOT NULL,
    level        INTEGER NOT NULL DEFAULT 1,
    xp           INTEGER NOT NULL DEFAULT 0,
    unlocked     INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY (user_id, character_id)
);

-- 服务端签发的会话。AuthTicket 对客户端完全不透明 —— 它只缓存并在重连时
-- 通过 AuthRequest(1) 回传, 从不解析。客户端自己会在 3 小时后丢弃并重新走
-- SteamAuthRequest(25)。
CREATE TABLE IF NOT EXISTS sessions (
    session_id  BLOB    PRIMARY KEY,
    user_id     INTEGER NOT NULL REFERENCES accounts(user_id) ON DELETE CASCADE,
    auth_ticket BLOB    NOT NULL UNIQUE,
    issued_utc  INTEGER NOT NULL,
    expires_utc INTEGER NOT NULL,
    remote_ip   TEXT
);

CREATE INDEX IF NOT EXISTS ix_sessions_auth_ticket ON sessions(auth_ticket);

CREATE TABLE IF NOT EXISTS server_meta (
    k TEXT PRIMARY KEY,
    v TEXT NOT NULL
);

-- ---------------------------------------------------------------------------
-- Phase 2: 匹配
-- ---------------------------------------------------------------------------

-- 匹配票据。
--
-- 存在的理由是 1555 和 2555 是**两条互不相干的 TCP 会话**, 客户端不会在它们之间
-- 传递身份。流程是: 客户端先在 1555 上拿票据 (GetMatchmakingTicketRequest),
-- 再把票据原样带到 2555 (SoloServerCreateRequest)。所以票据是唯一的关联凭据 ——
-- 签发时记下 user_id, 校验时取回。
--
-- 内容对客户端完全不透明 (byte[]), 所以用随机字节即可, 不需要签名。
-- 等做公开服时可以换成 HMAC 以防伪造, 见 PHASE_2_3_PLAN.md。
CREATE TABLE IF NOT EXISTS matchmaking_tickets (
    ticket      BLOB    PRIMARY KEY,
    user_id     INTEGER NOT NULL REFERENCES accounts(user_id) ON DELETE CASCADE,
    issued_utc  INTEGER NOT NULL,   -- DateTime.Ticks
    expires_utc INTEGER NOT NULL,
    consumed    INTEGER NOT NULL DEFAULT 0,
    queue_data  BLOB                -- 原样保存客户端上报的 PlayerQueueData, 供 Phase 3 取用
);

CREATE INDEX IF NOT EXISTS ix_tickets_user ON matchmaking_tickets(user_id);

-- 一局对局。Phase 2 只填到"已分配地址"为止, 真正的战斗服是 Phase 3。
CREATE TABLE IF NOT EXISTS matches (
    match_guid  BLOB    PRIMARY KEY,   -- 16 字节, 客户端用它做重连关联
    host_ip     TEXT    NOT NULL,
    port        INTEGER NOT NULL,
    queue_type  INTEGER NOT NULL,
    map_index   INTEGER NOT NULL,
    state       INTEGER NOT NULL DEFAULT 0,   -- 0=已分配 1=运行中 2=已结束
    created_utc INTEGER NOT NULL,
    ended_utc   INTEGER
);

CREATE TABLE IF NOT EXISTS match_players (
    match_guid BLOB    NOT NULL REFERENCES matches(match_guid) ON DELETE CASCADE,
    user_id    INTEGER NOT NULL REFERENCES accounts(user_id) ON DELETE CASCADE,
    joined_utc INTEGER NOT NULL,
    PRIMARY KEY (match_guid, user_id)
);

-- GetActiveMatchRequest 每 2 秒按 user_id 查一次"有没有进行中的对局", 需要这个索引
CREATE INDEX IF NOT EXISTS ix_match_players_user ON match_players(user_id);

-- 离局惩罚。中途放弃一局 (重连弹窗上点"离开") 之后一段时间内不能排队。
--
-- 客户端本来就有这套 UI: 服务端回 JoinQueueResult.PartyMemberIsTimedOut 并带上
-- MatchmakingTimeOutData(user_id, 到什么时候), 客户端弹"离局惩罚, 还要等 X 分钟"
-- (Matchmaker.DisplayUserTimedOutPopup)。惩罚时长是我们定的, 见 LeavePunishmentStore。
--
-- strikes 只在 StrikeMemory 之内累计, 隔得久了重新从第一档算。
CREATE TABLE IF NOT EXISTS leave_punishments (
    user_id   INTEGER PRIMARY KEY REFERENCES accounts(user_id) ON DELETE CASCADE,
    until_utc INTEGER NOT NULL,   -- DateTime.Ticks, 到这个时刻为止不能排队
    strikes   INTEGER NOT NULL,   -- 第几次 (决定时长)
    last_utc  INTEGER NOT NULL    -- 上一次犯规的时刻, 用于判断 strikes 要不要归零
);

-- 拾荒每日首胜。上一次拿到首胜奖励的时刻; 跨过一天 (北京时间零点, 见 RewardLedger.IsNewDay) 就又能拿。
-- 原厂结算引擎的开关叫 FirstScavengerWinBonusAvailable, "一天"怎么算随服务端丢了, 我们定的。
CREATE TABLE IF NOT EXISTS scavenger_first_wins (
    user_id      INTEGER PRIMARY KEY REFERENCES accounts(user_id) ON DELETE CASCADE,
    last_win_utc INTEGER NOT NULL   -- DateTime.Ticks
);
