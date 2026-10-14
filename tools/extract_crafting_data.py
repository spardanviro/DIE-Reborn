"""
DIE_Reborn - 从 resources.assets 提取 GameCraftingData。

背景:
  客户端用 Resources.Load("GameCraftingData") 取这份数据 (CribStart.cs:521),
  也就是说它嵌在 Unity 的 resources.assets 里, 磁盘上的 Crafting\ 目录并没有它。
  服务端要能构造合法的武器/配件 (Unique / Gadget), 就必须拿到这份 schematic 表。
  它同时也是 Phase 3 战斗服的前置。

为什么不用 UnityPy/AssetStudio:
  这是 Unity 4.6 的老格式, 第三方库支持参差。而 TextAsset 的内存布局极简 ——
      int32 nameLength | name bytes | (4 字节对齐填充) | int32 scriptLength | script bytes
  直接按这个结构切出来即可, 零依赖、零版本风险。校验方式是解析结果必须是合法 XML。

用法:
    python extract_crafting_data.py [resources.assets 路径] [输出路径]
"""

import re
import struct
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

DEFAULT_ASSETS = Path(
    r"C:\Program Files (x86)\Steam\steamapps\common\Dead Island Epidemic"
    r"\Dead Island Epidemic - Crib_Data\resources.assets"
)
DEFAULT_OUT = Path(__file__).resolve().parent.parent / "data" / "GameCraftingData.xml"

ASSET_NAME = b"GameCraftingData"


def extract(assets_path: Path, asset_name: bytes) -> bytes:
    data = assets_path.read_bytes()

    hits = [m.start() for m in re.finditer(re.escape(asset_name), data)]
    if not hits:
        raise SystemExit(f"在 {assets_path.name} 里找不到 {asset_name.decode()}")
    if len(hits) > 1:
        print(f"[!] {asset_name.decode()} 出现 {len(hits)} 次, 逐个尝试")

    for name_off in hits:
        # m_Name 前面 4 字节是长度, 用它确认这是真正的对象而不是类型树里的字符串
        declared = struct.unpack_from("<I", data, name_off - 4)[0]
        if declared != len(asset_name):
            continue

        # m_Name 之后按 4 字节对齐, 再读 m_Script 的长度
        pos = name_off + len(asset_name)
        pos += (-pos) % 4
        script_len = struct.unpack_from("<I", data, pos)[0]
        pos += 4

        if script_len <= 0 or pos + script_len > len(data):
            continue

        payload = data[pos:pos + script_len]
        print(f"[*] 命中 offset={name_off} 长度={script_len:,} 字节")
        return payload

    raise SystemExit("找到了名字但布局不符, 可能是 Unity 版本差异")


def main() -> None:
    assets = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_ASSETS
    out = Path(sys.argv[2]) if len(sys.argv) > 2 else DEFAULT_OUT

    print(f"[*] 读取 {assets}")
    payload = extract(assets, ASSET_NAME)

    # 校验: 必须是能解析的 XML, 否则说明切偏了
    text = payload.decode("utf-8")
    root = ET.fromstring(text)
    print(f"[*] XML 解析成功, 根元素 <{root.tag}>")
    for child in root:
        print(f"      <{child.tag}> 共 {len(child)} 项")

    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_bytes(payload)
    print(f"[*] 已写出 {out} ({len(payload):,} 字节)")


if __name__ == "__main__":
    main()
