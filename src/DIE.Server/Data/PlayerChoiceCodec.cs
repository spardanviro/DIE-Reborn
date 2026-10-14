using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ConductorCrafting;

namespace DIE.Server.Data;

/// <summary>
/// 玩家在排队时选的角色、外观、武器、装置 (Gadget), 编码进匹配票据的 <c>queue_data</c> 列。
///
/// ## 为什么需要跨越这一步
///
/// 客户端在 <c>GetMatchmakingTicketRequest.PlayerChoices</c> 里把角色、武器、
/// 消耗品、饰品的完整选择一次性发过来。但那是**菜单链路 (1555)** 上的事,
/// 而真正要用它的是**战斗服 (UDP)** —— 两者之间只有匹配票据这一条纽带。
///
/// ## 格式
///
/// 自定义的紧凑格式, **不是**游戏的线格式 —— 这份数据只在我们自己的库里流转,
/// 客户端永远看不到它。所以怎么编由我们说了算, 只要自洽。
///
///   [0]    版本号, 便于以后加字段而不破坏在途票据
///   [1]    CharacterEnum
///   [2..3] SelectedVanityTitleID (ushort, LE)
///   [4..5] SelectedVanityIconID  (ushort, LE)
///   v2:   [6] 武器数 N (0..2), N × ushort 武器 SchematicID
///   v3:   [6] 武器数 N, N × (ushort 长度 + Unique blob)
///         然后 装置数 M (0..2), M × (ushort 长度 + Gadget blob)
///   v4:   v3 之后 消耗品数 K (0..3), K × (byte 格子 + ushort 消耗品 ID + ushort 带进去的数量)
///
/// blob 用的是账号库同一套协议序列化器 (<see cref="CraftingCatalog.ToBlob(Unique)"/>),
/// 所以武器的**改装槽 (Slots)** 和装置的**词条掷点 (Stats)** 原样带进战斗服。
///
/// ## 为什么从 v2 升到 v3 (2026-09-15)
///
/// 玩家要求审核 Gadget 和改装 (Modification) 是否按描述生效。查下来 v2 只存了武器图纸号 ——
/// 改装槽和装置整个没进战斗服, **一个都不生效** (v2 当时注释里写着"代价: 强化和插槽丢了")。
///
/// v1/v2 的票据仍然认 —— 解出来的武器没有改装、没有装置。
///
/// ## v4 (2026-09-15)
///
/// 玩家要求审核消耗品。场外装的消耗品同样没进战斗服 (局内三格是空的)。
/// 数量在签票时按背包核好 (<see cref="ConsumableLoadout"/>), 局末按实际用掉的扣。v1..v3 的票据解出来没有消耗品。
/// </summary>
public static class PlayerChoiceCodec
{
    private const byte Version = 4;

    /// <summary>v1 的固定长度, 也是后续版本的头部长度。</summary>
    private const int HeaderSize = 6;

    /// <summary>最多带几把武器 —— 客户端的装备栏就是主副两把。</summary>
    public const int MaxWeapons = 2;

    /// <summary>最多带几个装置 —— ClientInfoData 里就两格 (Gadget1/Gadget2)。</summary>
    public const int MaxGadgets = 2;

    public static byte[] Encode(PlayerQueueData choice) => Encode(choice, (IReadOnlyList<Unique>)null, null);

    /// <summary>带武器、装置, 不带消耗品。</summary>
    public static byte[] Encode(PlayerQueueData choice, IReadOnlyList<Unique> weapons, IReadOnlyList<Gadget> gadgets) =>
        Encode(choice, weapons, gadgets, null);

    /// <summary>只带武器图纸号 (没有改装) —— 测试和老调用点用。</summary>
    public static byte[] Encode(PlayerQueueData choice, IReadOnlyList<ushort> weaponSchematicIds) =>
        Encode(choice,
            (weaponSchematicIds ?? Array.Empty<ushort>()).Select(id => new Unique { SchematicID = id }).ToList(),
            null);

