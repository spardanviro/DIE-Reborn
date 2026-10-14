using ConductorCrafting.GUI.Minimap;
using ConductorCrafting;
using ConductorGameLogic.Abilities;
using ConductorGameLogic.Entities.GameModeObjects;
using ConductorGameLogic.Entities;
using ConductorGameLogic.GameModes;
using ConductorGameLogic.Music;
using ConductorGameLogic.View;
using ConductorGameLogic;
using FarseerPhysics.Common;
using StunGameNetwork;
using System.Collections.Concurrent;
using System.Linq;
using Serilog;

namespace DIE.Server.Game;

/// <summary>
/// 无头 <see cref="IViewManager"/> —— 服务端不做任何表现。
///
/// ## 为什么需要它
///
/// <c>GameManager</c> 的构造函数第一个参数就是 IViewManager。这个接口是**表现层的
/// 抽象** (模型、特效、HUD、音乐), 服务端一件都不做, 但类型上绕不过去。
///
/// ## 这个文件是生成的, 不要手改
///
/// 由 <c>tools/gen_headless_viewmanager.py</c> 生成, 共 83 个成员。
/// 重新反编译后接口若有增减, 手抄的版本会静默漂移, 生成的版本会直接编译失败。
///
/// ## 每个调用都记下来
///
/// 全部实现成空转 + <see cref="Note"/> 计数。跑一局之后看 <see cref="Dump"/> 的输出,
/// 就知道模拟层到底回调了表现层的哪些成员 —— 那些才是可能需要真实语义的
/// (比如返回 null 会让上层 NRE)。**返回 default 是"暂未实现", 不是"确定无害"**,
/// 这个区别只能靠实跑消除。
/// </summary>
public sealed class HeadlessViewManager : IViewManager
{
    private readonly ConcurrentDictionary<string, int> _calls = new();

    private void Note(string member) => _calls.AddOrUpdate(member, 1, (_, n) => n + 1);

    /// <summary>把被调用过的成员按次数倒序打出来, 用来判断哪些空转需要补真实语义。</summary>
    public void Dump()
    {
        foreach (var pair in _calls.OrderByDescending(p => p.Value))
        {
            Log.Debug("  IViewManager.{Member} 被调用 {Count} 次", pair.Key, pair.Value);
        }
    }

    public void Clear()
    {
        Note("Clear");
    }

    public ICharacterModel GetCharacterModel(Character character, CharacterModelType modelType)
    {
        Note("GetCharacterModel");
        return default;
    }

    public IEffect GetEffect(EffectType effect)
    {
        Note("GetEffect");
        return default;
    }

    public IEffect GetWeaponEffect(Character Owner, int currentCombo, WeaponEffectType type)
    {
        Note("GetWeaponEffect");
        return default;
    }

    public EffectType GetWeaponEffectType(Character Owner, int currentCombo, WeaponEffectType type)
    {
        Note("GetWeaponEffectType");
        return default;
    }

    public IEffect GetWeaponEffect(CharacterEnum character, int weaponSchematicID, int currentCombo, WeaponEffectType type)
    {
        Note("GetWeaponEffect");
        return default;
    }

    public float GetWeaponKillForceMultiplier(Character owner)
    {
        Note("GetWeaponKillForceMultiplier");
        return default;
    }

    public bool UsingNewScoreboard()
    {
        Note("UsingNewScoreboard");
        return default;
    }

    public bool UsingNewOptionsMenu()
    {
        Note("UsingNewOptionsMenu");
        return default;
    }

    public void ToggleScoreboard()
    {
        Note("ToggleScoreboard");
    }

    public void DisableHUD()
    {
        Note("DisableHUD");
    }

    public bool ToggleOptionsMenu()
    {
        Note("ToggleOptionsMenu");
        return default;
    }

    public bool IsScoreboardVisible()
    {
        Note("IsScoreboardVisible");
        return default;
    }

    public bool IsOptionsMenuVisible()
    {
        Note("IsOptionsMenuVisible");
        return default;
    }

    public bool TutorialIntroActive()
    {
        Note("TutorialIntroActive");
        return default;
    }

    public IStorageRoomHUD GetStorageRoomHUD(StorageRoom storageRoomObject)
    {
        Note("GetStorageRoomHUD");
        return default;
    }

