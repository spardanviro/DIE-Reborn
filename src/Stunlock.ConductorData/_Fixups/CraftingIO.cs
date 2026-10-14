// [DIE_Reborn FIXUP] 本文件是 ConductorCrafting_SourceCode\ConductorCrafting$f.cs 的副本。
// 唯一改动: 把裸写的 Tuple<...> 全部改为完全限定的 StunCore.Tuple<...>。
// 原因: 本文件同时 using System 和 using StunCore, 两者都有 Tuple<,>, 在 net9 上触发 CS0104
//       (net35 时代 System.Tuple 尚不存在, 所以原本不冲突)。
// 不能像 SpinWait 那样删掉 backport: StunCore.Tuple 是带公开可变字段的 struct, 调用点用了
//       对象初始化器语法 (new Tuple<string,float>{ Item1 = ... }), 而 System.Tuple 是只读属性的 class。
// 也不能用 using 别名: C# 不允许为开放泛型起别名。
// 重新反编译 ConductorCrafting.dll 后需重新套用。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using StunCore;

namespace ConductorCrafting;

public static class CraftingIO
{
	public static void WriteCraftingData(CraftingData data, Stream stream)
	{
		XmlTextWriter writer = new XmlTextWriter(stream, Encoding.UTF8);
		writer.WriteStartDocument();
		writer.WriteStartElement("root");
		WriteArray(writer, "Weapons", data.Weapons, delegate(StructuredWeapon weapon)
		{
			WriteWeapon(writer, weapon, data.Parts);
		});
		WriteArray(writer, "Parts", data.Parts, delegate(StructuredPart part)
		{
			WritePart(writer, part);
		});
		WriteArray(writer, "Perks", data.Perks, delegate(StructuredPerk perk)
		{
			WritePerk(writer, perk);
		});
		WriteArray(writer, "Consumables", data.Consumable, delegate(StructuredConsumable consumable)
		{
			WriteConsumable(writer, consumable);
		});
		WriteArray(writer, "Trinkets", data.Gadgets, delegate(StructuredGadget trinket)
		{
			WriteTrinket(writer, trinket, data.Parts);
		});
		WriteArray(writer, "InfectionLevelIncrease", data.InfectionLevelIncrease, delegate(float precentage)
		{
			writer.WriteValue(precentage);
		});
		writer.WriteStartElement("Settings");
		for (int num = 0; num < data.SettingVariables.Count; num++)
		{
			WriteArray(writer, ((CraftingToolSettingCollection)num).ToString(), data.SettingVariables[num].ToArray(), delegate(KeyValuePair<string, float> setting)
			{
				WriteSetting(writer, setting);
			});
		}
		writer.WriteEndElement();
		writer.WriteStartElement("GlobalSettings");
		WriteMinMaxRangeSetting("TrinketStatRange", writer, data.GlobalSettings.TrinketStatRangeSettings);
		WriteMinMaxRangeSetting("TrinketPerkRange", writer, data.GlobalSettings.TrinketPerkRangeSettings);
		writer.WriteEndElement();
		writer.WriteEndElement();
		writer.WriteEndDocument();
		writer.Flush();
	}

	private static void WriteSetting(XmlTextWriter writer, KeyValuePair<string, float> setting)
	{
		WriteElement(writer, "Name", setting.Key, writer.WriteValue);
		WriteElement(writer, "Value", setting.Value.ToString(CultureInfo.InvariantCulture), writer.WriteValue);
	}

	private static void WriteMinMaxRangeSetting(string name, XmlTextWriter writer, MinMaxRangeSettings perkRange)
	{
		writer.WriteStartElement(name);
		WriteElement(writer, "LowMin", perkRange.LowMin, writer.WriteValue);
		WriteElement(writer, "LowMax", perkRange.LowMax, writer.WriteValue);
		WriteElement(writer, "MidMin", perkRange.MidMin, writer.WriteValue);
		WriteElement(writer, "MidMax", perkRange.MidMax, writer.WriteValue);
		WriteElement(writer, "HighMin", perkRange.HighMin, writer.WriteValue);
		WriteElement(writer, "HighMax", perkRange.HighMax, writer.WriteValue);
		WriteElement(writer, "ExtremeMin", perkRange.ExtremeMin, writer.WriteValue);
		WriteElement(writer, "ExtremeMax", perkRange.ExtremeMax, writer.WriteValue);
		writer.WriteEndElement();
	}

