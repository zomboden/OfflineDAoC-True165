using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using DOL.GS.RealmAbilities;

namespace DOL.GS;

/// <summary>
/// Gamebots spend their realm ability points the way a player of their class would: the same
/// points (one per realm level, as GetPlayerRealmPointsTotal), the same Atlas costs, maximum levels
/// and prerequisites, following a fixed build for their archetype (melee, caster, healer, archer),
/// and only at their class trainer like their spec training. The build is deterministic and only
/// grows with points, so only the points spent are saved; the abilities are rebuilt from them on load. Passive abilities apply
/// through the normal ability bonuses; the few actives bots can use (Purge, Ignore Pain, Second
/// Wind, First Aid) are fired from the brain by <see cref="UseActives"/>.
/// </summary>
public static class AutonomousBotRealmAbilities
{
    private static readonly Logging.Logger Log = Logging.LoggerManager.Create(typeof(AutonomousBotRealmAbilities));

    public enum Archetype { Melee, Caster, Healer, Archer, Tank, TwoHander }

    public readonly record struct Step(string Key, int Level);

    public const string AugStr = "AtlasOF_AugStr", AugCon = "AtlasOF_AugCon", AugDex = "AtlasOF_AugDex",
        AugQui = "AtlasOF_AugQui", AugAcuity = "AtlasOF_AugAcuity", FirstAid = "AtlasOF_FirstAid",
        IgnorePain = "AtlasOF_IgnorePain", IgnorePainTank = "AtlasOF_IgnorePainTank", SecondWind = "AtlasOF_SecondWind",
        Purge = "AtlasOF_Purge", PurgeReduced = "AtlasOF_PurgeReduced";

    // True 1.65 Mode: the class and caster actives of the Atlas OF list that bots also use.
    public const string MasteryOfConcentration = "AtlasOF_MasteryOfConcentration", MysticCrystalLore = "AtlasOF_MCL",
        BunkerOfFaith = "AtlasOF_BunkerOfFaith", PerfectRecovery = "AtlasOF_PerfectRecovery", SpeedOfSound = "AtlasOF_SpeedOfSound",
        AmelioratingMelodies = "AtlasOF_AmelioratingMelodies", Vanish = "AtlasOF_Vanish";

    // M8 (veteran review 2026-11-11): the class actives whose handlers also work for a gamebot.
    public const string SoldiersBarricade = "AtlasOF_SoldiersBarricade", FaithHealing = "AtlasOF_FaithHealing",
        RagingPower = "AtlasOF_RagingPower", VolcanicPillar = "AtlasOF_VolcanicPillar", BatteryOfLife = "AtlasOF_BatteryOfLife";

    // Owner 2026-11-11: the Cabalist's Juggernaut (Prima: "Summons powerful pet for 4 minutes", 14 points, 30 minutes),
    // for a bot a timed second pet beside its simulacrum (BotJuggernaut165).
    public const string Juggernaut = "AtlasOF_Juggernaut";

    /// <summary>Prerequisites, as the Atlas handlers' CheckRequirement (written for GamePlayer only).</summary>
    public static (string Key, int Level)? Requirement(string key, int classId) => key switch
    {
        "AtlasOF_MasteryOfPain" or "AtlasOF_MasteryOfParrying" or "AtlasOF_MasteryOfBlocking" or
            "AtlasOF_DualistsReflexes" or "AtlasOF_FalconsEye" => (AugDex, 2),
        "AtlasOF_MasteryOfArchery" or "AtlasOF_HailOfBlows" or "AtlasOF_Bladedance" or "AtlasOF_WhirlingDervish" => (AugDex, 3),
        "AtlasOF_MasteryOfHealing" or "AtlasOF_MasteryOfMagery" or "AtlasOF_MasteryOfTheArcane" or
            "AtlasOF_WildPower" or "AtlasOF_WildHealing" or "AtlasOF_WildArcana" or "AtlasOF_WildMinion" => (AugAcuity, 2),
        "AtlasOF_MasteryOfTheArt" or "AtlasOF_MasteryOfConcentration" => (AugAcuity, 3),
        "AtlasOF_MasteryOfArms" => classId == (int)eCharacterClass.Friar ? (AugDex, 3) : (AugStr, 3),
        "AtlasOF_Dodger" or "AtlasOF_MasteryOfStealth" => (AugQui, 2),
        SecondWind or "AtlasOF_ArmorOfFaith" or "AtlasOF_AvoidPain" or "AtlasOF_BattleYell" => (AugCon, 3),
        IgnorePain or IgnorePainTank => (FirstAid, 2),
        RagingPower => (MysticCrystalLore, 2),   // AtlasOF_RagingPower.CheckRequirement
        _ => null,
    };

