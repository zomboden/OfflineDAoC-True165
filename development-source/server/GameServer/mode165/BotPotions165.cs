using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using DOL.Database;

namespace DOL.GS
{
    /// <summary>
    /// Potions for bots (veteran-bots review 2026-11-11, M9 / economy G10 and owner decision O13). No bot code ever drank a
    /// potion, though the companion bag is documented as holding potions for the companion "to use", and merchants sell
    /// the Draughts of Healing, Power and Endurance (level 20, 9 charges, a 3-second drink, one a minute).
    /// - Drinking, the same for hired companions and gamebots: a heal draught at 35% health or less when hit while it is
    ///   neither casting nor swinging (a player can't drink a cast potion in attack mode: GamePlayer.UseSlot), and right
    ///   after a kill while another monster is still on it at 50% health or less; a power draught after a kill with an add
    ///   on it at 20% power or less (casters), an endurance draught at 20% endurance or less (fighters). The item's own
    ///   shared reuse timer is kept, a charge is used (the last one uses the item up), as a player's.
    /// - Buying (O13): a gamebot of level 20 or more with no heal draught buys one at a town errand from a potion merchant
    ///   near its merchant only when its coin is at least five times its reserve after paying; a second one when it is
    ///   twenty times. Hired companions don't shop: their player gives them potions.
    /// Cost: one health comparison per hit taken (already computed). The 40-slot bag look runs only when a threshold is
    /// crossed, the drink's reuse timer is free and the bag was not just found to hold no potion of that kind: a bot with
    /// no draught looks again only after its inventory changes (gamebots) or <see cref="NoPotionRecheckMilliseconds"/>
    /// (combat review 2026-11-11: before, a potionless bot under 35% scanned its bag on every hit). Buying is
    /// errand-time only.
    /// </summary>
    public static class BotPotions165
    {
        private static readonly DOL.Logging.Logger Log = DOL.Logging.LoggerManager.Create(typeof(BotPotions165));

        public const int HealWhenHitPercent = 35;
        public const int HealAfterKillPercent = 50;
        public const int PowerPercent = 20;
        public const int EndurancePercent = 20;
        public const int MinBuyLevel = 20;
        public const int RichReserveMultiple = 5;
        public const int VeryRichReserveMultiple = 20;

        public enum Kind { None, Heal, Power, Endurance }

        /// <summary>A bag found with no potion of a kind is not looked at again for this long (unless it changes).</summary>
        public const long NoPotionRecheckMilliseconds = 30_000;

        private sealed class DrinkState
        {
            // timer group -> tick the next drink of that group is allowed
            public readonly ConcurrentDictionary<int, long> Timers = new();
            // per kind: no bag look before this tick while the inventory revision is unchanged
            public readonly long[] SkipUntil = new long[4];
            public readonly int[] SkipRevision = new int[4];
            public int CompanionBagRevision;
            public int Drinking;
        }

        // bot id -> its drink timers and "no potion" marks
        private static readonly ConcurrentDictionary<long, DrinkState> NextDrink = new();

        /// <summary>Which drink a spell type is ("Heal", "PowerHeal", "EnduranceHeal"; anything else is not drunk).</summary>
        public static Kind KindOf(string spellType) => spellType switch
        {
            "Heal" => Kind.Heal,
            "PowerHeal" => Kind.Power,
            "EnduranceHeal" => Kind.Endurance,
            _ => Kind.None,
        };

        /// <summary>Which drink a spell type is, without a string per potion.</summary>
        public static Kind KindOf(eSpellType spellType) => spellType switch
        {
            eSpellType.Heal => Kind.Heal,
            eSpellType.PowerHeal => Kind.Power,
            eSpellType.EnduranceHeal => Kind.Endurance,
            _ => Kind.None,
        };

        /// <summary>
        /// Pure: the bag is looked at for a drink of this kind. Not while a "no potion" or reuse mark is still running for
        /// the same inventory revision; any inventory change (a gamebot's loot, a buy) clears it at once.
        /// </summary>
        public static bool ShouldLook(long now, long skipUntil, int skipRevision, int revision) =>
            now >= skipUntil || revision != skipRevision;

