using System;
using System.Collections.Generic;
using System.Linq;
using ConductorCrafting;

namespace DIE.Server.Data;

/// <summary>带进一局的一格消耗品: 第几格 (0..2)、哪个消耗品、带了几个。</summary>
public readonly record struct ConsumableSlot(byte Slot, ushort Id, ushort Amount);

/// <summary>
/// 玩家场外装的消耗品 -> 这一局实际带进去的数量。
///
/// ## 规则都照原厂客户端
///
/// 客户端排队时发 <c>PlayerQueueData.EquippedConsumableIDs</c> (三格, 0 = 空, Matchmaker.cs:145),
/// 服务端只信它挑了哪个, 其余按账号库重新核一遍:
///
///   - 格子要按账号等级解锁 —— 1 / 11 / 19 级各开一格 (<c>AccountLevelHelpers</c> 的 ConsumableSlot1..3,
///     客户端判法 <c>CharacterData.HasConsumableSlotUnlocked</c>)。没解锁的格子客户端会自己清掉, 这里再挡一次。
///   - 带进去的数量 = min(背包里有几个, 每格上限 <c>Stacks</c>)。装备栏显示的就是 "有几个/Stacks"
///     (GUI_ItemEquipSlot.cs:385, 不够一格时标黄), 角色面板显示 min(Stacks, 有几个)。
///   - 带 <c>InGameShop</c> 标签的是团队对战局内商店的货, 不能从背包带。
///
/// 同一个消耗品装在两格: 能力栏里一个技能只有一个实例 (<c>AbilityBar.SetAbility</c> 是挪不是复制),
/// 两格装同一个第二格会把第一格挤掉 —— 所以只认第一格。
/// </summary>
public static class ConsumableLoadout
{
    public const int SlotCount = 3;

    public static IReadOnlyList<ConsumableSlot> Resolve(IReadOnlyList<int> equippedIds, int accountLevel, Func<ushort, int> owned)
    {
        List<ConsumableSlot> slots = new List<ConsumableSlot>();
        if (equippedIds == null || owned == null)
        {
            return slots;
        }

        for (int slot = 0; slot < SlotCount && slot < equippedIds.Count; slot++)
        {
            int id = equippedIds[slot];
            if (id <= 0 || id > ushort.MaxValue || !IsSlotUnlocked(accountLevel, slot + 1)
                || slots.Any(s => s.Id == id))
            {
                continue;
            }

            StructuredConsumable consumable = CraftingManager.GetConsumable((ushort)id);
            if (consumable == null || (consumable.Tags != null && consumable.Tags.Contains(TagEnum.InGameShop)))
            {
                continue;
            }

            int amount = Math.Min(owned((ushort)id), Math.Max(consumable.Stacks, 1));
            if (amount > 0)
            {
                slots.Add(new ConsumableSlot((byte)slot, (ushort)id, (ushort)amount));
            }
        }

        return slots;
    }

    /// <summary>第 <paramref name="slot"/> 格 (1..3) 在这个账号等级解锁了没有。</summary>
    public static bool IsSlotUnlocked(int accountLevel, int slot)
    {
        AccountLevelRewardType needed = slot switch
        {
            1 => AccountLevelRewardType.ConsumableSlot1,
            2 => AccountLevelRewardType.ConsumableSlot2,
            3 => AccountLevelRewardType.ConsumableSlot3,
            _ => AccountLevelRewardType.Unspecified,
        };

        return needed != AccountLevelRewardType.Unspecified
               && AccountLevelHelpers.GetAllRewardsAtLevel(Math.Max(accountLevel, 1)).Contains(needed);
    }
}
