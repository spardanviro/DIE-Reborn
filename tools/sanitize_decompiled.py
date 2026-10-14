#!/usr/bin/env python3
"""把反编译树中编译不过的部分镜像到 obj\\ 下并就地修正。

## 处理的产物

ILSpy 解不开可选参数的默认值时会原样吐出一段注释:

    public void SendGameMessage(INetBuffer buf, IClient client = /*Read out of bounds.*/, ...)

不是合法 C# (CS1525)。StunGameNetwork + ConductorGameLogic 的 269 个文件里有 12 个中招,
共 51 处。

替换成 `= default`。判定依据: 出问题的**全部是引用类型参数** —— 同一批文件里
值类型的默认值 ILSpy 解得出来 (如 `float height = float.MinValue`)。
引用类型的 `default` 就是 null, 与原值一致。而且这些都是可选参数, 服务端调用时
一律显式传参, 取值本身也不影响行为。

## 为什么不用 _Fixups\\ 副本

12 个文件里包括 GameManager.cs (3206 行) 和 NetworkBase.cs (940 行)。为一个 token
抄几千行, 且每次重新反编译都要重抄一遍 —— 不可持续。

镜像出来的目录是**生成物** (在 obj\\ 下), 随时可重建, 所以"绝不手改反编译源码"
这条不变量仍然成立。

## 增量

内容没变就不写回, 保住 mtime, 避免每次构建触发全量重编译。
"""

import os
import re
import sys

ARTIFACT = "/*Read out of bounds.*/"
REPLACEMENT = "default"

# 引用**客户端专有程序集**的文件一律不镜像。
#
# 服务端跑在 net9 上, 这些依赖要么拿不到、要么没意义:
#   UnityEngine   Unity 4.6 的 x86 / .NET 3.5 程序集, 是原生播放器的托管封装,
#                 CoreCLR 根本加载不了
#   ManagedSteam  Steam API 封装 —— 战斗服不需要
#   FMODUnity     Unity 的 FMOD 绑定 (注意不能只写 "FMOD" —— 会误伤 GAME 树里
#                 纯 .NET 的 FMODMusic 命名空间, IMusicSystem 就在那里)
#   libgui        UI 库
#   RootMotion    动画 IK
#
# 这个过滤是**安全的**, 因为共享模拟层几乎不碰它们:
#   - StunGameNetwork + ConductorGameLogic 共 269 个文件, 依赖 Unity 的只有 1 个
#     (View 层的一个 UI 类), 依赖 ManagedSteam/FMOD 的也只有 1 个 (IMusicSystem)
#   - GAME 树整体 3144 个文件里约 640 个是客户端专有的, 剩下的是纯 .NET 的
#     共享模拟层 + 数据类型 —— 正是服务端要的那部分
#
# 万一过滤掉了服务端真正需要的类型, 表现是编译期 CS0246 —— 明确报错, 不会静默出问题。
CLIENT_ONLY_MARKERS = (
    "UnityEngine",
    "ManagedSteam",
    "using FMODUnity",
    "libgui",
    "RootMotion",
)

# 把 internal 一律放宽成 public。
#
# 这是混淆器留下的根本问题, 不是 ILSpy 的锅: SmartAssembly 在**编译之后**把类型
# 内部化, 而引用它们的成员仍然是 public。IL 层面不校验 C# 的可访问性规则, 所以
# 原始程序集没问题, 但反编译出来的源码不是合法 C# —— 表现为成片的
# CS0052 / CS0050 / CS0051 / CS0053 "可访问性不一致" (整树约 6000 处)。
#
# 放宽而不是收紧, 是因为收紧要逐个判断谁引用了谁, 而放宽在语义上是安全的:
# 可访问性只影响编译期检查, 不影响运行时行为或线格式。私服也不需要隐藏 API。
#
# `protected internal` 要保留 —— 换成 `protected public` 是语法错误。
INTERNAL_PATTERN = re.compile(r'(?<!protected )\binternal\b')

# 同样的问题还有一种形态: **嵌套类型**。
#
# 混淆器把编译器生成的闭包类等等留成 private 嵌套类型, 而外层的 public 字段
# 直接以它们为类型。IL 允许, C# 不允许 (CS0052)。
#
# 这里只放宽**类型声明**上的 private, 不动字段和方法 —— 后者数量巨大且没有必要,
# 放宽范围越小越好。
PRIVATE_TYPE_PATTERN = re.compile(
    r'\bprivate\b(?=\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+|readonly\s+|unsafe\s+|ref\s+)*'
    r'(?:class|struct|interface|enum|delegate)\b)')

# 去掉类声明上的 sealed。
#
# 又一处 IL 与 C# 的规则差异: 混淆/编译产物里有大量 `sealed` 类带着 `virtual` 成员
# (整树 116 处 CS0549 "sealed 类型中的新虚成员")。IL 层面 sealed 只是个标记,
# 虚方法表照样能有条目; C# 则直接判错。
#
# 去掉 sealed 而不是去掉 virtual: sealed 是设计约束和 JIT 优化提示, 去掉它
# 不改变任何行为; 而去掉 virtual 会改变方法分派方式, 有真实风险。
#
# 只匹配**类声明**上的 sealed —— 成员上的 `sealed override` 是另一回事, 必须保留。
SEALED_CLASS_PATTERN = re.compile(r'\bsealed\s+(?=(?:partial\s+)?class\b)')

