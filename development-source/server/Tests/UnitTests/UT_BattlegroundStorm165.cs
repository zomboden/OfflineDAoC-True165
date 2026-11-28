using System.Linq;
using System.Numerics;
using DOL.GS.Keeps;
using NUnit.Framework;

namespace DOL.GS.Tests
{
    /// <summary>Battlegrounds fix 2026-11-12, lane BGSTORM: the defense loop, the watchdog, the keep level after a capture,
    /// the battleground Siege Masters and the keep-gate walk check.</summary>
    [TestFixture, NonParallelizable]
    public class UT_BattlegroundStorm165
    {
        [SetUp] public void SetUp() { RvrEventTestState.Clear(); }
        [TearDown] public void TearDown() { RvrEventTestState.Clear(); }

        [TestCase(7, false, 7, 0, false)]
        [TestCase(8, false, 0, 0, true)]
        [TestCase(7, true, 0, 179_999, true)]
        [TestCase(7, true, 0, 180_000, false)]
        [TestCase(4, true, 4, 180_000, true)]
        [TestCase(3, true, 3, 0, false)]
        public void LaunchQuorumDoesNotAbortAnActiveAssault(int inside, bool launched, int nearKeep, long elapsed, bool allowed)
        {
            Assert.That(AutonomousBattlegroundWave.AllowsAssault(inside, launched, nearKeep, elapsed), Is.EqualTo(allowed));
        }

        [TestCase(3, true, 0, AutonomousBattlegroundWave.RallyDecision.Hold)]
        [TestCase(6, true, 10_000, AutonomousBattlegroundWave.RallyDecision.Push)]
        [TestCase(8, false, 49_999, AutonomousBattlegroundWave.RallyDecision.Hold)]
        [TestCase(4, false, 50_000, AutonomousBattlegroundWave.RallyDecision.Push)]
        [TestCase(3, true, 50_000, AutonomousBattlegroundWave.RallyDecision.Regroup)]
        public void GatePushWaitsForSupportButCanProceedWithoutAHealer(int rallied, bool healer, long elapsed,
            AutonomousBattlegroundWave.RallyDecision decision)
        {
            Assert.That(AutonomousBattlegroundWave.DecideRally(rallied, healer, elapsed), Is.EqualTo(decision));
        }

        [TestCase(3, 10_000, false)]
        [TestCase(4, 34_999, true)]
        [TestCase(8, 35_000, false)]
        public void OnlyARecoverableForceRegroupsAndTheWaitIsBounded(int inside, long elapsed, bool shouldRegroup)
        {
            Assert.That(AutonomousBattlegroundWave.CanRegroup(inside, 100_000 + elapsed,
                100_000 + AutonomousBattlegroundWave.RegroupMilliseconds), Is.EqualTo(shouldRegroup));
        }

        [Test]
        public void ACoordinatedWaveCanSurviveCasualtiesAndReachTheBreach()
        {
            Assert.That(AutonomousBattlegroundWave.WaveSize(11), Is.EqualTo(8));
            Assert.That(AutonomousBattlegroundWave.DecideRally(3, false, 15_000),
                Is.EqualTo(AutonomousBattlegroundWave.RallyDecision.Hold));
            Assert.That(AutonomousBattlegroundWave.DecideRally(6, true, 30_000),
                Is.EqualTo(AutonomousBattlegroundWave.RallyDecision.Push));

            Assert.That(KeepRaidDoors.NextRaidGate([(1, true, 2_000f), (2, true, 1_500f)]), Is.Zero);
            Assert.That(AutonomousBattlegroundWave.AllowsAssault(6, true, 5, 100_000), Is.True);
            Assert.That(KeepRaidDoors.NextRaidGate([(1, false, 2_000f), (2, true, 1_500f)]), Is.EqualTo(1));
            Assert.That(AutonomousBattlegroundWave.AllowsAssault(5, true, 4, 185_000), Is.True);
            Assert.That(KeepRaidDoors.NextRaidGate([(1, false, 2_000f), (2, false, 1_500f)]), Is.EqualTo(-1));
        }

        // ---- the defense loop (BG250 D1, BG252 S3, BG253 F2)

