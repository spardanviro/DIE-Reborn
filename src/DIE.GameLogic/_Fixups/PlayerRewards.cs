using _0023rye;
using ConductorCrafting;
using ConductorGameLogic.Entities;
using StunGameNetwork;

namespace ConductorGameLogic;

public struct PlayerRewards(PlayerRewardData data, PlayerRewards.Player[] playersInMatch) : ISerializable
{
	public struct Player(ulong steamID, TeamId team)
	{
		public ulong SteamID = steamID;

		public TeamId Team = team;

		public void Serialize(INetBuffer netBuffer)
		{
			netBuffer.Write(SteamID);
			netBuffer.Write((byte)Team);
		}

		public void Deserialize(INetBuffer netBuffer)
		{
			SteamID = netBuffer._0023Iae();
			Team = (TeamId)netBuffer._0023jae();
		}
	}

	public const byte Version = 9;

	public PlayerRewardData Data = data;

	public byte RewardVersion = 9;

	public Player[] PlayersInMatch = playersInMatch;

	public void Serialize(INetBuffer netBuffer)
	{
		netBuffer.Write((byte)9);
		_0023NKs(netBuffer);
	}

	public void Deserialize(INetBuffer netBuffer)
	{
		RewardVersion = netBuffer._0023jae();
		switch (RewardVersion)
		{
		case 1:
		case 2:
			_0023nfc(netBuffer);
			break;
		case 3:
			_0023ofc(netBuffer);
			break;
		case 4:
			DeserializeVersion4(netBuffer);
			break;
		case 5:
			_0023pfc(netBuffer);
			break;
		case 6:
			_0023qfc(netBuffer);
			break;
		case 7:
			_0023rfc(netBuffer);
			break;
		case 8:
			DeserializeVersion8(netBuffer);
			break;
		case 9:
			_0023OKs(netBuffer);
			break;
		}
	}

	private void _0023jfc(INetBuffer _00237d)
	{
		_00237d.Write(Data.PlayerKills);
		_00237d.Write(Data.ZombieKills);
		_00237d.Write(Data.Supplies);
		_00237d.Write(Data.Deaths);
		_00237d.Write((byte)0);
		_00237d.Write(0);
		_00237d.Write(Data.StartTimeTicks);
		_00237d.Write(Data.MatchTimeSeconds);
		_00237d.Write((byte)Data.Placement);
		_00237d.Write((byte)Data.Character);
		_00237d.Write((byte)Data.GameMode);
		int num = 1;
		_00237d.Write((byte)num);
		new RewardCollection(Data.Rewards).Serialize(_00237d);
		int num2 = ((PlayersInMatch != null) ? PlayersInMatch.Length : 0);
		_00237d.Write((byte)num2);
		for (int i = 0; i < num2; i++)
		{
			PlayersInMatch[i].Serialize(_00237d);
		}
		_00237d.Write((byte)Data.MapIndex);
		_00237d.Write((byte)Data.LevelReached);
		_00237d.Write((ushort)Data.Consumable1Uses);
		_00237d.Write((ushort)Data.Consumable2Uses);
		_00237d.Write((ushort)Data.Consumable3Uses);
		_00237d.Write((byte)0);
		_00237d.Write(Data.TeamSupplies);
		_00237d.Write(0);
		_00237d.Write(Data.Team);
		_00237d.Write(Data.LootTier);
		_00237d.Write(Data.InfectionLevel);
	}

	private void SerializeVersion4(INetBuffer _00237d)
	{
		_00237d.Write(Data.PlayerKills);
		_00237d.Write(Data.ZombieKills);
		_00237d.Write(Data.Supplies);
		_00237d.Write(Data.Deaths);
		_00237d.Write(Data.StartTimeTicks);
		_00237d.Write(Data.MatchTimeSeconds);
		_00237d.Write((byte)Data.Placement);
		_00237d.Write((byte)Data.Character);
		_00237d.Write((byte)Data.GameMode);
		new RewardCollection(Data.Rewards).SerializeVersion4(_00237d);
		int num = ((PlayersInMatch != null) ? PlayersInMatch.Length : 0);
		_00237d.Write((byte)num);
		for (int i = 0; i < num; i++)
		{
			PlayersInMatch[i].Serialize(_00237d);
		}
		int num2 = ((Data.BoxScores != null) ? Data.BoxScores.Length : 0);
		_00237d.Write((byte)num2);
		for (int j = 0; j < num2; j++)
		{
			Serialize(_00237d, Data.BoxScores[j]);
		}
		_00237d.Write((byte)Data.MapIndex);
		_00237d.Write((byte)Data.LevelReached);
		_00237d.Write((ushort)Data.Consumable1Uses);
		_00237d.Write((ushort)Data.Consumable2Uses);
		_00237d.Write((ushort)Data.Consumable3Uses);
		_00237d.Write(Data.TeamSupplies);
		_00237d.Write(Data.Team);
		_00237d.Write(Data.LootTier);
		_00237d.Write(Data.InfectionLevel);
	}