    public IObjectiveLog GetObjectiveLog()
    {
        Note("GetObjectiveLog");
        return default;
    }

    public IObjectiveManager GetObjectiveManager()
    {
        Note("GetObjectiveManager");
        return default;
    }

    public IResistantEventMessages GetResistantEventMessages()
    {
        Note("GetResistantEventMessages");
        return default;
    }

    public ITransmissionManager GetTransmissionManager()
    {
        Note("GetTransmissionManager");
        return default;
    }

    public ITutorialPopupManager GetTutorialPopupManager()
    {
        Note("GetTutorialPopupManager");
        return default;
    }

    public ITutorialHud GetTutorialHud()
    {
        Note("GetTutorialHud");
        return default;
    }

    public IScreenFade GetScreenFade()
    {
        Note("GetScreenFade");
        return default;
    }

    public IGameModeGUI GetGameModeGUI(GameMode gameMode)
    {
        Note("GetGameModeGUI");
        return default;
    }

    public IAbilityAim GetAbilityAim(Ability ability)
    {
        Note("GetAbilityAim");
        return default;
    }

    public IMusicSystem GetMusicSystem()
    {
        Note("GetMusicSystem");
        return default;
    }

    public float GetHeight(Vector2 position)
    {
        Note("GetHeight");
        return default;
    }

    public void ChatReceived(Player player, ServerLocalizedMessages message, string name, string text, ChatType chatType, TeamId iptTeamID = TeamId.Neutral)
    {
        Note("ChatReceived");
    }

    public void GoldSCTReceived(Entity entity, Entity targetEntity, float value)
    {
        Note("GoldSCTReceived");
    }

    public void SCTReceived(GameObjectVerifier verifier, Entity entity, Entity ownerChanger, GameObjectBase gameObjectChanger, SCTData data)
    {
        Note("SCTReceived");
    }

    public void TakeHitEffect(Entity entity, Entity ownerChanger)
    {
        Note("TakeHitEffect");
    }

    public void TargetEffect(Entity entity, Entity ownerChanger)
    {
        Note("TargetEffect");
    }

    public void AddReportData(ulong userID, ReportType reportType)
    {
        Note("AddReportData");
    }

    public void TeamFinished(TeamId team, Placement place, bool finishNow)
    {
        Note("TeamFinished");
    }

    public void MiniMapPlingReceived(Player sender, PingType pingType, Vector2 position)
    {
        Note("MiniMapPlingReceived");
    }

    public void MiniMapPlingReceived(Player sender, GameObjectBase entity, bool danger = false)
    {
        Note("MiniMapPlingReceived");
    }

    public bool StartTutorialCameraSequence()
    {
        Note("StartTutorialCameraSequence");
        return default;
    }

    public void StopRaidMissionIntro()
    {
        Note("StopRaidMissionIntro");
    }

    public void StartRaidMissionIntro()
    {
        Note("StartRaidMissionIntro");
    }

    public void UpdateRaidMissionIntro(float percentageDone, Vector2 startPos, Vector2 endPos)
    {
        Note("UpdateRaidMissionIntro");
    }

    public string GetKeybindText(string actionName)
    {
        Note("GetKeybindText");
        return default;
    }

    public void ShowGuideText(TutorialGuideText type, string localizedKey, Vector3 worldPosition, Vector3 rotation, float scale, bool useTween)
    {
        Note("ShowGuideText");
    }

    public void HideGuideText(TutorialGuideText type)
    {
        Note("HideGuideText");
    }

    public void ToggleHUD(bool toggle, bool disableCursor = false, bool disableSCT = true, bool disableChat = true)
    {
        Note("ToggleHUD");
    }

    public void HideScoreboard()
    {
        Note("HideScoreboard");
    }

    public void SkipTutorialIntro()
    {
        Note("SkipTutorialIntro");
    }

    public void ShakeCamera(float shackeForce)
    {
        Note("ShakeCamera");
    }

    public void SetCameraLookAtPosition(Vector2 position)
    {
        Note("SetCameraLookAtPosition");
    }

    public void DisbaleLookAtPosition()
    {
        Note("DisbaleLookAtPosition");
    }

    public void ShowEscapeAnnouncement(float time, IEffect effect, TeamId glowColor)
    {
        Note("ShowEscapeAnnouncement");
    }

