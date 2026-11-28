using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using DOL.AI.Brain;

namespace DOL.GS
{
    /// <summary>
    /// A hired companion's own choices, set by the player in the new client's Pets / Songs tab and kept in the hire
    /// record until changed (owner 2026-11-05, reset-day S33 / C17): charm on or off (Sorcerer, Minstrel and any class
    /// that has learned a charm; a hired Hunter keeps its wolf), the Cabalist / Enchanter pet, the Bonedancer's sub-pets
    /// (their level total within the commander's 75), and the song or chant held in a fight, standing around and
    /// travelling (Bard, Minstrel, Skald, Paladin). "Automatic" is the bot's own choice. Gamebots ignore all of this.
    ///
    /// Protocol (hire op 14, one per option row, then one with kind 0xFF): [0] row, [1] kind (1 charm, 2 pet, 3 sub-pet,
    /// 4 song), [2] slot, [3] chosen index, [4] number of choices, [5..6] used u16, [7..8] cap u16, [9..] choice names,
    /// each zero-ended. Verbs: charm (row) on|off, pet (row) (i), subpet (row) (slot 1-3) (i), song (row) (slot) (i)
    /// (song slot 1 in a fight, 2 standing around, 3 travelling).
    ///
    /// Kinds 5 and up are generic (client protocol, 2026-11-05): names[0] is the row's caption, the rest the choices,
    /// [3] the chosen index into the choices without the caption; the client sends "option (row) (kind) (slot) (i)".
    /// Each is one class AI choice from the 2026-11-05 class audit (see Generics); choice 0 is the default.
    /// 2026-11-11: kind 29 is the pure casters' "Save power" row (key savepower, third choice Strict; kind 26 stays for
    /// everyone else), kind 30 "Attack spells" (casters); every Role row (13-15) has a fourth choice, 3 "Melee and heal".
    /// </summary>
    public static class CompanionOptions165
    {
        public const byte KindCharm = 1, KindPet = 2, KindSubPet = 3, KindSong = 4;
        public const byte KindMainShroom = 19, KindShroomDamage = 20, KindShroomVents = 21, KindShroomAblative = 22,
            KindShroomSpore = 23, KindShroomBurst = 24, KindShroomHeal = 25;
        public const byte KindAggro = 5, KindHealAt = 6, KindDebuff = 7, KindBolts = 8, KindBerserk = 9, KindGuard = 10,
            KindHunterPet = 11, KindStealthOpen = 12, KindHealerRole = 13, KindShamanRole = 14, KindFriarRole = 15,
            KindWardenRole = 46,
            KindLifeTransfer = 16, KindAmnesia = 17, KindPerform = 18, KindSavePower = 26, KindPositioning = 27,
            KindDebuffLine = 28, KindSavePowerCaster = 29, KindAttackSpells = 30;
        /// <summary>Companion controls (veteran review M7 and the owner's twist option, 2026-11-11; CompanionControls165).</summary>
        public const byte KindTwist = 40, KindStance = 41, KindCrowdControl = 42, KindPetJob = 43, KindWeapon = 44,
            KindStag = 47, KindCriticalShot = 48, KindOpeningStun = 49, KindEngage = 50, KindSlamSwap = 51,
            KindTripleWield = 52, KindBuffPriority = 53, KindRunemasterBladeturn = 54, KindPetFocusShield = 55,
            KindMysticCrystalLore = 56, KindPotions = 57, KindGroupHealing = 58;
        public const string MysticCrystalLoreKey = "mcl", PotionsKey = "potions", GroupHealingKey = "groupheals";
        private const string PetFocusShieldKey = "petfocusshield";

        /// <summary>The fourth Role choice of every healer's Role row (owner 2026-11-11; CompanionSpellOptions165).</summary>
        public const string MeleeHealChoice = "Melee and heal, no attack spells (melee interrupts casts)";
        public static readonly HashSet<string> Verbs = ["charm", "pet", "subpet", "song", "option"];

        /// <summary>A generic AI choice row: its kind, stored key, caption, choices (0 = default) and who gets it.</summary>
        private sealed record Generic(byte Kind, string Key, string Caption, string[] Choices, Func<GameBot, bool> Applies);