	private void _0023kfc(INetBuffer _00237d)
	{
		_00237d.Write(Data.PlayerKills);
		_00237d.Write(Data.ZombieKills);
		_00237d.Write(Data.Supplies);
		_00237d.Write(Data.Deaths);
		_00237d.Write(Data.StartTimeTicks);
		_00237d.Write(Data.MatchTimeSeconds);
		_00237d.Write((byte)Data.Placement);
		_00237d.Write((byte)Data.Character);
		_00237d.Write((byte)Data.GameMode);
		new RewardCollection(Data.Rewards).SerializeVersion4(_00237d);
		int num = ((PlayersInMatch != null) ? PlayersInMatch.Length : 0);
		_00237d.Write((byte)num);
		for (int i = 0; i < num; i++)
		{
			PlayersInMatch[i].Serialize(_00237d);
		}
		int num2 = ((Data.BoxScores != null) ? Data.BoxScores.Length : 0);
		_00237d.Write((byte)num2);
		for (int j = 0; j < num2; j++)
		{
			Serialize(_00237d, Data.BoxScores[j]);
		}
		_00237d.Write((byte)Data.MapIndex);
		_00237d.Write((byte)Data.LevelReached);
		_00237d.Write((ushort)Data.Consumable1Uses);
		_00237d.Write((ushort)Data.Consumable2Uses);
		_00237d.Write((ushort)Data.Consumable3Uses);
		_00237d.Write(Data.TeamSupplies);
		_00237d.Write(Data.Team);
		_00237d.Write(Data.LootTier);
		_00237d.Write(Data.InfectionLevel);
		int num3 = ((Data.Weapon1 != null) ? Data.Weapon1.Length : 0);
		_00237d.Write((byte)num3);
		if (Data.Weapon1 != null)
		{
			_00237d.Write(Data.Weapon1);
		}
		num3 = ((Data.Weapon2 != null) ? Data.Weapon2.Length : 0);
		_00237d.Write((byte)num3);
		if (Data.Weapon2 != null)
		{
			_00237d.Write(Data.Weapon2);
		}
	}

	private void _0023lfc(INetBuffer _00237d)
	{
		_00237d.Write(Data.PlayerKills);
		_00237d.Write(Data.ZombieKills);
		_00237d.Write(Data.Supplies);
		_00237d.Write(Data.Deaths);
		_00237d.Write(Data.StartTimeTicks);
		_00237d.Write(Data.MatchTimeSeconds);
		_00237d.Write((byte)Data.Placement);
		_00237d.Write((byte)Data.Character);
		_00237d.Write((byte)Data.GameMode);
		new RewardCollection(Data.Rewards).SerializeVersion4(_00237d);
		int num = ((PlayersInMatch != null) ? PlayersInMatch.Length : 0);
		_00237d.Write((byte)num);
		for (int i = 0; i < num; i++)
		{
			PlayersInMatch[i].Serialize(_00237d);
		}
		int num2 = ((Data.BoxScores != null) ? Data.BoxScores.Length : 0);
		_00237d.Write((byte)num2);
		for (int j = 0; j < num2; j++)
		{
			Serialize(_00237d, Data.BoxScores[j]);
		}
		_00237d.Write((byte)Data.MapIndex);
		_00237d.Write((byte)Data.LevelReached);
		_00237d.Write((ushort)Data.Consumable1Uses);
		_00237d.Write((ushort)Data.Consumable2Uses);
		_00237d.Write((ushort)Data.Consumable3Uses);
		_00237d.Write(Data.TeamSupplies);
		_00237d.Write(Data.Team);
		_00237d.Write(Data.LootTier);
		_00237d.Write(Data.InfectionLevel);
		int num3 = ((Data.Weapon1 != null) ? Data.Weapon1.Length : 0);
		_00237d.Write((byte)num3);
		if (Data.Weapon1 != null)
		{
			_00237d.Write(Data.Weapon1);
		}
		num3 = ((Data.Weapon2 != null) ? Data.Weapon2.Length : 0);
		_00237d.Write((byte)num3);
		if (Data.Weapon2 != null)
		{
			_00237d.Write(Data.Weapon2);
		}
		num3 = ((Data.NewUnlockedNodes != null) ? Data.NewUnlockedNodes.Length : 0);
		_00237d.Write((byte)num3);
		if (Data.NewUnlockedNodes != null)
		{
			for (int k = 0; k < Data.NewUnlockedNodes.Length; k++)
			{
				_00237d.Write(Data.NewUnlockedNodes[k]);
			}
		}
	}