	private static void WriteTrinket(XmlTextWriter writer, StructuredGadget trinket, StructuredPart[] parts)
	{
		WriteElement(writer, "ID", trinket.ID, writer.WriteValue);
		WriteElement(writer, "Tier", trinket.Tier, writer.WriteValue);
		writer.WriteElementString("WorkingName", trinket.WorkingName);
		writer.WriteElementString("LocalizedName", trinket.LocalizedName);
		writer.WriteElementString("Icon", trinket.Icon);
		WriteElement(writer, "TrinketType", trinket.GadgetType.ToString(), writer.WriteValue);
		WriteElement(writer, "ItemValue", trinket.ItemValue, writer.WriteValue);
		for (int i = 0; i < trinket.Stats.Length; i++)
		{
			WriteTrinketStat(writer, trinket.Stats[i].Type, trinket.Stats[i].Value, trinket.Stats[i].IsCoreStat);
		}
		for (int j = 0; j < trinket.Parts.Count; j++)
		{
			WriteTrinketPart(writer, trinket.Parts[j], parts);
		}
		for (int k = 0; k < trinket.Perks.Length; k++)
		{
			writer.WriteStartElement("Perk");
			writer.WriteAttributeString("type", trinket.Perks[k].Type.ToString());
			writer.WriteEndElement();
		}
		for (int l = 0; l < trinket.Tags.Length; l++)
		{
			WriteElement(writer, "Tag", trinket.Tags[l].ToString(), writer.WriteValue);
		}
	}

	private static void WriteTrinketPart(XmlTextWriter writer, ItemSchematicPart craftingTrinketPart, StructuredPart[] parts)
	{
		StructuredPart structuredPart = parts.LastOrDefault((StructuredPart x) => x.ID == craftingTrinketPart.ID);
		writer.WriteStartElement("Part");
		writer.WriteAttributeString("id", craftingTrinketPart.ID.ToString());
		writer.WriteAttributeString("count", ((int)Math.Round((float)(structuredPart.DefaultAmount * craftingTrinketPart.Quantity) * 0.01f)).ToString());
		writer.WriteEndElement();
	}

	private static void WriteTrinketStat(XmlTextWriter writer, StatRatingType statRatingType, int value, bool isCore)
	{
		writer.WriteStartElement("Stat");
		writer.WriteAttributeString("type", statRatingType.ToString());
		writer.WriteAttributeString("minvalue", value.ToString());
		writer.WriteAttributeString("core", isCore.ToString());
		writer.WriteEndElement();
	}

	private static void WriteWeapon(XmlTextWriter writer, StructuredWeapon weapon, StructuredPart[] parts)
	{
		WriteElement(writer, "ID", weapon.ID, writer.WriteValue);
		WriteElement(writer, "Tier", weapon.Tier, writer.WriteValue);
		writer.WriteElementString("WorkingName", weapon.WorkingName);
		writer.WriteElementString("LocalizedName", weapon.LocalizedName);
		WriteElement(writer, "DefaultWeapon", weapon.DefaultWeapon.ToString(), writer.WriteValue);
		WriteElement(writer, "WeaponType", weapon.WeaponType.ToString(), writer.WriteValue);
		WriteElement(writer, "PowerValue", weapon.PowerValue, writer.WriteValue);
		WriteElement(writer, "VitalityValue", weapon.VitalityValue, writer.WriteValue);
		WriteElement(writer, "StrengthValue", weapon.StrengthValue, writer.WriteValue);
		WriteElement(writer, "ItemValue", weapon.ItemValue, writer.WriteValue);
		for (int i = 0; i < weapon.Stats.Length; i++)
		{
			writer.WriteStartElement("Stat");
			writer.WriteAttributeString("type", weapon.Stats[i].Type.ToString());
			writer.WriteAttributeString("value", weapon.Stats[i].Value.ToString());
			writer.WriteEndElement();
		}
		for (int j = 0; j < weapon.Perks.Length; j++)
		{
			writer.WriteStartElement("Perk");
			writer.WriteAttributeString("type", weapon.Perks[j].Type.ToString());
			writer.WriteAttributeString("value", weapon.Perks[j].Value.ToString());
			writer.WriteEndElement();
		}
		for (int k = 0; k < weapon.DesignSlots.Length; k++)
		{
			writer.WriteStartElement("Design");
			writer.WriteAttributeString("category", weapon.DesignSlots[k].Category.ToString());
			writer.WriteEndElement();
		}
		int l;
		for (l = 0; l < weapon.Parts.Length; l++)
		{
			StructuredPart structuredPart = parts.LastOrDefault((StructuredPart x) => x.ID == weapon.Parts[l].PartID);
			writer.WriteStartElement("Part");
			writer.WriteAttributeString("id", weapon.Parts[l].PartID.ToString());
			writer.WriteAttributeString("count", ((int)Math.Round((float)(structuredPart.DefaultAmount * weapon.Parts[l].Count) * 0.01f)).ToString());
			writer.WriteEndElement();
		}
		for (int num = 0; num < weapon.Tags.Length; num++)
		{
			WriteElement(writer, "Tag", weapon.Tags[num].ToString(), writer.WriteValue);
		}
	}

