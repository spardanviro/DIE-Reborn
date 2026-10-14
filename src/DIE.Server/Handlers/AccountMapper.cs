using ConductorCrafting;
using ConductorShop;
using DIE.Server.Data;

namespace DIE.Server.Handlers;

/// <summary>
/// 数据库记录 → 线上结构 的映射。登录推送和菜单期的 GetAccountData 都要用,
/// 抽出来避免两处各写一遍导致漂移。
/// </summary>
internal static class AccountMapper
{
    public static AccountsData ToAccountsData(Account account)
    {
        AccountsData data = default;
        data.UserID = account.UserId;
        data.StoryMapXP = account.StoryMapXp;
        data.StoryMapData = account.StoryMapData;
        data.StoryMapVersion = account.StoryMapVersion;
        data.RegularBoost = account.RegularBoost;
        data.PremiumBoost = account.PremiumBoost;
        data.UnboundXP = account.UnboundXp;
        data.CreateTime = account.CreatedUtcTicks;
        // 注意: VanityIcon 在线上是**裸 byte**, 不是 varint —— 这是 MessageSerialization
        // 里少数几个不按 varint 写枚举的字段之一 (另一个是 RewardSetupData.Type)。
        data.VanityIcon = (VanityIconType)account.VanityIcon;
        data.UnlockedDLC = (DLCPackage)account.UnlockedDlc;
        data.LastPremiumGain = 0;
        data.LastScavengerWinBonusTime = 0;
        data.LastHordeWinBonusTime = 0;
        data.CounterData = account.CounterData;
        data.CrossroadProgress = account.CrossroadProgress;
        data.Language = (Languages)account.Language;
        return data;
    }

    public static CurrencyCollection ToCurrency(Account account) =>
        new CurrencyCollection(account.Gold, account.Silver, account.CharacterPoints, account.ResearchPoints);
}
