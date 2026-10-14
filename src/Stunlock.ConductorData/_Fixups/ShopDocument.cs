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
using System.Linq;
using ConductorShop;
using StunCore;

namespace ConductorCrafting;

public class ShopDocument
{
	private Dictionary<int, ItemEventModifications[]> _ShopEventsByItemID = new Dictionary<int, ItemEventModifications[]>();

	private Dictionary<int, ShopItemData> _ShopItems = new Dictionary<int, ShopItemData>();

	private Dictionary<int, UserGroup> _UserGroups = new Dictionary<int, UserGroup>();

	public Dictionary<ShopItemType, int> ShopTypeMaxAmounts = new Dictionary<ShopItemType, int>
	{
		{
			ShopItemType.Consumable,
			10
		},
		{
			ShopItemType.Keycode,
			80
		},
		{
			ShopItemType.Part,
			100000
		}
	};

	private static ShopDocument _ActiveDocument;

	public int Revision { get; private set; }

	public ShopItemData[] ShopItems { get; private set; }

	public ShopEvent[] Events { get; private set; }

	public UserGroup[] UserGroups { get; private set; }

	public Banner[] Banners { get; private set; }

	public GoldPriceEntry[] GoldPrices { get; private set; }

	public CurrencyEvent[] CurrencyEvents { get; private set; }

	public GlobalPriceChange[] GlobalPriceChanges { get; private set; }

	public ShopSettings ShopSettings { get; private set; }

	public static bool HasShopDocument
	{
		get
		{
			ShopDocument activeDocument = GetActiveDocument();
			if (activeDocument != null)
			{
				return activeDocument.Revision > 0;
			}
			return false;
		}
	}

	public static int ActiveRevision => GetActiveDocument()?.Revision ?? (-1);

	public static void SetActiveDocument(ShopDocument doc)
	{
		_ActiveDocument = doc;
	}

	public static ShopDocument GetActiveDocument()
	{
		return _ActiveDocument;
	}

	public static ShopDocument LoadShopDocument(int revision, ShopEvent[] events, ShopItemData[] shopItems, UserGroup[] userGroups, Banner[] banners, GoldPriceEntry[] goldPrices, GlobalPriceChange[] globalPriceChanges, CurrencyEvent[] currencyEvents, ShopSettings shopSettings)
	{
		ShopDocument shopDocument = new ShopDocument();
		shopDocument.ShopItems = shopItems;
		shopDocument.UserGroups = userGroups;
		shopDocument.Events = events;
		shopDocument.Banners = banners.OrderByDescending((Banner e) => e.Start).ToArray();
		shopDocument.GoldPrices = goldPrices;
		shopDocument.GlobalPriceChanges = globalPriceChanges;
		shopDocument.ShopSettings = shopSettings;
		shopDocument.CurrencyEvents = currencyEvents;
		ShopDocument shopDocument2 = shopDocument;
		shopDocument2.Revision = revision;
		shopDocument2._ShopItems = shopItems.ToDictionary((ShopItemData d) => d.ShopItemID);
		shopDocument2._UserGroups = userGroups.ToDictionary((UserGroup g) => g.UserGroupID);
		for (int num = 0; num < events.Length; num++)
		{
			ShopEvent shopEvent = events[num];
			Dictionary<int, List<ShopModification>> dictionary = new Dictionary<int, List<ShopModification>>();
			ShopModification[] modifications = shopEvent.Modifications;
			for (int num2 = 0; num2 < modifications.Length; num2++)
			{
				ShopModification item = modifications[num2];
				if (!dictionary.ContainsKey(item.ShopItemID))
				{
					dictionary[item.ShopItemID] = new List<ShopModification>();
				}
				dictionary[item.ShopItemID].Add(item);
			}
			foreach (KeyValuePair<int, List<ShopModification>> item2 in dictionary)
			{
				ItemEventModifications itemEventModifications = new ItemEventModifications
				{
					Event = shopEvent,
					Modifications = item2.Value.ToArray()
				};
				shopDocument2._ShopEventsByItemID.TryGetValue(item2.Key, out var value);
				if (value == null)
				{
					value = new ItemEventModifications[1];
				}
				else
				{
					Array.Resize(ref value, value.Length + 1);
				}
				value[value.Length - 1] = itemEventModifications;
				shopDocument2._ShopEventsByItemID[item2.Key] = value;
			}
		}
		return shopDocument2;
	}

