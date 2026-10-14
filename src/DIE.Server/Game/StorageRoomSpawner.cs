using System.Collections.Generic;
using System.Linq;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.Entities.GameModeObjects;
using ConductorGameLogic.MapObjects;

namespace DIE.Server.Game;

/// <summary>
/// 生成补给点实体 (<see cref="StorageRoom"/>) —— 教程里那个
/// **lifeguard station**, 玩家管它叫"旗帜"。
///
/// ## 为什么箭头会指到地图外
///
/// <c>StorageRoom.Position</c> 是个**只读属性**, 它转发给配对的地图点:
///
///     public Vector2 Position =&gt;
///         StorageRoomPoint != null ? StorageRoomPoint._0023B : Vector2.Zero;
///
/// 配不上就是 <c>Vector2.Zero</c> = **(0, 0)**。而客户端教程状态 14
/// (-5cc.cs:438) 正是拿它当箭头目标:
///
///     foreach (StorageRoom item in GetActiveObjectsFromCategory(ObjCat.StorageRoom))
///         _0023Lcc = item.Position;
///
/// 教程图的坐标在 x -230~2030 / y 2960~3800 那一带, (0,0) 落在地图外 ——
/// 玩家反馈的"箭头指向一个地图外面的地方"就是这么来的。
///
/// ## 配对是原厂做的, 我们只要把实体放出来
///
/// <c>StorageRoom.UpdateGameObject</c> 每帧自己找搭档 (:73-81):
///
///     foreach (StaticMapObjects[i] is StorageRoomPoint p &amp;&amp; p._0023kg == ID)
///         StorageRoomPoint = p;
///
/// 而 <c>StorageRoomPoint</c> **双端都已经有了** ——
/// <c>GameMode.SpawnStorageRoomsPoints()</c> 从 <c>Event_StorageRoom</c> 建它们
/// (GameMode.cs:750-763), 而教程模式的 <c>LoadGameMode</c> 调了那个方法
/// (-5cc.cs:143), 加载地图时双端各跑一次。
///
/// 所以缺的只有**实体**这一半: 服务端生成它、设对 ID、同步过去,
/// 客户端那边的 <c>DeserializeGameObjectBase</c> 读到 ID 就能配上自己的点。
///
/// ## 位置不用我们设, 也设不了
///
/// <c>Position</c> 没有 setter。位置来自 <c>StorageRoomPoint._0023B</c>,
/// 而那个是 <c>GetTriggerPosition(event.ID)</c> —— 也就是同 ID 的
/// <c>Event_TriggerPosition</c> 的坐标。教程图是 (505.6, 3611.8),
/// 名字 <c>PCStrings.GUI.Storage.LifeGuard</c>。
/// </summary>
public static class StorageRoomSpawner
{
    public sealed record Result(int Spawned, int Points);

    /// <summary>
    /// 给地图上每个 <see cref="Event_StorageRoom"/> 放一个实体。开局调一次。
    ///
    /// **必须在 <c>LoadGameMode</c> 之后** —— 配对要 StaticMapObjects 里
    /// 已经有对应的 StorageRoomPoint。真机上 LoadGameMode 在地图加载时就跑完了,
    /// 我们在"客户端就绪"之后才调, 顺序天然满足。
    /// </summary>
    public static Result SpawnAll(GameManager manager)
    {
        if (manager?.MapObjects == null)
        {
            return new Result(0, 0);
        }

        int spawned = 0;
        int points = 0;

        // 按 ID 排, 下标 (IndexStorageRoom) 就是这个顺序 —— 客户端小地图也是按 ID 排序给图标编号的
        // (GameManagerClient.cs:2415-2426), 两边顺序一致看着整齐。
        foreach (Event_StorageRoom storage in manager.MapObjects.OfType<Event_StorageRoom>().OrderBy(s => s.ID))
        {
            if (Spawn(manager, storage, (byte)points))
            {
                spawned++;
            }

            points++;
        }

        return new Result(spawned, points);
    }

    private static bool Spawn(GameManager manager, Event_StorageRoom storage, byte index)
    {
        StorageRoom room = manager.GetGameObjectFromPool<StorageRoom>();
        if (room == null || room.IsDummyObject)
        {
            return false;
        }

        // ID 是配对的唯一依据, 而且它走 SerializeGameObjectBase 同步给客户端 ——
        // 两端各自拿它去找自己的 StorageRoomPoint。设在 Spawn 之前, 免得
        // 第一帧的同步发出去的是 0。
        room.ID = (ushort)storage.ID;

        // ## 下标必须由服务端给
        //
        // 客户端 HUD / 小地图拿 IndexStorageRoom 去查模式状态里的补给点数组 (ScavengerHunt._0023tvb),
        // 它随 StorageRoom.Serialize 下发 (:98), 而全树没有任何地方给它赋值 —— 服务端那半边的活。
        // 教程图只有一个点, 默认 0 恰好对; 拾荒图 4~6 个点全是 0 就全挤在第 0 格。
        room.IndexStorageRoom = index;

        manager.SpawnGameObject(room, null, null);

        return room.IsActive;
    }

    /// <summary>
    /// 场上有几个补给点**配对成功**了。
    ///
    /// 判据是 <c>StorageRoomPoint != null</c> 而不是"有几个实体" ——
    /// 没配上的实体位置是 (0,0), 存在等于没有, 而且正是它让箭头指到地图外。
    /// 配对在 <c>UpdateGameObject</c> 里做, 所以生成后要跑过一帧才为真。
    /// </summary>
    public static int CountPaired(GameManager manager)
    {
        if (manager?.ActiveGameObjectList == null)
        {
            return 0;
        }

        int paired = 0;
        List<GameObjectBase> objects = manager.ActiveGameObjectList;

        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is StorageRoom { IsActive: true } room && room.StorageRoomPoint != null)
            {
                paired++;
            }
        }

        return paired;
    }
}