	private static void WritePerk(XmlTextWriter writer, StructuredPerk perk)
	{
		WriteElement(writer, "ID", perk.ID, writer.WriteValue);
		WriteElement(writer, "WorkingName", perk.WorkingName, writer.WriteValue);
		WriteElement(writer, "AttackBonusValue", perk.AttackBonusValue, writer.WriteValue);
		WriteElement(writer, "AttackBonusValueMin", perk.AttackBonusValueMin, writer.WriteValue);
		WriteElement(writer, "AttackBonusValueMax", perk.AttackBonusValueMax, writer.WriteValue);
		WriteElement(writer, "Upgrade", perk.Upgrade.ToString(), writer.WriteValue);
		WriteElement(writer, "Global", perk.Global, writer.WriteValue);
		WriteElement(writer, "ItemValue", perk.ItemValue, writer.WriteValue);
		WriteElement(writer, "Category", (int)perk.Category, writer.WriteValue);
		for (int i = 0; i < perk.Tags.Length; i++)
		{
			WriteElement(writer, "Tag", perk.Tags[i].ToString(), writer.WriteValue);
		}
	}

	private static void WritePart(XmlTextWriter writer, StructuredPart part)
	{
		WriteElement(writer, "ID", part.ID, writer.WriteValue);
		WriteElement(writer, "Tier", part.Tier, writer.WriteValue);
		WriteElement(writer, "WorkingName", part.WorkingName, writer.WriteValue);
		WriteElement(writer, "Rarity", part.Rarity.ToString(), writer.WriteValue);
		WriteElement(writer, "Default", part.DefaultAmount, writer.WriteValue);
		WriteElement(writer, "LocalizedName", part.LocalizedName, writer.WriteValue);
		WriteElement(writer, "LocalizedDescription", part.LocalizedDescription, writer.WriteValue);
		for (int i = 0; i < part.Tags.Length; i++)
		{
			WriteElement(writer, "Tag", part.Tags[i].ToString(), writer.WriteValue);
		}
	}

	private static void WriteConsumable(XmlTextWriter writer, StructuredConsumable consumable)
	{
		WriteElement(writer, "ID", consumable.ID, writer.WriteValue);
		WriteElement(writer, "AbilityIdentifier", consumable.AbilityIdentifier, writer.WriteValue);
		WriteElement(writer, "WorkingName", consumable.WorkingName, writer.WriteValue);
		WriteElement(writer, "LocalizedName", consumable.LocalizedName, writer.WriteValue);
		WriteElement(writer, "Default", consumable.DefaultAmount, writer.WriteValue);
		WriteElement(writer, "Stacks", consumable.Stacks, writer.WriteValue);
		for (int i = 0; i < consumable.Tags.Length; i++)
		{
			WriteElement(writer, "Tag", consumable.Tags[i].ToString(), writer.WriteValue);
		}
	}

	private static void WriteArray<T>(XmlTextWriter writer, string element, T[] arr, Action<T> writeFunc)
	{
		writer.WriteStartElement(element);
		if (arr != null)
		{
			foreach (T value in arr)
			{
				WriteElement(writer, "value", value, writeFunc);
			}
		}
		writer.WriteEndElement();
	}

	private static void WriteElement<T>(XmlTextWriter writer, string element, T value, Action<T> writeFunc)
	{
		writer.WriteStartElement(element);
		writeFunc(value);
		writer.WriteEndElement();
	}