# 接口里的属性被剥成了裸的 get_X() 方法。
#
# 混淆器去掉了接口的 property 元数据, 只留下带 [SpecialName] 的 getter 方法;
# 实现类那边 ILSpy 正常还原成了属性, 于是对不上:
#
#   interface INetworkManager { [SpecialName] int get_MaxClients(); }
#   class NetworkBase        { public int MaxClients { get; } }        // CS0686
#
# IL 里属性访问器本来就是名为 get_X 的方法, 所以原程序集没问题; C# 不允许用属性
# 去实现一个方法成员。
#
# **这个转换必须逐文件白名单, 不能全树套用。** 试过全树, 结果是另外两个接口
# (INetBuffer / IConsoleUI) 的实现类是**显式实现 get_X() 方法**的, 一改接口
# 就冒出 104 个 CS0470/CS0535/CS0683。也就是说同一个产物在不同文件里两侧形态不同,
# 只能按实际报错的文件来改。
SPECIALNAME_PROPERTY_FILES = {
    "StunGameNetwork/INetworkManager.cs",
}

SPECIALNAME_GETTER_PATTERN = re.compile(
    r'\[SpecialName\]\s*\n(\s*)([\w<>,\[\]\.]+)\s+get_(\w+)\(\);')


# 把接口那侧改成属性只做了一半 —— 调用点还是 `x.get_MaxClients()` 形态,
# 对属性这么写是 CS0571 ("无法显式调用访问器")。全树 61 处, 25 个文件。
#
# 所以调用点必须一起改。**名单从白名单文件里现读**, 不另外硬编码一份 ——
# 两边共用同一个来源, 就不会出现改了接口忘了调用点 (或反过来) 的漂移。
#
# 安全性: 只重写"上面那批文件实际转换掉的成员名", 不是所有 get_*()。
# 已核实这五个名字 (MaxClients / ActiveClientsList / ConnectedClientsList /
# NumOfSynchronizables / UseNetworkProfiling) 在全树只有 INetworkManager 一处声明,
# 不存在同名的其它方法会被误伤。
#
# 只匹配带接收者的形式 (`.get_X()`)。裸的 `get_X()` 全都是接口声明本身,
# 那些由 SPECIALNAME_GETTER_PATTERN 处理。
#
# 注意: 同一个脚本还被 DIE.Lidgren 等项目复用, 它们选的子目录里根本没有
# INetworkManager.cs。所以"不在本次选中的子集里"是正常的, 不能报错;
# 只有"在源树上也找不到"才说明上游真的变了。
def collect_specialname_members(src_root: str, sources: dict) -> set:
    names = set()
    for rel in SPECIALNAME_PROPERTY_FILES:
        if rel not in sources:
            abs_path = os.path.join(src_root, rel.replace("/", os.sep))
            if os.path.exists(abs_path):
                continue      # 本次没选这个子目录, 正常
            raise SystemExit(
                f"[sanitize] SPECIALNAME_PROPERTY_FILES 里的 {rel} 在源树上不存在 —— "
                f"上游改了目录结构, 请核对后更新名单。")
        found = {m.group(3) for m in SPECIALNAME_GETTER_PATTERN.finditer(sources[rel])}
        if not found:
            raise SystemExit(
                f"[sanitize] {rel} 里一个 [SpecialName] get_X() 都没匹配到 —— "
                f"上游变了。若真的不再需要转换, 请把它从 SPECIALNAME_PROPERTY_FILES 移除。")
        names |= found
    return names


def rewrite_specialname_callsites(text: str, names: set) -> str:
    if not names:
        return text
    pattern = r'\.get_(' + '|'.join(sorted(re.escape(n) for n in names)) + r')\(\)'
    return re.sub(pattern, r'.\1', text)

# 自动属性的 setter 用错了标识符 (CS0103)。
#
# ILSpy 在 setter 里没有沿用隐式参数名 `value`, 而是按**类型**做了启发式命名:
#
#   public int MaxPlayers {
#       [CompilerGenerated] get { return _0023iBe; }
#       [CompilerGenerated] private set { _0023iBe = num; }    // <- 应为 value
#   }
#
# 类型不同名字也不同: int/float -> num, bool -> flag, string -> text,
# List<T> -> list, 自定义类型 -> 该类型名的驼峰变体 (_00235ze -> _00235ze2)。
# 这是同一个缺陷的不同表现, 不是多个 bug。
#
# 只匹配**编译器生成的**访问器 (带 [CompilerGenerated]) 且函数体是单条赋值 ——
# 这正是自动属性 setter 的形状, 这种 setter 的右值只可能是 value, 无歧义。
# 手写的 setter 不带 [CompilerGenerated], 不受影响。
#
# 同一个缺陷在索引器 setter 上还有一种形态 (连索引参数也被改名了),
# 那个要读签名取参数名, 走 FILE_PATCHES。
AUTOPROP_SETTER_PATTERN = re.compile(
    r'(\[CompilerGenerated\]\n\s*(?:private |protected |internal |public )?set\n'
    r'(\s*)\{\n\s*[\w\.]+ = )(\w+)(;\n\2\})')


def fix_autoprop_setters(text: str) -> str:
    def fix(match: "re.Match[str]") -> str:
        if match.group(3) == "value":
            return match.group(0)
        return match.group(1) + "value" + match.group(4)

    return AUTOPROP_SETTER_PATTERN.sub(fix, text)


# `Tuple` 的歧义: StunCore 自带一份 net35 时代的 Tuple backport, 在 net9 上与
# System.Tuple 同时可见 (CS0104)。
#
# **一定是 StunCore 那份** —— 游戏编译时的目标框架是 .NET 3.5, 而 System.Tuple
# 是 .NET 4.0 才加进 BCL 的, 当年根本不存在。所以源码里所有裸的 Tuple 都指
# StunCore.Tuple, 没有第二种可能。
#
# 逐文件白名单: 只有这两个文件里的裸 Tuple 会撞 (其它地方要么带限定符,
# 要么所在文件没 using System)。
TUPLE_QUALIFY_FILES = {
    "ConductorGameLogic.GameModes/ScavengerHunt.cs",
    "ConductorGameLogic.GameModes/TeamvsTeam.cs",
}
TUPLE_PATTERN = re.compile(r'(?<![\w.])Tuple<')