    /// <summary>The actives a bot knows how to use; every other active is never bought.</summary>
    public static readonly string[] UsableActives = { Purge, PurgeReduced, IgnorePainTank, IgnorePain, SecondWind, FirstAid };

    /// <summary>True 1.65 Mode adds these (escape and rescue first, then group, then self).</summary>
    public static readonly string[] ClassicActives =
        { Vanish, PerfectRecovery, BunkerOfFaith, BatteryOfLife, SoldiersBarricade, FaithHealing, SpeedOfSound, AmelioratingMelodies,
          VolcanicPillar, Juggernaut, MasteryOfConcentration, RagingPower, MysticCrystalLore };

    public static string[] UsableActivesFor(bool classic) => classic ? ClassicActives.Concat(UsableActives).ToArray() : UsableActives;

    // Passives with little use to a bot; bought only after everything else.
    private static readonly HashSet<string> LowValue = new(StringComparer.OrdinalIgnoreCase)
        { "AtlasOF_Lifter", "AtlasOF_MasteryOfWater", "AtlasOF_ArrowSalvaging", "AtlasOF_LongWind", "AtlasOF_MasteryOfStealth" };

    private static Step[] Build(params (string Key, int Level)[] steps) =>
        steps.Select(s => new Step(s.Key.StartsWith("AtlasOF_") ? s.Key : "AtlasOF_" + s.Key, s.Level)).ToArray();

    private static readonly Step[] MeleeBuild = Build(("AugCon", 1), ("AugDex", 2), ("MasteryOfPain", 1), ("AugStr", 1),
        ("MasteryOfBlocking", 1), ("MasteryOfParrying", 1), ("Toughness", 1), ("AugCon", 2), (PurgeReduced, 1), (Purge, 1),
        ("AugStr", 3), ("MasteryOfArms", 1), ("AugCon", 3), (SecondWind, 1), ("MasteryOfPain", 2), ("Determination", 1),
        ("DeterminationHybrid", 1), ("AugDex", 3), (FirstAid, 2), (IgnorePainTank, 1), (IgnorePain, 1),
        ("Toughness", 2), ("MasteryOfBlocking", 2), ("MasteryOfParrying", 2), ("MasteryOfArms", 2), ("MasteryOfPain", 3),
        ("AvoidanceOfMagic", 1), ("AugQui", 2), ("Dodger", 1), ("DualistsReflexes", 1));

    private static readonly Step[] CasterBuild = Build(("AugAcuity", 2), ("MasteryOfMagery", 1), ("AugCon", 1),
        ("WildPower", 1), ("Serenity", 1), ("AugDex", 1), ("MasteryOfTheArcane", 1), (PurgeReduced, 1), (Purge, 1),
        ("AugAcuity", 3), ("MasteryOfMagery", 2), ("Toughness", 1), ("EtherealBond", 1), ("WildMinion", 1),
        ("AvoidanceOfMagic", 1), (FirstAid, 1), ("Determination", 1), ("AugCon", 2), ("MasteryOfMagery", 3),
        ("WildPower", 2), ("AugAcuity", 4), ("MasteryOfTheArcane", 2), ("WildArcana", 1));

    private static readonly Step[] HealerBuild = Build(("AugAcuity", 2), ("MasteryOfHealing", 1), ("WildHealing", 1),
        ("AugCon", 1), ("Serenity", 1), (PurgeReduced, 1), (Purge, 1), ("AugDex", 1), ("AugAcuity", 3),
        ("MasteryOfHealing", 2), ("Toughness", 1), ("EtherealBond", 1), ("WildHealing", 2), ("AvoidanceOfMagic", 1),
        ("AugCon", 2), (FirstAid, 1), ("Determination", 1), ("DeterminationHybrid", 1), ("MasteryOfHealing", 3),
        ("AugAcuity", 4), ("WildPower", 1), ("MasteryOfMagery", 1));

    private static readonly Step[] ArcherBuild = Build(("AugDex", 3), ("MasteryOfArchery", 1), ("FalconsEye", 1),
        ("AugQui", 2), ("Dodger", 1), ("AugCon", 1), (PurgeReduced, 1), (Purge, 1), ("AugStr", 1),
        ("MasteryOfArchery", 2), ("Toughness", 1), ("FalconsEye", 2), ("AugCon", 3), (SecondWind, 1),
        ("MasteryOfPain", 1), ("MasteryOfArchery", 3), ("AvoidanceOfMagic", 1), ("Determination", 1));