        /// <summary>A drinkable potion: a backpack magical item with a spell and charges (GamePlayer.UseSlot's potion test).</summary>
        public static bool IsPotion(DbInventoryItem item) =>
            item != null && item.Object_Type == (int)eObjectType.Magical && item.Item_Type == (int)eInventorySlot.FirstBackpack &&
            item.SpellID > 0 && item.Charges > 0;

        /// <summary>When hit: a heal draught at 35% or less while neither casting nor swinging.</summary>
        public static bool WantsHealWhenHit(int healthPercent, bool casting, bool attacking) =>
            healthPercent > 0 && healthPercent <= HealWhenHitPercent && !casting && !attacking;

        /// <summary>After a kill with an add still on it: which drink, if any (heal first).</summary>
        public static Kind WantsAfterKill(int healthPercent, int powerPercent, bool usesPower, int endurancePercent, bool addsLeft)
        {
            if (!addsLeft)
                return Kind.None;
            if (healthPercent > 0 && healthPercent <= HealAfterKillPercent)
                return Kind.Heal;
            if (usesPower && powerPercent <= PowerPercent)
                return Kind.Power;
            if (!usesPower && endurancePercent <= EndurancePercent)
                return Kind.Endurance;
            return Kind.None;
        }

        /// <summary>How many heal draughts a gamebot wants to own at this level and coin (O13): 0, 1 or 2.</summary>
        public static int HealDraughtsWanted(int level, long coin, long reserve, long price)
        {
            if (level < MinBuyLevel || price <= 0)
                return 0;
            if (coin - 2 * price >= VeryRichReserveMultiple * reserve)
                return 2;
            return coin - price >= RichReserveMultiple * reserve ? 1 : 0;
        }

        private static bool Eligible(GameBot bot) =>
            Classic165Mode.IsOn && bot?.Inventory != null && bot.IsAlive && bot.DatabaseID > 0 &&
            (HiredCompanions165.IsHired(bot) || bot.IsAutonomousWorldBot && !bot.IsTemporaryGroupHelper) &&
            CompanionOptions165.Choice(bot, CompanionOptions165.PotionsKey) != 2;

        public static void InventoryChanged(GameBot bot)
        {
            if (NextDrink.TryGetValue(bot.DatabaseID, out DrinkState state))
                Interlocked.Increment(ref state.CompanionBagRevision);
        }

        // A companion can use an emergency draught while the fight is ongoing, even if a groupmate gets the kill.
        // Recovering with consumables between fights is an owner choice; standing regen and MCL get first use.
        public static void TryCompanionDrink(GameBot bot)
        {
            if (bot?.IsPlayerCompanion != true || !Eligible(bot) || bot.IsCasting ||
                bot.IsIncapacitated || bot.IsOnStableMasterRoute)
                return;
            bool fighting = bot.InCombat;
            bool recovery = !fighting && CompanionOptions165.Choice(bot, CompanionOptions165.PotionsKey) == 1;
            if (!fighting && !recovery || bot.IsSitting && (!recovery || !bot.IsRecoveryResting))
                return;
            if (bot.HealthPercent <= (fighting ? HealWhenHitPercent : HealAfterKillPercent) && !bot.IsAttacking &&
                TryDrink(bot, Kind.Heal, recovery))
                return;
            bool usesPower = bot.MaxMana > 0 && bot.CharacterClass?.ManaStat is not null and not eStat.UNDEFINED;
            if (usesPower && bot.ManaPercent <= (fighting ? PowerPercent : 35))
                TryDrink(bot, Kind.Power, recovery);
            else if (!usesPower && bot.EndurancePercent <= EndurancePercent)
                TryDrink(bot, Kind.Endurance, recovery);
        }

        /// <summary>GameBot.TakeDamage: an emergency heal draught.</summary>
        public static void OnDamaged(GameBot bot)
        {
            if (bot == null || bot.HealthPercent > HealWhenHitPercent || !Eligible(bot))
                return;
            if (WantsHealWhenHit(bot.HealthPercent, bot.IsCasting, bot.attackComponent?.AttackState == true))
                TryDrink(bot, Kind.Heal);
        }