	private void _0023mfc(INetBuffer _00237d)
	{
		_00237d.Write(Data.PlayerKills);
		_00237d.Write(Data.ZombieKills);
		_00237d.Write(Data.Supplies);
		_00237d.Write(Data.Deaths);
		_00237d.Write(Data.StartTimeTicks);
		_00237d.Write(Data.MatchTimeSeconds);
		_00237d.Write((byte)Data.Placement);
		_00237d.Write((byte)Data.Character);
		_00237d.Write((byte)Data.GameMode);
		new RewardCollection(Data.Rewards).SerializeVersion4(_00237d);
		int num = ((PlayersInMatch != null) ? PlayersInMatch.Length : 0);
		_00237d.Write((byte)num);
		for (int i = 0; i < num; i++)
		{
			PlayersInMatch[i].Serialize(_00237d);
		}
		int num2 = ((Data.BoxScores != null) ? Data.BoxScores.Length : 0);
		_00237d.Write((byte)num2);
		for (int j = 0; j < num2; j++)
		{
			Serialize(_00237d, Data.BoxScores[j]);
		}
		_00237d.Write((byte)Data.MapIndex);
		_00237d.Write((byte)Data.LevelReached);
		_00237d.Write((ushort)Data.Consumable1Uses);
		_00237d.Write((ushort)Data.Consumable2Uses);
		_00237d.Write((ushort)Data.Consumable3Uses);
		_00237d.Write(Data.TeamSupplies);
		_00237d.Write(Data.Team);
		_00237d.Write(Data.LootTier);
		_00237d.Write(Data.InfectionLevel);
		int num3 = ((Data.Weapon1 != null) ? Data.Weapon1.Length : 0);
		_00237d.Write((byte)num3);
		if (Data.Weapon1 != null)
		{
			_00237d.Write(Data.Weapon1);
		}
		num3 = ((Data.Weapon2 != null) ? Data.Weapon2.Length : 0);
		_00237d.Write((byte)num3);
		if (Data.Weapon2 != null)
		{
			_00237d.Write(Data.Weapon2);
		}
		num3 = ((Data.NewUnlockedNodes != null) ? Data.NewUnlockedNodes.Length : 0);
		_00237d.Write((byte)num3);
		if (Data.NewUnlockedNodes != null)
		{
			for (int k = 0; k < Data.NewUnlockedNodes.Length; k++)
			{
				_00237d.Write(Data.NewUnlockedNodes[k]);
			}
		}
	}

	private void SerializeVersion8(INetBuffer _00237d)
	{
		_00237d.Write(Data.PlayerKills);
		_00237d.Write(Data.ZombieKills);
		_00237d.Write(Data.Supplies);
		_00237d.Write(Data.Deaths);
		_00237d.Write(Data.StartTimeTicks);
		_00237d.Write(Data.MatchTimeSeconds);
		_00237d.Write((byte)Data.Placement);
		_00237d.Write((byte)Data.Character);
		_00237d.Write((byte)Data.GameMode);
		new RewardCollection(Data.Rewards).SerializeVersion8(_00237d);
		int num = ((PlayersInMatch != null) ? PlayersInMatch.Length : 0);
		_00237d.Write((byte)num);
		for (int i = 0; i < num; i++)
		{
			PlayersInMatch[i].Serialize(_00237d);
		}
		int num2 = ((Data.BoxScores != null) ? Data.BoxScores.Length : 0);
		_00237d.Write((byte)num2);
		for (int j = 0; j < num2; j++)
		{
			Serialize(_00237d, Data.BoxScores[j]);
		}
		_00237d.Write((byte)Data.MapIndex);
		_00237d.Write((byte)Data.LevelReached);
		_00237d.Write((ushort)Data.Consumable1Uses);
		_00237d.Write((ushort)Data.Consumable2Uses);
		_00237d.Write((ushort)Data.Consumable3Uses);
		_00237d.Write(Data.TeamSupplies);
		_00237d.Write(Data.Team);
		_00237d.Write(Data.LootTier);
		_00237d.Write(Data.InfectionLevel);
		int num3 = ((Data.Weapon1 != null) ? Data.Weapon1.Length : 0);
		_00237d.Write((byte)num3);
		if (Data.Weapon1 != null)
		{
			_00237d.Write(Data.Weapon1);
		}
		num3 = ((Data.Weapon2 != null) ? Data.Weapon2.Length : 0);
		_00237d.Write((byte)num3);
		if (Data.Weapon2 != null)
		{
			_00237d.Write(Data.Weapon2);
		}
		num3 = ((Data.NewUnlockedNodes != null) ? Data.NewUnlockedNodes.Length : 0);
		_00237d.Write((byte)num3);
		if (Data.NewUnlockedNodes != null)
		{
			for (int k = 0; k < Data.NewUnlockedNodes.Length; k++)
			{
				_00237d.Write(Data.NewUnlockedNodes[k]);
			}
		}
		_00237d.Write(Data.CrossroadProgress);
		num3 = ((Data.CrossroadCompleteScores != null) ? Data.CrossroadCompleteScores.Length : 0);
		_00237d.Write((byte)num3);
		if (Data.CrossroadCompleteScores != null)
		{
			_00237d.Write(Data.CrossroadCompleteScores);
		}
		bool flag = Data.FirstWinRewards.Items != default(ItemCollection);
		_00237d.Write(flag);
		if (flag)
		{
			new RewardCollection(Data.FirstWinRewards).SerializeVersion8(_00237d);
		}
	}