        private static readonly Generic[] Generics =
        [
            // melee: taunt styles to hold a monster, or detaunt styles to give it back to the tank (owner 2026-11-06: the
            // "Aggro" row renamed "Taunt"; stored values unchanged - 1 was "Hold it", 2 "Shed it"). Automatic: only the
            // tank-class companion taunts, never off the player as main tank, never under 40% (BotMeleeStylePolicy.MayTaunt).
            new(KindAggro, "aggro", "Taunt", ["Automatic", "Always", "Never (detaunt)"],
                bot => bot.StylesTaunt?.Count > 0 || bot.StylesDetaunt?.Count > 0),
            new(KindHealAt, "healat", "Heal at", ["Automatic", "Early (90%)", "Late (50%)"], bot => bot.CanCastHealSpells),
            new(KindMysticCrystalLore, MysticCrystalLoreKey, "Mystic Crystal Lore",
                ["Automatic (below 40% power)", "Save until below 25% power", "Off"],
                bot => bot.GetAbility(AutonomousBotRealmAbilities.MysticCrystalLore) != null),
            new(KindPotions, PotionsKey, "Potions at or below",
                [$"Auto: HP {BotPotions165.HealWhenHitPercent}% ({BotPotions165.HealAfterKillPercent}% after kill with adds), power/end. {BotPotions165.PowerPercent}%",
                 $"Also between fights: HP {BotPotions165.HealAfterKillPercent}%, power {BotPotions165.RecoveryPowerPercent}%, end. {BotPotions165.EndurancePercent}%",
                 "Off"], _ => true),
            new(KindGroupHealing, GroupHealingKey, "Group heals",
                ["Automatic", "Prefer spread heal", "Prefer group heal", "Single-target outside emergencies"],
                bot => bot.HealGroup != null || bot.HealSpread != null || bot.HealInstantGroup != null ||
                    bot.HealOverTimeGroup != null || bot.HealOverTimeInstantGroup != null),
            new(KindDebuff, "debuff", "Debuff before nuking", ["Automatic", "Always", "Never"],
                bot => (bot.HarmfulSpells ?? []).Concat(bot.InstantHarmfulSpells ?? []).Any(BotCasterPriority.IsDebuff) ||
                       DebuffLines(bot).Count > 0),
            new(KindBolts, "bolts", "Bolts", ["Automatic", "Nukes only"], bot => bot.CanCastBolts),
            new(KindBerserk, "berserk", "Berserk", ["Automatic (hard fights)", "On cooldown", "Off"],
                bot => bot.HasAbility(Abilities.Berserk)),
            new(KindGuard, "guard", "Guard and protect", ["Automatic", "You", "The healer", "Off"],
                bot => bot.HasAbility(Abilities.Guard) || bot.HasAbility(Abilities.Protect) || bot.HasAbility(Abilities.Intercept)),
            new(KindHunterPet, "hunterpet", "Pet", ["Automatic (on)", "Off"],
                bot => (eCharacterClass)bot.CharacterClass.ID == eCharacterClass.Hunter),
            // assassins: hide and open with Backstab / Perforate Artery (BotBrain.StealthOpener)
            new(KindStealthOpen, "stealthopen", "Open from stealth", ["Automatic (on)", "Off"],
                bot => BotPoisonSupply.IsAssassin((eCharacterClass)bot.CharacterClass.ID) &&
                       bot.Styles?.Any(style => style.StealthRequirement) == true),
            // Roles (one stored key "role"; the rows differ by class). Prima / period forums: a Cleric either smites and
            // does crowd control or heals; a Shaman buffs, heals and rezzes from the back; a Friar is the off-healer.
            new(KindHealerRole, "role", "Role", ["Automatic (heal and buff)", "Heal first, attack when nobody is hurt",
                    "Attack, heal only emergencies", MeleeHealChoice],
                bot => CompanionSpellOptions165.UsesHealerRoleRow((eCharacterClass)bot.CharacterClass.ID)),
            new(KindShamanRole, "role", "Role", ["Automatic (melee and cast)", "Stay back and heal", "Caster, no melee",
                    MeleeHealChoice],
                bot => (eCharacterClass)bot.CharacterClass.ID == eCharacterClass.Shaman),
            new(KindFriarRole, "role", "Role", ["Automatic (fight and heal)", "Off-healer (heals under 40% in a fight)",
                    "Main healer (stays back)", MeleeHealChoice],
                bot => (eCharacterClass)bot.CharacterClass.ID == eCharacterClass.Friar),
            // M7: the Warden too (period: a Warden backs up the healer with its heals and bladeturn, or is the group's
            // healer in a small group) - the Friar's choices on the same stored key
            new(KindWardenRole, "role", "Role", ["Automatic (fight and heal)", "Off-healer (heals under 40% in a fight)",
                    "Main healer (stays back)"],
                bot => (eCharacterClass)bot.CharacterClass.ID == eCharacterClass.Warden),
            new(KindLifeTransfer, "lifetransfer", "Life transfer", ["Automatic (groupmate under 35%)", "Off"],
                bot => bot.Spells?.Any(spell => spell.SpellType == eSpellType.LifeTransfer) == true),
            new(KindAmnesia, "amnesia", "Amnesia", ["Automatic (interrupt enemy casters in RvR; send monsters on me back to my own pet, if I have one)", "Off"],
                bot => bot.Spells?.Any(spell => spell.SpellType == eSpellType.Amnesia) == true),
            // Performers (Prima: a Bard's group role is to prevent downtime - the endurance song in battle, mez every add,
            // heal the Druid; a Minstrel plays a "passive game" of songs and shouts or an "active game" in melee; period
            // forums: the "Battlebard" melee build)
            // Animist shrooms (owner 2026-11-05): which main shroom, and each kind of field shroom on or off - only the
            // kinds this Animist knows are offered (BotAnimistPolicy.KnownKinds)
            new(KindMainShroom, "mainshroom", "Main shroom", ["Automatic", "Damage", "Taunt"],
                bot => BotAnimistPolicy.KnownKinds(bot).Contains("damagemain") && BotAnimistPolicy.KnownKinds(bot).Contains("tauntmain")),
            new(KindShroomDamage, "shroomdamage", "Damage shrooms", ["Automatic (on)", "Off"],
                bot => BotAnimistPolicy.KnownKinds(bot).Contains("damage")),
            new(KindShroomVents, "shroomvents", "Resist vents", ["Automatic (on)", "Off"],
                bot => BotAnimistPolicy.KnownKinds(bot).Contains("vents")),
            new(KindShroomAblative, "shroomablative", "Ablative shrooms", ["Automatic (on)", "Off"],
                bot => BotAnimistPolicy.KnownKinds(bot).Contains("ablative")),
            new(KindShroomSpore, "shroomspore", "Debuff spores", ["Automatic (on)", "Off"],
                bot => BotAnimistPolicy.KnownKinds(bot).Contains("spore")),
            new(KindShroomBurst, "shroomburst", "Shroom burst", ["Automatic (on)", "Off"],
                bot => BotAnimistPolicy.KnownKinds(bot).Contains("burst")),
            new(KindShroomHeal, "shroomheal", "Heal the main shroom", ["Automatic (on)", "Off"],
                bot => BotAnimistPolicy.KnownKinds(bot).Contains("healmain")),
            new(KindPerform, "perform", "Fight", ["Automatic", "Support (stay back: songs, spells, no melee)", "Melee"],
                bot => (eCharacterClass)bot.CharacterClass.ID is eCharacterClass.Bard or eCharacterClass.Minstrel or
                    eCharacterClass.Skald),
            // casters and hybrids (owner 2026-11-06, period: stop nuking at about half power while the tank holds the
            // monster): on, no damage spells under 50% power unless the monster is under 30%; off, nukes freely
            new(KindSavePower, "savepower", "Save power", ["Automatic (on)", "Off"],
                bot => HasAttackSpell(bot) && !IsCaster(bot)),
            // pure casters (owner 2026-11-11, "AI options only for those casters"): the same stored key with a third choice,
            // Strict - no damage spells under 50% power whoever holds the monster, point blank and area damage too
            new(KindSavePowerCaster, "savepower", "Save power", ["Automatic (on)", "Off",
                    "Below 50%: finish PvE targets under 30%", "Below 50%: no damage spells"],
                bot => HasAttackSpell(bot) && IsCaster(bot)),
            new(KindBuffPriority, "buffpriority", "Buff priority",
                ["Automatic (by job)", "Front line first", "Casters and healers first"],
                bot => (bot.MiscSpells ?? []).Any(spell => spell?.IsConcentration == true &&
                    spell.Target == eSpellTarget.REALM && BotBrain.IsMaintainableClassBuff(spell))),
            new(KindPetFocusShield, PetFocusShieldKey, "Pet focus shield", ["Off", "On (when pet is tanking)"],
                BotPetFocusShieldPolicy.KnowsPetFocusShield),
            new(KindRunemasterBladeturn, "runempbt", "Group bladeturn", ["Automatic", "Keep up when grouped", "Off"],
                bot => bot.CharacterClass?.ID == (int)eCharacterClass.Runemaster &&
                    (bot.MiscSpells ?? []).Any(BotBrain.IsGroupBladeturnPulse)),
            // ... and which attack spells at all (a Runemaster: "afk personal bladeturn, no nukes"): area damage only, or
            // none - buffs (its own bladeturn kept up in the fight), crowd control and utility only (CompanionSpellOptions165)
            new(KindAttackSpells, "attackspells", "Attack spells", ["Automatic", "Area spells only (no single-target nukes)",
                    "None - buffs only (no damage or debuffs)"],
                bot => HasAttackSpell(bot) && IsCaster(bot)),
            // melee with a back or side style (owner 2026-11-06; Prima: Berserkers, Blademasters, Mercenaries, Infiltrators
            // attack from behind while the tank holds the monster): Automatic steps behind a monster the main tank holds;
            // Flank behind any monster that is not on it; Stay in front never moves for a style (BotBrain.Flank)
            new(KindPositioning, "position", "Positioning", ["Automatic", "Flank (side/back styles)", "Stay in front"],
                BotBrain.KnowsFlankStyle),
            // owner 2026-11-11: Bards, Minstrels, Skalds and Paladins may cycle their songs or chants as players twisted
            // them; off (Automatic) they hold one, as since 2026-10-30. Companions only; a switch plays without its sound.
            new(KindTwist, "twist", "Twist songs / chants", ["Automatic (hold one)", "Twist (cycle them)"],
                bot => CompanionControls165.OffersTwist((eCharacterClass)bot.CharacterClass.ID, CompanionControls165.SongKinds(bot))),
            // M7 (veteran review): a stance of its own ("tank aggressive, healer passive"); Automatic follows the player's
            // /aggressive, /defensive or /passive
            new(KindStance, "stance", "Stance", ["Automatic (the group's stance)", "Aggressive", "Defensive", "Passive"],
                _ => true),
            // M7 / G7 (period: in PBAoE groups the pack is burned, not mezzed; "the CC class's job on adds is CC"): Main CC
            // mezzes every add first (its long-reuse mez too) and Automatic companions leave the adds to it; Adds only
            // never controls the kill target (the Pacification Healer's opening stun); Off controls nothing
            new(KindCrowdControl, "cc", "Crowd control", ["Automatic", "Main CC (mez every add first)",
                    "Adds only (never the kill target)", "Off"],
                bot => bot.CanCastCrowdControlSpells || bot.Spells?.Any(spell => spell?.SpellType is eSpellType.Mesmerize or
                    eSpellType.Mez or eSpellType.Stun) == true),
            // M7 / G9 (period: pet classes ran their pets on attack / defend; a Spiritmaster or Cabalist with a real tank
            // parked the pet on an add): Guard me keeps it on defence, Take adds sends it only at a monster on its owner or
            // on a groupmate who is no tank, Attack sends it at the fight's target (a Regrowth Druid's or a grouped
            // Spiritmaster's too)
            new(KindPetJob, "petjob", "Pet", ["Automatic", "Guard me (defend only)", "Take adds", "Attack my target"],
                bot => CompanionControls165.PetRowClass((eCharacterClass)bot.CharacterClass.ID)),
            // M7 / G6: Scouts, Hunters and Rangers - Automatic fights with the bow while archery is trained at least as high
            // as the melee weapon, otherwise in melee (a spear Hunter, a no-bow Ranger) with the bow only to pull
            new(KindWeapon, "weapon", "Weapon", ["Automatic (by training)", "Bow", "Melee (the bow only to pull)"],
                bot => CompanionControls165.ArcheryLine((eCharacterClass)bot.CharacterClass.ID) != null),
            // M7 class ability rows: the Hero's Stag (as Berserk: hard fights, on cooldown, off), the archer's Critical Shot
            // opener, and the Pacification Healer's opening stun on the kill target
            new(KindStag, "stag", "Stag", ["Automatic (hard fights)", "On cooldown", "Off"],
                bot => bot.HasAbility(Abilities.Stag)),
            new(KindCriticalShot, "critshot", "Critical Shot opener", ["Automatic (on an idle monster)", "Off"],
                bot => bot.HasAbility(Abilities.Critical_Shot)),
            // the shield tank's Engage (as the M8 AI: a lone monster or archer on it, never with another in melee), and the
            // Blademaster's Triple Wield (as Berserk and Stag: hard fights, on cooldown, off)
            new(KindEngage, "engage", "Engage", ["Automatic", "Off"], bot => bot.HasAbility(Abilities.Engage)),
            new(KindTripleWield, "triplewield", "Triple Wield", ["Automatic (hard fights)", "On cooldown", "Off"],
                bot => bot.HasAbility(Abilities.Triple_Wield)),
            // M7 / G10: a two-hander tank with a shield set swaps to it for one Slam and back (the real swap and swing)
            new(KindSlamSwap, "slamswap", "Shield swap for Slam", ["Automatic (on)", "Off"],
                bot => BotPartyRoles.IsTank(bot) && bot.Inventory?.GetItem(eInventorySlot.TwoHandWeapon) != null &&
                       bot.Inventory.GetItem(eInventorySlot.LeftHandWeapon) is { Object_Type: (int)eObjectType.Shield } &&
                       bot.Styles?.Any(style => style.WeaponTypeRequirement == (int)eObjectType.Shield &&
                                                BotMeleeStylePolicy.IsStunStyle(style)) == true),
            new(KindOpeningStun, "openstun", "Opening stun", ["Automatic (once as the tank engages)", "Off"],
                bot => (eCharacterClass)bot.CharacterClass.ID == eCharacterClass.Healer &&
                       (bot.HarmfulSpells ?? []).Concat(bot.InstantHarmfulSpells ?? []).Any(BotControlSpells.IsStun)),
        ];

