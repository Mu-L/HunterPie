﻿using HunterPie.Core.Game.Enums;
using HunterPie.Core.Game.Events;
using System;
using System.Collections.Generic;

namespace HunterPie.Core.Game.Entity.Enemy;

public interface IMonster
{
    public string Name { get; }
    public int Id { get; }
    public float Health { get; }
    public float MaxHealth { get; }
    public float Stamina { get; }
    public float MaxStamina { get; }
    public float CaptureThreshold { get; }
    public bool IsEnraged { get; }
    public Target Target { get; }
    public Target ManualTarget { get; }
    public IReadOnlyCollection<IMonsterPart> Parts { get; }
    public IReadOnlyCollection<IMonsterAilment> Ailments { get; }
    public IMonsterAilment Enrage { get; }
    public Crown Crown { get; }
    public Element[] Weaknesses { get; }
    public string[] Types { get; }
    public VariantType Variant { get; }

    public event EventHandler<EventArgs> OnSpawn;
    public event EventHandler<EventArgs> OnLoad;
    public event EventHandler<EventArgs> OnDespawn;
    public event EventHandler<EventArgs> OnDeath;
    public event EventHandler<EventArgs> OnCapture;
    public event EventHandler<MonsterTargetEventArgs> OnTargetChange;
    public event EventHandler<EventArgs> OnCrownChange;
    public event EventHandler<EventArgs> OnHealthChange;
    public event EventHandler<EventArgs> OnStaminaChange;
    public event EventHandler<EventArgs> OnActionChange;
    public event EventHandler<EventArgs> OnEnrageStateChange;
    public event EventHandler<IMonsterPart> OnNewPartFound;
    public event EventHandler<IMonsterAilment> OnNewAilmentFound;
    public event EventHandler<Element[]> OnWeaknessesChange;
    public event EventHandler<IMonster> OnCaptureThresholdChange;
}