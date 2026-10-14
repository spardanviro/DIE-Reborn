using System;
using System.Collections.Generic;
using System.IO;
using ConductorCrafting;
using GameProtocol;
using Serilog;
using StunMessage;

namespace DIE.Server.Data;

/// <summary>
/// 服务端侧的物品图鉴 —— 把 GameCraftingData.xml 灌进原厂 <see cref="CraftingManager"/>,
/// 之后就能按 schematic ID 造出客户端认可的 <see cref="Unique"/>。
///
/// 这份数据不在磁盘上, 是从 Crib_Data\resources.assets 里提取的
/// (客户端走 Resources.Load("GameCraftingData"))。提取脚本:
/// tools\extract_crafting_data.py, 产物落在 data\GameCraftingData.xml。
///
/// CraftingManager 是客户端与服务端共用的同一份代码, 所以它认可的东西客户端必然认可。
/// </summary>
public sealed class CraftingCatalog
{
    /// <summary>
    /// 新账号的初始装备。这两个 ID 是 CraftingManager 静态构造里硬编码的
    /// DEFAULT_MELEE_ID / DEFAULT_RANGE_ID, 也正是 Matchmaker 开教程时
    /// 会去背包里找的那两把 (Matchmaker.cs:119 / :127)。
    ///   1005 = Paddle       (Heavy,   tier 1)
    ///   1009 = ScrapShotgun (Shotgun, tier 1)
    /// </summary>
    public static readonly ushort[] StarterWeaponIds = { 1005, 1009 };

    /// <summary>客户端造展示用 Unique 时用的耐久值 (InventoryManager.cs:196)。</summary>
    private const int FullDurability = 100;

    public bool IsLoaded { get; private set; }

    public void Load(string craftingDataPath)
    {
        if (!File.Exists(craftingDataPath))
        {
            Log.Warning(
                "找不到 {Path} —— 初始装备将无法发放。跑一下 tools\\extract_crafting_data.py 提取它。",
                craftingDataPath);
            return;
        }

        using FileStream stream = File.OpenRead(craftingDataPath);
        CraftingManager.LoadData(stream);
        IsLoaded = true;

        Log.Information("物品图鉴已加载: {Path}", craftingDataPath);
    }

    /// <summary>
    /// 按 schematic 造一件属于某个账号的武器实例。
    ///
    /// 字段取值照抄客户端 InventoryManager.cs:196 的做法:
    /// Guid 任意 16 字节 (每件实例唯一)、IsInInventory=true、Durability=100、
    /// EP=0、Slots 为空 (没有镶嵌任何 design)。
    /// </summary>
    public Unique? CreateWeapon(ushort schematicId, ulong userId)
    {
        ItemSchematic schematic = CraftingManager.GetWeaponSchematic(schematicId);
        if (schematic == null)
        {
            Log.Warning("图鉴里没有 schematic {SchematicId}, 跳过", schematicId);
            return null;
        }

        return new Unique(
            guid: Guid.NewGuid().ToByteArray(),
            schematicID: schematicId,
            userID: userId,
            isInInventory: true,
            durability: FullDurability,
            ep: 0,
            slots: Array.Empty<byte>());
    }

    public IReadOnlyList<Unique> CreateStarterWeapons(ulong userId)
    {
        List<Unique> weapons = new List<Unique>();
        if (!IsLoaded)
        {
            return weapons;
        }

        foreach (ushort id in StarterWeaponIds)
        {
            Unique? weapon = CreateWeapon(id, userId);
            if (weapon.HasValue)
            {
                weapons.Add(weapon.Value);
            }
        }

        return weapons;
    }

    /// <summary>
    /// 用协议自己的序列化器把 Unique 存成 blob。
    ///
    /// 好处是存进库里的字节和发给客户端的字节走的是同一套编码, 不存在
    /// "数据库模型" 与 "线上模型" 漂移的可能; 而且 Unique 里的 Slots 是可变长的,
    /// 展开成关系表并不划算 (Phase 1 也从不按内容查询)。
    /// </summary>
    /// <summary>
    /// 把 <c>Unique.Slots</c> 那段字节还原成槽位表 (镶嵌了哪些改装图)。
    ///
    /// 空的 (新造的武器) 返回空表而不是 null —— 原厂 <c>Deserialize</c> 失败时
    /// 给的是 null, 而客户端 <c>WeaponItemInfo.SetupSlots</c> 遇到 null 会退成空表,
    /// 我们照做。编解码一个字节都不手写, 用原厂的 <c>Unique.Serialize/Deserialize</c>。
    /// </summary>
    public static List<UniqueSlot> DecodeSlots(byte[] blob)
    {
        if (blob == null || blob.Length == 0)
        {
            return new List<UniqueSlot>();
        }

        Message buffer = Message.Create(blob.Length, blob);

        return Unique.Deserialize(ref buffer, out List<UniqueSlot> slots) && slots != null
            ? slots
            : new List<UniqueSlot>();
    }

    /// <summary>槽位表写回字节。编码由原厂的 Serialize 负责。</summary>
    public static byte[] EncodeSlots(List<UniqueSlot> slots)
    {
        Message buffer = Message.Create(2 + slots.Count * 3);
        Unique.Serialize(ref buffer, ref slots);
        buffer.Position = 0L;
        return buffer.ToBytes();
    }
    public static byte[] ToBlob(Unique unique)
    {
        Message buffer = Message.Create(256);
        MessageSerialization.Serialize(ref unique, ref buffer);
        buffer.Position = 0L;
        return buffer.ToBytes();
    }

    public static Unique FromBlob(byte[] blob)
    {
        Message buffer = Message.Create(256, blob);
        Unique unique = default;
        if (!MessageSerialization.Deserialize(ref unique, ref buffer))
        {
            throw new InvalidDataException("库里的 Unique blob 无法反序列化 —— 数据已损坏");
        }
        return unique;
    }

    /// <summary>
    /// 按 schematic 造一件属于某个账号的**装置**。
    ///
    /// <c>Gadget.GenerateTrinket</c> 是原厂工厂 (Gadget.cs:17): 它按图鉴上的
    /// 词条槽数掷出一份 <c>TrinketStats</c> 再打包成字节。装置的属性是**随机
    /// 生成**的 (武器不是), 所以这一步不能自己拼 —— 拼出来的字节客户端解不了。
    /// </summary>
    public Gadget? CreateGadget(ushort gadgetId, ulong userId)
    {
        StructuredGadget schematic = CraftingManager.GetTrinket(gadgetId);
        if (schematic == null)
        {
            Log.Warning("图鉴里没有装置 {GadgetId}, 跳过", gadgetId);
            return null;
        }

        return Gadget.GenerateTrinket(userId, schematic);
    }

    /// <summary>装置的 blob 编码 —— 和 Unique 同一个道理, 见 <see cref="ToBlob(Unique)"/>。</summary>
    public static byte[] ToBlob(Gadget gadget)
    {
        Message buffer = Message.Create(256);
        MessageSerialization.Serialize(ref gadget, ref buffer);
        buffer.Position = 0L;
        return buffer.ToBytes();
    }

    public static Gadget GadgetFromBlob(byte[] blob)
    {
        Message buffer = Message.Create(256, blob);
        Gadget gadget = default;
        if (!MessageSerialization.Deserialize(ref gadget, ref buffer))
        {
            throw new InvalidDataException("库里的 Gadget blob 无法反序列化 —— 数据已损坏");
        }
        return gadget;
    }
}