	private void _0023NKs(INetBuffer _00237d)
	{
		_00237d.Write(Data.PlayerKills);
		_00237d.Write(Data.ZombieKills);
		_00237d.Write(Data.Supplies);
		_00237d.Write(Data.Deaths);
		_00237d.Write(Data.StartTimeTicks);
		_00237d.Write(Data.MatchTimeSeconds);
		_00237d.Write((byte)Data.Placement);
		_00237d.Write((byte)Data.Character);
		_00237d.Write((byte)Data.GameMode);
		new RewardCollection(Data.Rewards).SerializeVersion8(_00237d);
		int num = ((PlayersInMatch != null) ? PlayersInMatch.Length : 0);
		_00237d.Write((byte)num);
		for (int i = 0; i < num; i++)
		{
			PlayersInMatch[i].Serialize(_00237d);
		}
		int num2 = ((Data.BoxScores != null) ? Data.BoxScores.Length : 0);
		_00237d.Write((byte)num2);
		for (int j = 0; j < num2; j++)
		{
			Serialize(_00237d, Data.BoxScores[j]);
		}
		_00237d.Write((byte)Data.MapIndex);
		_00237d.Write((byte)Data.LevelReached);
		_00237d.Write((ushort)Data.Consumable1Uses);
		_00237d.Write((ushort)Data.Consumable2Uses);
		_00237d.Write((ushort)Data.Consumable3Uses);
		_00237d.Write(Data.TeamSupplies);
		_00237d.Write(Data.Team);
		_00237d.Write(Data.LootTier);
		_00237d.Write(Data.InfectionLevel);
		int num3 = ((Data.Weapon1 != null) ? Data.Weapon1.Length : 0);
		_00237d.Write((byte)num3);
		if (Data.Weapon1 != null)
		{
			_00237d.Write(Data.Weapon1);
		}
		num3 = ((Data.Weapon2 != null) ? Data.Weapon2.Length : 0);
		_00237d.Write((byte)num3);
		if (Data.Weapon2 != null)
		{
			_00237d.Write(Data.Weapon2);
		}
		num3 = ((Data.NewUnlockedNodes != null) ? Data.NewUnlockedNodes.Length : 0);
		_00237d.Write((byte)num3);
		if (Data.NewUnlockedNodes != null)
		{
			for (int k = 0; k < Data.NewUnlockedNodes.Length; k++)
			{
				_00237d.Write(Data.NewUnlockedNodes[k]);
			}
		}
		_00237d.Write(Data.CrossroadProgress);
		num3 = ((Data.CrossroadCompleteScores != null) ? Data.CrossroadCompleteScores.Length : 0);
		_00237d.Write((byte)num3);
		if (Data.CrossroadCompleteScores != null)
		{
			_00237d.Write(Data.CrossroadCompleteScores);
		}
		num3 = ((Data.CrossroadMissionTypes != null) ? Data.CrossroadMissionTypes.Length : 0);
		_00237d.Write((byte)num3);
		if (Data.CrossroadMissionTypes != null)
		{
			_00237d.Write(Data.CrossroadMissionTypes);
		}
		bool flag = Data.FirstWinRewards.Items != default(ItemCollection);
		_00237d.Write(flag);
		if (flag)
		{
			new RewardCollection(Data.FirstWinRewards).SerializeVersion8(_00237d);
		}
		_00237d.Write(Data.Healing);
		_00237d.Write(Data.Damage);
		_00237d.Write(Data.Assists);
	}