# 枚举里出现同名成员 (CS0102)。
#
# 这不是 ILSpy 的问题, 是**反反编译手段**: 混淆器故意把同一个枚举里的两个成员
# 改成同名。IL 里同一类型不允许两个同名字段, 所以这种程序集本身就是畸形的 ——
# 运行时按序号取值不受影响, 但任何反编译器都还原不出合法源码。
#
# 处理: 给重复出现的成员加后缀。**这不影响行为** —— 枚举成员的值由声明顺序/
# 显式赋值决定, 与名字无关; 而且按名字引用它的代码本来就是有歧义的, 编译器
# 也只能绑到第一个。
DUPLICATE_ENUM_MEMBER_SCAN = re.compile(
    r'enum\s+\w+[^{]*\{([^}]*)\}', re.DOTALL)


def fix_duplicate_enum_members(text: str) -> str:
    """把枚举体内重名的成员加后缀去重。"""
    def fix_body(match: "re.Match[str]") -> str:
        body = match.group(1)
        seen: dict[str, int] = {}
        lines = []
        for line in body.split("\n"):
            stripped = line.strip().rstrip(",")
            # 只处理"纯成员名"的行, 跳过带 = 赋值、特性、注释、空行
            if (stripped and stripped.replace("_", "").isalnum()
                    and not stripped.startswith("//")):
                count = seen.get(stripped, 0)
                seen[stripped] = count + 1
                if count:
                    line = line.replace(stripped, f"{stripped}_dup{count}", 1)
            lines.append(line)
        return match.group(0).replace(body, "\n".join(lines))

    return DUPLICATE_ENUM_MEMBER_SCAN.sub(fix_body, text)