	public static CraftingData ReadCraftingData(Stream stream)
	{
		CraftingData craftingData = new CraftingData();
		XmlDocument xmlDocument = new XmlDocument();
		xmlDocument.Load(stream);
		XmlNode xmlNode = xmlDocument["root"];
		XmlNode node = xmlNode["Weapons"];
		XmlNode node2 = xmlNode["Trinkets"];
		XmlNode node3 = xmlNode["Parts"];
		XmlNode node4 = xmlNode["Perks"];
		XmlNode settingsNode = xmlNode["Settings"];
		XmlNode globalSettingsNode = xmlNode["GlobalSettings"];
		_ = xmlNode["StatsSettings"];
		_ = xmlNode["TrinketStatsSettings"];
		_ = xmlNode["RaritySettings"];
		_ = xmlNode["TrinketRaritySettings"];
		XmlNode node5 = xmlNode["Consumables"];
		XmlNode node6 = xmlNode["InfectionLevelIncrease"];
		craftingData.Weapons = ReadArray(node, ReadWeapon);
		craftingData.Gadgets = ReadArray(node2, ReadTrinket);
		craftingData.Parts = ReadArray(node3, ReadPart);
		craftingData.Perks = ReadArray(node4, ReadPerk);
		craftingData.SettingVariables = ReadSettings(settingsNode);
		craftingData.Consumable = ReadArray(node5, ReadConsumable);
		craftingData.GlobalSettings = ReadGlobalSettings(globalSettingsNode);
		craftingData.InfectionLevelIncrease = ReadArray(node6, ReadFloat);
		return craftingData;
	}

	private static float ReadFloat(XmlNode node)
	{
		CultureInfo cultureInfo = (CultureInfo)CultureInfo.CurrentCulture.Clone();
		cultureInfo.NumberFormat.CurrencyDecimalSeparator = ".";
		return float.Parse(node.InnerText, NumberStyles.Any, cultureInfo);
	}

	private static List<Dictionary<string, float>> ReadSettings(XmlNode settingsNode)
	{
		List<Dictionary<string, float>> list = new List<Dictionary<string, float>>();
		foreach (CraftingToolSettingCollection value in Enum.GetValues(typeof(CraftingToolSettingCollection)))
		{
			XmlNode node = settingsNode[value.ToString()];
			Dictionary<string, float> dictionary = new Dictionary<string, float>();
			StunCore.Tuple<string, float>[] array = ReadArray(node, ReadSetting);
			StunCore.Tuple<string, float>[] array2 = array;
			for (int i = 0; i < array2.Length; i++)
			{
				StunCore.Tuple<string, float> tuple = array2[i];
				dictionary.Add(tuple.Item1, tuple.Item2);
			}
			list.Add(dictionary);
		}
		return list;
	}

	private static StunCore.Tuple<string, float> ReadSetting(XmlNode node)
	{
		return new StunCore.Tuple<string, float>
		{
			Item1 = node["Name"].InnerText,
			Item2 = float.Parse(node["Value"].InnerText, CultureInfo.InvariantCulture)
		};
	}

	private static StatsSettings ReadStatsSettings(XmlNode statsSettingsNode)
	{
		StatsSettings statsSettings = new StatsSettings();
		statsSettings.CoreStatBonus = ReadArray(statsSettingsNode["CoreStatBonus"], ReadInt);
		statsSettings.SecondaryStatBonus = ReadArray(statsSettingsNode["SecondaryStatBonus"], ReadInt);
		return statsSettings;
	}

	private static GlobalSettings ReadGlobalSettings(XmlNode globalSettingsNode)
	{
		GlobalSettings globalSettings = new GlobalSettings();
		XmlNode node = globalSettingsNode["TrinketStatRange"];
		XmlNode node2 = globalSettingsNode["TrinketPerkRange"];
		globalSettings.TrinketStatRangeSettings = ReadMinMaxRangeSettings(node);
		globalSettings.TrinketPerkRangeSettings = ReadMinMaxRangeSettings(node2);
		return globalSettings;
	}

