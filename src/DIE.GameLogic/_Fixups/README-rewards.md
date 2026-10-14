# PlayerRewards.cs / RewardCollection.cs

这两个文件**只存在于大厅那棵树** (`Dead Island Epidemic - Crib_Data_SourceCode/
ConductorGameLogic/`), 游戏树里没有 —— 因为游戏客户端拿到 `GameMessageType.Rewards`
之后只是 `File.WriteAllBytes("lastRewards.data", bytes)` 原样存盘
(GameManagerClient.cs:933), **不解析**; 解析它的是大厅进程。

服务端要发这条消息, 就得有写侧。按项目惯例 (不手改反编译树) 在这里放副本。

## 改了什么

只改了一处, 全文替换:

    _0023rye._0023Yze  ->  StunGameNetwork.INetBuffer

大厅树里缓冲区接口的混淆名是 `_0023Yze`, 游戏树里是 `_00237ze`/`INetBuffer`。
两者的 `Write(...)` 重载逐条一致 (比对过 `-rye/-Yze.cs` 和
`StunGameNetwork/INetBuffer.cs`), 换个名字就能编。

除此之外一个字节没动 —— 报文格式仍然是原厂的 `_0023NKs` (Version 9) 写的。