# 逐文件的精确补丁。
#
# 上面那些规则都是全树/白名单的模式匹配, 这里放**一次性的、必须逐字对照**的修改。
# 每条是 (相对路径, 原文, 替换文, 为什么安全)。
#
# 与模式规则的区别: 这些必须**恰好匹配一次**, 否则脚本报错退出 —— 重新反编译后
# 若上游变了会立刻暴露, 不会静默失效。
FILE_PATCHES = [
    (
        "-6/-jtc.cs",
        # 泛型方法的形参名和类型参数名撞了 (CS0412)。混淆器给两者分配了同一个名字,
        # IL 里类型参数和形参在不同的表里所以合法, C# 不允许。
        # 只改形参名, 类型参数保持不动。使用点只有一处 (:270), 一并改。
        "Func<int, _0023P8, _0023Q8, bool> _0023Etc, _0023P8 _0023P8, _0023Q8 _0023Q8)",
        "Func<int, _0023P8, _0023Q8, bool> _0023Etc, _0023P8 arg_P8, _0023Q8 arg_Q8)",
        "形参改名, 不影响类型参数; 唯一使用点同步修改",
    ),
    (
        "-6/-jtc.cs",
        "if (!_0023Etc(item._0023vdc._0023Rtc, _0023P8, _0023Q8))",
        "if (!_0023Etc(item._0023vdc._0023Rtc, arg_P8, arg_Q8))",
        "上一条改名的唯一使用点",
    ),
    (
        "-x3d/-g4d.cs",
        # 只有 operator == 没有 operator != (CS0216)。IL 不要求成对, C# 要求。
        # 补一个取反的实现 —— 与 == 语义严格互补, 无歧义。
        # 同样的形态在 GameProtocol\MailData.cs 也出现过 (那边用的是 _Fixups 副本)。
        "\tpublic static bool operator ==(_0023g4d P_0, _0023g4d P_1)\n",
        "\tpublic static bool operator !=(_0023g4d P_0, _0023g4d P_1)\n"
        "\t{\n"
        "\t\treturn !(P_0 == P_1);\n"
        "\t}\n"
        "\n"
        "\tpublic static bool operator ==(_0023g4d P_0, _0023g4d P_1)\n",
        "补上缺失的 != , 定义为 == 的取反",
    ),
    (
        "Stunlock.Console.GameEngineImplementation/MatchingSuggestion.cs",
        # CS0686: 属性的 getter 不能实现接口的**方法**。
        #
        # 成因: SmartAssembly 把 IConsoleSuggestion 的属性元数据剥掉了, IL 里只剩
        # [SpecialName] get_BoundCommand() 这样的裸方法; 而 MatchingSuggestion 这边
        # 元数据完好, ILSpy 还原成了属性。IL 里同一个方法既是属性 getter 又填接口槽,
        # C# 表达不了这种双重身份。
        #
        # 为什么不去改接口 (即 SPECIALNAME_PROPERTY_FILES 那条路):
        # 把 IConsoleSuggestion 的 get_X() 还原成属性, 会连带炸掉
        #   - 其它用显式 get_X() 方法实现该接口的类 (CS0535)
        #   - 全树所有 `x.get_SuggestionText()` 形式的调用点 (CS0571)
        # 上次全树套用这条规则就是这么炸出 104 个错的。
        #
        # 这里改实现侧, 纯追加: 显式接口实现填槽, 公开属性原样保留。
        # 两者都转发给 Suggestion, 与 IL 行为一致。
        "\tpublic MatchingSuggestion(string baseText, int baseTextSuggestionStartOffset, IConsoleSuggestion suggestion)\n",
        "\t_0023iPg IConsoleSuggestion.get_BoundCommand()\n"
        "\t{\n"
        "\t\treturn Suggestion.get_BoundCommand();\n"
        "\t}\n"
        "\n"
        "\tstring IConsoleSuggestion.get_SuggestionText()\n"
        "\t{\n"
        "\t\treturn Suggestion.get_SuggestionText();\n"
        "\t}\n"
        "\n"
        "\tstring IConsoleSuggestion.get_SuggestionDescription()\n"
        "\t{\n"
        "\t\treturn Suggestion.get_SuggestionDescription();\n"
        "\t}\n"
        "\n"
        "\tpublic MatchingSuggestion(string baseText, int baseTextSuggestionStartOffset, IConsoleSuggestion suggestion)\n",
        "显式接口实现填 get_X() 槽, 公开属性保留; 不动接口侧以免波及所有调用点",
    ),

    # ------------------------------------------------------------------
    # CS0102: 同一个类里两个字段重名。
    #
    # IL 用 token 区分字段, 名字重复合法; C# 不允许。这是 SmartAssembly 的
    # 反反编译手段, 不是 ILSpy 的缺陷。
    #
    # **判据一律来自 IL, 不靠猜** —— 这些字段住在 GameManager.BalanceData 里,
    # 猜错不会报错, 只会让数值静默改变。两种取证手段:
    #
    #   1) 类型不同时, ilspycmd -il 的字段引用自带签名, 直接读出来:
    #        ldsfld float32 '#iib.#GNb'::'#7q'
    #   2) 类型也相同时 (#Tdg.#Sdg 的两个 int64), IL 文本对二者完全一样,
    #      只能看 token -> tools/ILFieldRefs
    #
    # 改名规则: 保留原名, 加类型/序号后缀。**只改重名的那一个**, 另一个不动,
    # 这样绝大多数使用点不需要跟着改, diff 最小。
    # ------------------------------------------------------------------
    (
        "-iib/-GNb.cs",
        # #iib.#GNb 里 '#7q' 有两个:
        #   const int 12    —— 挤在一堆 const float 中间, 是设计期默认值表的一员。
        #                      const 在 IL 里是 static literal, 使用点全被内联,
        #                      所以它在全程序集**零** IL 引用 (这是正常的, 不是没人用)。
        #   static float 6f —— 挤在一堆 static float[] 中间, 是算 #O3b 的除数。
        #
        # ilspycmd -il 显示 GNb 内对 '#7q' 的 6 处引用全是 float32:
        #   .cctor 里 1 处 stsfld (存 6f) + 4 处 ldsfld (Math.Round 的除数) + 别处 1 处。
        # 所以下面这个初始化块里的 4 个 _00237q 全是 float, 与 const int 无关。
        "\tpublic static float _00237q = 6f;\n"
        "\n"
        "\tpublic static float[] _0023O3b = new float[4]\n"
        "\t{\n"
        "\t\t(float)Math.Round(_0023GG[0] / _00237q, 2),\n"
        "\t\t(float)Math.Round(_0023GG[1] / _00237q, 2),\n"
        "\t\t(float)Math.Round(_0023GG[2] / _00237q, 2),\n"
        "\t\t(float)Math.Round(_0023GG[3] / _00237q, 2)\n"
        "\t};\n",
        "\tpublic static float _00237q__f32 = 6f;\n"
        "\n"
        "\tpublic static float[] _0023O3b = new float[4]\n"
        "\t{\n"
        "\t\t(float)Math.Round(_0023GG[0] / _00237q__f32, 2),\n"
        "\t\t(float)Math.Round(_0023GG[1] / _00237q__f32, 2),\n"
        "\t\t(float)Math.Round(_0023GG[2] / _00237q__f32, 2),\n"
        "\t\t(float)Math.Round(_0023GG[3] / _00237q__f32, 2)\n"
        "\t};\n",
        "static float 的那个改名为 __f32; const int 保持原名 (它零 IL 引用, 改了反而扩大 diff)",
    ),
    (
        "-cL/-m3f.cs",
        # #cL.#m3f 同样有 int 和 float 两个 '#7q'。IL:
        #   ldsfld float32 '#iib.#GNb'::'#7q'  ->  stfld float32 '#cL.#m3f'::'#7q'
        # 即这一行是"用 GNb 的 float (6f) 初始化 m3f 的 float", 两侧都是 float。
        "\tpublic float _00237q = _0023GNb._00237q;\n",
        "\tpublic float _00237q__f32 = _0023GNb._00237q__f32;\n",
        "m3f 的 float 侧改名, 并跟上 GNb 那边的改名; int 侧 (= 12) 不动",
    ),
    (
        "ConductorGameLogic.Gameplay/AbilityManager.cs",
        # 这个 switch 是"运营下发的数值热更"分发表, case 顺序和枚举 #cL.#cQb 的
        # 成员顺序严格 1:1。枚举里 '#7q' 也重名, sanitizer 的枚举去重已把第二个
        # 改成了 _00237q_dup1 —— 但**只改了声明侧, 没改使用侧**, 于是这里留下两个
        # 相同的 case 标签 (潜伏的 CS0152, 目前被 CS0102 挡着没报出来)。
        #
        # 谁是谁由两条独立证据交叉确认:
        #   - 枚举顺序: ... FAb(14), 7q_dup1(15), O3b(16) 对上 switch 的排列
        #   - 反序列化方式: ToInt32 配 int 字段, ToSingle 配 float 字段
        "\t\t\t\tcase _0023cQb._00237q:\n"
        "\t\t\t\t\t_0023m3f._00237q = BitConverter.ToSingle(gameplayChange.Value, 0);\n",
        "\t\t\t\tcase _0023cQb._00237q_dup1:\n"
        "\t\t\t\t\t_0023m3f._00237q__f32 = BitConverter.ToSingle(gameplayChange.Value, 0);\n",
        "第二个 case 走 float 字段, 同时补上枚举去重漏掉的使用侧改名",
    ),
    (
        "-Tdg/-Sdg.cs",
        # #Tdg.#Sdg 里两个 '#8Ae' **类型也一样** (都是 int64), IL 文本无法区分,
        # 只能看 token。tools/ILFieldRefs 的结论:
        #   [#6]  声明在 L93,  用于 Connect (清零) 和 #jde (超时算差值)
        #   [#25] 声明在 L131, 只用于 Update: = #qdg.ElapsedMilliseconds
        # 和字段位置吻合 —— #6 挨着 Stopwatch #bdg, #25 挨着 Stopwatch #qdg。
        #
        # 改 #25 (使用点只有 1 处)。前面那行 Stopwatch 声明是用来定位的,
        # 因为 "private long _00238Ae;" 本身出现两次。
        "\tprivate Stopwatch _0023qdg = new Stopwatch();\n"
        "\n"
        "\tprivate long _00238Ae;\n",
        "\tprivate Stopwatch _0023qdg = new Stopwatch();\n"
        "\n"
        "\tprivate long _00238Ae__2;\n",
        "改使用点较少的那个 (#25); 靠前面的 Stopwatch 声明定位, 因为字段声明行本身重复",
    ),
    (
        "-Tdg/-Sdg.cs",
        "\t\t\t_00238Ae = _0023qdg.ElapsedMilliseconds;\n",
        "\t\t\t_00238Ae__2 = _0023qdg.ElapsedMilliseconds;\n",
        "上一条改名的唯一使用点 (Update 内, IL_018b stfld)",
    ),

    # ------------------------------------------------------------------
    # 索引器 setter 里的标识符全错了 (CS0103)。
    #
    # 和 fix_autoprop_setters 处理的是同一个 ILSpy 缺陷 —— setter 里不沿用声明的
    # 参数名, 改按类型启发式命名。索引器上错得更彻底: 索引参数 P_0 变成了 num,
    # 隐式的 value 变成了 val。
    #
    # 这两处不能走通用规则, 因为索引参数的正确名字得从签名里读。**getter 就是
    # 对照组** —— 同一个索引器的 getter 里 P_0 用得好好的 (`return P_0 switch`),
    # 而且 case 分支到字段的映射两边完全一致, 所以改法唯一。
    #
    # 同一形态在 Lidgren 那边也出现过, 见 src/DIE.Lidgren/_Fixups/NetBitVector.cs
    # (那里是 `Set(num, flag)` 应为 `Set(P_0, value)`)。
    # ------------------------------------------------------------------
    (
        "-EK/-Zuc.cs",
        # 放开基地炮塔的"当前射击间隔"。
        #
        # 原厂夜里给太阳能炮塔降威力的办法是**射得慢**: UpdateDestructible (:293-298) 里
        #     float num = _0023SJ;  if (!flag) num = _0023SJ * _0023Uuc;   // _0023Uuc = 1.5
        # 而 flag 只在"拾荒 + 新版终局 + 已入夜"时为 false。十字路口也有夜袭、也有基地炮塔
        # (大厅指引 "Solar powered turrets are less powerful during night" 说的是所有太阳能炮塔),
        # 但那一支判不到它。服务端要在十字路口入夜时照同样的倍率放慢, 只能直接改间隔 ——
        # _0023SJ 在 SpawnDestructible 时从公开的 _0023yJ 拷一份, 之后就只读它。
        "\tprivate float _0023SJ;\n",
        "\tpublic float _0023SJ;\n",
        "放开炮塔当前射击间隔; 十字路口夜里按原厂拾荒的倍率放慢, 原厂那一支只判拾荒",
    ),
    (
        "ConductorGameLogic.GameModes/GameMode.cs",
        # 放开"对局已开始"标志。
        #
        # _Started 在整个客户端程序集里只有两处写入:
        #     :862   _Started = false          (Reset)
        #     :1555  _Started = ReadBool()     (Deserialize, 从服务端收)
        # **没有任何地方把它置 true** —— 那个状态转换属于服务端, 随之丢失。
        #
        # 相关的钩子也都是空的: GameMode.Start() / EndMatch() / ClientReady() 全是
        # `public virtual void X() { }`, 实现在没发布的那半边。倒计时逻辑
        # UpdateGameCountDown 倒是幸存了 (开头就是 if (!IsServer) return false),
        # 但它归零时调的正是那个空的 Start()。
        #
        # 所以服务端必须自己置位。放宽字段而不是加方法, 是因为读侧
        # (IsStarted / Serialize) 都还在, 只缺一个写入口。
        "\tprotected bool _Started;\n",
        "\tpublic bool _Started;\n",
        "放开对局开始标志; 该状态转换的原实现随服务端丢失, 只缺写入口",
    ),
    (
        "ConductorGameLogic.Entities/NPC.cs",
        # 放开"待生效的出场状态"。
        #
        # NPC.SpawnCharacter() (:363) 在生成时把 _00232L 取出来赋给
        # SpawnState_Current, 那个 setter 触发 _00233M() 播出场动画
        # (从地里爬出来、坐起来、翻墙……)。
        #
        # 也就是说**出场动画必须在 Spawn() 之前写进 _00232L**。而它是 private
        # 且没有任何公开写入口 —— 设它的是服务端, 随服务端丢失。
        #
        # 不设的后果: 僵尸凭空出现在地上, 没有任何出场表现。
        "\tprivate _0023WM _00232L;\n",
        "\tpublic _0023WM _00232L;\n",
        "放开僵尸的待生效出场状态; 写它的是服务端, 而 SpawnCharacter 只在生成时读一次",
    ),
    (
        "-bd/-5cc.cs",
        # 放开教程游戏模式的状态数据。
        #
        # _0023l9 是教程的**被复制状态** (_0023wcc 结构体), 里面的 _0023pcc 是
        # 教程进行到第几步。客户端只读它:
        #     DeserializeGameMode (:741) 从服务端收, 变了就调 _0023E9() 做反应
        #     _0023E9 (:296) 按 _0023pcc 的新值播语音、显示引导文字、发武器
        # **没有任何地方推进它** —— 推进是服务端的活, 随 _00235cc 那个没发布的
        # 子类一起丢了 (壳还在: _0023Rcc/_0023Scc/_0023Tcc/_0023Ucc 四个空 virtual)。
        #
        # 为什么不用 GameModeData 属性: 它是 `=> _0023l9`, 而 _0023wcc 是 **struct**,
        # 返回的是副本, 改副本对字段没有任何影响。必须直接写字段。
        #
        # 这一步卡着两件事: 按 SPACE 跳过开场 (客户端要等状态变成 _0023ecc 才动),
        # 以及教程里的武器发放 (Player.cs:1867, 按 _0023pcc 的阈值给)。
        "\tprotected _0023wcc _0023l9;\n",
        "\tpublic _0023wcc _0023l9;\n",
        "放开教程状态数据; 推进它的原实现随服务端丢失, 且属性返回的是结构体副本改不动",
    ),
    (
        "ConductorGameLogic.GameModes/ScavengerHunt.cs",
        # 放开拾荒模式的状态数据, 理由和上面教程那条一样。
        #
        # _0023l9 (_0023Ctb 结构体) 里是每队的阶段 / 路障 / 夜晚 / 补给点 / 动态事件,
        # 五个 GameModeDataSynchronizer 从它序列化。客户端只在 _0023E9 里读它做反应,
        # 推进它的是服务端 (ScavengerHunt 那几个空 virtual: _0023Wub/_0023Xub/_0023Yub/_0023kvb)。
        # GameModeData 属性返回结构体副本, 标量字段 (_0023ctb 夜晚 / _0023dtb 快赢的队 /
        # _0023atb 补给点数) 改副本不生效。
        "\tprotected _0023Ctb _0023l9;\n",
        "\tpublic _0023Ctb _0023l9;\n",
        "放开拾荒模式状态数据; 推进它的原实现随服务端丢失, 且属性返回的是结构体副本改不动",
    ),
    (
        "StunGameNetwork/SynchronizableData.cs",
        # 放开"这个同步对象对某客户端是否激活"的查询。
        #
        # 服务端写出站帧时必须按客户端逐个判断激活状态 —— 激活位图 _0023cDe 和它的
        # 查询方法 _0023pDe 都是 private, 因为**客户端从不需要问这个问题**
        # (它只接收服务端发来的东西)。判断激活是服务端的活, 而那半边没发布。
        #
        # 只放宽这一个**纯读**方法, 不动位图本身: 写入仍然只能走 _00235Ce(index, bool),
        # 那是原厂的公开入口, 里面还带着激活/去激活时的额外簿记 (:172 / :189)。
        # 直接改位图会跳过那些簿记。
        "\tprivate bool _0023pDe(int P_0)\n",
        "\tpublic bool _0023pDe(int P_0)\n",
        "只放宽激活状态的纯读方法; 写入仍走原厂入口 _00235Ce, 以免跳过其中的簿记",
    ),
    (
        "StunGameNetwork/ISynchronizable.cs",
        # 接口上只剩 Deserialize —— Serialize 被裁掉了。
        #
        # 不是混淆, 是**链接器裁剪**: 客户端只反序列化世界状态, 从不序列化它,
        # 所以 ISynchronizable.Serialize 全程序集零调用点, 被当作死代码去掉了。
        # (同理服务端那半边整个没发布, 见 GameManagerServer / GameServer 的注释。)
        #
        # 而实现类上的方法**都还在** —— 它们被别的路径引用着。六个直接实现者
        # (GameObjectBase / GameMode / AbilityBar / SCTSynchronizer / Supplies / -Jmd)
        # 的签名完全一致: void Serialize(INetBuffer, IClient)。
        #
        # 把成员补回接口, 服务端就能对 _0023Zye 多态调用, 不必写一个按类型分派的
        # switch (那种 switch 会在漏掉某个实现时**静默**少发数据)。
        #
        # 安全性由编译器保证: 任何一个实现者缺这个方法都会 CS0535 编译失败,
        # 不会静默漂移。
        "\tvoid Deserialize(INetBuffer P_0);\n",
        "\tvoid Serialize(INetBuffer P_0, IClient P_1);\n"
        "\n"
        "\tvoid Deserialize(INetBuffer P_0);\n",
        "把链接器裁掉的 Serialize 补回接口; 六个实现者签名一致, 编译器验证完整性",
    ),
    (
        "-x3d/-ecg.cs",
        "\t\t\tswitch (num)\n"
        "\t\t\t{\n"
        "\t\t\tcase 0:\n"
        "\t\t\t\t_0023u3d = val;\n"
        "\t\t\t\tbreak;\n"
        "\t\t\tcase 1:\n"
        "\t\t\t\t_0023v3d = val;\n"
        "\t\t\t\tbreak;\n"
        "\t\t\tcase 2:\n"
        "\t\t\t\t_0023dcg = val;\n"
        "\t\t\t\tbreak;\n",
        "\t\t\tswitch (P_0)\n"
        "\t\t\t{\n"
        "\t\t\tcase 0:\n"
        "\t\t\t\t_0023u3d = value;\n"
        "\t\t\t\tbreak;\n"
        "\t\t\tcase 1:\n"
        "\t\t\t\t_0023v3d = value;\n"
        "\t\t\t\tbreak;\n"
        "\t\t\tcase 2:\n"
        "\t\t\t\t_0023dcg = value;\n"
        "\t\t\t\tbreak;\n",
        "num -> P_0 (签名是 this[int P_0], getter 里也是 P_0), val -> value; case 到字段的映射与 getter 一致",
    ),
    (
        "-x3d/-w3d.cs",
        "\t\t\tswitch (num)\n"
        "\t\t\t{\n"
        "\t\t\tcase 0:\n"
        "\t\t\t\t_0023u3d = val;\n"
        "\t\t\t\tbreak;\n"
        "\t\t\tcase 1:\n"
        "\t\t\t\t_0023v3d = val;\n"
        "\t\t\t\tbreak;\n",
        "\t\t\tswitch (P_0)\n"
        "\t\t\t{\n"
        "\t\t\tcase 0:\n"
        "\t\t\t\t_0023u3d = value;\n"
        "\t\t\t\tbreak;\n"
        "\t\t\tcase 1:\n"
        "\t\t\t\t_0023v3d = value;\n"
        "\t\t\t\tbreak;\n",
        "同上, 两个分量的版本",
    ),
]


