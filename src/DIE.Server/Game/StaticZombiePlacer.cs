using System;
using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.MapObjects;

namespace DIE.Server.Game;

/// <summary>
/// 按 <see cref="Spawn_Static"/> 摆放场景僵尸 —— 坐着的、趴着的、在啃东西的那些。
///
/// ## 为什么之前僵尸位置和原版对不上
///
/// 教程地图 <c>Tutorial_01_Prologue_scene.cmo</c> 一共 144 个地图对象, 我们原先
/// 只读了 9 个 <see cref="Spawn_RoamingZombies"/>, 然后在每个点周围**随机撒点**。
/// 而原版的僵尸是**逐只手摆**的:
///
///   Spawn_Static         29 只   Sitting 20 / Eating 9
///   Spawn_EventEntities  66 只   Crawling 41 / Digging 16 / Jumping 5 /
///                                Stumbling 3 / Leaping 1
///
/// 加起来 95 只固定摆位, 位置、朝向、出场姿势全部写死在地图里。随机撒点当然对不上。
///
/// 本类负责其中的 <see cref="Spawn_Static"/> 那 29 只。<see cref="Spawn_EventEntities"/>
/// 归 <see cref="EventSpawnDirector"/> —— 它们要等事件触发。
///
/// ## "坐着"不是动画, 是一个持续状态
///
/// <c>Spawn_Static.SpawnState</c> 是原厂属性, 把地图里的
/// <see cref="ZombieStaticSpawnAnimation"/> 映射到内部状态枚举:
///
///   Sitting     -> _0023VCc
///   Eating      -> _0023WCc
///   LayingBelly -> _0023Wed
///   LayingBack  -> _0023Ved
///   HittingWall -> _0023Ued
///
/// 和漫游僵尸的 Digging 不同, 这些是**待机姿势**: 僵尸一直保持着, 直到
/// <see cref="ZombieBrain"/> 发现玩家进入仇恨半径才起身。
/// <c>ZombieBrain.FinishEmerge</c> 已经在做这件事 —— 它把
/// <c>SpawnState_Current</c> 复位, 因为客户端没有任何代码会复位它。
///
/// ## 类型取 Walker
///
/// <see cref="Spawn_Static"/> **没有类型字段** —— 地图只说"这里有一只、什么姿势"。
/// 取 Walker 是我们的选择, 依据是那五种姿势 (坐、吃、趴、撞墙) 全是普通僵尸的
/// 待机动作, 特殊僵尸 (Ram/Butcher/Puller) 在 <c>_SpawnStateAnimation</c> 里
/// 根本没注册这些状态 —— <c>NPC.TypeSupportsSpawnState</c> 会返回 false。
///
/// ## Respawnable / IgnoreSpawnRestriction
///
/// 两个布尔都读了但**暂不消费**: 静态摆位僵尸的重生需要"玩家离得够远 + 计时器",
/// 和 <see cref="RoamingZombieDirector"/> 里那套一样, 等做正经重生时一并处理。
/// 日志会把带 Respawnable 的只数报出来。
/// </summary>
public static class StaticZombiePlacer
{
    public sealed record Result(
        int Placed,
        int Markers,
        int Respawnable,
        IReadOnlyDictionary<ZombieStaticSpawnAnimation, int> ByPose);

    /// <summary>
    /// 一次性摆完全图的静态僵尸。开局调一次。
    ///
    /// 幂等性由调用方保证 —— 本方法每调一次就摆一批。
    /// </summary>
    public static Result Place(GameManager manager)
    {
        Dictionary<ZombieStaticSpawnAnimation, int> byPose =
            new Dictionary<ZombieStaticSpawnAnimation, int>();
        int placed = 0;
        int markers = 0;
        int respawnable = 0;

        if (manager?.MapObjects == null)
        {
            return new Result(0, 0, 0, byPose);
        }

        string obfuscated = ZombieFactory.ObfuscatedNameFor(ZombieFactory.DefaultKind);
        Dictionary<string, Type> types = ZombieFactory.ResolveKindTypes();

        if (obfuscated == null || !types.TryGetValue(obfuscated, out Type walker))
        {
            return new Result(0, 0, 0, byPose);
        }

        foreach (MapObject mapObject in manager.MapObjects)
        {
            if (mapObject is not Spawn_Static marker)
            {
                continue;
            }

            markers++;
            if (marker.Respawnable)
            {
                respawnable++;
            }

            // 位置不散布 —— 这些点是手摆的, 散布就毁了原版布局。
            Character zombie = ZombieFactory.Spawn(
                manager, walker, marker.Position, marker.SpawnState, marker.Direction);

            if (zombie == null)
            {
                continue;
            }

            placed++;
            byPose.TryGetValue(marker.SpawnAnimation, out int seen);
            byPose[marker.SpawnAnimation] = seen + 1;
        }

        return new Result(placed, markers, respawnable, byPose);
    }

}