        /// <summary>A generic AI option's stored key and choices (choice 0 is the default), or null for an unknown kind.</summary>
        public static (string Key, string[] Choices)? Option(byte kind) =>
            Generics.FirstOrDefault(g => g.Kind == kind) is { } option ? (option.Key, option.Choices) : null;

        /// <summary>A generic AI option's caption (the row's name in the AI Options tab), or null for an unknown kind.</summary>
        public static string Caption(byte kind) => Generics.FirstOrDefault(g => g.Kind == kind)?.Caption;

        private static bool HasAttackSpell(GameBot bot) =>
            HasDamageSpell((bot.HarmfulSpells ?? []).Concat(bot.InstantHarmfulSpells ?? []).Concat(bot.BoltSpells ?? []));

        private static bool IsCaster(GameBot bot) =>
            bot.CharacterClass != null && CompanionSpellOptions165.IsCasterClass((eCharacterClass)bot.CharacterClass.ID);

        /// <summary>Whether a spell list holds a damage spell (the "Save power" row is offered then).</summary>
        public static bool HasDamageSpell(IEnumerable<Spell> spells) => spells.Any(BotCasterPriority.IsDamage);

        /// <summary>Pure (Q7, veteran review 2026-11-11): a Cleric, Druid or Healer whose Role row is set to anything but
        /// Automatic - heal first then attack, attack, or melee and heal - takes part in the player's fights.</summary>
        public static bool HealerRoleFights(eCharacterClass characterClass, int roleChoice) =>
            roleChoice is 1 or 2 or 3 &&
            characterClass is eCharacterClass.Cleric or eCharacterClass.Druid or eCharacterClass.Healer;

