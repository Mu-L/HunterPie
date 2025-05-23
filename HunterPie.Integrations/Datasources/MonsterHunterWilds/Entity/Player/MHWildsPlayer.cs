﻿using HunterPie.Core.Address.Map;
using HunterPie.Core.Client.Configuration.Enums;
using HunterPie.Core.Domain;
using HunterPie.Core.Domain.Process.Entity;
using HunterPie.Core.Extensions;
using HunterPie.Core.Game.Data.Definitions;
using HunterPie.Core.Game.Data.Repository;
using HunterPie.Core.Game.Entity.Party;
using HunterPie.Core.Game.Entity.Player;
using HunterPie.Core.Game.Entity.Player.Classes;
using HunterPie.Core.Game.Entity.Player.Vitals;
using HunterPie.Core.Game.Enums;
using HunterPie.Core.Game.Events;
using HunterPie.Core.Game.Services;
using HunterPie.Core.Scan.Service;
using HunterPie.Core.Utils;
using HunterPie.Integrations.Datasources.Common.Entity.Player;
using HunterPie.Integrations.Datasources.MonsterHunterWilds.Definitions.Abnormality;
using HunterPie.Integrations.Datasources.MonsterHunterWilds.Definitions.Party;
using HunterPie.Integrations.Datasources.MonsterHunterWilds.Definitions.Party.Data;
using HunterPie.Integrations.Datasources.MonsterHunterWilds.Definitions.Player;
using HunterPie.Integrations.Datasources.MonsterHunterWilds.Definitions.Types;
using HunterPie.Integrations.Datasources.MonsterHunterWilds.Entity.Abnormalities;
using HunterPie.Integrations.Datasources.MonsterHunterWilds.Entity.Abnormalities.Data;
using HunterPie.Integrations.Datasources.MonsterHunterWilds.Entity.Enemy;
using HunterPie.Integrations.Datasources.MonsterHunterWilds.Entity.Party;
using HunterPie.Integrations.Datasources.MonsterHunterWilds.Entity.Party.Data;
using HunterPie.Integrations.Datasources.MonsterHunterWilds.Entity.Player.Weapons;
using HunterPie.Integrations.Datasources.MonsterHunterWilds.Utils;
using WeaponType = HunterPie.Core.Game.Enums.Weapon;

namespace HunterPie.Integrations.Datasources.MonsterHunterWilds.Entity.Player;

public sealed class MHWildsPlayer : CommonPlayer
{
    private const int MAX_DAMAGE_HISTORY_SIZE = 100;
    private static readonly Lazy<AbnormalityDefinition[]> ConsumableDefinitions = new(static () =>
        AbnormalityRepository.FindAllAbnormalitiesBy(
            game: GameType.Wilds,
            category: AbnormalityGroup.CONSUMABLES
        )
    );
    private static readonly Lazy<AbnormalityDefinition[]> SongsDefinitions = new(static () =>
        AbnormalityRepository.FindAllAbnormalitiesBy(
            game: GameType.Wilds,
            category: AbnormalityGroup.SONGS
        )
    );
    private static readonly Lazy<AbnormalityDefinition[]> PalicoSongsDefinitions = new(static () =>
        AbnormalityRepository.FindAllAbnormalitiesBy(
            game: GameType.Wilds,
            category: AbnormalityGroup.ORCHESTRA
        )
    );
    private static readonly Lazy<AbnormalityDefinition[]> DebuffDefinitions = new(static () =>
        AbnormalityRepository.FindAllAbnormalitiesBy(
            game: GameType.Wilds,
            category: AbnormalityGroup.DEBUFFS
        )
    );
    private static readonly Lazy<AbnormalityDefinition[]> SkillDefinitions = new(() =>
        AbnormalityRepository.FindAllAbnormalitiesBy(
            game: GameType.Wilds,
            category: AbnormalityGroup.SKILLS
        )
    );
    private static readonly Lazy<int> DebuffIndexMax = new(static () => DebuffDefinitions.Value.Max(it => it.Index));

    private readonly MHWildsMonsterTargetKeyManager _monsterTargetKeyManager;

