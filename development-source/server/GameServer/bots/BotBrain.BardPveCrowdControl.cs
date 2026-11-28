using System;
using System.Collections.Generic;
using System.Linq;
using DOL.GS;

namespace DOL.AI.Brain
{
    public partial class BotBrain
    {
        private readonly Dictionary<GameLiving, long> _bardPveMezRetryUntil = new();
        private long _nextBardPveMezLook;

        private bool TryBardPveAddMez()
        {
            GameBot bard = BotBody;
            Group group = bard?.Group;
            if (bard?.CharacterClass?.ID != (int)eCharacterClass.Bard ||
                !BardBotCrowdControlPolicy.HasGroupForPveAdd(group?.MemberCount) ||
                bard.CanCastCrowdControlSpells != true ||
                bard.IsIncapacitated || bard.IsCasting ||
                bard.castingComponent?.HasPendingSkillRequests == true ||
                GameLoop.GameLoopTime < _nextBardPveMezLook ||
                bard.IsPlayerCompanion && !CompanionControls165.AddControlRuns(bard))   // M7: the Crowd control row
                return false;
            _nextBardPveMezLook = GameLoop.GameLoopTime + 1_000;   // owner 2026-11-08: once a second, as add control

            // M5 (veteran review 2026-11-11): the group's fight board, not a member list and area search of its own
            GroupFightBoard board = FightBoard();
            HashSet<GameLiving> members = board.MemberSet;
            if (board.Members.Length < 2)
                return false;

            // The normal selector already owns the primary kill target. An add
            // must be attacking this exact group while somebody is fighting a
            // distinct monster; merely seeing a neutral mob cannot start a pull.
            HashSet<GameLiving> selectedTargets = board.SelectedTargets;
            HashSet<GameLiving> activeTargets = board.ActiveTargets;
            bool batteringDoor = activeTargets.Count == 0 && IsBatteringKeepDoor(board);
            if (activeTargets.Count == 0 && !batteringDoor)
                return false;

            long now = GameLoop.GameLoopTime;
            foreach (GameLiving expired in _bardPveMezRetryUntil
                         .Where(pair => now >= pair.Value || pair.Key?.IsAlive != true)
                         .Select(pair => pair.Key).ToArray())
                _bardPveMezRetryUntil.Remove(expired);

            Spell[] singleTargetMezzes = bard.CrowdControlSpells
                .Where(spell => spell != null && spell.SpellType == eSpellType.Mesmerize &&
                    spell.Target == eSpellTarget.ENEMY && spell.Radius <= 0 && spell.Level <= bard.Level)
                .OrderByDescending(spell => spell.Level).ToArray();
            // Q26: the Music area mez (Captivate Audience ... Legions) on clustered adds, through the safe area-mez rule
            Spell[] areaMezzes = bard.CrowdControlSpells
                .Where(spell => spell != null && spell.SpellType == eSpellType.Mesmerize &&
                    spell.Target == eSpellTarget.ENEMY && spell.Radius > 0 && spell.Level <= bard.Level &&
                    !BotControlSpells.IsLongReuse(spell))
                .OrderByDescending(spell => spell.Level).ToArray();
            if (singleTargetMezzes.Length == 0 && areaMezzes.Length == 0)
                return false;

            // owner 2026-11-08: no add a pet or elemental of the group targets, none hurt or DoT'd lately
            HashSet<GameLiving> petTargets = board.PetTargets;
            List<GameNPC> safeAdds = new();
            foreach (GameNPC add in board.NpcsOnGroup
                         .Where(npc => bard.IsWithinRadius(npc, 1800))
                         .Where(npc => npc?.IsAlive == true && IsAddControlRealm(npc) &&
                             !BotPvpCrowdControl.PlayerLike(npc) && CanDefendAgainst(npc))
                         .OrderBy(bard.GetDistanceTo).Take(24))
            {
                bool attacksMember = add.TargetObject is GameLiving victim && members.Contains(victim);
                bool alreadyControlled = add.IsMezzed || add.IsStunned ||
                    add.effectListComponent.ContainsEffectForEffectType(eEffect.MezImmunity) ||
                    add.effectListComponent.ContainsEffectForEffectType(eEffect.NPCMezImmunity);
                long retryUntil = _bardPveMezRetryUntil.GetValueOrDefault(add);
                if (BardBotCrowdControlPolicy.IsSafePveAdd(true,
                        activeTargets.Any(target => target != add) || SiegeGuardAdd(add, batteringDoor), attacksMember,
                        selectedTargets.Contains(add), Body.TargetObject == add,
                        add.HealthPercent, alreadyControlled, now, retryUntil) &&
                    IsUntouchedAdd(add, petTargets))
                    safeAdds.Add(add);
            }
            // owner 2026-11-08: Hypnotic Harmony (instant, 10-minute reuse) only by the long-reuse rule, as in TryPveAddControl
            bool emergency = safeAdds.Count >= EmergencyAddCount;
            bool castTimeBlocked = bard.IsBeingInterruptedByOther ||
                !Array.Exists(singleTargetMezzes, spell => spell.CastTime > 0 && !BotControlSpells.IsLongReuse(spell) &&
                    bard.GetSkillDisabledDuration(spell) <= 0 && bard.Mana >= bard.PowerCost(spell));

            if (safeAdds.Count >= 2 && TryBardAreaMez(bard, safeAdds, areaMezzes, activeTargets, selectedTargets, now))
                return true;

            foreach (GameNPC add in safeAdds)
            {
                foreach (Spell spell in singleTargetMezzes)
                {
                    if (BotControlSpells.IsLongReuse(spell) &&
                        !AllowsLongReuseControl(Classic165Mode.IsOn, emergency, true, castTimeBlocked))
                        continue;
                    if (bard.Mana < bard.PowerCost(spell) || bard.GetSkillDisabledDuration(spell) > 0 ||
                        spell.CastTime > 0 && bard.IsBeingInterruptedByOther ||
                        !bard.IsWithinRadius(add, spell.CalculateEffectiveRange(bard)) ||
                        !NeedsOffensiveSpellApplication(add, spell) ||
                        !BotGroupSupport.HasCorpseLineOfSight(bard, add))
                        continue;

                    // The native casting request captures this target when it
                    // is queued. Restore the Bard's kill target immediately so
                    // the next combat decision resumes normal engagement.
                    if (_bardPveMezRetryUntil.Count >= 32)
                    {
                        GameLiving oldest = _bardPveMezRetryUntil
                            .OrderBy(pair => pair.Value).First().Key;
                        _bardPveMezRetryUntil.Remove(oldest);
                    }
                    // Q20: a resisted mez is tried again once the cast has landed, not 15 s later
                    _bardPveMezRetryUntil[add] = now + AddControlRetryMilliseconds(spell.CastTime);
                    GameObject previous = bard.TargetObject;
                    bard.TargetObject = add;
                    try
                    {
                        if (spell.CastTime > 0)
                        {
                            bard.StopMovingOnPath();
                            bard.StopMoving();
                        }
                        return CheckOffensiveSpells(spell);
                    }
                    finally
                    {
                        bard.TargetObject = previous;
                    }
                }
            }

            return false;
        }