	private void Serialize(INetBuffer _00237d, BoxScore _0023W5)
	{
		_00237d.Write((byte)_0023W5.ID);
		_00237d.Write(_0023W5.Score);
	}

	public BoxScore DeserializeBoxScore(INetBuffer netBuffer)
	{
		BoxScore result = default(BoxScore);
		result.ID = (DropTableBoxID)netBuffer._0023jae();
		result.Score = netBuffer._0023Hae();
		return result;
	}

	private void _0023nfc(INetBuffer _00237d)
	{
		Data.PlayerKills = _00237d._0023Hae();
		Data.ZombieKills = _00237d._0023Hae();
		Data.Supplies = _00237d._0023Hae();
		Data.Deaths = _00237d._0023Hae();
		int num = _00237d._0023jae();
		for (int i = 0; i < num; i++)
		{
			_00237d._0023jae();
		}
		_00237d._0023Hae();
		Data.StartTimeTicks = _00237d._0023Jae();
		Data.MatchTimeSeconds = _00237d._0023oae();
		Data.Placement = (Placement)_00237d._0023jae();
		Data.Character = (CharacterEnum)_00237d._0023jae();
		Data.GameMode = (QueueType)_00237d._0023jae();
		int num2 = _00237d._0023jae();
		for (int j = 0; j < num2; j++)
		{
			default(RewardCollection).Deserialize(_00237d);
		}
		int num3 = _00237d._0023jae();
		PlayersInMatch = new Player[num3];
		for (int k = 0; k < num3; k++)
		{
			PlayersInMatch[k].Deserialize(_00237d);
		}
		Data.MapIndex = _00237d._0023jae();
		Data.LevelReached = _00237d._0023jae();
		Data.Consumable1Uses = _00237d._0023nae();
		Data.Consumable2Uses = _00237d._0023nae();
		Data.Consumable3Uses = _00237d._0023nae();
		byte b = _00237d._0023jae();
		for (int l = 0; l < b; l++)
		{
			_00237d._0023kae(16);
		}
		Data.TeamSupplies = _00237d._0023Hae();
		_00237d._0023Fae();
	}

	private void _0023ofc(INetBuffer _00237d)
	{
		Data.PlayerKills = _00237d._0023Hae();
		Data.ZombieKills = _00237d._0023Hae();
		Data.Supplies = _00237d._0023Hae();
		Data.Deaths = _00237d._0023Hae();
		int num = _00237d._0023jae();
		for (int i = 0; i < num; i++)
		{
			_00237d._0023jae();
		}
		_00237d._0023Hae();
		Data.StartTimeTicks = _00237d._0023Jae();
		Data.MatchTimeSeconds = _00237d._0023oae();
		Data.Placement = (Placement)_00237d._0023jae();
		Data.Character = (CharacterEnum)_00237d._0023jae();
		Data.GameMode = (QueueType)_00237d._0023jae();
		int num2 = _00237d._0023jae();
		for (int j = 0; j < num2; j++)
		{
			default(RewardCollection).Deserialize(_00237d);
		}
		int num3 = _00237d._0023jae();
		PlayersInMatch = new Player[num3];
		for (int k = 0; k < num3; k++)
		{
			PlayersInMatch[k].Deserialize(_00237d);
		}
		Data.MapIndex = _00237d._0023jae();
		Data.LevelReached = _00237d._0023jae();
		Data.Consumable1Uses = _00237d._0023nae();
		Data.Consumable2Uses = _00237d._0023nae();
		Data.Consumable3Uses = _00237d._0023nae();
		byte b = _00237d._0023jae();
		for (int l = 0; l < b; l++)
		{
			_00237d._0023kae(16);
		}
		Data.TeamSupplies = _00237d._0023Hae();
		_00237d._0023Fae();
		Data.Team = _00237d._0023jae();
		Data.LootTier = _00237d._0023jae();
		Data.InfectionLevel = _00237d._0023jae();
	}