	public static bool GetShopItem(int shopItemID, Func<ShopRequirement, bool> requirementChecker, DateTime currentTime, out ShopItemData originalShopItem, out IEnumerable<ItemEventModifications> modifications, out ShopItemData modifiedItem)
	{
		originalShopItem = default(ShopItemData);
		modifications = null;
		modifiedItem = default(ShopItemData);
		return _ActiveDocument?.GetShopItemImpl(shopItemID, requirementChecker, currentTime, out originalShopItem, out modifications, out modifiedItem) ?? false;
	}

	public static IEnumerable<int> GetAllShopItemIDs()
	{
		ShopDocument activeDocument = _ActiveDocument;
		if (activeDocument != null)
		{
			return activeDocument.GetAllShopItemIDsImpl();
		}
		return new int[0];
	}

	public IEnumerable<int> GetAllShopItemIDsImpl()
	{
		foreach (KeyValuePair<int, ShopItemData> item in _ShopItems)
		{
			KeyValuePair<int, ShopItemData> keyValuePair = item;
			yield return keyValuePair.Key;
		}
	}

	public List<ShopItemData> GetAllShopItemsOfType(ShopItemType type)
	{
		if (_ShopItems != null)
		{
			List<ShopItemData> list = new List<ShopItemData>();
			{
				foreach (KeyValuePair<int, ShopItemData> shopItem in _ShopItems)
				{
					ShopItemData value = shopItem.Value;
					if (value.Items == null || value.Items.Length <= 0)
					{
						continue;
					}
					for (int i = 0; i < value.Items.Length; i++)
					{
						if (value.Items[i].Type == type)
						{
							list.Add(value);
						}
					}
				}
				return list;
			}
		}
		return null;
	}

	public int GetSalePercentage(BoostType type)
	{
		if (type == BoostType.PremiumBoost)
		{
			UserGroup userGroup = default(UserGroup);
			bool flag = false;
			for (int i = 0; i < UserGroups.Length; i++)
			{
				for (int j = 0; j < UserGroups[i].Requirements.Length; j++)
				{
					if (UserGroups[i].Requirements[j].Type == ShopRequirementType.PremiumBoostActive)
					{
						userGroup = UserGroups[i];
						flag = true;
					}
				}
			}
			if (flag)
			{
				bool flag2 = false;
				GlobalPriceChange globalPriceChange = default(GlobalPriceChange);
				for (int k = 0; k < GlobalPriceChanges.Length; k++)
				{
					if (GlobalPriceChanges[k].UserGroupIDs.Contains(userGroup.UserGroupID))
					{
						globalPriceChange = GlobalPriceChanges[k];
						flag2 = true;
					}
				}
				if (flag2)
				{
					return globalPriceChange.SilverChangePercentage;
				}
			}
		}
		return 0;
	}

	public IEnumerable<CurrencyEvent> GetActiveCurrencyEvents(Func<ShopRequirement, bool> requirementChecker)
	{
		DateTime currentTime = DateTime.UtcNow;
		return CurrencyEvents.Where((CurrencyEvent e) => EventActive(requirementChecker, currentTime, ref e));
	}

	public IEnumerable<ShopEvent> GetActiveShopEvents(Func<ShopRequirement, bool> requirementChecker)
	{
		DateTime currentTime = DateTime.UtcNow;
		return Events.Where((ShopEvent e) => EventActive(requirementChecker, currentTime, ref e));
	}