def apply_file_patches(rel_posix: str, text: str) -> str:
    """套用某个文件的一次性补丁; 匹配数不为 1 就直接失败。"""
    for path, old, new, why in FILE_PATCHES:
        if path != rel_posix:
            continue
        if new in text:
            continue          # 已经打过 (增量镜像时可能发生)
        count = text.count(old)
        if count != 1:
            raise SystemExit(
                f"[sanitize] {rel_posix}: 补丁期望恰好匹配 1 处, 实际 {count} 处 —— "
                f"上游变了, 请核对后更新 FILE_PATCHES。理由: {why}")
        text = text.replace(old, new)
    return text


# 传递过滤: 引用了客户端专有类型的文件, 也算客户端专有。
#
# CLIENT_ONLY_MARKERS 是**一趟**扫描, 只认 using。但客户端代码是分层的 ——
# 调试控制台的命令处理器不 using UnityEngine, 它只是引用了 Stunlock.Console 的类型;
# 而引用那些命令处理器的文件又更外层。手工列举这个闭包不现实 (试过, 见
# gamelogic_exclude.txt 的说明)。
#
# 这里从种子集 (按 using 判定的客户端文件) 出发, 反复把"引用了已标记类型"的文件
# 也标记掉, 直到不动为止。
#
# **与之前失败的自动剪枝的本质区别**: 判据是"引用了明确的客户端类型",
# 不是"编译不过"。后者会把 GameManager 这种核心也吃掉 —— 只因为它引用的
# IMusicSystem 被误过滤了。
# **默认关闭。** 实测正则按类型名传播太粗: 混淆名很短 (_0023P8 之类), 跨簇误撞,
# 结果把 GameManager / NetworkBase 这些核心也标记了, 被下面的守卫拦下。
#
# 要让它可用, 得换成**真实的引用图**而不是正则名字匹配 —— 项目根目录有 codegraph
# 索引 (.codegraph/), 它有精确的 references 边。那是下次的活。
#
# 留着代码和守卫是因为思路本身没问题, 错的只是"名字匹配"这个近似。
TRANSITIVE_SEED_NAMESPACES = ()