	private static MinMaxRangeSettings ReadMinMaxRangeSettings(XmlNode node)
	{
		MinMaxRangeSettings minMaxRangeSettings = new MinMaxRangeSettings();
		minMaxRangeSettings.LowMin = int.Parse(node["LowMin"].InnerText);
		minMaxRangeSettings.LowMax = int.Parse(node["LowMax"].InnerText);
		minMaxRangeSettings.MidMin = int.Parse(node["MidMin"].InnerText);
		minMaxRangeSettings.MidMax = int.Parse(node["MidMax"].InnerText);
		minMaxRangeSettings.HighMin = int.Parse(node["HighMin"].InnerText);
		minMaxRangeSettings.HighMax = int.Parse(node["HighMax"].InnerText);
		minMaxRangeSettings.ExtremeMin = int.Parse(node["ExtremeMin"].InnerText);
		minMaxRangeSettings.ExtremeMax = int.Parse(node["ExtremeMax"].InnerText);
		return minMaxRangeSettings;
	}

	private static RangeSettings ReadRangeSettings(XmlNode node)
	{
		RangeSettings rangeSettings = new RangeSettings();
		rangeSettings.Low = int.Parse(node["Low"].InnerText);
		rangeSettings.Mid = int.Parse(node["Mid"].InnerText);
		rangeSettings.High = int.Parse(node["High"].InnerText);
		rangeSettings.Extreme = int.Parse(node["Extreme"].InnerText);
		return rangeSettings;
	}

	private static CoreStatConversionSettings ReadCoreStatConversionSettings(XmlNode node)
	{
		CoreStatConversionSettings coreStatConversionSettings = new CoreStatConversionSettings();
		coreStatConversionSettings.VeryLow = int.Parse(node["VeryLow"].InnerText);
		coreStatConversionSettings.Low = int.Parse(node["Low"].InnerText);
		coreStatConversionSettings.Mid = int.Parse(node["Mid"].InnerText);
		coreStatConversionSettings.High = int.Parse(node["High"].InnerText);
		coreStatConversionSettings.VeryHigh = int.Parse(node["VeryHigh"].InnerText);
		return coreStatConversionSettings;
	}

	private static ItemValueSettings ReadItemValueSettings(XmlNode itemValueNode)
	{
		ItemValueSettings itemValueSettings = new ItemValueSettings();
		itemValueSettings.CoreStat = int.Parse(itemValueNode["CoreStat"].InnerText);
		itemValueSettings.SecondaryStat = int.Parse(itemValueNode["SecondaryStat"].InnerText);
		return itemValueSettings;
	}

	private static RaritySettings ReadRaritySettings(XmlNode raritySettingsNode)
	{
		RaritySettings raritySettings = new RaritySettings();
		raritySettings.Common = ReadArray(raritySettingsNode["Common"], ReadInt);
		raritySettings.Uncommon = ReadArray(raritySettingsNode["Uncommon"], ReadInt);
		raritySettings.Rare = ReadArray(raritySettingsNode["Rare"], ReadInt);
		raritySettings.Epic = ReadArray(raritySettingsNode["Epic"], ReadInt);
		raritySettings.Legendary = ReadArray(raritySettingsNode["Legendary"], ReadInt);
		return raritySettings;
	}

	private static StructuredPerk ReadPerk(XmlNode node)
	{
		StructuredPerk structuredPerk = new StructuredPerk();
		structuredPerk.ID = int.Parse(node["ID"].InnerText);
		structuredPerk.WorkingName = node["WorkingName"].InnerText;
		structuredPerk.Upgrade = ParseEnum<UpgradeType>(node["Upgrade"].InnerText);
		structuredPerk.AttackBonusValue = float.Parse(node["AttackBonusValue"].InnerText, CultureInfo.InvariantCulture);
		structuredPerk.AttackBonusValueMin = float.Parse(node["AttackBonusValueMin"].InnerText, CultureInfo.InvariantCulture);
		structuredPerk.AttackBonusValueMax = float.Parse(node["AttackBonusValueMax"].InnerText, CultureInfo.InvariantCulture);
		structuredPerk.Global = bool.Parse(node["Global"].InnerText);
		structuredPerk.ItemValue = int.Parse(node["ItemValue"].InnerText);
		structuredPerk.Category = (PerkCategory)int.Parse(node["Category"].InnerText);
		List<TagEnum> list = new List<TagEnum>();
		for (int i = 0; i < node.ChildNodes.Count; i++)
		{
			if (node.ChildNodes[i].Name == "Tag")
			{
				list.Add((TagEnum)Enum.Parse(typeof(TagEnum), node.ChildNodes[i].InnerText));
			}
		}
		structuredPerk.Tags = list.ToArray();
		return structuredPerk;
	}