        /// <summary>GameBot.EnemyKilled: a draught between a kill and the add still on the bot.</summary>
        public static void AfterKill(GameBot bot)
        {
            if (bot == null || !Eligible(bot) || bot.IsCasting)
                return;
            bool addsLeft = (bot.attackComponent?.AttackerTracker?.Count ?? 0) > 0;
            if (!addsLeft)
                return;
            bool usesPower = bot.MaxMana > 0 && bot.CharacterClass?.ManaStat is not null and not eStat.UNDEFINED;
            Kind kind = WantsAfterKill(bot.HealthPercent, bot.ManaPercent, usesPower, bot.EndurancePercent, addsLeft);
            if (kind != Kind.None)
                TryDrink(bot, kind);
        }

        /// <summary>Drinks the first potion of this kind in the bag when its reuse timer is free. True when drunk.</summary>
        public static bool TryDrink(GameBot bot, Kind kind, bool wakeToDrink = false)
        {
            if (bot.IsSitting && (!wakeToDrink || !bot.IsRecoveryResting) || bot.IsCrowdControlled || kind == Kind.None)
                return false;
            DrinkState state = NextDrink.GetOrAdd(bot.DatabaseID, _ => new DrinkState());
            if (Interlocked.CompareExchange(ref state.Drinking, 1, 0) != 0)
                return false;
            try
            {
                return TryUseBackpackPotion(bot, kind, state, wakeToDrink);
            }
            finally
            {
                Volatile.Write(ref state.Drinking, 0);
            }
        }

        private static bool TryUseBackpackPotion(GameBot bot, Kind kind, DrinkState state, bool wakeToDrink)
        {
            long now = GameLoop.GameLoopTime;
            int k = (int)kind;
            int revision = bot.IsPlayerCompanion ? Volatile.Read(ref state.CompanionBagRevision) :
                AutonomousBotEconomy.InventoryRevision(bot.DatabaseID);
            if (!ShouldLook(now, Volatile.Read(ref state.SkipUntil[k]), Volatile.Read(ref state.SkipRevision[k]), revision))
                return false;
            SpellLine line = SkillBase.GetSpellLine(GlobalSpellsLines.Potions_Effects);
            if (line == null)
                return false;
            ConcurrentDictionary<int, long> timers = state.Timers;
            for (eInventorySlot slot = eInventorySlot.FirstBackpack; slot <= eInventorySlot.LastBackpack; slot++)
            {
                DbInventoryItem item = bot.Inventory.GetItem(slot);
                if (!IsPotion(item))
                    continue;
                int required = item.Template?.LevelRequirement > 0 ? item.Template.LevelRequirement : Math.Min(50, item.Level);
                if (required > bot.Level)
                    continue;
                Spell spell = SkillBase.FindSpell(item.SpellID, line);
                if (spell == null || KindOf(spell.SpellType) != kind)
                    continue;
                if (timers.TryGetValue(spell.SharedTimerGroup, out long next) && now < next)
                {
                    // the timer is shared: no other potion of this group either, and no bag look until it is free
                    MarkSkip(state, k, next, revision);
                    return false;
                }
                if (spell.CastTime > 0 && (bot.IsCasting || bot.attackComponent.AttackState) ||
                    spell.IsHealing && spell.IsInstantCast && bot.IsAttacking)
                    return false;
                if (bot.IsSitting)
                {
                    if (!wakeToDrink || !bot.IsRecoveryResting || bot.InCombat)
                        return false;
                    bot.WakeRecoveryRest();
                }
                if (!bot.CastSpell(spell, line))
                    return false;
                bot.Stealth(false);
                timers[spell.SharedTimerGroup] = now + Math.Max(1, item.CanUseEvery) * 1000L;
                UseCharge(bot, item);
                FeatureCounters165.Add($"bot_potion_{kind.ToString().ToLowerInvariant()}");
                return true;
            }
            MarkSkip(state, k, now + NoPotionRecheckMilliseconds, revision);
            return false;
        }

        private static void MarkSkip(DrinkState state, int kind, long until, int revision)
        {
            Volatile.Write(ref state.SkipRevision[kind], revision);
            Volatile.Write(ref state.SkipUntil[kind], until);
        }