	public bool GetShopItemImpl(int shopItemID, Func<ShopRequirement, bool> requirementChecker, DateTime currentTime, out ShopItemData originalShopItem, out IEnumerable<ItemEventModifications> modifications, out ShopItemData modifiedItem)
	{
		modifications = null;
		modifiedItem = default(ShopItemData);
		if (!_ShopItems.TryGetValue(shopItemID, out originalShopItem))
		{
			return false;
		}
		_ShopEventsByItemID.TryGetValue(shopItemID, out var value);
		modifiedItem = originalShopItem;
		if (value != null && value.Any())
		{
			// [DIE_Reborn FIXUP] 原文为查询语法:
			//     (from e in value
			//      where EventActive(requirementChecker, currentTime, ref e.Event)
			//      orderby e.Event.Priority descending
			//      select e).ToArray()
			// `ref e.Event` 把范围变量的字段按 ref 传出, C# 不允许 (CS1939) —— 这是反编译产物, 不是合法 C#。
			// 已核对 EventActive -> EventIsActive -> IsActive 全链路只读取 e 的字段、从不写回,
			// 那个 ref 只是 net35 时代避免拷贝大结构体的性能写法。故复制到局部变量再按 ref 传, 语义等价。
			IEnumerable<ShopModification> source = (modifications = value
				.Where((ItemEventModifications e) =>
				{
					ShopEvent shopEvent = e.Event;
					return EventActive(requirementChecker, currentTime, ref shopEvent);
				})
				.OrderByDescending((ItemEventModifications e) => e.Event.Priority)
				.ToArray()).SelectMany((ItemEventModifications m) => m.Modifications);
			modifiedItem.IsActive = source.Where((ShopModification m) => (m.Type & ShopModificationType.IsActive) != 0).DefaultIfEmpty(new ShopModification
			{
				IsActive = originalShopItem.IsActive
			}).First()
				.IsActive;
			modifiedItem.GoldPrice = source.Where((ShopModification m) => (m.Type & ShopModificationType.GoldPrice) != 0).DefaultIfEmpty(new ShopModification
			{
				GoldPrice = originalShopItem.GoldPrice
			}).First()
				.GoldPrice;
			modifiedItem.SilverPrice = source.Where((ShopModification m) => (m.Type & ShopModificationType.SilverPrice) != 0).DefaultIfEmpty(new ShopModification
			{
				SilverPrice = originalShopItem.SilverPrice
			}).First()
				.SilverPrice;
			modifiedItem.CharacterPointPrice = source.Where((ShopModification m) => (m.Type & ShopModificationType.CharacterPointPrice) != 0).DefaultIfEmpty(new ShopModification
			{
				CharacterPointPrice = originalShopItem.CharacterPointPrice
			}).First()
				.CharacterPointPrice;
			modifiedItem.ResearchPointPrice = source.Where((ShopModification m) => (m.Type & ShopModificationType.ResearchPointPrice) != 0).DefaultIfEmpty(new ShopModification
			{
				ResearchPointPrice = originalShopItem.ResearchPointPrice
			}).First()
				.ResearchPointPrice;
			modifiedItem.New = source.Where((ShopModification m) => (m.Type & ShopModificationType.New) != 0).DefaultIfEmpty(new ShopModification
			{
				New = originalShopItem.New
			}).First()
				.New;
			modifiedItem.Featured = source.Where((ShopModification m) => (m.Type & ShopModificationType.Featured) != 0).DefaultIfEmpty(new ShopModification
			{
				Featured = originalShopItem.Featured
			}).First()
				.Featured;
			modifiedItem.TopSeller = source.Where((ShopModification m) => (m.Type & ShopModificationType.TopSeller) != 0).DefaultIfEmpty(new ShopModification
			{
				TopSeller = originalShopItem.TopSeller
			}).First()
				.TopSeller;
		}
		return true;
	}

	private bool EventActive(Func<ShopRequirement, bool> requirementChecker, DateTime currentTime, ref ShopEvent e)
	{
		if (EventIsActive(ref e, currentTime))
		{
			if (requirementChecker != null && e.UserGroupIDs.Any())
			{
				return e.UserGroupIDs.Any((int id) => GetUserGroup(id).Requirements.All((ShopRequirement r) => requirementChecker(r)));
			}
			return true;
		}
		return false;
	}

	private bool EventActive(Func<ShopRequirement, bool> requirementChecker, DateTime currentTime, ref CurrencyEvent e)
	{
		if (EventIsActive(ref e, currentTime))
		{
			if (requirementChecker != null && e.UserGroupIDs.Any())
			{
				return e.UserGroupIDs.Any((int id) => GetUserGroup(id).Requirements.All((ShopRequirement r) => requirementChecker(r)));
			}
			return true;
		}
		return false;
	}

	public static bool BannerIsActive(ref Banner e, DateTime currentTime, TimeSpan margin = default(TimeSpan))
	{
		return IsActive(e.Start, e.End, e.Repeat, e.RepeatCount, e.RepeatTimer, currentTime, ref margin);
	}

	public static bool EventIsActive(ref ShopEvent e, DateTime currentTime, TimeSpan margin = default(TimeSpan))
	{
		return IsActive(e.Start, e.End, e.Repeat, e.RepeatCount, e.RepeatTimer, currentTime, ref margin);
	}

	public static bool EventIsActive(ref CurrencyEvent e, DateTime currentTime, TimeSpan margin = default(TimeSpan))
	{
		return IsActive(e.Start, e.End, e.Repeat, e.RepeatCount, e.RepeatTimer, currentTime, ref margin);
	}

	public static bool EventIsFinished(ref CurrencyEvent e, DateTime currentTime)
	{
		return IsFinished(e.Start, e.End, e.Repeat, e.RepeatCount, e.RepeatTimer, currentTime);
	}

	public static bool BannerIsFinished(ref Banner e, DateTime currentTime)
	{
		return IsFinished(e.Start, e.End, e.Repeat, e.RepeatCount, e.RepeatTimer, currentTime);
	}