    // True 1.65 Mode: tanks take Purge first and Determination right after (period tank builds); every build takes its
    // class active (Vanish, Speed of Sound, Bunker of Faith, Perfect Recovery, Ameliorating Melodies) after Purge and
    // Mastery of Concentration once its Acuity is met. Steps a class lacks are skipped.
    private static readonly Step[] ClassActiveSteps = Build((Vanish, 1), (SpeedOfSound, 1), (BunkerOfFaith, 1),
        (PerfectRecovery, 1), (AmelioratingMelodies, 1), (SoldiersBarricade, 1), (FaithHealing, 1), (VolcanicPillar, 1),
        (BatteryOfLife, 1),
        (MysticCrystalLore, 1), (MysticCrystalLore, 2), (RagingPower, 1), (Juggernaut, 1));

    private static Step[] Insert(Step[] build, Step after, Step[] steps)
    {
        int index = Array.IndexOf(build, after);
        return index < 0 ? build.Concat(steps).ToArray() : build.Take(index + 1).Concat(steps).Concat(build.Skip(index + 1)).ToArray();
    }

    private static readonly Step PurgeStep = new(Purge, 1), AcuityThree = new(AugAcuity, 3);

    // M8: a shield tank's period build is Determination and Mastery of Blocking first; its class active (Soldier's
    // Barricade, Faith Healing) once that core is in.
    private static readonly Step[] ClassicTankBuild = Build((PurgeReduced, 1), (Purge, 1), ("Determination", 1),
        ("DeterminationHybrid", 1), ("AugCon", 1), ("AugDex", 2), ("MasteryOfBlocking", 1), ("MasteryOfPain", 1), ("AugStr", 1),
        ("MasteryOfParrying", 1), ("Toughness", 1), ("AugCon", 2), ("Determination", 2), ("DeterminationHybrid", 2))
        .Concat(ClassActiveSteps).Concat(MeleeBuild).ToArray();

    // M8: a two-handed tank class (Armsman, Paladin, Warrior, Thane, Champion, Hero on a two-hander) is damage or off-tank:
    // Mastery of Pain and Mastery of Arms first, no Mastery of Blocking.
    private static readonly Step[] ClassicTwoHanderBuild = Build((PurgeReduced, 1), (Purge, 1), ("AugStr", 1), ("AugDex", 2),
        ("MasteryOfPain", 1), ("AugStr", 3), ("MasteryOfArms", 1), ("Determination", 1), ("DeterminationHybrid", 1),
        ("AugCon", 1), ("Toughness", 1), ("MasteryOfPain", 2), ("MasteryOfArms", 2), ("AugCon", 2))
        .Concat(ClassActiveSteps).Concat(MeleeBuild.Where(step => step.Key != "AtlasOF_MasteryOfBlocking")).ToArray();

    private static readonly Step[] ClassicMeleeBuild = Insert(MeleeBuild, PurgeStep, ClassActiveSteps);
    private static readonly Step[] ClassicCasterBuild =
        Insert(Insert(CasterBuild, PurgeStep, ClassActiveSteps), AcuityThree, Build((MasteryOfConcentration, 1)));
    private static readonly Step[] ClassicHealerBuild =
        Insert(Insert(HealerBuild, PurgeStep, ClassActiveSteps), AcuityThree, Build((MasteryOfConcentration, 1)));
    private static readonly Step[] ClassicArcherBuild = Insert(ArcherBuild, PurgeStep, ClassActiveSteps);

    public static bool IsTankClass(int classId) => BotPartyRoles.For((eCharacterClass)classId) == BotPartyRole.Tank;

    /// <summary>The build a bot follows: the 1.65 builds (tanks apart) in True 1.65 Mode, the original ones otherwise.</summary>
    public static Archetype BuildArchetype(Archetype archetype, int classId, bool classic) =>
        classic && archetype == Archetype.Melee && IsTankClass(classId) ? Archetype.Tank : archetype;

    /// <summary>
    /// M8 (veteran review 2026-11-11): the build follows the bot's spec, not only its class. A Rejuvenation Friar takes
    /// the healer build (Aug Acuity, Mastery of Healing, Serenity), a Smite Cleric the caster build (Mastery of Magery,
    /// Mastery of Concentration), a two-handed tank class the two-hander build. True 1.65 Mode only.
    /// </summary>
    public static Archetype SpecArchetype(Archetype classArchetype, int classId, eSpecType spec, bool twoHanded, bool classic)
    {
        if (!classic)
            return classArchetype;
        if (classId == (int)eCharacterClass.Friar && spec == eSpecType.RejuvFriar)
            return Archetype.Healer;
        if (classId == (int)eCharacterClass.Cleric && spec == eSpecType.SmiteCleric)
            return Archetype.Caster;
        if (classArchetype == Archetype.Tank && twoHanded)
            return Archetype.TwoHander;
        return classArchetype;
    }