        /// <summary>A charge used, as a player's: a stack loses one, the last charge uses the item up.</summary>
        private static void UseCharge(GameBot bot, DbInventoryItem item)
        {
            if (item.Count > 1)
                bot.Inventory.RemoveCountFromStack(item, 1);
            else if (--item.Charges < 1)
                bot.Inventory.RemoveCountFromStack(item, 1);
            if (bot.IsAutonomousWorldBot)
                AutonomousBotEconomy.MarkInventoryChanged(bot);
            bot.MarkAutonomousStateDirty();
            AutonomousBotStatusPersistence.Queue(bot, true);
        }

        /// <summary>The bot left the world: its potion timers go.</summary>
        public static void Forget(long botId) => NextDrink.TryRemove(botId, out _);

        /// <summary>
        /// On a town errand (O13): a rich gamebot of level 20+ buys a heal draught from a potion merchant near its merchant.
        /// </summary>
        public static void RestockHealDraughts(GameBot bot, GameMerchant at)
        {
            if (!Classic165Mode.IsOn || bot?.Inventory == null || !bot.IsAutonomousWorldBot || bot.IsTemporaryGroupHelper ||
                HiredCompanions165.IsHired(bot) || bot.Level < MinBuyLevel)
                return;
            long reserve = AutonomousWorldBotController.ShoppingReserve(bot.Level);
            long coin = AutonomousBotEconomy.GetMoney(bot.DatabaseID);
            // the cheapest a draught can be: nothing to look at while the bot is far from rich
            if (HealDraughtsWanted(bot.Level, coin, reserve, 1) == 0)
                return;
            SpellLine line = SkillBase.GetSpellLine(GlobalSpellsLines.Potions_Effects);
            if (line == null)
                return;
            int owned = 0;
            for (eInventorySlot slot = eInventorySlot.FirstBackpack; slot <= eInventorySlot.LastBackpack; slot++)
                if (bot.Inventory.GetItem(slot) is { } item && IsPotion(item) &&
                    SkillBase.FindSpell(item.SpellID, line) is { } owns && KindOf(owns.SpellType) == Kind.Heal)
                    owned++;
            DbItemTemplate draught = new[] { at }.Concat(bot.GetNPCsInRadius(2500).OfType<GameMerchant>())
                .Where(m => m != null && AutonomousBotEconomy.SellsForCoin(m) && m.TradeItems != null &&
                            (m.Realm == eRealm.None || m.Realm == bot.Realm))
                .SelectMany(m => m.TradeItems.GetAllItems().Values.OfType<DbItemTemplate>())
                .Where(t => t.Price > 0 && t.Object_Type == (int)eObjectType.Magical && t.Item_Type == (int)eInventorySlot.FirstBackpack &&
                            t.SpellID > 0 && t.MaxCharges > 0 && (t.LevelRequirement > 0 ? t.LevelRequirement : t.Level) <= bot.Level &&
                            SkillBase.FindSpell(t.SpellID, line) is { } s && KindOf(s.SpellType) == Kind.Heal)
                .OrderBy(t => t.Price)
                .FirstOrDefault();
            if (draught == null)
                return;
            int wanted = HealDraughtsWanted(bot.Level, coin, reserve, draught.Price);
            for (; owned < wanted; owned++)
            {
                eInventorySlot slot = bot.Inventory.FindFirstEmptySlot(eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack);
                if (slot == eInventorySlot.Invalid || !AutonomousBotEconomy.TrySpend(bot.DatabaseID, draught.Price, BotMoneyLedger165.Kind.Potion))
                    return;
                DbInventoryItem bought = GameInventoryItem.Create(draught);
                if (bought == null || !bot.Inventory.AddItem(slot, bought))
                {
                    AutonomousBotEconomy.AddMoney(bot.DatabaseID, draught.Price, BotMoneyLedger165.Kind.Potion);
                    return;
                }
                AutonomousBotEconomy.MarkInventoryChanged(bot);
                bot.MarkAutonomousStateDirty();
                AutonomousBotStatusPersistence.Queue(bot, true);
                Log.Info($"BOT_POTION_165 bot={bot.Name} id={bot.DatabaseID} level={bot.Level} item=\"{draught.Name}\" copper={draught.Price}");
            }
        }
    }
}