	public static bool EventIsFinished(ref ShopEvent e, DateTime currentTime)
	{
		return IsFinished(e.Start, e.End, e.Repeat, e.RepeatCount, e.RepeatTimer, currentTime);
	}

	private static bool IsActive(long start, long end, bool repeat, int repeatCount, long repeatTimer, DateTime currentTime, ref TimeSpan margin)
	{
		bool result = false;
		if (repeat)
		{
			DateTime dateTime = new DateTime(start, DateTimeKind.Utc);
			TimeSpan timeSpan = new DateTime(end, DateTimeKind.Utc) - dateTime;
			TimeSpan timeSpan2 = new TimeSpan(repeatTimer);
			int num = 0;
			while (dateTime < currentTime && (repeatCount == 0 || repeatCount >= num))
			{
				if (dateTime - margin < currentTime && dateTime + timeSpan + margin > currentTime)
				{
					result = true;
					break;
				}
				if (timeSpan2 == TimeSpan.Zero)
				{
					break;
				}
				dateTime += timeSpan2;
				num++;
			}
		}
		else
		{
			result = new DateTime(start, DateTimeKind.Utc) - margin < currentTime && new DateTime(end, DateTimeKind.Utc) + margin > currentTime;
		}
		return result;
	}

	private static bool IsFinished(long start, long end, bool repeat, int repeatCount, long repeatTimer, DateTime currentTime)
	{
		bool flag = false;
		if (repeat)
		{
			if (repeatCount == 0)
			{
				return false;
			}
			DateTime dateTime = new DateTime(start, DateTimeKind.Utc);
			TimeSpan timeSpan = new DateTime(end, DateTimeKind.Utc) - dateTime;
			TimeSpan timeSpan2 = new TimeSpan(repeatTimer);
			for (int i = 0; i < repeatCount; i++)
			{
				dateTime += timeSpan2;
			}
			return dateTime + timeSpan < currentTime;
		}
		return new DateTime(end, DateTimeKind.Utc) < currentTime;
	}

	public IEnumerable<StunCore.Tuple<ShopItemData, GlobalPriceChange>> GetGlobalSalePrices(Func<ShopRequirement, bool> requirementChecker, ShopItemData modifiedItem)
	{
		GlobalPriceChange[] possibleGlobalSales = GlobalPriceChanges.Where((GlobalPriceChange change) => (requirementChecker == null || !change.UserGroupIDs.Any() || change.UserGroupIDs.Any((int id) => GetUserGroup(id).Requirements.All((ShopRequirement r) => requirementChecker(r)))) && modifiedItem.Items.All((ItemData item) => change.ItemTypes.Any((ShopItemType changeItemType) => item.Type == changeItemType))).ToArray();
		try
		{
			GlobalPriceChange[] array = possibleGlobalSales;
			for (int num = 0; num < array.Length; num++)
			{
				GlobalPriceChange globalSale = array[num];
				ShopItemData itemWithGlobalSale = modifiedItem;
				if (globalSale.SilverChangePercentage > 0)
				{
					float num2 = 100f / (float)globalSale.SilverChangePercentage;
					itemWithGlobalSale.SilverPrice = Math.Max(1, (int)((float)modifiedItem.SilverPrice - (float)modifiedItem.SilverPrice / num2));
				}
				if (globalSale.GoldChangePercentage > 0)
				{
					float num3 = 100f / (float)globalSale.GoldChangePercentage;
					itemWithGlobalSale.GoldPrice = Math.Max(1, (int)((float)modifiedItem.GoldPrice - (float)modifiedItem.GoldPrice / num3));
				}
				if (globalSale.CharacterPointChangePercentage > 0)
				{
					float num4 = 100f / (float)globalSale.CharacterPointChangePercentage;
					itemWithGlobalSale.CharacterPointPrice = Math.Max(1, (int)((float)modifiedItem.CharacterPointPrice - (float)modifiedItem.CharacterPointPrice / num4));
				}
				// [DIE_Reborn FIXUP] Tuple -> StunCore.Tuple (同上, CS0104 歧义)
				yield return StunCore.Tuple.Create(itemWithGlobalSale, globalSale);
			}
		}
		finally
		{
		}
	}