# 绝不允许被剔除的文件。算法要是把它们标记了, 直接报错退出 ——
# 这是把上次"构建变绿但内容被掏空"的教训编进代码。
GUARDED_FILES = (
    "ConductorGameLogic/GameManager.cs",
    "ConductorGameLogic/Client.cs",
    "ConductorGameLogic.Entities/GameObjectBase.cs",
    "StunGameNetwork/NetworkBase.cs",
    "StunGameNetwork/GameClient.cs",
    "StunGameNetwork/LidgrenNetBuffer.cs",
)

TYPE_DECL_PATTERN = re.compile(
    r'\b(?:class|struct|interface|enum|delegate)\s+([A-Za-z_][A-Za-z0-9_]*)')


def compute_transitive_client_files(sources: dict) -> set:
    """从种子出发, 迭代标记引用了客户端专有类型的文件。

    sources: {相对路径 -> 文件内容}
    """
    if not TRANSITIVE_SEED_NAMESPACES:
        return set()

    marked = {rel for rel, text in sources.items()
              if any(f"using {ns}" in text for ns in TRANSITIVE_SEED_NAMESPACES)}

    for _ in range(10):                      # 收敛得很快, 10 轮是安全上限
        # 已标记文件里声明的类型名
        client_types = set()
        for rel in marked:
            client_types.update(TYPE_DECL_PATTERN.findall(sources[rel]))
        if not client_types:
            break

        pattern = re.compile(r'\b(?:' + "|".join(map(re.escape, client_types)) + r')\b')
        newly = {rel for rel, text in sources.items()
                 if rel not in marked and pattern.search(text)}
        if not newly:
            break
        marked |= newly

    hit_guards = [g for g in GUARDED_FILES if g in marked]
    if hit_guards:
        raise SystemExit(
            "[sanitize] 传递过滤把受保护的核心文件标记成了客户端专有, 已中止:\n  "
            + "\n  ".join(hit_guards)
            + "\n这说明种子或传播规则有问题 —— 别放宽守卫清单, 去查为什么。")

    return marked