        /// <summary>The player's choice for a generic AI option (0 = default; always 0 for gamebots).</summary>
        public static int Choice(GameBot bot, string key) =>
            Get(bot, key) is { } v && int.TryParse(v, out int i) ? i : 0;

        /// <summary>Only hired companions have an owner setting. Unset and Off both mean no focus shield.</summary>
        public static bool PetFocusShieldEnabled(GameBot bot) =>
            HiredCompanions165.HiredRecord(bot) is not { } hired ||
            Read(hired).TryGetValue(PetFocusShieldKey, out string value) && value == "1";

        // ---- debuffs (owner 2026-11-08: "pick which debuffs are used before nuking as long as they stack on a target, or
        // choose just one, or just two, or none ... if debuffs before nuking is on")

        /// <summary>One debuff the companion knows, per kind of effect (debuffs of different kinds stack on a target):
        /// its stored key and its highest-rank spell (for a Necromancer, the servant command).</summary>
        public sealed record DebuffLine(string Key, Spell Spell);

        /// <summary>The stored key of a debuff's kind: its effect, damage type and single/area shape.</summary>
        public static string DebuffLineKey(Spell payload) =>
            $"{(int)payload.SpellType}.{payload.DamageType}.{(payload.Radius > 0 ? "a" : "s")}";

        private static Spell DebuffPayload(Spell spell) =>
            spell?.SpellType == eSpellType.PetSpell && spell.SubSpellID > 0 ? SkillBase.GetSpellByID(spell.SubSpellID) : spell;