	private void DeserializeVersion4(INetBuffer _00237d)
	{
		Data.PlayerKills = _00237d._0023Hae();
		Data.ZombieKills = _00237d._0023Hae();
		Data.Supplies = _00237d._0023Hae();
		Data.Deaths = _00237d._0023Hae();
		Data.StartTimeTicks = _00237d._0023Jae();
		Data.MatchTimeSeconds = _00237d._0023oae();
		Data.Placement = (Placement)_00237d._0023jae();
		Data.Character = (CharacterEnum)_00237d._0023jae();
		Data.GameMode = (QueueType)_00237d._0023jae();
		RewardCollection rewardCollection = default(RewardCollection);
		rewardCollection.DeserializeVersion4(_00237d);
		Data.Rewards = rewardCollection.Data;
		int num = _00237d._0023jae();
		PlayersInMatch = new Player[num];
		for (int i = 0; i < num; i++)
		{
			PlayersInMatch[i].Deserialize(_00237d);
		}
		int num2 = _00237d._0023jae();
		Data.BoxScores = new BoxScore[num2];
		for (int j = 0; j < num2; j++)
		{
			ref BoxScore reference = ref Data.BoxScores[j];
			reference = DeserializeBoxScore(_00237d);
		}
		Data.MapIndex = _00237d._0023jae();
		Data.LevelReached = _00237d._0023jae();
		Data.Consumable1Uses = _00237d._0023nae();
		Data.Consumable2Uses = _00237d._0023nae();
		Data.Consumable3Uses = _00237d._0023nae();
		Data.TeamSupplies = _00237d._0023Hae();
		Data.Team = _00237d._0023jae();
		Data.LootTier = _00237d._0023jae();
		Data.InfectionLevel = _00237d._0023jae();
	}

	private void _0023pfc(INetBuffer _00237d)
	{
		Data.PlayerKills = _00237d._0023Hae();
		Data.ZombieKills = _00237d._0023Hae();
		Data.Supplies = _00237d._0023Hae();
		Data.Deaths = _00237d._0023Hae();
		Data.StartTimeTicks = _00237d._0023Jae();
		Data.MatchTimeSeconds = _00237d._0023oae();
		Data.Placement = (Placement)_00237d._0023jae();
		Data.Character = (CharacterEnum)_00237d._0023jae();
		Data.GameMode = (QueueType)_00237d._0023jae();
		RewardCollection rewardCollection = default(RewardCollection);
		rewardCollection.DeserializeVersion4(_00237d);
		Data.Rewards = rewardCollection.Data;
		int num = _00237d._0023jae();
		PlayersInMatch = new Player[num];
		for (int i = 0; i < num; i++)
		{
			PlayersInMatch[i].Deserialize(_00237d);
		}
		int num2 = _00237d._0023jae();
		Data.BoxScores = new BoxScore[num2];
		for (int j = 0; j < num2; j++)
		{
			ref BoxScore reference = ref Data.BoxScores[j];
			reference = DeserializeBoxScore(_00237d);
		}
		Data.MapIndex = _00237d._0023jae();
		Data.LevelReached = _00237d._0023jae();
		Data.Consumable1Uses = _00237d._0023nae();
		Data.Consumable2Uses = _00237d._0023nae();
		Data.Consumable3Uses = _00237d._0023nae();
		Data.TeamSupplies = _00237d._0023Hae();
		Data.Team = _00237d._0023jae();
		Data.LootTier = _00237d._0023jae();
		Data.InfectionLevel = _00237d._0023jae();
		int num3 = _00237d._0023jae();
		if (num3 != 0)
		{
			Data.Weapon1 = _00237d._0023kae(num3);
		}
		num3 = _00237d._0023jae();
		if (num3 != 0)
		{
			Data.Weapon2 = _00237d._0023kae(num3);
		}
	}

	private void _0023qfc(INetBuffer _00237d)
	{
		Data.PlayerKills = _00237d._0023Hae();
		Data.ZombieKills = _00237d._0023Hae();
		Data.Supplies = _00237d._0023Hae();
		Data.Deaths = _00237d._0023Hae();
		Data.StartTimeTicks = _00237d._0023Jae();
		Data.MatchTimeSeconds = _00237d._0023oae();
		Data.Placement = (Placement)_00237d._0023jae();
		Data.Character = (CharacterEnum)_00237d._0023jae();
		Data.GameMode = (QueueType)_00237d._0023jae();
		RewardCollection rewardCollection = default(RewardCollection);
		rewardCollection.DeserializeVersion4(_00237d);
		Data.Rewards = rewardCollection.Data;
		int num = _00237d._0023jae();
		PlayersInMatch = new Player[num];
		for (int i = 0; i < num; i++)
		{
			PlayersInMatch[i].Deserialize(_00237d);
		}
		int num2 = _00237d._0023jae();
		Data.BoxScores = new BoxScore[num2];
		for (int j = 0; j < num2; j++)
		{
			ref BoxScore reference = ref Data.BoxScores[j];
			reference = DeserializeBoxScore(_00237d);
		}
		Data.MapIndex = _00237d._0023jae();
		Data.LevelReached = _00237d._0023jae();
		Data.Consumable1Uses = _00237d._0023nae();
		Data.Consumable2Uses = _00237d._0023nae();
		Data.Consumable3Uses = _00237d._0023nae();
		Data.TeamSupplies = _00237d._0023Hae();
		Data.Team = _00237d._0023jae();
		Data.LootTier = _00237d._0023jae();
		Data.InfectionLevel = _00237d._0023jae();
		int num3 = _00237d._0023jae();
		if (num3 != 0)
		{
			Data.Weapon1 = _00237d._0023kae(num3);
		}
		num3 = _00237d._0023jae();
		if (num3 != 0)
		{
			Data.Weapon2 = _00237d._0023kae(num3);
		}
		num3 = _00237d._0023jae();
		if (num3 > 0)
		{
			Data.NewUnlockedNodes = new int[num3];
			for (int k = 0; k < num3; k++)
			{
				Data.NewUnlockedNodes[k] = _00237d._0023nae();
			}
		}
	}

