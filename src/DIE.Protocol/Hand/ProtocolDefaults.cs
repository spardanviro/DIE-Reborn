using System;
using ConductorCrafting;

namespace GameProtocol;

/// <summary>
/// 协议级的"最小但合法"默认值。
///
/// 原则: 客户端会遍历的引用字段一律给 <see cref="Array.Empty{T}"/> 而不是 null。
/// 原厂 Serialize 对 null 会写 varint 0, 线格式上是安全的, 但显式空数组更清楚,
/// 也避免了"这里到底会不会 NPE"的反复确认。
/// </summary>
public static class ProtocolDefaults
{
    /// <summary>
    /// 不禁用任何东西的 DisabledFeaturesData。
    ///
    /// Revision 必须**非零且稳定**。客户端把它和自己缓存的版本号比对:
    /// 给 0 或每次递增, 会让匹配服返回 DisabledFeaturesOutdated, 客户端就会
    /// 重新拉取、重试匹配, 陷入无限循环 (Matchmaker.cs:750-777)。
    /// </summary>
    public const int DisabledFeaturesRevision = 1;

    /// <summary>
    /// GameplayChange 的版本号。客户端用它决定要不要重载平衡性数据,
    /// 同样保持稳定即可。
    /// </summary>
    public const int GameplayRevision = 1;

    public static DisabledFeaturesData NothingDisabled() => new DisabledFeaturesData(
        version: DisabledFeaturesRevision,
        features: Array.Empty<Features>(),
        characters: Array.Empty<CharacterEnum>(),
        languages: Array.Empty<Languages>(),
        maps: Array.Empty<MapIndex>());
}