	public void AddAllGlobalSalePrices(IEnumerable<ShopItemType> itemTypes, ref IEnumerable<int> cpPrices, ref IEnumerable<int> silverPrices, ref IEnumerable<int> goldPrices, ref IEnumerable<int> rpPrices)
	{
		GlobalPriceChange[] array = GlobalPriceChanges.Where((GlobalPriceChange change) => itemTypes.All((ShopItemType itemType) => change.ItemTypes.Any((ShopItemType changeItemType) => itemType == changeItemType))).ToArray();
		List<int> list = new List<int>();
		List<int> list2 = new List<int>();
		List<int> list3 = new List<int>();
		List<int> list4 = new List<int>();
		GlobalPriceChange[] array2 = array;
		for (int num = 0; num < array2.Length; num++)
		{
			GlobalPriceChange globalPriceChange = array2[num];
			if (globalPriceChange.SilverChangePercentage > 0)
			{
				foreach (int silverPrice in silverPrices)
				{
					float num2 = 100f / (float)globalPriceChange.SilverChangePercentage;
					list.Add(Math.Max(1, (int)((float)silverPrice - (float)silverPrice / num2)));
				}
			}
			if (globalPriceChange.GoldChangePercentage > 0)
			{
				foreach (int goldPrice in goldPrices)
				{
					float num3 = 100f / (float)globalPriceChange.GoldChangePercentage;
					list3.Add(Math.Max(1, (int)((float)goldPrice - (float)goldPrice / num3)));
				}
			}
			if (globalPriceChange.CharacterPointChangePercentage > 0)
			{
				foreach (int cpPrice in cpPrices)
				{
					float num4 = 100f / (float)globalPriceChange.CharacterPointChangePercentage;
					list2.Add(Math.Max(1, (int)((float)cpPrice - (float)cpPrice / num4)));
				}
			}
			if (globalPriceChange.ResearchPointChangePercentage <= 0)
			{
				continue;
			}
			foreach (int rpPrice in rpPrices)
			{
				float num5 = 100f / (float)globalPriceChange.ResearchPointChangePercentage;
				list4.Add(Math.Max(1, (int)((float)rpPrice - (float)rpPrice / num5)));
			}
		}
		silverPrices = silverPrices.Concat(list);
		goldPrices = goldPrices.Concat(list3);
		cpPrices = cpPrices.Concat(list2);
		rpPrices = rpPrices.Concat(list4);
	}

	public IEnumerable<Banner> GetBanners(Func<ShopRequirement, bool> requirementChecker, DateTime currentTime)
	{
		try
		{
			Banner[] banners = Banners;
			foreach (Banner banner in banners)
			{
				if (banner.FilterData.Type == BannerEventType.CashEvent)
				{
					CurrencyEvent currencyEvent = CurrencyEvents.LastOrDefault((CurrencyEvent x) => x.ID == banner.FilterData.EventID);
					if (EventIsActive(ref currencyEvent, currentTime) && (requirementChecker == null || !currencyEvent.UserGroupIDs.Any() || currencyEvent.UserGroupIDs.Any((int id) => GetUserGroup(id).Requirements.All((ShopRequirement r) => requirementChecker(r)))))
					{
						yield return banner;
					}
				}
				else if (banner.FilterData.Type == BannerEventType.ShopEvent)
				{
					ShopEvent shopEvent = Events.LastOrDefault((ShopEvent x) => x.ShopEventID == banner.FilterData.EventID);
					if (EventIsActive(ref shopEvent, currentTime) && (requirementChecker == null || !shopEvent.UserGroupIDs.Any() || shopEvent.UserGroupIDs.Any((int id) => GetUserGroup(id).Requirements.All((ShopRequirement r) => requirementChecker(r)))))
					{
						yield return banner;
					}
				}
				else
				{
					Banner bannerTemp = banner;
					if (BannerIsActive(ref bannerTemp, currentTime) && (requirementChecker == null || !banner.UserGroupIDs.Any() || banner.UserGroupIDs.Any((int id) => GetUserGroup(id).Requirements.All((ShopRequirement r) => requirementChecker(r)))))
					{
						yield return banner;
					}
				}
			}
		}
		finally
		{
		}
	}

	private UserGroup GetUserGroup(int id)
	{
		UserGroup value = default(UserGroup);
		_UserGroups.TryGetValue(id, out value);
		return value;
	}

	public static int[] GetValidCurrencyPrices(GoldPriceEntry entry, IEnumerable<CurrencyEvent> events)
	{
		List<int> list = new List<int>();
		foreach (CurrencyEvent @event in events)
		{
			CurrencyEventModification[] modifications = @event.Modifications;
			for (int i = 0; i < modifications.Length; i++)
			{
				CurrencyEventModification currencyEventModification = modifications[i];
				if (currencyEventModification.Package == entry.BoxID)
				{
					list.Add((int)Math.Round((float)entry.RealMoney * (1f - (float)(int)currencyEventModification.DiscountPrecentage * 0.01f)));
				}
			}
		}
		return list.ToArray();
	}
}