	private void _0023rfc(INetBuffer _00237d)
	{
		Data.PlayerKills = _00237d._0023Hae();
		Data.ZombieKills = _00237d._0023Hae();
		Data.Supplies = _00237d._0023Hae();
		Data.Deaths = _00237d._0023Hae();
		Data.StartTimeTicks = _00237d._0023Jae();
		Data.MatchTimeSeconds = _00237d._0023oae();
		Data.Placement = (Placement)_00237d._0023jae();
		Data.Character = (CharacterEnum)_00237d._0023jae();
		Data.GameMode = (QueueType)_00237d._0023jae();
		RewardCollection rewardCollection = default(RewardCollection);
		rewardCollection.DeserializeVersion4(_00237d);
		Data.Rewards = rewardCollection.Data;
		int num = _00237d._0023jae();
		PlayersInMatch = new Player[num];
		for (int i = 0; i < num; i++)
		{
			PlayersInMatch[i].Deserialize(_00237d);
		}
		int num2 = _00237d._0023jae();
		Data.BoxScores = new BoxScore[num2];
		for (int j = 0; j < num2; j++)
		{
			ref BoxScore reference = ref Data.BoxScores[j];
			reference = DeserializeBoxScore(_00237d);
		}
		Data.MapIndex = _00237d._0023jae();
		Data.LevelReached = _00237d._0023jae();
		Data.Consumable1Uses = _00237d._0023nae();
		Data.Consumable2Uses = _00237d._0023nae();
		Data.Consumable3Uses = _00237d._0023nae();
		Data.TeamSupplies = _00237d._0023Hae();
		Data.Team = _00237d._0023jae();
		Data.LootTier = _00237d._0023jae();
		Data.InfectionLevel = _00237d._0023jae();
		int num3 = _00237d._0023jae();
		if (num3 != 0)
		{
			Data.Weapon1 = _00237d._0023kae(num3);
		}
		num3 = _00237d._0023jae();
		if (num3 != 0)
		{
			Data.Weapon2 = _00237d._0023kae(num3);
		}
		num3 = _00237d._0023jae();
		if (num3 > 0)
		{
			Data.NewUnlockedNodes = new int[num3];
			for (int k = 0; k < num3; k++)
			{
				Data.NewUnlockedNodes[k] = _00237d._0023Hae();
			}
		}
	}

	private void DeserializeVersion8(INetBuffer _00237d)
	{
		Data.PlayerKills = _00237d._0023Hae();
		Data.ZombieKills = _00237d._0023Hae();
		Data.Supplies = _00237d._0023Hae();
		Data.Deaths = _00237d._0023Hae();
		Data.StartTimeTicks = _00237d._0023Jae();
		Data.MatchTimeSeconds = _00237d._0023oae();
		Data.Placement = (Placement)_00237d._0023jae();
		Data.Character = (CharacterEnum)_00237d._0023jae();
		Data.GameMode = (QueueType)_00237d._0023jae();
		RewardCollection rewardCollection = default(RewardCollection);
		rewardCollection.DeserializeVersion8(_00237d);
		Data.Rewards = rewardCollection.Data;
		int num = _00237d._0023jae();
		PlayersInMatch = new Player[num];
		for (int i = 0; i < num; i++)
		{
			PlayersInMatch[i].Deserialize(_00237d);
		}
		int num2 = _00237d._0023jae();
		Data.BoxScores = new BoxScore[num2];
		for (int j = 0; j < num2; j++)
		{
			ref BoxScore reference = ref Data.BoxScores[j];
			reference = DeserializeBoxScore(_00237d);
		}
		Data.MapIndex = _00237d._0023jae();
		Data.LevelReached = _00237d._0023jae();
		Data.Consumable1Uses = _00237d._0023nae();
		Data.Consumable2Uses = _00237d._0023nae();
		Data.Consumable3Uses = _00237d._0023nae();
		Data.TeamSupplies = _00237d._0023Hae();
		Data.Team = _00237d._0023jae();
		Data.LootTier = _00237d._0023jae();
		Data.InfectionLevel = _00237d._0023jae();
		int num3 = _00237d._0023jae();
		if (num3 != 0)
		{
			Data.Weapon1 = _00237d._0023kae(num3);
		}
		num3 = _00237d._0023jae();
		if (num3 != 0)
		{
			Data.Weapon2 = _00237d._0023kae(num3);
		}
		num3 = _00237d._0023jae();
		if (num3 > 0)
		{
			Data.NewUnlockedNodes = new int[num3];
			for (int k = 0; k < num3; k++)
			{
				Data.NewUnlockedNodes[k] = _00237d._0023Hae();
			}
		}
		Data.CrossroadProgress = _00237d._0023nae();
		num3 = _00237d._0023jae();
		if (num3 != 0)
		{
			Data.CrossroadCompleteScores = _00237d._0023kae(num3);
		}
		if (_00237d._0023Fae())
		{
			RewardCollection rewardCollection2 = default(RewardCollection);
			rewardCollection2.DeserializeVersion8(_00237d);
			Data.FirstWinRewards = rewardCollection2.Data;
		}
		else
		{
			Data.FirstWinRewards.Items = ItemCollection.NewItemCollection();
		}
	}

