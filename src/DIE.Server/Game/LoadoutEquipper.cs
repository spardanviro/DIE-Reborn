using System;
using System.Collections.Generic;
using System.Linq;
using ConductorCrafting;
using ConductorGameLogic;
using ConductorGameLogic.Crafting;
using ConductorGameLogic.Entities;

namespace DIE.Server.Game;

/// <summary>
/// 把玩家场外配好的**完整**装备装到战斗服的角色上: 武器 (带改装槽) + 装置 (Gadget)。
///
/// ## 为什么不再用 EquipHaxWeaponNew
///
/// 玩家要求审核 Gadget 和改装 (Modification) 是否按描述生效 (2026-09-15)。查下来之前走的是
/// <c>Player.EquipHaxWeaponNew(slot, 图纸号)</c> —— 调试用的入口, 造出来的武器**只有图纸自带属性、没有改装**
/// (Player.cs:2612 传的是空槽位表), 装置则压根没装。于是改装、装置在对局里一个都不生效。
///
/// ## 走原厂客户端同一条路
///
/// 原厂客户端进局时 (Client.cs:574) 和收到 ClientInfo 时 (GameManagerClient.cs:581) 做的是:
///
///     Weapon1 = GetWeapon(ClientInfoData.武器1, player);   // Unique -> Item, 槽位里的改装变成 ItemUpgrades
///     Gadget1 = GetTrinket(ClientInfoData.装置1, player);  // Gadget -> Item, 词条掷点算出属性和 perk
///
/// 服务端这里构造同样的 Item, 并把 Unique/Gadget 原样写回 ClientInfoData —— 客户端据此构造出
/// 一模一样的物品 (改装、装置属性两边一致)。
///
/// ## 不调 EquipWeaponsAndAbilities
///
/// 它最后 <c>EquipWeapon(_EquippedWeaponSlot)</c>, 而槽位只在 <c>LocalPlayer == this</c> 时从 -1 拨到 0
/// (Player.cs:753) —— 服务端没有 LocalPlayer, 调了就是空手。所以这里拆开做它做的三件事:
/// 武器 <c>SetWeaponAndEquipGlobals</c>、装置 <c>EquipGlobalStats/EquipGlobalUpgrades</c> (= 私有的 _00232C)、
/// <c>EquipWeapon(0)</c>。之后的复活、升级走原厂自己的 <c>EquipWeaponsAndAbilities</c> / <c>_00230C</c>,
/// 它们读的就是这里设好的 Weapon1/Weapon2/Gadget1/Gadget2。
/// </summary>
public static class LoadoutEquipper
{
    public sealed record Result(int Weapons, int Designs, int Gadgets, string Summary);

    public static Result Equip(GameManager simulation, Client client, Player player,
                               IReadOnlyList<Unique> weapons, IReadOnlyList<Gadget> gadgets)
    {
        if (simulation == null || player == null)
        {
            return new Result(0, 0, 0, "没有玩家");
        }

        int level = player.GetLevel();
        int weaponCount = 0, designCount = 0, gadgetCount = 0;
        List<string> parts = new List<string>();

        IReadOnlyList<Unique> chosenWeapons = weapons ?? Array.Empty<Unique>();
        for (int slot = 0; slot < 2 && slot < chosenWeapons.Count; slot++)
        {
            Unique unique = chosenWeapons[slot];
            if (unique.SchematicID == 0)
            {
                continue;
            }

            // ClientInfoData 按 Guid 判"这一格有没有武器" (ClientInfoData.cs:57), 老票据里没有就补一个
            unique.Guid ??= Guid.NewGuid().ToByteArray();

            Item item = simulation.GetWeapon(unique, player);
            if (item == null)
            {
                parts.Add($"武器{slot + 1} 图纸 {unique.SchematicID} 图鉴里没有");
                continue;
            }

            if (slot == 0)
            {
                player.Weapon1 = item;
            }
            else
            {
                player.Weapon2 = item;
            }

            player.SetWeaponAndEquipGlobals(slot, item, level);
            if (client?.ClientInfoData != null)
            {
                if (slot == 0)
                {
                    client.ClientInfoData._0023lYb = unique;
                }
                else
                {
                    client.ClientInfoData._0023mYb = unique;
                }
            }

            int designs = DesignsOn(unique);
            weaponCount++;
            designCount += designs;
            parts.Add($"武器{slot + 1} {unique.SchematicID} 改装 {designs} [{string.Join(",", item.ItemUpgrades.Select(u => u.UpgradeType))}]");
        }

        IReadOnlyList<Gadget> chosenGadgets = gadgets ?? Array.Empty<Gadget>();
        for (int slot = 0; slot < 2 && slot < chosenGadgets.Count; slot++)
        {
            Gadget gadget = chosenGadgets[slot];
            if (gadget.GadgetID == 0)
            {
                continue;
            }

            gadget.GUID ??= Guid.NewGuid().ToByteArray();
            Item item = simulation.GetTrinket(gadget, player);
            if (item == null)
            {
                parts.Add($"装置{slot + 1} {gadget.GadgetID} 图鉴里没有");
                continue;
            }

            if (slot == 0)
            {
                player.Gadget1 = item;
            }
            else
            {
                player.Gadget2 = item;
            }

            item.EquipGlobalStats(level);
            item.EquipGlobalUpgrades();
            if (client?.ClientInfoData != null)
            {
                if (slot == 0)
                {
                    client.ClientInfoData._0023qYb = gadget;
                }
                else
                {
                    client.ClientInfoData._0023rYb = gadget;
                }
            }

            gadgetCount++;
            parts.Add($"装置{slot + 1} {gadget.GadgetID} 属性 [{string.Join(",", item.Stats.Select(s => $"{s.StatRatingType}:{s.Value:0.#}"))}] perk [{string.Join(",", item.ItemUpgrades.Select(u => u.UpgradeType))}]");
        }

        if (client?.ClientInfoData != null)
        {
            client.ClientInfoData._0023sYb = true;
        }

        if (player.Weapon1 != null)
        {
            player.EquipWeapon(0);
        }
        else if (player.Weapon2 != null)
        {
            player.EquipWeapon(1);
        }

        player.UpdateWeaponBalanceBoosters(level);
        return new Result(weaponCount, designCount, gadgetCount, string.Join("; ", parts));
    }

    private static int DesignsOn(Unique unique)
    {
        if (unique.Slots == null || unique.Slots.Length == 0)
        {
            return 0;
        }

        StunMessage.Message buffer = StunMessage.Message.Create(unique.Slots.Length, unique.Slots);
        return Unique.Deserialize(ref buffer, out List<UniqueSlot> slots) && slots != null ? slots.Count : 0;
    }
}