        private static bool IsChoosableDebuff(Spell spell, Spell payload) =>
            payload != null && (spell.SpellType == eSpellType.PetSpell
                ? BotBrain.IsServantDebuff(payload) && !BotBrain.IsServantSnare(payload)
                : BotCasterPriority.IsDebuff(spell));

        /// <summary>The debuffs the companion can choose between, one per kind (its best rank), in level order.</summary>
        public static List<DebuffLine> DebuffLines(GameBot bot)
        {
            if (bot == null)
                return [];
            return (bot.HarmfulSpells ?? []).Concat(bot.InstantHarmfulSpells ?? []).Concat(bot.Spells ?? [])
                .Where(spell => spell != null && spell.Level <= bot.Level && spell.IsHarmful)
                .DistinctBy(spell => spell.ID)
                .Select(spell => (Spell: spell, Payload: DebuffPayload(spell)))
                .Where(entry => IsChoosableDebuff(entry.Spell, entry.Payload))
                .GroupBy(entry => DebuffLineKey(entry.Payload))
                .Select(group => new DebuffLine(group.Key, group.OrderByDescending(entry => entry.Spell.Level).First().Spell))
                .OrderBy(line => line.Spell.Level).ThenBy(line => line.Key, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>The player's choice for one debuff: 0 Automatic, 1 Use, 2 Skip (always 0 for gamebots).</summary>
        public static int DebuffLineChoice(GameBot bot, Spell spellOrCommand) =>
            DebuffPayload(spellOrCommand) is { } payload && Get(bot, "dl." + DebuffLineKey(payload)) is { } v &&
            int.TryParse(v, out int i) ? i : 0;

        /// <summary>How many debuffs the player marked "Use" (0: the companion picks its own).</summary>
        public static int PickedDebuffCount(GameBot bot) =>
            HiredCompanions165.HiredRecord(bot) is { } hired ? Read(hired).Count(p => p.Key.StartsWith("dl.", StringComparison.Ordinal) && p.Value == "1") : 0;

        /// <summary>
        /// Pure: may this debuff be cast before nuking? master = "Debuff before nuking" (0 Automatic, 1 Always, 2 Never),
        /// line = this debuff's choice (0 Automatic, 1 Use, 2 Skip). With any debuff marked Use, only those are cast.
        /// </summary>
        public static bool DebuffAllowed(int master, int line, bool anyPicked) =>
            master != 2 && line != 2 && (!anyPicked || line == 1);

        // ---- storage: "key=value;key=value" in OfflineHiredCompanionRecord.Options

        /// <summary>The last parsed options of each hire record, keyed by the exact Options string it was parsed from
        /// (performance review 2026-11-11: every one of the 42+ option reads rebuilt this table). A changed Options
        /// string - Set, a reload - is a different string reference, so the next read parses again.</summary>
        private sealed record ParsedOptions(string Source, IReadOnlyDictionary<string, string> Map);

        private sealed class ParsedSlot
        {
            public ParsedOptions Value;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<OfflineHiredCompanionRecord, ParsedSlot>
            ParsedCache = new();

        private static readonly IReadOnlyDictionary<string, string> NoOptions =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Pure: "key=value;key=value" to a case-insensitive table (later keys win).</summary>
        public static Dictionary<string, string> Parse(string options)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string pair in (options ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0)
                    map[pair[..eq]] = pair[(eq + 1)..];
            }
            return map;
        }

        /// <summary>The record's options, parsed once per Options string (read-only: Set works on its own copy).</summary>
        private static IReadOnlyDictionary<string, string> Read(OfflineHiredCompanionRecord hired)
        {
            if (hired == null)
                return NoOptions;
            string source = hired.Options ?? string.Empty;
            ParsedSlot slot = ParsedCache.GetOrCreateValue(hired);
            // one immutable (source, table) pair swapped as a whole: a reader never pairs a table with another string
            ParsedOptions cached = Volatile.Read(ref slot.Value);
            if (cached != null && ReferenceEquals(cached.Source, source))
                return cached.Map;
            IReadOnlyDictionary<string, string> map = Parse(source);
            Volatile.Write(ref slot.Value, new ParsedOptions(source, map));
            return map;
        }

        /// <summary>Whether the companion wears its cloak's hood up (/hood name; owner 2026-11-10: remembered).</summary>
        public static bool HoodUp(OfflineHiredCompanionRecord hired) =>
            Read(hired).TryGetValue("hood", out string value) && value == "1";

        public static void SetHoodUp(OfflineHiredCompanionRecord hired, bool up)
        {
            if (hired != null)
                Set(hired, "hood", up ? "1" : null);
        }

        private static string Get(GameBot bot, string key) =>
            HiredCompanions165.HiredRecord(bot) is { } hired && Read(hired).TryGetValue(key, out string value) ? value : null;

        private static void Set(OfflineHiredCompanionRecord hired, string key, string value)
        {
            Dictionary<string, string> map = Parse(hired.Options);
            if (value == null) map.Remove(key); else map[key] = value;
            hired.Options = string.Join(';', map.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));
            HiredCompanions165.SaveRecord(hired);
        }

        // ---- what the AI reads

        /// <summary>
        /// False only when the player switched a hired companion's charming off. A hired Hunter's charming starts OFF (owner
        /// 2026-11-06: it keeps its wolf unless the player turns charming on); gamebots are unchanged.
        /// </summary>
        public static bool CharmAllowed(GameBot bot) => CharmAllowed(Get(bot, "charm"), IsHiredHunter(bot));