        [TestCase((ushort)250, false)]
        [TestCase((ushort)251, false)]
        [TestCase((ushort)252, false)]
        [TestCase((ushort)253, false)]
        [TestCase((ushort)1, true)]
        [TestCase((ushort)100, true)]
        [TestCase((ushort)200, true)]
        [TestCase((ushort)163, true)]
        public void OnlyKeepsOutsideTheBattlegroundsRaiseRealmEvents(ushort region, bool raises) =>
            Assert.That(AutonomousRvrKeepPolicy.RaisesRealmEvent(region), Is.EqualTo(raises));

        [Test]
        public void APlayerAttackOnABotHeldBattlegroundKeepOpensNoDefenseEvent()
        {
            var thidranki = new AutonomousRvrEventLayer.LiveObjective("rvr-keep-11", "Thidranki Faste",
                AutonomousRvrEventLayer.Intent.AssaultKeep, eRealm.Midgard, 252, 33089, 38271, 3720, false, 0, 0, 18, 2);
            Assert.That(AutonomousRvrEventLayer.BeginDefenseResponse(thidranki, eRealm.Albion, "offline", GameLoop.GameLoopTime + 100),
                Is.False, "level-50 warbands can never enter a battleground");
            Assert.That(AutonomousRvrEventLayer.Snapshot(), Is.Empty);

            var frontier = thidranki with { Id = "rvr-keep-50", RegionId = 100 };
            Assert.That(AutonomousRvrEventLayer.BeginDefenseResponse(frontier, eRealm.Albion, "offline", GameLoop.GameLoopTime + 100),
                Is.True, "a frontier keep still raises its defense");
        }

        [Test]
        public void NoForcedSiegeOnABattlegroundKeep()
        {
            var murdaigean = new AutonomousRvrEventLayer.LiveObjective("rvr-keep-32", "Dun Murdaigean",
                AutonomousRvrEventLayer.Intent.AssaultKeep, eRealm.Hibernia, 251, 33089, 38271, 3720, false, 0, 0, 27, 2);
            Assert.That(AutonomousRvrEventLayer.ForceStart(murdaigean, eRealm.Albion, GameLoop.GameLoopTime + 100, out _), Is.False);
            Assert.That(AutonomousRvrEventLayer.Snapshot(), Is.Empty);
        }

        // ---- the stuck watchdog (BG252 S4, BG253 F3)

        [TestCase(true, false, false, true)]
        [TestCase(false, true, true, true)]
        [TestCase(false, true, false, false)]
        [TestCase(false, false, true, false)]
        [TestCase(false, false, false, false)]
        public void BattlegroundToursInsideTheirBattlegroundKeepNoGoalClock(bool rvr, bool battleground, bool inside, bool exempt) =>
            Assert.That(AutonomousStuckWatchdog.RoamsLikeRvr(rvr, battleground, inside), Is.EqualTo(exempt));

        [Test]
        public void ABattlegroundHolderAtItsPostRefreshesBothWatchdogClocks()
        {
            eAutonomousProgressKind inside = AutonomousWorldBotController.HoldingPostProgress(true);
            Assert.That(inside, Is.EqualTo(eAutonomousProgressKind.SiegeParticipation));
            Assert.That(AutonomousStuckWatchdog.CountsAsGoalProgress(inside), Is.True);
            Assert.That(AutonomousWorldBotController.HoldingPostProgress(false), Is.EqualTo(eAutonomousProgressKind.Objective),
                "frontier defenders keep their event's protection, unchanged");
        }

        [Test]
        public void RecoveriesEndTheGoalAttemptByTheirMarker()
        {
            Assert.That(AutonomousStuckWatchdog.GoalEndFor("AUTONOMOUS_GOAL_STALL_45M_RECOVERY"), Is.EqualTo(GoalAttemptEnd.NoProgress45m));
            Assert.That(AutonomousStuckWatchdog.GoalEndFor("AUTONOMOUS_STUCK_15M_RECOVERY"), Is.EqualTo(GoalAttemptEnd.NoMovement15m));
            Assert.That(AutonomousStuckWatchdog.GoalEndFor("AUTONOMOUS_POSITION_RECOVERY"), Is.EqualTo(GoalAttemptEnd.InvalidPosition));
            Assert.That(AutonomousStuckWatchdog.GoalEndFor(null), Is.EqualTo(GoalAttemptEnd.InvalidPosition));
        }