    private nint _address;

    private string _name = string.Empty;
    public override string Name
    {
        get => _name;
        protected set
        {
            if (value == _name)
                return;

            _name = value;

            this.Dispatch(
                value != ""
                    ? _onLogin
                    : _onLogout
            );
        }
    }

    private int _highRank;
    public override int HighRank
    {
        get => _highRank;
        protected set
        {
            if (value == _highRank)
                return;

            _highRank = value;
            this.Dispatch(
                toDispatch: _onLevelChange,
                data: new LevelChangeEventArgs(this)
            );
        }
    }

    private int _masterRank;
    public override int MasterRank
    {
        get => _masterRank;
        protected set
        {
            if (value == _masterRank)
                return;

            _masterRank = value;
            this.Dispatch(
                toDispatch: _onLevelChange,
                data: new LevelChangeEventArgs(this)
            );
        }
    }

    private int _stageId = -1;
    public override int StageId
    {
        get => _stageId;
        protected set
        {
            if (value == _stageId)
                return;

            _stageId = value;
            this.Dispatch(
                toDispatch: _onStageUpdate,
                data: EventArgs.Empty
            );
        }
    }

    private bool _inHuntingZone;
    public override bool InHuntingZone => _inHuntingZone;

    private readonly MHWildsParty _party = new();
    public override IParty Party => _party;

    private readonly Dictionary<string, IAbnormality> _abnormalities = new();
    public override IReadOnlyCollection<IAbnormality> Abnormalities => _abnormalities.Values;

    public override IHealthComponent? Health { get; }
    public override IStaminaComponent? Stamina { get; }

    private IWeapon _weapon;
    public override IWeapon Weapon
    {
        get => _weapon;
        protected set
        {
            if (_weapon == value)
                return;

            IWeapon oldWeapon = _weapon;
            _weapon = value;
            this.Dispatch(
                toDispatch: _onWeaponChange,
                data: new WeaponChangeEventArgs(oldWeapon, value
                )
            );
        }
    }

    public MHWildsPlayer(
        IGameProcess process,
        IScanService scanService,
        MHWildsMonsterTargetKeyManager monsterTargetKeyManager) : base(process, scanService)
    {
        _monsterTargetKeyManager = monsterTargetKeyManager;
        Weapon = new MHWildsWeapon(WeaponType.Greatsword);
    }

    [ScannableMethod]
    internal async Task GetBasicDataAsync()
    {
        _address = await Memory.DerefAsync<nint>(
            address: AddressMap.GetAbsolute("Game::PlayerManager"),
            offsets: AddressMap.GetOffsets("Player::Local")
        );

        MHWildsPlayerContext context = await Memory.DerefPtrAsync<MHWildsPlayerContext>(
            address: _address,
            offsets: AddressMap.GetOffsets("Player::Context")
        );

        int hunterRank = await Memory.DerefAsync<int>(
            address: AddressMap.GetAbsolute("Game::SaveManager"),
            offsets: AddressMap.GetOffsets("Save::Player::HunterRank")
        );

        Name = await Memory.ReadStringSafeAsync(context.NamePointer, size: 64);
        HighRank = hunterRank;
    }

    [ScannableMethod]
    internal async Task GetStageAsync()
    {
        MHWildsStageContext context = await Memory.DerefPtrAsync<MHWildsStageContext>(
            address: _address,
            offsets: AddressMap.GetOffsets("Player::Stage")
        );

        bool wasInHuntingZone = _inHuntingZone;
        int pauseState = await Memory.DerefAsync<int>(
            address: AddressMap.GetAbsolute("Game::PauseManager"),
            offsets: AddressMap.GetOffsets("Game::PauseState")
        );
        bool isLoading = (pauseState & 6) > 0;
        bool isInSafeAreaForced = context.StageId is 14 or 12;

        _inHuntingZone = context is { IsSafeZone: false, StageId: >= 0 } &&
            !isInSafeAreaForced &&
            !isLoading &&
            !string.IsNullOrEmpty(Name);

        if (wasInHuntingZone && !_inHuntingZone)
            this.Dispatch(_onVillageEnter);
        else if (!wasInHuntingZone && _inHuntingZone)
            this.Dispatch(_onVillageLeave);

        if (isLoading)
            StageId = -1;

        if (context.StageId != StageId && isLoading)
            return;

        StageId = context.StageId;
    }

