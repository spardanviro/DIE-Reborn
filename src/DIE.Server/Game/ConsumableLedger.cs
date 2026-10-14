using System;
using System.Collections.Generic;
using System.Linq;
using ConductorCrafting;
using ConductorGameLogic.Abilities;
using ConductorGameLogic.Entities;
using DIE.Server.Data;

namespace DIE.Server.Game;

/// <summary>
/// 一局里每个人带进来的消耗品: 开局放进能力栏, 局末 / 中途放弃时算用掉几个。
///
/// ## 放在哪
///
/// 局内三格是能力栏的 21..23 号槽 —— 原厂 <c>Player.EquipHaxConsumable</c> (调试入口, Player.cs:2299)
/// 和团队对战局内商店 (<c>BuyTvTConsumable</c> 的 <c>_0023wIs</c>) 都是 <c>SetAbility(技能, 21 + 格)</c>
/// 再 <c>SetConsumableCount</c>。剩几个随能力栏同步给客户端 (AbilityBar.cs:801), 用一次原厂自己减一
/// (Ability.cs:1621)。所以这里只管放进去和最后数一数。
///
/// ## 怎么扣
///
/// 原厂结算的入参里就有这份账: <c>RewardPlayerInputData.EquippedConsumables</c> (每格开局几个、现在几个),
/// <c>RewardManager</c> 算出 <c>Consumable1..3Uses</c> 发给客户端, 客户端据此从本地背包里减
/// (GUI_RewardWindow.RemoveUsedConsumables)。账号库那边原厂靠 <c>MergedStackableUpdates</c> 落库,
/// 我们在 <see cref="TakeUsed"/> 之后自己扣。
///
/// 中途放弃: 原厂 <c>LeaveMatchResponse.ConsumablesLost</c>, 客户端同样从背包减 (GUI_RejoinGamePopup.cs:384)。
/// 扣的是**到那时为止用掉的**, 扣完把基准挪到当前值 —— 之后这局照常结算时不会再扣一遍。
/// </summary>
public sealed class ConsumableLedger
{
    /// <summary>消耗品第一格在能力栏上的槽位号。</summary>
    public const int FirstAbilitySlot = 21;

    private sealed class Entry
    {
        public byte Slot;
        public StructuredConsumable Structure;
        public AbilityIdentifier Ability;
        public int Baseline;
    }

    private readonly object _lock = new object();
    private readonly Dictionary<ulong, List<Entry>> _byUser = new Dictionary<ulong, List<Entry>>();

    /// <summary>
    /// 开局把消耗品放进能力栏。同一个账号只放一次 —— 掉线重连接回原来的角色时, 剩的数量在角色身上, 不能补满。
    /// </summary>
    /// <returns>给日志看的一句摘要。</returns>
    public string Equip(ulong userId, Player player, IReadOnlyList<ConsumableSlot> slots)
    {
        if (userId == 0 || player?.AbilityBar == null || slots == null || slots.Count == 0)
        {
            return "没有";
        }

        List<string> parts = new List<string>();
        lock (_lock)
        {
            if (_byUser.ContainsKey(userId))
            {
                return "已经放过 (重连不补)";
            }

            List<Entry> entries = new List<Entry>();
            foreach (ConsumableSlot slot in slots)
            {
                StructuredConsumable structure = CraftingManager.GetConsumable(slot.Id);
                if (structure == null || slot.Slot >= ConsumableLoadout.SlotCount
                    || !Enum.TryParse(structure.AbilityIdentifier, out AbilityIdentifier identifier))
                {
                    parts.Add($"{slot.Id} 图鉴里没有");
                    continue;
                }

                int abilitySlot = FirstAbilitySlot + slot.Slot;
                player.AbilityBar.SetAbility(identifier, abilitySlot);
                Ability ability = player.AbilityBar.GetAbility(abilitySlot);
                if (ability == null || ability.AbilityIdentifier != identifier)
                {
                    parts.Add($"{structure.WorkingName} 这个角色的能力栏里没有");
                    continue;
                }

                ability.SetConsumableCount(slot.Amount);
                entries.Add(new Entry { Slot = slot.Slot, Structure = structure, Ability = identifier, Baseline = slot.Amount });
                parts.Add($"{slot.Slot + 1}:{structure.WorkingName}x{slot.Amount}");
            }

            _byUser[userId] = entries;
        }

        return string.Join(", ", parts);
    }

    /// <summary>局末结算的入参: 每格 "上次扣账时几个 / 现在几个"。</summary>
    public RewardConsumableInputData[] RewardInputFor(ulong userId, Player player)
    {
        lock (_lock)
        {
            if (!_byUser.TryGetValue(userId, out List<Entry> entries))
            {
                return Array.Empty<RewardConsumableInputData>();
            }

            return entries.Select(e => new RewardConsumableInputData
            {
                Index = e.Slot,
                AmountBefore = (ushort)e.Baseline,
                AmontAfter = CountNow(player, e),
                Structure = e.Structure,
            }).ToArray();
        }
    }

    /// <summary>
    /// 从上次扣账到现在用掉的 (消耗品 ID, 个数), 并把基准挪到现在 —— 同一批用量只会被取走一次。
    /// </summary>
    public IReadOnlyList<(ushort Id, int Used)> TakeUsed(ulong userId, Player player)
    {
        List<(ushort, int)> used = new List<(ushort, int)>();
        lock (_lock)
        {
            if (!_byUser.TryGetValue(userId, out List<Entry> entries))
            {
                return used;
            }

            foreach (Entry entry in entries)
            {
                int now = CountNow(player, entry);
                if (now < entry.Baseline)
                {
                    used.Add(((ushort)entry.Structure.ID, entry.Baseline - now));
                    entry.Baseline = now;
                }
            }
        }

        return used;
    }

    /// <summary>
    /// 这一格现在剩几个。找不到技能 (角色没了之类) 就当没用 —— 宁可少扣。
    /// 不会超过基准: 旅行商人改装会把用掉的退回来 (Ability.cs:1895), 退回来的不算用掉, 也不算多出来。
    /// </summary>
    private static int CountNow(Player player, Entry entry)
    {
        Ability ability = player?.AbilityBar?.GetAbility(entry.Ability);
        return ability == null ? entry.Baseline : Math.Clamp(ability.ConsumableCount, 0, entry.Baseline);
    }
}