	private void _0023OKs(INetBuffer _00237d)
	{
		Data.PlayerKills = _00237d._0023Hae();
		Data.ZombieKills = _00237d._0023Hae();
		Data.Supplies = _00237d._0023Hae();
		Data.Deaths = _00237d._0023Hae();
		Data.StartTimeTicks = _00237d._0023Jae();
		Data.MatchTimeSeconds = _00237d._0023oae();
		Data.Placement = (Placement)_00237d._0023jae();
		Data.Character = (CharacterEnum)_00237d._0023jae();
		Data.GameMode = (QueueType)_00237d._0023jae();
		RewardCollection rewardCollection = default(RewardCollection);
		rewardCollection.DeserializeVersion8(_00237d);
		Data.Rewards = rewardCollection.Data;
		int num = _00237d._0023jae();
		PlayersInMatch = new Player[num];
		for (int i = 0; i < num; i++)
		{
			PlayersInMatch[i].Deserialize(_00237d);
		}
		int num2 = _00237d._0023jae();
		Data.BoxScores = new BoxScore[num2];
		for (int j = 0; j < num2; j++)
		{
			ref BoxScore reference = ref Data.BoxScores[j];
			reference = DeserializeBoxScore(_00237d);
		}
		Data.MapIndex = _00237d._0023jae();
		Data.LevelReached = _00237d._0023jae();
		Data.Consumable1Uses = _00237d._0023nae();
		Data.Consumable2Uses = _00237d._0023nae();
		Data.Consumable3Uses = _00237d._0023nae();
		Data.TeamSupplies = _00237d._0023Hae();
		Data.Team = _00237d._0023jae();
		Data.LootTier = _00237d._0023jae();
		Data.InfectionLevel = _00237d._0023jae();
		int num3 = _00237d._0023jae();
		if (num3 != 0)
		{
			Data.Weapon1 = _00237d._0023kae(num3);
		}
		num3 = _00237d._0023jae();
		if (num3 != 0)
		{
			Data.Weapon2 = _00237d._0023kae(num3);
		}
		num3 = _00237d._0023jae();
		if (num3 > 0)
		{
			Data.NewUnlockedNodes = new int[num3];
			for (int k = 0; k < num3; k++)
			{
				Data.NewUnlockedNodes[k] = _00237d._0023Hae();
			}
		}
		Data.CrossroadProgress = _00237d._0023nae();
		num3 = _00237d._0023jae();
		if (num3 != 0)
		{
			Data.CrossroadCompleteScores = _00237d._0023kae(num3);
		}
		num3 = _00237d._0023jae();
		if (num3 != 0)
		{
			Data.CrossroadMissionTypes = _00237d._0023kae(num3);
		}
		if (_00237d._0023Fae())
		{
			RewardCollection rewardCollection2 = default(RewardCollection);
			rewardCollection2.DeserializeVersion8(_00237d);
			Data.FirstWinRewards = rewardCollection2.Data;
		}
		else
		{
			Data.FirstWinRewards.Items = ItemCollection.NewItemCollection();
		}
		Data.Healing = _00237d._0023Hae();
		Data.Damage = _00237d._0023Hae();
		Data.Assists = _00237d._0023Hae();
	}
}