        private bool TryBardAreaMez(GameBot bard, List<GameNPC> safeAdds, Spell[] areaMezzes,
            HashSet<GameLiving> activeTargets, HashSet<GameLiving> selectedTargets, long now)
        {
            foreach (Spell spell in areaMezzes)
            {
                if (bard.Mana < bard.PowerCost(spell) || bard.GetSkillDisabledDuration(spell) > 0 ||
                    spell.CastTime > 0 && bard.IsBeingInterruptedByOther)
                    continue;
                foreach (GameNPC add in safeAdds)
                {
                    int caught = 0;
                    foreach (GameNPC other in safeAdds)
                        if (add.IsWithinRadius(other, spell.Radius))
                            caught++;
                    if (caught < 2)
                        continue;
                    bool killNear = activeTargets.Concat(selectedTargets).Any(target =>
                        target is GameObject kill && add.IsWithinRadius(kill, spell.Radius + BotAoePolicy.SafetyMargin));
                    if (killNear || !bard.IsWithinRadius(add, spell.CalculateEffectiveRange(bard)) ||
                        !NeedsOffensiveSpellApplication(add, spell) || !BotGroupSupport.HasCorpseLineOfSight(bard, add))
                        continue;
                    if (!BardBotCrowdControlPolicy.AreaMezWorthwhile(caught, false, EvaluateBlast(add, spell).Vetoed))
                        continue;
                    foreach (GameNPC other in safeAdds)
                        if (add.IsWithinRadius(other, spell.Radius))
                            _bardPveMezRetryUntil[other] = now + AddControlRetryMilliseconds(spell.CastTime);
                    GameObject previous = bard.TargetObject;
                    bard.TargetObject = add;
                    try
                    {
                        if (spell.CastTime > 0)
                        {
                            bard.StopMovingOnPath();
                            bard.StopMoving();
                        }
                        if (CheckOffensiveSpells(spell))
                            return true;
                    }
                    finally
                    {
                        bard.TargetObject = previous;
                    }
                }
            }
            return false;
        }
    }
}