    [ScannableMethod]
    internal async Task GetWeaponAsync()
    {
        MHWildsPlayerGearContext context = await Memory.DerefPtrAsync<MHWildsPlayerGearContext>(
            address: _address,
            offsets: AddressMap.GetOffsets("Player::Gear")
        );

        if (Weapon.Id != context.WeaponId)
            Weapon = new MHWildsWeapon(context.WeaponId);
    }

    [ScannableMethod]
    internal async Task GetPartyAsync()
    {
        if (StageId < 0)
            return;

        nint partyLimitedArrayPointer = await Memory.DerefAsync<nint>(
            address: AddressMap.GetAbsolute("Game::PlayerManager"),
            offsets: AddressMap.GetOffsets("Player::Party")
        );
        nint partyMemberIndexesArrayPointer = await Memory.DerefAsync<nint>(
            address: AddressMap.GetAbsolute("Game::PlayerManager"),
            offsets: AddressMap.GetOffsets("Player::Quest::PlayerIndexes")
        );
        MHWildsLimitedArray partyArrayPointer = await Memory.ReadAsync<MHWildsLimitedArray>(partyLimitedArrayPointer);
        MHWildsPartyArray partyArray = await Memory.ReadAsync<MHWildsPartyArray>(partyArrayPointer.Elements);
        MHWildsPartyMemberIndex[] partyIndexesArray = await Memory.ReadArraySafeAsync<MHWildsPartyMemberIndex>(
            address: partyMemberIndexesArrayPointer,
            count: 4
        );

        if (partyArray.Capacity != 4
            || partyArray.Members is not { Length: 4 }
            || partyIndexesArray is not { Length: 4 })
        {
            _party.Clear();
            return;
        }

        int membersCount = Math.Max(1, partyArrayPointer.Length);

        if (membersCount > 4)
            return;

        nint networkPartyMemberArray = await Memory.ReadAsync(
            address: AddressMap.GetAbsolute("Game::NetworkManager"),
            offsets: AddressMap.GetOffsets("Network::Party")
        );

        var membersData = new UpdatePartyMember[membersCount];
        for (int i = 0; i < membersCount; i++)
        {
            int playerIndex = partyArray.Members[i];
            int realPartyIndex = partyIndexesArray[i].Index;
            nint playerPointer = await Memory.ReadAsync(
                address: AddressMap.GetAbsolute("Game::PlayerManager"),
                offsets: AddressMap.GetOffsets("Player::List")
            );
            playerPointer += playerIndex * sizeof(long);
            playerPointer = await Memory.ReadAsync<nint>(playerPointer);

            MHWildsPlayerBase playerBase = await Memory.ReadAsync<MHWildsPlayerBase>(
                address: playerPointer
            );

            if (!playerBase.IsReady() && !_party.Contains(playerBase.BasePointer))
                continue;

            string name = await GetPlayerNameAsync(playerBase.BasePointer);

            if (string.IsNullOrEmpty(name))
                continue;

            Task<float> damageDefer = GetDamageByPlayerAsync(playerBase.BasePointer, realPartyIndex);

            bool isMyself = playerIndex == 0;
            var data = new UpdatePartyMember
            {
                IsValid = true,
                Id = playerBase.BasePointer,
                Index = realPartyIndex,
                IsMyself = isMyself,
                Name = name,
                Weapon = isMyself ? Weapon.Id : await GetPlayerWeaponAsync(playerBase.BasePointer),
                Damage = await damageDefer,
                HunterRank = isMyself
                    ? HighRank
                    : await Memory.DerefAsync<int>(
                        address: networkPartyMemberArray + (realPartyIndex * sizeof(long)),
                        offsets: AddressMap.GetOffsets("Network::Party::Member::HunterRank")
                    )
            };

            membersData[i] = data;
        }

        _party.Update(new UpdateParty
        {
            Players = membersData.Where(it => it.IsValid)
                .Select(it => it.Id)
                .ToArray()
        });

        membersData.ForEach(_party.Update);
    }

