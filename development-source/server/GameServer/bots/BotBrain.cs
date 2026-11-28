using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using DOL.Database;
using DOL.Events;
using DOL.GS;
using DOL.GS.Effects;
using DOL.GS.Keeps;
using DOL.GS.PacketHandler;
using DOL.GS.RealmAbilities;
using DOL.GS.SkillHandler;
using DOL.GS.Spells;
using DOL.GS.Styles;
using DOL.Logging;

namespace DOL.AI.Brain
{
    public partial class BotBrain : ABrain, IOldAggressiveBrain, IControlledBrain
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        public GameBot BotBody => Body as GameBot;
        private long _nextPoisonSupplyTick;

        #region IControlledBrain Implementation

        public GameLiving Owner => BotBody?.Owner;

        public eWalkState WalkState { get; protected set; } = eWalkState.Follow;

        public eAggressionState AggressionState { get; set; } = eAggressionState.Defensive;

        public bool IsMainPet { get; set; } = true;

        public virtual void Attack(GameObject target)
        {
            if (AggressionState == eAggressionState.Passive)
                AggressionState = eAggressionState.Defensive;

            if (target is GameLiving livingTarget && livingTarget != Body.TargetObject)
            {
                AddToAggroList(livingTarget, 1);
                FSM.SetCurrentState(eFSMStateType.AGGRO);
                // An attack/pull order changes the next decision target. It
                // must not cancel a legal cast already in flight; repeated
                // assist or pet-related orders otherwise make list casters
                // interrupt themselves forever.
                AttackMostWanted();
            }
        }

        public virtual void Disengage()
        {
            _orderedPullTarget = null;
            _returnToFormationAfterPull = false;
            ClearAggroList();
            FSM.SetCurrentState(eFSMStateType.FOLLOW);
        }

        public virtual void CheckAggressionStateOnPlayerOrder()
        {
            // Bots use their own AI logic, aggression state is managed by FSM
        }

        public virtual void Follow(GameObject target)
        {
            WalkState = eWalkState.Follow;
            if (target != null)
                Body.Follow(target, BotManager.FOLLOW_DISTANCE, BotManager.MAX_FOLLOW_DISTANCE);
        }

        public virtual void FollowOwner()
        {
            Follow(BotBody?.Owner);
        }

        public virtual void Stay()
        {
            WalkState = eWalkState.Stay;
            Body.StopMoving();
            FSM.SetCurrentState(eFSMStateType.IDLE);
        }

        public virtual void ComeHere()
        {
            if (BotBody?.Owner != null)
            {
                WalkState = eWalkState.ComeHere;
                Body.StopFollowing();
                Body.PathTo(BotBody.Owner, Body.MaxSpeed);
            }
        }

        public virtual void Goto(GameObject target)
        {
            if (target != null)
            {
                WalkState = eWalkState.GoTarget;
                Body.StopFollowing();
                Body.PathTo(target, Body.MaxSpeed);
            }
        }

        public virtual void UpdatePetWindow()
        {
            // Bots don't use pet windows - this is for player-controlled pets
        }

        public virtual GamePlayer GetPlayerOwner()
        {
            return BotBody?.Owner;
        }

        public virtual GameNPC GetNPCOwner()
        {
            return null; // Bots are always owned by players
        }

        public virtual GameLiving GetLivingOwner()
        {
            return BotBody?.Owner;
        }

        public virtual void SetAggressionState(eAggressionState state)
        {
            AggressionState = state;
        }

        #endregion

        #region IOldAggressiveBrain

        public int AggroLevel { get; set; } = 100;
        public int AggroRange { get; set; } = 3600;

        #endregion

        #region Aggro List

        private GameLiving _recentDirectAttacker;
        private long _recentDirectAttackTick;

        public const int MAX_AGGRO_DISTANCE = 3600;
        public const int MAX_AGGRO_LIST_DISTANCE = 6000;
        private const int EFFECTIVE_AGGRO_AMOUNT_CALCULATION_DISTANCE_THRESHOLD = 500;

        protected ConcurrentDictionary<GameLiving, AggroAmount> AggroList { get; private set; } = new();
        protected List<(GameLiving, long)> OrderedAggroList { get; private set; } = [];
        public GameLiving LastHighestThreatInAttackRange { get; private set; }

        public class AggroAmount(long @base = 0)
        {
            public long Base { get; set; } = @base;
            public long Effective { get; set; }
        }

        public virtual bool HasAggro => !AggroList.IsEmpty;

        private GameLiving _committedDungeonPull;
        private long _committedDungeonPullUntil;

        public void CommitDungeonPull(GameLiving target)
        {
            // True 1.65 Mode: a solo bot's outdoor camp pull gets the same window (2026-10-31: a pull still walking in
            // was dropped after six seconds and picked again, Champions and Bards looping at Silvermine feccan).
            if (BotBody?.IsAutonomousWorldBot != true || BotBody.IsPlayerLedGroup ||
                Body.CurrentZone?.IsDungeon != true && !(Classic165Mode.IsOn && BotBody.Group == null) || target?.IsAlive != true)
                return;
            // Native combat's six-second idle timeout is shorter than travel
            // plus a first cast. Give a deliberately selected corridor pull a
            // bounded approach window; ordinary aggro and watchdogs still own
            // real combat, deaths and genuinely unreachable targets.
            _committedDungeonPull = target;
            _committedDungeonPullUntil = GameLoop.GameLoopTime + 30_000;
        }

        public static bool RetainCommittedDungeonPull(bool alive, bool sameRegion, bool inAggro,
            long now, long until) => alive && sameRegion && inAggro && now < until;

        private bool HasCommittedDungeonPull => _committedDungeonPull != null &&
            RetainCommittedDungeonPull(_committedDungeonPull.IsAlive,
                _committedDungeonPull.CurrentRegion == Body.CurrentRegion,
                AggroList.ContainsKey(_committedDungeonPull), GameLoop.GameLoopTime, _committedDungeonPullUntil);

        public virtual void AddToAggroList(GameLiving living, long aggroAmount)
        {
            if (Body.IsConfused || !Body.IsAlive || living == null)
                return;

            ForceAddToAggroList(living, aggroAmount);
        }

        // A player cannot target an NPC flagged CANTTARGET, so a bot does not fight one either. Run 27 (2026-10-19): Galladoria's
        // Corrupt Primal of Fire leaves short-lived "trail of fire" patches (untargetable, 99% resists, gone in 6 s) that burn
        // whoever stands in them; 145 raiders by the Magical Earthmender were swinging at them and 2 at the Earthmender.
        public static bool IsUntargetable(GameLiving living) => living is GameNPC { Flags: var flags } && (flags & GameNPC.eFlags.CANTTARGET) != 0;

        /// <summary>
        /// epic-bosses L1: a player who has a boss targeted keeps it when the boss turns untargetable mid-fight (Lich Lord
        /// Sanguis at 5%); only a new target cannot be picked. Bots dropped him at once, so only blows already in flight
        /// could finish the last 5,000 and he went on killing raiders who would not answer. A scripted boss already on the
        /// bot's aggro list stays there and may be fought on; a new untargetable monster is still refused (the fire
        /// patches the rule was written for are never on a list: they are untargetable from the start).
        /// </summary>
        private bool KeepsUntargetableFoe(GameLiving living) =>
            RaidBossTraits.KeepsFoeThatTurnedUntargetable(AggroList.ContainsKey(living), living is GameEpicBoss);

        public void ForceAddToAggroList(GameLiving living, long aggroAmount)
        {
            if (IsUntargetable(living) && !KeepsUntargetableFoe(living)) return;
            if (!CompanionEngagementMode.Allows(Body, living)) return;
            if (_autonomousWorldController?.IgnoresTarget(living) == true) return;
            if (CompanionRaidSiege.Ignores(Body as GameBot, living)) return;
            if (living is DOL.GS.Keeps.GuardLord { ShieldedFromBots: true }) return;
            if (aggroAmount > 0)
            {
                foreach (ProtectECSGameEffect protect in living.effectListComponent.GetAbilityEffects().Where(e => e.EffectType is eEffect.Protect))
                {
                    if (protect.Target != living)
                        continue;

                    GameLiving protectSource = protect.Source;

                    if (protectSource.IsIncapacitated || protectSource.IsSitting)
                        continue;

                    if (!living.IsWithinRadius(protectSource, ProtectAbilityHandler.PROTECT_DISTANCE))
                        continue;

                    int abilityLevel = protectSource.GetAbilityLevel(Abilities.Protect);
                    long protectAmount = (long)(abilityLevel * 0.1 * aggroAmount);

                    if (protectAmount > 0)
                    {
                        aggroAmount -= protectAmount;

                        if (protectSource is GamePlayer playerProtectSource)
                        {
                            playerProtectSource.Out.SendMessage(
                                $"You absorb {protectAmount} aggro from {living.GetName(0, false)} for {Body.GetName(0, false)}!",
                                eChatType.CT_System, eChatLoc.CL_SystemWindow);
                        }

                        AggroList.AddOrUpdate(protectSource, Add, Update, protectAmount);
                    }
                }
            }

            AggroList.AddOrUpdate(living, Add, Update, aggroAmount);

            if (living is IGamePlayer player)
            {
                if (player.Group != null)
                {
                    foreach (GamePlayer playerInGroup in player.Group.GetPlayersInTheGroup())
                    {
                        if (playerInGroup != living)
                            AggroList.TryAdd((GameLiving)playerInGroup, new());
                    }
                }
            }

            static AggroAmount Add(GameLiving key, long arg)
            {
                return new(Math.Max(0, arg));
            }

            static AggroAmount Update(GameLiving key, AggroAmount oldValue, long arg)
            {
                oldValue.Base = Math.Max(0, oldValue.Base + arg);
                return oldValue;
            }
        }

        public virtual void RemoveFromAggroList(GameLiving living)
        {
            AggroList.TryRemove(living, out _);
        }

        public long GetBaseAggroAmount(GameLiving living)
        {
            return AggroList.TryGetValue(living, out AggroAmount aggroAmount) ? aggroAmount.Base : 0;
        }

        public virtual void ClearAggroList()
        {
            _committedDungeonPull = null;
            _committedDungeonPullUntil = 0;
            AggroList.Clear();

            lock (((ICollection)OrderedAggroList).SyncRoot)
            {
                OrderedAggroList.Clear();
            }

            LastHighestThreatInAttackRange = null;
        }

        protected virtual bool ShouldBeRemovedFromAggroList(GameLiving living)
        {
            return !living.IsAlive ||
                   IsUntargetable(living) && !KeepsUntargetableFoe(living) ||   // epic-bosses L1
                   living.ObjectState != GameObject.eObjectState.Active ||
                   living.CurrentRegion != Body.CurrentRegion ||
                   !Body.IsWithinRadius(living, MAX_AGGRO_LIST_DISTANCE) ||
                   (!GameServer.ServerRules.IsAllowedToAttack(Body, living, true) && !living.effectListComponent.ContainsEffectForEffectType(eEffect.Shade));
        }

        protected virtual bool ShouldBeIgnoredFromAggroList(GameLiving living)
        {
            return living.effectListComponent.ContainsEffectForEffectType(eEffect.Shade);
        }

        protected virtual GameLiving CleanUpAggroListAndGetHighestModifiedThreat()
        {
            OrderedAggroList.Clear();

            int attackRange = Body.attackComponent.AttackRange;
            GameLiving highestThreat = null;
            KeyValuePair<GameLiving, AggroAmount> currentTarget = default;
            long highestEffectiveAggro = -1;
            long highestEffectiveAggroInAttackRange = -1;

            // A mesmerized or rooted monster waits while anything else is in the fight (BotBrain.CrowdControlRespect).
            bool uncontrolledAvailable = false;
            // The monster the player is mesmerizing, stunning or rooting is left alone even before the spell lands.
            GameLiving leaderHeld = PlayerLedPullCoordinator.LeaderControlTarget(AssistedPlayer);
            foreach (GameLiving candidate in AggroList.Keys)
            {
                if (candidate is { IsAlive: true } && !ShouldBeIgnoredFromAggroList(candidate) && !IsBreakablyControlled(candidate) &&
                    !IsReservedForMez(candidate))   // a mezzer's add waits too (BotBrain.PveAddControl)
                {
                    uncontrolledAvailable = true;
                    break;
                }
            }

            foreach (var pair in AggroList)
            {
                GameLiving living = pair.Key;

                if (ShouldBeRemovedFromAggroList(living))
                {
                    AggroList.TryRemove(living, out _);
                    continue;
                }

                if (ShouldBeIgnoredFromAggroList(living))
                    continue;

                if (PassOverControlled(IsBreakablyControlled(living) || IsReservedForMez(living), uncontrolledAvailable) ||
                    living == leaderHeld || LeavesHeldForPlayer(living))
                    continue;

                if (Body.TargetObject == living)
                    currentTarget = pair;

                AggroAmount aggroAmount = pair.Value;
                double distance = Body.GetDistanceTo(living);
                aggroAmount.Effective = distance > EFFECTIVE_AGGRO_AMOUNT_CALCULATION_DISTANCE_THRESHOLD ?
                                        (long)Math.Ceiling(aggroAmount.Base * (EFFECTIVE_AGGRO_AMOUNT_CALCULATION_DISTANCE_THRESHOLD / distance)) :
                                        aggroAmount.Base;

                if (aggroAmount.Effective > highestEffectiveAggroInAttackRange)
                {
                    if (distance <= attackRange)
                    {
                        highestEffectiveAggroInAttackRange = aggroAmount.Effective;
                        LastHighestThreatInAttackRange = living;
                    }

                    if (aggroAmount.Effective > highestEffectiveAggro)
                    {
                        highestEffectiveAggro = aggroAmount.Effective;
                        highestThreat = living;
                    }
                }
            }

            if (highestThreat != null)
            {
                if (currentTarget.Key != null && currentTarget.Key != highestThreat && currentTarget.Value.Effective >= highestEffectiveAggro)
                    highestThreat = currentTarget.Key;
            }
            else
            {
                return AggroList.FirstOrDefault().Key?.ControlledBrain?.Body;
            }

            return highestThreat;
        }

        protected virtual GameLiving CalculateNextAttackTarget()
        {
            return CleanUpAggroListAndGetHighestModifiedThreat();
        }

        public virtual bool CanAggroTarget(GameLiving target)
        {
            if (!CompanionEngagementMode.Allows(Body, target)) return false;
            if (!GameServer.ServerRules.IsAllowedToAttack(Body, target, true))
                return false;

            GameLiving realTarget = target;

            if (realTarget is GameNPC npcTarget
                && npcTarget.Brain is IControlledBrain npcTargetBrain
                && npcTargetBrain.GetLivingOwner() is GameLiving livingOwner)
            {
                realTarget = livingOwner;
            }

            if (realTarget is IGamePlayer && realTarget.Realm != Body.Realm)
                return true;

            // An enemy keep's doors and walls are fought whatever their con (owner 2026-10-29: companions would not help
            // the player break a battleground keep's door).
            if (realTarget is DOL.GS.Keeps.GameKeepDoor or DOL.GS.Keeps.GameKeepComponent or GameRealmGate && realTarget.Realm != Body.Realm)
                return true;

            // Evaluate the monster from the character's perspective, not the
            // reverse. The native NPC check rejected purple pulls because the
            // character was grey to the monster. Camp selection owns difficulty;
            // this shared execution gate only excludes non-XP grey monsters.
            if (Body.IsObjectGreyCon(realTarget))
                return false;

            return AggroLevel > 0;
        }

        /// <summary>
        /// Retaliation is not a new XP pull. A grey creature that actually hit
        /// the bot (or is already fighting its pet) must still be defendable.
        /// Keep proactive camp/pull selection on CanAggroTarget, while this
        /// narrower gate checks only live attack legality.
        /// </summary>
        private bool CanDefendAgainst(GameLiving target) =>
            target?.IsAlive == true &&
            CompanionEngagementMode.Allows(Body, target) &&
            target.ObjectState == GameObject.eObjectState.Active &&
            target.CurrentRegion == Body.CurrentRegion &&
            GameServer.ServerRules.IsAllowedToAttack(Body, target, true);

        public virtual void OnAttackedByEnemy(AttackData ad)
        {
            CompanionPvpEngagement.RecordThreat(BotBody, Body, ad);
            if (!Body.IsAlive || Body.ObjectState != GameObject.eObjectState.Active || FSM.GetCurrentState() == FSM.GetState(eFSMStateType.PASSIVE))
                return;

            if (ad.GeneratesAggro)
            {
                SoloRangedPull165.OnThreat(BotBody, ad.Attacker);   // True 1.65 Mode solo ranged pull: the fight is on
                if (BotBody?.IsRecoveryResting == true)
                    BotBody.WakeRecoveryRest();
                ConvertDamageToAggroAmount(ad.Attacker, Math.Max(1, ad.Damage + ad.CriticalDamage));
                if (ad.Attacker?.IsAlive == true && ad.Attacker != Body)
                {
                    _recentDirectAttacker = ad.Attacker;
                    _recentDirectAttackTick = GameLoop.GameLoopTime;
                }
                // A remote efficient bot may otherwise spend its one immediate
                // wake-up merely changing IDLE/FOLLOW to AGGRO, then wait for a
                // second background planning interval before retaliating. Enter
                // combat here, exactly as StandardMobBrain does when aggro is
                // added, so the first woken pulse executes the combat state.
                if (ShouldEnterAggroStateAfterDirectAttack(
                        HasAggro,
                        FSM.GetCurrentState() == FSM.GetState(eFSMStateType.AGGRO)))
                    FSM.SetCurrentState(eFSMStateType.AGGRO);

                // Damage is an event, so wake combat immediately without
                // increasing the ordinary idle/travel AI cadence.
                NextThinkTick = GameLoop.GameLoopTime;
                WakeForAdd(ad.Attacker, Body);   // M12: an add on this bot is looked at now, not at the next 1 s check
            }
        }

        public static bool ShouldEnterAggroStateAfterDirectAttack(bool hasAggro, bool alreadyInAggroState) =>
            hasAggro && !alreadyInAggroState;

        protected virtual void ConvertDamageToAggroAmount(GameLiving attacker, int damage)
        {
            if (attacker is GameNPC NpcAttacker && NpcAttacker.Brain is ControlledMobBrain controlledBrain)
            {
                damage = controlledBrain.ModifyDamageWithTaunt(damage);

                int aggroForOwner = (int)(damage * 0.15);

                if (aggroForOwner == 0)
                {
                    AddToAggroList(controlledBrain.Owner, 1);
                    AddToAggroList(NpcAttacker, Math.Max(2, damage));
                }
                else
                {
                    AddToAggroList(controlledBrain.Owner, aggroForOwner);
                    AddToAggroList(NpcAttacker, damage - aggroForOwner);
                }
            }
            else
                AddToAggroList(attacker, damage);
        }

        public const int GROUP_DEFENSE_ASSIST_RADIUS = 2000;

        public static GameLiving GroupMemberForCombat(GameLiving living)
        {
            if (living is GamePlayer or GameBot) return living;
            // Only the actual main pet of a player/bot. Never walk through a
            // companion's Owner and accidentally attribute its pet to the human.
            if (living is GameNPC { Brain: ControlledMobBrain { Owner: GameLiving owner } } &&
                owner is GamePlayer or GameBot && owner.ControlledBrain?.Body == living)
                return owner;
            // A player's field pets too (Theurgist elementals, Animist turrets: GitHub #13): a monster fighting them
            // is fighting the player's group.
            if (PlayerLedPullCoordinator.PlayerPetOwner(living) is GamePlayer fieldOwner)
                return fieldOwner;
            return living;
        }

        /// <summary>
        /// Wakes only nearby members of the victim's exact group. Being close by
        /// is never sufficient on its own, so solo bots and unrelated groups do
        /// not chain-aggro from another character's fight.
        /// </summary>
        public static void NotifyNearbyGroupBots(GameLiving victim, AttackData ad)
        {
            GameLiving groupMember = GroupMemberForCombat(victim);
            // Attached sub-pets also enlist their human-led raid in PvP. The
            // existing autonomous and PvE membership rules stay unchanged.
            GameLiving pvpMember = CompanionPvpEngagement.Character(victim);
            GamePlayer pvpLeader = pvpMember as GamePlayer ?? CompanionPvpEngagement.Leader(pvpMember);
            bool companionSubPet = pvpMember != groupMember && CompanionPvpEngagement.Enemy(pvpLeader, ad?.Attacker);
            if (companionSubPet) groupMember = pvpMember;
            Group group = groupMember?.Group;
            if (group == null || ad?.Attacker is not GameLiving attacker ||
                !attacker.IsAlive || !group.IsInTheGroup(groupMember))
                return;

            // M12: a groupmate dropping low wakes the group's healers at once (also while a pull is brought back)
            WakeHealersIfLow(group, victim, ad);

            // A hired tank bringing a /pull monster back to its player: the group waits for it there (True 1.65 Mode).
            if (PlayerLedPullCoordinator.IsBringingBack(groupMember, attacker))
                return;

            // Record the encounter synchronously so even a very short roadside
            // fight pauses autonomous travel for whole-party recovery afterward.
            AutonomousBotGroupCoordinator.MarkCombatObserved(group);

            foreach (GameLiving member in group.GetMembersInTheGroup())
            {
                if (member == victim || member is not GameBot bot ||
                    companionSubPet && CompanionPvpEngagement.Leader(bot) != pvpLeader ||
                    bot.Group != group || !group.IsInTheGroup(bot) ||
                    !bot.IsAlive || bot.ObjectState != GameObject.eObjectState.Active ||
                    bot.CurrentRegionID != victim.CurrentRegionID ||
                    !bot.IsWithinRadius(victim, GROUP_DEFENSE_ASSIST_RADIUS) ||
                    bot.Brain is not BotBrain brain)
                    continue;

                brain.OnGroupMemberAttacked(victim, ad);
                brain.WakeForAdd(attacker, victim);   // M12: an add joined the fight
            }
            if (groupMember is GameBot raidVictim)
                foreach (GameLiving member in AutonomousRealmRaid.ClaimNearbyDefenseBroadcast(raidVictim, GameLoop.GameLoopTime))
                    if (member is GameBot helper && helper.Group != group && helper.IsAlive && helper.ObjectState == GameObject.eObjectState.Active && !helper.IsOnStableMasterRoute &&
                        helper.CurrentRegionID == victim.CurrentRegionID && helper.IsWithinRadius(victim, GROUP_DEFENSE_ASSIST_RADIUS) &&
                        helper.Brain is BotBrain helperBrain)
                        helperBrain.OnGroupMemberAttacked(victim, ad);
        }

        public void OnGroupMemberAttacked(GameLiving victim, AttackData ad)
        {
            Group group = Body?.Group;
            GameLiving groupMember = GroupMemberForCombat(victim);
            if (CompanionPvpEngagement.RecordThreat(BotBody, victim, ad))
                groupMember = CompanionPvpEngagement.Character(victim);
            bool sisterParty = groupMember is GameBot other && other.Group != group &&
                AutonomousRealmRaid.SameExpedition(BotBody, other);
            if (group == null || groupMember == null || (!sisterParty && (groupMember.Group != group || !group.IsInTheGroup(groupMember))) ||
                !group.IsInTheGroup(Body) ||
                groupMember.CurrentRegionID != Body.CurrentRegionID ||
                victim.CurrentRegionID != Body.CurrentRegionID ||
                !Body.IsWithinRadius(victim, GROUP_DEFENSE_ASSIST_RADIUS) ||
                ad?.Attacker is not GameLiving attacker || !attacker.IsAlive ||
                !GameServer.ServerRules.IsAllowedToAttack(Body, attacker, true))
                return;
            if (sisterParty && (BotBody.IsOnStableMasterRoute || Body.CurrentZone == null || Body.CurrentZone != attacker.CurrentZone ||
                !PathfindingProvider.Instance.HasLineOfSight(Body.CurrentZone, new(Body.X,Body.Y,Body.Z),
                    new(attacker.X,attacker.Y,attacker.Z),PathfindingProvider.Instance.DefaultFilters))) return;

            switch (ad.AttackResult)
            {
                case eAttackResult.Blocked:
                case eAttackResult.Evaded:
                case eAttackResult.Fumbled:
                case eAttackResult.HitStyle:
                case eAttackResult.HitUnstyled:
                case eAttackResult.Missed:
                case eAttackResult.Parried:
                    PlayerLedPullCoordinator.OnGroupThreat(groupMember);
                    AutonomousDefensivePull.OnThreat(groupMember, attacker);
                    if (sisterParty) AutonomousDefensivePull.OnThreat(BotBody, attacker);
                    AddToAggroList(attacker, attacker.EffectiveLevel + ad.Damage + ad.CriticalDamage);
                    break;
            }

            if (HasAggro)
            {
                if (sisterParty) AutonomousBotGroupCoordinator.MarkCombatObserved(group);
                // Like a direct hit, a nearby group-defense event must wake a
                // resting bot now, not wait for its next idle planning turn.
                // Membership/range/attack legality were checked above.
                if (BotBody?.IsRecoveryResting == true)
                    BotBody.WakeRecoveryRest();
                NextThinkTick = GameLoop.GameLoopTime;
                if (FSM.GetState(eFSMStateType.AGGRO) != FSM.GetCurrentState())
                    FSM.SetCurrentState(eFSMStateType.AGGRO);
            }
        }

        #endregion

        #region Brain Core

        // owner 2026-11-10: true for a healing class, and for a Bard while it holds its current group's Healer slot
        // (BardGroupHealer165 sets HealerSlotGroup; leaving that group ends the duty by itself; switching
        // bot_bard_fills_healer_slot off ends it at once in live groups too)
        private bool _isHealerClass;
        public Group HealerSlotGroup;
        public bool IsHealer
        {
            get => _isHealerClass || BardGroupHealer165.HoldsHealerSlot(HealerSlotGroup, Body?.Group);
            set => _isHealerClass = value;
        }
        public bool AlreadyCheckedHeals;
        private long _nextPriorityTauntTick;
        private long _nextDeployablePetTick;
        private long _nextCombatProgressTick;
        private long _nextMaintenanceBuffTick;
        private readonly Dictionary<(GameLiving Target, int Spell), (int Tries, long Since)> _buffAttempts = new();

        /// <summary>
        /// A buff cast on someone who still lacks it after two tries in two minutes is left alone for the rest of those
        /// two minutes (owner 2026-11-05, player report: buffers spun from member to member with "20 concentration
        /// spells" in the log, which broke sitting; the effect was refused when it landed and the bot kept retrying).
        /// </summary>
        private bool BuffKeepsFailing(GameLiving target, Spell spell)
        {
            long now = GameLoop.GameLoopTime;
            if (!_buffAttempts.TryGetValue((target, spell.ID), out var tries))
                return false;
            if (now - tries.Since > 120_000)
            {
                _buffAttempts.Remove((target, spell.ID));
                return false;
            }
            return tries.Tries >= 2;
        }

        private void NoteBuffAttempt(GameLiving target, Spell spell)
        {
            long now = GameLoop.GameLoopTime;
            if (_buffAttempts.Count > 64)
                foreach (var stale in _buffAttempts.Where(entry => now - entry.Value.Since > 120_000).Select(entry => entry.Key).ToList())
                    _buffAttempts.Remove(stale);
            _buffAttempts[(target, spell.ID)] = _buffAttempts.TryGetValue((target, spell.ID), out var tries) && now - tries.Since <= 120_000
                ? (tries.Tries + 1, tries.Since)
                : (1, now);
        }
        private int _groupAliveCount;
        private long _nextSongTwistTick;
        private bool _companionPerformerBuffBatch;
        private long _lastPerformerFollowTick = long.MinValue;
        private long _nextInstrumentKitCheck;
        private long _nextNecromancerCommandTick;
        private int _activeTwistedSongId;
        private long _songHoldUntil, _songMovingSince, _songStoppedSince;   // holding one song (owner 2026-10-30)
        private bool _songTraveling;
        private readonly Dictionary<int, SpellLine> _knownSpellLines = new();
        private GamePlayer _subscribedAssistedPlayer;
        private GameLiving _orderedPullTarget;
        private bool _returnToFormationAfterPull;
        private GamePlayer _activityTrackedLeader;
        private int _leaderLastX;
        private int _leaderLastY;
        private int _leaderLastZ;
        private long _leaderLastActiveTick;
        private long _temporaryCompanionLastActionTick;
        private bool _ambientWanderMovement;
        private DateTime _lastIdleRoleplayUtc = DateTime.MinValue;
        private long _nextAmbientChatCheckTick;
        private long _nextExchangeCheckTick;
        private long _nextMerchantServiceTick;
        // The merchant whose visit set _nextMerchantServiceTick: the wait holds back that merchant, not the one a trip
        // was sent to (2026-11-01 run 5: the nearest-merchant pulse failed at another merchant in town, and its wait kept
        // the planned merchant from serving the bot until the trip gave up - 131 of 131 abandoned trips).
        private GameMerchant _nextMerchantServiceFor;
        private long _pendingExchangeListTick;
        private DbInventoryItem _pendingExchangeItem;
        private AutonomousBotEconomy.ListingCandidate _pendingExchangePlan;
        private int _pendingExchangePrice;
        private AutonomousWorldBotController _autonomousWorldController;
        private long _nextFidelityCheckTick;
        private long _nextNaturalMobWakeTick;
        private eAutonomousFidelity _cachedFidelity = eAutonomousFidelity.Efficient;
        public GamePlayer AssistedPlayer => BotBody?.PlayerGroupLeader ?? BotBody?.Owner;
        public override int ThinkInterval { get; set; } = 500;

        private const int LEADER_IDLE_WANDER_DELAY_MS = 30_000;
        private const int IDLE_WANDER_CYCLE_MS = 12_000;

        public BotBrain() : base()
        {
            FSM = new FSM();
            FSM.Add(new BotState_Idle(this));
            FSM.Add(new BotState_Follow(this));
            FSM.Add(new BotState_Aggro(this));
            FSM.Add(new BotState_Passive(this));
            FSM.SetCurrentState(eFSMStateType.IDLE);
        }

        public override bool Start()
        {
            if (!base.Start())
                return false;

            // Track the current player leader for formation and group defense.
            RebindAssistedPlayer();

            return true;
        }

        public override bool Stop()
        {
            // Unregister from owner attack events
            if (_subscribedAssistedPlayer != null)
                GameEventMgr.RemoveHandler(_subscribedAssistedPlayer, GameLivingEvent.AttackedByEnemy, new DOLEventHandler(OnOwnerAttacked));
            _subscribedAssistedPlayer = null;

            if (base.Stop())
            {
                ClearAggroList();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Called when the bot's owner is attacked by an enemy.
        /// Adds the attacker to the aggro list and transitions to AGGRO state.
        /// </summary>
        /// <param name="ad">The attack data</param>
        public virtual void OnOwnerAttacked(AttackData ad)
        {
            GamePlayer leader = AssistedPlayer;
            if (leader != null)
                OnGroupMemberAttacked(leader, ad);
        }

        public void RebindAssistedPlayer()
        {
            GamePlayer next = AssistedPlayer;
            if (_subscribedAssistedPlayer == next)
                return;
            if (_subscribedAssistedPlayer != null)
                GameEventMgr.RemoveHandler(_subscribedAssistedPlayer, GameLivingEvent.AttackedByEnemy, new DOLEventHandler(OnOwnerAttacked));
            _subscribedAssistedPlayer = next;
            ResetLeaderActivity(next);
            // GamePlayer.OnAttackedByEnemy performs one group-scoped broadcast.
            // Do not subscribe every bot independently or the same attack would
            // be processed twice by player-led companions.
        }

        public virtual bool OrderPull(GameLiving target)
        {
            if (target == null || !target.IsAlive || !CanAggroTarget(target))
                return false;

            if (BotBody?.IsTemporaryCompanionRestLocked == true)
                return false;

            if (SupportSitsOutFights())
            {
                _orderedPullTarget = null;
                _returnToFormationAfterPull = false;
                ClearAggroList();
                Body.StopAttack();
                Body.TargetObject = null;
                if (SupportCallsPetOff)
                    Body.ControlledBrain?.Disengage();
                FSM.SetCurrentState(eFSMStateType.FOLLOW);
                return false;
            }

            _orderedPullTarget = target;
            _returnToFormationAfterPull = BotBody?.IsPlayerLedGroup == true;
            AddToAggroList(target, Math.Max(100, target.EffectiveLevel * 10));
            FSM.SetCurrentState(eFSMStateType.AGGRO);
            AttackMostWanted();
            return true;
        }

        public void AssistPlayerAttack(GameLiving target)
        {
            CompanionEngagementMode.RememberPull(AssistedPlayer, target);
            if (!CompanionEngagementMode.Allows(Body, target)) return;
            if (CompanionPvpEngagement.Enemy(CompanionPvpEngagement.Leader(Body), target))
                BotBody.WakeTemporaryCompanionRest();
            if (SupportSitsOutFights())
            {
                TryCommandCompanionDruidPet(target);
                HoldSupportCombat();
                NextThinkTick = GameLoop.GameLoopTime;
                return;
            }
            if (ActiveOrderedPullTarget != target || !HasAggro)
            {
                if (BotBody.IsRecoveryResting) BotBody.WakeRecoveryRest();
                OrderPull(target);
                NextThinkTick = GameLoop.GameLoopTime;
            }
        }

        public virtual void PrepareForTankPull()
        {
            _orderedPullTarget = null;
            _returnToFormationAfterPull = false;
            ClearAggroList();
            Body.StopAttack();
            Body.StopFollowing();
            Body.TargetObject = null;
            Body.ControlledBrain?.Disengage();
            Body.ControlledBrain?.Follow(Body);
            if (Body.IsCasting && Body.castingComponent?.SpellHandler?.Spell?.IsHarmful == true && !PvpControlInFlight)
                Body.StopCurrentSpellcast();
        }

        public virtual void CancelOrderedPull(GameLiving target)
        {
            if (_orderedPullTarget != target) return;
            _orderedPullTarget = null;
            _returnToFormationAfterPull = false;
            RemoveFromAggroList(target);
            if (!HasAggro)
            {
                Body.StopAttack();
                if (Body.TargetObject == target) Body.TargetObject = null;
                Body.ControlledBrain?.Follow(Body);
                FSM.SetCurrentState(eFSMStateType.FOLLOW);
            }
        }

        internal GameLiving ActiveOrderedPullTarget
        {
            get
            {
                if (_orderedPullTarget?.IsAlive == true &&
                    _orderedPullTarget.ObjectState is GameObject.eObjectState.Active &&
                    CanAggroTarget(_orderedPullTarget))
                    return _orderedPullTarget;

                _orderedPullTarget = null;
                return null;
            }
        }

        // M7: the Pet row - Guard me and Take adds hold the owner's own "pet, attack my target" orders (Take adds is
        // sent at adds by the pet service), Attack my target never holds them; Automatic and gamebots as before
        private bool UsesDefensiveOnlyPet => BotBody?.CharacterClass != null &&
            CompanionControls165.BrainHoldsPetOffTarget(CompanionControls165.PetJob(BotBody),
                AutonomousPetSupport.IsDefensiveOnlyPetClass((eCharacterClass)BotBody.CharacterClass.ID, BotBody.Group?.MemberCount > 1,
                    BotBody.CharacterClass.ID == (int)eCharacterClass.Druid && AutonomousPetSupport.IsNatureDruid(BotBody)));

        // A support companion sitting a fight out calls its pet off - but never a companion Druid's pet, which she sends
        // at the group's target herself while she heals (TryCommandCompanionDruidPet). On "Attack my target", and for a
        // Nature Druid on Automatic, the call-off undid that order every pulse: the pet ran out and came back (owner
        // 2026-11-12)
        private bool SupportCallsPetOff => CompanionControls165.SupportCallsPetOff(UsesDefensiveOnlyPet,
            CompanionFollowPolicy.SendsDruidPet(BotBody));

        // Class role, not pet ownership: includes Runemaster/Wizard as well as
        // summoners. Melee hybrids and archers retain their own combat policy.
        public static bool PrefersSpellRange(ICharacterClass characterClass, bool grouped = false, bool autonomous = false) =>
            characterClass?.ID == (int)eCharacterClass.Minstrel && !grouped && !autonomous ||
            characterClass?.ClassType == eClassType.ListCaster &&
            characterClass.ID != (int)eCharacterClass.Valewalker &&
            characterClass.ID != (int)eCharacterClass.Vampiir &&
            // Shamans melee between their nukes (ShamanBotCombatPolicy).
            characterClass.ID != (int)eCharacterClass.Shaman;

        /// <summary>Hybrids that fight with their weapon and cast on the side (period play): they melee once in range.</summary>
        public static bool IsMeleeHybridClass(eCharacterClass characterClass) => characterClass is
            eCharacterClass.Thane or eCharacterClass.Paladin or eCharacterClass.Reaver or eCharacterClass.Champion or
            eCharacterClass.Valewalker or eCharacterClass.Skald or eCharacterClass.Warden or eCharacterClass.Friar;

        private bool PrefersCurrentSpellRange() =>
            (PrefersSpellRange(BotBody?.CharacterClass, BotBody?.Group?.MemberCount > 1, BotBody?.IsAutonomousWorldBot == true) ||
            BotBody?.CharacterClass?.ID == (int)eCharacterClass.Shaman && RoleChoice is 1 or 2 ||   // AI Options role
            // a grouped Shaman with a tank doesn't melee: "aggro then follows you when you heal" (period forums, Jun 2002)
            BotBody?.CharacterClass?.ID == (int)eCharacterClass.Shaman && RoleChoice == 0 && Classic165Mode.IsOn &&
                BotMeleeStylePolicy.HasTankPartner(BotBody) ||
            // Q3: a Smite Cleric fights from spell range (Prima: pulls with a spec Smite, then baseline Smite, a stun and
            // more Smites "until the mob closes"), unless the player made it a melee healer
            IsSmiteCleric && RoleChoice != 3 ||
            PerformerStaysBack) && !PerformerMelees;

        /// <summary>Set by CheckSpells when a grouped support Cleric keeps its power for heals: no mace charge.</summary>
        private bool _clericHoldsBack;

        /// <summary>A Cleric built for Smite: its Smite spec template, or Smite trained (above 1) at least as high as
        /// Rejuvenation.</summary>
        private bool IsSmiteCleric => BotBody?.CharacterClass?.ID == (int)eCharacterClass.Cleric &&
            (BotBody.BotSpec?.SpecType == eSpecType.SmiteCleric ||
             BotCasterPriority.SmiteSpecced(BotBody.GetBaseSpecLevel(Specs.Smite), BotBody.GetBaseSpecLevel(Specs.Rejuvenation)));

        /// <summary>A hired Bard, Minstrel or Skald whose player chose "Support": songs and spells from the back, no melee.</summary>
        private bool PerformerStaysBack => IsClassicSongClass(BotBody) && CompanionOptions165.Choice(BotBody, "perform") == 1;

        /// <summary>... or "Melee": it fights in melee even where it would otherwise keep its distance.</summary>
        private bool PerformerMelees => IsClassicSongClass(BotBody) && CompanionOptions165.Choice(BotBody, "perform") == 2;

        // True when the body already runs the same melee follow NpcStartAttack would
        // issue (its own target, melee weapon, stick distance).
        private bool IsMeleeChasing(GameObject target) =>
            target != null && Body.FollowTarget == target &&
            Body.ActiveWeaponSlot != eActiveWeaponSlot.Distance &&
            Body.movementComponent.MinFollowDistance <= Body.StickMinimumRange;

        private bool UsesMinstrelHybridCombat => BotBody?.IsAutonomousWorldBot == true &&
            BotBody.CharacterClass?.ID == (int)eCharacterClass.Minstrel;

        /// <summary>The player's "Role" choice for a hired healer (AI Options; 0 = Automatic).</summary>
        private int RoleChoice => CompanionOptions165.Choice(BotBody, "role");

        /// <summary>
        /// A support class that stays out of the player's fights and pulls (it heals and buffs). Q7 (veteran review
        /// 2026-11-11): a Cleric, Druid or Healer companion whose player picked a fighting Role ("Heal first, attack when
        /// nobody is hurt", "Attack, heal only emergencies", "Melee and heal") joins the fight like any other companion;
        /// before, the four assist and pull checks left it out. Gamebots (always Automatic) are unchanged.
        /// </summary>
        private bool SupportSitsOutFights() =>
            BotPartyRoles.IsSupport(BotBody) && !CompanionOptions165.HealerRoleFights(
                (eCharacterClass)BotBody.CharacterClass.ID, RoleChoice);

        // M7: the Friar's and the Warden's "Main healer (stays back)" is choice 2 (the Friar's stayed back on its
        // "Off-healer" choice 1 before), the Shaman's "Stay back and heal" choice 1
        private bool HoldsExclusiveSupportRole() =>
            BotBody?.CharacterClass != null && CompanionControls165.StaysBackAsHealer((eCharacterClass)BotBody.CharacterClass.ID,
                RoleChoice, BotBody.Group?.MemberCount > 1) ||
            (RoleChoice == 0 || BotBody?.CharacterClass?.ID is not ((int)eCharacterClass.Cleric or (int)eCharacterClass.Druid or
                (int)eCharacterClass.Healer)) &&
            BotPartyRoles.IsSupport(BotBody) &&
            ((eCharacterClass?)BotBody?.CharacterClass?.ID != eCharacterClass.Minstrel ||
             RecentDirectAttacker() is not GameLiving attacker ||
             !Body.IsWithinRadius(attacker, Body.MeleeAttackRange + 35));

        private int PlayerLedCasterEngagementRange(GameLiving target)
        {
            if (target == null || BotBody?.IsPlayerLedGroup != true ||
                !PrefersCurrentSpellRange())
                return 0;

            int range = OffensiveApproachRange(target);
            return Math.Max(0, range - 100);
        }

        private bool ApproachPlayerLedPullTarget(GameLiving target)
        {
            int range = PlayerLedCasterEngagementRange(target);
            if (range <= 0 || Body.IsWithinRadius(target, range))
                return false;

            // Walking into range is an intentional combat action; a resting bot
            // ignores follow orders.
            BotBody.WakeRecoveryRest();
            Body.StopAttack();
            if (!UsesDefensiveOnlyPet && LeaderIsFighting(target))
                CommandPetAttack(target);
            else
                Body.ControlledBrain?.Follow(Body);
            (int minDistance, int maxDistance) = PlayerLedApproachFollowDistances(range);
            Body.Follow(target, minDistance, maxDistance);
            return true;
        }

        /// <summary>
        /// Follow distances for walking a caster into range of a player-led
        /// target. The stop distance is just inside spell range. The maximum is
        /// how far away the target may be while we still chase it. It must not
        /// be the spell range: native follow gives up at once when the target
        /// is beyond the maximum, which left casters standing still whenever
        /// the target was more than a few units out of range.
        /// </summary>
        public static (int MinDistance, int MaxDistance) PlayerLedApproachFollowDistances(int engagementRange) =>
            (Math.Clamp(engagementRange - 100, 160, short.MaxValue), MAX_AGGRO_LIST_DISTANCE);

        /// <summary>True when the human leader, or the leader's own pet, is already fighting this target.</summary>
        private bool LeaderIsFighting(GameLiving target) =>
            target != null && PlayerLedPullCoordinator.FindLeaderTarget(AssistedPlayer) == target;

        /// <summary>
        /// The one combat target this bot's pet should have for this decision.
        /// Several owner systems (pull orders, protection, the last attacker,
        /// threat) can name different enemies in the same turn, and each changed
        /// order used to cancel the pet's current cast. The pet therefore keeps
        /// its current, still-valid target, and switches only when:
        /// - the owner has an explicit ordered pull target (a leader/pull
        ///   command), or
        /// - the current target has died, left, become illegal, or been mezzed, or
        /// - the candidate is attacking the owner while the current target is
        ///   attacking neither the owner nor the pet (the pet peels for its owner).
        /// None of these can alternate between two enemies.
        /// </summary>
        public GameLiving ResolvePetCombatTarget(GameLiving candidate)
        {
            if (candidate == null || Body.ControlledBrain is not ControlledMobBrain petBrain)
                return candidate;
            // sh-bosses M2: never at a boss pets cannot hurt; an add instead, or back to its owner (BotBrain.RaidBoss)
            if (PetsDoNoDamageTo(candidate) && (candidate = PetTargetInsteadOfBoss(candidate, petBrain)) == null)
                return null;

            GameLiving current = petBrain.OrderedAttackTarget;
            if (current == null || current == candidate || candidate == ActiveOrderedPullTarget)
                return candidate;

            if (!IsValidPetCombatTarget(petBrain.Body, current))
                return candidate;

            bool candidateAttacksOwner = candidate.TargetObject == Body;
            bool currentAttacksOwnerOrPet = current.TargetObject == Body || current.TargetObject == petBrain.Body;
            return candidateAttacksOwner && !currentAttacksOwnerOrPet ? candidate : current;
        }

        public static bool IsValidPetCombatTarget(GameNPC pet, GameLiving target) =>
            pet != null && target?.IsAlive == true &&
            target.ObjectState == GameObject.eObjectState.Active &&
            target.CurrentRegion == pet.CurrentRegion &&
            !target.IsMezzed &&
            pet.IsWithinRadius(target, MAX_AGGRO_LIST_DISTANCE) &&
            CompanionEngagementMode.Allows(pet, target) &&
            GameServer.ServerRules.IsAllowedToAttack(pet, target, true);

        /// <summary>
        /// Druids and Minstrels keep a defensive pet in the bot's own pet logic, and a Druid heals with its target on a
        /// friend, so their pets only defended (owner 2026-10-31: druids send their pet in while healing, as they did
        /// before, and minstrels use their pet aggressively too, solo or grouped). Every turn of a fight the pet goes for
        /// the enemy: the human leader's target, else the bot's own hostile target, else the enemy a groupmate is
        /// attacking, else the bot's worst threat.
        /// </summary>
        private void KeepSupportPetFighting()
        {
            if (BotBody?.CharacterClass?.ID is not ((int)eCharacterClass.Druid or (int)eCharacterClass.Minstrel) ||
                Body.ControlledBrain is not ControlledMobBrain pet ||
                pet.Body?.IsAlive != true || pet.Body.ObjectState != GameObject.eObjectState.Active)
                return;
            GameLiving enemy = AssistedPlayer is GamePlayer ? PlayerLedPullCoordinator.FindLeaderTarget(AssistedPlayer) : null;
            if (enemy == null && (Body.IsAttacking || Body.InCombat || HasAggro) && Body.TargetObject is GameLiving own && own.IsAlive &&
                GameServer.ServerRules.IsAllowedToAttack(Body, own, true))
                enemy = own;
            if (enemy == null && Body.Group != null)
                enemy = Body.Group.GetMembersInTheGroup()
                    .Where(m => m != Body && m.IsAlive && m.IsAttacking && m.CurrentRegion == Body.CurrentRegion)
                    .Select(m => m.TargetObject as GameLiving)
                    .FirstOrDefault(t => t != null && GameServer.ServerRules.IsAllowedToAttack(Body, t, true));
            if (enemy == null && (Body.InCombat || HasAggro))
                enemy = CalculateNextAttackTarget();
            if (enemy != null && IsValidPetCombatTarget(pet.Body, enemy) && GameServer.ServerRules.IsAllowedToAttack(Body, enemy, true))
                CommandPetAttack(enemy);
        }

        /// <summary>Orders the pet onto the resolved target; repeated identical orders are ignored by the pet brain.</summary>
        private void CommandPetAttack(GameLiving candidate)
        {
            // A Deathsight servant is not sent into melee (it casts the shade's commands from where it stands)
            if (AutonomousPetSupport.ServantHoldsBack(Body))
            {
                AutonomousPetSupport.CallOffServant(Body.ControlledBrain);
                return;
            }
            GameLiving target = ResolvePetCombatTarget(candidate);
            if (target != null)
                Body.ControlledBrain?.Attack(target);
        }

        private void ResetLeaderActivity(GamePlayer leader)
        {
            _activityTrackedLeader = leader;
            _leaderLastX = leader?.X ?? 0;
            _leaderLastY = leader?.Y ?? 0;
            _leaderLastZ = leader?.Z ?? 0;
            _leaderLastActiveTick = GameLoop.GameLoopTime;
            _temporaryCompanionLastActionTick = GameLoop.GameLoopTime;
        }

        private void ObserveLeaderActivity()
        {
            GamePlayer leader = AssistedPlayer;
            if (leader == null || leader != _activityTrackedLeader)
            {
                ResetLeaderActivity(leader);
                return;
            }

            long dx = leader.X - _leaderLastX;
            long dy = leader.Y - _leaderLastY;
            long dz = leader.Z - _leaderLastZ;
            bool changedPosition = dx * dx + dy * dy + dz * dz >= 64;
            bool activelyPlaying = leader.IsMoving || changedPosition || leader.InCombat || leader.IsAttacking ||
                                   (leader.IsCasting && leader.castingComponent?.SpellHandler?.Spell?.IsHarmful == true);
            if (activelyPlaying)
                _leaderLastActiveTick = GameLoop.GameLoopTime;

            _leaderLastX = leader.X;
            _leaderLastY = leader.Y;
            _leaderLastZ = leader.Z;

            GameBot bot = BotBody;
            bool companionActing = bot?.IsTemporaryGroupHelper == true &&
                ((bot.IsMoving && !CanSettleForCompanionRest(bot, leader)) || bot.InCombat || bot.IsAttacking || HasAggro ||
                 ActiveOrderedPullTarget != null ||
                 (bot.IsCasting || bot.castingComponent?.HasPendingSkillRequests == true) &&
                 !BotSongTwistPolicy.HasMobileSongCast(bot));
            if (companionActing)
                _temporaryCompanionLastActionTick = GameLoop.GameLoopTime;
        }

        internal void MarkTemporaryCompanionRestActivity()
        {
            if (BotBody?.IsTemporaryGroupHelper == true)
                _temporaryCompanionLastActionTick = GameLoop.GameLoopTime;
        }

        private static bool CanSettleForCompanionRest(GameBot bot, GamePlayer leader) =>
            bot != null && BotRestRecovery.CanSettleForRest(
                bot.IsTemporaryGroupHelper, bot.IsAutonomousWorldBot,
                leader?.IsAlive == true && bot.Group == leader.Group &&
                    bot.CurrentRegionID == leader.CurrentRegionID && bot.IsWithinRadius(leader, 350),
                leader?.IsMoving != false,
                BotRestRecovery.HasAnyResourceDeficit(bot.Health, bot.MaxHealth,
                    bot.Mana, bot.MaxMana, bot.Endurance, bot.MaxEndurance));

        private bool CanAmbientWander =>
            BotBody?.IsPlayerLedGroup == true &&
            AssistedPlayer?.IsAlive == true &&
            !BotBody.IsRecoveryResting &&
            !AutonomousRestPolicy.NeedsRecovery(
                Body.HealthPercent,
                Body.ManaPercent,
                Body.EndurancePercent,
                Body.MaxMana > 0) &&
            !AssistedPlayer.IsMoving &&
            !AssistedPlayer.InCombat &&
            !AssistedPlayer.IsAttacking &&
            GameLoop.GameLoopTime - _leaderLastActiveTick >= LEADER_IDLE_WANDER_DELAY_MS;

        public void EnforceCompanionEngagementRange()
        {
            if (CompanionEngagementMode.DefensiveLeader(Body) == null) return;
            foreach (GameLiving target in AggroList.Keys)
                if (!CompanionEngagementMode.Allows(Body, target)) RemoveFromAggroList(target);
            if (ActiveOrderedPullTarget is GameLiving order && !CompanionEngagementMode.Allows(Body, order))
                CancelOrderedPull(order);
            if (Body.TargetObject is GameLiving current && !CompanionEngagementMode.Allows(Body, current))
            {
                Body.StopAttack();
                Body.StopFollowing();
                if (Body.castingComponent?.SpellHandler?.Spell?.IsHarmful == true) Body.StopCurrentSpellcast();
                Body.TargetObject = null;
            }
        }

        private long _nextPassiveLogTick;

        /// <summary>
        /// /passive (owner 2026-11-11: "for retreating or walking through areas without stopping to fight anything"): a
        /// player's companion stops attacking and casting, drops every target and any ordered pull, puts its pets (a
        /// Bonedancer's minions too) on passive, stands up and follows. False when its player is not passive.
        /// A Minstrel's charm renewal is not interrupted, so a charmed pet does not turn; O2 (owner 2026-11-11): neither is
        /// a heal, a cure mez or the speed song (see <see cref="TryPassiveLifesavers"/>).
        /// </summary>
        public bool HoldCompanionPassive()
        {
            GameBot bot = BotBody;
            GamePlayer leader = CompanionEngagementMode.PassiveLeader(bot);
            if (leader == null) return false;
            bool acted = false;
            if (bot.Group != null) acted |= PlayerLedPullCoordinator.CancelPendingForPassive(leader, bot);
            if (_orderedPullTarget != null) { CancelOrderedPull(_orderedPullTarget); _orderedPullTarget = null; acted = true; }
            if (HasAggro) { ClearAggroList(); acted = true; }
            if (Body.IsAttacking) { Body.StopAttack(); acted = true; }
            // O2 (owner 2026-11-11): a charm, the speed song or chant, a cure mez or a heal it may cast is left to finish
            if (Body.IsCasting && Body.castingComponent?.SpellHandler?.Spell is Spell casting &&
                !CompanionControls165.PassiveKeepsCast(casting.SpellType == eSpellType.Charm,
                    casting.SpellType == eSpellType.SpeedEnhancement && casting.IsPulsing, casting.SpellType == eSpellType.CureMezz,
                    casting.IsHealing, casting.IsInstantCast, leader.IsMoving))
            {
                Body.StopCurrentSpellcast();
                acted = true;
            }
            if (Body.TargetObject is GameLiving) Body.TargetObject = null;
            if (bot.IsRecoveryResting) bot.WakeTemporaryCompanionRest();
            var visited = new HashSet<IControlledBrain>();
            foreach (GameNPC pet in BotGroupPetBuffTargets.AttachedTree(Body.ControlledBrain, Body, visited))
            {
                if (pet.Brain is not IControlledBrain petBrain) continue;
                if (petBrain is StandardMobBrain { HasAggro: true } petAggro) { petAggro.ClearAggroList(); acted = true; }
                if (petBrain is NecromancerPetBrain servant && servant.HasPendingBotCommand)
                {
                    servant.ClearSpellQueue();
                    servant.ClearAttackSpellQueue();
                    if (pet.IsCasting) pet.StopCurrentSpellcast();
                    acted = true;
                }
                if (petBrain.AggressionState != eAggressionState.Passive)
                {
                    petBrain.SetAggressionState(eAggressionState.Passive);   // Disengage + follow the owner
                    acted = true;
                }
                else if (pet.IsAttacking || petBrain is ControlledMobBrain { OrderedAttackTarget: not null })
                {
                    petBrain.Disengage();
                    acted = true;
                }
            }
            if (acted && log.IsInfoEnabled && GameLoop.GameLoopTime >= _nextPassiveLogTick)
            {
                _nextPassiveLogTick = GameLoop.GameLoopTime + 10_000;
                log.Info($"COMPANION_PASSIVE_HOLD bot=\"{bot.Name}\" player=\"{leader.Name}\" region={Body.CurrentRegionID}");
            }
            return true;
        }

        /// <summary>How the heal check is limited this turn (O2: a /passive companion's lifesaver heals).</summary>
        private CompanionControls165.PassiveHealing _passiveHealing;

        /// <summary>
        /// O2 (owner 2026-11-11): what a passive companion still does while it follows and fights nothing - one path for
        /// /passive and the old /bot stance passive. Escape and rescue realm abilities (Vanish, Speed of Sound, Purge,
        /// Ignore Pain, Second Wind); instant heals and cure mez while its player moves, its normal heals once the player
        /// stops (never a resurrection, never a walk to reach someone); the speed song or chant.
        /// </summary>
        internal void TryPassiveLifesavers()
        {
            GameBot bot = BotBody;
            if (bot == null || !bot.IsAlive)
                return;
            AutonomousBotRealmAbilities.UseActives(bot, retreating: true);
            GamePlayer leader = AssistedPlayer ?? CompanionEngagementMode.PassiveLeader(bot) ?? bot.Owner;
            bool leaderMoving = leader?.IsMoving == true;
            _passiveHealing = leaderMoving ? CompanionControls165.PassiveHealing.WhileMoving : CompanionControls165.PassiveHealing.Stopped;
            try { CheckHeals(); }
            finally { _passiveHealing = CompanionControls165.PassiveHealing.None; }
            TryPassiveSpeedSong(bot, leaderMoving);
        }

        /// <summary>O2: a passive performer (or Warden) keeps its speed song or chant up; a cast-time chant only while the
        /// player stands still, and never over a faster speed already in the group.</summary>
        private void TryPassiveSpeedSong(GameBot bot, bool leaderMoving)
        {
            long now = GameLoop.GameLoopTime;
            bool performer = IsClassicSongClass(bot);
            if (!performer && bot.CharacterClass?.ID != (int)eCharacterClass.Warden || now < _nextSongTwistTick ||
                bot.IsCasting || bot.castingComponent?.HasPendingSkillRequests == true || bot.IsIncapacitated || bot.IsSilenced)
                return;
            _nextSongTwistTick = now + 1_000;
            Spell speed = (bot.MiscSpells ?? []).Concat(bot.InstantMiscSpells ?? [])
                .Where(spell => spell != null && spell.SpellType == eSpellType.SpeedEnhancement && spell.IsPulsing &&
                                !spell.IsHarmful && spell.Level <= bot.Level &&
                                (performer ? IsMaintainableClassBuff(spell) : BotSongTwistPolicy.IsManagedSong(bot, spell)))
                .OrderByDescending(spell => spell.Level).FirstOrDefault();
            if (speed == null || bot.Mana < bot.PowerCost(speed) || bot.GetSkillDisabledDuration(speed) > 0 ||
                leaderMoving && !BotSongTwistPolicy.IsMobileSong(bot, speed) ||
                bot.effectListComponent.GetPulseEffects().Any(effect => !effect.IsEnding && !effect.IsEnded &&
                    effect.SpellHandler?.Spell?.SpellType == eSpellType.SpeedEnhancement) ||
                GroupmateHasStrongerSpeed(speed))
                return;
            if (speed.NeedInstrument && !TryEquipRealInstrument(bot, speed.InstrumentRequirement))
                return;
            GameObject previousTarget = bot.TargetObject;
            bot.TargetObject = FindMissingMaintenanceTarget(speed) ?? bot;
            try
            {
                if (bot.CastSpell(speed, m_mobSpellLine, false) && performer)
                    _activeTwistedSongId = speed.ID;
            }
            finally { bot.TargetObject = previousTarget; }
        }

        private bool TryMaintainTemporaryCompanionRest()
        {
            GameBot bot = BotBody;
            if (bot?.IsTemporaryGroupHelper != true || bot.IsAutonomousWorldBot || !bot.IsPlayerLedGroup)
                return false;

            GamePlayer leader = AssistedPlayer;
            CabalistRestDiagnostics.Observe(bot, leader);
            bool atPlayer = leader?.IsAlive == true && bot.Group == leader.Group &&
                            leader.CurrentRegionID == bot.CurrentRegionID && bot.IsWithinRadius(leader, 350);
            bool movementBlocksRest = !CanSettleForCompanionRest(bot, leader) && (leader == null || AutonomousRestPolicy.MovementBlocksRest(
                bot.IsMoving,
                leader.IsMoving,
                _ambientWanderMovement));
            bool combatOrOrderBlocksRest = leader?.IsAttacking == true ||
                                           (leader?.IsCasting == true &&
                                            leader.castingComponent?.SpellHandler?.Spell?.IsHarmful == true) ||
                                           ActiveOrderedPullTarget != null ||
                                           PlayerLedPullCoordinator.IsWaiting(bot) ||
                                           BotRestRecovery.BlocksRest(bot) || BotRestRecovery.ServantBusyHelping(bot);

            bool shouldRest = BotRestRecovery.ShouldTemporaryCompanionRest(
                true,
                atPlayer,
                movementBlocksRest,
                combatOrOrderBlocksRest,
                GameLoop.GameLoopTime,
                BotRestRecovery.LatestRestActivityTick(
                    _leaderLastActiveTick,
                    _temporaryCompanionLastActionTick),
                bot.Health,
                bot.MaxHealth,
                bot.Mana,
                bot.MaxMana,
                bot.Endurance,
                bot.MaxEndurance);

            if (!shouldRest)
            {
                bot.WakeTemporaryCompanionRest();
                return false;
            }

            // Stop the old formation order once the quiet/combat gates permit
            // recovery, including before upkeep queues a non-mobile buff.
            bot.StopFollowing();
            bot.StopMovingOnPath();
            bot.StopMoving();

            // Required class buffs and permanent-pet/Bonedancer-army upkeep get
            // one chance before the rest transition. This prevents the old
            // sit-wake-sit cycle between maintenance casts. A successful cast
            // records companion activity and must finish before a new two-second
            // quiet window can qualify the bot for rest.
            if (TryRunTemporaryCompanionRequiredMaintenance())
                return true;

            _ambientWanderMovement = false;
            bool resting = bot.BeginTemporaryCompanionRest();
            if (resting && IsClassicSongClass(bot))
                TryMaintainClassicSongTwist();
            return resting;
        }

        private bool TryRunTemporaryCompanionRequiredMaintenance()
        {
            GameBot bot = BotBody;
            if (bot?.IsTemporaryGroupHelper != true || !bot.IsAlive || bot.InCombat || HasAggro)
                return false;

            // A missing real class buff is required upkeep. The normal
            // selection/cost/cooldown checks remain authoritative; CastSpell
            // clears an existing rest lock only after one valid request is selected.
            if (TryMaintainTravelAndClassBuffs())
                return true;

            eCharacterClass characterClass = (eCharacterClass)bot.CharacterClass.ID;
            if (!AutonomousPetSupport.OwnsPetUpkeep(characterClass))
                return false;

            // Main pets and Bonedancer armies must not remain absent merely
            // because their owner is recovering. Animist combat turrets are not
            // in OwnsPetUpkeep and therefore cannot wake an idle resting bot.
            return AutonomousPetSupport.Maintain(
                bot,
                null,
                ref _nextDeployablePetTick,
                out _);
        }

        private bool TryMaintainTemporaryCompanionBonedancerArmy()
        {
            GameBot bot = BotBody;
            if (bot == null || HasAggro ||
                !AutonomousPetSupport.NeedsTemporaryCompanionBonedancerArmyUpkeep(bot))
            {
                return false;
            }

            // This gate exists only for ephemeral /spawn Bonedancers. It runs
            // before owner-follow/rest can indefinitely defer the level-15+
            // commander replacement and subordinate summon. Persistent
            // autonomous gamebots remain on their existing maintenance path.
            return AutonomousPetSupport.Maintain(
                bot,
                null,
                ref _nextDeployablePetTick,
                out _);
        }

        private void FollowFormation(bool ambientWander = false)
        {
            // Any FOLLOW entry or support path must honor a temporary caster's
            // non-mobile speed cast, even if the leader leaves IDLE range while
            // those three seconds are in progress.
            if (CompanionFollowPolicy.NativeTravelSpeedCastInProgress(BotBody))
                return;
            // The traveling-performer preflight may already have issued this
            // turn's path. Do not calculate a duplicate raid formation path.
            if (_lastPerformerFollowTick == GameLoop.GameLoopTime)
                return;
            if (!ambientWander)
                _ambientWanderMovement = false;

            GamePlayer leader = AssistedPlayer;
            if (leader == null || leader.CurrentRegion != Body.CurrentRegion)
            {
                _ambientWanderMovement = false;
                return;
            }
            if (!ambientWander && CompanionHealerCombatHold.Holds(BotBody, leader) && CompanionEngagementMode.PassiveLeader(BotBody) == null)
            {
                // Stop a formation walk already under way; other movement is left alone.
                if (Body.IsMoving && CompanionFollowPolicy.HasFormationOrder(BotBody))
                    Body.StopMoving();
                return;
            }

            bool tightInterior = Body.CurrentRegion?.IsDungeon == true;
            bool companionTravel = CompanionFollowPolicy.Applies(BotBody);
            // Do not run native owner-follow and formation orders concurrently.
            if (companionTravel) Body.StopFollowing();
            // /camp (M7): the same slots, laid out around the camp and its facing instead of the player; a companion on its
            // own passive stance follows its player
            if (BotBody.IsPlayerCompanion && !CompanionRaid.IsMember(BotBody) &&
                CompanionEngagementMode.PassiveLeader(BotBody) == null &&
                CompanionEngagementMode.CampOf(leader, out Vector3 camp, out ushort campHeading))
            {
                _ambientWanderMovement = false;
                AutonomousFormation.Offset campFormation = AutonomousFormation.For(BotBody.Name, tightInterior);
                int campSlot = Math.Max(0, Body.GroupIndex - 1) % RearFormationDegrees.Length;
                ushort campSlotHeading = (ushort)((campHeading + RearFormationDegrees[campSlot] * 4096 / 360 + 4096 * 2) & 0xFFF);
                Point2D campSpot = new Point2D((int)camp.X, (int)camp.Y).GetPointFromHeading(campSlotHeading, campFormation.Distance);
                Vector3 campPoint = new(campSpot.X, campSpot.Y, camp.Z);
                if (companionTravel && leader.CurrentZone != null)
                    campPoint = CompanionFollowPolicy.FormationDestination(BotBody, campPoint);
                if (Vector3.DistanceSquared(new(Body.X, Body.Y, Body.Z), campPoint) > 20 * 20)
                {
                    if (companionTravel)
                        CompanionFollowPolicy.BeginFormation(BotBody, campPoint);
                    Body.PathTo(campPoint, Body.MaxSpeed);
                }
                return;
            }
            if (CompanionRaid.IsMember(BotBody))
            {
                // Stable, distinct slots for all 39 companions. Surface projection
                // and PathTo keep nearby slots from becoming walks through walls.
                // During a player keep siege a squad holds its own post instead (CompanionRaidSiege).
                var post = CompanionRaidSiege.FormationOverride(BotBody);
                var raidPoint = post ?? TemporaryGroupStableTravel.FormationPoint(leader, Math.Max(0, Body.GroupIndex - 1));
                if (post == null) raidPoint = CompanionFollowPolicy.FormationDestination(BotBody, raidPoint);
                _ambientWanderMovement = false;
                var point = new Point3D((int)raidPoint.X, (int)raidPoint.Y, (int)raidPoint.Z);
                if (Body.GetDistanceTo(point) > 35)
                {
                    CompanionFollowPolicy.BeginFormation(BotBody, raidPoint);
                    Body.PathTo(raidPoint, Body.MaxSpeed);
                }
                return;
            }
            AutonomousFormation.Offset formation = ambientWander
                ? AutonomousFormation.ForIdleWander(BotBody.Name, tightInterior, (int)(GameLoop.GameLoopTime / IDLE_WANDER_CYCLE_MS))
                : AutonomousFormation.For(BotBody.Name, tightInterior);
            // Behind and beside the player, turned with the player's facing (owner 2026-11-03: a companion stood in
            // front of its standing player; the spot used to be a fixed compass direction). Each group slot has its own
            // place in the rear half (behind, the rear diagonals, the flanks); idle wander drifts around it.
            int slot = Math.Max(0, Body.GroupIndex - 1) % RearFormationDegrees.Length;
            int drift = ambientWander ? formation.AngleDegrees - AutonomousFormation.For(BotBody.Name, tightInterior).AngleDegrees : 0;
            int relative = RearFormationDegrees[slot] + drift;
            ushort facing = FormationHeading(leader);
            ushort slotHeading = (ushort)((facing + relative * 4096 / 360 + 4096 * 2) & 0xFFF);
            Point2D spot = leader.GetPointFromHeading(slotHeading, formation.Distance);
            var destination = new Point3D(spot.X, spot.Y, leader.Z);
            // A standing player who only turns does not send the group walking around: a companion already behind or
            // beside its player, at about its distance, stays; one left in front of the player (a run-past, a fight)
            // walks back round behind.
            if (!ambientWander && !leader.IsMoving && !InFrontOfFormation(leader, facing, Body, 120) &&
                Body.IsWithinRadius(leader, formation.Distance + 80) && !Body.IsWithinRadius(leader, 30))
            {
                if (!Body.IsMoving)
                    _ambientWanderMovement = false;
                return;
            }
            if (companionTravel && !ambientWander && leader.CurrentZone != null)
            {
                Vector3 floor = CompanionFollowPolicy.FormationDestination(BotBody, new(destination.X, destination.Y, destination.Z));
                destination = new Point3D((int)floor.X, (int)floor.Y, (int)floor.Z);
            }
            int arrivalTolerance = ambientWander ? 12 : 20;
            if (Body.GetDistanceTo(destination) > arrivalTolerance)
            {
                if (companionTravel && !ambientWander)
                    CompanionFollowPolicy.BeginFormation(BotBody, new(destination.X, destination.Y, destination.Z));
                short speed = ambientWander
                    ? (short)Math.Clamp(Body.MaxSpeed * 2 / 5, 60, Body.MaxSpeed)
                    : Body.MaxSpeed;
                _ambientWanderMovement = ambientWander;
                if (companionTravel && !ambientWander)
                    Body.PathTo(destination, speed);
                else
                    Body.WalkTo(destination, speed);
            }
            else if (!Body.IsMoving)
                _ambientWanderMovement = false;
        }

        /// <summary>Formation places around a player, in degrees from the player's facing (180 = straight behind).</summary>
        private static readonly int[] RearFormationDegrees = [180, 140, 220, 110, 250, 160, 200];

        private sealed class FormationAnchor { public ushort Heading; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameLiving, FormationAnchor> FormationAnchors = new();

        /// <summary>
        /// The facing the formation is laid out from: the leader's heading, taken only while the leader moves (owner
        /// 2026-11-05: turning the camera with the right mouse button while standing turned the character and so
        /// reshuffled the companions; they should stay put until the player walks).
        /// </summary>
        private static ushort FormationHeading(GameLiving leader)
        {
            FormationAnchor anchor = FormationAnchors.GetValue(leader, l => new FormationAnchor { Heading = l.Heading });
            if (leader.IsMoving)
                anchor.Heading = leader.Heading;
            return anchor.Heading;
        }

        /// <summary>Is the bot within the leader's front cone, judged from the formation facing (see FormationHeading).</summary>
        private static bool InFrontOfFormation(GameLiving leader, ushort heading, GameObject body, double cone)
        {
            float diff = leader.GetHeading(body) - heading;
            if (diff < 0) diff += 4096f;
            float angle = diff * 360f / 4096f;
            return angle >= 360 - cone / 2 || angle < cone / 2;
        }

        private void MaybeIdleRoleplay()
        {
            if (!CanAmbientWander || Body.IsMoving || Body.IsCasting || Body.InCombat)
                return;

            DateTime now = DateTime.UtcNow;
            if (!AutonomousBotChat.ShouldSpeak(now, _lastIdleRoleplayUtc, false))
                return;

            GameNPC nearbyMonster = Body.GetNPCsInRadius(1200)
                .Where(npc => npc != Body && npc.IsAlive && npc.Realm == eRealm.None && CanAggroTarget(npc) &&
                              StealthedMonsters165.CanSee(Body, npc))   // never chats about a hidden ambusher
                .OrderBy(Body.GetDistanceTo)
                .FirstOrDefault();
            var context = new AutonomousBotChat.Context(
                BotBody.Name,
                BotBody.ClassName,
                Body.CurrentZone?.Description,
                nearbyMonster?.Name,
                string.Empty,
                string.Empty,
                string.Empty,
                BotBody.Level,
                Math.Max(1, (int)(Body.Group?.MemberCount ?? 1)),
                BotBody.Realm);
            if (AutonomousBotChatCoordinator.TryStartAmbient(BotBody, context))
                _lastIdleRoleplayUtc = now;
        }


        private void OnOwnerAttacked(DOLEvent e, object sender, EventArgs args)
        {
            if (args is AttackedByEnemyEventArgs attackArgs)
                OnOwnerAttacked(attackArgs.AttackData);
        }

        /// <summary>
        /// A bot swinging at its target faces it (owner 2026-10-31: gamebots meleed facing away). The attack turns an NPC
        /// only once, when it starts; a bot that then walks to its own spot (closing in, a flank, behind for a style) and
        /// stops kept facing the way it walked. The original client turned fighters itself, the new client draws the
        /// server's heading, so the server turns it: standing, attacking in melee, more than 30 degrees off.
        /// </summary>
        private void FaceMeleeTarget()
        {
            GameNPC body = Body;
            if (body is not { IsAlive: true, IsAttacking: true } || body.IsMoving || body.IsCasting ||
                body.ActiveWeaponSlot == eActiveWeaponSlot.Distance ||
                body.TargetObject is not GameLiving target || !target.IsAlive || target.CurrentRegion != body.CurrentRegion ||
                !body.IsWithinRadius(target, 600))
                return;
            int off = Math.Abs(body.GetHeading(target) - body.Heading) % 4096;
            if (Math.Min(off, 4096 - off) > 4096 / 12)
                body.TurnTo(target);
        }

        public override void Think()
        {
            bool nativeSpeedTravelStarted;
            if (CompanionFollowPolicy.ObserveAndCancelBuffs(BotBody, out nativeSpeedTravelStarted))
            {
                _nextMaintenanceBuffTick = 0;
                _nextDeployablePetTick = 0;
            }
            // A parked companion may have just completed a no-missing-buffs
            // scan. On the first moving owner pulse, check native group speed
            // immediately instead of waiting for that idle-scan backoff.
            if (nativeSpeedTravelStarted)
                _nextMaintenanceBuffTick = 0;
            // Someone in the group came back to life: re-buff now, not after the idle-scan wait (owner 2026-11-05: a
            // cleric finished buffing a revived player only after the player moved and stopped again).
            int alive = BotBody?.IsPlayerLedGroup == true ? Body.Group?.GetMembersInTheGroup().Count(member => member.IsAlive) ?? 0 : 0;
            if (alive > _groupAliveCount)
                _nextMaintenanceBuffTick = 0;
            _groupAliveCount = alive;
            EnforceCompanionEngagementRange();
            GameLiving nearbyPull = CompanionEngagementMode.NearbyPull(BotBody);
            if (nearbyPull != null && CanAggroTarget(nearbyPull)) AssistPlayerAttack(nearbyPull);
            AlreadyCheckedHeals = false;
            MaintainChaseSprint();
            if (!HasAggro && GameLoop.GameLoopTime >= _nextPoisonSupplyTick)
            {
                _nextPoisonSupplyTick = GameLoop.GameLoopTime + 2_000;
                BotPoisonSupply.Maintain(BotBody);
            }
            AutonomousSummonActivity.RecoverOwner(BotBody);
            BotAnimistPolicy.RestoreEncounterTarget(BotBody);
            ObserveLeaderActivity();
            CompanionStealth.Maintain(BotBody);
            WatchPetFocusShield();
            WatchHarmfulAura();
            BotBody?.WakeRecoveryRestIfCombatBlocked();
            BotBody?.EnsureAutonomousRecoveryTimers();

            if (AutonomousStuckWatchdog.Observe(BotBody))
                return;

            // a Minstrel's charm twist: the charm goes back on the pet before anything else takes the turn
            TryRenewTwistedCharm();
            FaceMeleeTarget();

            // put away for its player's horse ride: nothing to do until it is back at the player's side. Checked on every
            // think, not only when the stable master hands over the ride (owner 2026-10-31: companions still ran behind)
            if (TemporaryGroupStableTravel.SyncStowWithLeaderRide(BotBody) || Body.StowedForRide)
                return;

            // /passive: fight nothing, follow the player (no buffs, rest or pet upkeep; regeneration ticks). O2 (owner
            // 2026-11-11): the lifesavers stay - escapes, instant heals and cure mez on the move, heals when the player
            // stops, the speed song or chant
            if (BotBody?.IsOnStableMasterRoute == false && HoldCompanionPassive())
            {
                TryPassiveLifesavers();
                if (HiredCompanions165.KeepWithOwner(BotBody) || HiredCompanions165.TryRecallToOwner(BotBody) ||
                    HiredCompanions165.TryUnstick(BotBody) ||
                    BotBody.IsTemporaryGroupHelper && BotBody.TryReturnTemporaryCompanionToLeader())
                    return;
                eFSMStateType? state = FSM.GetCurrentState()?.StateType;
                if (state == eFSMStateType.PASSIVE)
                    return;                                     // a GM's /bot hold stays as it is
                if (state != eFSMStateType.FOLLOW)
                    FSM.SetCurrentState(eFSMStateType.FOLLOW);   // its Enter walks to the formation spot
                else
                    FollowFormation();
                return;
            }

            // M6 (veteran review 2026-11-11): a party that called RUN, a solo bot bailing out, a hired healer surviving
            // to raise its player: run, fight nothing
            if (BotBody?.IsOnStableMasterRoute == false && TryHoldRetreat())
                return;

            AutonomousBotRealmAbilities.UseActives(BotBody);
            BotPotions165.TryCompanionDrink(BotBody);

            // Once a stable ticket has boarded, its waypoint chain exclusively
            // owns movement until the final point. No ordinary bot subsystem is
            // allowed to issue a competing follow, cast, pet, or combat order.
            if (BotBody?.IsOnStableMasterRoute == true)
            {
                ThinkInterval = 1_500 + BotBody.ObjectID % 350;
                BotBody.MaintainStableMasterRoute();
                if (BotBody.IsMovingOnPath || BotBody.CurrentPathPoint != null)
                    return;
                BotBody.TryCompleteStableMasterRouteAfterArrival();
                return;
            }

            // Population lifecycle: a gamebot logging out walks home and sits before it leaves (AutonomousBotLifecycle).
            if (BotBody?.IsAutonomousWorldBot == true && AutonomousBotLifecycle.DriveLogout(BotBody))
                return;

            // owner 2026-11-06: a hired companion with charming on and no pet charms the monster its player selects, before
            // anyone starts on it (Charm165; hired companions only, gamebots unchanged)
            if (Charm165.TryPreFightCharm(BotBody))
                return;

            CompanionPvpEngagement.Observe(BotBody);
            KeepSupportPetFighting();
            // Goal 11: a /raid 40 or /raid 80 on a keep siege works its squad job (rams, engines) on its own.
            if (CompanionRaidSiege.Think(this))
                return;

            // Frontier enemies take priority over rally/follow/rest and optional
            // buffs, even on a PvE task. Horse travel above stays authoritative.
            // True 1.65 Mode: a hired companion never runs the world bots' goals; out of its player's group it rejoins them
            if (HiredCompanions165.KeepWithOwner(BotBody))
                return;

            if (BotBody?.IsAutonomousWorldBot == true && !BotBody.IsPlayerLedGroup)
            {
                _autonomousWorldController ??= new AutonomousWorldBotController();
                if (_autonomousWorldController.TryEngageFrontierThreat(this))
                {
                    ThinkInterval = AutonomousFidelityPolicy.IntervalMilliseconds(eAutonomousThinkMode.Combat,
                        CurrentFidelity(), AutonomousBotRegistry.PopulationForBrainTick);
                    FSM.Think();
                    return;
                }
            }

            if (AutonomousDefensivePull.Hold(BotBody) || SoloRangedPull165.Hold(BotBody))
            {
                if (!Body.IsCasting) CheckHeals();
                return;
            }

            // A Necromancer servant's long self-buff is cast by the zombie,
            // not the shade. Hold the shade's already-issued route while that
            // native servant command is queued/casting so tether-follow cannot
            // drag the zombie out of its cast. This is deliberately below
            // stable travel and combat-aware: damage or a live combat order
            // releases it immediately, and the unchanged goal controller
            // resumes the route as soon as the buff completes.
            if (TryHoldForNecromancerServantSelfBuff())
                return;

            ClearIdleOwnedPetTarget();

            if (BotBody?.IsTemporaryGroupHelper == true &&
                (BotBody.TryReturnTemporaryCompanionToLeader() || TryResurrectCompanionOwner()))
                return;
            // True 1.65 Mode: a hired healer raises its dead player first, as a /spawn helper does
            if ((HiredCompanions165.IsHired(BotBody) || BotBody?.IsInvitedGamebot165 == true) && TryResurrectCompanionOwner())
                return;
            // True 1.65 Mode: a hired companion left far behind comes back to its player (owner 2026-10-30)
            if (HiredCompanions165.TryRecallToOwner(BotBody) || HiredCompanions165.TryUnstick(BotBody))
                return;
            // Any move of a companion (MoveTo stops the brain: WAKING_UP, which BotBrain lacks) left it with no state at all,
            // standing still until a stuck check moved it again (owner 2026-11-01). A companion in its player's group
            // follows; FOLLOW itself drops to IDLE while the player is dead.
            if (FSM.GetCurrentState() == null && BotBody?.IsPlayerCompanion == true && BotBody.IsPlayerLedGroup)
                FSM.SetCurrentState(eFSMStateType.FOLLOW);

            if (PlayerLedPullCoordinator.IsWaiting(BotBody))
            {
                // The tank alone owns the approach. Waiting allies may heal or
                // buff, but cannot chase or send a pet ahead of first contact.
                // A companion that was recovering before the pull must stand
                // immediately; it must never retain the sitting pose in combat.
                if (BotBody.IsRecoveryResting)
                    BotBody.WakeTemporaryCompanionRest();
                if (!CheckHeals()) CheckSpells(eCheckSpellType.Defensive);
                return;
            }

            // The player's mesmerize, stun or root joins the fight on another monster of the pack (owner 2026-10-29).
            GameLiving leaderTarget = PlayerLedPullCoordinator.FindLeaderTarget(AssistedPlayer) ??
                PlayerLedPullCoordinator.FindLeaderControlFight(AssistedPlayer, BotBody);
            if (leaderTarget != null && PlayerLedPullCoordinator.Available(BotBody, AssistedPlayer) &&
                Body.IsWithinRadius(leaderTarget, GROUP_DEFENSE_ASSIST_RADIUS))
                AssistPlayerAttack(leaderTarget);
            else if (leaderTarget == null)
                JoinStunnedCompanionKillTarget();

            if (BotBody?.TryApplyEndgameCompanionUpgrade() == true)
                return;

            WakeNearbyNaturalAggroBrains();

            // Narrow failsafe for an ephemeral /spawn companion whose owner's
            // completed portal/region transfer was not observed by its event.
            // Persistent bots and ordinary player group members never enter it.
            if (TemporaryGroupStableTravel.EnsureOwnerTransferCohesion(BotBody))
                return;

            // A used-up self bladeturn goes back up as soon as nothing is fighting
            // the bot, before rest and the rest of the upkeep (gamebots and companions).
            if (TryRestoreSelfBladeturn())
                return;

            if (TryMaintainTemporaryCompanionBonedancerArmy())
                return;

            // A parked /spawn party rests before optional songs, chants, buffs,
            // pet upkeep, or ambient roleplay can claim the AI turn. Combat,
            // pull orders, owner movement, resurrection/return travel, and full
            // recovery all release the lock and continue through normal AI.
            // Persistent autonomous playerbots never enter this branch.
            if (TryMaintainTemporaryCompanionRest())
                return;

            eAutonomousThinkMode mode = CurrentThinkMode();
            eAutonomousFidelity fidelity = CurrentFidelity();
            if (BotBody != null)
                BotBody.AutonomousFidelity = fidelity;
            // Set the cadence before any autonomous handler can return. Movement
            // remains continuous in the movement service between decisions.
            int interval = AutonomousFidelityPolicy.IntervalMilliseconds(mode, fidelity, AutonomousBotRegistry.PopulationForBrainTick);
            ThinkInterval = mode == eAutonomousThinkMode.Combat
                ? interval
                : AutonomousFidelityPolicy.Stagger(interval, BotBody?.DatabaseID ?? Body?.ObjectID ?? 0);

            // The forty-five minute goal watchdog is about a bot that has made
            // neither combat nor economic/XP progress. Record active fighting
            // at a coarse cadence so movement alone cannot hide a dead goal and
            // combat itself does not create per-frame persistence traffic.
            if (mode == eAutonomousThinkMode.Combat && BotBody?.IsAutonomousWorldBot == true &&
                (BotBody.InCombatInLast(15_000) || BotBody.ControlledBrain?.Body?.InCombatInLast(15_000) == true) &&
                GameLoop.GameLoopTime >= _nextCombatProgressTick)
            {
                _nextCombatProgressTick = GameLoop.GameLoopTime + 15_000 + BotBody.ObjectID % 2_000;
                AutonomousStuckWatchdog.MarkProgress(BotBody, eAutonomousProgressKind.Combat);
            }

            // Classic song classes rotate only learned, level-valid chants.
            // Every pulse is started through the ordinary spell handler and
            // therefore keeps its real instrument, power and interruption rules.
            // Grouped performers favor this support job; solo levelers never let
            // twisting displace their own immediate combat response.
            // A parked /spawn performer completes its ordinary buff pass before
            // another song can consume the cast slot. The batch flag below
            // keeps the song paused across the normal 1.75s buff backoff; a
            // moving owner releases it immediately for travel songs instead.
            bool companionPerformerBuffPriority = IsStationaryTemporaryCompanionPerformer();
            if (companionPerformerBuffPriority &&
                !BotRestRecovery.DeferOptionalCasterUpkeep(BotBody) &&
                TryMaintainTravelAndClassBuffs())
                return;

            FollowTravelingCompanionPerformer();
            if (TryMaintainClassicSongTwist())
                return;

            // Instant tank chants must not consume the attack/follow turn.
            TryMaintainTankChant();

            // A failed level-one summon/buff must never starve the world goal
            // controller.  The affected primary caster-pet classes postpone
            // upkeep while acquiring or following a route, then resume normal
            // pet/buff behavior at the destination or as soon as combat starts.
            bool movementFirstPetClass = BotBody != null &&
                AutonomousPetSupport.ShouldPrioritizeWorldMovement(
                    (eCharacterClass)BotBody.CharacterClass.ID,
                    BotBody.IsAutonomousWorldBot,
                    BotBody.IsTemporaryGroupHelper,
                    BotBody.IsPlayerLedGroup,
                    mode == eAutonomousThinkMode.Combat,
                    BotBody.IsMoving,
                    !string.IsNullOrWhiteSpace(BotBody.PersistentRecord?.CurrentCampId),
                    BotBody.PersistentRecord?.Activity);
            bool requiredBonedancerArmyUpkeep =
                AutonomousPetSupport.NeedsBonedancerArmyUpkeep(BotBody);
            bool requiredPrimaryPetUpkeep =
                AutonomousPetSupport.NeedsPrimaryPetUpkeep(BotBody);

            // A dungeon corridor pull is already committed after rest/readiness
            // checks. Do not let optional travel buffs repeatedly consume its
            // first combat turn. Missing primary pets still take their normal
            // upkeep path, and the combat FSM retains healing/casting rules.
            if (BotBody?.IsAutonomousWorldBot == true && !BotBody.IsPlayerLedGroup &&
                Body.CurrentZone?.IsDungeon == true && HasCommittedDungeonPull && !Body.InCombat &&
                !requiredPrimaryPetUpkeep && !requiredBonedancerArmyUpkeep &&
                Body.TargetObject is GameLiving { IsAlive: true } dungeonPull)
            {
                if (dungeonPull.CurrentRegion == Body.CurrentRegion &&
                    FSM.GetCurrentState()?.StateType == eFSMStateType.AGGRO)
                {
                    FSM.Think();
                    return;
                }
            }

            // Persistent world bots and temporary /spawn companions share this
            // conservative maintenance pass. It uses only learned spells and
            // real equipment, and never refreshes routine buffs in combat.
            bool deferCasterUpkeep = BotRestRecovery.DeferOptionalCasterUpkeep(BotBody);
            if (!companionPerformerBuffPriority && !movementFirstPetClass && !deferCasterUpkeep &&
                TryMaintainTravelAndClassBuffs())
                return;

            // Adopt a live target from our own pet before optional pet upkeep.
            // Previously this ran after Maintain(), so a pet heal/buff/summon
            // could consume every owner turn while a Hunter's pet fought alone.
            GameLiving ownedPetTarget = BotBody?.IsAutonomousWorldBot == true && !BotBody.IsPlayerLedGroup
                ? AutonomousPetSupport.ActiveOwnedPetCombatTarget(BotBody)
                : null;
            if (ownedPetTarget != null && CanDefendAgainst(ownedPetTarget))
            {
                AddToAggroList(ownedPetTarget, Math.Max(1, ownedPetTarget.EffectiveLevel));
                EnterOwnedPetCombatState(FSM);
            }

            GameLiving orderedPullTarget = ActiveOrderedPullTarget;
            // While a caster walks into spell range its pet waits with it,
            // unless the human leader (or the leader's pet) is already fighting
            // that target: then the pet joins the fight immediately.
            bool closingOnPullTarget = orderedPullTarget != null &&
                                       PlayerLedCasterEngagementRange(orderedPullTarget) is int range && range > 0 &&
                                       !Body.IsWithinRadius(orderedPullTarget, range) &&
                                       !LeaderIsFighting(orderedPullTarget);
            GameLiving petCombatTarget = UsesDefensiveOnlyPet || closingOnPullTarget
                ? null
                : ResolvePetCombatTarget(orderedPullTarget ?? Body?.TargetObject as GameLiving ?? ownedPetTarget);
            // A healing companion Druid has no target of her own, so the pet service saw "no fight" and called the pet
            // back from the target she had just sent it at. Her pet keeps the order she gave it while that target lives.
            if (petCombatTarget == null && !UsesDefensiveOnlyPet && !closingOnPullTarget &&
                CompanionFollowPolicy.SendsDruidPet(BotBody) &&
                Body.ControlledBrain is ControlledMobBrain { OrderedAttackTarget: { IsAlive: true } druidPetTarget } &&
                CanAggroTarget(druidPetTarget) && CompanionEngagementMode.Allows(Body, druidPetTarget))
                petCombatTarget = druidPetTarget;
            bool archerCombatOwnsPetUpkeep = BotBody?.CharacterClass != null &&
                BotRangedCombat.CombatOwnsArcherPetUpkeep(
                    (eCharacterClass)BotBody.CharacterClass.ID,
                    petCombatTarget?.IsAlive == true,
                    Body.InCombat,
                    Body.IsAttacking,
                    Body.attackComponent?.AttackState == true &&
                    Body.ActiveWeaponSlot == eActiveWeaponSlot.Distance);
            bool requiredPetUpkeep = requiredPrimaryPetUpkeep || requiredBonedancerArmyUpkeep;
            // A missing primary pet (or a missing Bonedancer subordinate) is
            // combat equipment, not an optional buff. Always allow that legal
            // summon attempt. The archer guard suppresses only routine upkeep
            // on an existing pet, so stale targets can never leave a Hunter
            // permanently petless while traveling.
            if ((!archerCombatOwnsPetUpkeep || requiredPetUpkeep) &&
                (BotAnimistPolicy.AppliesTo(BotBody) || requiredPetUpkeep ||
                 (!movementFirstPetClass && !deferCasterUpkeep)) &&
                AutonomousPetSupport.Maintain(Body, petCombatTarget, ref _nextDeployablePetTick, out _))
                return;

            // A controlled pet may reach the camp first while its persistent
            // owner loses a stale/expired aggro entry. Re-adopt only the live
            // target that this bot's own pet tree is already fighting. The
            // ordinary ranged-caster or bow policy below then closes to its
            // proper attack range; it never turns a caster into a melee bot.
            // Player-led persistent bots still publish bot-only group metadata
            // for the launcher. The human leader is never serialized there.
            if (BotBody?.IsAutonomousWorldBot == true && BotBody.IsPlayerLedGroup)
                AutonomousBotGroupCoordinator.Pulse(BotBody);

            // True 1.65 Mode: a hired companion speaks as a companion while it follows its player (rate-limited inside)
            if (BotBody?.IsPlayerLedGroup == true && CompanionChat165.IsCompanion(BotBody))
                CompanionChat165.TryAmbient(BotBody);

            MaybeAutonomousWorldChat();

            if (TryHandleAutonomousMerchantService())
                return;

            if (TryHandleAutonomousRealmExchange())
                return;

            // True 1.65 Mode: a hired companion never runs the world bots' goals; out of its player's group it rejoins them
            if (HiredCompanions165.KeepWithOwner(BotBody))
                return;

            if (BotBody?.IsAutonomousWorldBot == true && !BotBody.IsPlayerLedGroup)
            {
                _autonomousWorldController ??= new AutonomousWorldBotController();
                if (_autonomousWorldController.Tick(this))
                {
                    // A decision that just started a fight (a route or camp pull) thinks again at the
                    // combat cadence. The travel/planning interval chosen above (up to ~10 s for a
                    // standing bot far from players) outlasted the aggro state's 6 s first-contact
                    // window, so the pull expired unthrown and was re-issued forever: 1,479 such
                    // loops (186 bot-hours) in run 9, 2026-10-06.
                    if (mode != eAutonomousThinkMode.Combat && HasAggro)
                        ThinkInterval = AutonomousFidelityPolicy.IntervalMilliseconds(eAutonomousThinkMode.Combat,
                            fidelity, AutonomousBotRegistry.PopulationForBrainTick);
                    return;
                }
            }

            FSM.Think();
        }

        private bool TryHoldForNecromancerServantSelfBuff()
        {
            GameBot owner = BotBody;
            if (owner?.ControlledBrain is not NecromancerPetBrain servantBrain ||
                servantBrain.Body?.IsAlive != true || !servantBrain.HasPendingHelpfulCommand ||   // its buffs and power gifts
                ActiveOrderedPullTarget?.IsAlive == true ||
                BotRestRecovery.BlocksRest(owner))
            {
                return false;
            }

            if (owner.IsMoving)
                owner.StopMoving();
            if (servantBrain.Body.IsMoving)
                servantBrain.Body.StopMoving();
            return true;
        }

        public static void EnterOwnedPetCombatState(FSM fsm)
        {
            // FSM.SetCurrentState always runs Exit, even for the same state.
            // Aggro.Exit stops the pending bow/cast attack and clears aggro.
            // Re-adopting our pet's opponent is not a new combat transition.
            // Keep this local to bot pet adoption; other FSM users retain their
            // intentional exit/re-entry semantics.
            if (fsm.GetCurrentState() != fsm.GetState(eFSMStateType.AGGRO))
                fsm.SetCurrentState(eFSMStateType.AGGRO);
        }

        private void ClearIdleOwnedPetTarget()
        {
            if (Body?.TargetObject == null || Body.InCombat || Body.IsAttacking || Body.IsCasting || HasAggro ||
                Body.ControlledBrain?.Body?.InCombat == true)
                return;

            if (Body.TargetObject == Body || IsOwnedPetTreeTarget(Body.ControlledBrain, Body.TargetObject, 0))
                Body.TargetObject = null;
        }

        private static bool IsOwnedPetTreeTarget(IControlledBrain brain, GameObject target, int depth)
        {
            if (brain?.Body == null || depth > 3)
                return false;
            if (brain.Body == target)
                return true;

            IControlledBrain[] children = brain.Body.ControlledNpcList;
            if (children == null)
                return false;
            foreach (IControlledBrain child in children)
                if (IsOwnedPetTreeTarget(child, target, depth + 1))
                    return true;
            return false;
        }

        /// <summary>
        /// Real clients wake APlayerVicinityBrain mobs through object-create
        /// packets. GameBots have no client, so they explicitly keep only nearby
        /// hostile brains awake. The mob still discovers and attacks the bot via
        /// StandardMobBrain.CheckNpcAggro using its database-authored range,
        /// con/faction checks, LoS, leash and combat FSM.
        /// </summary>
        private void WakeNearbyNaturalAggroBrains()
        {
            GameBot bot = BotBody;
            long now = GameLoop.GameLoopTime;
            if (bot == null || now < _nextNaturalMobWakeTick)
                return;

            _nextNaturalMobWakeTick = now + 1_250 + bot.ObjectID % 500;
            if (!bot.IsAlive || bot.ObjectState != GameObject.eObjectState.Active ||
                bot.IsOnStableMasterRoute || bot.IsStealthed ||
                bot.effectListComponent.ContainsEffectForEffectType(eEffect.Shade))
                return;

            foreach (GameNPC npc in bot.GetNPCsInRadius(StandardMobBrain.MAX_AGGRO_DISTANCE))
            {
                // A scripted encounter boss is woken by a raider nearby as by a player, aggressive or not: run 29 (2026-10-20),
                // Galladoria's Olcasgean slept as a statue with all four primals dead and 264 raiders at its staging, because
                // only its own (sleeping) brain starts the timer that wakes it.
                if (npc is IGameEpicNpc && npc.IsAlive && npc.ObjectState == GameObject.eObjectState.Active && npc != bot)
                {
                    npc.OnAutonomousBotNearby();
                    continue;
                }
                if (npc == bot || !npc.IsAlive || npc.ObjectState != GameObject.eObjectState.Active ||
                    npc is GameTaxi or GameTrainingDummy || npc.Brain is not StandardMobBrain brain ||
                    brain.AggroLevel <= 0 || brain.AggroRange <= 0 ||
                    !npc.IsWithinRadius(bot, brain.AggroRange) || !brain.CanAggroTarget(bot))
                    continue;

                npc.OnAutonomousBotNearby();
            }
        }

        private void MaybeAutonomousWorldChat()
        {
            GameBot bot = BotBody;
            long nowTick = GameLoop.GameLoopTime;
            if (bot?.IsAutonomousWorldBot != true || bot.IsTemporaryGroupHelper || bot.IsPlayerLedGroup ||
                nowTick < _nextAmbientChatCheckTick)
                return;
            _nextAmbientChatCheckTick = nowTick + 15_000 + Random.Shared.Next(20_001) + bot.ObjectID % 4_000;

            DateTime now = DateTime.UtcNow;
            if (!AutonomousBotChat.ShouldSpeak(now, _lastIdleRoleplayUtc,
                    bot.InCombat || bot.IsAttacking || bot.IsCasting || bot.IsOnStableMasterRoute))
                return;

            OfflineWorldBotRecord record = bot.PersistentRecord;
            string item = bot.Inventory?.AllItems
                .FirstOrDefault(entry => entry != null && entry.SlotPosition >= (int)eInventorySlot.FirstBackpack &&
                                         entry.SlotPosition <= (int)eInventorySlot.LastBackpack)?.Name ?? string.Empty;
            var context = new AutonomousBotChat.Context(
                bot.Name, bot.ClassName, bot.CurrentZone?.Description,
                record?.TargetName, record?.TravelDestination, item, string.Empty,
                bot.Level, Math.Max(1, (int)(bot.Group?.MemberCount ?? 1)), bot.Realm);
            if (AutonomousBotChatCoordinator.TryStartAmbient(bot, context))
                _lastIdleRoleplayUtc = now;
        }

        private string _lastBetweenTaskPurchaseAssignment = string.Empty; // Ordinary vendor purchase throttle only.

        private long _nextArrivedServiceWarning;
        private int _fruitlessExchangeChecks;   // owner 2026-11-10: real exchange checks in a row that did nothing
        internal bool TryUseArrivedAutonomousService(GameNPC service)
        {
            if (service?.ObjectState != GameObject.eObjectState.Active || BotBody == null ||
                !BotBody.IsWithinRadius(service, GS.ServerProperties.Properties.WORLD_PICKUP_DISTANCE)) return false;
            // The world controller already selected a reachable merchant and stopped
            // beside it. Use that exact merchant on this turn; another AI action can
            // otherwise postpone the ordinary merchant pulse indefinitely.
            if (service is GameMerchant merchant && service is not RealmExchangeBroker)
                return TryHandleAutonomousMerchantService(merchant);
            // True 1.65 Mode: buy back lost Constitution and cure resurrection sickness (DeathPenalty165).
            if (service is GameHealer healer)
            {
                bool healed = DeathPenalty165.Visit(BotBody, healer);
                AutonomousBotEconomy.MarkEconomyChanged(BotBody.DatabaseID);
                AutonomousObjectiveAssignments.TryCompleteBetweenTaskServicesIfSatisfied(BotBody);
                return healed;
            }
            if (service is not RealmExchangeBroker broker) return false;
            bool handled = TryHandleAutonomousRealmExchange(broker);
            if (handled)
            {
                _nextArrivedServiceWarning = GameLoop.GameLoopTime + 60_000;
                return true;
            }
            if (_nextArrivedServiceWarning == 0)
                _nextArrivedServiceWarning = GameLoop.GameLoopTime + 60_000;
            if (GameLoop.GameLoopTime >= _nextArrivedServiceWarning)
            {
                _nextArrivedServiceWarning = GameLoop.GameLoopTime + 60_000;
                log.Warn($"AUTONOMOUS_SERVICE_BLOCKED bot=\"{BotBody.Name}\" id={BotBody.DatabaseID} service=\"{broker.Name}\" " +
                    $"alive={BotBody.IsAlive} grouped={BotBody.Group != null} combat={BotBody.InCombat} " +
                    $"attacking={BotBody.IsAttacking} casting={BotBody.IsCasting} moving={BotBody.IsMoving} " +
                    $"exchangeRegion={Classic165Mode.IsExchangeRegion(BotBody.CurrentRegion)} inventoryPhase={AutonomousObjectiveAssignments.WantsBetweenTaskInventory(BotBody)} " +
                    $"expired={AutonomousObjectiveAssignments.BetweenTaskServiceExpired(BotBody)} cooldown={GameLoop.GameLoopTime < _nextExchangeCheckTick}");
            }
            return false;
        }

        private bool TryHandleAutonomousRealmExchange(RealmExchangeBroker arrivedBroker = null)
        {
            GameBot bot = BotBody;
            long now = GameLoop.GameLoopTime;
            if (bot?.IsAutonomousWorldBot != true || bot.IsTemporaryGroupHelper || bot.IsPlayerLedGroup ||
                bot.Group != null || !bot.IsAlive || bot.InCombat || bot.IsAttacking || bot.IsCasting ||
                // True 1.65 Mode keeps its brokers at the traveler's inns in the home regions, not in the capitals
                // (owner 2026-10-31 run: 212 bots stood 15 minutes beside Brynhild, Adalyn and Eilwen, never trading).
                bot.IsMoving || !Classic165Mode.IsExchangeRegion(bot.CurrentRegion) ||
                !AutonomousObjectiveAssignments.WantsBetweenTaskInventory(bot) ||
                AutonomousObjectiveAssignments.BetweenTaskServiceExpired(bot))
            {
                _pendingExchangeItem = null;
                _pendingExchangePlan = null;
                _pendingExchangePrice = 0;
                _pendingExchangeListTick = 0;
                _fruitlessExchangeChecks = 0;
                return false;
            }

            RealmExchangeBroker broker = arrivedBroker ?? bot.GetNPCsInRadius((ushort)Math.Clamp(GS.ServerProperties.Properties.WORLD_PICKUP_DISTANCE, 1, ushort.MaxValue))
                .OfType<RealmExchangeBroker>().FirstOrDefault(candidate => candidate.Realm == bot.Realm);
            if (broker == null || broker.Realm != bot.Realm ||
                !bot.IsWithinRadius(broker, GS.ServerProperties.Properties.WORLD_PICKUP_DISTANCE))
            {
                _fruitlessExchangeChecks = 0;
                return false;
            }

            if (_pendingExchangeItem != null)
            {
                if (now < _pendingExchangeListTick)
                {
                    bot.StopMoving();
                    return true;
                }

                DbInventoryItem item = _pendingExchangeItem;
                AutonomousBotEconomy.ListingCandidate plan = _pendingExchangePlan;
                int price = _pendingExchangePrice;
                _pendingExchangeItem = null;
                _pendingExchangePlan = null;
                _pendingExchangePrice = 0;
                _pendingExchangeListTick = 0;
                bool busy = false;
                bool listed = plan != null
                    ? AutonomousBotEconomy.TryListPlanned(bot, plan, price, out busy)
                    : AutonomousBotEconomy.TryList(bot, item, price, out busy);
                if (busy)
                {
                    // the exchange was busy: the same plan is tried again at the next check, not counted as fruitless
                    _pendingExchangeItem = item;
                    _pendingExchangePlan = plan;
                    _pendingExchangePrice = price;
                    _pendingExchangeListTick = now + 3_000 + Random.Shared.Next(4_001);
                    return true;
                }
                SetExchangeStatus(bot, listed
                    ? $"Listed {item.Name} on the Realm Exchange"
                    : $"Kept {item.Name} after its listing failed",
                    listed ? $"Real item listed for {price} copper" : "The real inventory item was not removed");
                _nextExchangeCheckTick = now + 3_000 + Random.Shared.Next(4_001);
                if (listed)
                {
                    _fruitlessExchangeChecks = 0;
                    AutonomousObjectiveAssignments.TryCompleteBetweenTaskServicesIfSatisfied(bot);
                }
                else
                    NoteFruitlessExchangeCheck(bot, now);
                return listed;
            }

            if (now < _nextExchangeCheckTick)
                return false;
            _nextExchangeCheckTick = now + 3_000 + Random.Shared.Next(4_001);

            // Reserve and post the single best item before shopping, so a full
            // 40-slot backpack—not a partially vendored remainder—determines
            // the listing. The world controller routes to a vendor after this
            // slot is occupied and returns here for upgrades only after clearing.
            // 2026-11-11 exchange plan: the plan is a free slot or a swap of the bot's weakest listing; its ask undercuts
            // the copies already listed, with a small mood roll on top (never above the plan's own ask band).
            AutonomousBotEconomy.ListingCandidate candidate = AutonomousBotEconomy.FindValuableListingCandidate(bot);
            if (candidate != null)
            {
                int listingPrice = AutonomousBotEconomy.RollPlannedPrice(candidate);

                bool advertised = Random.Shared.NextDouble() < 0.70 &&
                                  AutonomousBotChatCoordinator.TryAdvertiseExchangeItem(bot, candidate.Item.Name);
                if (!advertised)
                {
                    bool listed = AutonomousBotEconomy.TryListPlanned(bot, candidate, listingPrice, out bool busy);
                    if (busy)
                        return true;   // tried again at the next check; a busy exchange is not a fruitless check
                    if (listed)
                    {
                        _fruitlessExchangeChecks = 0;
                        SetExchangeStatus(bot, $"Listed {candidate.Item.Name} on the Realm Exchange",
                            $"Real item listed for {listingPrice} copper");
                        AutonomousObjectiveAssignments.TryCompleteBetweenTaskServicesIfSatisfied(bot);
                    }
                    else
                        NoteFruitlessExchangeCheck(bot, now);
                    return listed;
                }

                _pendingExchangeItem = candidate.Item;
                _pendingExchangePlan = candidate;
                _pendingExchangePrice = listingPrice;
                _pendingExchangeListTick = now + 7_000 + Random.Shared.Next(4_001);
                // Owner 2026-11-10 (review): no reset here; only a real listing or purchase resets the count, so a
                // listing that keeps failing still ends the step (GivesUpOnExchange waits while one is pending).
                SetExchangeStatus(bot, $"Asking the realm about {candidate.Item.Name}",
                    $"Will list the real item for {listingPrice} copper after the chatter");
                bot.StopMoving();
                return true;
            }

            AutonomousBotEconomy.PurchaseCandidate purchase = AutonomousBotEconomy.IsBackpackFull(bot)
                ? null : AutonomousBotEconomy.FindBestUsefulExchangePurchase(bot);
            if (purchase != null)
            {
                int paidCopper = purchase.Item.SellPrice;
                bool bought = AutonomousBotEconomy.TryBuy(bot, purchase.Item, out bool busy);
                if (busy)
                    return true;   // tried again at the next check; a busy exchange is not a fruitless check
                if (bought)
                {
                    bool equipped = AutonomousBotEconomy.TryEquipPurchasedUpgrade(bot, purchase);
                    SetExchangeStatus(bot,
                        equipped ? $"Bought and equipped {purchase.Item.Name}" : $"Bought {purchase.Item.Name} from the Realm Exchange",
                        $"Paid {paidCopper} copper from real saved coin");
                    AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Money);
                    _fruitlessExchangeChecks = 0;
                    AutonomousObjectiveAssignments.TryCompleteBetweenTaskServicesIfSatisfied(bot);
                    _nextExchangeCheckTick = now + 3_000 + Random.Shared.Next(4_001);
                    return true;
                }
            }

            // Another buyer can remove the prospective upgrade while this bot
            // travels here. Do not keep routing to a stale cached service for
            // another five minutes when the actual broker has nothing to do.
            AutonomousBotEconomy.MarkEconomyChanged(bot.DatabaseID);
            NoteFruitlessExchangeCheck(bot, now);
            return false;
        }

        // Owner 2026-11-10: a broker that has nothing to list or sell to this bot several real checks in a row ends the
        // exchange step (48 h log: 1,701 AUTONOMOUS_SERVICE_BLOCKED at Adalyn, bots idle an hour beside it). The economy
        // then plans without the exchange for a while (a merchant sale, or nothing), and the errands close when done.
        private void NoteFruitlessExchangeCheck(GameBot bot, long now)
        {
            // Owner 2026-11-10 (review): already given up in this window - no new give-up every 3 checks
            if (AutonomousServiceFallback.IsExchangeUnreachable(bot.DatabaseID, now))
            {
                _fruitlessExchangeChecks = 0;
                return;
            }
            if (!AutonomousServiceFallback.GivesUpOnExchange(++_fruitlessExchangeChecks, _pendingExchangeItem != null))
                return;
            _fruitlessExchangeChecks = 0;
            AutonomousServiceFallback.MarkExchangeUnreachable(bot.DatabaseID, now);
            AutonomousBotEconomy.MarkEconomyChanged(bot.DatabaseID);
            log.Info($"AUTONOMOUS_EXCHANGE_GAVE_UP bot=\"{bot.Name}\" id={bot.DatabaseID} level={bot.Level} " +
                     $"checks={AutonomousServiceFallback.FruitlessExchangeChecksBeforeGivingUp} " +
                     $"next={AutonomousBotEconomy.GetNeededService(bot)?.ToString() ?? "none"}");
            AutonomousObjectiveAssignments.TryCompleteBetweenTaskServicesIfSatisfied(bot);
        }

        /// <summary>
        /// Executes only after an autonomous persistent bot has physically reached an ordinary
        /// merchant.  Routing remains the world-controller's responsibility; this method has no
        /// movement side effects, cannot affect /spawn helpers, and uses only saved bot inventory
        /// and copper through AutonomousBotEconomy.
        /// </summary>
        /// <summary>Game time of the bot's last merchant transaction (a sale, purchase, repair...): a visit still at work.</summary>
        internal long LastMerchantActionTick { get; private set; }

        /// <summary>Why the merchant service would not run for this bot right now, or null (run diagnostics).</summary>
        internal string MerchantServiceBlock(GameMerchant merchant)
        {
            GameBot bot = BotBody;
            if (bot == null) return "no body";
            if (bot.Group != null) return "grouped";
            if (!bot.IsAlive) return "dead";
            if (bot.InCombat) return "in combat";
            if (bot.IsAttacking) return "attacking";
            if (bot.IsCasting) return "casting";
            if (bot.IsMoving) return "moving";
            if (GameLoop.GameLoopTime < _nextMerchantServiceTick && (merchant == null || merchant == _nextMerchantServiceFor))
                return $"waiting {(_nextMerchantServiceTick - GameLoop.GameLoopTime) / 1000}s";
            if (merchant != null && !merchant.IsWithinRadius(bot, GS.ServerProperties.Properties.WORLD_PICKUP_DISTANCE)) return "out of reach";
            return null;
        }

        private bool TryHandleAutonomousMerchantService(GameMerchant arrivedMerchant = null)
        {
            bool served = TryHandleAutonomousMerchantServiceCore(arrivedMerchant);
            if (served)
                LastMerchantActionTick = GameLoop.GameLoopTime;
            return served;
        }

        private bool TryHandleAutonomousMerchantServiceCore(GameMerchant arrivedMerchant = null)
        {
            GameBot bot = BotBody;
            long now = GameLoop.GameLoopTime;
            if (bot?.IsAutonomousWorldBot != true || bot.IsTemporaryGroupHelper || bot.IsPlayerLedGroup ||
                bot.Group != null || !bot.IsAlive || bot.InCombat || bot.IsAttacking || bot.IsCasting || bot.IsMoving ||
                now < _nextMerchantServiceTick && (arrivedMerchant == null || arrivedMerchant == _nextMerchantServiceFor))
                return false;
            // A shopping trip can start in the middle of a task, not only in the between-task errands (2026-11-01 run:
            // 188 bots reached the merchant on their plan, were never served and stood there until the 15-minute
            // watchdog): the merchant the plan names always serves the bot.
            bool shoppingHere = arrivedMerchant != null &&
                                (AutonomousWorldBotController.ShoppingPlanFor(bot)?.Merchant == arrivedMerchant ||
                                 AutonomousBotEconomy.GetNeededService(bot) == eWorldServiceKind.Shop);   // any merchant a shopping trip reaches
            if (!shoppingHere && (!AutonomousObjectiveAssignments.WantsBetweenTaskInventory(bot) ||
                                  AutonomousObjectiveAssignments.BetweenTaskServiceExpired(bot)))
                return false;

            TownErrands165.Visited(bot);     // True 1.65 Mode: one errand trip per 30 minutes (TownErrands165)
            ItemWear165.TryBotRepair(bot);   // True 1.65 Mode: worn gear is repaired at the town's smith, paid
            Supplies165.EnsurePullWeapon(bot, arrivedMerchant); // True 1.65 Mode: a class that can pull at range buys a ranged weapon
            Supplies165.Restock(bot, arrivedMerchant);  // True 1.65 Mode: arrows, bolts or throwing weapons bought when running low
            CrafterBots165.Work(bot);        // True 1.65 Mode: crafter bots craft (siegecrafters build siege kits)
            Supplies165.RestockPoisons(bot); // True 1.65 Mode: poison vials for assassins
            AutonomousBotDyes.TryDye(bot, arrivedMerchant); // now and then a dye for worn gear, by wealth (owner 2026-11-10)
            ushort interactionRadius = (ushort)Math.Clamp(
                GS.ServerProperties.Properties.WORLD_PICKUP_DISTANCE, 1, ushort.MaxValue);
            // Capital service areas contain several merchants inside the old
            // 900-unit lookup ring. FirstOrDefault could therefore select a
            // different, out-of-range merchant while the world controller had
            // correctly parked the bot beside its chosen vendor. The sale then
            // failed forever and the watchdog repeatedly recovered the bot.
            // Consider only merchants the bot can actually interact with and
            // prefer the nearest one; the normal sale/range checks remain the
            // final authority.
            GameMerchant merchant = arrivedMerchant;
            if (merchant == null)
                merchant = bot.GetNPCsInRadius(interactionRadius)
                    .OfType<GameMerchant>()
                    .Where(candidate => candidate.IsWithinRadius(bot, interactionRadius) &&
                                        (candidate.Realm == eRealm.None || candidate.Realm == bot.Realm))
                    .OrderBy(candidate => bot.GetDistanceTo(candidate))
                    .FirstOrDefault();
            if (merchant?.ObjectState != GameObject.eObjectState.Active ||
                !merchant.IsWithinRadius(bot, interactionRadius) ||
                merchant.Realm != eRealm.None && merchant.Realm != bot.Realm)
                return false;

            // Darkness Falls seals buy seal gear here, paid in the bot's own seals (owner 2026-10-30).
            if (merchant is GameDarknessFallsSealsMerchant sealMerchant &&
                AutonomousBotEconomy.TryBuySealItem(bot, sealMerchant, out DbInventoryItem sealGear, out int sealsPaid, out bool sealUpgrade))
            {
                SetMerchantStatus(bot, $"Bought {sealGear.Name} from {merchant.Name}", sealUpgrade
                    ? $"Paid {sealsPaid} looted seals for a seal gear upgrade"
                    : $"Paid {sealsPaid} looted seals for an item to sell; no seal upgrade left to save for");
                AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Inventory);
                AutonomousObjectiveAssignments.TryCompleteBetweenTaskServicesIfSatisfied(bot);
                _nextMerchantServiceFor = merchant; _nextMerchantServiceTick = now + 3_000;
                return true;
            }

            if (bot.Inventory?.AllItems.Any(i=>BotSiegeRuntime.IsSupply(i.Id_nb))==true)
            {
                int repairKits=BotSiegeRuntime.TryRestockRepairKits(bot,merchant);
                if (repairKits>0)
                {
                    SetMerchantStatus(bot,$"Restocked {repairKits} siege repair kits", "Paid from saved coins; topped up to five at the current merchant");
                    AutonomousObjectiveAssignments.TryCompleteBetweenTaskServicesIfSatisfied(bot);
                    _nextMerchantServiceFor = merchant; _nextMerchantServiceTick = now+30_000;
                    return true;
                }
            }

            eWorldServiceKind? service = AutonomousBotEconomy.GetNeededService(bot);
            int usedSlots = bot.Inventory?.CountSlots(true, eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack) ?? 0;
            int capacity = (int)eInventorySlot.LastBackpack - (int)eInventorySlot.FirstBackpack + 1;
            if (service == eWorldServiceKind.Vendor || usedSlots >= capacity)
            {
                DbInventoryItem trash = AutonomousBotEconomy.FindVendorTrashCandidate(bot);
                if (trash != null && AutonomousBotEconomy.TrySellToVendor(bot, merchant, trash, out long earned))
                {
                    SetMerchantStatus(bot, $"Sold {trash.Name} to {merchant.Name}", $"Received {earned} saved copper from a real vendor");
                    AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Money);
                    AutonomousObjectiveAssignments.TryCompleteBetweenTaskServicesIfSatisfied(bot);
                    _nextMerchantServiceFor = merchant; _nextMerchantServiceTick = now + 3_000;
                    return true;
                }
            }

            // Legacy full bags can contain a still-useful equipment upgrade.
            // Wear it first, without discarding or vending it; the displaced
            // piece becomes an ordinary backpack candidate on the next pulse.
            if (usedSlots >= capacity &&
                AutonomousBotEconomy.TryEquipBestOwnedUpgrade(bot, out DbInventoryItem equippedUpgrade))
            {
                SetMerchantStatus(bot, $"Equipped {equippedUpgrade.Name} before clearing the backpack",
                    "The replaced equipment remains in the real inventory for appraisal");
                AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Inventory);
                AutonomousObjectiveAssignments.TryCompleteBetweenTaskServicesIfSatisfied(bot);
                _nextMerchantServiceFor = merchant; _nextMerchantServiceTick = now + 3_000;
                return true;
            }

            // An old character may carry 40 non-vendorable quest/reputation
            // objects indefinitely. Clear expendable backpack items only
            // after it reaches a merchant and no ordinary sale exists.
            // Equipped and swappable gear, instruments and money stay intact.
            if (usedSlots >= capacity &&
                AutonomousBotEconomy.TryClearBlockedBackpackAtMerchant(bot, true, out int discarded))
            {
                SetMerchantStatus(bot, $"Cleared {discarded} blocked backpack items at {merchant.Name}",
                    "No backpack item could be sold; combat gear, instruments and saved copper were kept");
                AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Inventory);
                AutonomousObjectiveAssignments.TryCompleteBetweenTaskServicesIfSatisfied(bot);
                _nextMerchantServiceFor = merchant; _nextMerchantServiceTick = now + 30_000;
                return true;
            }

            // A planned shopping trip (eWorldServiceKind.Shop) may buy again; an ordinary visit buys once per task. The trip
            // counts by its merchant too: the needed service can change on the way (run 12, 2026-10-31: bots that had
            // bought at one vendor walked to a shield merchant on their plan, weren't allowed to buy there and stood
            // "Ready to use Carly" until the 15-minute timeout).
            bool plannedHere = AutonomousWorldBotController.ShoppingPlanFor(bot)?.Merchant == merchant;
            if ((_lastBetweenTaskPurchaseAssignment != bot.PersistentRecord.ObjectiveAssignmentId || service == eWorldServiceKind.Shop ||
                 plannedHere) &&
                AutonomousBotEconomy.TryBuyUsefulVendorUpgrade(bot, merchant, out DbInventoryItem purchased, out long spent))
            {
                _lastBetweenTaskPurchaseAssignment = bot.PersistentRecord.ObjectiveAssignmentId;
                SetMerchantStatus(bot, $"Bought {purchased.Name} from {merchant.Name}", $"Spent {spent} saved copper on a legal equipment upgrade");
                AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Money);
                AutonomousObjectiveAssignments.TryCompleteBetweenTaskServicesIfSatisfied(bot);
                _nextMerchantServiceFor = merchant; _nextMerchantServiceTick = now + 30_000 + Random.Shared.Next(30_001);
                return true;
            }

            // A planned shopping trip that bought nothing here (sold out, price changed): don't plan this merchant again
            // for a while, or the bot would walk back and forth.
            if (service == eWorldServiceKind.Shop || plannedHere)
                AutonomousWorldBotController.SkipShoppingAt(bot, merchant);
            _nextMerchantServiceFor = merchant; _nextMerchantServiceTick = now + 20_000 + Random.Shared.Next(20_001);
            AutonomousBotEconomy.MarkEconomyChanged(bot.DatabaseID);
            return false;
        }

        private static void SetMerchantStatus(GameBot bot, string activity, string progress)
        {
            if (bot?.PersistentRecord == null)
                return;
            bot.PersistentRecord.Activity = activity;
            bot.PersistentRecord.CurrentGoal = "Use a real merchant with finite saved inventory and copper";
            bot.PersistentRecord.ObjectiveProgress = progress;
            bot.PersistentRecord.TargetName = string.Empty;
            bot.PersistentRecord.TravelDestination = bot.CurrentZone?.Description ?? string.Empty;
            bot.MarkAutonomousStateDirty();
        }

        private static void SetExchangeStatus(GameBot bot, string activity, string progress)
        {
            if (bot?.PersistentRecord == null)
                return;
            bot.PersistentRecord.Activity = activity;
            bot.PersistentRecord.CurrentGoal = "Use the Realm Exchange with real inventory and coin";
            bot.PersistentRecord.ObjectiveProgress = progress;
            bot.PersistentRecord.TargetName = string.Empty;
            bot.PersistentRecord.TravelDestination = bot.CurrentZone?.Description ?? string.Empty;
            bot.MarkAutonomousStateDirty();
            AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Objective);
        }

        private bool TryMaintainTravelAndClassBuffs()
        {
            GameBot bot = BotBody;
            if (!bot.InCombat && TryNecromancerPowerGift())
                return true;
            bool waitingForLeader = CompanionFollowPolicy.WaitingForLeaderToStop(bot);
            if (waitingForLeader && !CompanionFollowPolicy.CanStopForNativeTravelSpeed(bot))
                return false;
            if (bot == null || !bot.IsAlive || bot.IsOnStableMasterRoute || bot.InCombat || HasAggro ||
                bot.IsAttacking || bot.IsCasting || bot.IsStunned || bot.IsMezzed || bot.IsSilenced ||
                bot.castingComponent?.HasPendingSkillRequests == true ||
                GameLoop.GameLoopTime < _nextMaintenanceBuffTick ||
                HasResurrectionDuty())   // a corpse in range is raised before any re-buff (as songs and chants already do)
                return false;
            // Owner 2026-11-10: an autonomous party holds its upkeep while it walks - no stop for a cast the formation
            // refuses, no walk toward an out-of-range member (that turned the bot to the member and back every second)
            if (HoldsUpkeepForGroupTravel())
            {
                _nextMaintenanceBuffTick = GameLoop.GameLoopTime + 1_000;
                return false;
            }

            if (TryMaintainRunemasterBladeturn())
                return true;

            bool autonomousPetUpkeep = bot.CharacterClass != null &&
                AutonomousPetSupport.OwnsPetUpkeep((eCharacterClass)bot.CharacterClass.ID);
            List<Spell> known = (bot.MiscSpells ?? [])
                .Concat(bot.InstantMiscSpells ?? [])
                .Where(spell => spell != null && !spell.IsHarmful && spell.Level <= bot.Level &&
                                IsMaintainableClassBuff(spell) && !IsShortCombatShield(spell) &&
                                (!autonomousPetUpkeep ||
                                 spell.Target is not (eSpellTarget.PET or eSpellTarget.CONTROLLED)) &&
                                (spell.Target is not (eSpellTarget.PET or eSpellTarget.CONTROLLED) ||
                                 AutonomousPetSupport.ShouldMaintainRoutinePetBuff(spell, false)) &&
                                (!waitingForLeader || CompanionFollowPolicy.CanStopForNativeTravelSpeed(bot, spell)) &&
                                !BotSongTwistPolicy.IsReservedPulse(bot, spell) &&
                                !(ManagesRunemasterBladeturn && IsGroupBladeturnPulse(spell)) &&
                                !BaseBuffLeftToGroupmate(spell) &&
                                !BladeturnBlocked(spell))   // df-bosses L3
                .DistinctBy(spell => spell.ID)
                .ToList();
            if (known.Count == 0)
            {
                CompleteCompanionPerformerBuffBatch(bot);
                return false;
            }

            bool traveling = IsMaintenanceTraveling();

            // Native helpful pulses replace one another. Recasting every
            // missing child alternated caster speed and bladeturn indefinitely,
            // stopping Runemasters/Theurgists on each cast before route work.
            // Managed performer/tank chants were already excluded above.
            int maintenancePulse = BotMaintenancePulsePolicy.Choose(known, traveling);
            IEnumerable<Spell> candidates = known
                .Where(spell => BotMaintenancePulsePolicy.CanMaintain(spell, maintenancePulse))
                .Where(spell => spell.SpellType != eSpellType.SpeedEnhancement || traveling &&
                    (!ManagesRunemasterBladeturn || !GroupmateHasStrongerSpeed(spell)))
                .OrderByDescending(spell => spell.SpellType == eSpellType.SpeedEnhancement && traveling)
                // True 1.65: by buff type first (Value compared across types put endurance regeneration, value 1-5,
                // after everything else, when the concentration had run out)
                .ThenBy(BuffUpkeepPriority)
                .ThenByDescending(spell => spell.Value)
                .ThenByDescending(spell => spell.Level);

            GameLiving outOfRangeBuffTarget = null;
            bool waitingForPower = false;
            foreach (Spell spell in candidates)
            {
                if (spell.HasRecastDelay && bot.GetSkillDisabledDuration(spell) > 0)
                    continue;
                if (bot.Mana < bot.PowerCost(spell))
                {
                    // a buff someone is missing, only short of power (just after a resurrection): look again soon
                    waitingForPower |= !waitingForPower && FindMissingMaintenanceTarget(spell) != null;
                    continue;
                }
                if (!bot.CanAffordConcentration(spell))
                    continue;
                if (spell.NeedInstrument && !TryEquipRealInstrument(bot, spell.InstrumentRequirement))
                    continue;

                GameLiving target = FindMissingMaintenanceTarget(spell);
                if (target == null)
                {
                    outOfRangeBuffTarget ??= FindOutOfRangeBuffTarget(spell);
                    continue;
                }
                if (BuffKeepsFailing(target, spell))
                    continue;
                // A pet buff just cast whose effect has not shown up yet keeps its
                // old bounded retry window; other missing buffs may go next.
                if (target == bot.ControlledBrain?.Body && spell.ID == _pendingMaintenancePetBuffId &&
                    GameLoop.GameLoopTime < _pendingMaintenancePetBuffUntil)
                    continue;

                bool companionPerformerBuff = IsStationaryTemporaryCompanionPerformer();
                if (companionPerformerBuff)
                    StopTwistedSong();
                GameObject previousTarget = bot.TargetObject;
                if (spell.CastTime > 0)
                {
                    bot.StopMovingOnPath();
                    bot.StopMoving();
                }
                bot.TargetObject = target;
                bool cast = CastCoordinatedBuff(spell, false);
                bot.TargetObject = previousTarget;
                if (cast)
                    NoteBuffAttempt(target, spell);
                if (cast && companionPerformerBuff)
                    _companionPerformerBuffBatch = true;
                bool isMainPetTarget = target == bot.ControlledBrain?.Body;
                // Pet effects can be applied asynchronously. The just-cast pet buff
                // is skipped for 15 seconds unless its effect appears (the
                // Cabalist/Enchanter pile symptom), while the next missing buff
                // follows right after the cast instead of waiting 15 seconds.
                if (cast && isMainPetTarget)
                {
                    _pendingMaintenancePetBuffId = spell.ID;
                    _pendingMaintenancePetBuffUntil = GameLoop.GameLoopTime + 15_000;
                }
                _nextMaintenanceBuffTick = GameLoop.GameLoopTime +
                    (cast ? (isMainPetTarget ? Math.Max(1_750, spell.CastTime + 500) : 1_750) : 5_000);

                // Persistent world bots must keep their route/goal scheduler
                // alive after a non-combat pet upkeep cast. The native cast
                // pipeline remains authoritative; returning false here merely
                // lets the controller run its normal movement decision for the
                // same pulse. Player-led and temporary companions retain the
                // ordinary action-priority behavior.
                if (cast && isMainPetTarget && bot.IsAutonomousWorldBot &&
                    !bot.IsPlayerLedGroup && !bot.InCombat && !HasAggro)
                    return false;

                return cast;
            }

            // Nothing castable from here, but a group member out of range still needs
            // a buff: walk toward them (not while following a moving leader).
            if (outOfRangeBuffTarget != null && !waitingForLeader && ApproachSupportTarget(outOfRangeBuffTarget))
            {
                _nextMaintenanceBuffTick = GameLoop.GameLoopTime + 1_000;
                return true;
            }

            // A complete scan found nothing missing. Avoid rescanning the whole
            // spellbook on every lightweight AI tick. A buff that only waits for power is looked at again in 2 s.
            _nextMaintenanceBuffTick = GameLoop.GameLoopTime + (waitingForPower ? 2_000 : 8_000);
            CompleteCompanionPerformerBuffBatch(bot);
            return false;
        }

        /// <summary>
        /// The order a True 1.65 buffer keeps its buffs up in, by type: the specialization strength/constitution, then
        /// dexterity/quickness, the haste, endurance regeneration, acuity, everything else (armor, resists, damage adds,
        /// procs, self and pet buffs), and the base (single stat and base armor) buffs last - period buffbots put the
        /// specialization buffs and endurance regeneration on the tanks first and the base buffs with what concentration
        /// was left. Within a type the stronger spell goes first, as before.
        /// </summary>
        public static int UpkeepPriority165(Spell spell) => spell?.SpellType switch
        {
            eSpellType.StrengthConstitutionBuff => 0,
            eSpellType.DexterityQuicknessBuff => 1,
            eSpellType.CombatSpeedBuff => 2,
            eSpellType.EnduranceRegenBuff => 3,
            eSpellType.AcuityBuff => 4,
            eSpellType.StrengthBuff or eSpellType.DexterityBuff or eSpellType.ConstitutionBuff or
                eSpellType.BaseArmorFactorBuff => 6,
            _ => 5,
        };

        private int _pendingMaintenancePetBuffId;
        private long _pendingMaintenancePetBuffUntil;
        private long _nextSelfBladeturnTick;

        /// <summary>The self "absorb the next hit" shields: Protecting Spirit, Barrier of Warding, Barrier of Negation and the like.</summary>
        public static bool IsSelfBladeturn(Spell spell) =>
            spell?.SpellType == eSpellType.Bladeturn && spell.Target == eSpellTarget.SELF &&
            !spell.IsPulsing && !spell.IsHarmful;

        /// <summary>Safe to stop for a cast: nothing on the aggro list, not swinging, not busy, not hit in the last 3 seconds.</summary>
        public static bool SelfBladeturnSafe(bool alive, bool hasAggro, bool attacking, bool busy,
            bool crowdControlled, long sinceAttackedMilliseconds) =>
            alive && !hasAggro && !attacking && !busy && !crowdControlled && sinceAttackedMilliseconds >= 3_000;

        /// <summary>
        /// A used-up self bladeturn is recast as soon as it is safe. It used to go
        /// through the general buff upkeep, which waits for the combat flag to
        /// clear, skips offensive casters while they rest, and puts the shield
        /// behind every other long buff, so it came back long after the fight.
        /// </summary>
        /// <param name="fight">owner 2026-11-11: a caster set to "Attack spells: None" also puts it back up in the fight
        /// (aggro on the group is no reason to wait; still never while hit in the last 3 s, swinging or busy).</param>
        private bool TryRestoreSelfBladeturn(bool fight = false)
        {
            GameBot bot = BotBody;
            if (bot == null || !bot.IsAlive || BladeturnFeedsBoss())   // df-bosses L3 (BotBrain.RaidBoss)
                return false;
            long now = GameLoop.GameLoopTime;
            // Hold the turn while our own shield cast finishes, so a route or
            // follow order cannot cancel it.
            if (bot.IsCasting && IsSelfBladeturn(bot.castingComponent?.SpellHandler?.Spell) && !HasAggro)
                return true;
            if (now < _nextSelfBladeturnTick || bot.IsOnStableMasterRoute ||
                CompanionFollowPolicy.WaitingForLeaderToStop(bot) ||
                !SelfBladeturnSafe(true, !fight && HasAggro, bot.IsAttacking,
                    bot.IsCasting || bot.castingComponent?.HasPendingSkillRequests == true,
                    bot.IsStunned || bot.IsMezzed || bot.IsSilenced,
                    now - bot.LastAttackedByEnemyTick))
                return false;

            Spell shield = (bot.MiscSpells ?? []).Concat(bot.InstantMiscSpells ?? [])
                .Where(spell => IsSelfBladeturn(spell) && spell.Level <= bot.Level)
                .OrderByDescending(spell => spell.Level)
                .FirstOrDefault();
            if (shield == null)
            {
                _nextSelfBladeturnTick = now + 10_000;
                return false;
            }
            if (LivingHasEffect(bot, shield) ||
                shield.HasRecastDelay && bot.GetSkillDisabledDuration(shield) > 0 ||
                bot.Mana < bot.PowerCost(shield))
            {
                _nextSelfBladeturnTick = now + 1_000;
                return false;
            }

            // Owner 2026-11-10: not while its autonomous party walks (the stop ended its formation follow)
            if (HoldsUpkeepForGroupTravel())
            {
                _nextSelfBladeturnTick = now + 1_000;
                return false;
            }

            // True 1.65: in its player's group the rest goes on after the shield (BotRestRecovery.KeepsRestThroughCast)
            if (bot.IsRecoveryResting && !BotRestRecovery.KeepsRestThroughCast(Classic165Mode.IsOn, bot.IsPlayerLedGroup,
                    bot.IsTemporaryGroupHelper, true, bot.InCombat || HasAggro))
                bot.WakeRecoveryRest();
            if (shield.CastTime > 0)
            {
                bot.StopMovingOnPath();
                bot.StopMoving();
            }
            GameObject previousTarget = bot.TargetObject;
            bot.TargetObject = bot;
            bool cast = CastCoordinatedBuff(shield, false);
            bot.TargetObject = previousTarget;
            _nextSelfBladeturnTick = now + (cast ? Math.Max(1_000, shield.CastTime + 500) : 5_000);
            return cast;
        }

        /// <summary>
        /// A group member (not a pet) within support reach that lacks this group or
        /// single-target buff and is beyond its range, closest first.
        /// </summary>
        private GameLiving FindOutOfRangeBuffTarget(Spell spell)
        {
            if (Body.Group == null || spell.IsPulsing ||
                spell.Target is not (eSpellTarget.REALM or eSpellTarget.GROUP))
                return null;
            int range = spell.Target == eSpellTarget.GROUP
                ? Math.Max(350, spell.Range)
                : spell.CalculateEffectiveRange(Body);
            return Body.Group.GetMembersInTheGroup()
                .Where(member => member != Body && member.IsAlive && InSupportReach(member) &&
                    !Body.IsWithinRadius(member, range) && !LivingHasEffect(member, spell))
                .OrderBy(member => Body.GetDistanceTo(member))
                .FirstOrDefault();
        }

        private bool IsStationaryTemporaryCompanionPerformer() =>
            IsTemporaryCompanionPerformer(BotBody) &&
            AssistedPlayer is { IsAlive: true, IsMoving: false } &&
            !BotBody.InCombat && !HasAggro && !BotBody.IsAttacking &&
            !CompanionFollowPolicy.WaitingForLeaderToStop(BotBody);

        private static bool IsTemporaryCompanionPerformer(GameBot bot) =>
            bot is { IsPlayerCompanion: true, IsPlayerLedGroup: true } &&
            IsClassicSongClass(bot);

        private void CompleteCompanionPerformerBuffBatch(GameBot bot)
        {
            if (!_companionPerformerBuffBatch || !IsTemporaryCompanionPerformer(bot))
                return;
            _companionPerformerBuffBatch = false;
            _nextSongTwistTick = 0;
        }

        private bool TryMaintainClassicSongTwist()
        {
            GameBot bot = BotBody;
            if (!IsClassicSongClass(bot) || !bot.IsAlive || HasResurrectionDuty())
                return false;

            bool temporaryCompanion = IsTemporaryCompanionPerformer(bot);
            bool ownerMoving = temporaryCompanion && AssistedPlayer?.IsMoving == true;
            if (_companionPerformerBuffBatch && temporaryCompanion)
            {
                if (ownerMoving || bot.InCombat || HasAggro || bot.IsAttacking)
                    CompleteCompanionPerformerBuffBatch(bot);
                else
                    return false;
            }

            if (bot.IsOnStableMasterRoute || bot.IsCasting ||
                bot.castingComponent.HasPendingSkillRequests ||
                bot.IsStunned || bot.IsMezzed || bot.IsSilenced ||
                GameLoop.GameLoopTime < _nextSongTwistTick)
                return false;

            bool groupedSupport = bot.Group?.MemberCount > 1;
            if (GameLoop.GameLoopTime >= _nextInstrumentKitCheck)
            {
                _nextInstrumentKitCheck = GameLoop.GameLoopTime + 60_000;
                if (BotStarterInstruments.Ensure(bot) && bot.IsAutonomousWorldBot)
                    AutonomousBotStatusPersistence.Queue(bot, true);
            }
            // Enhanced rest already verifies actual attacks, aggro, harmful
            // casts and pet/group combat. A lingering combat flag after the
            // fight must not stop the resting performer's harmless songs.
            bool immediateCombat = !bot.IsEnhancedResting && (bot.InCombat || HasAggro || bot.IsAttacking);

            // Bard and Skald preserve the last song's child buff, swap back to
            // their legal melee weapon, and fight in both solo and group play.
            // Autonomous Minstrels and grouped companions swap to melee;
            // only the legacy ungrouped companion keeps its ranged posture.
            // None of the three becomes a passive
            // support-only actor merely because a group exists.
            bool minstrelAtRange = !groupedSupport && !UsesMinstrelHybridCombat && (eCharacterClass)bot.CharacterClass.ID == eCharacterClass.Minstrel &&
                                    !IsUnderImmediateMeleePressure() && !PerformerMelees;
            // a performer in the Support role keeps its instrument and plays on in the fight (no melee weapon)
            if (immediateCombat && !minstrelAtRange && !PerformerStaysBack)
            {
                // The fight song stays on while it melees (owner 2026-11-05 class audit; Prima: "In battle, keep the Endurance
                // Song playing"): the endurance song, or the one the player chose for fights, is started once as the fight
                // begins and left playing; any other song stops. Until now every song stopped, so the chosen fight song
                // never played.
                // A Skald has no endurance song: its fight chant is the group damage add (Chant of the Brawl ... Chant of
                // Blood; Prima: open with the damage-add song and keep fighting under it). Before, it played nothing in a fight.
                eSpellType? fightSong = CompanionOptions165.Song(bot, 1) ?? DefaultFightSong(bot);
                ECSPulseEffect playing = Body.effectListComponent.GetPulseEffects().FirstOrDefault(effect => !effect.IsEnding && !effect.IsEnded &&
                    effect.SpellHandler?.Spell is Spell s && s.IsPulsing && !s.IsHarmful && IsMaintainableClassBuff(s));
                bool knowsFightSong = (bot.MiscSpells ?? []).Concat(bot.InstantMiscSpells ?? []).Any(s => s != null &&
                    s.Level <= bot.Level && s.IsPulsing && s.SpellType == fightSong);
                if (playing?.SpellHandler.Spell.SpellType == fightSong || !knowsFightSong)
                {
                    Spell currentSong = playing?.SpellHandler.Spell;
                    if (currentSong != null && currentSong.SpellType != fightSong && !PreserveCombatSong(currentSong))
                        StopTwistedSong();
                    if (!KeepsCombatSongInstrument(currentSong))
                        SwitchToUsableMeleeWeapon();
                    _nextSongTwistTick = GameLoop.GameLoopTime + 1_000;
                    return false;
                }
            }

            // True 1.65: a Minstrel holding a pet twists its charm song with ONE other song, as a player does (owner
            // 2026-11-03): see TryTwistWithCharm. Between the partner song and the renewed charm, TryRenewTwistedCharm
            // owns the turn; a charm song still pulsing with no pet (waiting to recapture) is left alone.
            long charmHoldLeft = 0;
            bool managingCharm = Classic165Mode.IsOn && Charm165.PulsingCharmPet(bot, out _, out charmHoldLeft) != null;
            if (Classic165Mode.IsOn && (managingCharm ? !Charm165.HoldsPulsingCharm(bot) : Charm165.HoldsPulsingCharm(bot)))
            {
                _nextSongTwistTick = GameLoop.GameLoopTime + (managingCharm ? 250 : 1_000);
                return false;
            }

            bool traveling = bot.IsMoving || bot.IsReturningAfterRelease ||
                             bot.Group?.LivingLeader?.IsMoving == true ||
                             bot.PersistentRecord?.Activity?.Contains("travel", StringComparison.OrdinalIgnoreCase) == true ||
                             bot.PersistentRecord?.Activity?.Contains("walking", StringComparison.OrdinalIgnoreCase) == true;

            // Hold one song for the situation, like a player (owner 2026-10-30): no twisting a second song in and out.
            long now = GameLoop.GameLoopTime;
            bool moving = traveling || ownerMoving;
            if (moving)
            {
                if (_songMovingSince == 0) _songMovingSince = now;
                _songStoppedSince = 0;
            }
            else
            {
                _songMovingSince = 0;
                if (_songStoppedSince == 0) _songStoppedSince = now;
            }
            _songTraveling = moving
                ? ownerMoving || now - _songMovingSince >= BotSongTwistPolicy.TravelAfterMovingMilliseconds || _songTraveling
                : _songTraveling && now - _songStoppedSince < BotSongTwistPolicy.StayTravelingMilliseconds;

            Dictionary<eSpellType, Spell> songsByType = (bot.MiscSpells ?? [])
                .Concat(bot.InstantMiscSpells ?? [])
                .Where(spell => spell != null && spell.IsPulsing && !spell.IsHarmful &&
                                spell.Level <= bot.Level && IsMaintainableClassBuff(spell))
                .GroupBy(spell => spell.SpellType)
                .ToDictionary(group => group.Key, group => group
                    .OrderByDescending(spell => groupedSupport && spell.Target is eSpellTarget.GROUP or eSpellTarget.REALM)
                    .ThenByDescending(spell => spell.Level)
                    .ThenByDescending(spell => spell.Value).First());

            // The real pulse may have ended from interruption or lack of power.
            ECSPulseEffect activePulse = Body.effectListComponent.GetPulseEffects()
                .FirstOrDefault(effect => !effect.IsEnding && !effect.IsEnded &&
                    effect.SpellHandler?.Spell is Spell activeSpell && activeSpell.IsPulsing &&
                    !activeSpell.IsHarmful && IsMaintainableClassBuff(activeSpell));
            _activeTwistedSongId = activePulse?.SpellHandler.Spell.ID ?? 0;
            eSpellType? activeType = activePulse?.SpellHandler.Spell.SpellType;

            GameLiving[] audience = bot.Group?.GetMembersInTheGroup()
                .Where(member => member.IsAlive && member.CurrentRegionID == bot.CurrentRegionID &&
                                 bot.IsWithinRadius(member, 2000)).ToArray() ?? [bot];
            eSpellType? wanted = BotSongTwistPolicy.PerformerHold(_songTraveling, immediateCombat,
                audience.Any(member => Short(member, m => m.MaxMana > 0 && m.ManaPercent < 85)),
                audience.Any(member => Short(member, m => m.HealthPercent < 85)),
                audience.Any(member => Short(member, m => m.EndurancePercent < 85)),
                songsByType.Keys, activeType,
                audience.Any(member => member.effectListComponent.GetSpellEffects().Any(effect =>
                    effect.SpellHandler?.Spell?.SpellType == eSpellType.PowerRegenBuff && effect.SpellHandler.Caster != bot)));
            // Q23: in a fight, the song that fits the group (a Minstrel's power song for casters, a second Skald's speed or
            // regeneration) when known - the same song the fight-song start above looks for
            if (immediateCombat && DefaultFightSong(bot) is var groupFightSong && songsByType.ContainsKey(groupFightSong))
                wanted = groupFightSong;
            // the player's choice for this situation, for a hired performer (CompanionOptions165, owner 2026-11-05)
            if (CompanionOptions165.Song(bot, immediateCombat ? 1 : _songTraveling ? 3 : 2) is { } picked && songsByType.ContainsKey(picked))
                wanted = picked;
            Spell song = wanted is { } type ? songsByType[type] : null;
            if (managingCharm)
                return TryTwistWithCharm(bot, song, charmHoldLeft);
            // owner 2026-11-11, "Twist songs / chants" (a hired performer, default off): the held song is the anchor and
            // the other songs that fit the situation are twisted in as their buff runs low, as before 2026-10-30
            bool twisting = false;
            if (song != null && CompanionControls165.TwistsSongs(bot))
            {
                var characterClass = (eCharacterClass)bot.CharacterClass.ID;
                Spell anchor = song;
                List<Spell> cycle = songsByType.Values.Where(candidate => candidate.ID == anchor.ID ||
                        CompanionControls165.TwistPartner(characterClass, candidate.SpellType, anchor.SpellType, _songTraveling && !immediateCombat,
                            bot.Mana >= bot.PowerCost(candidate)))
                    .ToList();
                if (cycle.Count > 1)
                {
                    twisting = true;
                    int chosenId = BotSongTwistPolicy.Choose(anchor.ID, _activeTwistedSongId,
                        cycle.Select(candidate => new BotSongTwistPolicy.Song(candidate.ID, candidate.CastTime,
                            bot.GetSkillDisabledDuration(candidate), SongRemainingMilliseconds(candidate))).ToArray());
                    song = cycle.FirstOrDefault(candidate => candidate.ID == chosenId);
                    if (song == null)
                    {
                        _nextSongTwistTick = now + 750;
                        return false;
                    }
                }
            }
            if (song == null || song.ID == _activeTwistedSongId)
            {
                _nextSongTwistTick = now + (song == null ? 4_000 : 1_000);
                return false;
            }
            // a held song stays at least HoldMilliseconds; starting to travel may always switch to speed
            if (!immediateCombat && !twisting && activePulse != null && now < _songHoldUntil &&
                song.SpellType != eSpellType.SpeedEnhancement)
            {
                _nextSongTwistTick = Math.Min(_songHoldUntil, now + 1_000);
                return false;
            }
            if (bot.Mana < bot.PowerCost(song) || bot.GetSkillDisabledDuration(song) > 0)
            {
                _nextSongTwistTick = now + 1_000;
                return false;
            }

            if (song.NeedInstrument && !TryEquipRealInstrument(bot, song.InstrumentRequirement))
            {
                _nextSongTwistTick = GameLoop.GameLoopTime + 5_000;
                return false;
            }

            // Switching away from an instrument ends that song's source, but
            // not its lingering child buffs. Otherwise let the native effect
            // list replace the old source only when the new song actually takes;
            // an asynchronously rejected request must not silence the anchor.
            if (activePulse?.SpellHandler.Spell is Spell previousSong && previousSong.NeedInstrument &&
                bot.ActiveWeapon?.DPS_AF != previousSong.InstrumentRequirement)
                activePulse.End();

            GameObject previousTarget = bot.TargetObject;
            GameLiving target = FindMissingMaintenanceTarget(song) ?? bot;
            bot.TargetObject = target;
            if (CompanionControls165.QuietSwitch(twisting, activePulse != null))
                bot.SongSoundQuietUntil = now + Math.Max(0, song.CastTime) + CompanionControls165.TwistQuietMarginMilliseconds;
            bool cast = bot.CastSpell(song, m_mobSpellLine, false);
            bot.TargetObject = previousTarget;
            if (cast)
            {
                _activeTwistedSongId = song.ID;
                _songHoldUntil = GameLoop.GameLoopTime + BotSongTwistPolicy.HoldMilliseconds;
            }

            // Actual casts, child lifetimes and skill reuse own the timing, not
            // a fixed two-second toggle. No additional timer/service is created.
            _nextSongTwistTick = GameLoop.GameLoopTime + (cast ? 250 : 1_000);
            // Queuing a legal mobile song must not consume the movement turn.
            // Cast selectors still see the pending/active request and cannot
            // enqueue another spell; follow/route decisions may run alongside it.
            return cast && !immediateCombat && !BotSongTwistPolicy.IsMobileSong(bot, song);
        }

        /// <summary>Renew the partner song when this much of it is left beyond its own cast time (it lands just
        /// before the old one runs out: no constant switching, no gap).</summary>
        private const int CharmTwistRenewLeadMilliseconds = 500;
        /// <summary>The charm's hold on the pet must outlast the partner song's cast by this much before switching.</summary>
        private const int CharmTwistReturnMarginMilliseconds = 2_500;
        private long _nextCharmRenewTick;
        private long _nextCharmTwistAllowed;

        /// <summary>How long the bot's own effect of this song has left (0: none).</summary>
        private long OwnSongRemainingMilliseconds(Spell song) =>
            Body.effectListComponent.GetSpellEffects(EffectHelper.GetEffectFromSpell(song))
                .Where(effect => (effect.IsActive || effect.IsStarting) && !effect.IsEnding &&
                                 effect.SpellHandler?.Spell.SpellType == song.SpellType)
                .Select(effect => effect.Duration == 0 ? long.MaxValue : Math.Max(0, effect.ExpireTick - GameLoop.GameLoopTime))
                .DefaultIfEmpty(0).Max();

        /// <summary>
        /// True 1.65 Mode, a Minstrel holding a charmed pet (owner 2026-11-03): the charm song comes before every other
        /// song. Only while traveling is it twisted, with speed, on the move (no stopping): speed is played only when
        /// its effect is about to run out and only while the charm's hold outlasts the switch; TryRenewTwistedCharm
        /// then plays the charm again at once. Idle or fighting: the charm alone. Speed gets its drum from the bag.
        /// </summary>
        private bool TryTwistWithCharm(GameBot bot, Spell song, long charmHoldLeft)
        {
            long now = GameLoop.GameLoopTime;
            if (song?.SpellType != eSpellType.SpeedEnhancement)
            {
                _nextSongTwistTick = now + 1_000;
                return false;
            }
            int castTime = Math.Max(0, song.CastTime);
            // its own speed only: speed never lands on a member in combat, riding or hidden, and waiting on them made it
            // switch every think (review 2026-11-03); and never sooner than the song's own duration allows
            long left = OwnSongRemainingMilliseconds(song);
            long renewAt = castTime + CharmTwistRenewLeadMilliseconds;
            if (now < _nextCharmTwistAllowed)
            {
                _nextSongTwistTick = Math.Min(_nextCharmTwistAllowed, now + 1_000);
                return false;
            }
            if (left > renewAt)
            {
                _nextSongTwistTick = now + Math.Clamp(left - renewAt, 250, 1_000);
                return false;
            }
            if (charmHoldLeft < castTime + CharmTwistReturnMarginMilliseconds ||
                bot.Mana < bot.PowerCost(song) || bot.GetSkillDisabledDuration(song) > 0)
            {
                _nextSongTwistTick = now + 250;   // the next charm pulse renews the hold
                return false;
            }
            if (song.NeedInstrument && !TryEquipRealInstrument(bot, song.InstrumentRequirement))
            {
                _nextSongTwistTick = now + 5_000;
                return false;
            }
            GameObject previousTarget = bot.TargetObject;
            bot.TargetObject = FindMissingMaintenanceTarget(song) ?? bot;
            bot.CharmTwistSong = song;
            bool cast;
            try { cast = bot.CastSpell(song, m_mobSpellLine, false); }
            finally { bot.CharmTwistSong = null; bot.TargetObject = previousTarget; }
            if (cast)
                _nextCharmTwistAllowed = now + Math.Max(castTime + 1_000, song.Duration - renewAt);
            _nextSongTwistTick = now + (cast ? 250 : 1_000);
            return cast && !BotSongTwistPolicy.IsMobileSong(bot, song);
        }

        /// <summary>
        /// The other half of the Minstrel's charm twist: once the partner song has started (it ended the charm song),
        /// the charm is played again on the pet at once, before its hold runs out. In or out of a fight.
        /// </summary>
        private void TryRenewTwistedCharm()
        {
            GameBot bot = BotBody;
            if (!Classic165Mode.IsOn || bot == null || !bot.IsAlive || !IsClassicSongClass(bot) || Charm165.HoldsPulsingCharm(bot) ||
                bot.StowedForRide || bot.IsOnStableMasterRoute ||
                Charm165.PulsingCharmPet(bot, out Spell charm, out _) is not GameNPC pet || bot.Mana < bot.PowerCost(charm) ||
                bot.IsCasting || bot.castingComponent?.HasPendingSkillRequests == true ||
                bot.IsStunned || bot.IsMezzed || bot.IsSilenced || GameLoop.GameLoopTime < _nextCharmRenewTick)
                return;
            _nextCharmRenewTick = GameLoop.GameLoopTime + 400;
            if (charm.NeedInstrument && !TryEquipRealInstrument(bot, charm.InstrumentRequirement))
                return;
            GameObject previousTarget = bot.TargetObject;
            bot.TargetObject = pet;
            try { bot.CastSpell(charm, m_mobSpellLine, false); }
            finally { bot.TargetObject = previousTarget; }
        }

        // A member whose stats can't be read (not fully in the world yet) counts as short of nothing.
        private static bool Short(GameLiving member, Func<GameLiving, bool> test)
        {
            try { return test(member); }
            catch (Exception) { return false; }
        }

        private long SongRemainingMilliseconds(Spell song)
        {
            eEffect type = EffectHelper.GetEffectFromSpell(song);
            IEnumerable<GameLiving> members = song.Target is eSpellTarget.GROUP or eSpellTarget.REALM
                ? Body.Group?.GetMembersInTheGroup() ?? [Body] : [Body];
            return members.Where(member => member.IsAlive && Body.IsWithinRadius(member, Math.Max(350, song.Range)))
                .Select(member => member.effectListComponent.GetSpellEffects(type)
                    .Where(effect => (effect.IsActive || effect.IsStarting) && !effect.IsEnding &&
                        effect.SpellHandler?.Spell.SpellType == song.SpellType)
                    .Select(effect => effect.Duration == 0 ? long.MaxValue : Math.Max(0, effect.ExpireTick - GameLoop.GameLoopTime))
                    .DefaultIfEmpty(0).Max())
                .DefaultIfEmpty(0).Min();
        }

        /// <summary>
        /// Q30 (completeness review 2026-11-11): a tank's chants - the best rank of each chant type at its level - built
        /// once per spell list and level instead of Concat/Where/GroupBy/Select every 750 ms per Paladin and Warden.
        /// </summary>
        public sealed class TankChantTable
        {
            public readonly int Level;
            public readonly List<Spell> Misc, InstantMisc;
            public readonly int MiscCount, InstantMiscCount;
            public readonly Spell[] Chants;

            public TankChantTable(List<Spell> misc, List<Spell> instantMisc, int level, Func<Spell, bool> managed)
            {
                Level = level;
                Misc = misc;
                InstantMisc = instantMisc;
                MiscCount = misc?.Count ?? 0;
                InstantMiscCount = instantMisc?.Count ?? 0;
                Chants = BestRankPerType((misc ?? []).Concat(instantMisc ?? []), level, managed);
            }

            public bool Matches(List<Spell> misc, List<Spell> instantMisc, int level) =>
                level == Level && ReferenceEquals(misc, Misc) && ReferenceEquals(instantMisc, InstantMisc) &&
                (misc?.Count ?? 0) == MiscCount && (instantMisc?.Count ?? 0) == InstantMiscCount;
        }

        /// <summary>Pure: of the managed spells at or under the level, the highest rank of each spell type (first seen order).</summary>
        public static Spell[] BestRankPerType(IEnumerable<Spell> spells, int level, Func<Spell, bool> managed) =>
            spells.Where(spell => spell?.Level <= level && managed(spell))
                .GroupBy(spell => spell.SpellType)
                .Select(group => group.OrderByDescending(spell => spell.Level).First()).ToArray();

        private List<Spell> TankChants(GameBot bot)
        {
            TankChantTable table = bot.TankChants;
            if (table == null || !table.Matches(bot.MiscSpells, bot.InstantMiscSpells, bot.Level))
                bot.TankChants = table = new TankChantTable(bot.MiscSpells, bot.InstantMiscSpells, bot.Level,
                    spell => BotSongTwistPolicy.IsManagedSong(bot, spell));
            return new List<Spell>(table.Chants);   // the Warden drops its speed chant from its copy
        }

        private void TryMaintainTankChant()
        {
            GameBot bot = BotBody;
            if (bot?.CharacterClass == null ||
                (eCharacterClass)bot.CharacterClass.ID is not (eCharacterClass.Paladin or eCharacterClass.Warden) ||
                !bot.IsAlive || bot.IsOnStableMasterRoute || bot.IsCasting || HasResurrectionDuty() ||
                bot.castingComponent.HasPendingSkillRequests || bot.IsCrowdControlled || bot.IsSilenced ||
                GameLoop.GameLoopTime < _nextSongTwistTick)
                return;

            _nextSongTwistTick = GameLoop.GameLoopTime + 750;
            // A chant that drains power each pulse (Chant of Endurance...) is dropped while the bot rests and not
            // restarted then: it kept running while seated, so power never reached full and a Paladin sat forever
            // (owner 2026-11-05). No free power: the drain just stops, as a player stops it.
            if (bot.IsRecoveryResting)
            {
                foreach (ECSPulseEffect draining in bot.effectListComponent.GetPulseEffects()
                             .Where(effect => !effect.IsEnding && !effect.IsEnded && effect.SpellHandler?.Spell is { PulsePower: > 0 } s &&
                                              BotSongTwistPolicy.IsManagedSong(bot, s)).ToList())
                    draining.End();
                return;
            }
            List<Spell> chants = TankChants(bot);
            // df-bosses L3: no bladeturn chant while the fight's boss consumes bladeturn; a Warden holds its damage add
            if (BladeturnFeedsBoss())
                chants.RemoveAll(spell => spell.SpellType == eSpellType.Bladeturn);
            if (chants.Count == 0) return;
            Spell active = bot.effectListComponent.GetPulseEffects()
                .FirstOrDefault(effect => !effect.IsEnding && !effect.IsEnded &&
                    BotSongTwistPolicy.IsManagedSong(bot, effect.SpellHandler?.Spell))?.SpellHandler.Spell;
            Spell chosen;
            bool paladinTwisting = false;
            int paladinHeldId = 0;   // O1: the situational chant (the twist's anchor)
            if ((eCharacterClass)bot.CharacterClass.ID == eCharacterClass.Warden)
            {
                bool combat = bot.InCombat || HasAggro || bot.IsAttacking ||
                    bot.Group?.GetMembersInTheGroup().Any(member => member.IsAlive &&
                        member.CurrentRegionID == bot.CurrentRegionID &&
                        bot.IsWithinRadius(member, GROUP_DEFENSE_ASSIST_RADIUS) && member.InCombat) == true;
                bool traveling = bot.IsMoving || bot.IsReturningAfterRelease || bot.Group?.LivingLeader?.IsMoving == true;
                eSpellType anchor = BotSongTwistPolicy.WardenAnchor(traveling, combat,
                    bot.Group?.MemberCount > 1, chants.Any(spell => spell.SpellType == eSpellType.Bladeturn));
                // M7: a hired Warden's chant rows - in a fight (1), standing around (2), travelling (3)
                if (CompanionOptions165.Song(bot, combat ? 1 : traveling ? 3 : 2) is { } pickedChant &&
                    chants.Any(spell => spell.SpellType == pickedChant))
                    anchor = pickedChant;
                // A faster speed in the group (a performer's song) replaces the
                // Warden's travel chant; it keeps bladeturn or damage add instead.
                Spell speedChant = chants.FirstOrDefault(spell => spell.SpellType == eSpellType.SpeedEnhancement);
                if (anchor == eSpellType.SpeedEnhancement && speedChant != null && GroupmateHasStrongerSpeed(speedChant))
                {
                    chants.Remove(speedChant);
                    anchor = eSpellType.Bladeturn;
                }
                chosen = chants.FirstOrDefault(spell => spell.SpellType == anchor) ??
                    chants.FirstOrDefault(spell => spell.SpellType == eSpellType.Bladeturn) ??
                    chants.FirstOrDefault(spell => spell.SpellType == eSpellType.DamageAdd);
                // No PBT round trips: its native recast can exceed child lifetime.
                // Wait for the selected anchor; do not spam/toggle the active one.
            }
            else
            {
                // A Paladin holds one chant, no cycling, picked by situation (O1, owner 2026-11-11): the heal chant as
                // main tank, the damage add as off-tank or solo (the heal chant solo when hurt), endurance out of a
                // fight or when low and not tanking; a chant it started stays at least 10 s. Each recast replays the
                // chant's sound.
                bool fighting = bot.InCombat || HasAggro || bot.IsAttacking;
                bool groupedPaladin = bot.Group?.MemberCount > 1;
                GameLiving sheetMainTank = groupedPaladin ? BotGroupRoles.MainTank(bot) : null;   // M2's role sheet, if any
                BotClassAbilities165.PaladinRole role = BotClassAbilities165.PaladinRoleFor(groupedPaladin,
                    sheetMainTank == null ? null : sheetMainTank == bot,
                    groupedPaladin && sheetMainTank == null && BotMeleeStylePolicy.HasTankPartner(bot));
                bool tanking = role == BotClassAbilities165.PaladinRole.MainTank ||
                    bot.TargetObject is GameLiving foe && foe.TargetObject == bot;
                eSpellType held = BotClassAbilities165.PaladinChant(role, fighting, tanking, bot.HealthPercent, bot.EndurancePercent,
                    chants.Select(spell => spell.SpellType).ToHashSet());
                // the player's choice for a hired Paladin, in a fight (1) or not (2) (CompanionOptions165, owner 2026-11-05)
                if (CompanionOptions165.Song(bot, fighting ? 1 : 2) is { } chosenChant &&
                    chants.Any(spell => spell.SpellType == chosenChant))
                    held = chosenChant;
                else if (active != null && chants.Any(spell => spell.ID == active.ID) &&
                         BotClassAbilities165.PaladinKeepsChant(active.ID == _paladinChantId,
                             GameLoop.GameLoopTime - _paladinChantSince))
                    held = active.SpellType;
                chosen = chants.FirstOrDefault(spell => spell.SpellType == held);
                paladinHeldId = chosen?.ID ?? 0;
                // owner 2026-11-11, "Twist songs / chants" (a hired Paladin, default off): the held chant is the anchor and
                // the other chants are twisted in as their buff runs low, as the Paladin did before 2026-10-30
                if (chosen != null && CompanionControls165.TwistsSongs(bot))
                {
                    bool combatHealing = fighting && (bot.HealthPercent < 95 || bot.Group?.GetMembersInTheGroup().Any(member =>
                        member.IsAlive && member.HealthPercent < 95 && member.CurrentRegionID == bot.CurrentRegionID &&
                        bot.IsWithinRadius(member, GROUP_DEFENSE_ASSIST_RADIUS)) == true);
                    Spell anchor = chosen;
                    List<Spell> cycle = chants.Where(spell => spell.ID == anchor.ID ||
                            CompanionControls165.PaladinTwistPartner(spell.SpellType, anchor.SpellType, combatHealing,
                                bot.Mana >= bot.PowerCost(spell)))
                        .ToList();
                    if (cycle.Count > 1)
                    {
                        paladinTwisting = true;
                        int id = BotSongTwistPolicy.Choose(anchor.ID, active?.ID ?? 0,
                            cycle.Select(spell => new BotSongTwistPolicy.Song(spell.ID, spell.CastTime,
                                bot.GetSkillDisabledDuration(spell), SongRemainingMilliseconds(spell))).ToArray());
                        chosen = cycle.FirstOrDefault(spell => spell.ID == id);
                    }
                }
            }

            if (chosen == null || chosen.ID == active?.ID || bot.GetSkillDisabledDuration(chosen) > 0 ||
                bot.Mana < bot.PowerCost(chosen)) return;
            // A draining chant starts only with power for a while of it (5 pulses, or a fifth of the bot's power):
            // restarted near empty, the server ended it at the next pulse and the bot recast it every few seconds,
            // replaying its sound and animation (owner 2026-11-05).
            if (chosen.PulsePower > 0 && bot.Mana < bot.PowerCost(chosen) + Math.Max(chosen.PulsePower * 5, bot.MaxMana / 5))
                return;
            // A companion Warden stops for its 3 s speed cast only when it can keep up afterwards (the casters' rule:
            // the player moving, out of combat, within reach); otherwise it waits (GitHub #14).
            if (chosen.SpellType == eSpellType.SpeedEnhancement && CompanionFollowPolicy.DeferBuff(bot, chosen))
                return;
            GameObject oldTarget = bot.TargetObject;
            try
            {
                bot.TargetObject = bot;
                if (CompanionControls165.QuietSwitch(paladinTwisting, active != null))
                    bot.SongSoundQuietUntil = GameLoop.GameLoopTime + Math.Max(0, chosen.CastTime) +
                                              CompanionControls165.TwistQuietMarginMilliseconds;
                // O1's 10 s hold follows the held chant only; a chant twisted in around it does not restart the hold
                if (bot.CastSpell(chosen, m_mobSpellLine, false) && (!paladinTwisting || chosen.ID == paladinHeldId))
                {
                    _paladinChantId = chosen.ID;
                    _paladinChantSince = GameLoop.GameLoopTime;
                }
            }
            finally { bot.TargetObject = oldTarget; }
        }

        private int _paladinChantId;
        private long _paladinChantSince;

        private bool IsUnderImmediateMeleePressure() =>
            AggroList.Keys.Any(attacker => attacker?.IsAlive == true &&
                                           Body.IsWithinRadius(attacker, Body.MeleeAttackRange + 35));

        private bool PreserveCombatSong(Spell spell) =>
            Classic165Mode.IsOn && IsClassicSongClass(BotBody) && spell != null &&
            spell.SpellType != eSpellType.SpeedEnhancement && BotSongTwistPolicy.IsManagedSong(Body, spell);

        private bool KeepsCombatSongInstrument(Spell spell) =>
            spell?.NeedInstrument == true && PreserveCombatSong(spell) &&
            Body.ActiveWeapon is { Object_Type: (int)eObjectType.Instrument } instrument &&
            (instrument.DPS_AF == 4 || instrument.DPS_AF == spell.InstrumentRequirement);

        private bool HasActiveCombatSongInstrument() =>
            Classic165Mode.IsOn && IsClassicSongClass(BotBody) &&
            Body.effectListComponent.GetPulseEffects().Any(effect => !effect.IsEnding && !effect.IsEnded &&
                KeepsCombatSongInstrument(effect.SpellHandler?.Spell));

        private void StopTwistedSong(bool preserveCombatSongs = false)
        {
            if (Body == null)
                return;

            // End the maintained pulse, not the short-duration child buffs it
            // already applied. Scanning also adopts/corrects a song that was
            // started before this brain began tracking its rotation.
            _activeTwistedSongId = 0;
            foreach (ECSPulseEffect active in Body.effectListComponent.GetPulseEffects()
                         .Where(effect => effect?.SpellHandler?.Spell is Spell spell &&
                                          spell.IsPulsing && !spell.IsHarmful &&
                                          IsMaintainableClassBuff(spell))
                         .ToList())
            {
                if (preserveCombatSongs && !active.IsEnding && !active.IsEnded && PreserveCombatSong(active.SpellHandler.Spell))
                    _activeTwistedSongId = active.SpellHandler.Spell.ID;
                else
                    active.End();
            }
        }

        private static bool IsClassicSongClass(GameBot bot) => bot?.CharacterClass != null &&
            IsClassicSongClass((eCharacterClass)bot.CharacterClass.ID);

        public static bool IsClassicSongClass(eCharacterClass characterClass) => characterClass is
            eCharacterClass.Bard or eCharacterClass.Minstrel or eCharacterClass.Skald;

        private GameLiving FindMissingMaintenanceTarget(Spell spell)
        {
            if (spell.Target is eSpellTarget.PET or eSpellTarget.CONTROLLED)
            {
                GameLiving pet = Body.ControlledBrain?.Body;
                return pet?.IsAlive == true && Body.IsWithinRadius(pet, Math.Max(350, spell.Range)) && !LivingHasEffect(pet, spell)
                    ? pet
                    : null;
            }

            if (spell.Target == eSpellTarget.SELF)
                return LivingHasEffect(Body, spell) ? null : Body;

            if (spell.Target == eSpellTarget.GROUP)
            {
                // A moving native-speed companion only needs to start its own
                // pulse. A group member briefly missing the child effect is
                // not grounds to stop again and restart the three-second cast.
                if (CompanionFollowPolicy.IsNativeCompanionTravelSpeed(BotBody, spell))
                    return LivingHasEffect(Body, spell) ? null : Body;
                IEnumerable<GameLiving> members = Body.Group?.GetMembersInTheGroup() ?? [Body];
                return members.Any(member => member.IsAlive && Body.IsWithinRadius(member, Math.Max(350, spell.Range)) && !LivingHasEffect(member, spell)) ||
                       FindMissingPartyPetBuffTarget(spell) != null
                    ? Body
                    : null;
            }

            if (spell.Target == eSpellTarget.REALM)
            {
                if (!spell.IsPulsing)
                    return FindRandomMissingRealmBuffTarget(spell, 350);
                if (!LivingHasEffect(Body, spell))
                    return Body;
                return Body.Group?.GetMembersInTheGroup()
                    .FirstOrDefault(member => member != Body && member.IsAlive &&
                                              Body.IsWithinRadius(member, Math.Max(350, spell.Range)) &&
                                              !LivingHasEffect(member, spell)) ?? FindMissingPartyPetBuffTarget(spell);
            }

            return null;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, BotBuffReservations<GameLiving>> BuffClaims = new();

        /// <summary>The scope's buff claims; null when the group broke up mid-think (a null key throws, and the NPC service
        /// then removes the bot from the world; owner 2026-11-04).</summary>
        private static BotBuffReservations<GameLiving> BuffClaimsFor(GameBot bot) =>
            AutonomousRealmRaid.SupportScope(bot) is { } scope ? BuffClaims.GetValue(scope, _ => new()) : null;

        private GameLiving FindRandomMissingRealmBuffTarget(Spell spell, int minimumRange = 0)
        {
            Group group = Body.Group;
            var claims = group == null ? null : BuffClaimsFor(BotBody);
            int family = (int)EffectHelper.GetEffectFromSpell(spell);
            long now = GameLoop.GameLoopTime;
            int range = Math.Max(minimumRange, spell.CalculateEffectiveRange(Body));
            IEnumerable<GameLiving> members = spell.Target == eSpellTarget.REALM ? AutonomousRealmRaid.SupportMembers(BotBody) : group?.GetMembersInTheGroup() ?? [Body];
            // One pool: players, companions, world bots and valid attached pets.
            // Do not leave pets until every humanoid has been processed first.
            IEnumerable<GameLiving> pool = members.Concat(BotGroupPetBuffTargets.Enumerate(BotBody, spell));
            bool Eligible(GameLiving target) => target.IsAlive && Body.IsWithinRadius(target, range) &&
                PartyBuffHelps(target, spell) && !LivingHasEffect(target, spell) && !(claims?.IsReserved(target, family, now) ?? false);
            int emphasis = UsesVeteranBuffing ? CompanionOptions165.Choice(BotBody, "buffpriority") : 0;
            return Classic165Mode.IsOn
                ? BotBuffReservations<GameLiving>.ChooseBest(pool, Eligible, target =>
                    BotPetBuffBenefit.IsPetLike(target) ? BotPetBuffBenefit.PetRank :
                        UsesVeteranBuffing ? VeteranBuffTargetRank(ClassOf(target), spell.SpellType, emphasis) : BuffTargetRank(ClassOf(target)))
                : BotBuffReservations<GameLiving>.Choose(pool, Eligible);
        }

        private static ICharacterClass ClassOf(GameLiving living) =>
            living switch { GamePlayer p => p.CharacterClass, GameBot b => b.CharacterClass, _ => null };

        /// <summary>
        /// The order a realm buff goes out in True 1.65: tanks (0), melee and pets (1), healers (2), casters (3). Period
        /// groups: "casters get acuity/regen buffs, tanks get str/haste", the tank buffed first.
        /// </summary>
        public static int BuffTargetRank(ICharacterClass cls)
        {
            if (cls == null)
                return 1;   // a pet fights in melee
            var id = (eCharacterClass)cls.ID;
            return BotPartyRoles.For(id) switch
            {
                BotPartyRole.Tank => 0,
                BotPartyRole.Support => 2,
                _ => cls.ClassType == eClassType.ListCaster && id != eCharacterClass.Valewalker ? 3 : 1,
            };
        }

        /// <summary>
        /// Whether a stat buff does anything for this target's class, so a bot spends its concentration where a player
        /// would. Pure casters benefit from the constitution in a combined buff, but standalone strength is reserved
        /// for weapon users. Acuity follows the class's actual mana rules.
        /// Pets: everything but power/endurance regeneration, acuity and fatigue (GitHub #28, BotPetBuffBenefit).
        /// </summary>
        internal static bool BuffHelps(GameLiving target, Spell spell) => BotPetBuffBenefit.IsPetLike(target)
            ? BotPetBuffBenefit.HelpsCounted(spell)
            : BuffHelps(ClassOf(target), spell, Classic165Mode.IsOn);

        /// <summary>
        /// <see cref="BuffHelps(GameLiving, Spell)"/> by class. True 1.65 also: haste (the attack speed buff) skips pure
        /// casters, who do not swing, and the single-target power regeneration buff goes only to classes that use power.
        /// </summary>
        public static bool BuffHelps(ICharacterClass cls, Spell spell, bool classic165)
        {
            if (cls == null || spell == null)
                return true;
            return spell.SpellType switch
            {
                eSpellType.StrengthConstitutionBuff when classic165 => true,
                eSpellType.StrengthBuff when classic165 =>
                    cls.ClassType != eClassType.ListCaster || cls.ID == (int)eCharacterClass.Valewalker,
                eSpellType.StrengthBuff or eSpellType.StrengthConstitutionBuff => cls.ClassType != eClassType.ListCaster,
                // True 1.65: the server adds acuity only to a pure caster's mana stat (StatCalculator), so a Shaman's
                // acuity on a Healer or a hybrid was wasted concentration
                eSpellType.AcuityBuff => classic165 ? cls.ClassType == eClassType.ListCaster : cls.ManaStat is not eStat.UNDEFINED,
                eSpellType.CombatSpeedBuff when classic165 =>
                    cls.ClassType != eClassType.ListCaster || cls.ID == (int)eCharacterClass.Valewalker,
                eSpellType.PowerRegenBuff when classic165 => cls.ManaStat is not eStat.UNDEFINED,
                // Q14 (veteran review 2026-11-11): damage adds and weapon procs only help a class that fights in melee -
                // not a Runemaster, Spiritmaster or Healer
                eSpellType.DamageAdd or eSpellType.OffensiveProc => MeleeBuffHelps(cls),
                _ => true,
            };
        }

        /// <summary>Q14: a class whose weapon swings a damage add or proc helps - not a pure caster or a healer.</summary>
        public static bool MeleeBuffHelps(ICharacterClass cls) =>
            cls == null || MeleeBuffHelps(cls.ClassType, (eCharacterClass)cls.ID);

        /// <summary>Pure. A Valewalker is a list caster by class type but fights with its scythe (review 2026-11-11: the
        /// first version of Q14 took its damage adds away).</summary>
        public static bool MeleeBuffHelps(eClassType classType, eCharacterClass characterClass) =>
            (classType != eClassType.ListCaster || characterClass == eCharacterClass.Valewalker) &&
            BotPartyRoles.For(characterClass) != BotPartyRole.Support;

        private eSpellType _cachedFightSong;
        private long _cachedFightSongUntil;

        /// <summary>Q23: the fight song for this performer's group (BotSongTwistPolicy.DefaultFightSong), worked out at
        /// most every 2 s.</summary>
        private eSpellType DefaultFightSong(GameBot bot)
        {
            long now = GameLoop.GameLoopTime;
            if (now < _cachedFightSongUntil)
                return _cachedFightSong;
            _cachedFightSongUntil = now + 2_000;
            return _cachedFightSong = WorkOutFightSong(bot);
        }

        private eSpellType WorkOutFightSong(GameBot bot)
        {
            var known = new HashSet<eSpellType>();
            foreach (Spell s in (bot.MiscSpells ?? []).Concat(bot.InstantMiscSpells ?? []))
                if (s != null && s.Level <= bot.Level && s.IsPulsing)
                    known.Add(s.SpellType);
            var cls = (eCharacterClass)bot.CharacterClass.ID;
            int others = 0, powerUsers = 0;
            bool anotherSkald = false;
            if (bot.Group is Group group && cls is eCharacterClass.Minstrel or eCharacterClass.Skald)
            {
                foreach (GameLiving member in group.GetMembersInTheGroup())
                {
                    if (member == null || member == bot || !member.IsAlive || !bot.IsWithinRadius(member, 2000))
                        continue;
                    others++;
                    ICharacterClass memberClass = member switch { GameBot b => b.CharacterClass, GamePlayer p => p.CharacterClass, _ => null };
                    if (memberClass == null)
                        continue;
                    var memberId = (eCharacterClass)memberClass.ID;
                    if (memberClass.ClassType == eClassType.ListCaster || BotPartyRoles.For(memberId) == BotPartyRole.Support)
                        powerUsers++;
                    // the Skald with the lowest object id keeps the damage chant
                    if (cls == eCharacterClass.Skald && memberId == eCharacterClass.Skald && member.ObjectID < bot.ObjectID)
                        anotherSkald = true;
                }
            }
            return BotSongTwistPolicy.DefaultFightSong(cls, known, BotSongTwistPolicy.CasterHeavy(powerUsers, others),
                anotherSkald, bot.CurrentRegion?.IsRvR == true);
        }

        private bool CastCoordinatedBuff(Spell spell, bool? queued = null)
        {
            Group group = Body.Group;
            GameLiving target = Body.TargetObject as GameLiving;
            bool shared = group != null && target != null && spell.Target == eSpellTarget.REALM &&
                !spell.IsPulsing && IsMaintainableClassBuff(spell);
            BotBuffReservations<GameLiving> claims = shared ? BuffClaimsFor(BotBody) : null;
            shared = claims != null;
            int family = shared ? (int)EffectHelper.GetEffectFromSpell(spell) : 0;
            if (shared && (LivingHasEffect(target, spell) ||
                !claims.TryReserve(Body, target, family, GameLoop.GameLoopTime, spell.CastTime)))
                return false;
            bool cast = false;
            try
            {
                cast = queued.HasValue ? Body.CastSpell(spell, m_mobSpellLine, queued.Value) : Body.CastSpell(spell, m_mobSpellLine);
                return cast;
            }
            finally
            {
                if (shared && !cast) claims.Release(Body, target, family);
            }
        }

        private GameLiving FindMissingPartyPetBuffTarget(Spell spell)
        {
            // GitHub #28 (owner 2026-11-10): never a buff a pet gets nothing from
            if (!BotPetBuffBenefit.HelpsCounted(spell))
                return null;
            GameNPC pet = BotGroupPetBuffTargets.Enumerate(BotBody, spell)
                .FirstOrDefault(candidate => !LivingHasEffect(candidate, spell));
            // Native group spells fan out from the caster; realm buffs target one pet.
            return pet == null ? null : spell.Target == eSpellTarget.GROUP ? Body : pet;
        }

        internal static bool TryEquipRealInstrument(GameLiving bot, int requiredInstrument)
        {
            bool CorrectInstrument(DbInventoryItem item) =>
                item?.Object_Type == (int)eObjectType.Instrument &&
                (requiredInstrument <= 0 || item.DPS_AF == requiredInstrument);

            // The best instrument of the type it carries: a real one before a training one (owner 2026-11-05: given a
            // Melodic Lute, a Minstrel kept playing her training lute), then the higher level and quality.
            DbInventoryItem best = bot.Inventory?.AllItems.Where(item => item != null && CorrectInstrument(item) &&
                    (item.SlotPosition is (int)eInventorySlot.DistanceWeapon or (int)eInventorySlot.TwoHandWeapon ||
                     item.SlotPosition >= (int)eInventorySlot.FirstBackpack && item.SlotPosition <= (int)eInventorySlot.LastBackpack))
                .OrderBy(item => BotStarterInstruments.IsTraining(item) ? 1 : 0)
                .ThenByDescending(item => item.Level).ThenByDescending(item => item.Quality).FirstOrDefault();
            if (best != null && CorrectInstrument(bot.ActiveWeapon) &&
                (bot.ActiveWeapon == best || !BotStarterInstruments.IsTraining(bot.ActiveWeapon) || BotStarterInstruments.IsTraining(best)))
                return true;
            if (best != null && best.SlotPosition >= (int)eInventorySlot.FirstBackpack && best.SlotPosition <= (int)eInventorySlot.LastBackpack &&
                bot.Inventory.MoveItem((eInventorySlot)best.SlotPosition, eInventorySlot.TwoHandWeapon, best.Count))
            {
                bot.SwitchWeapon(eActiveWeaponSlot.TwoHanded);
                return CorrectInstrument(bot.ActiveWeapon);
            }
            if (best != null && best != bot.ActiveWeapon &&
                best.SlotPosition is (int)eInventorySlot.DistanceWeapon or (int)eInventorySlot.TwoHandWeapon)
            {
                bot.SwitchWeapon(best.SlotPosition == (int)eInventorySlot.DistanceWeapon ? eActiveWeaponSlot.Distance : eActiveWeaponSlot.TwoHanded);
                return CorrectInstrument(bot.ActiveWeapon);
            }

            if (CorrectInstrument(bot.ActiveWeapon))
                return true;

            DbInventoryItem distance = bot.Inventory?.GetItem(eInventorySlot.DistanceWeapon);
            if (CorrectInstrument(distance))
            {
                bot.SwitchWeapon(eActiveWeaponSlot.Distance);
                return true;
            }

            DbInventoryItem twoHand = bot.Inventory?.GetItem(eInventorySlot.TwoHandWeapon);
            if (CorrectInstrument(twoHand))
            {
                bot.SwitchWeapon(eActiveWeaponSlot.TwoHanded);
                return true;
            }

            DbInventoryItem packed = bot.Inventory?.AllItems.FirstOrDefault(item =>
                item != null && item.SlotPosition >= (int)eInventorySlot.FirstBackpack &&
                item.SlotPosition <= (int)eInventorySlot.LastBackpack && CorrectInstrument(item));
            if (packed != null && bot.Inventory.MoveItem(
                    (eInventorySlot)packed.SlotPosition,
                    eInventorySlot.TwoHandWeapon,
                    packed.Count))
            {
                bot.SwitchWeapon(eActiveWeaponSlot.TwoHanded);
                return CorrectInstrument(bot.ActiveWeapon);
            }

            return false;
        }

        /// <summary>
        /// The Spiritmaster's one-minute damage shields (Boon of the Fallen and
        /// its ranks). Cast as routine upkeep they ran out before the next fight
        /// and cost the bot a 4-second cast each time, so they are cast only in
        /// combat. Pet focus shields (pulsing) and longer shields are unchanged.
        /// </summary>
        public static bool IsShortCombatShield(Spell spell) =>
            spell?.SpellType == eSpellType.DamageShield && !spell.IsConcentration && !spell.IsPulsing &&
            spell.Target != eSpellTarget.PET && spell.Duration > 0 && spell.Duration <= 60_000;

        /// <summary>
        /// In a fight: shield the pet that is fighting, otherwise the bot itself
        /// when the enemy is attacking it. One cast per expiry, never out of combat.
        /// </summary>
        private bool TryShortCombatShield()
        {
            if (Body.MiscSpells == null || Body.IsCasting || !Body.CanCastMiscSpells || Body.TargetObject is not GameLiving enemy)
                return false;

            Spell shield = Body.MiscSpells
                .Where(spell => IsShortCombatShield(spell) && !AutonomousPetSupport.RequiresManagedPetFocusShield(BotBody, spell) &&
                                CanCastDefensiveSpell(spell))
                .OrderByDescending(spell => spell.Level)
                .FirstOrDefault();
            if (shield == null)
                return false;

            GameLiving pet = Body.ControlledBrain?.Body;
            GameLiving target =
                pet != null && pet.IsAlive && pet.InCombat && !LivingHasEffect(pet, shield) &&
                Body.IsWithinRadius(pet, shield.CalculateEffectiveRange(Body)) ? pet :
                Body.InCombat && enemy.TargetObject == Body && !LivingHasEffect(Body, shield) ? Body : null;
            if (target == null)
                return false;

            GameObject oldTarget = Body.TargetObject;
            Body.TargetObject = target;
            bool cast = Body.CastSpell(shield, m_mobSpellLine);
            Body.TargetObject = oldTarget;
            return cast;
        }

        internal static bool IsMaintainableClassBuff(Spell spell) => IsMaintainableClassBuff(spell, Classic165Mode.IsOn);

        /// <summary>
        /// A buff kept up between fights. True 1.65 also: the concentration haste (the Healer's Purity of
        /// Attack/Offense/Aggression, the Druid's Hunter's line) - a lasting single-ally buff that was dropped from the
        /// upkeep, so a Healer never cast it. Celerity (group, 20 s) stays a fight buff (TryGroupCelerity).
        /// </summary>
        public static bool IsMaintainableClassBuff(Spell spell, bool classic165) => spell?.SpellType switch
        {
            eSpellType.CombatSpeedBuff => classic165 && spell.IsConcentration && !spell.IsPulsing,
            eSpellType.SpeedEnhancement or
            eSpellType.BodySpiritEnergyBuff or eSpellType.HeatColdMatterBuff or
            eSpellType.SpiritResistBuff or eSpellType.EnergyResistBuff or eSpellType.HeatResistBuff or
            eSpellType.ColdResistBuff or eSpellType.BodyResistBuff or eSpellType.MatterResistBuff or
            eSpellType.AllMagicResistBuff or eSpellType.EnduranceRegenBuff or eSpellType.PowerRegenBuff or
            eSpellType.AblativeArmor or eSpellType.AcuityBuff or eSpellType.AFHitsBuff or
            eSpellType.ArmorAbsorptionBuff or eSpellType.BaseArmorFactorBuff or eSpellType.SpecArmorFactorBuff or
            eSpellType.PaladinArmorFactorBuff or eSpellType.Buff or eSpellType.CelerityBuff or
            eSpellType.ConstitutionBuff or eSpellType.CourageBuff or eSpellType.CrushSlashTrustBuff or
            eSpellType.DexterityBuff or eSpellType.DexterityQuicknessBuff or eSpellType.EffectivenessBuff or
            eSpellType.FatigueConsumptionBuff or eSpellType.FlexibleSkillBuff or eSpellType.HasteBuff or
            eSpellType.HealthRegenBuff or eSpellType.HeroismBuff or eSpellType.MagicResistBuff or
            eSpellType.MeleeDamageBuff or eSpellType.MLABSBuff or eSpellType.ParryBuff or
            eSpellType.PowerHealthEnduranceRegenBuff or eSpellType.StrengthBuff or
            eSpellType.StrengthConstitutionBuff or eSpellType.SuperiorCourageBuff or
            eSpellType.ToHitBuff or eSpellType.WeaponSkillBuff or eSpellType.DamageAdd or
            eSpellType.OffensiveProc or eSpellType.DefensiveProc or eSpellType.DamageShield or
            eSpellType.Bladeturn => true,
            _ => false
        };

        /// <summary>A base buff: one stat (strength, dexterity, constitution) or base armor, from a baseline line.</summary>
        public static bool IsBaseStatBuff(Spell spell) => spell?.SpellType is eSpellType.StrengthBuff or
            eSpellType.DexterityBuff or eSpellType.ConstitutionBuff or eSpellType.BaseArmorFactorBuff;

        /// <summary>Pure rule: of two bot buffers knowing the same base buff, the Healer keeps it up, else the lower ObjectID.</summary>
        public static bool LeavesBaseBuffToMate(bool selfHealer, int selfObjectId, bool mateHealer, int mateObjectId) =>
            selfHealer != mateHealer ? mateHealer : mateObjectId < selfObjectId;

        private Group _baseBuffShareGroup;
        private long _baseBuffShareKey;
        private long _nextBaseBuffShareCheck;
        private Dictionary<int, GameBot> _baseBuffsLeftToMates;

        /// <summary>
        /// True 1.65: a Healer and a Shaman share the baseline Augmentation buffs, and both spent their concentration on
        /// them (two buffers each putting the same base buffs on half the group, neither left with concentration for its
        /// own buffs). A bot buffer leaves a base buff to a living groupmate bot that knows the same spell and keeps it
        /// (the Healer first, else the lower ObjectID), as a song is left to a groupmate with a faster one
        /// (GroupmateHasStrongerSpeed). Worked out once per group change; checked at most once a second.
        /// </summary>
        private bool BaseBuffLeftToGroupmate(Spell spell)
        {
            if (!Classic165Mode.IsOn || !IsBaseStatBuff(spell) || Body?.Group is not { } group)
                return false;
            long now = GameLoop.GameLoopTime;
            if (group != _baseBuffShareGroup || now >= _nextBaseBuffShareCheck)
            {
                _nextBaseBuffShareCheck = now + 1_000;
                List<GameLiving> members = group.GetMembersInTheGroup();
                long key = members.Count;
                foreach (GameLiving member in members)
                    key = key * 31 + member.ObjectID + (member is GameBot mateBot ? mateBot.Level * 7919L : 0);
                if (group != _baseBuffShareGroup || key != _baseBuffShareKey || _baseBuffsLeftToMates == null)
                {
                    _baseBuffShareGroup = group;
                    _baseBuffShareKey = key;
                    _baseBuffsLeftToMates = BaseBuffsKeptByMates(members);
                }
            }
            return _baseBuffsLeftToMates.TryGetValue(spell.ID, out GameBot keeper) && keeper.IsAlive && keeper.Group == group &&
                (!UsesVeteranBuffing || keeper.CanAffordConcentration(spell) && !keeper.InCombat &&
                 Body.IsWithinRadius(keeper, Math.Max(350, spell.CalculateEffectiveRange(Body))));
        }

        private Dictionary<int, GameBot> BaseBuffsKeptByMates(List<GameLiving> members)
        {
            Dictionary<int, GameBot> left = new();
            GameBot self = BotBody;
            if (self == null)
                return left;
            bool selfHealer = self.CharacterClass?.ID == (int)eCharacterClass.Healer;
            HashSet<int> mine = (self.MiscSpells ?? []).Concat(self.InstantMiscSpells ?? [])
                .Where(spell => IsBaseStatBuff(spell) && spell.Level <= self.Level).Select(spell => spell.ID).ToHashSet();
            if (mine.Count == 0)
                return left;
            foreach (GameLiving member in members)
            {
                if (member == self || member is not GameBot mate)
                    continue;
                bool mateHealer = mate.CharacterClass?.ID == (int)eCharacterClass.Healer;
                if (!LeavesBaseBuffToMate(selfHealer, self.ObjectID, mateHealer, mate.ObjectID))
                    continue;
                foreach (Spell spell in (mate.MiscSpells ?? []).Concat(mate.InstantMiscSpells ?? []))
                    if (spell != null && spell.Level <= mate.Level && mine.Contains(spell.ID))
                        left.TryAdd(spell.ID, mate);
            }
            return left;
        }

        /// <summary>
        /// Another living groupmate provides a faster speed: a bot that knows one
        /// (it will run it), or a real player who is running one right now.
        /// From the stefanrows/OfflineDAoC fork (0.200.0).
        /// </summary>
        private bool GroupmateHasStrongerSpeed(Spell speed)
        {
            if (Body?.Group == null || speed == null)
                return false;
            foreach (GameLiving member in Body.Group.GetMembersInTheGroup())
            {
                if (member == Body || !member.IsAlive)
                    continue;
                if (member is GamePlayer player)
                {
                    if (player.effectListComponent.GetPulseEffects().Any(effect =>
                            !effect.IsEnding && !effect.IsEnded &&
                            effect.SpellHandler?.Spell is Spell running &&
                            running.SpellType == eSpellType.SpeedEnhancement && running.Value > speed.Value))
                        return true;
                    continue;
                }
                if (member is not GameBot mate)
                    continue;
                if ((mate.MiscSpells ?? []).Concat(mate.InstantMiscSpells ?? [])
                    .Any(spell => spell != null && spell.SpellType == eSpellType.SpeedEnhancement &&
                                  spell.Level <= mate.Level && spell.Value > speed.Value))
                    return true;
            }
            return false;
        }

        private eAutonomousThinkMode CurrentThinkMode() => HasAggro || Body?.InCombat == true
                ? eAutonomousThinkMode.Combat
                : BotBody?.IsPlayerLedGroup == true
                    ? eAutonomousThinkMode.PlayerLed
                    : BotBody?.IsRecoveryResting == true
                        ? eAutonomousThinkMode.Resting
                        : Body?.IsMoving == true || AutonomousGroupMotion.GroupTraveling(BotBody)
                            ? eAutonomousThinkMode.Travel
                            : eAutonomousThinkMode.Planning;

        private eAutonomousFidelity CurrentFidelity()
        {
            if (BotBody?.IsPlayerLedGroup == true)
                return eAutonomousFidelity.NearbyHuman;

            long now = GameLoop.GameLoopTime;
            if (now < _nextFidelityCheckTick)
                return _cachedFidelity;
            _nextFidelityCheckTick = now + 5_000 + (Body?.ObjectID ?? 0) % 1_500;
            if (Body?.IsVisibleToPlayers != true)
                return _cachedFidelity = eAutonomousFidelity.Efficient;
            return _cachedFidelity = Body.GetPlayersInRadius(6000).Count > 0
                ? eAutonomousFidelity.NearbyHuman
                : eAutonomousFidelity.Standard;
        }

        public override void KillFSM()
        {
            FSM.KillFSM();
        }

        public override void Notify(DOLEvent e, object sender, EventArgs args)
        {
            if (e == GameLivingEvent.AttackedByEnemy && args is AttackedByEnemyEventArgs attackArgs)
            {
                OnAttackedByEnemy(attackArgs.AttackData);
            }
        }

        #endregion

        #region FSM States

        private class BotState : FSMState
        {
            protected BotBrain _brain;

            public BotState(BotBrain brain) : base()
            {
                _brain = brain;
            }

            public override void Enter() { }
            public override void Exit() { }
            public override void Think() { }
        }

        private class BotState_Idle : BotState
        {
            public BotState_Idle(BotBrain brain) : base(brain)
            {
                StateType = eFSMStateType.IDLE;
            }

            public override void Enter()
            {
                if (_brain.Body != null)
                {
                    _brain.Body.StopFollowing();
                    _brain.Body.StopAttack();
                }
            }

            public override void Think()
            {
                _brain.AlreadyCheckedHeals = false;

                if (_brain.HasAggro)
                {
                    _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                    return;
                }

                GamePlayer leader = _brain.AssistedPlayer;
                if (leader != null && leader.IsAlive)
                {
                    // Check if owner is attacking something
                    if (leader.TargetObject is GameLiving ownerTarget
                        && !_brain.SupportSitsOutFights()
                        && PlayerLedPullCoordinator.Available(_brain.BotBody, leader)
                        && _brain.Body.IsWithinRadius(ownerTarget, GROUP_DEFENSE_ASSIST_RADIUS)
                        && PlayerLedPullCoordinator.LeaderAttacks(leader)
                        && _brain.CanAggroTarget(ownerTarget))
                    {
                        _brain.AddToAggroList(ownerTarget, 1);
                        _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                        return;
                    }

                    // True 1.65 Mode: a bot in a player's group goes back to following (and buffing) the moment its
                    // player is alive again, not once the player is 650 away (owner 2026-11-03: after a raise they
                    // stood idle). /bot stay (normal mode's /spawn helpers) still holds position until 650.
                    if (!_brain.Body.IsWithinRadius(leader, 650) ||
                        Classic165Mode.IsOn && _brain.BotBody?.IsPlayerLedGroup == true)
                    {
                        _brain.FSM.SetCurrentState(eFSMStateType.FOLLOW);
                        return;
                    }
                }

                _brain.CheckSpells(eCheckSpellType.Defensive);
            }
        }

        private class BotState_Follow : BotState
        {
            public BotState_Follow(BotBrain brain) : base(brain)
            {
                StateType = eFSMStateType.FOLLOW;
            }

            public override void Enter()
            {
                _brain.FollowFormation();
            }

            public override void Think()
            {
                _brain.AlreadyCheckedHeals = false;

                if (_brain.HasAggro)
                {
                    _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                    return;
                }

                // Persistent world bots have no human leader. Their release walk
                // must run before the ordinary owner/leader guard or they would
                // enter IDLE immediately and remain stranded at the bind point.
                if (_brain.BotBody.ContinueReturnJourney())
                    return;

                GamePlayer leader = _brain.AssistedPlayer;
                if (leader == null || !leader.IsAlive)
                {
                    _brain.FSM.SetCurrentState(eFSMStateType.IDLE);
                    return;
                }

                // Check if owner is in combat or attacking something
                if (PlayerLedPullCoordinator.LeaderAttacks(leader))
                {
                    // Try to get owner's target
                    if (leader.TargetObject is GameLiving ownerTarget
                        && !_brain.SupportSitsOutFights()
                        && PlayerLedPullCoordinator.Available(_brain.BotBody, leader)
                        && _brain.Body.IsWithinRadius(ownerTarget, GROUP_DEFENSE_ASSIST_RADIUS)
                        && ownerTarget.IsAlive
                        && _brain.CanAggroTarget(ownerTarget))
                    {
                        _brain.AddToAggroList(ownerTarget, 1);
                        _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                        return;
                    }

                    // If owner is in combat but we don't have a valid target, check recent attackers
                }

                // No combat — heal/buff
                if (_brain.CheckHeals())
                {
                    _brain.FollowDuringMobileSong();
                    return;
                }

                // A native caster-speed pulse is not a mobile performer song.
                // Keep the temporary companion planted for the real cast; the
                // next FOLLOW pulse resumes formation as soon as it finishes.
                if (CompanionFollowPolicy.NativeTravelSpeedCastInProgress(_brain.BotBody))
                    return;

                // Temporary companions already passed through the strict rest
                // preflight before songs, buffs, pets, roleplay, and this FSM.
                // Keep the autonomous threshold policy byte-for-byte scoped to
                // non-temporary playerbots until companion behavior is proven.
                if (!_brain.BotBody.IsTemporaryGroupHelper)
                {
                    // True 1.65 Mode: buffs and heals out of combat first, on all the power there is, then a rest
                    // (owner 2026-11-06); a bot already seated finishes its rest (BotRestRecovery.DeferOptionalCasterUpkeep)
                    bool atRestPoint = leader.CurrentRegionID == _brain.Body.CurrentRegionID &&
                        _brain.Body.IsWithinRadius(leader, 350);
                    bool movementBlocks = AutonomousRestPolicy.MovementBlocksRest(
                        _brain.Body.IsMoving,
                        leader.IsMoving,
                        _brain._ambientWanderMovement);
                    bool restBlocked = BotRestRecovery.BlocksRest(_brain.BotBody) || BotRestRecovery.ServantBusyHelping(_brain.BotBody);
                    bool rest = !BotRestRecovery.BuffingBeforeRest(_brain.BotBody, GameLoop.GameLoopTime) &&
                        AutonomousRestPolicy.ShouldRest(
                        atRestPoint,
                        movementBlocks,
                        restBlocked,
                        _brain.BotBody.IsRecoveryResting,
                        _brain.Body.HealthPercent,
                        _brain.Body.ManaPercent,
                        _brain.Body.EndurancePercent,
                        _brain.Body.MaxMana > 0);
                    // owner 2026-11-13: a rest /grind started (it waits for full endurance) is not ended at 90% here
                    if (!rest && _brain.BotBody.IsRecoveryResting && atRestPoint && !movementBlocks && !restBlocked &&
                        DOL.GS.Commands.PlayerCompanionGrind.HoldsRest(_brain.BotBody))
                        rest = true;
                    if (rest)
                        _brain._ambientWanderMovement = false;
                    if (rest)
                        _brain.BotBody.BeginRecoveryRest();
                    else
                        _brain.BotBody.WakeRecoveryRest();
                    if (rest)
                        return;
                }

                // Ambient movement is deliberately the lowest priority. Healing,
                // pet maintenance, resting and defensive abilities all finish first.
                if (!_brain.Body.InCombat && _brain.CheckSpells(eCheckSpellType.Defensive))
                {
                    _brain.FollowDuringMobileSong();
                    return;
                }

                // Idle bots stand in formation; there is no idle wandering (it walked
                // at 40% speed). Idle chat around a resting leader is kept.
                _brain.FollowFormation();
                if (_brain.CanAmbientWander && !_brain.Body.IsCasting)
                    _brain.MaybeIdleRoleplay();
            }

            public override void Exit()
            {
                _brain.Body.StopFollowing();
            }
        }

        private class BotState_Aggro : BotState
        {
            private const int LEAVE_WHEN_OUT_OF_COMBAT_FOR = 6000;
            private long _aggroEndTime;

            public BotState_Aggro(BotBrain brain) : base(brain)
            {
                StateType = eFSMStateType.AGGRO;
            }

            public override void Enter()
            {
                _brain._ambientWanderMovement = false;
                // The first-contact window starts at the state's first think, not when it was set:
                // a pull or raid call ordered between thinks (travel/planning intervals reach ~10 s)
                // otherwise expired before the bot ever acted on it (run 9/10 pull loops, raid
                // target calls that never landed on Summoner Cunovinda).
                _aggroEndTime = long.MaxValue;
            }

            public override void Exit()
            {
                _brain.Body.StopAttack();
                _brain.Body.TargetObject = null;
                _brain.ClearAggroList();
            }

            public override void Think()
            {
                _brain.AlreadyCheckedHeals = false;
                if (_aggroEndTime == long.MaxValue)
                    _aggroEndTime = GameLoop.GameLoopTime + LEAVE_WHEN_OUT_OF_COMBAT_FOR;

                if (_brain._returnToFormationAfterPull && _brain.ActiveOrderedPullTarget == null &&
                    _brain.CalculateNextAttackTarget() == null)
                {
                    _brain._returnToFormationAfterPull = false;
                    if (_brain.Body.IsCasting)
                        _brain.Body.StopCurrentSpellcast();
                    _brain.Body.StopAttack();
                    _brain.Body.TargetObject = null;
                    _brain.ClearAggroList();
                    _brain.FSM.SetCurrentState(_brain.AssistedPlayer?.IsAlive == true
                        ? eFSMStateType.FOLLOW
                        : eFSMStateType.IDLE);
                    return;
                }

                // A mesmerized or rooted monster still near is not the end of the fight: wait for its hold to end or
                // break early, then engage it, instead of forgetting it after six quiet seconds (owner 2026-11-03).
                bool waitingOnHeld = _brain.WaitsOnHeldEnemy();
                if (waitingOnHeld)
                    _aggroEndTime = GameLoop.GameLoopTime + LEAVE_WHEN_OUT_OF_COMBAT_FOR;
                if (_brain.ActiveOrderedPullTarget == null && !waitingOnHeld &&
                    (!_brain.HasAggro || (!_brain.HasCommittedDungeonPull &&
                        !_brain.Body.InCombatInLast(LEAVE_WHEN_OUT_OF_COMBAT_FOR) && GameServiceUtils.ShouldTick(_aggroEndTime))))
                {
                    if (!_brain.Body.IsMezzed && !_brain.Body.IsStunned)
                    {
                        if (_brain.AssistedPlayer != null && _brain.AssistedPlayer.IsAlive)
                            _brain.FSM.SetCurrentState(eFSMStateType.FOLLOW);
                        else
                            _brain.FSM.SetCurrentState(eFSMStateType.IDLE);

                        return;
                    }
                }

                // Passive stance: no attacking; the same lifesavers as /passive (O2: escapes, heals, the speed song)
                if (_brain.BotBody.Stance == eBotStance.Passive)
                {
                    _brain.TryPassiveLifesavers();
                    return;
                }

                if (_brain.HoldsExclusiveSupportRole())
                {
                    _brain.PerformGroupSupport();
                }
                else if (_brain.IsHealer)
                {
                    if (!_brain.CheckHeals())
                        _brain.AttackMostWanted();
                }
                else
                    _brain.AttackMostWanted();
            }
        }

        private class BotState_Passive : BotState
        {
            public BotState_Passive(BotBrain brain) : base(brain)
            {
                StateType = eFSMStateType.PASSIVE;
            }

            public override void Enter()
            {
                _brain.Body.StopAttack();
                _brain.Body.StopFollowing();
                _brain.ClearAggroList();
            }

            public override void Think()
            {
                _brain.AlreadyCheckedHeals = false;

                if (_brain.IsHealer)
                    _brain.CheckHeals();
            }
        }

        #endregion

        #region Combat

        public virtual void AttackMostWanted()
        {
            if (!IsActive)
                return;

            // M6: a solo caster steps straight back from a rooted or snared monster (once per 20 s, no route)
            if (TryCasterStepBack())
                return;

            if (AutonomousDefensivePull.Hold(BotBody) || SoloRangedPull165.Hold(BotBody))
            {
                if (!Body.IsCasting) CheckHeals();
                return;
            }

            EnforceCompanionEngagementRange();

            if (HoldsExclusiveSupportRole())
            {
                PerformGroupSupport();
                return;
            }

            // A pet focus shield channel stands still; heals and add control may take over it.
            if (WatchPetFocusShield())
            {
                ContinuePetFocusShield();
                return;
            }

            // The native handler owns an accepted cast through completion (and
            // handles real hits, range/LOS loss and death). Do not replace it
            // with a melee/range/pull decision merely because an enemy is near.
            // This also preserves legal uninterruptible casts.
            if (Body.IsCasting &&
                !SavageBotCombatPolicy.MayAttackDuringActiveCast(
                    (eCharacterClass)BotBody.CharacterClass.ID,
                    Body.castingComponent?.SpellHandler?.Spell))
                return;

            GameLiving protectionTarget = FindProtectionTarget();
            if (protectionTarget != null)
                AddToAggroList(protectionTarget, 10000);

            GameLiving directAttacker = RecentDirectAttacker();

            // Bow release is driven by AttackAction, not by this brain pulse.
            // Re-entering the general target/movement decision while Aim is
            // pending is what produced the rapid draw animation with no arrow.
            // A close attacker or a group-protection emergency still breaks the
            // guard and follows the existing defensive/melee behavior.
            // A stalled close-range draw (see RequestArcherMeleeFallback) no
            // longer owns the turn, so the melee fallback below can engage.
            if (!ArcherMeleeFallbackActive && Body.TargetObject is GameLiving currentBowTarget &&
                BotRangedCombat.BowDrawOwnsDecision(
                    (eCharacterClass)BotBody.CharacterClass.ID,
                    Body.attackComponent?.AttackState == true,
                    Body.ActiveWeaponSlot,
                    currentBowTarget.IsAlive && currentBowTarget.ObjectState == GameObject.eObjectState.Active &&
                    (currentBowTarget is not DOL.GS.Keeps.GuardLord || Body.IsWithinRadius(currentBowTarget, 280)),
                    Body.attackComponent != null &&
                    Body.IsWithinRadius(currentBowTarget, CapRangeForTarget(currentBowTarget, Body.attackComponent.AttackRange)),
                    directAttacker != null && Body.IsWithinRadius(directAttacker, Body.MeleeAttackRange + 200),
                    protectionTarget != null))
                return;

            GameObject previousAttackTarget = Body.TargetObject;
            GameLiving activePetTarget = BotBody?.IsAutonomousWorldBot == true && !BotBody.IsPlayerLedGroup
                ? AutonomousPetSupport.ActiveOwnedPetCombatTarget(BotBody)
                : null;
            if (activePetTarget != null)
                AddToAggroList(activePetTarget, Math.Max(1, activePetTarget.EffectiveLevel));
            // The pet's fight is not followed onto a mesmerized or rooted monster (BotBrain.CrowdControlRespect).
            if (activePetTarget != null && IsBreakablyControlled(activePetTarget))
                activePetTarget = null;
            // Once our own pet has made contact, keep the owner and pet on that
            // same live opponent unless the owner is personally attacked or is
            // protecting a group member. Generic aggro may still contain the
            // previous pull and used to make archers/casters repeatedly switch
            // away from the fight their pet had already started.
            // A gamebot party's members follow its kill target (BotBrain.MainAssist); the tank sets it.
            // sh-bosses L5: a raid's call onto a "kill first" add outranks the party's kill target (BotBrain.RaidBoss)
            GameLiving raidCall = protectionTarget == null ? RaidCallTarget(directAttacker) : null;
            GameLiving assistTarget = protectionTarget == null && raidCall == null ? MainAssistTarget(directAttacker) : null;
            Body.TargetObject = protectionTarget ?? raidCall ?? assistTarget ?? directAttacker ?? activePetTarget ?? CalculateNextAttackTarget();
            // M2: a tank peeling an add off a groupmate never moves the kill target onto that add (it may start one)
            OfferKillTarget(Body.TargetObject as GameLiving,
                protectionTarget != null && Body.TargetObject == protectionTarget && protectionTarget.TargetObject != Body);
            RedirectPetFromControlledTarget(Body.TargetObject as GameLiving);
            MaintainTankPeel();
            TryShieldSwapForSlam();
            // A tethered boss outside its tether is immune: stop hitting it and lead it home (AutonomousTetherReset).
            if (Body.TargetObject is GameNPC tetheredBoss && AutonomousTetherReset.TryHandle(BotBody, tetheredBoss))
                return;
            // dragon-fight L1: ... and a boss outside the lair circle it takes damage in (BotBrain.RaidBoss)
            if (Body.TargetObject is GameNPC lairBoss && TryHoldForBossOutsideLair(lairBoss))
                return;
            // epic-bosses L2: out of a boss's area spell while it is cast (BotBrain.RaidBoss)
            if (Body.TargetObject is GameLiving areaBoss && TryStepOutOfBossArea(areaBoss))
                return;
            // a shield tank concentrating on blocking (Engage) does not swing this turn
            if (MaintainEngage())
                return;

            if (Body.TargetObject is GameLiving wallTarget && AutonomousRvrDefense.HoldWall(BotBody, wallTarget))
            {
                if (BotRangedCombat.IsDedicatedArcher((eCharacterClass)BotBody.CharacterClass.ID) &&
                    BotBody.Inventory.GetItem(eInventorySlot.DistanceWeapon) != null &&
                    Body.ActiveWeaponSlot != eActiveWeaponSlot.Distance)
                    BotBody.SwitchWeapon(eActiveWeaponSlot.Distance);
                int defenseRange = PrefersCurrentSpellRange() ? OffensiveApproachRange(wallTarget) :
                    Body.attackComponent.AttackRange;
                if (!AutonomousRvrDefense.IsCommittedDefender(BotBody) && (!Body.IsWithinRadius(wallTarget, defenseRange) ||
                    !PathfindingProvider.Instance.HasLineOfSight(Body.CurrentZone, new(Body.X, Body.Y, Body.Z),
                        new(wallTarget.X, wallTarget.Y, wallTarget.Z), PathfindingProvider.Instance.DefaultFilters)))
                {
                    Body.StopAttack();
                    Body.StopFollowing();
                    Body.StopMovingOnPath();
                    Body.StopMoving();
                    CheckHeals();
                    return;
                }
            }

            if (Body.TargetObject != null)
            {
                if (BotBody.IsAutonomousWorldBot && Body.TargetObject is DOL.GS.Keeps.GuardLord lord &&
                    !Body.IsWithinRadius(lord, 280))
                {
                    Body.StopAttack();
                    Body.PathTo(new Point3D(lord.X, lord.Y, lord.Z), Body.MaxSpeed);
                    return;
                }
                if (Body.TargetObject is GameLiving ambushTarget && TryApproachAmbush(ambushTarget))
                    return;
                // an assassin hides and opens with Backstab / Perforate Artery (BotBrain.StealthOpener)
                if (Body.TargetObject is GameLiving openerTarget && TryStealthOpener(openerTarget))
                    return;
                // O3: a solo Shaman or Runemaster roots its monster and takes one step back (BotBrain.SoloRootKite)
                if (Body.TargetObject is GameLiving kiteTarget && TrySoloRootKite(kiteTarget))
                    return;
                // a damage dealer steps behind (or beside) a monster the tank holds, for its back/side styles
                if (Body.TargetObject is GameLiving flankTarget && TryFlank(flankTarget))
                    return;
                if (Body.IsStealthed)
                    Body.Stealth(false);

                if (Body.TargetObject is GameLiving pullTarget && pullTarget == ActiveOrderedPullTarget &&
                    ApproachPlayerLedPullTarget(pullTarget))
                    return;

                bool rangedCaster = PrefersCurrentSpellRange();

                // A melee bot already chasing its own target at stick range keeps
                // that order. Clearing it every think made NpcStartAttack stop the
                // running bot and re-follow, which clients saw as rubberbanding.
                // A caster meleeing because it has no attack spell yet (a new base-class caster) chases like a melee
                // bot. Out of power it no longer walks in (owner 2026-11-05): it waits for power, see TryOutOfPowerWait.
                bool outOfPowerMelee = rangedCaster && NoAttackSpellYet();
                if (rangedCaster && !outOfPowerMelee || !IsMeleeChasing(Body.TargetObject))
                    Body.StopFollowing();
                if (!rangedCaster && TryHealBetweenSwings())
                    return;
                if (!rangedCaster && Body.TargetObject is GameLiving meleeTarget && TryEngageUnderMeleePressure(meleeTarget))
                    return;

                // End our own staff swings/chase before considering a spell.
                // Otherwise each new swing refreshes SelfInterruptTime forever.
                // Actual incoming interruption still goes through native rules.
                // A caster with no attack spell yet keeps swinging.
                if (rangedCaster && !Body.IsBeingInterruptedByOther && Body.attackComponent.AttackState &&
                    !NoAttackSpellYet())
                    Body.StopAttack();

                if (!UsesDefensiveOnlyPet)
                {
                    // Primary caster-pet classes never hand ownership of the AI
                    // turn to the pet. The pet gets the same target while the
                    // owner continues immediately through its ordinary legal
                    // spell/range/melee decision below.
                    if (BotBody.CharacterClass != null &&
                        AutonomousPetSupport.UsesIndependentOwnerPetCombat((eCharacterClass)BotBody.CharacterClass.ID))
                        AutonomousPetSupport.SynchronizeIndependentPet(
                            Body,
                            Body.ControlledBrain,
                            ResolvePetCombatTarget(Body.TargetObject as GameLiving));
                    else
                        CommandPetAttack(Body.TargetObject as GameLiving);
                }

                if (protectionTarget != null && TryPriorityTaunt(protectionTarget))
                {
                    Body.StopAttack();
                    return;
                }

                bool bowCycleOwnsAction = Body.attackComponent != null &&
                    Body.rangeAttackComponent != null &&
                    BotRangedCombat.BowCycleOwnsAction(
                        (eCharacterClass)BotBody.CharacterClass.ID,
                        Body.attackComponent.AttackState,
                        Body.ActiveWeaponSlot,
                        Body.rangeAttackComponent.RangedAttackState);
                bool spellAction = !bowCycleOwnsAction && CheckSpells(eCheckSpellType.Offensive);
                bool hasPendingSpell = Body.castingComponent?.HasPendingSkillRequests == true;
                Spell pendingSpell = null;
                if (hasPendingSpell)
                    Body.castingComponent.TryPeekPendingSpell(out pendingSpell);
                bool continueSavageMelee = SavageBotCombatPolicy.ContinueMeleeAfterSpell(
                    (eCharacterClass)BotBody.CharacterClass.ID,
                    spellAction,
                    Body.IsCasting,
                    Body.castingComponent?.SpellHandler?.Spell,
                    hasPendingSpell,
                    pendingSpell);
                bool preserveRangedDraw = BotRangedCombat.PreserveDrawAfterInstantSpell(
                    spellAction,
                    Body.IsCasting,
                    Body.castingComponent?.HasPendingSkillRequests == true,
                    Body.ActiveWeaponSlot,
                    Body.rangeAttackComponent?.RangedAttackState ?? eRangedAttackState.None);
                bool continueMinstrelMelee = MinstrelBotCombatPolicy.ContinueAfterInstantDamage(
                    UsesMinstrelHybridCombat, spellAction, Body.IsCasting,
                    Body.castingComponent?.SpellHandler?.Spell, hasPendingSpell, pendingSpell);
                // Every melee hybrid keeps closing to melee after an instant spell, as the Minstrel rule above does
                // (2026-10-31 runs: promoted Champions and Bards cast Blast of Courage or Concussive Shout, stopped,
                // sat to refill the 5 endurance and pulled again every 12 seconds, so they levelled slowest of all).
                bool continueHybridMelee = !rangedCaster && MinstrelBotCombatPolicy.ContinueAfterInstantDamage(
                    true, spellAction, Body.IsCasting,
                    Body.castingComponent?.SpellHandler?.Spell, hasPendingSpell, pendingSpell);
                // Q3: a grouped support Cleric keeping its power for heals stands back instead of charging in with its
                // mace (Prima: "stick close to your group ... don't let the enemy close in"); a monster on it was
                // already answered by TryEngageUnderMeleePressure above.
                if (!spellAction && _clericHoldsBack && !rangedCaster && !Body.IsCasting)
                {
                    if (Body.IsAttacking)
                        Body.StopAttack();
                    Body.StopFollowing();
                    return;
                }
                if (spellAction && !continueSavageMelee && !preserveRangedDraw && !continueMinstrelMelee && !continueHybridMelee)
                {
                    Body.StopAttack();
                }
                else
                {
                    // The performer can still heal, control or use shouts above. An instrument song
                    // needs the instrument equipped, so do not enter the weapon or unarmed fallback.
                    if (HasActiveCombatSongInstrument())
                    {
                        Body.StopAttack();
                        return;
                    }

                    if (rangedCaster && Body.TargetObject is GameLiving spellTarget)
                    {
                        // Try casts (including instant spells) first.
                        // A close incoming attack permits melee, and so does
                        // having no power for any attack spell: the caster walks
                        // in with its staff until it can cast again. Cooldowns,
                        // an already-active debuff or a rejected cast never
                        // order a staff charge.
                        if (TryEngageUnderMeleePressure(spellTarget)) return;
                        if (TryOutOfPowerMelee(spellTarget)) return;
                        if (TryOutOfPowerWait(spellTarget)) return;
                        ApproachForOffensiveSpell(spellTarget);
                        return;
                    }

                    CheckOffensiveAbilities();

                    if (Body.ControlledBrain != null && !UsesDefensiveOnlyPet)
                        CommandPetAttack(Body.TargetObject as GameLiving);

                    if (BotBody.CharacterClass.ClassType == eClassType.ListCaster && BotBody.CharacterClass.ID != (int)eCharacterClass.Valewalker)
                    {
                        if (Body.TargetObject is GameLiving casterTarget && ApproachForOffensiveSpell(casterTarget))
                            return;

                    }

                    // Switch weapon based on stance and target distance before attacking
                    if (Body.TargetObject is GameLiving livingTarget)
                    {
                        int distance = (int)Body.GetDistanceTo(livingTarget);
                        var stance = BotBody.Stance;
                        DbInventoryItem distanceItem = BotBody.Inventory.GetItem(eInventorySlot.DistanceWeapon);
                        // Back to the bow from melee only with endurance for a few shots: at the bare shot cost the bot
                        // flipped bow/melee on every shot (2026-11-05 weapon-swap audit).
                        bool hasRangedCombatWeapon = BotRangedCombat.CanUse(BotBody, distanceItem) &&
                                                     Body.Endurance >= Body.rangeAttackComponent.ShotEnduranceCost *
                                                     (Body.ActiveWeaponSlot == eActiveWeaponSlot.Distance ? 1 : 3);

                        // Direct melee pressure was handled above by
                        // TryEngageUnderMeleePressure.  Until that happens, a
                        // persistent Scout/Hunter/Ranger holds bow range even
                        // when its target closes inside nominal melee distance.
                        // Proximity alone must not restart a valid draw.
                        // Gamebot and hired archers fight like a player (owner 2026-11-03): a monster that reaches
                        // them and is on them gets the melee weapon before its first blow; one busy with someone
                        // else is still shot at.
                        // once in melee, a monster stepping just out of reach does not swap the bow back in (audit O4)
                        int meleeHold = Body.ActiveWeaponSlot == eActiveWeaponSlot.Distance ? 0 : 150;
                        bool targetForcesMelee = distance <= Body.MeleeAttackRange + meleeHold &&
                                                 (!BotRangedCombat.UsesAutonomousBowPositioning(BotBody) ||
                                                  livingTarget.TargetObject == Body) ||
                                                 ArcherMeleeFallbackActive;

                        bool useRanged = BotRangedCombat.ShouldUseRangedWeapon(
                            (eCharacterClass)BotBody.CharacterClass.ID,
                            stance,
                            targetForcesMelee,
                            hasRangedCombatWeapon) &&
                            (stance == eBotStance.Ranged || ArcherFightsWithBow());   // M7: the Weapon row / training
                        MaintainRapidFire(livingTarget, useRanged);
                        if (useRanged)
                        {
                            if (Body.ActiveWeaponSlot != eActiveWeaponSlot.Distance)
                                Body.SwitchWeapon(eActiveWeaponSlot.Distance);

                            // df-bosses L2: an archer stands where a raid boss takes damage (Legion: inside 1,400)
                            int rangedAttackRange = CapRangeForTarget(livingTarget, Body.attackComponent.AttackRange);
                            if (BotRangedCombat.ShouldCloseToRangedRange(
                                    true, distance, rangedAttackRange))
                            {
                                int desired = BotRangedCombat.BowApproachDistance(Body.MeleeAttackRange, rangedAttackRange);
                                BotBody.WakeRecoveryRest();   // a resting bot ignores follow orders
                                Body.StopAttack();
                                Body.Follow(livingTarget,
                                    (short)Math.Clamp(desired, 160, short.MaxValue),
                                    (short)Math.Clamp((int)MAX_AGGRO_LIST_DISTANCE, 200, (int)short.MaxValue));
                                return;
                            }
                        }
                        else
                        {
                            // Includes the class-level Savage override. A real
                            // melee weapon makes StartAttack close to the target
                            // instead of entering the ranged draw state. Repair
                            // the GameBot loadout once before conceding that the
                            // actor has no legal melee action; never leave a
                            // stale "Fighting" target with no weapon.
                            bool weaponReady = SwitchToUsableMeleeWeapon();
                            if (!weaponReady)
                            {
                                BotBody.EnsureBotWeaponReady();
                                weaponReady = SwitchToUsableMeleeWeapon();
                            }
                            if (!weaponReady)
                            {
                                Body.StopAttack();
                                Body.TargetObject = null;
                                return;
                            }
                            QueueUsableMeleeStyle();
                        }
                    }

                    // Preserve a draw already in progress. Melee keeps its
                    // existing follow/retry behavior; a new target still starts.
                    if (Body.ActiveWeaponSlot != eActiveWeaponSlot.Distance ||
                        BotRangedCombat.NeedsAttackStart(Body.attackComponent.AttackState, previousAttackTarget, Body.TargetObject))
                        Body.StartAttack(Body.TargetObject);
                }
            }
            else
            {
                // nothing left to fight but a held monster (owner 2026-11-03): stop swinging at it, wait for the hold
                if (Body.IsAttacking)
                    Body.StopAttack();
                TryEnterStealthWhileIdle();
            }
        }

        private void TryEnterStealthWhileIdle()
        {
            // Idle is not a reason to hide (and slow travel). Stealth belongs to
            // the approach to a legal enemy player/bot, not random PvE pauses,
            // or to a companion hidden with its hidden player (CompanionStealth).
            if (Body.IsStealthed && !CompanionStealth.HoldsStealth(BotBody)) Body.Stealth(false);
        }

        private bool TryApproachAmbush(GameLiving target)
        {
            if (!BotRvrAmbush.CanApproach(BotBody, target)) return false;
            int range = Body.MeleeAttackRange;
            if (BotRangedCombat.IsDedicatedArcher((eCharacterClass)BotBody.CharacterClass.ID) &&
                BotRangedCombat.CanUse(BotBody, BotBody.Inventory.GetItem(eInventorySlot.DistanceWeapon)))
                range = 1_000;
            if (Body.IsWithinRadius(target, range)) return false;
            Body.Stealth(true);
            if (!Body.IsStealthed) return false; // A running song can legally prevent stealth.
            Body.Follow(target, (short)range, (short)MAX_AGGRO_LIST_DISTANCE);
            return true;
        }

        private bool NeedsOffensiveSpellApplication(GameLiving target, Spell spell) =>
            spell != null && (spell.Duration == 0 || !LivingHasEffect(target, spell) ||
                spell.SpellType is eSpellType.DirectDamageWithDebuff or eSpellType.DamageSpeedDecrease);

        private int OffensiveApproachRange(GameLiving target)
        {
            int range = SpellApproachRange();
            // An Animist fights through its shrooms: stand where they reach the
            // target (1,000), not at nuke range (1,500), so every shroom can be
            // planted beside the Animist instead of out by the enemy.
            int shroomRange = BotAnimistPolicy.ShroomStandoffRange(BotBody);
            // df-bosses L2: never beyond the range a raid boss can be damaged from (BotBrain.RaidBoss)
            return CapRangeForTarget(target, shroomRange > 0 && range > 0 ? Math.Min(range, shroomRange) : range);
        }

        /// <summary>A caster that knows no attack spell yet (a level-1 Forester: its first damage spell comes at 2) fights
        /// with its weapon instead of standing still (owner 2026-10-31: no Forester got past level 1).</summary>
        // Only a True 1.65 base-class bot: a promoted caster with no castable spell (a failed or unaffordable cast)
        // still never charges in with its staff.
        private bool NoAttackSpellYet() => BotBody?.IsBaseClass165 == true && SpellApproachRange() == 0;

        private int SpellApproachRange()
        {
            // SortSpells stores bolts and instant nukes outside HarmfulSpells.
            // Read the small learned list so those classes use their real range.
            return Body.Spells?.Where(spell => spell != null && spell.IsHarmful &&
                    spell.Level <= Body.Level && spell.Target is eSpellTarget.ENEMY or eSpellTarget.AREA or eSpellTarget.CONE &&
                    spell.SpellType is not eSpellType.Charm and not eSpellType.Amnesia and not eSpellType.Confusion and not eSpellType.Taunt &&
                    !BotControlSpells.IsControl(spell) &&   // owner 2026-11-08: control is no attack spell to stand at
                    !AutonomousPetSupport.RequiresManagedPetFocusShield(BotBody, spell) &&
                    AnimistSingleTargetPolicy.AllowsAutomatedSpell(BotBody, spell))
                .Select(spell => spell.Target == eSpellTarget.AREA && spell.Range <= 0
                    ? Math.Max(1, spell.Radius) : Math.Max(1, spell.CalculateEffectiveRange(Body)))
                .DefaultIfEmpty(0).Max() ?? 0;
        }

        private long _closerSpellApproachUntilTick;

        /// <summary>
        /// A watched pull that has not connected asks the caster to stand well
        /// inside its spell range for a while (a wandering target, awkward
        /// terrain, or a cast that kept failing at the edge of range).
        /// </summary>
        public void RequestCloserSpellApproach(long untilTick) => _closerSpellApproachUntilTick = untilTick;

        private long _archerMeleeFallbackUntilTick;

        /// <summary>
        /// A watched pull whose archer stalled close to its target (the native NPC
        /// attack treats ~300 units as melee distance for bows, while the bot keeps
        /// holding its bow there) fights it in melee for a while instead.
        /// </summary>
        public void RequestArcherMeleeFallback(long untilTick) => _archerMeleeFallbackUntilTick = untilTick;

        private bool ArcherMeleeFallbackActive => GameLoop.GameLoopTime < _archerMeleeFallbackUntilTick;

        private bool ApproachForOffensiveSpell(GameLiving target)
        {
            if (target?.IsAlive != true || Body.IsCrowdControlled ||
                !PrefersCurrentSpellRange())
                return false;

            int castRange = OffensiveApproachRange(target);
            if (castRange > 0 && GameLoop.GameLoopTime < _closerSpellApproachUntilTick)
                castRange = Math.Max(300, castRange * 6 / 10);
            if (castRange <= 0 || Body.IsWithinRadius(target, castRange))
                return false;

            int desired = Math.Max(160, castRange - 100);
            // Walking into range is an intentional combat action. A resting
            // bot ignores follow orders, so an out-of-range target would
            // otherwise leave it standing still indefinitely.
            BotBody.WakeRecoveryRest();
            Body.StopAttack();
            Body.Follow(target,
                (short)Math.Clamp(desired, 160, short.MaxValue),
                (short)Math.Clamp((int)MAX_AGGRO_LIST_DISTANCE, 200, (int)short.MaxValue));
            return true;
        }

        /// <summary>
        /// A bot meleeing with a monster on it heals itself (or a group member) right after the monster's swing, when its
        /// heal finishes before the next one, as a player does (owner 2026-11-06). The melee-pressure branch below returned
        /// before the heal check, so Acolytes, Naturalists and every other melee class with a heal never healed in a fight:
        /// level-1 Acolytes died at full power to blue monsters swinging every 6 s, with a 2.2 s heal unused.
        /// </summary>
        private bool TryHealBetweenSwings()
        {
            if (!Body.CanCastHealSpells || Body.IsCasting || Body.castingComponent?.HasPendingSkillRequests == true ||
                _recentDirectAttacker is not { IsAlive: true } attacker)
                return false;
            long sinceHit = GameLoop.GameLoopTime - _recentDirectAttackTick;
            if (sinceHit > 1_000)
                return false;
            int longestHeal = 0;
            foreach (Spell heal in Body.HealSpells)
                if (heal != null && heal.Level <= Body.Level)
                    longestHeal = Math.Max(longestHeal, heal.CastTime);
            int swing = attacker.attackComponent?.AttackSpeed(attacker.ActiveWeapon) ?? 0;
            if (swing - sinceHit < longestHeal + 300)
                return false;
            return CheckHeals();
        }

        /// <summary>Paladins and Wardens chant while they melee: a hit never stops the chant.</summary>
        public static bool KeepsChantInMelee(GameBot bot) => bot?.CharacterClass != null &&
            (eCharacterClass)bot.CharacterClass.ID is eCharacterClass.Paladin or eCharacterClass.Warden;

        private bool TryEngageUnderMeleePressure(GameLiving target)
        {
            // A Necromancer's shade has no melee: when hit it keeps commanding its servant (it swung its staff and
            // cancelled its own 3 s servant commands)
            if (BotBody?.CharacterClass?.ID is (int)eCharacterClass.Necromancer or (int)eCharacterClass.Disciple)
                return false;
            if (target?.IsAlive != true || !Body.IsWithinRadius(target, Body.MeleeAttackRange + 35))
                return false;

            // Being close to an enemy is not itself an interruption. A caster
            // may legally finish a spell while the selected mob has not yet
            // attacked. SelfInterruptTime is also raised by the bot's own
            // melee swings, so only an external hit/targeted attack should
            // force the melee fallback.
            // LastInterrupter retains the last actor indefinitely; identity is
            // not proof of a current hit once the interruption window expires.
            bool actuallyUnderAttack = Body.IsBeingInterruptedByOther ||
                target == _recentDirectAttacker &&
                GameLoop.GameLoopTime - _recentDirectAttackTick <= 5_000;
            if (!actuallyUnderAttack || HasActiveCombatSongInstrument())
                return false;

            // M7 / G12: a pure caster first walks once to the group's tank, so the tank can taunt or protect it
            if (TryWalkToTankUnderAttack(target))
                return true;

            // A player cannot repeatedly begin an interruptible cast while a mob
            // is standing on them. Stop the failed cast loop and defend with the
            // class-appropriate melee weapon until range is opened again.
            if (Body.IsCasting)
                Body.StopCurrentSpellcast();

            // An active combat song survives a hit. Instrument-free chants can coexist with melee.
            if (!KeepsChantInMelee(BotBody))
                StopTwistedSong(preserveCombatSongs: true);
            SwitchToUsableMeleeWeapon();

            CheckOffensiveAbilities();
            TryUseInstantOffenseWhileMeleeing(target);
            CommandPetAttack(target);

            QueueUsableMeleeStyle();

            if (!Body.attackComponent.AttackState)
                Body.StartAttack(target);
            return true;
        }

        private GameLiving _walkedToTankFrom;
        private long _walkToTankUntil;

        /// <summary>
        /// M7 / G12: a pure caster (Wizard, Sorcerer, Eldritch, Runemaster ...) hit in melee walks to the side of a living
        /// tank of its group within 1,000 units - once per attacker (one path order, no repeats) - instead of staff-meleeing
        /// at once. Already beside the tank, with no tank near, or after that one walk, it fights back as before.
        /// </summary>
        private bool TryWalkToTankUnderAttack(GameLiving attacker)
        {
            GameBot bot = BotBody;
            // the one walk under way: let it finish (up to 3 s) before anything else turns the caster round
            if (_walkedToTankFrom == attacker)
                return GameLoop.GameLoopTime < _walkToTankUntil && Body.IsMoving;
            if (bot?.CharacterClass == null || bot.Group == null ||
                !CompanionSpellOptions165.IsCasterClass((eCharacterClass)bot.CharacterClass.ID))
                return false;
            GameLiving tank = null;
            int tankDistance = int.MaxValue;
            foreach (GameLiving member in bot.Group.GetMembersInTheGroup())
            {
                if (member == null || member == bot || !member.IsAlive || member.CurrentRegionID != bot.CurrentRegionID ||
                    !BotHealTriage165.IsTank(member))
                    continue;
                int distance = (int)bot.GetDistanceTo(member);
                if (distance < tankDistance)
                {
                    tank = member;
                    tankDistance = distance;
                }
            }
            if (!CompanionControls165.CasterWalksToTank(true, tank != null, tankDistance, false))
                return false;
            _walkedToTankFrom = attacker;
            _walkToTankUntil = GameLoop.GameLoopTime + 3_000;
            if (Body.IsCasting)
                Body.StopCurrentSpellcast();
            // the tank's side facing the caster, a little in front of it
            Point2D side = tank.GetPointFromHeading(tank.GetHeading(Body), 80);
            Body.StopAttack();
            Body.StopFollowing();
            Body.PathTo(new Vector3(side.X, side.Y, tank.Z), Body.MaxSpeed);
            return true;
        }

        /// <summary>
        /// True when the caster knows attack spells but cannot pay for any of them.
        /// A spell that costs nothing (or none known) never counts as out of power.
        /// </summary>
        public static bool OutOfPower(IEnumerable<int> attackSpellCosts, int mana)
        {
            bool any = false;
            foreach (int cost in attackSpellCosts)
            {
                if (cost <= mana)
                    return false;
                any = true;
            }
            return any;
        }

        private bool OutOfPowerForOffense()
        {
            if (Body.Spells == null || BotBody == null)
                return false;
            return OutOfPower(Body.Spells
                .Where(spell => spell != null && spell.IsHarmful && spell.Level <= Body.Level &&
                    spell.Target is eSpellTarget.ENEMY or eSpellTarget.AREA or eSpellTarget.CONE &&
                    spell.SpellType is not eSpellType.Charm and not eSpellType.Amnesia and
                        not eSpellType.Confusion and not eSpellType.Taunt &&
                    !BotControlSpells.IsControl(spell) &&   // owner 2026-11-08: power for a snare or stun is no attack power
                    !AutonomousPetSupport.RequiresManagedPetFocusShield(BotBody, spell) &&
                    !BotSpellPower.BlocksAttackerRotation(BotBody, spell) &&
                    !IsOutclassedDamageSpell(spell) &&
                    AnimistSingleTargetPolicy.AllowsAutomatedSpell(BotBody, spell))
                .Select(spell => BotBody.PowerCost(spell)), Body.Mana);
        }

        /// <summary>
        /// Bots always fight with their best damage ranks. A damage spell is skipped
        /// when a higher rank of the same kind is known, or when it is under half the
        /// level of the bot's best damage spell (the top rank of a barely trained
        /// line, e.g. a level 4 lifedrain on a level 50 Bonedancer, hits for 1). When
        /// the real spells cost more power than the bot has, it melees instead.
        /// </summary>
        public static bool IsOutclassedDamageSpell(Spell spell, IEnumerable<Spell> known, int casterLevel)
        {
            if (spell == null || !spell.IsHarmful || !BotCasterPriority.IsDamage(spell))
                return false;
            int best = 0;
            foreach (Spell other in known)
            {
                if (other == null || other == spell || !other.IsHarmful || other.Level > casterLevel ||
                    !BotCasterPriority.IsDamage(other))
                    continue;
                best = Math.Max(best, other.Level);
                if (other.Level > spell.Level && other.SpellType == spell.SpellType &&
                    other.Target == spell.Target && (other.Radius > 0) == (spell.Radius > 0) &&
                    (other.CastTime <= 0) == (spell.CastTime <= 0) && other.IsPBAoE == spell.IsPBAoE)
                    return true;
            }
            return best - spell.Level >= 5 && spell.Level * 2 < best;
        }

        /// <summary>
        /// Q30 (completeness review 2026-11-11): a spell list's outclassed damage spells at one level, worked out once
        /// (O(n^2) once per list and level, not per candidate on every combat think of a 40-70 spell caster).
        /// </summary>
        public sealed class OutclassedSpells
        {
            public readonly IReadOnlyList<Spell> List;
            public readonly int Level;
            /// <summary>The list's length when built: a list cleared or trimmed in place is worked out again.</summary>
            public readonly int Count;
            private readonly HashSet<Spell> _known = new(ReferenceEqualityComparer.Instance);
            private readonly HashSet<Spell> _outclassed = new(ReferenceEqualityComparer.Instance);

            public OutclassedSpells(IReadOnlyList<Spell> list, int level)
            {
                List = list;
                Level = level;
                Count = list.Count;
                foreach (Spell spell in list)
                {
                    if (spell == null || !_known.Add(spell))
                        continue;
                    if (IsOutclassedDamageSpell(spell, list, level))
                        _outclassed.Add(spell);
                }
            }

            /// <summary>The same answer as the static rule; a spell not in the list is worked out directly.</summary>
            public bool IsOutclassed(Spell spell) =>
                spell != null && (_known.Contains(spell) ? _outclassed.Contains(spell) : IsOutclassedDamageSpell(spell, List, Level));
        }

        private bool IsOutclassedDamageSpell(Spell spell)
        {
            if (Body?.Spells is not { } spells)
                return false;
            if (BotBody is not { } bot)
                return IsOutclassedDamageSpell(spell, spells, Body.Level);
            OutclassedSpells cache = bot.OutclassedCache;
            if (cache == null || cache.Level != Body.Level || !ReferenceEquals(cache.List, spells) || cache.Count != spells.Count)
            {
                try
                {
                    bot.OutclassedCache = cache = new OutclassedSpells(spells, Body.Level);
                }
                catch (InvalidOperationException)
                {
                    // the list changed while it was read (respec, SortSpells): the direct rule this once
                    return IsOutclassedDamageSpell(spell, spells, Body.Level);
                }
            }
            return cache.IsOutclassed(spell);
        }

        /// <summary>
        /// A caster (pet classes included) with no power for any attack spell walks
        /// in and fights with its staff alongside its pet, then casts again as soon
        /// as it can pay for a spell. A Necromancer's shade has no melee.
        /// </summary>
        private bool TryOutOfPowerMelee(GameLiving target)
        {
            if (target?.IsAlive != true || Body.IsCasting ||
                BotBody?.CharacterClass?.ID is (int)eCharacterClass.Necromancer or (int)eCharacterClass.Disciple ||
                !GameServer.ServerRules.IsAllowedToAttack(Body, target, true) ||
                !NoAttackSpellYet())
                return false;

            StopTwistedSong(preserveCombatSongs: true);
            SwitchToUsableMeleeWeapon();
            CheckOffensiveAbilities();
            CommandPetAttack(target);
            QueueUsableMeleeStyle();
            // Re-issue the attack (which restarts the melee chase) when the target ran
            // off, e.g. after aggroing a distant party member.
            if (!Body.attackComponent.AttackState || Body.TargetObject != target ||
                !IsMeleeChasing(target) && !Body.IsWithinRadius(target, Body.MeleeAttackRange))
            {
                Body.TargetObject = target;
                Body.StartAttack(target);
            }
            return true;
        }

        /// <summary>
        /// Out of power for every attack spell (owner 2026-11-05, reverting the staff charge): a caster or pet class
        /// stays where it is and waits for power, as a player does; its pet keeps fighting. It melees back and uses its
        /// instants only when something is hitting it (TryEngageUnderMeleePressure, checked before this).
        /// </summary>
        private bool TryOutOfPowerWait(GameLiving target)
        {
            // owner 2026-11-11: a caster set to "Attack spells: None" waits the same way (no staff charge, no walk-in)
            if (target?.IsAlive != true || Body.IsCasting || !OutOfPowerForOffense() && !CastsNoAttackSpells)
                return false;
            if (Body.ControlledBrain != null && !UsesDefensiveOnlyPet)
                CommandPetAttack(target);
            if (Body.attackComponent.AttackState && !Body.IsBeingInterruptedByOther)
                Body.StopAttack();
            if (Body.IsMoving && !CompanionFollowPolicy.HasFormationOrder(BotBody))
                Body.StopMoving();
            return true;
        }

        private GameLiving RecentDirectAttacker()
        {
            GameLiving attacker = _recentDirectAttacker;
            if (attacker?.IsAlive != true || attacker.ObjectState != GameObject.eObjectState.Active ||
                attacker.CurrentRegion != Body.CurrentRegion ||
                GameLoop.GameLoopTime - _recentDirectAttackTick > 5_000 ||
                !Body.IsWithinRadius(attacker, MAX_AGGRO_LIST_DISTANCE) || !CanDefendAgainst(attacker))
            {
                _recentDirectAttacker = null;
                return null;
            }

            // True 1.65: an add on this bot that a groupmate mezzed (or is mezzing) is not hit back - that broke the mez
            // at once; the target choice finishes it when nothing else is left (BotBrain.CrowdControlRespect)
            if (Classic165Mode.IsOn && (IsBreakablyControlled(attacker) || IsReservedForMez(attacker)))
                return null;

            return attacker;
        }

        private long _nextSlamSwapCheck;
        private Style _slamSwapStyle;

        /// <summary>
        /// M7 / G10: a two-hander tank swaps to its weapon and shield, Slams once, and swaps back - only when a stun is
        /// wanted (BotMeleeStylePolicy.StunWanted: a casting monster, or one on someone who is not the main tank). The
        /// swaps are ordinary weapon switches (their real swing timing); the Slam's numbers are unchanged. Checked at
        /// most once a second while fighting.
        /// </summary>
        private void TryShieldSwapForSlam()
        {
            GameBot bot = BotBody;
            if (bot == null)
                return;
            long now = GameLoop.GameLoopTime;
            GameLiving target = Body.TargetObject as GameLiving;
            if (bot.SlamSwapUntil != 0)
            {
                bool stunned = target?.effectListComponent?.ContainsEffectForEffectType(eEffect.Stun) == true;
                bool swung = _slamSwapStyle != null && Body.attackComponent?.attackAction?.LastAttackData?.Style == _slamSwapStyle;
                if (target?.IsAlive != true || CompanionControls165.SlamSwapEnds(stunned, swung, now - bot.SlamSwapStarted))
                {
                    bot.SlamSwapUntil = 0;
                    _slamSwapStyle = null;
                    if (Body.styleComponent.NextCombatStyle != null && BotMeleeStylePolicy.IsStunStyle(Body.styleComponent.NextCombatStyle))
                        Body.styleComponent.NextCombatStyle = null;
                    SwitchToUsableMeleeWeapon();   // back to the two-hander
                }
                return;
            }
            if (now < _nextSlamSwapCheck || Body.ActiveWeaponSlot != eActiveWeaponSlot.TwoHanded || target?.IsAlive != true ||
                !Body.IsWithinRadius(target, Body.MeleeAttackRange + 50) || !BotPartyRoles.IsTank(bot))
                return;
            _nextSlamSwapCheck = now + 1_000;
            DbInventoryItem right = bot.Inventory?.GetItem(eInventorySlot.RightHandWeapon);
            bool shieldSet = bot.Inventory?.GetItem(eInventorySlot.LeftHandWeapon) is { Object_Type: (int)eObjectType.Shield } &&
                             BotWeaponStats.FitsConfiguredSlot(bot, right, eInventorySlot.RightHandWeapon);
            if (!shieldSet)
                return;
            AttackData lastAttack = Body.attackComponent?.attackAction?.LastAttackData;
            Style slam = bot.Styles?.Where(style => style.WeaponTypeRequirement == (int)eObjectType.Shield &&
                                                    BotMeleeStylePolicy.IsStunStyle(style) && style.Level <= bot.Level &&
                                                    bot.Endurance >= style.EnduranceCost)
                .OrderByDescending(style => style.Level).FirstOrDefault();
            bool stunImmune = target.effectListComponent.ContainsEffectForEffectType(eEffect.StunImmunity) ||
                              target.effectListComponent.ContainsEffectForEffectType(eEffect.NPCStunImmunity) ||
                              target.effectListComponent.ContainsEffectForEffectType(eEffect.Stun);
            if (!CompanionControls165.SlamSwapWanted(CompanionOptions165.Choice(bot, "slamswap"), true, true, true, slam != null,
                    slam != null && BotMeleeStylePolicy.StunWanted(bot, target, lastAttack), stunImmune))
                return;
            bot.SlamSwapStarted = now;
            bot.SlamSwapUntil = now + CompanionControls165.SlamSwapMilliseconds;
            _slamSwapStyle = slam;
            Body.SwitchWeapon(eActiveWeaponSlot.Standard);
            Body.styleComponent.NextCombatStyle = slam;
        }

        private void QueueUsableMeleeStyle()
        {
            if (Body.styleComponent.NextCombatStyle != null || Body.ActiveWeapon == null)
                return;
            Body.styleComponent.NextCombatStyle = BotMeleeStylePolicy.Select(BotBody,
                Body.attackComponent.attackAction.LastAttackData, false);
        }

        private bool SwitchToUsableMeleeWeapon()
        {
            if (BotBody?.CharacterClass?.ID == (int)eCharacterClass.Savage)
            {
                if (!SavageBotWeaponPolicy.TryBestActiveSlot(BotBody, out eActiveWeaponSlot savageSlot))
                    return false;
                if (Body.ActiveWeaponSlot != savageSlot)
                    Body.SwitchWeapon(savageSlot);
                return true;
            }

            DbInventoryItem rightHand = BotBody?.Inventory?.GetItem(eInventorySlot.RightHandWeapon);
            DbInventoryItem leftHand = BotBody?.Inventory?.GetItem(eInventorySlot.LeftHandWeapon);
            DbInventoryItem twoHand = BotBody?.Inventory?.GetItem(eInventorySlot.TwoHandWeapon);
            bool rightUsable = BotWeaponStats.FitsConfiguredSlot(BotBody, rightHand, eInventorySlot.RightHandWeapon);
            bool leftUsable = BotWeaponStats.CanUseMelee(BotBody, leftHand);
            bool twoHandUsable = BotWeaponStats.FitsConfiguredSlot(BotBody, twoHand, eInventorySlot.TwoHandWeapon);
            // The standard attack action requires a real primary-hand weapon.
            // An off-hand-only persisted loadout is not combat-ready; weapon
            // reconciliation will move/create a legal primary weapon instead.
            bool oneHandUsable = rightUsable;
            bool preferTwoHand = BotWeaponSetPolicy.PreferTwoHanded(BotBody, twoHandUsable, oneHandUsable,
                BotBody.PlansTwoHandedSet);

            if (preferTwoHand)
            {
                if (Body.ActiveWeaponSlot != eActiveWeaponSlot.TwoHanded)
                    Body.SwitchWeapon(eActiveWeaponSlot.TwoHanded);
                return true;
            }

            if (oneHandUsable)
            {
                if (Body.ActiveWeaponSlot != eActiveWeaponSlot.Standard)
                    Body.SwitchWeapon(eActiveWeaponSlot.Standard);
                return true;
            }

            return false;
        }

        /// <summary>
        /// The two-handed slot is used only when it holds a usable weapon. A two-handed build
        /// that has no usable two-hander yet (a level 1 Skald, a Paladin or Armsman before its
        /// two-handed ability) fights with whatever one-hander it has meanwhile.
        /// </summary>
        public static bool PreferTwoHandedSlot(bool twoHandUsable, bool oneHandUsable, bool twoHandedBuild) =>
            twoHandUsable && (!oneHandUsable || twoHandedBuild);

        private void TryUseInstantOffenseWhileMeleeing(GameLiving target)
        {
            if (target?.IsAlive != true || Body.InstantHarmfulSpells == null)
                return;

            Body.TargetObject = target;
            foreach (Spell spell in Body.InstantHarmfulSpells
                         .Where(spell => spell != null && spell.Level <= Body.Level)
                         .OrderByDescending(spell => spell.Level))
            {
                if (CheckInstantOffensiveSpells(spell))
                    break;
            }
        }

        private void HoldSupportCombat()
        {
            Body.StopAttack();
            if (SupportCallsPetOff) Body.ControlledBrain?.Disengage();
            // A mez or stun on an add (TryPveAddControl) is a support action and finishes.
            if (Body.IsCasting && Body.castingComponent?.SpellHandler?.Spell is { IsHarmful: true } casting &&
                !IsAddControlSpell(casting))
                Body.StopCurrentSpellcast();
        }

        private void PerformGroupSupport()
        {
            if (CompanionFollowPolicy.SendsDruidPet(BotBody))
                TryCommandCompanionDruidPet(CalculateNextAttackTarget());
            HoldSupportCombat();
            // True 1.65: an add is mezzed before a top-up heal; a groupmate under 40% is healed first
            if (Classic165Mode.IsOn && AddControlBeforeHeals(true, LowestGroupHealthPercent()))
            {
                if (!TryPvpCrowdControl() && !TryPveAddControl() && !CheckHeals() && !TryHealerOpeningStun() &&
                    !TryShedAggroInSupport() && !TryGroupCelerity())
                    CheckSpells(eCheckSpellType.Defensive);
            }
            else if (!CheckHeals() && !TryPvpCrowdControl() && !TryPveAddControl() && !TryHealerOpeningStun() &&
                     !TryShedAggroInSupport() && !TryGroupCelerity())
                CheckSpells(eCheckSpellType.Defensive);
            if (BotBody.IsPlayerLedGroup)
            {
                if (!Body.IsCasting && Body.castingComponent?.HasPendingSkillRequests != true)
                    FollowFormation();
                else
                    FollowDuringMobileSong();
            }
        }

        private void FollowDuringMobileSong()
        {
            if (BotBody?.IsPlayerLedGroup == true && !BotBody.IsRecoveryResting &&
                !BotBody.IsIncapacitated && !BotBody.IsOnStableMasterRoute &&
                BotSongTwistPolicy.HasMobileSongCast(Body))
                FollowFormation();
        }

        private bool _archerFightsWithBow = true;
        private long _archerBowCheckedAt = long.MinValue;

        /// <summary>
        /// M7 / G6: whether this Scout, Hunter or Ranger fights with its bow - the companion's Weapon row, else by training
        /// (archery line against the melee weapon's line). The training answer is worked out at most every 30 s.
        /// </summary>
        private bool ArcherFightsWithBow()
        {
            GameBot bot = BotBody;
            if (bot?.CharacterClass == null ||
                CompanionControls165.ArcheryLine((eCharacterClass)bot.CharacterClass.ID) is not string archeryLine)
                return true;
            int choice = CompanionOptions165.Choice(bot, "weapon");
            if (choice != CompanionControls165.WeaponAutomatic)
                return CompanionControls165.ArcherFightsWithBow(choice, 0, 0);
            long now = GameLoop.GameLoopTime;
            if (now - _archerBowCheckedAt < 30_000)
                return _archerFightsWithBow;
            _archerBowCheckedAt = now;
            int melee = 0;
            foreach (eInventorySlot slot in new[] { eInventorySlot.RightHandWeapon, eInventorySlot.TwoHandWeapon })
                if (bot.Inventory?.GetItem(slot) is DbInventoryItem weapon)
                    melee = Math.Max(melee, bot.TrainedWeaponSpec((eObjectType)weapon.Object_Type, weapon.SlotPosition));
            // a Ranger's Celtic Dual is its melee line too (the no-bow dual-wield builds)
            if (bot.CharacterClass.ID == (int)eCharacterClass.Ranger)
                melee = Math.Max(melee, bot.GetBaseSpecLevel(Specs.Celtic_Dual));
            _archerFightsWithBow = CompanionControls165.ArcherFightsWithBow(choice, bot.GetBaseSpecLevel(archeryLine), melee);
            return _archerFightsWithBow;
        }

        private void TryCommandCompanionDruidPet(GameLiving target)
        {
            // M7 Pet row: Guard me keeps the Druid's pet back, Take adds sends it only at an add
            int petJob = CompanionControls165.PetJob(BotBody);
            if (petJob == CompanionControls165.PetGuardMe) return;
            if (petJob == CompanionControls165.PetTakeAdds)
                target = AutonomousPetSupport.TakeAddsTarget(BotBody, Body.ControlledBrain);
            if (!CompanionFollowPolicy.SendsDruidPet(BotBody) || target?.IsAlive != true ||
                !CanAggroTarget(target) || !CompanionEngagementMode.Allows(Body, target) ||
                !Body.IsWithinRadius(target, GROUP_DEFENSE_ASSIST_RADIUS) ||
                Body.ControlledBrain is not { Body: { IsAlive: true } } pet) return;
            // The Druid remains a support actor. Only its pet gets an attack
            // order, and an existing matching order is not restarted each tick.
            if (pet is ControlledMobBrain controlled && controlled.OrderedAttackTarget == target) return;
            pet.Attack(target);
        }

        private void FollowTravelingCompanionPerformer()
        {
            // Only player-led /spawn performers: ordinary gamebot route and
            // scheduling rules are untouched. Optional upkeep can otherwise
            // return before the FOLLOW state's mobile-song movement branch.
            if (BotBody is not { IsPlayerCompanion: true,
                    IsRecoveryResting: false, IsOnStableMasterRoute: false, IsAlive: true } bot ||
                !IsClassicSongClass(bot) || !bot.IsPlayerLedGroup || bot.IsIncapacitated ||
                bot.InCombat || HasAggro || bot.IsAttacking ||
                FSM.GetCurrentState()?.StateType != eFSMStateType.FOLLOW ||
                AssistedPlayer is not { IsAlive: true, IsMoving: true } leader ||
                leader.CurrentRegion != bot.CurrentRegion ||
                (bot.IsCasting || bot.castingComponent?.HasPendingSkillRequests == true) &&
                    !BotSongTwistPolicy.HasMobileSongCast(bot))
                return;

            FollowFormation();
            _lastPerformerFollowTick = GameLoop.GameLoopTime;
        }

        private bool IsTankClass => BotPartyRoles.IsTank(BotBody);

        public bool TryAssistAutonomousPveCombat()
        {
            if (BotBody?.IsAutonomousWorldBot != true || BotBody.IsPlayerLedGroup ||
                BotBody.IsTemporaryGroupHelper || Body.Group == null)
                return false;

            // Use the party's actual combat targets, not a new camp search.
            // The controller must then yield to the normal combat FSM so support,
            // pet attacks and movement into spell/melee range can execute.
            GameLiving target = Body.Group.GetMembersInTheGroup()
                .Where(member => member != Body && member?.IsAlive == true &&
                    member.CurrentRegion == Body.CurrentRegion &&
                    (member.InCombat || member.IsAttacking))
                .Select(member => member.TargetObject as GameLiving)
                .Where(candidate => CanDefendAgainst(candidate) &&
                    Body.IsWithinRadius(candidate, GROUP_DEFENSE_ASSIST_RADIUS))
                .OrderBy(candidate => Body.GetDistanceTo(candidate))
                .FirstOrDefault();
            if (target == null)
                return false;

            AddToAggroList(target, 1);
            FSM.SetCurrentState(eFSMStateType.AGGRO);
            return HasAggro;
        }

        /// <summary>Q10 (veteran review 2026-11-11): how long one protection answer is kept.</summary>
        public const int ProtectionSearchIntervalMs = 750;
        /// <summary>... and how often an area search may find a monster running at a groupmate that has not hit anyone
        /// yet. M5: that search is now the group fight board's one search per group every second (shared by all members),
        /// so a tank no longer runs its own.</summary>
        public const int ProtectionAreaScanIntervalMs = 3000;
        private long _nextProtectionSearchTick;
        private GameLiving _lastProtectionTarget;

        /// <summary>Pure: may the cached protection answer be reused at <paramref name="now"/>?</summary>
        public static bool ReuseProtectionAnswer(long now, long nextSearchTick, bool cachedStillValid) =>
            now < nextSearchTick && cachedStillValid;

        private GameLiving FindProtectionTarget()
        {
            if (!IsTankClass || Body?.Group == null)
                return null;

            // Q10: two 2,600-unit area searches ran on every combat think of every grouped tank. The answer is now kept
            // for 0.75 s (while it still points at a living groupmate); M5: candidates come from the group's fight board.
            long now = GameLoop.GameLoopTime;
            GameLiving cached = _lastProtectionTarget;
            bool cachedValid = cached == null ||
                cached.IsAlive && cached.ObjectState == GameObject.eObjectState.Active &&
                cached.TargetObject is GameLiving cachedVictim && cachedVictim.IsAlive && Body.Group.IsInTheGroup(cachedVictim);
            if (ReuseProtectionAnswer(now, _nextProtectionSearchTick, cachedValid))
                return cached;
            _nextProtectionSearchTick = now + ProtectionSearchIntervalMs;
            _lastProtectionTarget = SearchProtectionTarget(now);
            // M2: the add this tank goes for is its peel for a moment, so a second tank takes another one
            if (_lastProtectionTarget != null && _lastProtectionTarget.TargetObject != Body)
                BotPeelReservations.Reserve(_lastProtectionTarget, Body, now + BotPeelReservations.HoldMilliseconds);
            return _lastProtectionTarget;
        }

        private GameLiving SearchProtectionTarget(long now)
        {
            GamePlayer leader = AssistedPlayer;
            HashSet<GameLiving> protectedMembers = Body.Group.GetMembersInTheGroup()
                .Where(member => member?.IsAlive == true && member.CurrentRegionID == Body.CurrentRegionID &&
                                 Body.IsWithinRadius(member, GROUP_DEFENSE_ASSIST_RADIUS))
                .ToHashSet();
            if (protectedMembers.Count == 0)
                return null;

            List<GameLiving> pool = new(AggroList.Keys);
            if (FightBoard() is { } board)
                pool.AddRange(board.Threats);
            IEnumerable<GameLiving> candidates = pool.Where(candidate =>
                    candidate != null && Body.IsWithinRadius(candidate, 2600)).Distinct();
            // M2 (veteran review 2026-11-11): the off-tank owns the peels. A main tank with a free off-tank near leaves the
            // adds on other members to it and holds the kill; no two tanks run at the same add.
            bool leavePeelsToOffTank = Classic165Mode.IsOn && BotGroupRoles.IsMainTank(Body) &&
                BotGroupRoles.OffTankNear(Body, GROUP_DEFENSE_ASSIST_RADIUS) is GameLiving offTank &&
                !BotPeelReservations.IsBusy(offTank, now) && !offTank.IsIncapacitated;
            // A mesmerized monster still points at its last victim: never protect against it (that broke the mez).
            // True 1.65 Mode: nor against a monster on the player who is the main tank (BotMeleeStylePolicy.MayTaunt).
            GamePlayer mainTankRules = BotMeleeStylePolicy.TauntRulesPlayer(BotBody);
            return candidates
                .Where(candidate => candidate?.IsAlive == true &&
                                    candidate.TargetObject is GameLiving victim &&
                                    IsProtectionThreat(protectedMembers.Contains(victim),
                                        IsBreakablyControlled(candidate) || IsReservedForMez(candidate)) &&
                                    !(mainTankRules != null && victim == mainTankRules &&
                                      BotMeleeStylePolicy.PlayerIsMainTank(mainTankRules, candidate)) &&
                                    TankTakesPeel(victim == Body, leavePeelsToOffTank,
                                        BotPeelReservations.IsReservedByOther(candidate, Body, now)) &&
                                    CanAggroTarget(candidate))
                .OrderByDescending(candidate => candidate.TargetObject == leader)
                .ThenByDescending(candidate => candidate.TargetObject is GameBot bot && bot.Brain is BotBrain { IsHealer: true })
                .ThenBy(candidate => Body.GetDistanceTo(candidate))
                .FirstOrDefault();
        }

        /// <summary>
        /// Pure (M2): whether this tank goes for a monster on a groupmate. A monster on the tank itself always; another
        /// tank's peel never; a main tank leaves adds on others to a free off-tank nearby.
        /// </summary>
        public static bool TankTakesPeel(bool onSelf, bool leavePeelsToOffTank, bool reservedByOtherTank) =>
            onSelf || !leavePeelsToOffTank && !reservedByOtherTank;

        /// <summary>A monster on a group member is a protection threat unless it is mesmerized or rooted (don't break mez).</summary>
        public static bool IsProtectionThreat(bool targetsProtectedMember, bool breakablyControlled) =>
            targetsProtectedMember && !breakablyControlled;

        private bool TryPriorityTaunt(GameLiving target)
        {
            if (!IsTankClass || target?.TargetObject is not GameLiving victim || Body.Group == null ||
                IsBreakablyControlled(target) ||
                victim.Group != Body.Group || !Body.Group.IsInTheGroup(victim) ||
                victim.CurrentRegionID != Body.CurrentRegionID ||
                !Body.IsWithinRadius(victim, GROUP_DEFENSE_ASSIST_RADIUS))
                return false;
            // the taunt spell too follows the "Taunt" choice and the main tank (owner 2026-11-06: no taunting off the player)
            if (BotMeleeStylePolicy.TauntRulesPlayer(BotBody) != null && !BotMeleeStylePolicy.MayTaunt(BotBody, target))
                return false;
            // M2 (veteran review 2026-11-11): only the main tank holds the kill; another tank never taunts its monster off it
            if (Classic165Mode.IsOn && victim != Body && victim == BotGroupRoles.MainTank(Body))
                return false;

            if (GameLoop.GameLoopTime >= _nextPriorityTauntTick)
            {
                // Instant taunts (no cast time, like the Sluaghbinder's) are
                // sorted into InstantHarmfulSpells, so look in both lists.
                Spell taunt = (Body.HarmfulSpells ?? Enumerable.Empty<Spell>())
                    .Concat(Body.InstantHarmfulSpells ?? Enumerable.Empty<Spell>())
                    .Where(spell => spell.SpellType == eSpellType.Taunt &&
                                    Body.GetSkillDisabledDuration(spell) <= 0 &&
                                    Body.Mana >= BotBody.PowerCost(spell) &&
                                    Body.IsWithinRadius(target, Math.Max(500, spell.Range)))
                    .OrderByDescending(spell => spell.Level)
                    .FirstOrDefault();
                if (taunt != null)
                {
                    _nextPriorityTauntTick = GameLoop.GameLoopTime + 2500;
                    return Body.CastSpell(taunt, m_mobSpellLine);
                }
            }

            Style tauntStyle = BotBody.StylesTaunt?
                .Where(style => style.Level <= Body.Level)
                .OrderByDescending(style => style.Level)
                .FirstOrDefault();
            if (tauntStyle != null)
            {
                Body.styleComponent.NextCombatStyle = tauntStyle;
                Body.styleComponent.NextCombatBackupStyle = BotBody.StylesAnytime?
                    .Where(style => style.Level <= Body.Level)
                    .OrderByDescending(style => style.Level)
                    .FirstOrDefault();
            }

            return false;
        }

        /// <summary>
        /// A fight worth a long-reuse burst (Berserk; Prima treats it as one): the target at or above the bot's level, a
        /// player-like enemy, two or more monsters on the bot, or the bot below 60% health. Before 2026-11-05 Berserk went
        /// off on the first swing at anything, a grey rabbit included, and was then down for seven minutes.
        /// </summary>
        /// <summary>
        /// A melee bot sprints after its target when the monster runs at low health, as a player does so a runner does not
        /// bring friends; it stops once in reach, out of the chase, or at a third of its endurance (the styles need it).
        /// Before 2026-11-05 no bot ever sprinted (class audit, all realms).
        /// </summary>
        private void MaintainChaseSprint()
        {
            GameBot bot = BotBody;
            if (bot == null || !Classic165Mode.IsOn)
                return;
            bool want = bot.IsAlive && bot.InCombat && !PrefersCurrentSpellRange() && !bot.IsStealthed &&
                        bot.TargetObject is GameNPC runner and not GameBot && runner.IsAlive && runner.IsMoving &&
                        runner.HealthPercent < 30 && runner.TargetObject != bot &&
                        !bot.IsWithinRadius(runner, bot.MeleeAttackRange + 150) && bot.IsWithinRadius(runner, 2_000) &&
                        bot.EndurancePercent > (bot.IsSprinting ? 33 : 50);
            if (want != bot.IsSprinting && bot.Sprint(want))
                bot.StartEnduranceRegeneration();   // the drain runs on the regeneration timer
        }

        /// <summary>
        /// A cast worth Quick Cast while being hit (player practice: Quick Cast is the escape and the finisher, not a way to
        /// nuke faster): a stun, a finishing nuke on a monster under 25% health, and against a player-like enemy also a
        /// mez, root or a nuke under half health. Emergency heals are handled in CheckHeals.
        /// </summary>
        private bool QuickCastWorthwhile(Spell spell)
        {
            if (spell == null || Body.TargetObject is not GameLiving target || !target.IsAlive)
                return false;
            if (spell.SpellType == eSpellType.Stun)
                return true;
            bool playerLike = target is GamePlayer or GameBot;
            if (BotCasterPriority.IsDamage(spell))
                return target.HealthPercent < (playerLike ? 50 : 25);
            return playerLike && (spell.SpellType is eSpellType.Mesmerize or eSpellType.Mez ||
                                  spell.SpellType == eSpellType.SpeedDecrease && spell.Value >= 99);
        }

        private GameLiving _hybridOpenedOn;
        private GameLiving _holdDamageTarget;
        private long _holdDamageSince;
        private const long HoldDamageMilliseconds = 8_000;

        /// <summary>
        /// A ranged damage caster lets the tank or its own pet take the monster before it nukes (period forums: Theurgist,
        /// "wait for the tank's hits/taunts, start nuking at ~50-75% mob health"; Enchanter, "send pet, start nuking at
        /// ~85%"; Spiritmaster, "pet takes aggro, SM nukes"). Holds damage spells (debuffs and control still go) until the
        /// monster is on the group's tank (or a player) under 75% health, or on its own pet under 85% (a Spiritmaster's
        /// under 75%, as Prima says: O10); never when the
        /// monster is on the caster, never solo without a pet, never against players, and at most 8 seconds per target so
        /// a fight with nobody holding the monster is not stalled.
        /// </summary>
        private bool HoldDamageForTank()
        {
            if (!Classic165Mode.IsOn || !PrefersCurrentSpellRange() || Body.TargetObject is not GameNPC target ||
                target is GameBot || !target.IsAlive || target.TargetObject == Body)
                return false;
            GameNPC pet = Body.ControlledBrain?.Body;
            bool tankInGroup = BotMeleeStylePolicy.HasTankPartner(BotBody);
            if (!tankInGroup && pet?.IsAlive != true)
                return false;
            long now = GameLoop.GameLoopTime;
            if (_holdDamageTarget != target)
            {
                _holdDamageTarget = target;
                _holdDamageSince = now;
            }
            if (now - _holdDamageSince > HoldDamageMilliseconds)
                return false;
            GameLiving holder = target.TargetObject as GameLiving;
            if (holder == null)
                return tankInGroup;   // a pet caster opens itself (the pet goes in with it); a group waits for its tank's pull
            if (pet != null && holder == pet)
                return target.HealthPercent > BotClassAbilities165.PetHoldsDamageAbovePercent(
                    (eCharacterClass)(BotBody?.CharacterClass?.ID ?? 0));
            if (!tankInGroup)
                return false;
            bool tankHolds = holder != null && holder != Body && Body.Group?.IsInTheGroup(holder) == true &&
                             (holder is GamePlayer || holder is GameBot tankBot && BotPartyRoles.IsTank(tankBot));
            return !tankHolds || target.HealthPercent > 75;
        }

        /// <summary>
        /// True 1.65: a caster in a gamebot group saves its power under half while a group tank holds the monster
        /// (BotCasterPriority.SavePowerInGroup). A player's companion (hired, invited gamebot, /spawn helper; casters and
        /// hybrids) does too unless its "Save power" AI Option is off or it is a healer set to attack (owner 2026-11-06).
        /// The tank may be a tank-class companion, gamebot or the player.
        /// </summary>
        private bool SavePowerForGroup()
        {
            GameBot bot = BotBody;
            if (!Classic165Mode.IsOn || bot == null || Body.ManaPercent >= BotCasterPriority.SavePowerBelowPercent)
                return false;
            int savePower = bot.IsPlayerCompanion ? CompanionOptions165.Choice(bot, "savepower") : 0;
            if (savePower == CompanionSpellOptions165.SavePowerNoFinish)
                return true;
            if (Body.TargetObject is GameKeepDoor { IsAlive: true } && bot.IsPlayerCompanion)
            {
                bool healerAttacking = RoleChoice == 2 && bot.CharacterClass != null &&
                    CompanionSpellOptions165.UsesHealerRoleRow((eCharacterClass)bot.CharacterClass.ID);
                return savePower == CompanionSpellOptions165.SavePowerStrict ||
                    Body.Group != null && BotCasterPriority.CompanionSavesPower(savePower, healerAttacking);
            }
            if (Body.TargetObject is not GameNPC target || target is GameBot || !target.IsAlive)
                return false;
            // The original explicit mode keeps its PvE finishing exception.
            if (savePower == CompanionSpellOptions165.SavePowerStrict)
                return CompanionSpellOptions165.StrictSavesPower(savePower, Body.ManaPercent, target.HealthPercent);
            if (Body.Group == null)
                return false;
            if (bot.IsPlayerCompanion)
            {
                bool attackingHealer = RoleChoice == 2 && bot.CharacterClass != null &&
                    CompanionSpellOptions165.UsesHealerRoleRow((eCharacterClass)bot.CharacterClass.ID);
                if (!BotCasterPriority.CompanionSavesPower(savePower, attackingHealer))
                    return false;
            }
            else if (!bot.IsAutonomousWorldBot || bot.IsPlayerLedGroup || !PrefersCurrentSpellRange())
                return false;
            bool tankHolds = target.TargetObject is GameLiving holder && holder != Body && holder.IsAlive &&
                             Body.Group.IsInTheGroup(holder) && BotHealTriage165.IsTank(holder);
            return BotCasterPriority.SavePowerInGroup(Body.ManaPercent, target.HealthPercent, tankHolds);
        }

        /// <summary>How many others are meleeing <paramref name="target"/> (its own attacker list, no area scan); the bot's
        /// own swings do not count.</summary>
        private int OthersMeleeing(GameLiving target)
        {
            int melee = target?.attackComponent?.AttackerTracker.MeleeCount ?? 0;
            if (melee > 0 && Body.attackComponent.AttackState && Body.ActiveWeaponSlot != eActiveWeaponSlot.Distance &&
                Body.IsWithinRadius(target, Body.MeleeAttackRange + 50))
                melee--;
            return melee;
        }

        private bool HardFight(GameLiving target)
        {
            if (target is GamePlayer or GameBot || target.Level >= Body.Level || Body.HealthPercent < 60)
                return true;
            // Q9: two or more enemies on the bot, from its own attacker list (no 600-unit area search)
            int onMe = 0;
            foreach (GameLiving attacker in Body.attackComponent.AttackerTracker.Attackers)
                if (attacker.IsAlive && attacker.TargetObject == Body && attacker.InCombat && ++onMe >= 2)
                    return true;
            return false;
        }

        /// <summary>
        /// M8: a Ranger's Rapid Fire stance is on for a runner or a casting or running player-like enemy and off for
        /// everything else (BotClassAbilities165.WantsRapidFire). Only bots that own the ability look at it.
        /// </summary>
        private void MaintainRapidFire(GameLiving target, bool usingBow)
        {
            if (Body.GetAbilityLevel(Abilities.RapidFire) <= 0)
                return;
            ECSGameEffect rapidFire = EffectListService.GetAbilityEffectOnTarget(Body, eEffect.RapidFire);
            bool want = usingBow && target?.IsAlive == true && BotClassAbilities165.WantsRapidFire(
                target is GamePlayer or GameBot, target.IsMoving, target.IsCasting, target.HealthPercent, target.TargetObject == Body);
            if (want == (rapidFire != null))
                return;
            if (!want)
                rapidFire.End();
            else if (EffectListService.GetEffectOnTarget(Body, eEffect.Volley) == null &&
                     EffectListService.GetAbilityEffectOnTarget(Body, eEffect.SureShot) == null &&
                     EffectListService.GetAbilityEffectOnTarget(Body, eEffect.TrueShot) == null)
                ECSGameEffectFactory.Create(new(Body, 0, 1), static (in ECSGameEffectInitParams i) => new RapidFireECSGameEffect(i));
        }

        private long _engageSince;
        private bool _engageOnArcher;

        /// <summary>
        /// M8: a shield tank's Engage (BotClassAbilities165.EngageWorthwhile / EngageHolds). Returns true while the bot is
        /// engaged: it then closes in or faces the enemy without swinging (a swing ends Engage). Only bots that own the
        /// ability look at it; the healer check reads the group list only when everything else already holds.
        /// </summary>
        private bool MaintainEngage()
        {
            GameBot bot = BotBody;
            if (bot == null || !Classic165Mode.IsOn || bot.GetAbilityLevel(Abilities.Engage) <= 0)
                return false;
            // M7: a hired tank's "Engage" row set to Off ends an Engage it holds and starts none
            if (!CompanionControls165.EngageAllowed(bot))
            {
                if (Body.IsEngaging)
                    Body.CancelEngageEffect();
                return false;
            }
            long now = GameLoop.GameLoopTime;
            GameLiving enemy = Body.TargetObject as GameLiving;
            bool attackedRecently = enemy != null &&
                enemy.LastAttackedByEnemyTick > now - EngageAbilityHandler.ENGAGE_ATTACK_DELAY_TICK;
            if (Body.IsEngaging)
            {
                EngageECSGameEffect engage = EffectListService.GetEffectOnTarget(Body, eEffect.Engage) as EngageECSGameEffect;
                bool inReach = enemy != null && Body.IsWithinRadius(enemy, Body.MeleeAttackRange);
                bool holds = engage != null && enemy?.IsAlive == true && engage.EngageTarget == enemy &&
                    BotClassAbilities165.EngageHolds(attackedRecently, now - _engageSince, _engageOnArcher, inReach,
                        !_engageOnArcher && GroupHealerLowOnPower(bot));
                if (!holds)
                {
                    // ending it restarts the attack on the current target (EngageECSGameEffect.OnStopEffect)
                    if (engage != null)
                        engage.Cancel(false, true);
                    else
                        Body.CancelEngageEffect();
                    return false;
                }
                FaceOrCloseIn(enemy, inReach);
                return true;
            }

            if (enemy?.IsAlive != true || Body.ActiveWeaponSlot != eActiveWeaponSlot.Standard ||
                Body.ActiveLeftWeapon?.Object_Type != (int)eObjectType.Shield ||
                bot.GetBaseSpecLevel(Specs.Shields) < BotClassAbilities165.EngageShieldSpec ||
                enemy.TargetObject != Body || attackedRecently || !Body.IsWithinRadius(enemy, 2_000))
                return false;
            bool archer = enemy is GamePlayer or GameBot && enemy.ActiveWeaponSlot == eActiveWeaponSlot.Distance;
            bool otherInMelee = false;
            foreach (GameLiving attacker in Body.attackComponent.AttackerTracker.Attackers)
                if (attacker != enemy && attacker.IsAlive && attacker.TargetObject == Body &&
                    Body.IsWithinRadius(attacker, Body.MeleeAttackRange + 50))
                {
                    otherInMelee = true;
                    break;
                }
            if (!BotClassAbilities165.EngageWorthwhile(true, true, true, false, otherInMelee, archer,
                    !archer && !otherInMelee && GroupHealerLowOnPower(bot)) ||
                !GameServer.ServerRules.IsAllowedToAttack(Body, enemy, true))
                return false;
            ECSGameEffectFactory.Create(new(Body, 0, 1, null), static (in ECSGameEffectInitParams i) => new EngageECSGameEffect(i));
            if (!Body.IsEngaging)
                return false;
            _engageSince = now;
            _engageOnArcher = archer;
            FaceOrCloseIn(enemy, Body.IsWithinRadius(enemy, Body.MeleeAttackRange));
            return true;
        }

        private void FaceOrCloseIn(GameLiving enemy, bool inReach)
        {
            if (inReach)
            {
                if (Body.IsMoving)
                    Body.StopFollowing();
                Body.TurnTo(enemy);
            }
            else if (!IsMeleeChasing(enemy))
                Body.Follow(enemy, (short)Math.Max(50, Body.MeleeAttackRange - 30), 5_000);
        }

        /// <summary>A healer of the bot's group under 30% power (the group list, at most eight members).</summary>
        private static bool GroupHealerLowOnPower(GameBot bot)
        {
            if (bot.Group == null)
                return false;
            foreach (GameLiving member in bot.Group.GetMembersInTheGroup())
            {
                if (member == bot || member?.IsAlive != true || member.MaxMana <= 0)
                    continue;
                int classId = member is GameBot memberBot ? memberBot.CharacterClass?.ID ?? 0 :
                    member is GamePlayer player ? player.CharacterClass?.ID ?? 0 : 0;
                if (BotPartyRoles.IsHealingClass((eCharacterClass)classId) && member.ManaPercent < 30)
                    return true;
            }
            return false;
        }

        public void CheckOffensiveAbilities()
        {
            if (Body.AbilityCount <= 0)
                return;

            foreach (Ability ab in Body.GetAllAbilities())
            {
                if (Body.GetSkillDisabledDuration(ab) == 0)
                {
                    switch (ab.KeyName)
                    {
                        case Abilities.Berserk:
                        {
                            int berserkChoice = CompanionOptions165.Choice(BotBody, "berserk");
                            if (berserkChoice == 2)
                                break;
                            if (Body.TargetObject is GameLiving target && (berserkChoice == 1 || HardFight(target)))
                            {
                                if (Body.IsWithinRadius(Body.TargetObject, Body.MeleeAttackRange) &&
                                    GameServer.ServerRules.IsAllowedToAttack(Body, target, true))
                                {
                                    var berserk = new BerserkECSGameEffect(
                                        new ECSGameEffectInitParams(Body, BerserkAbilityHandler.DURATION, 1));

                                    // Constructing an ECS effect does not activate it. Only consume
                                    // the reuse timer after the frenzy was accepted by the effect
                                    // service, otherwise bots silently waste Berserk every seven minutes.
                                    if (berserk.Start())
                                        Body.DisableSkill(ab, 420000);
                                }
                            }
                            break;
                        }

                        case Abilities.Stag:
                        {
                            // Stag is a 15-minute ability: save it for fights that need it (the
                            // same hard-fight test Berserk uses) instead of the first grey mob.
                            // M7: a hired Hero's Stag row - on cooldown, or off
                            if (Body.TargetObject is GameLiving target &&
                                CompanionControls165.StagWanted(CompanionOptions165.Choice(BotBody, "stag"), HardFight(target)) &&
                                EffectListService.GetAbilityEffectOnTarget(Body, eEffect.Stag) == null)
                            {
                                if (Body.IsWithinRadius(Body.TargetObject, Body.MeleeAttackRange) &&
                                    GameServer.ServerRules.IsAllowedToAttack(Body, target, true))
                                {
                                    // DURATION is already in milliseconds, and constructing an ECS
                                    // effect does not start it. Start it the way Berserk does and
                                    // only spend the real reuse timer once the effect was accepted.
                                    var stag = new StagECSGameEffect(
                                        new ECSGameEffectInitParams(Body, StagAbilityHandler.DURATION, 1), ab.Level);
                                    if (stag.Start())
                                        Body.DisableSkill(ab, StagAbilityHandler.REUSE_TIMER);
                                }
                            }
                            break;
                        }

                        // M8 (veteran review 2026-11-11): the Blademaster's Triple Wield on the fights that need a burst
                        // (the Berserk test), Flurry on a player-like enemy or keep guard in reach.
                        case Abilities.Triple_Wield:
                        {
                            if (Body.TargetObject is GameLiving target &&
                                // M7: a hired Blademaster's Triple Wield row - Automatic (hard fights), on cooldown, off
                                BotClassAbilities165.TripleWieldWorthwhile(
                                    CompanionControls165.TripleWieldWanted(CompanionOptions165.Choice(BotBody, "triplewield"), HardFight(target)),
                                    Body.IsWithinRadius(target, Body.MeleeAttackRange),
                                    EffectListService.GetAbilityEffectOnTarget(Body, eEffect.TripleWield) != null) &&
                                GameServer.ServerRules.IsAllowedToAttack(Body, target, true))
                            {
                                var tripleWield = new TripleWieldECSGameEffect(
                                    new ECSGameEffectInitParams(Body, TripleWieldAbilityHandler.DURATION * 1000, 1));
                                if (tripleWield.Start())
                                    Body.DisableSkill(ab, TripleWieldAbilityHandler.REUSE_TIMER * 1000);
                            }
                            break;
                        }

                        case Abilities.Flurry:
                        {
                            if (Body.TargetObject is GameLiving target && target.IsAlive &&
                                BotClassAbilities165.FlurryTargetAllowed(target) &&
                                Body.IsWithinRadius(target, BotClassAbilities165.FlurryRange) &&
                                Body.IsObjectInFront(target, 180) &&
                                GameServer.ServerRules.IsAllowedToAttack(Body, target, true))
                            {
                                // the ability's own damage and slash resists (FlurryAbilityHandler.Damage)
                                int damage = FlurryAbilityHandler.Damage(Body, target);
                                target.TakeDamage(Body, eDamageType.Slash, damage, 0);
                                foreach (GamePlayer viewer in target.GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
                                    viewer.Out.SendSpellEffectAnimation(Body, target, 7103, 0, false, 0x01);
                                if (target is GamePlayer hitPlayer)
                                    hitPlayer.Out.SendMessage(DOL.Language.LanguageMgr.GetTranslation(hitPlayer.Client, "Skill.Ability.Flurry.HitYou",
                                        Body.Name, damage), eChatType.CT_Damaged, eChatLoc.CL_SystemWindow);
                                Body.LastAttackTickPvP = Body.CurrentRegion.Time;
                                target.LastAttackedByEnemyTickPvP = target.CurrentRegion.Time;
                                Body.DisableSkill(ab, FlurryAbilityHandler.REUSE_TIMER);
                            }
                            break;
                        }
                    }
                }
            }
        }

        #endregion

        #region Spells

        public enum eCheckSpellType
        {
            Offensive,
            Defensive,
            CrowdControl
        }

        protected static SpellLine m_mobSpellLine = SkillBase.GetSpellLine(GlobalSpellsLines.Mob_Spells);

        public virtual bool CheckSpells(eCheckSpellType type)
        {
            if (type == eCheckSpellType.Offensive)
                _clericHoldsBack = false;
            if (Body == null || Body.Spells == null || Body.Spells.Count <= 0)
                return false;

            // CastSpell starts an asynchronous cast/animation. Re-entering
            // this selector on every AI pulse while that cast is still active
            // makes Wizards, Necromancers and other ranged casters repeatedly
            // select/interrupt their own spell. Treat an active cast as the
            // completed decision for this pulse and wait for the native cast
            // pipeline to finish.
            if (Body.IsCasting || Body.castingComponent?.HasPendingSkillRequests == true)
                return true;

            // Necromancer commands finish on the shade first, then the servant
            // performs the real cast. Do not enqueue another command while the
            // servant is still casting or has a queued spell; that produced the
            // visible rapid-fire loop and repeatedly replaced pending actions.
            if (Body.ControlledBrain is NecromancerPetBrain necromancerPetBrain &&
                necromancerPetBrain.HasPendingBotCommand)
                return true;

            bool casted = false;
            List<Spell> spellsToCast = new();

            if (CheckHeals())
                return true;

            // A classic Necromancer does not cast its servant attacks directly.
            // The shade issues a PetSpell wrapper and the native
            // NecromancerPetBrain queues/executes its SubSpellID on the zombie.
            // Most wrappers are instant and therefore never entered the generic
            // interruptible-spell selector below; handle that command pipeline
            // explicitly while retaining all native cost, queue, range and
            // servant-cast behavior.
            if (TryIssueNecromancerServantCommand(type))
                return true;

            if (!casted && type == eCheckSpellType.Defensive)
            {
                if (Body.CanCastMiscSpells)
                    casted = CheckDefensiveSpells(Body.MiscSpells);
            }
            else if (!casted && type == eCheckSpellType.Offensive)
            {
                if (TryMaintainRunemasterBladeturn()) return true;
                // df-bosses L2: from where the target takes no damage, no attack spell: the caller walks in to range
                if (BeyondTargetDamageRange()) return false;
                if (TryPvpCrowdControl()) return true;
                if (TryUtilitySpells()) return true;
                if (TryBardPveAddMez()) return true;
                if (TryPveAddControl()) return true;
                if (TryGroupCelerity()) return true;
                if (TryShamanPveAddRoot()) return true;
                if (TryShortCombatShield()) return true;
                if (TryNoAttackSpellsBladeturn()) return true;
                // Q3 (veteran review 2026-11-11): a fixed heal reserve instead of a random roll that sent the Cleric
                // walking in with its mace whenever it failed. A support Cleric in a group that keeps its power stands
                // back (AttackMostWanted reads _clericHoldsBack); a Smite Cleric nukes down to a smaller reserve.
                if (BotBody.CharacterClass.ID == (int)eCharacterClass.Cleric && RoleChoice != 2)
                {
                    bool clericGrouped = Body.Group?.MemberCount > 1;
                    if (!BotCasterPriority.ClericOffenceAllowed(IsSmiteCleric, clericGrouped, Body.ManaPercent))
                    {
                        _clericHoldsBack = clericGrouped && !IsSmiteCleric && RoleChoice != 3;   // 3: "Melee and heal"
                        return false;
                    }
                }

                // Check instant spells
                bool debuffOpening = DebuffOpeningWorthwhile();
                bool holdDamage = HoldDamageForTank() || SavePowerForGroup();
                if (Body.CanCastInstantHarmfulSpells)
                {
                    IEnumerable<Spell> instantOffense = Body.InstantHarmfulSpells;
                    if (UsesMinstrelHybridCombat)
                        instantOffense = instantOffense.OrderByDescending(BotCasterPriority.IsDamage);
                    else if (debuffOpening)
                        instantOffense = instantOffense.OrderByDescending(BotCasterPriority.IsDebuff);
                    // Q18 (veteran review 2026-11-11): a melee hybrid (Champion, Valewalker, ...) put every instant debuff on
                    // every mob, greys and blues too. Now one debuff on a fight that lasts (DebuffOpeningWorthwhile:
                    // yellow or harder, grouped, or a player-like enemy), none otherwise; a player's picked debuffs stay.
                    bool rangedNow = PrefersCurrentSpellRange();
                    bool meleeDebuffAllowed = rangedNow || BotCasterPriority.MeleeHybridDebuffAllowed(debuffOpening,
                        _openingDebuffsCast, CompanionOptions165.PickedDebuffCount(BotBody));
                    foreach (Spell spell in instantOffense)
                    {
                        bool meleePressure = Body.IsBeingInterruptedByOther &&
                            Body.IsWithinRadius(Body.TargetObject, Body.MeleeAttackRange + 35);
                        if (!BotCasterPriority.AllowInstant(spell, rangedNow, meleePressure, debuffOpening))
                            continue;
                        if (!meleeDebuffAllowed && BotCasterPriority.IsDebuff(spell))
                            continue;
                        // M1: a resist debuff only for the bot's own damage types (unless the player picked it)
                        if (!PlaybookDebuffFits(spell))
                            continue;
                        if (holdDamage && BotCasterPriority.IsDamage(spell))
                            continue;
                        if (CheckInstantOffensiveSpells(spell))
                        {
                            NoteOpeningDebuff(spell);
                            return true;
                        }
                    }
                }

                if (Body.CanCastInstantMiscSpells)
                {
                    IEnumerable<Spell> instantMisc = Body.InstantMiscSpells;
                    if (BotBody.CharacterClass.ID == (int)eCharacterClass.Savage)
                        instantMisc = instantMisc.OrderBy(spell => SavageBotCombatPolicy.BuffPriority(spell.SpellType));
                    foreach (Spell spell in instantMisc)
                    {
                        if (CheckInstantDefensiveSpells(spell))
                            return true;
                    }
                }

                // Autonomous Minstrels try available instants first, then close
                // with their real weapon. No spell, cooldown, or low power must
                // leave a level-1 (or higher) Minstrel waiting at spell range.
                if (UsesMinstrelHybridCombat)
                    return false;

                // Nightshade melee focus
                if (BotBody.CharacterClass.ID == (int)eCharacterClass.Nightshade)
                    return false;

                // A point blank spell with a cast time: cast in place, or walk in while the group holds the monsters.
                if (TryPointBlankArea())
                    return true;

                // A pet being hit, power to save: the pet focus shield instead of nukes (BotPetFocusShieldPolicy).
                // True 1.65: also as the opener of a fight on the pet, at any power.
                if ((Body.ManaPercent <= BotPetFocusShieldPolicy.MaxPowerPercent || MayOpenWithPetShield) &&
                    TryStartPetFocusShield(false))
                    return true;

                // Melee hybrids prefer melee when in range
                // (owner 2026-11-05: a melee hybrid class melees in range whether or not it has learned a style yet - low
                // Thanes train Stormcalling first and stood nuking at point blank like casters)
                // From farther away they open with ONE spell, then close in (class audit 2026-11-05: Thanes chain-cast
                // Thor's Bolt from range; Prima's Thane opens with a bolt and fights in melee).
                bool meleeHybrid = !PrefersCurrentSpellRange() && (BotBody.CanUsePositionalStyles || BotBody.CanUseAnytimeStyles ||
                     IsMeleeHybridClass((eCharacterClass)BotBody.CharacterClass.ID));
                if (meleeHybrid &&
                    (Body.IsWithinRadius(Body.TargetObject, 550) || Body.ManaPercent <= 10 || _hybridOpenedOn == Body.TargetObject))
                    return false;

                // never crowd control on the monster the player ordered pulled (owner 2026-11-07: a Cabalist /pulled with
                // a root and the group stood waiting for a monster that could not come)
                // owner 2026-11-08: mez and root never go on the bot's own monster (the next hit breaks them; adds are
                // TryPveAddControl/TryShamanPveAddRoot's job), so this block is for player-like targets only
                if (BotBody.CanCastCrowdControlSpells && Body.TargetObject != ActiveOrderedPullTarget &&
                    BotPvpCrowdControl.PlayerLike(Body.TargetObject as GameLiving))
                {
                    int ccChance = 50;

                    GameLiving livingTarget = Body.TargetObject as GameLiving;

                    if (livingTarget?.TargetObject == Body && Body.IsWithinRadius(Body.TargetObject, 500))
                        ccChance = 95;

                    if ((!PrefersCurrentSpellRange() || Body.IsBeingInterruptedByOther) && Util.Chance(ccChance))
                    {
                        foreach (Spell spell in BotBody.CrowdControlSpells)
                        {
                            if (!ControlAllowedOnTarget(spell, livingTarget))
                                continue;
                            if (CanCastOffensiveSpell(spell) && !LivingHasEffect((GameLiving)Body.TargetObject, spell))
                                spellsToCast.Add(spell);
                        }
                    }
                }

                // Q2 (veteran review 2026-11-11; Prima: "forget bolts in large battles, since they'll often be blocked"):
                // under Automatic a bolt is the opener, or for a target nobody else is meleeing - never into a melee
                // scrum. A caster with no other attack spell still bolts (below).
                // M1 (veteran playbook): a Wizard or Theurgist bolts only as the opener, and no bolt goes into a monster that
                // is meleeing the caster itself (a target engaged in melee blocks bolts). The bolts and the other attack
                // spells are offered together; the playbook puts the bolt first while it is the opener.
                bool boltsAllowed = BotBody.CanCastBolts && CompanionOptions165.Choice(BotBody, "bolts") != 1 && !holdDamage;
                bool crowdControlQueued = spellsToCast.Count > 0;
                bool boltsHeldForMelee = boltsAllowed && !crowdControlQueued && Body.TargetObject is GameLiving boltTarget &&
                    (!BotCasterPriority.BoltWorthwhile(OthersMeleeing(boltTarget)) ||
                     !BotPlaybook.BoltLands(0, boltTarget.TargetObject == Body &&
                                               Body.IsWithinRadius(boltTarget, Body.MeleeAttackRange + 50)) ||
                     !BoltAllowedByPlaybook(BotBody.Playbook.BoltOpenerOnly, PlaybookDamageCastsOn(boltTarget)));
                if (boltsAllowed && !boltsHeldForMelee && !crowdControlQueued)
                {
                    foreach (Spell spell in BotBody.BoltSpells)
                    {
                        if (CanCastOffensiveSpell(spell))
                            spellsToCast.Add(spell);
                    }
                }

                if (debuffOpening && crowdControlQueued && Body.CanCastHarmfulSpells)
                    spellsToCast.AddRange(Body.HarmfulSpells.Where(spell => BotCasterPriority.IsDebuff(spell) &&
                                                                            CanCastOffensiveSpell(spell)));

                if (!crowdControlQueued)
                {
                    if (Body.CanCastHarmfulSpells)
                    {
                        foreach (Spell spell in Body.HarmfulSpells)
                        {
                            if (spell.SpellType == eSpellType.Charm ||
                                spell.SpellType == eSpellType.Amnesia ||
                                spell.SpellType == eSpellType.Confusion ||
                                spell.SpellType == eSpellType.Taunt)
                                continue;

                            if (CanCastOffensiveSpell(spell))
                                spellsToCast.Add(spell);
                        }
                    }
                }

                // a bolt held back from a melee scrum is still better than no attack spell at all
                if (boltsHeldForMelee && !spellsToCast.Exists(BotCasterPriority.IsDamage))
                    foreach (Spell spell in BotBody.BoltSpells)
                        if (CanCastOffensiveSpell(spell))
                            spellsToCast.Add(spell);

                // Nothing else to cast while the pet is being hit: the shield, above 60% power too.
                if (spellsToCast.Count == 0 && TryStartPetFocusShield(true))
                    return true;

                if (spellsToCast.Count > 0)
                {
                    // Do not roll a DoT/debuff already on this mob and mistake
                    // its refusal for a reason to abandon ranged combat.
                    spellsToCast.RemoveAll(spell => !NeedsOffensiveSpellApplication((GameLiving)Body.TargetObject, spell));
                    // True 1.65: a grouped Theurgist calls no elementals while the tanks hold everything, nor while its
                    // wave of three is up (AutonomousPetSupport)
                    if (spellsToCast.Exists(spell => spell.SpellType == eSpellType.SummonTheurgistPet) &&
                        (AutonomousPetSupport.TheurgistHoldsPets(BotBody) || AutonomousPetSupport.TheurgistWaveUp(BotBody)))
                        spellsToCast.RemoveAll(spell => spell.SpellType == eSpellType.SummonTheurgistPet);
                    // owner 2026-11-08: while holding damage only real debuffs (and an interrupt/peel stun) go, never a
                    // snare, root or nearsight on the monster the tank holds
                    if (holdDamage)
                    {
                        bool playerLikeTarget = BotPvpCrowdControl.PlayerLike(Body.TargetObject as GameLiving);
                        spellsToCast.RemoveAll(spell => BotControlSpells.HoldDamageStrips(spell, playerLikeTarget));
                    }
                    // M1: a resist debuff for a damage type the bot does not deal is no cast at all (Void Eldritch)
                    spellsToCast.RemoveAll(spell => !PlaybookDebuffFits(spell));
                    if (spellsToCast.Count == 0) return Body.IsCasting;
                    PreferPlainNukeOverSnareNuke(spellsToCast, Body.TargetObject as GameLiving);
                    // a stun kept by ControlAllowedOnTarget interrupts a cast or peels: it goes first
                    Spell urgentStun = spellsToCast.Find(BotControlSpells.IsStun);
                    if (PrefersCurrentSpellRange() &&
                        spellsToCast.Exists(spell => spell.Range > Body.MeleeAttackRange))
                        spellsToCast.RemoveAll(spell => spell.Range <= Body.MeleeAttackRange);
                    if (PrefersCurrentSpellRange() && spellsToCast.Exists(BotCasterPriority.IsDamage))
                        spellsToCast.RemoveAll(spell => !BotCasterPriority.IsDamage(spell) &&
                                                        !(debuffOpening && BotCasterPriority.IsDebuff(spell)));
                    // Debuff first, then nuke (owner 2026-11-05 class audit: casters never cast a single debuff).
                    // M1: the playbook's order - a resist debuff for the bot's own damage type first, then the others
                    Spell openingDebuff = urgentStun == null && debuffOpening && !Body.IsBeingInterrupted
                        ? ChoosePlaybookOpeningDebuff(spellsToCast)
                        : null;
                    if (openingDebuff != null && CheckOffensiveSpells(openingDebuff))
                    {
                        NoteOpeningDebuff(openingDebuff);
                        return true;
                    }
                    bool shamanHybrid = ShamanBotCombatPolicy.IsHybrid((eCharacterClass)BotBody.CharacterClass.ID);
                    // Two or more monsters in the fight within an area damage spell's reach: that spell, when its total
                    // beats the best single nuke. Area spells that would wake a mez or pull a monster are dropped here.
                    Spell areaChoice = urgentStun == null ? ChooseAreaDamage(spellsToCast, Body.TargetObject as GameLiving) : null;
                    if (urgentStun == null && spellsToCast.Count == 0) return Body.IsCasting;
                    Spell spellToCast = urgentStun ?? areaChoice ?? (shamanHybrid
                        ? spellsToCast.OrderBy(ShamanBotCombatPolicy.RotationPriority)
                            .ThenByDescending(spell => spell.Level).First()
                        : BotBody.IsEndgameCompanion
                        ? TemporaryCompanionBalance.HighestSpell(spellsToCast)
                        // M1 (veteran review 2026-11-11): the playbook order, not a random pick (BotPlaybook.ChooseAttack)
                        : ChoosePlaybookAttack(spellsToCast, Body.TargetObject as GameLiving));

                    if (spellToCast.Uninterruptible || !Body.IsBeingInterrupted)
                    {
                        casted = CheckOffensiveSpells(spellToCast);
                        NotePlaybookCast(spellToCast, casted);
                    }
                    else if (Body.IsBeingInterruptedByOther && QuickCastWorthwhile(spellToCast) &&
                             BotBody.ArmQuickCast(spellToCast))
                    {
                        casted = CheckOffensiveSpells(spellToCast);
                        NotePlaybookCast(spellToCast, casted);
                        BotBody.DisarmQuickCast();
                    }
                    else if (shamanHybrid && ShamanBotCombatPolicy.HoldMeleeForCast(true,
                                 Body.IsBeingInterruptedByOther, Body.IsBeingSelfInterrupted))
                    {
                        // A ready nuke or an expired damage over time ends the
                        // melee: stop swinging so the Shaman's own swing timer
                        // runs out and the cast starts on a following turn.
                        Body.StopAttack();
                        return true;
                    }
                    // Otherwise an interrupted ordinary cast waits for its legal window (Quick Cast is kept for the casts
                    // that matter: QuickCastWorthwhile).
                }
            }

            if (casted && type == eCheckSpellType.Offensive && Body.TargetObject is GameLiving opened &&
                !PrefersCurrentSpellRange() && !Body.IsWithinRadius(opened, 550))
                _hybridOpenedOn = opened;   // the ranged opener is spent on this target: melee from here on
            return casted || Body.IsCasting;
        }

        private GameLiving _openingDebuffTarget;
        private int _openingDebuffsCast;

        private bool DebuffOpeningWorthwhile()
        {
            if (Body.TargetObject is not GameLiving target || !target.IsAlive)
                return false;
            if (_openingDebuffTarget != target)
            {
                _openingDebuffTarget = target;
                _openingDebuffsCast = 0;
            }
            int choice = CompanionOptions165.Choice(BotBody, "debuff");   // 1 always, 2 never
            if (choice == 2)
                return false;
            // debuffs the player marked "Use" are all cast (any number); otherwise the companion's own two at most
            int picked = CompanionOptions165.PickedDebuffCount(BotBody);
            return BotCasterPriority.DebuffOpeningWorthwhile(Body.Level, Body.ManaPercent, Body.Group?.MemberCount > 1 || choice == 1,
                target.Level, target.HealthPercent, target is GamePlayer or GameBot, _openingDebuffsCast,
                picked > 0 ? picked : BotCasterPriority.OpeningDebuffs);
        }

        /// <summary>The player's debuff choices (AI Options, owner 2026-11-08): never a debuff marked Skip or with
        /// "Debuff before nuking" on Never, and only the ones marked Use when any is. Gamebots: every debuff.</summary>
        private bool DebuffChosen(Spell spell) =>
            !BotCasterPriority.IsDebuff(spell) ||
            CompanionOptions165.DebuffAllowed(CompanionOptions165.Choice(BotBody, "debuff"),
                CompanionOptions165.DebuffLineChoice(BotBody, spell), CompanionOptions165.PickedDebuffCount(BotBody) > 0);

        private void NoteOpeningDebuff(Spell spell)
        {
            if (BotCasterPriority.IsDebuff(spell) && Body.TargetObject == _openingDebuffTarget)
                _openingDebuffsCast++;
        }

        /// <summary>A seated Necromancer rests to this much power before it gifts again (out of combat).</summary>
        /// <summary>A cast-time heal waits while the bot's own last swing still blocks casting (owner 2026-11-08: trying
        /// anyway turned it to the groupmate and back over and over). Hits from enemies are handled before this.</summary>
        public static bool HealWaitsForSwing(bool instant, bool uninterruptible, bool selfInterrupted, bool interruptedByOther) =>
            !instant && !uninterruptible && selfInterrupted && !interruptedByOther;

        public const int GiftAfterRestPercent = 90;

        /// <summary>In a fight a Necromancer gifts down to this much of its own power (period: "3 transfers and the
        /// necros near enough oop", then the power drain refills it); out of a fight it keeps half.</summary>
        public const int GiftFloorInCombatPercent = 30;

        /// <summary>
        /// A Deathsight necromancer as a power battery (owner 2026-11-05, reset-day S32): the servant's "Gift of ..."
        /// command gives power to a group member who uses it and is under 60%, healers first, in or out of combat - but
        /// only while the necromancer keeps enough of its own power after paying for it (30% in a fight, half out of it).
        /// </summary>
        private bool TryNecromancerPowerGift()
        {
            if (BotBody?.CharacterClass?.ID is not ((int)eCharacterClass.Necromancer or (int)eCharacterClass.Disciple) ||
                Body.Group == null || Body.ControlledBrain is not NecromancerPetBrain servantBrain ||
                servantBrain.Body?.IsAlive != true || servantBrain.Body.ObjectState is not GameObject.eObjectState.Active ||
                GameLoop.GameLoopTime < _nextNecromancerCommandTick || Body.IsCasting || servantBrain.HasPendingBotCommand ||
                Body.Spells == null)
                return false;
            // Out of a fight (owner 2026-11-08: a sit-and-cast loop): no gift while the player is walking off (the servant
            // holds still for its 3 s cast), and a seated Necromancer finishes most of its rest first, then gifts in one go
            // instead of standing up for every gift and sitting down again.
            bool fighting = Body.InCombat || HasAggro || Body.IsAttacking;
            if (!fighting &&
                (CompanionFollowPolicy.WaitingForLeaderToStop(BotBody) || BotBody.IsRecoveryResting && Body.ManaPercent < GiftAfterRestPercent))
                return false;
            int floor = Body.MaxMana * (fighting ? GiftFloorInCombatPercent : 50) / 100;
            if (fighting)   // and enough left for the power drain that refills it (owner 2026-11-08)
                floor = Math.Max(floor, Body.Spells.Where(spell => spell?.SpellType == eSpellType.PetSpell && spell.SubSpellID > 0 &&
                        spell.Level <= Body.Level && SkillBase.GetSpellByID(spell.SubSpellID)?.SpellType == eSpellType.PowerDrainPet)
                    .Select(spell => BotBody.PowerCost(spell)).DefaultIfEmpty(0).Min());
            Spell gift = Body.Spells
                .Where(spell => spell != null && spell.SpellType == eSpellType.PetSpell && spell.SubSpellID > 0 &&
                                spell.Level <= Body.Level && Body.GetSkillDisabledDuration(spell) <= 0 &&
                                SkillBase.GetSpellByID(spell.SubSpellID)?.SpellType == eSpellType.PowerTransferPet &&
                                Body.Mana - BotBody.PowerCost(spell) >= floor)
                .OrderByDescending(spell => spell.Level).FirstOrDefault();
            if (gift == null)
                return false;
            int range = Math.Max(1000, gift.CalculateEffectiveRange(Body));
            GameLiving needy = Body.Group.GetMembersInTheGroup()
                .Where(member => member != Body && member.IsAlive && member.MaxMana > 0 && member.ManaPercent < 60 &&
                                 member.CurrentRegionID == Body.CurrentRegionID && Body.IsWithinRadius(member, range) &&
                                 !(member is GameBot { CharacterClass: { } cls } &&
                                   (eCharacterClass)cls.ID is eCharacterClass.Necromancer or eCharacterClass.Disciple))
                .OrderBy(member => ((member as GamePlayer)?.CharacterClass?.ID ?? (member as GameBot)?.CharacterClass?.ID) is int id &&
                                   BotPartyRoles.IsHealingClass((eCharacterClass)id) ? 0 : 1)
                .ThenBy(member => member.ManaPercent)
                .FirstOrDefault();
            if (needy == null)
                return false;
            GameObject oldTarget = Body.TargetObject;
            Body.TargetObject = needy;
            bool cast = Body.CastSpell(gift, ResolveKnownSpellLine(gift), false);
            Body.TargetObject = oldTarget;
            _nextNecromancerCommandTick = GameLoop.GameLoopTime + (cast ? 1_500 : 3_000);
            return cast;
        }

        /// <summary>
        /// Q30 (completeness review 2026-11-11): a Necromancer's servant commands (wrapper and payload), its best damage
        /// command level and its power drains, built once per spell list and level instead of on every command tick (a
        /// SkillBase lookup per command, LINQ and a HashSet each time). The cooldown, power and range filters stay live.
        /// </summary>
        public sealed class ServantCommandTable
        {
            public readonly List<Spell> List;
            public readonly int Count, Level;
            public readonly Dictionary<int, Spell> PayloadById = new();
            public readonly Spell[] Drains;
            public readonly HashSet<int> DrainIds = new();
            public readonly int BestDamageLevel;

            public ServantCommandTable(List<Spell> list, int level, Func<int, Spell> payloadOf)
            {
                List = list;
                Count = list?.Count ?? 0;
                Level = level;
                var drains = new List<Spell>();
                foreach (Spell spell in list ?? [])
                {
                    if (spell?.SpellType != eSpellType.PetSpell || spell.SubSpellID <= 0 || spell.Level > level ||
                        PayloadById.ContainsKey(spell.ID) || payloadOf(spell.SubSpellID) is not { } payload)
                        continue;
                    PayloadById[spell.ID] = payload;
                    if (spell.IsHarmful && !IsServantDebuff(payload))
                        BestDamageLevel = Math.Max(BestDamageLevel, spell.Level);
                    if (payload.SpellType == eSpellType.PowerDrainPet)
                    {
                        drains.Add(spell);
                        DrainIds.Add(spell.ID);
                    }
                }
                Drains = drains.ToArray();
            }

            public bool Matches(List<Spell> list, int level) =>
                ReferenceEquals(list, List) && (list?.Count ?? 0) == Count && level == Level;
        }

        private ServantCommandTable ServantCommandsOf(GameBot bot)
        {
            ServantCommandTable table = bot.ServantCommands;
            if (table == null || !table.Matches(bot.Spells, bot.Level))
                bot.ServantCommands = table = new ServantCommandTable(bot.Spells, bot.Level, SkillBase.GetSpellByID);
            return table;
        }

        private bool TryIssueNecromancerServantCommand(eCheckSpellType type)
        {
            if (BotBody?.CharacterClass?.ID is not ((int)eCharacterClass.Necromancer or (int)eCharacterClass.Disciple) ||
                type != eCheckSpellType.Offensive ||
                Body.ControlledBrain is not NecromancerPetBrain servantBrain ||
                servantBrain.Body?.IsAlive != true ||
                servantBrain.Body.ObjectState is not GameObject.eObjectState.Active ||
                GameLoop.GameLoopTime < _nextNecromancerCommandTick ||
                Body.IsCasting || servantBrain.HasPendingBotCommand)
                return false;

            if (TryNecromancerPowerGift())
                return true;

            GameLiving target;
            IEnumerable<Spell> commands = Body.Spells
                .Where(spell => spell != null && spell.SpellType == eSpellType.PetSpell &&
                                spell.SubSpellID > 0 && spell.Level <= Body.Level &&
                                Body.GetSkillDisabledDuration(spell) <= 0 &&
                                Body.Mana >= BotBody.PowerCost(spell));

            target = Body.TargetObject as GameLiving;
            if (target?.IsAlive != true || !GameServer.ServerRules.IsAllowedToAttack(Body, target, true))
                return false;

            // Owner 2026-11-08: out of power Aldith spammed the level 1 Tormenting Cackle (the only command she could still
            // afford) at range and never got back to her drains. A damage command far below her best one is never cast; a
            // Deathsight Necromancer keeps enough power for its power drain, and when it cannot afford the drain (or Save
            // power holds it back) it sends the servant into melee and waits for power, draining again once it can.
            ServantCommandTable table = ServantCommandsOf(BotBody);
            int bestDamageLevel = table.BestDamageLevel;
            bool deathsight = AutonomousPetSupport.IsDeathsightSpec(BotBody.GetBaseSpecLevel(Specs.Deathsight),
                BotBody.GetBaseSpecLevel(Specs.Painworking), BotBody.GetBaseSpecLevel(Specs.Death_Servant));
            int drainCost = 0;
            if (deathsight && table.Drains.Length > 0)
            {
                drainCost = int.MaxValue;
                foreach (Spell drain in table.Drains)
                    drainCost = Math.Min(drainCost, BotBody.PowerCost(drain));
            }
            bool savingPower = deathsight && SavePowerForGroup();
            if (DeathsightShortOfPower(deathsight, Body.Mana, drainCost, savingPower))
                BotBody.ServantSentInOn = target;
            HashSet<int> drains = table.DrainIds;
            commands = commands.Where(spell =>
                table.PayloadById.TryGetValue(spell.ID, out Spell payload) &&
                !ServantDamageOutclassed(spell.Level, !IsServantDebuff(payload), bestDamageLevel) &&
                (drains.Contains(spell.ID) || !savingPower && Body.Mana - BotBody.PowerCost(spell) >= drainCost));

            // Helpful servant commands are exclusively maintained by
            // AutonomousPetSupport. Keeping them out of this combat selector
            // prevents a second cooldown/queue from recasting buffs or treating
            // one-shot power transfer as idle upkeep.
            GameLiving servant = servantBrain.Body;
            // a held-back servant casts from where it stands: its payload range counts from the servant, not the shade
            bool heldBack = AutonomousPetSupport.ServantHoldsBack(Body);
            commands = commands.Where(spell => spell.IsHarmful &&
                Body.IsWithinRadius(target, Math.Max(1, spell.CalculateEffectiveRange(Body))) &&
                (!heldBack || ServantInPayloadRange(servant, target, spell)) &&
                ServantCanReach(target, spell) &&
                ServantPayloadNeedsApplication(target, spell));

            // One command per servant line (the hardest-hitting, then highest learned rank of each payload kind), so every
            // line gets used. Taking the four highest-level commands overall kept out the lower-level area debuffs (Shroud
            // of Affliction, Vortex of Anguish) whenever damage lines had higher ranks (owner report 2026-10-15). By damage
            // first: the level 16 Cry of the Dead (35) no longer replaces the stronger Soul Evacuation (49) lifedrain.
            Spell[] best = commands
                .GroupBy(ServantCommandKind)
                .Select(line => line.OrderByDescending(spell => SkillBase.GetSpellByID(spell.SubSpellID)?.Damage ?? 0)
                    .ThenByDescending(spell => spell.Level).ThenByDescending(spell => spell.SubSpellID).First())
                .ToArray();
            // the player's debuff choices (AI Options): Skip / "Debuff before nuking" Never removes a debuff; with any
            // marked Use only those go, each one until it is on the monster (owner 2026-11-08)
            int debuffMaster = CompanionOptions165.Choice(BotBody, "debuff");
            bool debuffsPicked = CompanionOptions165.PickedDebuffCount(BotBody) > 0;
            best = best.Where(spell => SkillBase.GetSpellByID(spell.SubSpellID) is not { } payload || !IsServantDebuff(payload) ||
                IsServantSnare(payload) ||
                CompanionOptions165.DebuffAllowed(debuffMaster, CompanionOptions165.DebuffLineChoice(BotBody, spell), debuffsPicked))
                .ToArray();
            if (best.Length == 0)
                return false;

            // Owner 2026-11-08 (Aldith snared and debuffed the whole fight and never drained) and period play: the armour
            // or stat debuff only when a groupmate melees the monster ("only useful if someone else will be hitting the
            // mob"), one at a time; the snare only on a runner; otherwise the damage.
            bool othersMelee = target.TargetObject is GameLiving holder && holder.IsAlive && holder != Body &&
                               holder != servant && Body.Group?.IsInTheGroup(holder) == true;
            // a runner: moving at low health and not in melee with anyone (fleeing)
            bool runner = target.IsMoving && target.HealthPercent < 35 &&
                          !(target.TargetObject is GameLiving victim && victim.IsAlive && target.IsWithinRadius(victim, 300));
            bool debuffOn = Body.Spells.Any(spell => spell?.SpellType == eSpellType.PetSpell && spell.SubSpellID > 0 &&
                SkillBase.GetSpellByID(spell.SubSpellID) is { } payload && IsServantDebuff(payload) && !IsServantSnare(payload) &&
                LivingHasEffect(target, payload));
            // "Debuff before nuking: Always" debuffs solo too; debuffs marked Use are all put on, not just one
            Spell command = ChooseServantCommand(best, SkillBase.GetSpellByID, othersMelee || debuffMaster == 1,
                debuffOn && !debuffsPicked, runner);
            if (command == null)
                return false;
            // The Necromancer's Quick Cast is Facilitate Painworking (owner 2026-11-05): an instant servant command that
            // keeps the servant's casts from being interrupted for 6 s (30 s reuse; normal cast times, normal power). Used
            // as a player does: when the servant is being hit and a cast-time command is next.
            if (SkillBase.GetSpellByID(command.SubSpellID) is { CastTime: > 0 } && servant.IsBeingInterruptedByOther &&
                !servant.effectListComponent.ContainsEffectForEffectType(eEffect.FacilitatePainworking) &&
                Body.Spells.FirstOrDefault(spell => spell != null && spell.SpellType == eSpellType.PetSpell &&
                    spell.Level <= Body.Level && Body.GetSkillDisabledDuration(spell) <= 0 &&
                    SkillBase.GetSpellByID(spell.SubSpellID)?.SpellType == eSpellType.FacilitatePainworking) is Spell facilitate &&
                Body.CastSpell(facilitate, ResolveKnownSpellLine(facilitate), false))
            {
                _nextNecromancerCommandTick = GameLoop.GameLoopTime + 250;
                return true;
            }
            GameObject oldTarget = Body.TargetObject;
            Body.TargetObject = target;
            bool cast = Body.CastSpell(command, ResolveKnownSpellLine(command), false);
            Body.TargetObject = oldTarget;
            _nextNecromancerCommandTick = GameLoop.GameLoopTime + (cast ? 750 : 2_000);
            return cast;

            // A point-blank payload (Billowing Death and its ranks: range 0, radius 350) bursts around the servant, so
            // the servant, not the shade, must stand by the target. It was sent from 1,500 away and hit nothing.
            bool ServantCanReach(GameLiving commandTarget, Spell wrapper)
            {
                Spell payload = SkillBase.GetSpellByID(wrapper.SubSpellID);
                if (payload == null || payload.Range > 0 || payload.Radius <= 0)
                    return true;
                return servant.CurrentRegionID == commandTarget.CurrentRegionID &&
                       servant.IsWithinRadius(commandTarget, Math.Max(100, payload.Radius * 8 / 10));
            }

            static bool ServantInPayloadRange(GameLiving pet, GameLiving commandTarget, Spell wrapper)
            {
                Spell payload = SkillBase.GetSpellByID(wrapper.SubSpellID);
                return payload == null || payload.Range <= 0 || pet.IsWithinRadius(commandTarget, payload.Range);
            }

            bool ServantPayloadNeedsApplication(GameLiving commandTarget, Spell wrapper)
            {
                Spell payload = SkillBase.GetSpellByID(wrapper.SubSpellID);
                if (payload == null)
                    return false;

                if (payload.IsHealing)
                    return commandTarget.HealthPercent < 88;

                return payload.Duration <= 0 || !LivingHasEffect(commandTarget, payload);
            }
        }

        /// <summary>A servant command's line: what its payload does, its damage type, and whether it is single-target,
        /// a targeted area or a burst around the servant.</summary>
        public static string ServantCommandKind(Spell wrapper)
        {
            Spell payload = SkillBase.GetSpellByID(wrapper.SubSpellID);
            if (payload == null)
                return $"wrapper:{wrapper.ID}";
            string shape = payload.Radius <= 0 ? "single" : payload.Range <= 0 ? "pbaoe" : "area";
            return $"{payload.SpellType}/{payload.DamageType}/{shape}";
        }

        /// <summary>
        /// The servant command to send, from one ready command per line (owner 2026-11-08: "why would she not use her best
        /// ability"; "a death sight necro should cast power vacuums and then give power to their group"; period forums:
        /// the snare on runners, the armour debuff only when others melee). The snare on a runner first; then one debuff
        /// (the highest rank) while a groupmate melees and none is on the monster yet; then the damage: the power drain
        /// first (its power feeds the group through the power gift), else the hardest-hitting payload. No random picks.
        /// </summary>
        public static Spell ChooseServantCommand(IReadOnlyCollection<Spell> ready, Func<int, Spell> payloadOf, bool othersMelee,
            bool debuffOn, bool runner)
        {
            var commands = ready.Where(spell => spell != null)
                .Select(spell => (Wrapper: spell, Payload: payloadOf(spell.SubSpellID)))
                .Where(command => command.Payload != null).ToList();
            var snares = commands.Where(command => IsServantSnare(command.Payload)).ToList();
            var debuffs = commands.Where(command => IsServantDebuff(command.Payload) && !IsServantSnare(command.Payload)).ToList();
            var damage = commands.Where(command => !IsServantDebuff(command.Payload)).ToList();
            Spell Highest(IEnumerable<(Spell Wrapper, Spell Payload)> pool) => pool
                .OrderByDescending(command => command.Wrapper.Level).ThenByDescending(command => command.Wrapper.SubSpellID)
                .Select(command => command.Wrapper).FirstOrDefault();
            if (runner && snares.Count > 0)
                return Highest(snares);
            if (othersMelee && !debuffOn && debuffs.Count > 0)
                return Highest(debuffs);
            Spell hit = damage
                .OrderByDescending(command => command.Payload.SpellType == eSpellType.PowerDrainPet)
                .ThenByDescending(command => command.Payload.Damage)
                .ThenByDescending(command => command.Wrapper.Level)
                .Select(command => command.Wrapper).FirstOrDefault();
            return hit ?? (othersMelee ? Highest(debuffs) : null);
        }

        /// <summary>
        /// A damage command far below the Necromancer's best one (owner 2026-11-08: the level 1 Tormenting Cackle at level
        /// 19): at least 5 levels and less than half the level of its best damage command. Debuffs are never outclassed.
        /// </summary>
        public static bool ServantDamageOutclassed(int commandLevel, bool damagePayload, int bestDamageLevel) =>
            damagePayload && bestDamageLevel - commandLevel >= 5 && commandLevel * 2 < bestDamageLevel;

        /// <summary>A Deathsight Necromancer that cannot pay for its power drain, or that Save power holds back: its servant
        /// goes into melee while the shade waits for power (owner 2026-11-08).</summary>
        public static bool DeathsightShortOfPower(bool deathsight, int mana, int drainCost, bool savingPower) =>
            deathsight && (savingPower || drainCost > 0 && mana < drainCost);

        /// <summary>A snare payload (Constricting Pain, Vortex of Anguish): only for a runner.</summary>
        public static bool IsServantSnare(Spell payload) =>
            payload.SpellType is eSpellType.SpeedDecrease or eSpellType.UnbreakableSpeedDecrease;

        /// <summary>Stat, resist, armor and speed debuffs (Shroud of Affliction, Vortex of Anguish and the like).</summary>
        public static bool IsServantDebuff(Spell payload) =>
            IsServantSnare(payload) ||
            payload.SpellType.ToString().Contains("Debuff", StringComparison.Ordinal);

        private SpellLine ResolveKnownSpellLine(Spell spell)
        {
            if (spell == null)
                return m_mobSpellLine;
            if (_knownSpellLines.TryGetValue(spell.ID, out SpellLine cached))
                return cached;

            foreach (var tuple in BotBody.GetAllUsableListSpells())
            {
                if (tuple?.Item1 == null || tuple.Item2 == null ||
                    !tuple.Item2.OfType<Spell>().Any(known => known.ID == spell.ID))
                    continue;
                _knownSpellLines[spell.ID] = tuple.Item1;
                return tuple.Item1;
            }

            return m_mobSpellLine;
        }

        protected bool CanCastOffensiveSpell(Spell spell)
        {
            if (IsServantCommandOutsideItsPath(spell) || spell != null && !DebuffChosen(spell) || SpellOptionBlocks(spell))
                return false;
            if (spell == null || spell.Level > Body.Level || IsOutclassedDamageSpell(spell) ||
                Body.TargetObject is not GameLiving target || !target.IsAlive ||
                !BardBotCrowdControlPolicy.AllowsOrdinaryOffense((eCharacterClass)BotBody.CharacterClass.ID, spell.SpellType) ||
                !ShamanBotCombatPolicy.AllowsOrdinaryOffense((eCharacterClass)BotBody.CharacterClass.ID, spell) ||
                !ControlAllowedOnTarget(spell, target) ||   // owner 2026-11-08: snares, stuns, roots by their own rules
                BotSpellPower.BlocksAttackerRotation(BotBody, spell) ||
                !NeedsOffensiveSpellApplication(target, spell) ||
                Body.GetSkillDisabledDuration(spell) > 0 || Body.Mana < BotBody.PowerCost(spell))
                return false;

            if (spell.CastTime > 0 && spell.Target is eSpellTarget.ENEMY or eSpellTarget.AREA or eSpellTarget.CONE)
            {
                int range = spell.Target == eSpellTarget.AREA && spell.Range <= 0
                    ? Math.Max(1, spell.Radius)
                    : Math.Max(1, spell.CalculateEffectiveRange(Body));
                return Body.IsWithinRadius(target, range);
            }

            return false;
        }

        protected bool CanCastDefensiveSpell(Spell spell)
        {
            if (CompanionFollowPolicy.DeferBuff(BotBody, spell)) return false;
            if (spell == null || spell.IsHarmful || spell.Level > Body.Level ||
                !BotBody.CanAffordConcentration(spell) ||
                BotSongTwistPolicy.IsReservedPulse(BotBody, spell) ||
                Body.Mana < BotBody.PowerCost(spell) ||
                BladeturnBlocked(spell))   // df-bosses L3
                return false;

            if (spell.CastTime > 0 && Body.IsBeingInterrupted && !spell.Uninterruptible)
                return false;

            if (Body.GetSkillDisabledDuration(spell) > 0)
                return false;

            return true;
        }

        protected virtual bool CheckOffensiveSpells(Spell spell)
        {
            if (spell == null || Body.Mana < BotBody.PowerCost(spell))
                return false;

            if (spell.NeedInstrument && !TryEquipRealInstrument(BotBody, spell.InstrumentRequirement))
                return false;

            bool casted = false;

            if (Body.TargetObject is GameLiving living && NeedsOffensiveSpellApplication(living, spell))
            {
                AimGroundSpell(spell, living);
                casted = Body.CastSpell(spell, m_mobSpellLine);
            }

            return casted;
        }

        protected virtual bool CheckInstantDefensiveSpells(Spell spell)
        {
            if (CompanionFollowPolicy.DeferBuff(BotBody, spell)) return false;
            if (spell == null || spell.Level > Body.Level || Body.Mana < BotBody.PowerCost(spell) ||
                !BotBody.CanAffordConcentration(spell) ||
                BotSongTwistPolicy.IsReservedPulse(BotBody, spell) ||
                spell.HasRecastDelay && Body.GetSkillDisabledDuration(spell) > 0 ||
                BladeturnBlocked(spell))   // df-bosses L3
                return false;

            bool castSpell = false;

            switch (spell.SpellType)
            {
                case eSpellType.SavageEnduranceHeal:
                    castSpell = SavageBotCombatPolicy.ShouldUseEnduranceHeal(
                        Body.HealthPercent, Body.EndurancePercent);
                    break;

                case eSpellType.SavageCrushResistanceBuff:
                case eSpellType.SavageSlashResistanceBuff:
                case eSpellType.SavageThrustResistanceBuff:
                case eSpellType.SavageCombatSpeedBuff:
                case eSpellType.SavageDPSBuff:
                case eSpellType.SavageParryBuff:
                case eSpellType.SavageEvadeBuff:
                    int activeSavageBuffs = Body.effectListComponent.GetEffects(eEffect.SavageBuff)
                        .Count(effect => effect?.SpellHandler?.Spell != null);
                    // Health-cost buffs only once the Savage is actually at its
                    // target, never while still walking up (a level 1 Savage
                    // arrived half dead), and just one of them below level 10.
                    bool atTarget = Body.TargetObject is GameLiving buffTarget && buffTarget.IsAlive &&
                        Body.IsWithinRadius(buffTarget, Body.MeleeAttackRange + 150);
                    if (atTarget &&
                        SavageBotCombatPolicy.ShouldUseBuff(spell.SpellType, Body.HealthPercent, activeSavageBuffs, Body.Level,
                            ((GameLiving)Body.TargetObject).HealthPercent) &&
                        !LivingHasEffect(Body, spell))
                        castSpell = true;
                    break;

                case eSpellType.SummonHunterPet:
                    // Summons have an effect on the pet, not the caster. An
                    // owner-effect check incorrectly created a new pet every turn.
                    castSpell = Body.ControlledBrain == null && CompanionOptions165.Choice(BotBody, "hunterpet") != 1 &&
                                !AutonomousPetSupport.WaitsToSummon(Body);
                    break;

                case eSpellType.CombatHeal:
                case eSpellType.DamageAdd:
                case eSpellType.PaladinArmorFactorBuff:
                case eSpellType.DexterityQuicknessBuff:
                case eSpellType.EnduranceRegenBuff:
                case eSpellType.CombatSpeedBuff:
                case eSpellType.AblativeArmor:
                case eSpellType.Bladeturn:
                case eSpellType.OffensiveProc:
                    if (spell.SpellType == eSpellType.CombatSpeedBuff)
                    {
                        if (Body.TargetObject != null && !Body.IsWithinRadius(Body.TargetObject, Body.MeleeAttackRange))
                            break;
                    }

                    if (!LivingHasEffect(Body, spell))
                        castSpell = true;
                    break;
            }

            return castSpell && Body.CastSpell(spell, m_mobSpellLine);
        }

        /// <summary>A Necromancer's servant commands go only through TryIssueNecromancerServantCommand (owner 2026-11-08:
        /// the generic offense cast the commands that path had just ruled out).</summary>
        private bool IsServantCommandOutsideItsPath(Spell spell) =>
            spell?.SpellType == eSpellType.PetSpell && Body.ControlledBrain is NecromancerPetBrain;

        protected virtual bool CheckInstantOffensiveSpells(Spell spell)
        {
            if (IsServantCommandOutsideItsPath(spell) || spell != null && !DebuffChosen(spell) || SpellOptionBlocks(spell))
                return false;
            if (spell == null || IsOutclassedDamageSpell(spell) || Body.Mana < BotBody.PowerCost(spell) ||
                !BardBotCrowdControlPolicy.AllowsOrdinaryOffense((eCharacterClass)BotBody.CharacterClass.ID, spell.SpellType) ||
                spell.HasRecastDelay && Body.GetSkillDisabledDuration(spell) > 0)
                return false;
            // owner 2026-11-08: an instant stun only interrupts or peels, an instant snare only stops a runner, no mez on
            // the bot's own monster (Minstrel Stunning Yell, Healer Paralyzing Glance, Skald/Champion snares)
            if (!ControlAllowedOnTarget(spell, Body.TargetObject as GameLiving))
                return false;

            bool castSpell = false;

            switch (spell.SpellType)
            {
                case eSpellType.DirectDamage:
                case eSpellType.NightshadeNuke:
                case eSpellType.Lifedrain:
                case eSpellType.DexterityDebuff:
                case eSpellType.DexterityQuicknessDebuff:
                case eSpellType.StrengthDebuff:
                case eSpellType.StrengthConstitutionDebuff:
                case eSpellType.ConstitutionDebuff:   // Animist Sap Strength (owner 2026-11-05 class audit)
                case eSpellType.CombatSpeedDebuff:
                case eSpellType.DamageOverTime:
                case eSpellType.MeleeDamageDebuff:
                case eSpellType.ArmorAbsorptionDebuff:
                case eSpellType.AllStatsPercentDebuff:
                case eSpellType.CrushSlashThrustDebuff:
                case eSpellType.EffectivenessDebuff:
                case eSpellType.Disease:
                case eSpellType.ArmorFactorDebuff:
                case eSpellType.SlashResistDebuff:
                case eSpellType.CrushResistDebuff:
                case eSpellType.ThrustResistDebuff:
                case eSpellType.BodyResistDebuff:
                case eSpellType.SpiritResistDebuff:
                case eSpellType.EnergyResistDebuff:
                case eSpellType.HeatResistDebuff:
                case eSpellType.ColdResistDebuff:
                case eSpellType.MatterResistDebuff:
                case eSpellType.DexterityConstitutionDebuff:
                case eSpellType.WeaponSkillConstitutionDebuff:
                case eSpellType.Stun:
                case eSpellType.Mez:
                case eSpellType.Mesmerize:
                    // A point blank spell (range 0) hits around the caster: the old check wanted the target within range 0,
                    // so these were never cast (issue #17). Two or more monsters in the fight around it, nothing woken or pulled.
                    if (spell.IsPBAoE)
                    {
                        castSpell = InstantPointBlankWorthwhile(spell, Body.TargetObject as GameLiving);
                        break;
                    }
                    if (AreaBlastVetoed(spell, Body.TargetObject))
                        break;

                    if (!LivingHasEffect((GameLiving)Body.TargetObject, spell) && Body.IsWithinRadius(Body.TargetObject, spell.Range))
                        castSpell = true;
                    break;
                // An instant snare (Champion Slowness, Valewalker Close Path, Nightshade Constrict Spirit) on a monster that is
                // about to run, as players kept runners from bringing friends (owner 2026-11-05 class audit: never cast).
                // owner 2026-11-08: only a real runner (moving, low, out of melee: ControlAllowedOnTarget checked above)
                case eSpellType.SpeedDecrease:
                case eSpellType.UnbreakableSpeedDecrease:
                    if (!spell.IsPBAoE && spell.Radius == 0 && Body.TargetObject is GameNPC runner &&
                        (!BotPvpCrowdControl.PlayerLike(runner) || runner.HealthPercent < BotControlSpells.RunnerHealthPercent) &&
                        !LivingHasEffect(runner, spell) && Body.IsWithinRadius(runner, spell.Range))
                        castSpell = true;
                    break;
            }

            ECSGameEffect pulseEffect = EffectListService.GetPulseEffectOnTarget(Body, spell);

            if (pulseEffect != null)
                return false;

            if (castSpell)
            {
                return Body.CastSpell(spell, m_mobSpellLine);
            }

            return false;
        }

        private bool IsMaintenanceTraveling() => BotBody is { } bot &&
            (bot.IsMoving || bot.IsReturningAfterRelease || bot.Group?.LivingLeader?.IsMoving == true ||
             bot.PersistentRecord?.Activity?.Contains("travel", StringComparison.OrdinalIgnoreCase) == true ||
             bot.PersistentRecord?.Activity?.Contains("walking", StringComparison.OrdinalIgnoreCase) == true);

        protected bool CheckDefensiveSpells(Spell spell)
        {
            if (spell.SpellType == eSpellType.SpeedEnhancement && spell.IsPulsing && !IsMaintenanceTraveling())
                return false;
            if (!CanCastDefensiveSpell(spell))
                return false;

            bool casted = false;

            // No pet near an encounter that devours pets (AutonomousPetSupport.PetEaterNear).
            // ... and no summon while the companion's player is on the move (owner 2026-11-08)
            if ((AutonomousPetSupport.IsMainPetSummon(spell.SpellType) || spell.SpellType == eSpellType.SummonMinion) &&
                (AutonomousPetSupport.PetEaterNear(Body) != null || AutonomousPetSupport.WaitsToSummon(Body)))
                return false;

            Body.TargetObject = null;

            if (spell.NeedInstrument)
                return false;

            switch (spell.SpellType)
            {
                #region Summon

                case eSpellType.SummonCommander:
                case eSpellType.SummonUnderhill:
                case eSpellType.SummonDruidPet:
                case eSpellType.SummonSimulacrum:
                case eSpellType.SummonSpiritFighter:
                    if (Body.ControlledBrain != null)
                        return false;
                    Body.TargetObject = Body;
                    break;

                case eSpellType.SummonMinion:
                    if (Body.ControlledBrain != null)
                    {
                        IControlledBrain[] icb = Body.ControlledBrain.Body?.ControlledNpcList;
                        // A level-one Bonedancer Returned Commander has no
                        // subordinate array. Treat that as unavailable rather
                        // than aborting the bot's entire think turn. The
                        // autonomous pet policy replaces the commander when a
                        // valid rank becomes known.
                        if (icb == null || icb.Length == 0)
                            break;

                        int numberofpets = 0;

                        for (int i = 0; i < icb.Length; i++)
                        {
                            if (icb[i] != null)
                                numberofpets++;
                        }

                        if (numberofpets >= icb.Length)
                            break;

                        Body.TargetObject = Body;
                    }
                    break;

                #endregion

                #region Buffs

                case eSpellType.SpeedEnhancement when spell.IsPulsing:
                    if (!LivingHasEffect(Body, spell))
                        Body.TargetObject = Body;
                    break;

                case eSpellType.BodySpiritEnergyBuff:
                case eSpellType.HeatColdMatterBuff:
                case eSpellType.SpiritResistBuff:
                case eSpellType.EnergyResistBuff:
                case eSpellType.HeatResistBuff:
                case eSpellType.ColdResistBuff:
                case eSpellType.BodyResistBuff:
                case eSpellType.MatterResistBuff:
                case eSpellType.AllMagicResistBuff:
                case eSpellType.EnduranceRegenBuff:
                case eSpellType.PowerRegenBuff:
                case eSpellType.AblativeArmor:
                case eSpellType.AcuityBuff:
                case eSpellType.AFHitsBuff:
                case eSpellType.ArmorAbsorptionBuff:
                case eSpellType.BaseArmorFactorBuff:
                case eSpellType.SpecArmorFactorBuff:
                case eSpellType.PaladinArmorFactorBuff:
                case eSpellType.Buff:
                case eSpellType.CelerityBuff:
                case eSpellType.ConstitutionBuff:
                case eSpellType.CourageBuff:
                case eSpellType.CrushSlashTrustBuff:
                case eSpellType.DexterityBuff:
                case eSpellType.DexterityQuicknessBuff:
                case eSpellType.EffectivenessBuff:
                case eSpellType.FatigueConsumptionBuff:
                case eSpellType.FlexibleSkillBuff:
                case eSpellType.HasteBuff:
                case eSpellType.HealthRegenBuff:
                case eSpellType.HeroismBuff:
                case eSpellType.KeepDamageBuff:
                case eSpellType.MagicResistBuff:
                case eSpellType.MeleeDamageBuff:
                case eSpellType.MLABSBuff:
                case eSpellType.ParryBuff:
                case eSpellType.PowerHealthEnduranceRegenBuff:
                case eSpellType.StrengthBuff:
                case eSpellType.StrengthConstitutionBuff:
                case eSpellType.SuperiorCourageBuff:
                case eSpellType.ToHitBuff:
                case eSpellType.WeaponSkillBuff:
                case eSpellType.DamageAdd:
                case eSpellType.OffensiveProc:
                case eSpellType.DefensiveProc:
                case eSpellType.DamageShield:
                case eSpellType.SpeedEnhancement when spell.Target == eSpellTarget.PET:
                case eSpellType.CombatSpeedBuff when spell.Duration > 20:
                case eSpellType.CombatSpeedBuff when spell.IsConcentration:
                case eSpellType.MesmerizeDurationBuff when !spell.IsPulsing:
                case eSpellType.Bladeturn when !spell.IsPulsing:
                {
                    if (!BotBody.CanAffordConcentration(spell))
                        break;

                    if (spell.Target == eSpellTarget.REALM && !spell.IsPulsing)
                    {
                        Body.TargetObject = FindRandomMissingRealmBuffTarget(spell);
                        break;
                    }

                    if (spell.Target == eSpellTarget.PET)
                    {
                        if (spell.SpellType == eSpellType.DamageShield)
                            return false;

                        if (Body.ControlledBrain?.Body != null)
                        {
                            if (!LivingHasEffect(Body.ControlledBrain.Body, spell))
                                Body.TargetObject = Body.ControlledBrain.Body;
                        }

                        break;
                    }

                    if (!LivingHasEffect(Body, spell))
                    {
                        Body.TargetObject = Body;
                        break;
                    }

                    if (Body.Group != null)
                    {
                        if (spell.Target == eSpellTarget.REALM || spell.Target == eSpellTarget.GROUP)
                        {
                            foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                            {
                                if (groupMember != Body)
                                {
                                    if (!LivingHasEffect(groupMember, spell) && Body.IsWithinRadius(groupMember, spell.Range) && groupMember.IsAlive)
                                    {
                                        Body.TargetObject = groupMember;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    if (Body.TargetObject == null)
                        Body.TargetObject = FindMissingPartyPetBuffTarget(spell);
                }
                break;

                #endregion

                #region Cures

                case eSpellType.CureDisease:
                    if (Body.IsDiseased)
                    {
                        Body.TargetObject = Body;
                        break;
                    }

                    if (Body.Group != null)
                    {
                        foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                        {
                            if (groupMember != Body && groupMember.IsDiseased && Body.IsWithinRadius(groupMember, spell.Range))
                            {
                                Body.TargetObject = groupMember;
                                break;
                            }
                        }
                    }
                    break;

                case eSpellType.CurePoison:
                    if (Body.IsPoisoned)
                    {
                        Body.TargetObject = Body;
                        break;
                    }

                    if (Body.Group != null)
                    {
                        foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                        {
                            if (groupMember != Body && groupMember.IsPoisoned && Body.IsWithinRadius(groupMember, spell.Range))
                            {
                                Body.TargetObject = groupMember;
                                break;
                            }
                        }
                    }
                    break;

                case eSpellType.CureMezz:
                    if (Body.Group != null)
                    {
                        foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                        {
                            if (groupMember != Body && groupMember.IsMezzed && Body.IsWithinRadius(groupMember, spell.Range))
                            {
                                Body.TargetObject = groupMember;
                                break;
                            }
                        }
                    }
                    break;

                #endregion
            }

            if (Body?.TargetObject != null)
            {
                casted = CastCoordinatedBuff(spell);
            }

            return casted;
        }

        bool CheckDefensiveSpells(List<Spell> spells)
        {
            List<(Spell, GameLiving)> spellsToCast = new(spells.Count);

            foreach (Spell spell in spells)
            {
                if (Body.InCombat && IsMaintainableClassBuff(spell))
                    continue;
                // Cast in the fight by TryShortCombatShield, never as upkeep.
                if (IsShortCombatShield(spell))
                    continue;
                // True 1.65: a groupmate buffer keeps this base buff up (BaseBuffLeftToGroupmate)
                if (BaseBuffLeftToGroupmate(spell))
                    continue;
                // Caster-pet classes, including a Bonedancer's commander tree,
                // maintain their controlled pets through AutonomousPetSupport. The
                // generic defensive selector historically treated PET spells
                // as self buffs, queued the cast, then lost the AI turn when
                // the native target validation rejected it.  Repeating that on
                // every FOLLOW pulse starved formation movement forever after
                // the first successful summon.
                if (BotBody?.CharacterClass != null &&
                    AutonomousPetSupport.OwnsPetUpkeep(
                        (eCharacterClass)BotBody.CharacterClass.ID) &&
                    spell.Target is eSpellTarget.PET or eSpellTarget.CONTROLLED)
                {
                    continue;
                }
                if (CanCastDefensiveSpell(spell, out GameLiving target))
                    spellsToCast.Add((spell, target));
            }

            if (spellsToCast.Count == 0)
                return false;

            GameObject oldTarget = Body.TargetObject;
            (Spell spell, GameLiving target) spellToCast = BotBody.IsEndgameCompanion
                ? spellsToCast.OrderByDescending(entry => entry.Item1.Level).ThenByDescending(entry => entry.Item1.ID).First()
                : spellsToCast[Util.Random(spellsToCast.Count - 1)];
            if (UsesVeteranBuffing && IsMaintainableClassBuff(spellToCast.spell))
                spellToCast = spellsToCast.Where(entry => IsMaintainableClassBuff(entry.Item1))
                    .OrderBy(entry => BuffUpkeepPriority(entry.Item1)).ThenByDescending(entry => entry.Item1.Level).First();
            Body.TargetObject = spellToCast.target;
            bool cast = CastCoordinatedBuff(spellToCast.spell);

            Body.TargetObject = oldTarget;
            return cast;

            bool CanCastDefensiveSpell(Spell spell, out GameLiving target)
            {
                target = null;
                if (CompanionFollowPolicy.DeferBuff(BotBody, spell)) return false;

                if (spell.Level > Body.Level || Body.Mana < BotBody.PowerCost(spell) ||
                    !BotBody.CanAffordConcentration(spell) ||
                    BotSongTwistPolicy.IsReservedPulse(BotBody, spell) ||
                    spell.NeedInstrument || (!spell.Uninterruptible && Body.IsBeingInterrupted) ||
                    (spell.HasRecastDelay && Body.GetSkillDisabledDuration(spell) > 0))
                {
                    return false;
                }

                target = FindTargetForDefensiveSpell(spell);
                return target != null;
            }
        }

        protected virtual GameLiving FindTargetForDefensiveSpell(Spell spell)
        {
            // Do not let the secondary defensive selector undo stationary PBT
            // upkeep by starting caster speed again on the next FOLLOW pulse.
            if (spell.SpellType == eSpellType.SpeedEnhancement && spell.IsPulsing && !IsMaintenanceTraveling())
                return null;
            if (spell.Target == eSpellTarget.REALM && !spell.IsPulsing && IsMaintainableClassBuff(spell))
                return FindRandomMissingRealmBuffTarget(spell);
            GameLiving target = null;

            switch (spell.SpellType)
            {
                case eSpellType.SpeedEnhancement when spell.IsPulsing:
                    if (!LivingHasEffect(Body, spell))
                        target = Body;
                    break;

                case eSpellType.BodySpiritEnergyBuff:
                case eSpellType.HeatColdMatterBuff:
                case eSpellType.AblativeArmor:
                case eSpellType.AcuityBuff:
                case eSpellType.AFHitsBuff:
                case eSpellType.ArmorAbsorptionBuff:
                case eSpellType.BaseArmorFactorBuff:
                case eSpellType.SpecArmorFactorBuff:
                case eSpellType.PaladinArmorFactorBuff:
                case eSpellType.Buff:
                case eSpellType.ConstitutionBuff:
                case eSpellType.CourageBuff:
                case eSpellType.CrushSlashTrustBuff:
                case eSpellType.DexterityBuff:
                case eSpellType.DexterityQuicknessBuff:
                case eSpellType.HealthRegenBuff:
                case eSpellType.StrengthBuff:
                case eSpellType.StrengthConstitutionBuff:
                case eSpellType.SuperiorCourageBuff:
                case eSpellType.DamageAdd:
                case eSpellType.OffensiveProc:
                case eSpellType.DefensiveProc:
                case eSpellType.Bladeturn when !spell.IsPulsing:
                    if (!LivingHasEffect(Body, spell) && (spell.Target != eSpellTarget.REALM || BuffHelps(Body, spell)))
                    {
                        target = Body;
                        break;
                    }

                    if (Body.Group != null && (spell.Target == eSpellTarget.REALM || spell.Target == eSpellTarget.GROUP))
                    {
                        foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                        {
                            if (groupMember != Body && !LivingHasEffect(groupMember, spell) && Body.IsWithinRadius(groupMember, spell.Range) && groupMember.IsAlive &&
                                (spell.Target != eSpellTarget.REALM || BuffHelps(groupMember, spell)))
                            {
                                target = groupMember;
                                break;
                            }
                        }
                    }
                    if (target == null)
                        target = FindMissingPartyPetBuffTarget(spell);
                    break;
            }

            return target;
        }

        // Mirrors SpellHandler.HasConflictingEffectWith.
        public static bool DotsConflict(Spell spell, Spell existing) =>
            spell.ID == existing.ID ||
            (spell.EffectGroup != 0 || existing.EffectGroup != 0
                ? spell.EffectGroup == existing.EffectGroup
                : spell.SpellType == existing.SpellType);

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameLiving, Dictionary<int, long>> StrongerBuffTries = new();

        /// <summary>One try every 30 s per target and spell to put a stronger buff over a weaker one.</summary>
        private static bool TakeStrongerBuffTry(GameLiving target, Spell spell)
        {
            Dictionary<int, long> tries = StrongerBuffTries.GetValue(target, _ => new());
            long now = GameLoop.GameLoopTime;
            lock (tries)
            {
                if (tries.TryGetValue(spell.ID, out long next) && now < next)
                    return false;
                tries[spell.ID] = now + 30_000;
                return true;
            }
        }

        public bool LivingHasEffect(GameLiving target, Spell spell)
        {
            if (target == null)
                return true;

            eEffect spellEffect = EffectHelper.GetEffectFromSpell(spell);

            if (spellEffect is eEffect.DirectDamage or eEffect.Pet or eEffect.Unknown)
                return false;

            ISpellHandler spellHandler = Body.castingComponent.SpellHandler;

            if (spellHandler != null && spellHandler.Spell.ID == spell.ID && spellHandler.Target == target)
                return true;

            ISpellHandler queuedSpellHandler = Body.castingComponent.QueuedSpellHandler;

            if (queuedSpellHandler != null && queuedSpellHandler.Spell.ID == spell.ID && queuedSpellHandler.Target == target)
                return true;

            if (spell.SpellType is eSpellType.OffensiveProc or eSpellType.DefensiveProc)
            {
                List<ECSGameSpellEffect> existingEffects = target.effectListComponent.GetSpellEffects(spellEffect);

                foreach (ECSGameSpellEffect effect in existingEffects)
                {
                    if (effect.SpellHandler.Spell.ID == spell.ID || (spell.EffectGroup > 0 && effect.SpellHandler.Spell.EffectGroup == spell.EffectGroup))
                        return true;
                    // A same-or-higher rank of this proc line already wins
                    // the effect conflict; recasting a lower rank only burns power.
                    if (spell.Group != 0 && effect.SpellHandler.Spell.Group == spell.Group &&
                        effect.SpellHandler.Spell.Level >= spell.Level)
                        return true;
                }

                return false;
            }

            // Every native Savage self-buff intentionally shares eEffect.SavageBuff.
            // A category-only lookup therefore made the first active buff suppress
            // all other DPS, haste, parry, evade, and resistance families.
            if (spellEffect == eEffect.SavageBuff)
                return target.effectListComponent.GetEffects(eEffect.SavageBuff)
                    .Any(effect => SavageBotCombatPolicy.SameBuffFamily(
                        spell, effect?.SpellHandler?.Spell));

            ECSGameEffect pulseEffect = EffectListService.GetPulseEffectOnTarget(target, spell);

            if (pulseEffect != null)
                return true;

            if (UsesVeteranBuffing && spell.SpellType == eSpellType.DamageAdd)
                return target.effectListComponent.GetSpellEffects(spellEffect).Any(effect =>
                    !effect.IsEnding && effect.SpellHandler?.Spell is Spell other &&
                    (other.ID == spell.ID || other.EffectGroup == spell.EffectGroup && other.Damage >= spell.Damage));

            // DoTs from different effect groups stack (Sluaghbinder Rot + Bane), so
            // only a DoT the server would treat as conflicting blocks a new one.
            if (spellEffect == eEffect.DamageOverTime)
                return target.effectListComponent.GetSpellEffects(spellEffect)
                    .Any(effect => effect?.SpellHandler?.Spell is Spell existing && DotsConflict(spell, existing));

            // A beneficial buff is "already there" only when what is there is at least as strong: a weaker potion,
            // charge or lower rank from someone else no longer blocks the bot's own better buff (owner 2026-11-03).
            // The effect list keeps the better one and disables the weaker, so the next pass sees this one and stops.
            // Strength as the server weighs it (value x the caster's effectiveness); a concentration buff never
            // contests another concentration buff (the server refuses it, or ends a player's own), and one try per
            // target and spell every 30 s at most, so a refused buff can't loop (review 2026-11-03).
            if (!spell.IsHarmful && spell.Value > 0 && EffectListService.GetEffectOnTarget(target, spellEffect) != null)
            {
                double ours = spell.Value * Math.Max(0.01, Body.Effectiveness);
                List<ECSGameEffect> present = target.effectListComponent.GetEffects(spellEffect);
                bool asStrong = present.Any(effect => effect is not ECSGameSpellEffect spellEffectPresent ||
                    spellEffectPresent.SpellHandler?.Spell is not Spell existing || existing.ID == spell.ID ||
                    spell.IsConcentration && spellEffectPresent.IsConcentrationEffect() ||
                    existing.Value * Math.Max(0.01, spellEffectPresent.Effectiveness) >= ours);
                if (asStrong || !TakeStrongerBuffTry(target, spell))
                    return true;
                return HasImmunityEffect(EffectHelper.GetImmunityEffectFromSpell(spell)) || HasImmunityEffect(EffectHelper.GetNpcImmunityEffectFromSpell(spell));
            }

            return EffectListService.GetEffectOnTarget(target, spellEffect) != null || HasImmunityEffect(EffectHelper.GetImmunityEffectFromSpell(spell)) || HasImmunityEffect(EffectHelper.GetNpcImmunityEffectFromSpell(spell));

            bool HasImmunityEffect(eEffect immunityEffect)
            {
                return immunityEffect is not eEffect.Unknown && EffectListService.GetEffectOnTarget(target, immunityEffect) != null;
            }
        }

        #endregion

        #region Healing

        private long nextCureTime = 0;

        /// <summary>
        /// Two or more group members hurt: a group heal-over-time not already running on the patient, else the group heal
        /// (cast-time, then instant when it has no long recast), else the spread heal (Prima: a Healer has an instant heal
        /// and a group heal going in every fight; Shaman group regeneration).
        /// </summary>
        /// <param name="burst">False: only a group heal-over-time (True 1.65: two hurt, or the fight has just begun).</param>
        private Spell GroupHealChoice(GameLiving patient, bool burst, bool preferSpread, bool immediate = false)
        {
            if (!immediate)
                foreach (Spell regen in new[] { BotBody.HealOverTimeInstantGroup, BotBody.HealOverTimeGroup })
                    if (CheckHealSpell(regen) && patient != null && !HotRunningOrComing(patient, regen))
                        return regen;
            if (!burst)
                return null;
            if (preferSpread && CheckHealSpell(BotBody.HealSpread))
                return BotBody.HealSpread;
            if (CheckHealSpell(BotBody.HealGroup))
                return BotBody.HealGroup;
            if (CheckHealSpell(BotBody.HealSpread))
                return BotBody.HealSpread;
            return null;
        }

        private long _healFightSince;   // when the group's current fight began, as this healer saw it (0: no fight)

        /// <summary>The heal order between two members: the lower health first; in True 1.65 a tank counts 10 points lower.</summary>
        private static bool PreferHealTarget(GameLiving candidate, bool candidateTank, GameLiving current, bool classic165) =>
            classic165
                ? BotHealTriage165.Prefer(candidate.HealthPercent, candidateTank, current.HealthPercent, BotHealTriage165.IsTank(current))
                : candidate.HealthPercent < current.HealthPercent;

        bool CheckHealSpell(Spell spell)
        {
            return spell != null
                && (!BotBody.IsBeingInterruptedByOther || spell.IsInstantCast)
                && (!spell.HasRecastDelay || BotBody.GetSkillDisabledDuration(spell) <= 0)
                && BotBody.Mana >= BotBody.PowerCost(spell);
        }

        private readonly BotSupportReach _supportReach = new();

        /// <summary>
        /// A group member a heal or buff may be aimed at: this bot, or a member within
        /// 2,500 units in the same region that is not being skipped after a failed approach.
        /// </summary>
        private bool InSupportReach(GameLiving member) =>
            member == Body || member != null && member.CurrentRegion == Body.CurrentRegion &&
            Body.IsWithinRadius(member, BotSupportReach.SupportRadius) &&
            !_supportReach.IsSkipped(member, GameLoop.GameLoopTime);

        // Owner 2026-10-27: a /spawn companion does not walk away from its player to heal or buff a member that is
        // far from the player (in a battleground the Cleric and Friar ran off alone to buff a revived Paladin).
        public const int CompanionSupportLeash = 1_500;

        public static bool LeavesPlayerToSupport(bool temporaryCompanion, bool autonomous, bool playerNear, int targetToPlayer) =>
            temporaryCompanion && !autonomous && playerNear && targetToPlayer > CompanionSupportLeash;

        private bool LeavesPlayerToSupport(GameLiving target) =>
            AssistedPlayer is { IsAlive: true } player && LeavesPlayerToSupport(BotBody?.IsPlayerCompanion == true,
                false, player.CurrentRegionID == Body.CurrentRegionID,
                player.CurrentRegionID == target.CurrentRegionID ? (int)player.GetDistanceTo(target) : int.MaxValue);

        /// <summary>
        /// Walks toward a group member that is out of range of a heal or buff. False once the
        /// walk has made no progress for 12 seconds (that member is then skipped for 30).
        /// </summary>
        private bool ApproachSupportTarget(GameLiving target)
        {
            if (target == null || target == Body || !InSupportReach(target) || LeavesPlayerToSupport(target) ||
                !_supportReach.KeepApproaching(target, Body.GetDistanceTo(target), GameLoop.GameLoopTime))
                return false;
            BotBody.WalkTo(new Point3D(target.X, target.Y, target.Z), BotBody.MaxSpeed);
            return true;
        }

        public bool CheckHeals()
        {
            const byte ManaThreshold = 90;
            const long CureDelay = 5000;

            if (AlreadyCheckedHeals || (!Body.CanCastHealSpells && !(Body.Group != null && BotBody.ResurrectionSpell != null)) || Body.IsStunned || Body.IsMezzed || Body.IsSilenced)
                return false;

            AlreadyCheckedHeals = true;

            // The player's "Heal at" choice for a hired healer (AI Options): early 90%, late 50%; emergencies stay at 40%.
            int healAt = CompanionOptions165.Choice(BotBody, "healat");
            int needAt = healAt == 1 ? 90 : healAt == 2 ? 50 : 65;
            // True 1.65: healers start at two-thirds health too (BotHealTriage165), normal mode at 80%
            bool classic165 = Classic165Mode.IsOn;
            int healerNeedAt = BotHealTriage165.HealerNeedAt(healAt, classic165);
            // Role (AI Options): an attacking healer, or an off-healer Friar or Warden in a fight, heals emergencies only
            bool emergenciesOnly = healAt == 0 && BotBody.CharacterClass != null &&
                (RoleChoice == 2 && CompanionSpellOptions165.UsesHealerRoleRow((eCharacterClass)BotBody.CharacterClass.ID) ||
                 CompanionControls165.OffHealerInFight((eCharacterClass)BotBody.CharacterClass.ID, RoleChoice, Body.InCombat));
            if (emergenciesOnly)
                needAt = healerNeedAt = 40;

            // O2: a passive companion never stops to resurrect
            bool passiveHealing = _passiveHealing != CompanionControls165.PassiveHealing.None;
            if (!passiveHealing && TryResurrectGroupMember())
                return true;
            if (!passiveHealing && TryResurrectSiegeComrade())
                return true;

            if (!Body.CanCastHealSpells) return false;

            // Check if we can cast any instant heals
            bool canCastInstantHeal = CheckHealSpell(BotBody.HealInstant);
            bool canCastInstantGroupHeal = CheckHealSpell(BotBody.HealInstantGroup);

            bool quickHealReady = BotBody.IsBeingInterruptedByOther &&
                (BotBody.QuickCastReady(BotBody.HealBig) || BotBody.QuickCastReady(BotBody.HealEfficient) ||
                 BotBody.QuickCastReady(BotBody.HealGroup));
            if (BotBody.IsBeingInterruptedByOther && !canCastInstantHeal && !canCastInstantGroupHeal && !quickHealReady)
                return false;

            bool isCastingHeal = BotBody.IsCasting && BotBody.castingComponent.SpellHandler.Spell.IsHealing &&
                !BotSongTwistPolicy.HasMobileSongCast(BotBody);

            if (isCastingHeal && !canCastInstantHeal && !canCastInstantGroupHeal)
                return true;

            // Working variables
            int amountToHeal = 0;
            int numEmergency = 0;
            int numNeedHealing = 0;
            Spell spellToCast = null;
            GameLiving spellTarget = null;
            bool startedCasting = false;
            int numGroupHurt = 0;   // members under the heal line by their real health (a group heal's count)
            int numGroupBadlyHurt = 0;
            int groupHealChoice = classic165 ? CompanionOptions165.Choice(BotBody, CompanionOptions165.GroupHealingKey) : 0;
            bool groupInFight = false;

            // Check group health
            if (Body.Group != null)
            {
                foreach (GameLiving member in Body.Group.GetMembersInTheGroup())
                {
                    if (!member.IsAlive || !InSupportReach(member))
                        continue;

                    groupInFight |= member.InCombat;
                    int deficit = member.MaxHealth - member.Health;

                    if (deficit > 0)
                    {
                        amountToHeal += deficit;
                        // True 1.65: the main tank first - outside an emergency a tank counts 10 points lower
                        bool tank = classic165 && BotHealTriage165.IsTank(member);
                        int triage = BotHealTriage165.TriageHealth(member.HealthPercent, tank);
                        if (member.HealthPercent < needAt)
                        {
                            numGroupHurt++;
                            if (member.HealthPercent < 50)
                                numGroupBadlyHurt++;
                        }

                        if (triage < needAt)
                        {
                            numNeedHealing++;

                            if (member.HealthPercent < 40)
                                numEmergency++;

                            if (!BotBody.IsPlayerCompanion
                                    ? spellTarget == null || PreferHealTarget(member, tank, spellTarget, classic165)
                                    : TemporaryCompanionPetHealing.PreferHealingTarget(BotBody, member, spellTarget))
                                spellTarget = member;
                        }
                        else if (IsHealer && triage < healerNeedAt)
                        {
                            numNeedHealing++;

                            if (!BotBody.IsPlayerCompanion
                                    ? spellTarget == null || PreferHealTarget(member, tank, spellTarget, classic165)
                                    : TemporaryCompanionPetHealing.PreferHealingTarget(BotBody, member, spellTarget))
                                spellTarget = member;
                        }
                    }
                }
                if (!groupInFight)
                    _healFightSince = 0;
                else if (_healFightSince == 0)
                    _healFightSince = GameLoop.GameLoopTime;

                // A temporary companion treats the exact player-led party's
                // pets as party-adjacent heal targets. Attached pets are walked as a
                // bounded tree; the local-radius pass also finds Theurgist and
                // Animist field pets which native code intentionally leaves
                // outside ControlledBrain. Ownership must resolve to the player
                // or an exact-group temporary companion.
                if (BotBody.IsPlayerCompanion)
                {
                    int fieldPetRange = new[]
                    {
                        BotBody.HealInstant, BotBody.HealBig, BotBody.HealEfficient,
                        BotBody.HealOverTime, BotBody.HealGroup, BotBody.HealInstantGroup
                    }.Where(spell => spell != null)
                     .Select(spell => BotBody.castingComponent.CalculateSpellRange(spell))
                     .DefaultIfEmpty(0).Max();

                    foreach (GameNPC playerPet in TemporaryCompanionPetHealing.TriageTargets(BotBody, fieldPetRange))
                    {
                        int deficit = playerPet.MaxHealth - playerPet.Health;
                        if (deficit <= 0)
                            continue;

                        amountToHeal += deficit;
                        if (playerPet.HealthPercent < needAt)
                        {
                            numNeedHealing++;
                            if (playerPet.HealthPercent < 40)
                                numEmergency++;
                            if (TemporaryCompanionPetHealing.PreferHealingTarget(BotBody, playerPet, spellTarget))
                                spellTarget = playerPet;
                        }
                        else if (IsHealer && playerPet.HealthPercent < healerNeedAt)
                        {
                            numNeedHealing++;
                            if (TemporaryCompanionPetHealing.PreferHealingTarget(BotBody, playerPet, spellTarget))
                                spellTarget = playerPet;
                        }
                    }
                }

                // A player-led bot party exists to support its player leader.  When the
                // leader needs a heal, prefer that player over autonomous bot members;
                // healthy leaders do not prevent the healer from tending the rest.
                GamePlayer assistedPlayer = AssistedPlayer;
                if (BotBody.IsPlayerLedGroup && assistedPlayer?.IsAlive == true && Body.Group.IsInTheGroup(assistedPlayer) &&
                    InSupportReach(assistedPlayer))
                {
                    bool playerNeedsHealing = assistedPlayer.HealthPercent < needAt || (IsHealer && assistedPlayer.HealthPercent < healerNeedAt);
                    // ...unless someone else is an emergency and worse off (owner 2026-11-03: a tank at 15% before the
                    // player at 64%)
                    bool worseEmergency = spellTarget != null && spellTarget != assistedPlayer &&
                        spellTarget.HealthPercent < TemporaryCompanionPetHealing.EmergencyHealthPercent &&
                        spellTarget.HealthPercent < assistedPlayer.HealthPercent;
                    if (playerNeedsHealing && !worseEmergency)
                        spellTarget = assistedPlayer;
                }

                // A real relic carrier remains fully governed by native relic
                // rules.  For an allied group member in a legal heal range,
                // however, keeping that carrier alive takes precedence over
                // ordinary group triage.
                GameLiving relicCarrier = RelicCarriers()
                    .FirstOrDefault(carrier => carrier?.IsAlive == true && carrier.Realm == BotBody.Realm &&
                                               (Body.Group.IsInTheGroup(carrier) || BotBody.IsAutonomousWorldBot &&
                                                AutonomousObjectiveAssignments.Is(BotBody, eAutonomousObjectiveKind.RvR)) && CanHealRelicCarrier(carrier));
                if (relicCarrier != null && relicCarrier.HealthPercent < 100 &&
                    (!BotBody.IsPlayerCompanion ||
                     TemporaryCompanionPetHealing.PreferHealingTarget(BotBody, relicCarrier, spellTarget)))
                {
                    spellTarget = relicCarrier;
                    // A 80-99% carrier may not have met ordinary triage's
                    // threshold, so make the existing non-emergency spell
                    // selection operational without inventing extra deficit or
                    // altering its real emergency threshold.
                    numNeedHealing = Math.Max(1, numNeedHealing);
                }
            }
            else
            {
                amountToHeal = BotBody.MaxHealth - BotBody.Health;

                if (amountToHeal > 0)
                {
                    spellTarget = BotBody;

                    if (BotBody.HealthPercent < needAt)
                    {
                        numNeedHealing = 1;

                        if (BotBody.HealthPercent < 40)
                            numEmergency = 1;
                    }
                }
            }

            // RvR support uses ordinary, range/LOS-checked single-target heals.
            // This does not extend PvE/companion triage to unrelated NPCs.
            // Battlegrounds fix 2026-11-12 (G2): a PvP-focus healer in its battleground supports its realm there too.
            if (BotBody.IsAutonomousWorldBot && (AutonomousObjectiveAssignments.Is(BotBody, eAutonomousObjectiveKind.RvR) ||
                                                 AutonomousObjectiveAssignments.IsBattlegroundPvp(BotBody)))
            {
                // Q30: held 2 s, ranked before any line-of-sight raycast (BotBrain.RvrSupport.cs)
                GameLiving support = RvrSupportTarget();
                if (support != null && (spellTarget == null || spellTarget.HealthPercent >= 40 || support.HealthPercent < spellTarget.HealthPercent))
                {
                    spellTarget = support;
                    amountToHeal = Math.Max(amountToHeal, support.MaxHealth - support.Health);
                    numNeedHealing = Math.Max(1, numNeedHealing);
                    if (support.HealthPercent < 40) numEmergency = Math.Max(1, numEmergency);
                }
            }

            if (AutonomousRealmRaid.HasSharedSupport(BotBody))
            {
                // Lowest-health raid member or pet in range, without sorting the whole roster.
                GameLiving raidPatient = null;
                foreach (GameLiving m in AutonomousRealmRaid.SupportMembers(BotBody))
                    if (m != null && m.IsAlive && m.CurrentRegionID == Body.CurrentRegionID && m.HealthPercent < healerNeedAt &&
                        Body.IsWithinRadius(m, 1800) && (raidPatient == null || m.HealthPercent < raidPatient.HealthPercent))
                        raidPatient = m;
                foreach (GameLiving m in AutonomousRealmRaid.SupportPets(BotBody, 1800))
                    if (m.HealthPercent < healerNeedAt && (raidPatient == null || m.HealthPercent < raidPatient.HealthPercent))
                        raidPatient = m;
                // dragon-fight M1: a real player of the realm fighting beside the raid (the horn sounder) is healed too
                foreach (GameLiving m in BotGroupSupport.RaidGuests(BotBody))
                    if (m.IsAlive && m.CurrentRegionID == Body.CurrentRegionID && m.HealthPercent < healerNeedAt &&
                        Body.IsWithinRadius(m, 1800) && (raidPatient == null || m.HealthPercent < raidPatient.HealthPercent))
                        raidPatient = m;
                if (raidPatient != null && (spellTarget == null || raidPatient.HealthPercent < spellTarget.HealthPercent))
                {
                    spellTarget = raidPatient;
                    numNeedHealing = Math.Max(1, numNeedHealing);
                    if (raidPatient.HealthPercent < 40) numEmergency = Math.Max(1, numEmergency);
                }
            }

            // Emergency heal
            if (numEmergency > 0)
            {
                // Several in danger: the instant group heal reaches them all (owner 2026-11-05 class audit: group heals
                // were only ever a last fallback, so a Healer never cast one while the whole group was low).
                if (numEmergency >= 2 && canCastInstantGroupHeal)
                    spellToCast = BotBody.HealInstantGroup;
                else if (canCastInstantHeal)
                    spellToCast = BotBody.HealInstant;
                else if (canCastInstantGroupHeal)
                    spellToCast = BotBody.HealInstantGroup;
                else if (!isCastingHeal)
                {
                    // Two critical targets can justify a cast spread immediately if the healer is free to cast.
                    if (classic165 && BotBody.IsPlayerCompanion && numEmergency >= 2 && groupHealChoice != 3 &&
                        GroupHealChoice(spellTarget, true, groupHealChoice != 2, immediate: true) is { } emergencyGroup)
                        spellToCast = emergencyGroup;
                    // Being hit, no instant heal left: Quick Cast carries the emergency heal (player practice).
                    else if (CheckHealSpell(BotBody.HealBig) || quickHealReady && BotBody.QuickCastReady(BotBody.HealBig))
                        spellToCast = BotBody.HealBig;
                    else if (CheckHealSpell(BotBody.HealEfficient) || quickHealReady && BotBody.QuickCastReady(BotBody.HealEfficient))
                        spellToCast = BotBody.HealEfficient;
                    else if (CheckHealSpell(BotBody.HealGroup) || quickHealReady && BotBody.QuickCastReady(BotBody.HealGroup))
                        spellToCast = BotBody.HealGroup;
                }
            }

            // Cure Mezz/Disease/Poison
            if (spellToCast == null)
            {
                // owner 2026-11-10: read the group once; a bot leaving it mid-turn made Body.Group null here (NRE,
                // NpcService removed the bot from the world)
                if (Body.Group is Group cureGroup)
                {
                    foreach (GameLiving member in cureGroup.GetMembersInTheGroup().OrderBy(member => member == AssistedPlayer ? 0 : 1))
                    {
                        if (member.IsMezzed && member != Body && InSupportReach(member) && CheckHealSpell(BotBody.CureMezz))
                        {
                            spellToCast = BotBody.CureMezz;
                            spellTarget = member;
                            break;
                        }
                    }

                    // Trainable healer-line nearsight cures were added after 1.65.
                    if (!Classic165Mode.IsOn && spellToCast == null && BotBody.HealSpells?.Where(spell => spell.SpellType is eSpellType.CureNearsight or
                                eSpellType.CureNearsightCustom && spell.Level <= Body.Level)
                            .OrderByDescending(spell => spell.Level).FirstOrDefault() is Spell cureNearsight &&
                        CheckHealSpell(cureNearsight))
                    {
                        GameLiving blinded = cureGroup.GetMembersInTheGroup().FirstOrDefault(member => member.IsAlive &&
                            InSupportReach(member) && member.effectListComponent.ContainsEffectForEffectType(eEffect.Nearsight));
                        if (blinded != null)
                        {
                            spellToCast = cureNearsight;
                            spellTarget = blinded;
                        }
                    }

                    if (spellToCast == null && nextCureTime < GameLoop.GameLoopTime)
                    {
                        foreach (GameLiving member in cureGroup.GetMembersInTheGroup().OrderBy(member => member == AssistedPlayer ? 0 : 1))
                        {
                            if (!InSupportReach(member))
                                continue;
                            if (member.IsDiseased)
                            {
                                if (CheckHealSpell(BotBody.CureDisease))
                                {
                                    spellToCast = BotBody.CureDisease;
                                    spellTarget = member;
                                    break;
                                }
                                else if (CheckHealSpell(BotBody.CureDiseaseGroup))
                                {
                                    spellToCast = BotBody.CureDiseaseGroup;
                                    spellTarget = member;
                                    break;
                                }
                            }

                            if (member.IsPoisoned)
                            {
                                if (CheckHealSpell(BotBody.CurePoison))
                                {
                                    spellToCast = BotBody.CurePoison;
                                    spellTarget = member;
                                    break;
                                }
                                else if (CheckHealSpell(BotBody.CurePoisonGroup))
                                {
                                    spellToCast = BotBody.CurePoisonGroup;
                                    spellTarget = member;
                                    break;
                                }
                            }
                        }
                    }
                }
                else
                {
                    if (BotBody.IsDiseased && nextCureTime < GameLoop.GameLoopTime && CheckHealSpell(BotBody.CureDisease))
                    {
                        spellToCast = BotBody.CureDisease;
                        spellTarget = BotBody;
                    }
                    else if (BotBody.IsPoisoned && nextCureTime < GameLoop.GameLoopTime && CheckHealSpell(BotBody.CurePoison))
                    {
                        spellToCast = BotBody.CurePoison;
                        spellTarget = BotBody;
                    }
                }
            }

            // Non-emergency heal
            if (spellToCast == null && numNeedHealing > 0)
            {
                if (!BotBody.IsCasting || (numEmergency > 0 && !isCastingHeal))
                {
                    long fightTime = _healFightSince == 0 ? 0 : GameLoop.GameLoopTime - _healFightSince;
                    bool preferSpread = BotBody.IsPlayerCompanion && classic165 &&
                        (groupHealChoice == 1 || groupHealChoice == 0 && numGroupBadlyHurt >= 2 && numGroupHurt < 4);
                    bool burstGroupHeal = !classic165 || BotHealTriage165.GroupHealWorthwhile(numGroupHurt, groupInFight, fightTime) ||
                        preferSpread && BotBody.HealSpread != null && numGroupBadlyHurt >= 2 &&
                        (!groupInFight || fightTime >= BotHealTriage165.GroupHealFightMilliseconds);
                    if (groupHealChoice != 3 && Body.Group != null && numNeedHealing >= 2 &&
                        GroupHealChoice(spellTarget, burstGroupHeal, preferSpread) is Spell groupHeal)
                        spellToCast = groupHeal;
                    else if (CheckHealSpell(BotBody.HealOverTime) && !HotRunningOrComing(spellTarget, BotBody.HealOverTime))
                        spellToCast = BotBody.HealOverTime;
                    else if (CheckHealSpell(BotBody.HealOverTimeInstant) && !HotRunningOrComing(spellTarget, BotBody.HealOverTimeInstant))
                        spellToCast = BotBody.HealOverTimeInstant;
                    // True 1.65: the small efficient heal outside emergencies; the big heal is for under 40% (above)
                    else if (!classic165 && BotBody.ManaPercent >= ManaThreshold && CheckHealSpell(BotBody.HealBig)
                        && (spellTarget.MaxHealth - spellTarget.Health) >= GameBot.HealAmount(BotBody.HealBig, spellTarget))
                        spellToCast = BotBody.HealBig;
                    else if (CheckHealSpell(BotBody.HealEfficient))
                        spellToCast = BotBody.HealEfficient;
                    else if (classic165 && CheckHealSpell(BotBody.HealBig))
                        spellToCast = BotBody.HealBig;
                    else if (groupHealChoice != 3 && CheckHealSpell(BotBody.HealGroup))
                        spellToCast = BotBody.HealGroup;
                }
            }

            // Owner 2026-11-10 (GitHub #28 review): a heal over time on the fighting tank as the sustained damage starts
            if (spellToCast == null && numNeedHealing == 0 && classic165 && IsHealer && !emergenciesOnly && healAt != 2 &&
                groupInFight && Body.Group is Group hotGroup && !BotBody.IsCasting)
            {
                Spell hot = CheckHealSpell(BotBody.HealOverTimeInstant) ? BotBody.HealOverTimeInstant :
                    CheckHealSpell(BotBody.HealOverTime) ? BotBody.HealOverTime : null;
                // Owner 2026-11-10 (review): never a walk or a stop for it - only a tank already in the spell's range and
                // sight (the dungeon line-of-sight check), and a cast-time one only on a healer standing free
                if (hot != null && BotHealTriage165.PreHotHealerFree(hot.IsInstantCast, BotBody.IsAttacking, BotBody.IsMoving,
                        BotBody.FollowTarget != null))
                {
                    int hotRange = BotBody.castingComponent.CalculateSpellRange(hot);
                    // Owner 2026-11-10 (review): the group read once; another thread can clear Body.Group mid-turn
                    foreach (GameLiving member in hotGroup.GetMembersInTheGroup())
                        if (member.IsAlive && member.InCombat && member.HealthPercent < BotHealTriage165.PreHotBelowPercent &&
                            BotHealTriage165.IsTank(member) && InSupportReach(member) && Body.IsWithinRadius(member, hotRange) &&
                            BotHealTriage165.PreHotTank(true, true, member.HealthPercent, HotRunningOrComing(member, hot),
                                BotBody.ManaPercent) &&
                            BotGroupSupport.HasCorpseLineOfSight(BotBody, member))
                        {
                            spellToCast = hot;
                            spellTarget = member;
                            FeatureCounters165.Add("healer_prehot_tank");   // FEATURE_COUNTERS_165
                            break;
                        }
                }
            }

            // Cast the selected spell
            if (BotBody.IsAutonomousWorldBot && (AutonomousObjectiveAssignments.Is(BotBody, eAutonomousObjectiveKind.RvR) || AutonomousRealmRaid.GetView(BotBody.Group) != null) &&
                spellTarget != null && spellTarget != Body && Body.Group?.IsInTheGroup(spellTarget) != true &&
                (spellToCast == BotBody.HealGroup || spellToCast == BotBody.HealInstantGroup ||
                 spellToCast == BotBody.HealSpread || spellToCast == BotBody.HealOverTimeGroup ||
                 spellToCast == BotBody.HealOverTimeInstantGroup))
                spellToCast = CheckHealSpell(BotBody.HealBig) ? BotBody.HealBig :
                    CheckHealSpell(BotBody.HealEfficient) ? BotBody.HealEfficient :
                    CheckHealSpell(BotBody.HealInstant) ? BotBody.HealInstant : null;
            // O2: a passive companion casts only an instant heal or a cure mez while its player moves (a needed instant
            // heal replaces a cast-time one), its normal heals once the player stops
            if (passiveHealing && spellToCast != null &&
                !CompanionControls165.PassiveAllowsHeal(_passiveHealing, spellToCast.IsInstantCast, spellToCast == BotBody.CureMezz))
                spellToCast = spellTarget != null && spellTarget.HealthPercent < needAt && CheckHealSpell(BotBody.HealInstant)
                    ? BotBody.HealInstant : null;
            if (spellToCast != null && spellTarget != null)
            {
                spellTarget = BotGroupSupport.ReserveHeal(BotBody, spellToCast, spellTarget);
                if (spellTarget == null) return isCastingHeal;
                if (!BotBody.IsWithinRadius(spellTarget, BotBody.castingComponent.CalculateSpellRange(spellToCast)))
                {
                    // O2: no walk to reach someone while passive (it follows its player)
                    if (passiveHealing)
                    {
                        BotGroupSupport.CancelHeal(BotBody);
                        return isCastingHeal;
                    }
                    // Walk into range; give up on a member it cannot get closer to.
                    return ApproachSupportTarget(spellTarget) || isCastingHeal;
                }
                _supportReach.Reached(spellTarget);

                if (!spellToCast.IsInstantCast)
                {
                    // GitHub #45: the cast it is already making of this spell on this target is left to finish. A healer
                    // with an instant heal ready comes through here on every think, and the cure it had chosen again
                    // (the nearsight or mez is still on the target until the cast lands) cancelled and restarted its
                    // own 6-second Cure Nearsight until the nearsight ran out.
                    if (KeepsCastInProgress(BotBody.IsCasting, BotBody.castingComponent?.SpellHandler?.Spell, spellToCast,
                            BotBody.castingComponent?.SpellHandler?.Target, spellTarget))
                        return true;
                    if (BotBody.IsCasting)
                        BotBody.StopCurrentSpellcast();
                    else if (BotBody.IsAttacking)
                        BotBody.StopAttack();
                    // Owner 2026-11-08: melee healers (Shaman, Friar, Warden ...) turned between the monster and the groupmate
                    // over and over before a heal went off: right after its own swing a bot cannot start a cast (the swing's
                    // self-interrupt), so each try turned it to the groupmate, failed silently, and the melee follow turned it
                    // back. Now it stops swinging and following once, stays facing the monster until the swing has passed,
                    // then casts once; after the heal the normal melee start turns it back.
                    BotBody.StopFollowing();
                    if (HealWaitsForSwing(spellToCast.IsInstantCast, spellToCast.Uninterruptible,
                            BotBody.IsBeingSelfInterrupted, BotBody.IsBeingInterruptedByOther))
                        return true;
                }

                GameObject oldTarget = BotBody.TargetObject;
                BotBody.TargetObject = spellTarget;
                bool quickHeal = !spellToCast.IsInstantCast && BotBody.IsBeingInterruptedByOther && numEmergency > 0 &&
                                 BotBody.ArmQuickCast(spellToCast);
                startedCasting = BotBody.CastSpell(spellToCast, m_mobSpellLine, false);
                if (quickHeal)
                    BotBody.DisarmQuickCast();

                if (!startedCasting)
                {
                    BotGroupSupport.CancelHeal(BotBody);
                    BotBody.TargetObject = oldTarget;
                }
                else
                {
                    if (spellToCast.IsInstantCast)
                    {
                        BotBody.TargetObject = oldTarget;
                        startedCasting = false;
                    }
                    else if (spellToCast.SpellType == eSpellType.CureDisease || spellToCast.SpellType == eSpellType.CurePoison)
                        nextCureTime = GameLoop.GameLoopTime + CureDelay;
                }
            }

            return startedCasting || isCastingHeal;
        }

        /// <summary>
        /// Pure (GitHub #45): the bot is already casting the chosen spell on the chosen target, so that cast goes on - it
        /// is not cancelled and started again.
        /// </summary>
        public static bool KeepsCastInProgress(bool casting, Spell current, Spell chosen, object currentTarget, object chosenTarget) =>
            casting && current != null && chosen != null && (ReferenceEquals(current, chosen) || current.ID == chosen.ID) &&
            currentTarget != null && ReferenceEquals(currentTarget, chosenTarget);

        private bool CanHealRelicCarrier(GameLiving carrier)
        {
            Spell[] heals = [BotBody.HealInstant, BotBody.HealBig, BotBody.HealEfficient, BotBody.HealOverTime];
            return heals.Any(spell => spell != null && BotBody.IsWithinRadius(carrier, BotBody.castingComponent.CalculateSpellRange(spell)));
        }

        private long _nextCompanionResurrectionTick;

        private bool TryResurrectCompanionOwner()
        {
            GameBot bot = BotBody;
            // hired companions too (audit 2026-11-03): their player has no BotManager Owner, so they raised only a
            // corpse already in range, after re-buffing the group, and never sat for the power to do it
            bool hired = HiredCompanions165.IsHired(bot) || bot?.IsInvitedGamebot165 == true;   // or a gamebot its player invited
            if (bot?.IsTemporaryGroupHelper != true && !hired) return false;
            GamePlayer owner = hired ? bot.CompanionPlayer : bot.Owner;
            Spell resurrection = bot.ResurrectionSpell;
            if (owner == null || owner.IsAlive || resurrection == null || owner.ObjectState != GameObject.eObjectState.Active ||
                owner.CurrentRegionID != bot.CurrentRegionID ||
                !TemporaryCompanionRecovery.CanPrioritizeOwnerResurrection(true, bot.IsAlive, owner.IsAlive,
                    bot.Group != null && bot.Group == owner.Group && bot.Group.IsInTheGroup(bot) && bot.Group.IsInTheGroup(owner),
                    bot.PlayerGroupLeader == owner, resurrection != null && resurrection.Level <= bot.Level,
                    TemporaryCompanionRecovery.HasPartyCombat(bot)))
                return false;

            if (bot.IsCasting) return true;
            if (bot.IsCrowdControlled || bot.IsSilenced) return false;
            if (GameLoop.GameLoopTime < _nextCompanionResurrectionTick) return true;
            _nextCompanionResurrectionTick = GameLoop.GameLoopTime + 2000;
            // Use the native resurrection cost for this corpse, not the generic
            // spell.Power field (resurrection overrides its power calculation).
            var handler = ScriptMgr.CreateSpellHandler(bot, resurrection, m_mobSpellLine);
            if (handler == null) return false;
            int requiredPower = Math.Max(bot.PowerCost(resurrection), handler.PowerCost(owner));
            if (bot.Mana < requiredPower)
            {
                if (hired) bot.BeginRecoveryRest();
                else bot.BeginTemporaryCompanionRest();
                return true;
            }
            if (owner.TempProperties.GetProperty<GameLiving>("RESURRECT_CASTER") != null)
                return true; // The normal accept/decline prompt is already open.
            bot.WakeRecoveryRest();
            if (!CheckHealSpell(resurrection)) return true;
            return TryResurrectGroupMember();
        }

        private long _nextResurrectionBlockReportTick;

        // Resurrection outranks chant and song upkeep. A Paladin's chant (or a
        // bard's song) was queued early in the think; the resurrection later in
        // the same think was then refused because a cast was already pending
        // (31 Paladin "cast refused" reports in one evening run, Albion holding
        // 570 of 950 resurrection timeouts). Upkeep resumes once nobody waits.
        private bool HasResurrectionDuty()
        {
            GameBot bot = BotBody;
            Spell resurrection = bot?.ResurrectionSpell;
            if (resurrection == null || bot.Group == null || !bot.IsAlive)
                return false;
            int range = bot.castingComponent.CalculateSpellRange(resurrection);
            bool corpseWaiting = bot.Group.GetMembersInTheGroup().Any(member => member != bot && !member.IsAlive &&
                member.ObjectState == GameObject.eObjectState.Active && member.CurrentRegionID == bot.CurrentRegionID &&
                bot.IsWithinRadius(member, range) && member.TempProperties.GetProperty<GameLiving>("RESURRECT_CASTER") == null);
            // owner 2026-11-10: sitting for the power to raise also outranks upkeep, so songs and buffs don't spend it
            return BotGroupSupport.ResurrectionOutranksUpkeep(true, true, bot.Mana >= bot.PowerCost(resurrection) ||
                BotGroupSupport.IsRestingForResurrection(bot, null, GameLoop.GameLoopTime), corpseWaiting);
        }

        // Diagnostic for failed group resurrections (an in-range resurrector stood idle
        // for the whole corpse wait in hundreds of cases). At most one line per bot per
        // minute, and only while a dead group member is actually within spell range.
        private void ReportResurrectionBlock(Spell resurrection, string reason, GameLiving corpse = null)
        {
            if (Body.Group == null || !BotBody.IsAutonomousWorldBot || GameLoop.GameLoopTime < _nextResurrectionBlockReportTick)
                return;
            int range = BotBody.castingComponent.CalculateSpellRange(resurrection);
            corpse ??= Body.Group.GetMembersInTheGroup().FirstOrDefault(member => member != Body && !member.IsAlive &&
                member.ObjectState == GameObject.eObjectState.Active && member.CurrentRegionID == Body.CurrentRegionID &&
                Body.IsWithinRadius(member, range));
            if (corpse == null)
                return;
            _nextResurrectionBlockReportTick = GameLoop.GameLoopTime + 60_000;
            reason ??= BotGroupSupport.DescribeResurrectionBlock(BotBody, resurrection, corpse);
            log.Info($"AUTONOMOUS_RESURRECTION_BLOCKED bot=\"{BotBody.Name}\" class=\"{BotBody.ClassName}\" corpse=\"{corpse.Name}\" " +
                     $"distance={Body.GetDistanceTo(corpse)} spell=\"{resurrection.Name}\" mana={BotBody.Mana}/{BotBody.MaxMana} " +
                     $"inCombat={Body.InCombat} reason=\"{reason}\"");
        }

        private long _nextSiegeRaiseScanTick;

        /// <summary>
        /// A siege attacker's healer raises fallen realm-mates near it, not only its own party (AutonomousSiegeResurrection).
        /// Out of combat only, as players raised between waves; one corpse per healer at a time.
        /// </summary>
        private bool TryResurrectSiegeComrade()
        {
            Spell resurrection = BotBody.ResurrectionSpell;
            long now = GameLoop.GameLoopTime;
            // In the battle too, as players raised from the back line: only while nothing has hit the healer for a few
            // seconds and no enemy stands by the corpse. Out of combat only, no siege corpse was ever raised (run 22: 48
            // Midgard dead at Dun Dagda, none raised, each waiting for a raise before walking back from its muster).
            bool fighting = Body.InCombat;
            if (resurrection == null || now < _nextSiegeRaiseScanTick || Body.IsCasting)
                return false;
            _nextSiegeRaiseScanTick = now + 3_000;
            // An RvR healer of the realm that is not in the siege army itself (a roaming group at the battle) raises the
            // army's dead too, as any realm-mate would (run 27: 20-100 such checks per 5 minutes with dead beside them).
            // Battlegrounds fix 2026-11-12 (G2): a PvP-focus healer in its battleground counts as the realm's army there
            // (battleground keeps have no siege event and its bots no group), so it raises its realm's dead too.
            if (!AutonomousSiegeResurrection.IsSiegeAttacker(BotBody) &&
                !((AutonomousObjectiveAssignments.Is(BotBody, eAutonomousObjectiveKind.RvR) ||
                   AutonomousObjectiveAssignments.IsBattlegroundPvp(BotBody)) && AutonomousSiegeResurrection.HasFallenComradeNear(BotBody)))
            {
                _nextSiegeRaiseScanTick = now + 15_000;
                return false;
            }
            if (fighting && now - Body.LastAttackedByEnemyTick < AutonomousSiegeResurrection.QuietMilliseconds)
            {
                AutonomousSiegeResurrection.Note(AutonomousSiegeResurrection.Check.UnderAttack);
                return false;
            }
            if (!AutonomousSiegeResurrection.CanRaise(BotBody) || !CheckHealSpell(resurrection))
            {
                AutonomousSiegeResurrection.Note(AutonomousSiegeResurrection.Check.CannotCast);
                return false;
            }
            GameBot corpse = AutonomousSiegeResurrection.ClaimCorpse(BotBody, fighting, out bool enemyBlocked);
            AutonomousSiegeResurrection.Note(corpse != null ? AutonomousSiegeResurrection.Check.Corpse :
                enemyBlocked ? AutonomousSiegeResurrection.Check.EnemyByCorpse : AutonomousSiegeResurrection.Check.NoCorpse);
            if (corpse == null)
                return false;
            int range = BotBody.castingComponent.CalculateSpellRange(resurrection);
            if (!BotBody.IsWithinRadius(corpse, range) || !BotGroupSupport.HasCorpseLineOfSight(BotBody, corpse))
            {
                Vector3 current = new(BotBody.X, BotBody.Y, BotBody.Z);
                Vector3 desired = new(corpse.X, corpse.Y, corpse.Z);
                AutonomousThreatAwarePathing.SafeStep step = AutonomousThreatAwarePathing.ChooseStep(BotBody, current, desired, 900);
                BotBody.PathTo(step.Position, BotBody.MaxSpeed);
                return true;
            }
            GameObject previous = BotBody.TargetObject;
            BotBody.attackComponent.StopAttack();
            BotBody.StopFollowing();
            BotBody.StopMoving();
            BotBody.TargetObject = corpse;
            if (BotBody.CastSpell(resurrection, m_mobSpellLine, false))
            {
                log.Info($"AUTONOMOUS_SIEGE_RESURRECTION healer=\"{BotBody.Name}\" corpse=\"{corpse.Name}\" realm={BotBody.Realm} " +
                         $"region={BotBody.CurrentRegionID} at={BotBody.X},{BotBody.Y},{BotBody.Z}");
                return true;
            }
            BotBody.TargetObject = previous;
            return false;
        }

        private bool TryResurrectGroupMember()
        {
            Spell resurrection = BotBody.ResurrectionSpell;
            if (Body.Group != null)
            {
                if (Body.IsCasting && Body.castingComponent.SpellHandler?.Spell?.SpellType == eSpellType.Resurrect)
                    return BotGroupSupport.ContinueResurrection(BotBody);
                if (resurrection == null) return false;
                if (Body.IsCasting || !CheckHealSpell(resurrection))
                {
                    ReportResurrectionBlock(resurrection, Body.IsCasting ? "casting another spell" :
                        BotBody.Mana < BotBody.PowerCost(resurrection) ? $"not enough power to cast ({BotBody.Mana}/{BotBody.PowerCost(resurrection)})" :
                        BotBody.IsBeingInterruptedByOther ? "being interrupted" : "resurrection on recast");
                    return TryRestForResurrection(resurrection);
                }
                GameLiving corpse = BotGroupSupport.ReserveResurrection(BotBody, resurrection);
                if (corpse != null)
                {
                    GameObject previous = BotBody.TargetObject;
                    BotBody.attackComponent.StopAttack();
                    BotBody.StopFollowing();
                    BotBody.StopMoving();
                    BotBody.TargetObject = corpse;
                    if (BotBody.CastSpell(resurrection, m_mobSpellLine, false)) return true;
                    BotBody.TargetObject = previous;
                    BotGroupSupport.CancelResurrection(BotBody);
                    ReportResurrectionBlock(resurrection, BotBody.castingComponent.HasPendingSkillRequests
                        ? "another cast was already queued" : "the resurrection cast was refused", corpse);
                    return false;
                }
                ReportResurrectionBlock(resurrection, null);
                if (TryRestForResurrection(resurrection))
                    return true;
            }
            if (resurrection == null || Body.Group == null || Body.InCombat || Body.Group.GetMembersInTheGroup().Any(m => m.IsAlive && m.InCombat) || Body.IsCasting || !CheckHealSpell(resurrection))
                return false;

            int range = BotBody.castingComponent.CalculateSpellRange(resurrection);
            bool regrouping = AutonomousBotGroupCoordinator.IsRecovering(BotBody);
            GameLiving deadMember = Body.Group.GetMembersInTheGroup()
                .Where(member => member != Body && !member.IsAlive && member.ObjectState is GameObject.eObjectState.Active &&
                                 member.CurrentRegionID == Body.CurrentRegionID)
                // Raise nearby casualties, but do not abandon recovery to chase
                // a distant corpse back into the failed pull/dungeon room.
                // ... unless the party grants this resurrector a safe walk to it (item 17, 2026-11-11: 153 of 235
                // overnight timeouts were regrouping parties whose healers stood 2,800 units from the corpse)
                .Where(member => BotBody.IsWithinRadius(member, range) ||
                                 AutonomousBotGroupCoordinator.MayWalkToCorpse(BotBody, member, regrouping))
                // A player's companion raises only corpses near its player, and gives up on one it cannot reach (owner
                // 2026-11-05: a hired cleric kept walking out of its leash to a dead invited gamebot until it faded).
                .Where(member => !LeavesPlayerToSupport(member) && !_supportReach.IsSkipped(member, GameLoop.GameLoopTime))
                .OrderBy(member => member == AssistedPlayer ? 0 : 1)
                .ThenBy(Body.GetDistanceTo)
                .FirstOrDefault();
            if (deadMember == null)
                return false;

            if (!BotBody.IsWithinRadius(deadMember, range) || !BotGroupSupport.HasCorpseLineOfSight(BotBody, deadMember))
            {
                if (!_supportReach.KeepApproaching(deadMember, Body.GetDistanceTo(deadMember), GameLoop.GameLoopTime))
                    return false;   // no progress for 12 s: skipped for 30 s
                Vector3 current = new(BotBody.X, BotBody.Y, BotBody.Z);
                Vector3 desired = new(deadMember.X, deadMember.Y, deadMember.Z);
                AutonomousThreatAwarePathing.SafeStep step = AutonomousThreatAwarePathing.ChooseStep(BotBody, current, desired, 900);
                BotBody.PathTo(step.Position, BotBody.MaxSpeed);
                return true;
            }

            // In-range corpses must obtain the shared reservation above.
            return false;
        }

        /// <summary>
        /// Owner 2026-11-10: a gamebot resurrector out of combat whose only problem is power sits until it has the raise's cost
        /// (42 of 101 resurrection timeouts in 48 h: the healer stood near the corpse, short of power, until the dead released).
        /// The corpse it sits for waits up to 120 s (AutonomousBotGroupCoordinator.PveCorpseRecovery). Spell costs unchanged.
        /// </summary>
        private bool TryRestForResurrection(Spell resurrection)
        {
            GameBot bot = BotBody;
            Group group = bot?.Group; // owner 2026-11-10 (review): read once, another thread can clear it
            if (resurrection == null || bot?.IsAutonomousWorldBot != true || bot.IsPlayerLedGroup || group == null || !bot.IsAlive)
            {
                BotGroupSupport.ClearRestingForResurrection(bot);
                return false;
            }
            int range = bot.castingComponent.CalculateSpellRange(resurrection);
            GameLiving corpse = group.GetMembersInTheGroup()
                .Where(member => member != bot && !member.IsAlive && member.ObjectState == GameObject.eObjectState.Active &&
                                 member.CurrentRegionID == bot.CurrentRegionID && bot.IsWithinRadius(member, range) &&
                                 member.TempProperties.GetProperty<GameLiving>("RESURRECT_CASTER") == null)
                .OrderBy(bot.GetDistanceTo)
                .FirstOrDefault();
            bool partyCombat = bot.InCombat || group.GetMembersInTheGroup().Any(member => member.IsAlive && member.InCombat);
            int required = corpse == null ? 0 : BotGroupSupport.ResurrectionPowerCost(bot.MaxMana, bot.Level, corpse.Level,
                bot.PowerCost(resurrection));
            bool recast = resurrection.HasRecastDelay && bot.GetSkillDisabledDuration(resurrection) > 0;
            if (!BotGroupSupport.ShouldRestForResurrection(corpse != null && BotGroupSupport.HasCorpseLineOfSight(bot, corpse),
                    partyCombat, bot.IsCasting, bot.IsBeingInterruptedByOther || bot.IsCrowdControlled || bot.IsSilenced, recast,
                    bot.Mana, bot.MaxMana, required) || BotRestRecovery.BlocksRest(bot))
            {
                BotGroupSupport.ClearRestingForResurrection(bot);
                return false;
            }
            if (!bot.BeginRecoveryRest())
            {
                BotGroupSupport.ClearRestingForResurrection(bot);
                return false;
            }
            BotGroupSupport.NoteRestingForResurrection(bot, corpse, GameLoop.GameLoopTime);
            return true;
        }

        #endregion
    }
}