    private static Step[] BuildFor(Archetype archetype, bool classic) => archetype switch
    {
        Archetype.Tank => ClassicTankBuild,
        Archetype.TwoHander => ClassicTwoHanderBuild,
        Archetype.Caster => classic ? ClassicCasterBuild : CasterBuild,
        Archetype.Healer => classic ? ClassicHealerBuild : HealerBuild,
        Archetype.Archer => classic ? ClassicArcherBuild : ArcherBuild,
        _ => classic ? ClassicMeleeBuild : MeleeBuild,
    };

    public static Archetype ArchetypeOf(ICollection<string> available)
    {
        if (available.Contains("AtlasOF_MasteryOfArchery")) return Archetype.Archer;
        bool melee = available.Contains("AtlasOF_MasteryOfPain") || available.Contains("AtlasOF_MasteryOfParrying");
        if (!melee && (available.Contains("AtlasOF_MasteryOfHealing") || available.Contains("AtlasOF_WildHealing"))) return Archetype.Healer;
        if (!melee && available.Contains(AugAcuity)) return Archetype.Caster;
        return Archetype.Melee;
    }

    /// <summary>
    /// The abilities and levels bought with these points. available: the class's buyable keys with
    /// their maximum level; cost(key, currentLevel): points for the next level. The archetype build
    /// comes first (steps the class lacks or cannot meet yet are skipped), then the remaining
    /// abilities are raised cheapest-first. Buying stops at the first step it cannot afford, so more
    /// points only ever extend the result.
    /// </summary>
    public static Dictionary<string, int> Plan(IReadOnlyDictionary<string, int> available, Archetype archetype, int classId,
        int points, Func<string, int, int> cost, bool classic = false)
    {
        var owned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int Owned(string key) => owned.TryGetValue(key, out int level) ? level : 0;
        bool Meets(string key) => Requirement(key, classId) is not { } need || Owned(need.Key) >= need.Level;
        // One purge is enough: the reduced-cost variant replaces the full one where a class has it.
        bool Redundant(string key) => key == Purge && owned.ContainsKey(PurgeReduced) || key == PurgeReduced && owned.ContainsKey(Purge) ||
            key == IgnorePain && owned.ContainsKey(IgnorePainTank) || key == IgnorePainTank && owned.ContainsKey(IgnorePain);

        // Returns false when the next level is unaffordable (stop buying).
        bool Raise(string key, int target)
        {
            while (Owned(key) < target)
            {
                int price = cost(key, Owned(key));
                if (price <= 0 || price > points) return false;
                points -= price;
                owned[key] = Owned(key) + 1;
            }
            return true;
        }

        Step[] build = BuildFor(archetype, classic);
        foreach (Step step in build)
        {
            if (!available.TryGetValue(step.Key, out int max) || Redundant(step.Key) || !Meets(step.Key)) continue;
            if (!Raise(step.Key, Math.Min(step.Level, max))) return owned;
        }
        // The rest, cheapest next level first (ties by name), low-value passives last.
        while (true)
        {
            string next = null; int nextPrice = int.MaxValue; bool nextLow = true;
            foreach (var (key, max) in available.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (Owned(key) >= max || Redundant(key) || !Meets(key)) continue;
                int price = cost(key, Owned(key));
                if (price <= 0 || price >= 1000) continue;
                bool low = LowValue.Contains(key);
                if (next == null || (nextLow && !low) || (low == nextLow && price < nextPrice))
                    (next, nextPrice, nextLow) = (key, price, low);
            }
            if (next == null || !Raise(next, Owned(next) + 1)) return owned;
        }
    }

    /// <summary>Realm ability points, as the player rule: one per realm level, at least 1 from level 20.</summary>
    public static int Points(int level, int realmLevel) => level > 19 ? Math.Max(1, realmLevel) : realmLevel;

    private sealed class State
    {
        public int TrainedPoints;
        public int CheckedPoints = -1;
        public bool HasPurchase;
        public TimedRealmAbility[] Actives = Array.Empty<TimedRealmAbility>();
        public long NextUse;
    }

    private sealed record Catalog(Dictionary<string, RealmAbility> ByKey, Dictionary<string, int> Available, Archetype Archetype);