        /// <summary>Pure: the stored charm choice ("1" on, "0" off, null never set) for a Hunter or any other class.</summary>
        public static bool CharmAllowed(string stored, bool hiredHunter) => hiredHunter ? stored == "1" : stored != "0";

        private static bool IsHiredHunter(GameBot bot) =>
            bot?.CharacterClass?.ID == (int)eCharacterClass.Hunter && HiredCompanions165.IsHired(bot);

        /// <summary>The spell line of the pet the player chose (Cabalist, Enchanter), or null for the bot's own choice.</summary>
        public static string PetLine(GameBot bot) => HasPetChoice(bot) ? Get(bot, "pet") : null;

        /// <summary>
        /// The classes whose pets are a real choice (owner 2026-11-05): the Cabalist's simulacrums and the Enchanter's
        /// Underhill pets each have their own perks, the Theurgist picks an element. A Bonedancer's commander, a
        /// Spiritmaster's or a Necromancer's pet is always the best rank - no choice (a choice stored by the build that
        /// offered one is ignored).
        /// </summary>
        public static bool HasPetChoice(GameBot bot) =>
            bot?.CharacterClass != null &&
            (eCharacterClass)bot.CharacterClass.ID is eCharacterClass.Cabalist or eCharacterClass.Enchanter or eCharacterClass.Theurgist;

        /// <summary>The song or chant type chosen for a situation (1 fight, 2 standing around, 3 travelling), or null.</summary>
        public static eSpellType? Song(GameBot bot, int slot) =>
            Get(bot, $"song{slot}") is { } v && int.TryParse(v, out int type) ? (eSpellType)type : null;

        /// <summary>The Bonedancer's sub-pet plan: per slot null (bot's choice), -1 (none) or a sub-pet type.</summary>
        public static int?[] SubPets(GameBot bot)
        {
            var plan = new int?[3];
            for (int i = 0; i < 3; i++)
                plan[i] = Get(bot, $"sub{i + 1}") is { } v && int.TryParse(v, out int type) ? type : null;
            return plan;
        }

        // ---- what the window offers

        /// <summary>Ids: the spell behind each name (0 none), for the client's spell tooltips (owner 2026-11-08).</summary>
        private sealed record Row(byte Kind, byte Slot, int Chosen, List<string> Choices, int Used, int Cap, List<int> Ids = null);

        // a Hunter too, once it has learned a charm (owner 2026-11-06: charming on = no wolf, off = its wolf)
        private static bool CanCharm(GameBot bot) => bot.CharacterClass != null &&
            ((eCharacterClass)bot.CharacterClass.ID is eCharacterClass.Sorcerer or eCharacterClass.Minstrel ||
             Charm165.LearnedCharm(bot) != null);

        /// <summary>
        /// The pets the player can choose (AI Options pet row; stored as the choice key). Cabalist and Enchanter: every
        /// summon the companion has learned - each is its own pet with its own creature and perks (owner 2026-11-05: Amber,
        /// Ruby, Sapphire... simulacrums; Underhill Friend ... Zealot), keyed "spell:&lt;id&gt;". Theurgist: one choice
        /// per element line (earth, ice, air), keyed by the line.
        /// </summary>
        private static List<(string Line, string Name, int Id)> PetLines(GameBot bot)
        {
            if ((eCharacterClass)bot.CharacterClass.ID == eCharacterClass.Theurgist)
                return AutonomousPetSupport.TheurgistSummons(bot)
                    .Where(entry => entry.Line != null)
                    .GroupBy(entry => entry.Line.KeyName)
                    .Select(group => group.OrderByDescending(entry => entry.Spell.Level).First())
                    .Select(best => (Line: best.Line.KeyName, Name: best.Spell.Name, Id: best.Spell.ID))
                    .OrderBy(entry => entry.Line, StringComparer.Ordinal)
                    .ToList();
            return AutonomousPetSupport.MainPetSummons(bot)
                .GroupBy(entry => entry.Spell.ID)
                .Select(group => group.First())
                .OrderBy(entry => entry.Spell.Level).ThenBy(entry => entry.Spell.ID)
                .Select(entry => (Line: $"spell:{entry.Spell.ID}", Name: entry.Spell.Name, Id: entry.Spell.ID))
                .ToList();
        }

        private static readonly BdSubPet.SubPetType[] SubPetTypes =
            [BdSubPet.SubPetType.Melee, BdSubPet.SubPetType.Archer, BdSubPet.SubPetType.Caster,
             BdSubPet.SubPetType.Debuffer, BdSubPet.SubPetType.Healer, BdSubPet.SubPetType.Buffer];

        /// <summary>The sub-pet kinds the Bonedancer can summon now: the name of its best rank (the minion spell, e.g.
        /// Bone Guard) and the level that rank comes at.</summary>
        private static List<(BdSubPet.SubPetType Type, int Level, string Name, int Id)> SubPetKinds(GameBot bot) =>
            SubPetTypes.Select(type => AutonomousPetSupport.MinionSummons(bot)
                    .Where(entry => (BdSubPet.SubPetType)entry.Spell.DamageType == type)
                    .Select(entry => (Type: type, Level: AutonomousPetSupport.BonedancerSubPetLevel(bot.Level, entry.Spell),
                        Name: entry.Spell.Name, Id: entry.Spell.ID))
                    .OrderByDescending(entry => entry.Level).ThenBy(entry => entry.Name)
                    .FirstOrDefault())
                .Where(entry => entry.Level > 0)
                .ToList();