	private static int ReadInt(XmlNode node)
	{
		return int.Parse(node.InnerText);
	}

	private static T[] ReadArray<T>(XmlNode node, Func<XmlNode, T> readFunc)
	{
		List<T> list = new List<T>();
		foreach (XmlNode item in node)
		{
			if (item.Name == "value")
			{
				list.Add(readFunc(item));
			}
		}
		return list.ToArray();
	}

	private static StructuredPart ReadPart(XmlNode node)
	{
		StructuredPart structuredPart = new StructuredPart();
		structuredPart.ID = int.Parse(node["ID"].InnerText);
		structuredPart.Tier = int.Parse(node["Tier"].InnerText);
		structuredPart.WorkingName = node["WorkingName"].InnerText;
		structuredPart.Rarity = ParseEnum<Rarity>(node["Rarity"].InnerText);
		structuredPart.DefaultAmount = int.Parse(node["Default"].InnerText);
		structuredPart.LocalizedName = node["LocalizedName"].InnerText;
		structuredPart.LocalizedDescription = node["LocalizedDescription"].InnerText;
		List<TagEnum> list = new List<TagEnum>();
		for (int i = 0; i < node.ChildNodes.Count; i++)
		{
			if (node.ChildNodes[i].Name == "Tag")
			{
				list.Add((TagEnum)Enum.Parse(typeof(TagEnum), node.ChildNodes[i].InnerText));
			}
		}
		structuredPart.Tags = list.ToArray();
		return structuredPart;
	}

	private static StructuredConsumable ReadConsumable(XmlNode node)
	{
		StructuredConsumable structuredConsumable = new StructuredConsumable();
		structuredConsumable.ID = int.Parse(node["ID"].InnerText);
		structuredConsumable.AbilityIdentifier = node["AbilityIdentifier"].InnerText;
		structuredConsumable.WorkingName = node["WorkingName"].InnerText;
		structuredConsumable.Stacks = int.Parse(node["Stacks"].InnerText);
		structuredConsumable.DefaultAmount = int.Parse(node["Default"].InnerText);
		structuredConsumable.LocalizedName = node["LocalizedName"].InnerText;
		List<TagEnum> list = new List<TagEnum>();
		for (int i = 0; i < node.ChildNodes.Count; i++)
		{
			if (node.ChildNodes[i].Name == "Tag")
			{
				list.Add((TagEnum)Enum.Parse(typeof(TagEnum), node.ChildNodes[i].InnerText));
			}
		}
		structuredConsumable.Tags = list.ToArray();
		return structuredConsumable;
	}

	private static StructuredGadget ReadTrinket(XmlNode node)
	{
		StructuredGadget structuredGadget = new StructuredGadget();
		structuredGadget.ID = int.Parse(node["ID"].InnerText);
		structuredGadget.Tier = int.Parse(node["Tier"].InnerText);
		structuredGadget.WorkingName = node["WorkingName"].InnerText;
		structuredGadget.LocalizedName = node["LocalizedName"].InnerText;
		structuredGadget.Icon = node["Icon"].InnerText;
		structuredGadget.GadgetType = ParseEnum<GadgetType>(node["TrinketType"].InnerText);
		structuredGadget.ItemValue = int.Parse(node["ItemValue"].InnerText);
		List<CraftingTrinketStat> list = new List<CraftingTrinketStat>();
		List<ItemSchematicPart> list2 = new List<ItemSchematicPart>();
		List<CraftingTrinketPerk> list3 = new List<CraftingTrinketPerk>();
		List<TagEnum> list4 = new List<TagEnum>();
		for (int i = 0; i < node.ChildNodes.Count; i++)
		{
			if (node.ChildNodes[i].Name == "Stat")
			{
				list.Add(new CraftingTrinketStat
				{
					Type = ParseEnum<StatRatingType>(node.ChildNodes[i].Attributes[0].Value),
					Value = int.Parse(node.ChildNodes[i].Attributes[1].Value),
					IsCoreStat = bool.Parse(node.ChildNodes[i].Attributes[2].Value)
				});
			}
			if (node.ChildNodes[i].Name == "Part")
			{
				list2.Add(new ItemSchematicPart
				{
					ID = int.Parse(node.ChildNodes[i].Attributes[0].Value),
					Quantity = int.Parse(node.ChildNodes[i].Attributes[1].Value)
				});
			}
			else if (node.ChildNodes[i].Name == "Perk")
			{
				list3.Add(new CraftingTrinketPerk
				{
					Type = ParseEnum<UpgradeType>(node.ChildNodes[i].Attributes[0].Value)
				});
			}
			else if (node.ChildNodes[i].Name == "Tag")
			{
				list4.Add((TagEnum)Enum.Parse(typeof(TagEnum), node.ChildNodes[i].InnerText));
			}
		}
		structuredGadget.Stats = list.ToArray();
		structuredGadget.Perks = list3.ToArray();
		structuredGadget.Parts = list2;
		structuredGadget.Tags = list4.ToArray();
		return structuredGadget;
	}

