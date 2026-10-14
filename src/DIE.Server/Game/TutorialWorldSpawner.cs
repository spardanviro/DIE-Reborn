using System;
using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.MapObjects;

namespace DIE.Server.Game;

/// <summary>
/// 生成教程里那些**不来自 <see cref="Spawn_Tutorial"/>** 的实体: 变压器和卡车司机。
///
/// ## 类名是查表查出来的, 不是猜的
///
/// 这一轮找到了原厂的**类型 ↔ 明文名**对照表, 做法见
/// <c>tools/resolve_entity_names.py</c>: 把
///
///   `-z/-1zb.cs`            类型 ↔ GUID (842 条)
///   `cacheattributs.sexp`   明文名 ↔ GUID + 池化参数 (841 条)
///
/// 按 GUID join 起来。相关的几条:
///
///   TutorialLightingGenerator   _0023p0b   0e86a8a6-…   池 1
///   Trucker                     Trucker    4a8b0db0-…   池 3
///   TutorialNPC_Blade           _0023s3b   830806f4-…   ← Amber
///
/// <c>Trucker</c> 这个类**名字根本没被混淆**, 我先前按混淆名去找当然找不到。
///
/// 交叉验证: 属性表里那 6 个数字就是类上 <c>[_0023had(...)]</c> 的值,
/// 逐条对得上 (例: TutorialNPC_Trapper 是 `1 0 0 1 0 0`, 而 -Mk/-u3b.cs:8
/// 写的是 `[_0023had(1, 0, 0, 1, 0, 0)]`)。
///
/// ## 变压器
///
/// 地图上有 1 个 <see cref="Trigger_TutorialLightingGenerator"/> (教程图 ID=10103
/// 那组之外的独立标记)。教程状态 8 (<c>_0023hcc</c>) 的引导指向它
/// (-bd/-5cc.cs:369-372 遍历 MapObjects 找这个类型), 玩家要用霰弹枪打爆它。
///
/// 实体是 <c>_0023p0b</c>, 池大小 1 —— 和地图上只有一个标记吻合。
///
/// ## 卡车司机
///
/// <see cref="TruckerSpawnPoint"/> 1 个 @ (-172.4, 3363.3), <c>Team2</c>,
/// 就在 TRUCK / NPC_TRUCK 那一组旁边。生成 <c>Trucker</c>。
///
/// 队伍照地图给的 <c>Team</c> 字段走 —— 那是标记自带的数据, 不是我们定的。
/// </summary>
public static class TutorialWorldSpawner
{
    /// <summary>明文名 -> 混淆类名。来源见类说明。</summary>
    private const string GeneratorClass = "_0023p0b";   // TutorialLightingGenerator
    private const string TruckerClass = "Trucker";      // 没被混淆

    public sealed record Result(int Generators, int Truckers, IReadOnlyList<string> Skipped);

    public static Result SpawnAll(GameManager manager)
    {
        List<string> skipped = new List<string>();
        int generators = 0;
        int truckers = 0;

        if (manager?.MapObjects == null)
        {
            return new Result(0, 0, skipped);
        }

        Dictionary<string, Type> types = ResolveTypes(GeneratorClass, TruckerClass);

        foreach (MapObject mapObject in manager.MapObjects)
        {
            switch (mapObject)
            {
                case Trigger_TutorialLightingGenerator generator:
                    if (Spawn(manager, types, GeneratorClass, generator.Position,
                              generator.Direction, TeamId.Neutral, skipped))
                    {
                        generators++;
                    }

                    break;

                case TruckerSpawnPoint trucker:
                    // 队伍用标记自带的 Team 字段 (教程图上是 Team2), 不是我们定的。
                    if (Spawn(manager, types, TruckerClass, trucker.Position,
                              trucker.Direction, trucker.Team, skipped))
                    {
                        truckers++;
                    }

                    break;
            }
        }

        return new Result(generators, truckers, skipped);
    }

    /// <summary>
    /// 场上还有几个变压器活着。教程状态机用它判断"发电机打爆了没有"。
    ///
    /// ## 血量是原厂数据, 不用我们设
    ///
    /// <c>_0023p0b</c> 自带 <c>new _0023q5(StatType.MaxHealth, 60f, 0f, 10000f, 0)</c>
    /// (-EK/-p0b.cs:19) —— 60 血, 而且 <c>ShowHealthBar = true</c>。
    /// 和路障不同 (那个 MaxHealth 默认 0, 必须外部设)。
    ///
    /// ## 判据是血量, 不是 IsDead
    ///
    /// 和 <see cref="DestructibleSpawner.CountAlive"/> 同一个理由: 路障家族的
    /// <c>IsDead</c> 可能被重写成看状态枚举, 而那个枚举每帧才刷。血量是即时的。
    /// </summary>
    public static int CountAliveGenerators(GameManager manager)
    {
        if (manager?.ActiveGameObjectList == null)
        {
            return 0;
        }

        int count = 0;
        List<GameObjectBase> objects = manager.ActiveGameObjectList;

        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is Destructible destructible
                && destructible.GetType().Name == GeneratorClass
                && destructible.IsActive && destructible.Health > 0f)
            {
                count++;
            }
        }

        return count;
    }

    private static bool Spawn(GameManager manager, Dictionary<string, Type> types, string className,
                             FarseerPhysics.Common.Vector2 position,
                             FarseerPhysics.Common.Vector2 direction,
                             TeamId team, List<string> skipped)
    {
        if (!types.TryGetValue(className, out Type type))
        {
            skipped.Add($"{className}(类型注册表里没有)");
            return false;
        }

        if (GameManager.GetGameObjectTypeId(type) < 0)
        {
            skipped.Add($"{className}(没进对象池)");
            return false;
        }

        // Position 在 Entity 上, 不在 GameObjectBase 上 —— 变压器和司机都是
        // Entity 的子类 (Destructible / Character), 所以按 Entity 取。
        GameObjectBase pooled = manager.GetGameObjectFromPool(type);
        if (pooled is not Entity entity || entity.IsDummyObject)
        {
            // dummy 表示 CacheGame 没缓存它 —— 生成了也不会激活, 报出来而不是静默。
            skipped.Add($"{className}(拿到 dummy 对象或不是 Entity)");
            return false;
        }

        entity.Position = position;
        entity.TeamId = team;

        // 朝向只有 Character 有 —— Destructible 没有 AimDirection。
        if (entity is Character character && direction != FarseerPhysics.Common.Vector2.Zero)
        {
            character.AimDirection = direction;
        }

        manager.SpawnGameObject(entity, null, null);

        if (!entity.IsActive)
        {
            skipped.Add($"{className}(Spawn 后未激活)");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 按类名从注册表里找类型。不硬编码 typeId —— 那是按注册顺序算的。
    /// </summary>
    private static Dictionary<string, Type> ResolveTypes(params string[] classNames)
    {
        Dictionary<string, Type> found = new Dictionary<string, Type>();

        foreach (KeyValuePair<int, Type> entry in GameManager.GameObjectTypeIDToType)
        {
            if (entry.Value == null)
            {
                continue;
            }

            foreach (string className in classNames)
            {
                if (entry.Value.Name == className)
                {
                    found[className] = entry.Value;
                }
            }
        }

        return found;
    }
}