        /// <summary>The songs or chants the companion holds, one per kind (its best rank), in a fixed order. A Warden's
        /// are its three chants (speed, bladeturn, damage add; M7).</summary>
        private static List<(eSpellType Type, string Name, int Id)> Songs(GameBot bot) =>
            (bot.MiscSpells ?? []).Concat(bot.InstantMiscSpells ?? [])
                .Where(spell => spell != null && spell.Level <= bot.Level && spell.IsPulsing && !spell.IsHarmful &&
                                BotBrain.IsMaintainableClassBuff(spell) &&
                                (bot.CharacterClass.ID != (int)eCharacterClass.Warden || BotSongTwistPolicy.IsManagedSong(bot, spell)))
                .GroupBy(spell => spell.SpellType)
                .Select(group => group.OrderByDescending(spell => spell.Level).First())
                .Select(best => (Type: best.SpellType, Name: best.Name, Id: best.ID))
                .OrderBy(entry => (int)entry.Type)
                .ToList();

        private static bool Performs(GameBot bot) => bot.CharacterClass != null &&
            (eCharacterClass)bot.CharacterClass.ID is eCharacterClass.Bard or eCharacterClass.Minstrel or eCharacterClass.Skald;

        private static int SubPetCapacity(GameBot bot) =>
            bot.ControlledBrain?.Body is CommanderPet commander ? Math.Min(3, commander.SubPetCapacity)
                : bot.Level >= 45 ? 3 : bot.Level >= 30 ? 2 : bot.Level >= 15 ? 1 : 0;

        private static int PlanLevels(int?[] plan, List<(BdSubPet.SubPetType Type, int Level, string Name, int Id)> kinds) =>
            plan.Sum(slot => slot is int type && type >= 0 ? kinds.FirstOrDefault(k => (int)k.Type == type).Level : 0);

        private static List<Row> Rows(GameBot bot)
        {
            var rows = new List<Row>();
            if (bot?.CharacterClass == null)
                return rows;
            var cls = (eCharacterClass)bot.CharacterClass.ID;
            if (CanCharm(bot))
                rows.Add(new Row(KindCharm, 0, CharmAllowed(bot) ? 1 : 0, ["Off", "On"], 0, 0));
            if (HasPetChoice(bot))
            {
                var pets = PetLines(bot);
                if (pets.Count > 1)
                {
                    string chosen = PetLine(bot);
                    rows.Add(new Row(KindPet, 0, pets.FindIndex(p => p.Line == chosen) + 1,
                        ["Automatic", .. pets.Select(p => p.Name)], 0, 0, [0, .. pets.Select(p => p.Id)]));
                }
            }
            if (cls == eCharacterClass.Bonedancer)
            {
                var kinds = SubPetKinds(bot);
                int capacity = SubPetCapacity(bot);
                if (kinds.Count > 0 && capacity > 0)
                {
                    int?[] plan = SubPets(bot);
                    int used = PlanLevels(plan.Take(capacity).ToArray(), kinds);
                    List<string> names = ["Automatic", "None", .. kinds.Select(k => $"{k.Name} ({k.Level})")];
                    for (int slot = 0; slot < capacity; slot++)
                    {
                        int chosen = plan[slot] switch
                        {
                            null => 0,
                            -1 => 1,
                            int type => kinds.FindIndex(k => (int)k.Type == type) is var i && i >= 0 ? i + 2 : 0,
                        };
                        rows.Add(new Row(KindSubPet, (byte)(slot + 1), chosen, names, used, 75, [0, 0, .. kinds.Select(k => k.Id)]));
                    }
                }
            }
            // M7: the Warden's chant rows - in a fight, standing around, travelling - as a performer's
            if (Performs(bot) || cls is eCharacterClass.Paladin or eCharacterClass.Warden)
            {
                var songs = Songs(bot);
                if (songs.Count > 0)
                {
                    List<string> names = ["Automatic", .. songs.Select(s => s.Name)];
                    for (int slot = 1; slot <= (cls == eCharacterClass.Paladin ? 2 : 3); slot++)
                    {
                        int index = Song(bot, slot) is { } type ? songs.FindIndex(s => s.Type == type) + 1 : 0;
                        rows.Add(new Row(KindSong, (byte)slot, Math.Max(0, index), names, 0, 0, [0, .. songs.Select(s => s.Id)]));
                    }
                }
            }
            foreach (Generic option in Generics)
            {
                if (!option.Applies(bot))
                    continue;
                rows.Add(new Row(option.Kind, 0, Math.Clamp(Choice(bot, option.Key), 0, option.Choices.Length - 1),
                    [option.Caption, .. option.Choices], 0, 0));
                // under "Debuff before nuking": one row per debuff it knows (its best rank), slot = its place in the list;
                // names[0] is the debuff's name (the caption), Ids[0] its spell for the tooltip
                if (option.Kind == KindDebuff)
                {
                    List<DebuffLine> lines = DebuffLines(bot);
                    for (int i = 0; i < lines.Count && i < 250; i++)
                        rows.Add(new Row(KindDebuffLine, (byte)(i + 1), Math.Clamp(DebuffLineChoice(bot, lines[i].Spell), 0, 2),
                            [lines[i].Spell.Name, "Automatic", "Use", "Skip"], 0, 0, [lines[i].Spell.ID, 0, 0, 0]));
                }
            }
            return rows;
        }