        // ---- the keep level after a capture (BG252 S2)

        [Test]
        public void ACapturedBattlegroundKeepStaysAtLevelOne()
        {
            Assert.That(AbstractGameKeep.LevelAfterCapture(true, 4), Is.EqualTo(1));
            Assert.That(AbstractGameKeep.LevelAfterCapture(false, 4), Is.EqualTo(4), "frontier keeps unchanged");
            Assert.That(AbstractGameKeep.BattlegroundKeepLevel, Is.EqualTo(1));
        }

        [Test]
        public void BattlegroundKeepGuardsTakeTheirRowLevel()
        {
            // Prima: Thidranki guards 24, lord 33; Murdaigean commanders 33; Caer Caledon guards 35, lord 42.
            Assert.That(AbstractGameKeep.UsesRowLevel(true, false, true, 24), Is.True);
            Assert.That(AbstractGameKeep.UsesRowLevel(true, false, true, 42), Is.True, "lords too (same as the formula at level 1)");
            Assert.That(AbstractGameKeep.UsesRowLevel(true, false, false, 24), Is.False, "frontier keeps keep the formula");
            Assert.That(AbstractGameKeep.UsesRowLevel(true, true, true, 90), Is.False, "portal keeps keep the formula");
            Assert.That(AbstractGameKeep.UsesRowLevel(false, false, true, 24), Is.False, "outside True 1.65");
            Assert.That(AbstractGameKeep.UsesRowLevel(true, false, true, 1), Is.False, "a row without a real level");
            Assert.That(AbstractGameKeep.UsesRowLevel(true, false, true, 0), Is.False, "not loaded from a row");
        }

        [Test]
        public void TheLordFormulaMatchesPrimaAtKeepLevelOne()
        {
            // AbstractGameKeep.GetBaseLevel + SetGuardLevel for a lord: BaseLevel + (BaseLevel/10 + 1) * 2 + keepLevel * 1.6,
            // keep level 1 after a capture too (LevelAfterCapture). The rows carry the same values.
            static int Lord(int baseLevel) => (byte)(baseLevel + (baseLevel / 10 + 1) * 2 + AbstractGameKeep.LevelAfterCapture(true, 4) * 1.6);
            Assert.That(Lord(26), Is.EqualTo(33), "Thidranki");
            Assert.That(Lord(31), Is.EqualTo(40), "Murdaigean");
            Assert.That(Lord(33), Is.EqualTo(42), "Caer Caledon at BaseLevel 33");
        }

        // ---- the battleground Siege Masters

        [Test]
        public void ASiegeMasterServesItsOwnRealm()
        {
            Assert.That(AutonomousWorldBotController.IsSiegeMasterFor(eRealm.Albion, eRealm.Albion, "SiegeMerchantAlb"), Is.True);
            Assert.That(AutonomousWorldBotController.IsSiegeMasterFor(eRealm.Albion, eRealm.None, "SiegeMerchantAlb"), Is.True);
            Assert.That(AutonomousWorldBotController.IsSiegeMasterFor(eRealm.Midgard, eRealm.Albion, "SiegeMerchantAlb"), Is.False);
            Assert.That(AutonomousWorldBotController.IsSiegeMasterFor(eRealm.Midgard, eRealm.Midgard, "SiegeMerchantAlb"), Is.False);
            Assert.That(AutonomousWorldBotController.IsSiegeMasterFor(eRealm.Hibernia, eRealm.Hibernia, "SiegeMerchantHib"), Is.True);
            Assert.That(AutonomousWorldBotController.IsSiegeMasterFor(eRealm.Hibernia, eRealm.Hibernia, null), Is.False);
        }

