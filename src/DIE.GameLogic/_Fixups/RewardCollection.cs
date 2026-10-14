using _0023rye;
using ConductorCrafting;
using StunGameNetwork;

namespace ConductorGameLogic;

public struct RewardCollection(RewardCollectionData data) : ISerializable
{
	public RewardCollectionData Data = data;

	public void Serialize(INetBuffer netBuffer)
	{
		netBuffer.Write((byte)0);
		netBuffer.Write((byte)0);
		netBuffer.Write((byte)Data.Items.Stackables.Count);
		foreach (Stackable stackable in Data.Items.Stackables)
		{
			netBuffer.Write(stackable.ID);
			netBuffer.Write((byte)stackable.Type);
			netBuffer.Write(stackable.Amount);
		}
		netBuffer.Write((byte)Data.Items.Uniques.Count);
		foreach (Unique unique in Data.Items.Uniques)
		{
			netBuffer.Write(unique.Guid);
			netBuffer.Write(unique.SchematicID);
		}
		netBuffer.Write(Data.Currencies.Gold);
		netBuffer.Write(Data.Currencies.Silver);
		netBuffer.Write(Data.Currencies.CharacterPoints);
		netBuffer.Write(Data.AccountExperience);
		netBuffer.Write(0);
		netBuffer.Write(0);
		netBuffer.Write(0);
	}

	public void Deserialize(INetBuffer netBuffer)
	{
		netBuffer._0023jae();
		netBuffer._0023jae();
		Data.Items = ItemCollection.NewItemCollection();
		int num = netBuffer._0023jae();
		for (int i = 0; i < num; i++)
		{
			Stackable item = new Stackable
			{
				ID = netBuffer._0023nae(),
				Type = (StackableType)netBuffer._0023jae(),
				Amount = netBuffer._0023nae()
			};
			Data.Items.Stackables.Add(item);
		}
		int num2 = netBuffer._0023jae();
		for (int j = 0; j < num2; j++)
		{
			Unique item2 = new Unique
			{
				Guid = netBuffer._0023kae(16),
				SchematicID = netBuffer._0023nae()
			};
			Data.Items.Uniques.Add(item2);
		}
		Data.Currencies.Gold = netBuffer._0023Hae();
		Data.Currencies.Silver = netBuffer._0023Hae();
		Data.Currencies.CharacterPoints = netBuffer._0023Hae();
		Data.AccountExperience = netBuffer._0023Hae();
		netBuffer._0023Hae();
		netBuffer._0023Hae();
		netBuffer._0023Hae();
	}

	public void SerializeVersion4(INetBuffer netBuffer)
	{
		netBuffer.Write((byte)Data.Items.Stackables.Count);
		foreach (Stackable stackable in Data.Items.Stackables)
		{
			netBuffer.Write(stackable.ID);
			netBuffer.Write((byte)stackable.Type);
			netBuffer.Write(stackable.Amount);
		}
		netBuffer.Write((byte)Data.Items.Uniques.Count);
		foreach (Unique unique in Data.Items.Uniques)
		{
			netBuffer.Write(unique.Guid);
			netBuffer.Write(unique.SchematicID);
		}
		netBuffer.Write((byte)Data.Items.Gadgets.Count);
		foreach (Gadget gadget in Data.Items.Gadgets)
		{
			netBuffer.Write(gadget.GUID);
			netBuffer.Write(gadget.GadgetID);
			if (gadget.Stats == null)
			{
				netBuffer.Write((byte)0);
				continue;
			}
			netBuffer.Write((byte)gadget.Stats.Length);
			netBuffer.Write(gadget.Stats);
		}
		netBuffer.Write(Data.Currencies.Gold);
		netBuffer.Write(Data.Currencies.Silver);
		netBuffer.Write(Data.Currencies.CharacterPoints);
		netBuffer.Write(Data.AccountExperience);
		netBuffer.Write(0);
		netBuffer.Write(0);
		netBuffer.Write(0);
	}