        public static void Send(GamePlayer player, int row, GameBot bot)
        {
            foreach (Row option in Rows(bot))
            {
                var body = new List<byte> { (byte)row, option.Kind, option.Slot, (byte)Math.Clamp(option.Chosen, 0, 255),
                    (byte)Math.Min(option.Choices.Count, 255),
                    (byte)option.Used, (byte)(option.Used >> 8), (byte)option.Cap, (byte)(option.Cap >> 8) };
                foreach (string name in option.Choices.Take(255))
                {
                    body.AddRange(Encoding.ASCII.GetBytes(name.Length > 60 ? name[..60] : name));
                    body.Add(0);
                }
                // then, for rows with spells, one u32 spell id per name (0 = none) for the tooltips (owner 2026-11-08);
                // older clients stop reading after the names
                if (option.Ids != null)
                    for (int i = 0; i < Math.Min(option.Choices.Count, 255); i++)
                    {
                        int id = i < option.Ids.Count ? option.Ids[i] : 0;
                        body.AddRange([(byte)id, (byte)(id >> 8), (byte)(id >> 16), (byte)(id >> 24)]);
                    }
                Hire165.Send(player, 14, body.ToArray());
            }
            Hire165.Send(player, 14, [(byte)row, 0xFF]);
        }

        // ---- the verbs

        /// <summary>Applies a choice; null when done, otherwise the problem to show.</summary>
        public static string Handle(GameBot bot, OfflineHiredCompanionRecord hired, string verb, string[] args)
        {
            if (bot == null || hired == null)
                return "Summon them first: they are at the inn.";
            string Arg(int i) => args.Length > i ? args[i] : string.Empty;
            int Int(int i) => int.TryParse(Arg(i), out int v) ? v : -1;
            switch (verb)
            {
                case "charm":
                {
                    if (!CanCharm(bot)) return $"{bot.Name} does not charm.";
                    bool on = Arg(3).Equals("on", StringComparison.OrdinalIgnoreCase) || Arg(3).Equals("yes", StringComparison.OrdinalIgnoreCase);
                    bool hunter = IsHiredHunter(bot);
                    // a Hunter's default is off, so its "on" is stored; every other class's default is on
                    Set(hired, "charm", on ? (hunter ? "1" : null) : (hunter ? null : "0"));
                    // switched off with a charmed pet: it is let go now (a Hunter then calls its wolf again)
                    if (!on && bot.ControlledBrain?.Body is GameNPC charmed && charmed is not GameSummonedPet)
                        bot.CommandNpcRelease();
                    // a Hunter switched on: its wolf goes, so it can charm
                    if (on && hunter && bot.ControlledBrain?.Body is GameSummonedPet)
                        bot.CommandNpcRelease();
                    return null;
                }
                case "pet":
                {
                    var pets = PetLines(bot);
                    int i = Int(3);
                    if (i < 0 || i > pets.Count) return "Choose a pet.";
                    string line = i == 0 ? null : pets[i - 1].Line;
                    if (line == PetLine(bot)) return null;
                    Set(hired, "pet", line);
                    if (line != null && bot.ControlledBrain != null)
                        bot.CommandNpcRelease();   // the chosen pet is summoned in its place
                    return null;
                }
                case "subpet":
                {
                    int slot = Int(3), i = Int(4);
                    var kinds = SubPetKinds(bot);
                    if (slot is < 1 or > 3 || i < 0 || i > kinds.Count + 1) return "Choose a sub-pet.";
                    int?[] plan = SubPets(bot);
                    plan[slot - 1] = i == 0 ? null : i == 1 ? -1 : (int)kinds[i - 2].Type;
                    if (PlanLevels(plan, kinds) > 75) return "Those sub-pets together are above the commander's level 75.";
                    Set(hired, $"sub{slot}", plan[slot - 1]?.ToString());
                    // the commander's minions are let go and summoned again to the plan
                    if (bot.ControlledBrain?.Body is CommanderPet commander)
                        foreach (IControlledBrain minion in commander.ControlledNpcList?.Where(m => m?.Body != null).ToArray() ?? [])
                            minion.Body.Die(null);
                    return null;
                }
                case "option" when Int(3) == KindDebuffLine:
                {
                    int slot = Int(4), i = Int(5);
                    List<DebuffLine> lines = DebuffLines(bot);
                    if (slot < 1 || slot > lines.Count || i is < 0 or > 2) return "Choose a debuff.";
                    Set(hired, "dl." + lines[slot - 1].Key, i == 0 ? null : i.ToString());
                    return null;
                }
                case "option":
                {
                    int kind = Int(3), i = Int(5);
                    Generic option = Generics.FirstOrDefault(g => g.Kind == kind);
                    if (option == null || !option.Applies(bot) || i < 0 || i >= option.Choices.Length)
                        return "Choose an option.";
                    bool wasPassive = option.Kind == KindStance && CompanionEngagementMode.PassiveLeader(bot) != null;
                    Set(hired, option.Key, i == 0 ? null : i.ToString());
                    // a companion leaving its own passive stance: its pets defend again at once
                    if (wasPassive && CompanionEngagementMode.PassiveLeader(bot) == null)
                        CompanionEngagementMode.RestorePets(bot);
                    // the Hunter's pet switched off: the wolf that is out goes now
                    if (option.Kind == KindHunterPet && i == 1 && bot.ControlledBrain != null)
                        bot.CommandNpcRelease();
                    return null;
                }
                case "song":
                {
                    int slot = Int(3), i = Int(4);
                    var songs = Songs(bot);
                    if (slot is < 1 or > 3 || i < 0 || i > songs.Count) return "Choose a song.";
                    Set(hired, $"song{slot}", i == 0 ? null : ((int)songs[i - 1].Type).ToString());
                    return null;
                }
            }
            return null;
        }
    }
}