        [Test]
        public void ACrewBuysAKitOnlyWhenItCanPayAndAmmunitionAsFarAsItsCoinGoes()
        {
            // ram deed 500,000 copper (the world's list price), catapult stone 1,820
            Assert.That(AutonomousWorldBotController.SiegeMasterPurchaseCount(false, 0, 1, 499_999, 500_000), Is.EqualTo(0));
            Assert.That(AutonomousWorldBotController.SiegeMasterPurchaseCount(false, 0, 1, 500_000, 500_000), Is.EqualTo(1));
            Assert.That(AutonomousWorldBotController.SiegeMasterPurchaseCount(false, 0, 1, 900_000, 0), Is.EqualTo(0), "not for sale");
            Assert.That(AutonomousWorldBotController.SiegeMasterPurchaseCount(true, 0, 20, 1_000_000, 1_820), Is.EqualTo(20));
            Assert.That(AutonomousWorldBotController.SiegeMasterPurchaseCount(true, 15, 20, 1_000_000, 1_820), Is.EqualTo(5));
            Assert.That(AutonomousWorldBotController.SiegeMasterPurchaseCount(true, 0, 20, 1_820 * 7 + 5, 1_820), Is.EqualTo(7));
            Assert.That(AutonomousWorldBotController.SiegeMasterPurchaseCount(true, 20, 20, 1_000_000, 1_820), Is.EqualTo(0));
        }

        [Test]
        public void ACrewWorkingAnEngineLooksForNoNewKit()
        {
            Assert.That(AutonomousWorldBotController.NeedsNewKit(true, false, false), Is.True, "no engine, no kit: get one");
            Assert.That(AutonomousWorldBotController.NeedsNewKit(true, true, false), Is.False,
                "the kit was spent on the deployed engine: keep operating it (frontier and battleground)");
            Assert.That(AutonomousWorldBotController.NeedsNewKit(true, false, true), Is.False, "carries one");
            Assert.That(AutonomousWorldBotController.NeedsNewKit(false, false, false), Is.False, "normal mode: synthetic engines");
        }

        [Test]
        public void KitsAreReadByRealm()
        {
            Assert.That(AutonomousWorldBotController.KitKind(eRealm.Albion, "deploy_siege_ram"), Is.EqualTo(BotSiegeKind.Ram));
            Assert.That(AutonomousWorldBotController.KitKind(eRealm.Midgard, "deploy_siege_catapult2"), Is.EqualTo(BotSiegeKind.Catapult));
            Assert.That(AutonomousWorldBotController.KitKind(eRealm.Hibernia, "deploy_siege_ballista3"), Is.EqualTo(BotSiegeKind.Ballista));
            Assert.That(AutonomousWorldBotController.KitKind(eRealm.Midgard, "deploy_siege_ram"), Is.Null, "another realm's deed");
            Assert.That(AutonomousWorldBotController.KitKind(eRealm.Albion, "catapult_stone"), Is.Null);
        }

        // ---- the keep-gate walk check (BG250 N1, Caer Caledon's gatehouse)

        private static readonly Vector3 Caledon302 = new(32963, 38991, 3783);
        private const int Caledon302Heading = 617;
        private static readonly Vector3 Thidranki301 = new(34333, 38275, 3720);
        private const int Thidranki301Heading = 1024;

        [Test]
        public void TheWalkUnderCaerCaledonsGatehouseIsCaughtAtTheInnerDoor()
        {
            // BG250 leak.py: the shortest walk with both gates shut runs at Z 3721 under the gatehouse's floor plate,
            // from 32935,39056 into the courtyard at 32921,38451.
            Assert.That(Pathfinder.TryCrossKeepGate(new(32935, 39056, 3721), new(32921, 38451, 3721), Caledon302, Caledon302Heading,
                out float stop), Is.True);
            Assert.That(stop, Is.GreaterThanOrEqualTo(0f).And.LessThan(0.2f), "the stop comes before the door");
            Assert.That(Pathfinder.TryCrossKeepGate(new(32780, 39296, 3721), new(32935, 39056, 3721), Caledon302, Caledon302Heading,
                out _), Is.False, "the leg before the door does not cross it");
        }