    private async Task<float> GetDamageByPlayerAsync(nint address, int index)
    {
        bool isLocalPlayer = address == _address;

        float syncedDamage = isLocalPlayer switch
        {
            true => await GetQuestSyncedLocalPlayerDamage(),
            _ => await GetQuestSyncedRemotePlayerDamage(index)
        };

        if (syncedDamage > 0)
            return syncedDamage;

        return await GetHistoricalPlayerDamage(address);
        ;
    }

    private async Task<float> GetHistoricalPlayerDamage(nint playerAddress)
    {
        nint damageHistoryPointer = await Memory.ReadPtrAsync(
            address: playerAddress,
            offsets: AddressMap.GetOffsets("Player::DamageHistory")
        );
        MHWildsDamageHistory damageHistory = await Memory.ReadAsync<MHWildsDamageHistory>(damageHistoryPointer);

        if (damageHistory.Size <= 0)
            return 0;

        int offset = 0x20;
        int damageHistorySafeSize = damageHistory.Size;
        // The history array grows infinitely and stores the damage done to each monster individually
        // we don't need to actually read every monster damage as some of the monsters might not even be in the map anymore
        // This also handles invalid memory addresses that sometimes makes HunterPie read insanely high lengths
        if (damageHistorySafeSize > MAX_DAMAGE_HISTORY_SIZE)
        {
            offset += sizeof(long) * (damageHistorySafeSize - MAX_DAMAGE_HISTORY_SIZE);
            damageHistorySafeSize = MAX_DAMAGE_HISTORY_SIZE;
        }

        nint[] damagePointers = await Memory.ReadAsync<nint>(
            address: damageHistory.Elements + offset,
            count: damageHistorySafeSize
        );
        IAsyncEnumerable<MHWildsTargetDamage> damageEnumerable = damagePointers.AsParallel()
            .Select(async it => await Memory.ReadAsync<MHWildsTargetDamage>(it))
            .ToAsyncEnumerable();

        float totalDamage = 0;

        await foreach (MHWildsTargetDamage targetDamage in damageEnumerable)
        {
            bool hasQuestTargets = _monsterTargetKeyManager.HasQuestTargets();
            bool isInExpedition = !hasQuestTargets && _monsterTargetKeyManager.IsMonster(targetDamage.TargetKey);
            bool isInQuest = hasQuestTargets && _monsterTargetKeyManager.IsQuestTarget(targetDamage.TargetKey);

            if (!isInQuest && !isInExpedition)
                continue;

            totalDamage += targetDamage.Damage;
        }

        return totalDamage;
    }

    private async Task<float> GetQuestSyncedLocalPlayerDamage()
    {
        nint damagePointer = await Memory.ReadAsync(
            address: AddressMap.GetAbsolute("Game::QuestManager"),
            offsets: AddressMap.GetOffsets("Quest::LocalPlayer::Damage")
        );

        return await Memory.ReadAsync<float>(damagePointer);
    }

    private async Task<float> GetQuestSyncedRemotePlayerDamage(int index)
    {
        nint damagePointer = await Memory.ReadAsync(
            address: AddressMap.GetAbsolute("Game::QuestManager"),
            offsets: AddressMap.GetOffsets("Quest::RemotePlayer::Damage")
        );

        return await Memory.ReadAsync<float>(damagePointer + (index * 0x78));
    }

    private async Task<string> GetPlayerNameAsync(nint address)
    {
        MHWildsPlayerContext context = await Memory.DerefPtrAsync<MHWildsPlayerContext>(
            address: address,
            offsets: AddressMap.GetOffsets("Player::Context")
        );

        return await Memory.ReadStringSafeAsync(context.NamePointer, size: 64);
    }

    private async Task<WeaponType> GetPlayerWeaponAsync(nint address)
    {
        MHWildsPlayerGearContext context = await Memory.DerefPtrAsync<MHWildsPlayerGearContext>(
            address: address,
            offsets: AddressMap.GetOffsets("Player::Gear")
        );

        return context.WeaponId;
    }