    /// <summary>
    /// 带上完整的武器 (含改装槽) 和装置。由调用方从账号库解出来 (客户端给的是 GUID)。
    /// </summary>
    public static byte[] Encode(PlayerQueueData choice, IReadOnlyList<Unique> weapons, IReadOnlyList<Gadget> gadgets,
                                IReadOnlyList<ConsumableSlot> consumables)
    {
        List<byte[]> weaponBlobs = (weapons ?? Array.Empty<Unique>())
            .Where(w => w.SchematicID != 0)
            .Take(MaxWeapons)
            .Select(CraftingCatalog.ToBlob)
            .ToList();
        List<byte[]> gadgetBlobs = (gadgets ?? Array.Empty<Gadget>())
            .Where(g => g.GadgetID != 0)
            .Take(MaxGadgets)
            .Select(CraftingCatalog.ToBlob)
            .ToList();

        using MemoryStream stream = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(stream);
        writer.Write(Version);
        writer.Write((byte)choice.Character);
        writer.Write(choice.SelectedVanityTitleID);
        writer.Write(choice.SelectedVanityIconID);
        WriteBlobs(writer, weaponBlobs);
        WriteBlobs(writer, gadgetBlobs);

        List<ConsumableSlot> carried = (consumables ?? Array.Empty<ConsumableSlot>())
            .Where(c => c.Id != 0 && c.Amount > 0 && c.Slot < ConsumableLoadout.SlotCount)
            .Take(ConsumableLoadout.SlotCount)
            .ToList();
        writer.Write((byte)carried.Count);
        foreach (ConsumableSlot slot in carried)
        {
            writer.Write(slot.Slot);
            writer.Write(slot.Id);
            writer.Write(slot.Amount);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteBlobs(BinaryWriter writer, List<byte[]> blobs)
    {
        writer.Write((byte)blobs.Count);
        foreach (byte[] blob in blobs)
        {
            writer.Write((ushort)blob.Length);
            writer.Write(blob);
        }
    }

    /// <summary>
    /// 解码。数据缺失或版本不认识时返回 null —— 调用方据此走默认值,
    /// 而不是拿一个半解出来的结构继续。
    ///
    /// 截断或坏掉的武器/装置段只取读得完整的那些 —— 宁可少带也不要拿越界数据当图纸号。
    /// </summary>
    public static PlayerChoice? Decode(byte[] blob)
    {
        if (blob == null || blob.Length < HeaderSize)
        {
            return null;
        }

        byte version = blob[0];
        if (version < 1 || version > Version)
        {
            return null;
        }

        CharacterEnum character = (CharacterEnum)blob[1];
        ushort title = BitConverter.ToUInt16(blob, 2);
        ushort icon = BitConverter.ToUInt16(blob, 4);

        List<Unique> weapons = new List<Unique>();
        List<Gadget> gadgets = new List<Gadget>();
        List<ConsumableSlot> consumables = new List<ConsumableSlot>();

        if (version == 2 && blob.Length > HeaderSize)
        {
            int count = blob[HeaderSize];
            for (int i = 0; i < count && i < MaxWeapons; i++)
            {
                int offset = HeaderSize + 1 + i * 2;
                if (offset + 1 >= blob.Length)
                {
                    break;
                }

                weapons.Add(new Unique { SchematicID = BitConverter.ToUInt16(blob, offset) });
            }
        }
        else if (version >= 3)
        {
            int offset = HeaderSize;
            ReadBlobs(blob, ref offset, MaxWeapons, weapons, CraftingCatalog.FromBlob);
            ReadBlobs(blob, ref offset, MaxGadgets, gadgets, CraftingCatalog.GadgetFromBlob);
            if (version >= 4 && offset < blob.Length)
            {
                int count = blob[offset++];
                for (int i = 0; i < count && offset + 5 <= blob.Length; i++, offset += 5)
                {
                    ConsumableSlot slot = new ConsumableSlot(blob[offset], BitConverter.ToUInt16(blob, offset + 1),
                        BitConverter.ToUInt16(blob, offset + 3));
                    if (slot.Slot < ConsumableLoadout.SlotCount && slot.Id != 0 && slot.Amount > 0
                        && consumables.All(c => c.Slot != slot.Slot))
                    {
                        consumables.Add(slot);
                    }
                }
            }
        }

        return new PlayerChoice(character, title, icon, weapons, gadgets, consumables);
    }

    private static void ReadBlobs<T>(byte[] blob, ref int offset, int max, List<T> into, Func<byte[], T> decode)
    {
        if (offset >= blob.Length)
        {
            return;
        }

        int count = blob[offset++];
        for (int i = 0; i < count; i++)
        {
            if (offset + 2 > blob.Length)
            {
                offset = blob.Length;
                return;
            }

            int length = BitConverter.ToUInt16(blob, offset);
            offset += 2;
            if (offset + length > blob.Length)
            {
                offset = blob.Length;
                return;
            }

            byte[] one = blob.AsSpan(offset, length).ToArray();
            offset += length;
            if (into.Count >= max)
            {
                continue;
            }

            try
            {
                into.Add(decode(one));
            }
            catch (InvalidDataException)
            {
                // 坏掉的一件跳过, 别的照带
            }
        }
    }
}

/// <summary>解码后的玩家选择。</summary>
public readonly record struct PlayerChoice(
    CharacterEnum Character,
    ushort VanityTitleId,
    ushort VanityIconId,
    IReadOnlyList<Unique> Weapons,
    IReadOnlyList<Gadget> Gadgets,
    IReadOnlyList<ConsumableSlot> Consumables)
{
    /// <summary>老调用点用的三参构造 —— 武器、装置、消耗品都为空。</summary>
    public PlayerChoice(CharacterEnum character, ushort titleId, ushort iconId)
        : this(character, titleId, iconId, Array.Empty<Unique>(), Array.Empty<Gadget>())
    {
    }

    /// <summary>不带消耗品。</summary>
    public PlayerChoice(CharacterEnum character, ushort titleId, ushort iconId, IReadOnlyList<Unique> weapons, IReadOnlyList<Gadget> gadgets)
        : this(character, titleId, iconId, weapons, gadgets, Array.Empty<ConsumableSlot>())
    {
    }

    /// <summary>武器图纸号 (结算、感染等级、开局日志用)。</summary>
    public IReadOnlyList<ushort> WeaponSchematicIds =>
        (Weapons ?? Array.Empty<Unique>()).Select(w => w.SchematicID).ToList();
}