	private static StructuredWeapon ReadWeapon(XmlNode node)
	{
		StructuredWeapon structuredWeapon = new StructuredWeapon();
		structuredWeapon.ID = int.Parse(node["ID"].InnerText);
		structuredWeapon.Tier = int.Parse(node["Tier"].InnerText);
		structuredWeapon.WorkingName = node["WorkingName"].InnerText;
		structuredWeapon.LocalizedName = node["LocalizedName"].InnerText;
		structuredWeapon.DefaultWeapon = (DefaultWeapon)Enum.Parse(typeof(DefaultWeapon), node["DefaultWeapon"].InnerText);
		structuredWeapon.WeaponType = ParseEnum<WeaponType>(node["WeaponType"].InnerText);
		structuredWeapon.VitalityValue = int.Parse(node["VitalityValue"].InnerText);
		structuredWeapon.PowerValue = int.Parse(node["PowerValue"].InnerText);
		structuredWeapon.StrengthValue = int.Parse(node["StrengthValue"].InnerText);
		structuredWeapon.ItemValue = int.Parse(node["ItemValue"].InnerText);
		List<CraftingWeaponStat> list = new List<CraftingWeaponStat>();
		List<CraftingWeaponPart> list2 = new List<CraftingWeaponPart>();
		List<CraftingWeaponPerk> list3 = new List<CraftingWeaponPerk>();
		List<DesignSlot> list4 = new List<DesignSlot>();
		List<TagEnum> list5 = new List<TagEnum>();
		for (int i = 0; i < node.ChildNodes.Count; i++)
		{
			if (node.ChildNodes[i].Name == "Stat")
			{
				list.Add(new CraftingWeaponStat
				{
					Type = ParseEnum<StatRatingType>(node.ChildNodes[i].Attributes[0].Value),
					Value = int.Parse(node.ChildNodes[i].Attributes[1].Value)
				});
			}
			else if (node.ChildNodes[i].Name == "Part")
			{
				list2.Add(new CraftingWeaponPart
				{
					PartID = int.Parse(node.ChildNodes[i].Attributes[0].Value),
					Count = int.Parse(node.ChildNodes[i].Attributes[1].Value)
				});
			}
			else if (node.ChildNodes[i].Name == "Perk")
			{
				list3.Add(new CraftingWeaponPerk
				{
					Type = ParseEnum<UpgradeType>(node.ChildNodes[i].Attributes[0].Value),
					Value = int.Parse(node.ChildNodes[i].Attributes[1].Value)
				});
			}
			else if (node.ChildNodes[i].Name == "Design")
			{
				list4.Add(new DesignSlot
				{
					Category = ParseEnum<PerkCategory>(node.ChildNodes[i].Attributes[0].Value)
				});
			}
			else if (node.ChildNodes[i].Name == "Tag")
			{
				list5.Add((TagEnum)Enum.Parse(typeof(TagEnum), node.ChildNodes[i].InnerText));
			}
		}
		structuredWeapon.Stats = list.ToArray();
		structuredWeapon.Parts = list2.ToArray();
		structuredWeapon.Perks = list3.ToArray();
		structuredWeapon.DesignSlots = list4.ToArray();
		structuredWeapon.Tags = list5.ToArray();
		return structuredWeapon;
	}

	private static T ParseEnum<T>(string str) where T : struct
	{
		return (T)Enum.Parse(typeof(T), str);
	}
}