def load_exclusions(dst_root: str) -> set[str]:
    """读取排除清单 (相对路径, 正斜杠, 每行一个)。

    清单由 tools\\prune_gamelogic.sh 迭代生成 —— 标记的是"客户端专有、服务端编不动"
    的文件。按 using 里的标记做的一层过滤抓不到传递依赖 (某文件不 using UnityEngine,
    却引用了被过滤掉的 GUI_ 类型), 那部分只能靠编译器判定。
    """
    path = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                        "tools", "gamelogic_exclude.txt")
    if not os.path.isfile(path):
        return set()

    with open(path, "r", encoding="utf-8") as f:
        return {line.strip() for line in f if line.strip() and not line.startswith("#")}



def iter_sources(src_root: str, subdirs: list) -> dict:
    """收集候选源文件 {相对路径(正斜杠) -> 原文}, 已排除清单和客户端 marker 命中的。"""
    exclusions = load_exclusions(src_root)
    sources = {}

    for subdir in subdirs:
        # 四种条目形式:
        #   "**"      整棵树 (递归)
        #   "."       只取根目录本身的文件 (不递归)
        #   "xxx.cs"  单个文件 —— 根目录 595 个文件里只有个别几个是共享层要的
        #   其它      该子目录递归
        if subdir.endswith(".cs"):
            path = os.path.join(src_root, subdir)
            if not os.path.isfile(path):
                print(f"  [警告] 源文件不存在, 跳过: {subdir}", file=sys.stderr)
                continue
            walker = [(os.path.dirname(path), [], [os.path.basename(subdir)])]
        else:
            src_dir = src_root if subdir in (".", "**") else os.path.join(src_root, subdir)
            if not os.path.isdir(src_dir):
                print(f"  [警告] 源目录不存在, 跳过: {subdir}", file=sys.stderr)
                continue
            walker = ([(src_dir, [], os.listdir(src_dir))] if subdir == "."
                      else os.walk(src_dir))

        for dirpath, _, filenames in walker:
            for name in filenames:
                if not name.endswith(".cs"):
                    continue
                path = os.path.join(dirpath, name)
                if not os.path.isfile(path):
                    continue

                rel = os.path.relpath(path, src_root).replace(os.sep, "/")
                if rel in exclusions:
                    continue

                with open(path, "r", encoding="utf-8-sig") as f:
                    text = f.read()

                if any(marker in text for marker in CLIENT_ONLY_MARKERS):
                    continue

                sources[rel] = text

    return sources