        [Test]
        public void AWalkThroughAGateStopsInFrontOfItAndAWalkBesideItDoesNot()
        {
            Assert.That(Pathfinder.TryCrossKeepGate(new(34600, 38275, 3720), new(34100, 38275, 3720), Thidranki301, Thidranki301Heading,
                out float stop), Is.True);
            Assert.That(34600 - stop * 500, Is.EqualTo(34333 + Pathfinder.KeepGateStandOff).Within(1));
            // along the wall (parallel to the gate)
            Assert.That(Pathfinder.TryCrossKeepGate(new(34200, 38000, 3720), new(34200, 38600, 3720), Thidranki301, Thidranki301Heading,
                out _), Is.False);
            // through the plane, but 200 to the side of the door (outside the doorway)
            Assert.That(Pathfinder.TryCrossKeepGate(new(34600, 38475, 3720), new(34100, 38475, 3720), Thidranki301, Thidranki301Heading,
                out _), Is.False);
            // the wall-walk above the gate (Z 3975)
            Assert.That(Pathfinder.TryCrossKeepGate(new(34600, 38275, 3975), new(34100, 38275, 3975), Thidranki301, Thidranki301Heading,
                out _), Is.False);
            // ending on the plane counts, starting on it going away does too (either side)
            Assert.That(Pathfinder.TryCrossKeepGate(new(34600, 38275, 3720), new(34333, 38275, 3720), Thidranki301, Thidranki301Heading,
                out _), Is.True);
            Assert.That(Pathfinder.TryCrossKeepGate(new(34400, 38275, 3720), new(34500, 38275, 3720), Thidranki301, Thidranki301Heading,
                out _), Is.False, "wholly in front of the gate");
        }

        [TestCase(250000302, 32963, 38991, 617, 250000301, 32673, 39237)]
        [TestCase(251000302, 32698, 37833, 1589, 251000301, 32337, 37404)]
        [TestCase(252000302, 34044, 38272, 1024, 252000301, 34333, 38275)]
        [TestCase(253000302, 33659, 39059, 3901, 253000301, 33849, 39604)]
        public void TheInnerDoorsStandOffIsInTheGatePassage(int innerId, int ix, int iy, int heading, int outerId, int ox, int oy)
        {
            // All four central keep rows stand at 33089,38271.
            Vector2 spot = KeepRaidDoors.StandOff(new(ix, iy), heading, new(33089, 38271), 150);
            Vector2 inner = new(ix, iy), outer = new(ox, oy);
            Assert.That(Vector2.Distance(spot, inner), Is.EqualTo(150).Within(0.5));
            Vector2 passage = Vector2.Normalize(outer - inner);
            Vector2 offset = spot - inner;
            Assert.That(Vector2.Dot(offset, passage), Is.GreaterThan(140), $"{innerId}: toward {outerId}, along the passage");
            Assert.That(System.Math.Abs(offset.X * passage.Y - offset.Y * passage.X), Is.LessThan(15), "not into the gatehouse wall");
        }

        [Test]
        public void TheOuterGatesStandOffIsOnTheField()
        {
            Vector2 spot = KeepRaidDoors.StandOff(new(34333, 38275), 1024, new(33089, 38271), 150);
            Assert.That(spot.X, Is.EqualTo(34483).Within(0.5), "Thidranki gate 01, outside");
            Assert.That(spot.Y, Is.EqualTo(38275).Within(0.5));
        }

        [TestCase(250000301, 32673, 39237, 2665, 250000302, 32963, 38991)]
        [TestCase(251000301, 32337, 37404, 3642, 251000302, 32698, 37833)]
        [TestCase(252000301, 34333, 38275, 1024, 252000302, 34044, 38272)]
        [TestCase(253000301, 33849, 39604, 1864, 253000302, 33659, 39059)]
        public void EveryBattlegroundGatePassageRunsAlongItsDoorsHeading(int outerId, int ox, int oy, int heading, int innerId, int ix, int iy)
        {
            // The walk from the outer gate to the inner door (the gatehouse passage) crosses the outer gate's plane.
            Assert.That(Pathfinder.TryCrossKeepGate(new(ox - (ix - ox) / 2f, oy - (iy - oy) / 2f, 3720), new(ix, iy, 3720),
                new(ox, oy, 3720), heading, out _), Is.True, $"{outerId} -> {innerId}");
        }
    }
}