    [ScannableMethod]
    internal Task GetAbnormalitiesCleanUpAsync()
    {
        if (InHuntingZone)
            return Task.CompletedTask;

        ClearAbnormalities(_abnormalities);

        return Task.CompletedTask;
    }

    [ScannableMethod]
    internal async Task GetConsumableAbnormalitiesAsync()
    {
        if (!InHuntingZone)
            return;

        ConsumableAbnormalities consumableAbnormalities = await Memory.DerefPtrAsync<ConsumableAbnormalities>(
            address: _address,
            offsets: AddressMap.GetOffsets("Player::Abnormalities::Consumables")
        );

        if (consumableAbnormalities.Raw is not { Length: > 0 })
            return;

        foreach (AbnormalityDefinition definition in ConsumableDefinitions.Value)
        {
            UpdateAbnormalityData data;

            if (!definition.HasMaxTimer)
            {
                int timerOffset = definition.Offset;

                data = new UpdateAbnormalityData
                {
                    ShouldInferMaxTimer = true,
                    Timer = BitConverter.ToSingle(consumableAbnormalities.Raw, timerOffset)
                };
            }
            else
            {
                int maxTimerOffset = definition.Offset;
                int timerOffset = maxTimerOffset + sizeof(float);

                data = new UpdateAbnormalityData
                {
                    MaxTimer = BitConverter.ToSingle(consumableAbnormalities.Raw, maxTimerOffset),
                    Timer = BitConverter.ToSingle(consumableAbnormalities.Raw, timerOffset)
                };
            }

            HandleAbnormality(
                abnormalities: _abnormalities,
                schema: definition,
                timer: data.Timer,
                newData: data,
                activator: () => new MHWildsAbnormality(definition, AbnormalityType.Consumable)
            );
        }
    }

    [ScannableMethod]
    internal async Task GetSongAbnormalitiesAsync()
    {
        if (!InHuntingZone)
            return;

        SongAbnormalities songsAbnormalities = await Memory.DerefPtrAsync<SongAbnormalities>(
            address: _address,
            offsets: AddressMap.GetOffsets("Player::Abnormalities::Songs")
        );

        AbnormalityDefinition[] songDefinitions = SongsDefinitions.Value;

        float[] songTimers = await Memory.ReadArraySafeAsync<float>(
            address: songsAbnormalities.TimersPointer,
            count: songDefinitions.Length
        );
        float[] songMaxTimers = await Memory.ReadArraySafeAsync<float>(
            address: songsAbnormalities.MaxTimersPointer,
            count: songDefinitions.Length
        );

        for (int i = 0; i < songDefinitions.Length; i++)
        {
            if (i >= songTimers.Length || i >= songMaxTimers.Length)
                break;

            AbnormalityDefinition definition = songDefinitions[i];
            var data = new UpdateAbnormalityData
            {
                Timer = songTimers[i],
                MaxTimer = songMaxTimers[i]
            };

            HandleAbnormality(
                abnormalities: _abnormalities,
                schema: songDefinitions[i],
                timer: data.Timer,
                newData: data,
                activator: () => new MHWildsAbnormality(definition, AbnormalityType.Song)
            );
        }
    }

    [ScannableMethod]
    internal async Task GetPalicoSongAbnormalitiesAsync()
    {
        if (!InHuntingZone)
            return;

        nint palicoSongsPointer = await Memory.ReadPtrAsync(
            address: _address,
            offsets: AddressMap.GetOffsets("Player::Abnormalities::PalicoSongs")
        );

        AbnormalityDefinition[] definitions = PalicoSongsDefinitions.Value;
        float[] timers = await Memory.ReadArraySafeAsync<float>(
            address: palicoSongsPointer,
            count: definitions.Length
        );

        for (int i = 0; i < timers.Length; i++)
        {
            AbnormalityDefinition definition = definitions[i];
            float timer = timers[i];

            var data = new UpdateAbnormalityData
            {
                ShouldInferMaxTimer = true,
                Timer = timer
            };

            HandleAbnormality(
                abnormalities: _abnormalities,
                schema: definition,
                timer: timer,
                newData: data,
                activator: () => new MHWildsAbnormality(definition, AbnormalityType.Orchestra)
            );
        }
    }