def sanitize(rel_posix: str, text: str, specialname_members: set) -> str:
    """套用全部清洗规则。"""
    text = text.replace(ARTIFACT, REPLACEMENT)
    text = INTERNAL_PATTERN.sub("public", text)
    text = PRIVATE_TYPE_PATTERN.sub("public", text)
    text = SEALED_CLASS_PATTERN.sub("", text)
    if rel_posix in SPECIALNAME_PROPERTY_FILES:
        text = SPECIALNAME_GETTER_PATTERN.sub(r'\1\2 \3 { get; }', text)
    text = rewrite_specialname_callsites(text, specialname_members)
    text = fix_autoprop_setters(text)
    if rel_posix in TUPLE_QUALIFY_FILES:
        text = TUPLE_PATTERN.sub("StunCore.Tuple<", text)
    text = fix_duplicate_enum_members(text)
    text = apply_file_patches(rel_posix, text)
    return text


def mirror(src_root: str, dst_root: str, subdirs: list) -> tuple:
    sources = iter_sources(src_root, subdirs)

    # 传递过滤要看到全集才能算, 所以必须先收集再判定
    client_only = compute_transitive_client_files(sources)

    # 调用点改写要用到接口那侧转换掉的成员名, 同样得先看到源文件
    specialname_members = collect_specialname_members(src_root, sources)

    written = 0
    patched = 0

    for rel_posix, original in sources.items():
        dst_path = os.path.join(dst_root, rel_posix.replace("/", os.sep))

        if rel_posix in client_only:
            if os.path.exists(dst_path):
                os.remove(dst_path)
            continue

        text = sanitize(rel_posix, original, specialname_members)
        if text != original:
            patched += 1

        # 内容没变就不写 —— 保住 mtime, 让 MSBuild 的增量判断继续有效
        if os.path.exists(dst_path):
            with open(dst_path, "r", encoding="utf-8") as f:
                if f.read() == text:
                    continue

        os.makedirs(os.path.dirname(dst_path), exist_ok=True)
        with open(dst_path, "w", encoding="utf-8") as f:
            f.write(text)
        written += 1

    return written, patched, len(client_only)

def main() -> int:
    if len(sys.argv) < 4:
        print("用法: sanitize_decompiled.py <反编译树根> <输出根> <子目录> [<子目录> ...]",
              file=sys.stderr)
        return 2

    src_root = sys.argv[1].rstrip("\\/")
    dst_root = sys.argv[2].rstrip("\\/")
    subdirs = sys.argv[3:]

    if not os.path.isdir(src_root):
        print(f"反编译树不存在: {src_root}", file=sys.stderr)
        return 1

    written, patched, skipped = mirror(src_root, dst_root, subdirs)
    print(f"[sanitize] 写入 {written} 个, 修正 {patched} 个, 因依赖 Unity 跳过 {skipped} 个")
    return 0


if __name__ == "__main__":
    sys.exit(main())