    private static readonly ConditionalWeakTable<GameBot, State> States = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, bool), Catalog> Catalogs = new();

    private static Catalog CatalogFor(int classId) => Catalogs.GetOrAdd((classId, Classic165Mode.IsOn), key =>
    {
        string[] usable = UsableActivesFor(key.Item2);
        var byKey = SkillBase.GetClassRealmAbilities(key.Item1)
            .Where(ra => ra is not RR5RealmAbility && (ra is not TimedRealmAbility || usable.Contains(ra.KeyName)))
            .GroupBy(ra => ra.KeyName).ToDictionary(g => g.Key, g => g.First());
        var available = byKey.ToDictionary(pair => pair.Key, pair => pair.Value.MaxLevel);
        return new Catalog(byKey, available, BuildArchetype(ArchetypeOf(available.Keys), key.Item1, key.Item2));
    });

    private static int ClassOf(GameBot bot) => bot.CharacterClass?.ID ?? bot.ClassId;

    private static Archetype ArchetypeOf(GameBot bot) =>
        SpecArchetype(CatalogFor(ClassOf(bot)).Archetype, ClassOf(bot), bot.BotSpec?.SpecType ?? eSpecType.None,
            bot.BotSpec?.Is2H == true, Classic165Mode.IsOn);

    private static Dictionary<string, int> PlanFor(GameBot bot, int points)
    {
        Catalog catalog = CatalogFor(ClassOf(bot));
        return Plan(catalog.Available, ArchetypeOf(bot), ClassOf(bot), points, (key, level) => catalog.ByKey[key].CostForUpgrade(level),
            Classic165Mode.IsOn);
    }

    private static bool Eligible(GameBot bot) => bot is { IsAutonomousWorldBot: true, IsTemporaryGroupHelper: false };

    /// <summary>Realm ability points already spent at a trainer (saved with the bot).</summary>
    public static int TrainedPoints(GameBot bot) => bot != null && States.TryGetValue(bot, out State state) ? state.TrainedPoints : 0;

    /// <summary>Gives a loading bot back the abilities it bought with these points earlier.</summary>
    public static void Restore(GameBot bot, int trainedPoints)
    {
        if (!Eligible(bot) || trainedPoints <= 0) return;
        try
        {
            Grant(bot, PlanFor(bot, Math.Min(trainedPoints, Points(bot.Level, bot.RealmLevel))));
            States.GetOrCreateValue(bot).TrainedPoints = trainedPoints;
        }
        catch (Exception exception)
        {
            Log.Warn($"BOT_REALM_ABILITY_FAILED bot={bot.Name} class={ClassOf(bot)} restoring={trainedPoints}", exception);
        }
    }

    /// <summary>True when a trainer visit would buy something new with the bot's realm points.</summary>
    public static bool HasPurchase(GameBot bot)
    {
        if (!Eligible(bot)) return false;
        int points = Points(bot.Level, bot.RealmLevel);
        State state = States.GetOrCreateValue(bot);
        if (points <= state.TrainedPoints) return false;
        if (state.CheckedPoints != points)
        {
            try
            {
                state.HasPurchase = PlanFor(bot, points).Values.Sum() > PlanFor(bot, state.TrainedPoints).Values.Sum();
            }
            catch (Exception exception)
            {
                state.HasPurchase = false;
                Log.Warn($"BOT_REALM_ABILITY_FAILED bot={bot.Name} class={ClassOf(bot)}", exception);
            }
            state.CheckedPoints = points;
        }
        return state.HasPurchase;
    }

    /// <summary>Unspent realm points that alone justify a trip to the trainer; fewer are spent at the next level-up visit.</summary>
    public const int PointsWorthATrip = 3;

    /// <summary>True when realm points alone are worth a trainer trip (enough unspent points and something to buy).</summary>
    public static bool WorthTrainerTrip(GameBot bot) =>
        HasPurchase(bot) && Points(bot.Level, bot.RealmLevel) - TrainedPoints(bot) >= PointsWorthATrip;

    /// <summary>Spends the bot's realm points at a trainer. Returns what was bought ("" when nothing).</summary>
    public static string Train(GameBot bot)
    {
        if (!HasPurchase(bot)) return string.Empty;
        int points = Points(bot.Level, bot.RealmLevel);
        State state = States.GetOrCreateValue(bot);
        try
        {
            string gained = string.Join(",", Grant(bot, PlanFor(bot, points)));
            state.TrainedPoints = points;
            state.HasPurchase = false;
            Log.Info($"BOT_REALM_ABILITY_TRAINED bot=\"{bot.Name}\" class={ClassOf(bot)} realmLevel={bot.RealmLevel} points={points} " +
                $"archetype={ArchetypeOf(bot)} gained=\"{gained}\"");
            return gained;
        }
        catch (Exception exception)
        {
            state.TrainedPoints = points;
            Log.Warn($"BOT_REALM_ABILITY_FAILED bot={bot.Name} class={ClassOf(bot)}", exception);
            return string.Empty;
        }
    }

    private static List<string> Grant(GameBot bot, Dictionary<string, int> plan)
    {
        var gained = new List<string>();
        foreach (var (key, level) in plan)
        {
            Ability current = bot.GetAbility(key);
            if (current != null && current.Level >= level) continue;
            if (SkillBase.GetAbility(key, level) is not RealmAbility ability) continue;
            bot.AddAbility(ability, false);
            gained.Add($"{key.Replace("AtlasOF_", "")}:{current?.Level ?? 0}->{level}");
        }
        State state = States.GetOrCreateValue(bot);
        state.Actives = UsableActivesFor(Classic165Mode.IsOn).Select(key => bot.GetAbility(key) as TimedRealmAbility).Where(a => a != null).ToArray();
        return gained;
    }

    /// <summary>Re-reads the actives a bot has (a hired companion's player buys them in its realm-ability window; they
    /// never reached the list the brain fires from, so a companion never used a Purge or Ignore Pain it was given;
    /// owner 2026-11-05 audit).</summary>
    public static void RefreshActives(GameBot bot)
    {
        if (bot == null) return;
        States.GetOrCreateValue(bot).Actives = UsableActivesFor(Classic165Mode.IsOn).Select(key => bot.GetAbility(key) as TimedRealmAbility).Where(a => a != null).ToArray();
    }

    /// <summary>The actives a /passive companion still uses (owner decision O2: escapes and rescues while retreating).</summary>
    public static readonly string[] RetreatActives = { Vanish, SpeedOfSound, Purge, PurgeReduced, IgnorePainTank, IgnorePain, SecondWind };

    /// <summary>
    /// Pure (O2): while a /passive companion retreats, Vanish once it is hit and under half health, Speed of Sound once
    /// anyone near in the group is being hit, Purge when it is mezzed or stunned in a fight; Ignore Pain and Second Wind
    /// keep their usual rules. Never a fight active (Bunker, Ameliorating Melodies, Mastery of Concentration...).
    /// </summary>
    public static bool WantsWhileRetreating(string key, bool inCombat, int health, bool controlled, bool groupFighting) => key switch
    {
        Vanish => !controlled && inCombat && health < 50,
        SpeedOfSound => !controlled && groupFighting,
        Purge or PurgeReduced => controlled && inCombat,
        _ => false,
    };

    /// <summary>Uses a bought active when the situation calls for it. Checked at most once a second. Retreating (a
    /// /passive companion), only the escape and rescue actives (<see cref="RetreatActives"/>).</summary>
    public static void UseActives(GameBot bot, bool retreating = false)
    {
        if (bot == null || !bot.IsAlive || !States.TryGetValue(bot, out State state) || state.Actives.Length == 0) return;
        long now = GameLoop.GameLoopTime;
        if (now < state.NextUse) return;
        state.NextUse = now + 1_000;
        Situation? situation = null;
        GameLiving deadMember = null;
        foreach (TimedRealmAbility ability in state.Actives)
        {
            if (bot.GetSkillDisabledDuration(ability) > 0) continue;
            if (retreating)
            {
                if (!RetreatActives.Contains(ability.KeyName)) continue;
                if (ability.KeyName is IgnorePain or IgnorePainTank or SecondWind)
                {
                    if (!Wanted(bot, ability.KeyName)) continue;
                }
                else
                {
                    situation ??= SituationOf(bot, out deadMember);
                    if (!WantsWhileRetreating(ability.KeyName, situation.Value.InCombat, situation.Value.Health,
                            situation.Value.Controlled, situation.Value.GroupFighting)) continue;
                }
            }
            else if (ClassicActives.Contains(ability.KeyName))
            {
                situation ??= SituationOf(bot, out deadMember);
                // the one radius count, only for a bot whose Volcanic Pillar is ready and only while it fights
                if (ability.KeyName == VolcanicPillar && situation.Value.InCombat)
                    situation = situation.Value with { EnemiesAtTarget = EnemiesAtTarget(bot) };
                if (ability.KeyName == MysticCrystalLore && Classic165Mode.IsOn && bot.IsPlayerCompanion)
                {
                    int choice = CompanionOptions165.Choice(bot, CompanionOptions165.MysticCrystalLoreKey);
                    var status = situation.Value;
                    if (choice == 2 || status.InCombat || status.Controlled || status.Casting ||
                        status.Mana >= (choice == 1 ? 25 : 40) || status.Sitting && !bot.IsRecoveryResting)
                        continue;
                    if (status.Sitting)
                        bot.WakeRecoveryRest();
                }
                else if (!Wants(ability.KeyName, situation.Value)) continue;
                if (ability.KeyName == PerfectRecovery) bot.TargetObject = deadMember;
            }
            else if (!Wanted(bot, ability.KeyName)) continue;
            ability.Execute(bot);
            if (ability.KeyName == Vanish && bot.IsStealthed)
            {
                // Vanish is the escape: drop the fight and slip away hidden.
                bot.StopAttack();
                (bot.Brain as DOL.AI.Brain.BotBrain)?.ClearAggroList();
            }
            // A purge that removed nothing only blocks itself for 5 ms; log real uses (long reuse timers).
            if (bot.GetSkillDisabledDuration(ability) > 1_000 && ability.KeyName != FirstAid)
                Log.Info($"BOT_REALM_ABILITY_USED bot={bot.Name} ability={ability.KeyName.Replace("AtlasOF_", "")} " +
                    $"health={bot.HealthPercent} endurance={bot.EndurancePercent} region={bot.CurrentRegionID}");
            return;
        }
    }

    /// <summary>
    /// M6 (veteran review 2026-11-11): a bot leaving a lost fight (a party's RUN, a solo bail-out, a companion healer
    /// surviving to raise) uses its escape active when it owns one and it is ready: Vanish, else Speed of Sound.
    /// </summary>
    public static bool TryUseEscape(GameBot bot)
    {
        if (bot == null || !bot.IsAlive || bot.IsMezzed || bot.IsStunned || !States.TryGetValue(bot, out State state))
            return false;
        foreach (string key in EscapeActives)
            foreach (TimedRealmAbility ability in state.Actives)
            {
                if (ability.KeyName != key || bot.GetSkillDisabledDuration(ability) > 0) continue;
                ability.Execute(bot);
                if (ability.KeyName == Vanish && bot.IsStealthed)
                {
                    bot.StopAttack();
                    (bot.Brain as DOL.AI.Brain.BotBrain)?.ClearAggroList();
                }
                Log.Info($"BOT_REALM_ABILITY_USED bot={bot.Name} ability={ability.KeyName.Replace("AtlasOF_", "")} " +
                    $"health={bot.HealthPercent} reason=escape region={bot.CurrentRegionID}");
                return true;
            }
        return false;
    }

    /// <summary>The escape actives, in the order a bot leaving a fight tries them.</summary>
    public static readonly string[] EscapeActives = { Vanish, SpeedOfSound };

    /// <summary>What the True 1.65 actives look at, read once per check.</summary>
    public readonly record struct Situation(bool InCombat, bool Battleground, int Health, int Mana, bool Controlled,
        bool Sitting, bool Casting, bool Interrupted, int GroupNear, int GroupHurt, bool GroupFighting, bool DeadMemberNear,
        int EnemiesAtTarget = 0);

    // Volcanic Pillar's radius (AtlasOF_VolcanicPillar: 500) and range (1,875).
    private const int PillarRadius = 500, PillarRange = 1875;

    /// <summary>Enemies the bot may attack within the pillar's radius of its target (the target included).</summary>
    private static int EnemiesAtTarget(GameBot bot)
    {
        if (bot.TargetObject is not GameLiving target || !target.IsAlive || !bot.IsWithinRadius(target, PillarRange))
            return 0;
        int count = 0;
        foreach (GameNPC npc in target.GetNPCsInRadius(PillarRadius))
            if (npc.IsAlive && npc.InCombat && GameServer.ServerRules.IsAllowedToAttack(bot, npc, true))
                count++;
        foreach (GamePlayer player in target.GetPlayersInRadius(PillarRadius))
            if (player.IsAlive && GameServer.ServerRules.IsAllowedToAttack(bot, player, true))
                count++;
        return count;
    }

    // Group range of the group actives (Bunker of Faith, Ameliorating Melodies, Perfect Recovery 1,500; Speed of Sound 2,000).
    private const int GroupActiveRange = 1500;
    private const int HurtPercent = 70;

    private static Situation SituationOf(GameBot bot, out GameLiving dead)
    {
        int near = 0, hurt = 0;
        bool fighting = bot.InCombat;
        dead = null;
        if (bot.Group != null)
            foreach (GameLiving member in bot.Group.GetMembersInTheGroup())
            {
                if (member == bot || member.CurrentRegionID != bot.CurrentRegionID || !bot.IsWithinRadius(member, GroupActiveRange)) continue;
                if (!member.IsAlive)
                {
                    if (member.ObjectState == GameObject.eObjectState.Active) dead ??= member;
                    continue;
                }
                near++;
                if (member.HealthPercent < HurtPercent) hurt++;
                fighting |= member.InCombat;
            }
        if (bot.HealthPercent < HurtPercent) hurt++;
        return new(bot.InCombat, AutonomousWorldBotController.IsBattleground(bot), bot.HealthPercent, bot.MaxMana > 0 ? bot.ManaPercent : 100,
            bot.IsMezzed || bot.IsStunned, bot.IsSitting, bot.IsCasting, bot.IsBeingInterruptedByOther, near + 1, hurt, fighting, dead != null);
    }

    /// <summary>
    /// True 1.65 Mode use rules. Vanish: escape when a frontier fight is going badly. Perfect Recovery: raise a dead group
    /// member while the group still fights. Bunker of Faith / Speed of Sound: the group taking burst damage (half the group
    /// in range, at least two, under 70% health; Speed of Sound in the frontier). Ameliorating Melodies: two or more hurt
    /// in a fight. Mastery of Concentration: casting while being hit in a fight. Mystic Crystal Lore: low power out of combat.
    /// </summary>
    public static bool Wants(string key, Situation s)
    {
        if (s.Controlled) return false;
        bool burst = s.GroupFighting && s.GroupHurt >= Math.Max(2, (s.GroupNear + 1) / 2);
        return key switch
        {
            Vanish => s.InCombat && s.Battleground && s.Health < 35,
            PerfectRecovery => s.DeadMemberNear && s.GroupFighting && !s.Casting,
            BunkerOfFaith => burst,
            SpeedOfSound => burst && s.Battleground,
            AmelioratingMelodies => s.GroupFighting && s.GroupHurt >= 2,
            // M8 Albion actives: Soldier's Barricade on group burst (the Bunker rule); Faith Healing on group burst or the
            // Paladin itself near death; Raging Power is the in-fight twin of MCL; Volcanic Pillar on three or more enemies.
            SoldiersBarricade or BatteryOfLife => burst,
            FaithHealing => burst || s.InCombat && s.Health < 25,
            RagingPower => s.InCombat && !s.Sitting && s.Mana < 25,
            VolcanicPillar => s.InCombat && !s.Casting && s.EnemiesAtTarget >= 3,
            // a 30-minute summon: a fight against another realm, the Cabalist under half health, or its group under burst
            Juggernaut => BotJuggernaut165.Wanted(s.InCombat, s.Controlled, s.Battleground, s.Health, burst),
            MasteryOfConcentration => s.InCombat && s.Interrupted && (s.Battleground || s.Health < 60),
            MysticCrystalLore => !s.InCombat && !s.Sitting && s.Mana < 30,
            _ => false,
        };
    }

    /// <summary>A crowd control lasting at least this long more is worth a healer's Purge in a PvE fight.</summary>
    public const long HealerPurgeControlMilliseconds = 10_000;

    /// <summary>
    /// Pure: Purge on a stunned or mesmerized bot. As before in a fight against other realms or under 60% health. And
    /// (dragon-fight L3) a healer in any fight held by a crowd control with 10 s or more left: the dragons stun everything
    /// within 2,000 for about 40 s every two to three minutes, and the raid's healers stood stunned with Purge ready while
    /// its fighters died (healers first, from heal threat). A veteran healer purges that.
    /// </summary>
    public static bool PurgeWanted(bool inCombat, int health, bool battleground, bool healer, long controlRemainingMs) =>
        (inCombat || health < 90) && (battleground || health < 60) ||
        inCombat && healer && controlRemainingMs >= HealerPurgeControlMilliseconds;

    /// <summary>The longer of the bot's stun and mez, in milliseconds left (0 when neither).</summary>
    private static long ControlRemaining(GameBot bot)
    {
        long stun = bot.IsStunned ? EffectListService.GetEffectOnTarget(bot, eEffect.Stun)?.GetRemainingTimeForClient() ?? 0 : 0;
        long mez = bot.IsMezzed ? EffectListService.GetEffectOnTarget(bot, eEffect.Mez)?.GetRemainingTimeForClient() ?? 0 : 0;
        return Math.Max(stun, mez);
    }

    private static bool Wanted(GameBot bot, string key) => key switch
    {
        // Crowd control in a fight against other realms (or when it is going badly), or a healer held by a long stun or
        // mez in any fight (dragon-fight L3).
        Purge or PurgeReduced => (bot.IsMezzed || bot.IsStunned) && PurgeWanted(bot.InCombat, bot.HealthPercent,
            AutonomousWorldBotController.IsBattleground(bot), bot.CanCastHealSpells, ControlRemaining(bot)),
        IgnorePain or IgnorePainTank => bot.InCombat && bot.HealthPercent < 30 && !bot.IsMezzed && !bot.IsStunned,
        SecondWind => bot.InCombat && bot.EndurancePercent < 10 && !bot.IsMezzed && !bot.IsStunned,
        FirstAid => !bot.InCombat && bot.HealthPercent < 50 && !bot.IsSitting,
        _ => false,
    };
}