	public void SerializeVersion8(INetBuffer netBuffer)
	{
		netBuffer.Write((byte)Data.Items.Stackables.Count);
		foreach (Stackable stackable in Data.Items.Stackables)
		{
			netBuffer.Write(stackable.ID);
			netBuffer.Write((byte)stackable.Type);
			netBuffer.Write(stackable.Amount);
		}
		netBuffer.Write((byte)Data.Items.Uniques.Count);
		foreach (Unique unique in Data.Items.Uniques)
		{
			netBuffer.Write(unique.Guid);
			netBuffer.Write(unique.SchematicID);
		}
		netBuffer.Write((byte)Data.Items.Gadgets.Count);
		foreach (Gadget gadget in Data.Items.Gadgets)
		{
			netBuffer.Write(gadget.GUID);
			netBuffer.Write(gadget.GadgetID);
			if (gadget.Stats == null)
			{
				netBuffer.Write((byte)0);
				continue;
			}
			netBuffer.Write((byte)gadget.Stats.Length);
			netBuffer.Write(gadget.Stats);
		}
		netBuffer.Write(Data.Currencies.Gold);
		netBuffer.Write(Data.Currencies.Silver);
		netBuffer.Write(Data.Currencies.CharacterPoints);
		netBuffer.Write(Data.Currencies.ResearchPoints);
		netBuffer.Write(Data.AccountExperience);
	}

	public void DeserializeVersion4(INetBuffer netBuffer)
	{
		Data.Items = ItemCollection.NewItemCollection();
		int num = netBuffer._0023jae();
		for (int i = 0; i < num; i++)
		{
			Stackable item = new Stackable
			{
				ID = netBuffer._0023nae(),
				Type = (StackableType)netBuffer._0023jae(),
				Amount = netBuffer._0023nae()
			};
			Data.Items.Stackables.Add(item);
		}
		int num2 = netBuffer._0023jae();
		for (int j = 0; j < num2; j++)
		{
			Unique item2 = new Unique
			{
				Guid = netBuffer._0023kae(16),
				SchematicID = netBuffer._0023nae()
			};
			Data.Items.Uniques.Add(item2);
		}
		int num3 = netBuffer._0023jae();
		for (int k = 0; k < num3; k++)
		{
			Gadget item3 = new Gadget
			{
				GUID = netBuffer._0023kae(16),
				GadgetID = netBuffer._0023nae()
			};
			byte b = netBuffer._0023jae();
			item3.Stats = netBuffer._0023kae(b);
			Data.Items.Gadgets.Add(item3);
		}
		Data.Currencies.Gold = netBuffer._0023Hae();
		Data.Currencies.Silver = netBuffer._0023Hae();
		Data.Currencies.CharacterPoints = netBuffer._0023Hae();
		Data.AccountExperience = netBuffer._0023Hae();
		netBuffer._0023Hae();
		netBuffer._0023Hae();
		netBuffer._0023Hae();
	}

	public void DeserializeVersion8(INetBuffer netBuffer)
	{
		Data.Items = ItemCollection.NewItemCollection();
		int num = netBuffer._0023jae();
		for (int i = 0; i < num; i++)
		{
			Stackable item = new Stackable
			{
				ID = netBuffer._0023nae(),
				Type = (StackableType)netBuffer._0023jae(),
				Amount = netBuffer._0023nae()
			};
			Data.Items.Stackables.Add(item);
		}
		int num2 = netBuffer._0023jae();
		for (int j = 0; j < num2; j++)
		{
			Unique item2 = new Unique
			{
				Guid = netBuffer._0023kae(16),
				SchematicID = netBuffer._0023nae()
			};
			Data.Items.Uniques.Add(item2);
		}
		int num3 = netBuffer._0023jae();
		for (int k = 0; k < num3; k++)
		{
			Gadget item3 = new Gadget
			{
				GUID = netBuffer._0023kae(16),
				GadgetID = netBuffer._0023nae()
			};
			byte b = netBuffer._0023jae();
			if (b != 0)
			{
				item3.Stats = netBuffer._0023kae(b);
			}
			else
			{
				item3.Stats = new byte[0];
			}
			Data.Items.Gadgets.Add(item3);
		}
		Data.Currencies.Gold = netBuffer._0023Hae();
		Data.Currencies.Silver = netBuffer._0023Hae();
		Data.Currencies.CharacterPoints = netBuffer._0023Hae();
		Data.Currencies.ResearchPoints = netBuffer._0023Hae();
		Data.AccountExperience = netBuffer._0023Hae();
	}
}
