using System.Runtime.CompilerServices;

// 游戏那份 Lidgren 被 ILMerge 内部化过 —— 几乎所有类型都是 internal, 而且
// NetPeerConfiguration 的 Port / MaximumConnections 只有字段没有公开 setter
// (客户端是连出去的, 用不上)。服务端两样都要设。
//
// 开 InternalsVisibleTo 而不是去改反编译源码把它们改成 public: 后者会在每次
// 重新反编译后失效, 前者是我们自己的一行代码, 不碰生成物。
[assembly: InternalsVisibleTo("DIE.Server")]
[assembly: InternalsVisibleTo("DIE.Protocol.Tests")]
[assembly: InternalsVisibleTo("DIE.FakeClient")]   // 压测用的假客户端, 见 tests\DIE.FakeClient