    [ScannableMethod]
    internal async Task GetSkillAbnormalitiesAsync()
    {
        if (!InHuntingZone)
            return;

        nint skillsBasePtr = await Memory.ReadPtrAsync(
            address: _address,
            offsets: AddressMap.GetOffsets("Player::Abnormalities::Skills")
        );

        AbnormalityDefinition[] definitions = SkillDefinitions.Value;

        foreach (AbnormalityDefinition definition in definitions)
        {
            nint abnormalityPointer = await Memory.ReadAsync<nint>(
                address: skillsBasePtr + definition.Offset
            );
            SkillAbnormality abnormality = await Memory.ReadAsync<SkillAbnormality>(abnormalityPointer);

            HandleAbnormality(
                abnormalities: _abnormalities,
                schema: definition,
                timer: abnormality.Timer,
                newData: new UpdateAbnormalityData
                {
                    Timer = abnormality.Timer,
                    MaxTimer = abnormality.MaxTimer
                },
                activator: () => new MHWildsAbnormality(definition, AbnormalityType.Skill)
            );
        }
    }

    [ScannableMethod]
    internal async Task GetDebuffsAbnormalitiesAsync()
    {
        if (!InHuntingZone)
            return;

        nint debuffsComponent = await Memory.ReadPtrAsync(
            address: _address,
            offsets: AddressMap.GetOffsets("Player::Abnormalities::Debuffs")
        );

        AbnormalityDefinition[] definitions = DebuffDefinitions.Value;

        nint[] debuffPointers = await Memory.ReadAsync<nint>(
            address: debuffsComponent + 0x10,
            count: DebuffIndexMax.Value + 1
        );

        foreach (AbnormalityDefinition definition in definitions)
        {
            if (definition.Index >= debuffPointers.Length)
                continue;

            nint debuffPointer = debuffPointers[definition.Index];

            int dependingValue = definition.DependsOn switch
            {
                0 => 0,
                _ => await Memory.ReadAsync<int>(
                    address: debuffPointer + definition.DependsOn
                )
            };

            UpdateAbnormalityData data;

            if (dependingValue != definition.WithValue)
                data = new UpdateAbnormalityData
                {
                    Timer = 0
                };
            else if (definition.IsBuildup)
            {
                DebuffAbnormality abnormality = await Memory.ReadAsync<DebuffAbnormality>(
                    address: debuffPointer
                );
                bool isValidValue = abnormality.BuildUp >= 0 && abnormality.BuildUp <= definition.MaxBuildup;

                data = new UpdateAbnormalityData
                {
                    Timer = isValidValue
                     ? abnormality.BuildUp
                     : 0,
                    MaxTimer = definition.MaxBuildup
                };
            }
            else if (!definition.HasMaxTimer)
            {
                byte isActive = await Memory.ReadAsync<byte>(
                    address: debuffPointer + 0x2F
                );

                data = new UpdateAbnormalityData
                {
                    ShouldInferMaxTimer = true,
                    Timer = isActive == 1
                    ? await Memory.ReadAsync<float>(
                        address: debuffPointer + definition.Offset
                    )
                    : 0
                };
            }
            else
            {
                float[] timers = await Memory.ReadAsync<float>(
                    address: debuffPointer + definition.Offset,
                    count: 2
                );
                byte isActive = await Memory.ReadAsync<byte>(
                    address: debuffPointer + 0x2F
                );

                data = new UpdateAbnormalityData
                {
                    Timer = isActive == 1 ? timers[0] : 0,
                    MaxTimer = timers[1]
                };
            }

            HandleAbnormality(
                abnormalities: _abnormalities,
                schema: definition,
                timer: data.Timer,
                newData: data,
                activator: () => new MHWildsAbnormality(definition, AbnormalityType.Debuff)
            );
        }
    }
}