    public void ShowAnnouncement(string localizedText, string subtext, IEffect effect, TeamId glowColor, bool overwrite = false, bool isEscape = false)
    {
        Note("ShowAnnouncement");
    }

    public void ShowAnnouncement(LocalizedParameterMap localizedText, LocalizedParameterMap subtext, IEffect effect, TeamId glowColor, bool overwrite = false, bool isEscape = false)
    {
        Note("ShowAnnouncement");
    }

    public void ExpoEndTextReceived(string text1, string text2)
    {
        Note("ExpoEndTextReceived");
    }

    public void CountdownStarted()
    {
        Note("CountdownStarted");
    }

    public void SendGameplayInputActive(InputMask mask)
    {
        Note("SendGameplayInputActive");
    }

    public void ResetUIScale()
    {
        Note("ResetUIScale");
    }

    public void SetUIScale(float value)
    {
        Note("SetUIScale");
    }

    public void ClientConnected(Client client, TeamId team)
    {
        Note("ClientConnected");
    }

    public void ClientReconnected(Client client)
    {
        Note("ClientReconnected");
    }

    public void ClientDisconnected(Client client)
    {
        Note("ClientDisconnected");
    }

    public void ClientReady(Client client)
    {
        Note("ClientReady");
    }

    public void ShutDownApplication()
    {
        Note("ShutDownApplication");
    }

    public void BossSpawned(int bossEventID)
    {
        Note("BossSpawned");
    }

    public void OnConnected(int mapIndex)
    {
        Note("OnConnected");
    }

    public void OnCharacterKilled(Entity killed, Entity killer, AbilityIdentifier killerAbilityIdentifier)
    {
        Note("OnCharacterKilled");
    }

    public void CombatLogReceived(string combatLogLine)
    {
        Note("CombatLogReceived");
    }

    public void SetMusicLevel(float musicLevel)
    {
        Note("SetMusicLevel");
    }

    public void SetMusicNightFallLevel(float musicLevel)
    {
        Note("SetMusicNightFallLevel");
    }

    public void TransitionDayCycle(bool day)
    {
        Note("TransitionDayCycle");
    }

    public void PlayEnemySupplyCarrySound(TeamId enemyTeam)
    {
        Note("PlayEnemySupplyCarrySound");
    }

    public void ConcedeReceived(ConcedeMessageType concedeMessageType, IClient fromClient)
    {
        Note("ConcedeReceived");
    }

    public void ScavengerMedalInformation(TeamId barricadeRace, TeamId fillTruck, TeamId victoryTeam)
    {
        Note("ScavengerMedalInformation");
    }

    public void RaidMissionMedalInformation(bool supply_low, bool supply_mid, bool supply_high)
    {
        Note("RaidMissionMedalInformation");
    }

    public float RaidEventTransmission(RaidEventType eventType, TransmissionType transmissionType, int visualID, int randomID)
    {
        Note("RaidEventTransmission");
        return default;
    }

    public float PlayTransmissionPackage(TransmissionEventID eventID, int randomID, bool useRadioCrackle)
    {
        Note("PlayTransmissionPackage");
        return default;
    }

    public float PlayTransmissionPackage(TransmissionPackageID package, int randomID, bool useRadioCrackle, Vector2 position)
    {
        Note("PlayTransmissionPackage");
        return default;
    }

    public float ShowCompleteScore(CompleteScore score, byte missionNr)
    {
        Note("ShowCompleteScore");
        return default;
    }

    public float ShowNewMission()
    {
        Note("ShowNewMission");
        return default;
    }

    public float ShowMainObjective()
    {
        Note("ShowMainObjective");
        return default;
    }

    public void OnCastAbility(Ability ability)
    {
        Note("OnCastAbility");
    }

    public VoiceClipID PlayCharacterVoiceClip(EntityID entity, CharacterEnum character, CharacterVoiceClip clip, Optional<EntityID> affected, float valuePercentage)
    {
        Note("PlayCharacterVoiceClip");
        return default;
    }

    public void StopCharacterVoiceClip(VoiceClipID clip)
    {
        Note("StopCharacterVoiceClip");
    }

    public void PlaySound(object FMODPrefab)
    {
        Note("PlaySound");
    }
}
