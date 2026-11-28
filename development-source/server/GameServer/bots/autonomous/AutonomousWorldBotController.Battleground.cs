using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS.Keeps;

namespace DOL.GS
{
    /// <summary>
    /// Battleground tours (the fourth bot goal: the PvP focus). A bot only ever goes to the battleground of its own
    /// level bracket. True 1.65 Mode: through its realm's portal keep teleporter (Master Visur, Stor Gothi Annark,
    /// Glasny), the period way in with the battlegrounds medallion; normal mode used the town teleporters'
    /// [Battlegrounds] choice. Inside, enemies are found by the normal frontier threat scan; between fights the bot
    /// assaults the central keep when another realm holds it (guards, the raid doors in order, then the lord through
    /// the normal capture pipeline) and otherwise defends or roams around it. Leaving uses the realm's exit back to
    /// its border keep. Owner 2026-11-12: any bot whose level fits the bracket may also hunt the battleground's
    /// monsters with an ordinary PvE goal (SelectCamp); LeaveBattlegroundWhenDone walks such a bot out when its
    /// business there is over.
    /// </summary>
    public sealed partial class AutonomousWorldBotController
    {
        // Teleporter trips into a battleground have no walking alternative.
        private const double BattlegroundNoWalkSeconds = 3600;
        private const int BattlegroundGateGuardRadius = 450;
        private const int BattlegroundGuardClearRadius = 1_300;
        private long _nextBattlegroundRoamTick;
        private Vector3? _battlegroundRoamPoint;
        private int _battlegroundApproachDoor;
        private Vector3 _battlegroundDoorApproach;

        private bool ExecuteBattleground(BotBrain brain, GameBot bot)
        {
            BattlegroundBrackets.Bracket bracket = BattlegroundBrackets.ForLevel(bot.Level);
            if (bracket == null)
            {
                // Out-levelled: the tour ends here; leaving the battleground happens on the next goal's travel.
                AutonomousObjectiveAssignments.EndBattlegroundTour(bot, $"Outgrew the battleground brackets at level {bot.Level}");
                _nextPlanTick = 0;
                return true;
            }
            if (bot.CurrentRegionID != bracket.RegionId)
            {
                GameLocation entry = BattlegroundBrackets.BotArrival(bracket, bot.Realm);
                return TravelToBattleground(bot, bracket, new(entry.X, entry.Y, entry.Z));
            }

            AbstractGameKeep central = CentralKeep(bracket); // starts neutral (realm none); taken from any realm
            bool assault = central != null && central.Realm != bot.Realm;
            if (central != null)
            {
                // Rebuilt only when the target changes (no per-turn allocation).
                if (_rvrDestination?.RegionId != bracket.RegionId || _rvrDestination.Id != $"rvr-keep-{central.KeepID}")
                {
                    _rvrApproachDestination = null;
                    _patrolDestination = null;
                    _rvrDestination = new($"rvr-keep-{central.KeepID}", central.Name, bracket.Name, bracket.RegionId,
                        central.X, central.Y, central.Z, 1, false, true);
                }
            }
            else
            {
                _rvrDestination = null;
            }
            _rvrSharedEvent = false;
            // Too few of the realm here for a wave: fight in the field instead of standing at the staging point or
            // feeding single bots to the keep's guards (Thidranki, 2026-10-17).
            bool classic = Classic165Mode.IsOn;
            bool tooFew = assault && (classic
                ? !AutonomousBattlegroundWave.CanAssault(bot, bracket, central)
                : AutonomousBattlegroundWave.TooFewForAssault(bot.Realm, bracket));
            _rvrIntent = assault && !tooFew ? AutonomousRvrEventLayer.Intent.AssaultKeep : AutonomousRvrEventLayer.Intent.Roam;
            if (!assault || tooFew)
                _battlegroundGatePassed = false;
            if (tooFew)
                return RoamBattleground(bot, bracket, central);

            if (assault)
            {
                GameKeepDoor shutGate = ClosedMainGate(central);
                if (shutGate != null)
                {
                    _battlegroundGatePassed = false;
                    if (!classic && TryRunSiegeJob(bot)) return true;
                }
                else
                {
                    ReportBattleAttendance(bot, GameLoop.GameLoopTime);
                    // The breach is the end of this siege assignment. Do not send an operator
                    // back to the portal keep for a kit while the wave goes for the lord.
                    if (_siegeJobKeep != null)
                        ReleaseSiegeJob(bot);
                    if (_crewSpotRam != null)
                        LeaveRamCrew(bot);
                }
                // The realm assaults as one wave (AutonomousBattlegroundWave); a bot in a fight finishes it first.
                if (!bot.InCombat && shutGate != null)
                {
                    if (AutonomousBattlegroundWave.ShouldGather(bot, bracket, central, out Vector3 staging, out string gathered))
                        return HoldBattlegroundAssault(bot, bracket, central, staging, "Gathering for the assault", gathered);
                    if (classic && AutonomousBattlegroundWave.TryHoldForPush(bot, bracket, central, shutGate,
                            out Vector3 rally, out string ready))
                        return HoldBattlegroundAssault(bot, bracket, central, rally, "Forming for the gate push", ready);
                }

                // Keep the ordinary threat scan and combat AI. At the gate, agree on a nearby
                // reachable guard before spending the push's time on the door or a siege errand.
                GameLiving target = classic && shutGate != null && bot.IsWithinRadius(shutGate, BattlegroundGuardClearRadius)
                    ? FindRvrTarget(bot, shutGate)
                    : null;
                if (target == null && classic && shutGate != null && TryRunSiegeJob(bot))
                    return true;
                target ??= FindReachableEnemyDoor(bot, shutGate, classic);
                // The normal threat scan and retaliation have already had their turn. Avoid pulling
                // the force off the march to chase optional keep guards before it reaches the gate.
                bool marchBeforeGuards = classic && shutGate != null && !bot.InCombat &&
                    !bot.IsWithinRadius(shutGate, BattlegroundGateGuardRadius);
                if (target == null && marchBeforeGuards && MarchToBattlegroundGate(bot, bracket, central, shutGate, classic))
                    return true;

                // With both gates open, use the explicit breach and lord walk below. A generic
                // guard scan could otherwise pick up a distant guard and pull the push away.
                if (!classic)
                    target ??= FindRvrTarget(bot);
                if (target != null)
                {
                    bot.StopMovingOnPath();
                    bot.StopMoving();
                    bot.TargetObject = target;
                    brain.AddToAggroList(target, Math.Max(100, target.EffectiveLevel * 12));
                    brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                    SetBattlegroundStatus(bot, bracket, target is GameKeepDoor ? "Battering the keep gate" : "Assaulting the keep",
                        $"{central.Name} is held by {Holder(central)}", target.Name);
                    return false;
                }
                if (shutGate != null)
                {
                    // Skip a second movement order when this turn already tried the same approach.
                    return !marchBeforeGuards && MarchToBattlegroundGate(bot, bracket, central, shutGate, classic);
                }

                GameKeepGuard lord = central.Guards.Values.FirstOrDefault(guard => guard is GuardLord && guard.IsAlive);
                if (lord != null && lord.CurrentRegionID == bot.CurrentRegionID)
                {
                    if (_battlegroundGateKeep != central.KeepID)
                    {
                        _battlegroundGateKeep = central.KeepID;
                        _battlegroundGatePassed = false;
                    }

                    // Walk beyond the final raid door. Arriving near a door centre did not
                    // mean the bot had crossed it, and nearest-gate selection changed mid-walk.
                    GameKeepDoor innerGate = central.Doors.Values
                        .Where(door => KeepRaidDoors.IsRaidGate(central, door))
                        .OrderByDescending(KeepRaidDoors.Number).FirstOrDefault();
                    if (innerGate != null && !_battlegroundGatePassed)
                    {
                        Vector3 inner = BattlegroundBreachPoint(central, innerGate);
                        Vector3 here = new(bot.X, bot.Y, bot.Z);
                        if (IsRouteDestinationReached(here, inner) ||
                            bot.IsWithinRadius(lord, BattlegroundLordStrike) && IsPastBattlegroundGate(central, innerGate, here))
                            _battlegroundGatePassed = true;
                        else if (IssuePath(bot, inner, retargetWhileMoving: true))
                        {
                            TraceStorm(this, bot, central, lord, innerGate, "breach");
                            SetBattlegroundStatus(bot, bracket, "Crossing the breached keep gate", central.Name, lord.Name);
                            return true;
                        }
                    }

                    if ((_battlegroundGatePassed || innerGate == null) && bot.IsWithinRadius(lord, BattlegroundLordStrike) &&
                        GameServer.ServerRules.IsAllowedToAttack(bot, lord, true) &&
                        AutonomousZoneItinerary.HasCompleteCorridor(
                            AutonomousKeepApproachNavigation.ForRealm(PathfindingProvider.Instance, bot.CurrentRegion, bot.Realm),
                            bot.CurrentZone, new(bot.X, bot.Y, bot.Z), new(lord.X, lord.Y, lord.Z)))
                    {
                        bot.StopMovingOnPath();
                        bot.StopMoving();
                        bot.TargetObject = lord;
                        brain.AddToAggroList(lord, Math.Max(100, lord.EffectiveLevel * 12));
                        brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                        TraceStorm(this, bot, central, lord, innerGate, "lord-engage");
                        SetBattlegroundStatus(bot, bracket, "Assaulting the keep lord", central.Name, lord.Name);
                        return false;
                    }
                    if (IssuePath(bot, new(lord.X, lord.Y, lord.Z), retargetWhileMoving: true))
                    {
                        TraceStorm(this, bot, central, lord, innerGate, "lord");
                        SetBattlegroundStatus(bot, bracket, "Storming the keep", "Heading for the keep lord", lord.Name);
                        return true;
                    }
                    TraceStorm(this, bot, central, lord, innerGate, "lord-path-refused");
                    return false;
                }
                if (FollowKeepTravel(bot, _rvrDestination))
                {
                    SetBattlegroundStatus(bot, bracket, $"Marching on {central.Name}", $"{Holder(central)} holds the central keep");
                    return true;
                }
                // While a raid door stands the keep's centre has no path (209 route give-ups in Abermenai, run 21): the
                // approach is then the front of the raid's next shut door (S1/S1b, as above).
                Vector3 approach = _rvrApproachDestination ?? (ClosedMainGate(central) is { } closedGate
                    ? KeepRaidDoors.StandOff(central, closedGate)
                    : new Vector3(central.X, central.Y, central.Z));
                if (Vector3.DistanceSquared(new(bot.X, bot.Y, bot.Z), approach) > 120 * 120)
                    IssuePath(bot, approach);
                else
                {
                    bot.StopMovingOnPath();
                    bot.StopMoving();
                }
                SetBattlegroundStatus(bot, bracket, "At the keep assault approach", $"{central.Name} is held by {Holder(central)}");
                return true;
            }

            // Own (or no) central keep: a third of the realm's bots hold its walls, the rest roam the field. A warband
            // holds or roams together (its leader's id decides).
            if (central != null && BattlegroundSquadKey(bot) % 3 == 0)
            {
                if (bot.GetDistanceTo(new Point3D(central.X, central.Y, central.Z)) > 3500)
                    return TravelToDefensivePost(bot, central, new(central.X, central.Y, central.Z));
                SetBattlegroundStatus(bot, bracket, $"Defending {central.Name}", "Holding the realm's central keep");
                // Holding the post is the holder's job: no stuck exit for standing at it (AutonomousBattlegroundExit).
                MarkBattlegroundProgress(bot, GameLoop.GameLoopTime);
                return HoldDefensiveKeepPost(bot, central);
            }
            return RoamBattleground(bot, bracket, central);
        }

        /// <summary>The id that puts a bot in its squad: its warband leader's when it is in a battleground warband (the whole
        /// group heads for the same landmark), otherwise its own.</summary>
        private long BattlegroundSquadKey(GameBot bot) =>
            _groupDirective?.IsDynamic == true && _groupDirective.ObjectiveKind == eAutonomousObjectiveKind.Battleground &&
            _groupDirective.Leader is { DatabaseID: > 0 } leader ? leader.DatabaseID : bot.DatabaseID;

        private bool RoamBattleground(GameBot bot, BattlegroundBrackets.Bracket bracket, AbstractGameKeep central)
        {
            long now = GameLoop.GameLoopTime;
            Vector3 current = new(bot.X, bot.Y, bot.Z);
            if (!_battlegroundRoamPoint.HasValue || now >= _nextBattlegroundRoamTick ||
                Vector3.DistanceSquared(current, _battlegroundRoamPoint.Value) < 200 * 200)
            {
                // Roam between the field's landmarks: the central keep and the other realms' portal approaches.
                eRealm[] others = [eRealm.Albion, eRealm.Midgard, eRealm.Hibernia];
                Vector3[] anchors = others.Where(realm => realm != bot.Realm)
                    .Select(realm => BattlegroundBrackets.BotArrival(bracket, realm))
                    .Select(entry => Vector3.Lerp(new(entry.X, entry.Y, entry.Z),
                        central != null ? new(central.X, central.Y, central.Z) : new(entry.X, entry.Y, entry.Z), 0.6f))
                    .Append(central != null ? new Vector3(central.X, central.Y, central.Z) : current)
                    .ToArray();
                // squads (veteran-bots review 2026-11-11, rvr-social G7): a realm's roamers in a battleground split into
                // four packs that head for the same landmark in the same two-minute window, as Thidranki's gank squads
                // camped the portal approaches and the keep, instead of every bot wandering alone
                long window = now / BattlegroundSquads.WindowMilliseconds;
                Vector3 anchor = BattlegroundSquads.Enabled
                    ? anchors[BattlegroundSquads.AnchorIndex(bot.Realm, BattlegroundSquads.SquadOf(BattlegroundSquadKey(bot)), window, anchors.Length)]
                    : anchors[Random.Shared.Next(anchors.Length)];
                Zone zone = bot.CurrentRegion?.GetZone((int)anchor.X, (int)anchor.Y) ?? bot.CurrentZone;
                IPathfindingMgr nav = PathfindingProvider.Instance;
                if (zone != null && nav.IsAvailable && nav.HasNavmesh(zone))
                {
                    // Landmark heights come from stored coordinates; settle them on the mesh first.
                    anchor = nav.GetClosestPoint(zone, anchor, 700, 700, 6000, nav.DefaultFilters) ?? anchor;
                    anchor = nav.GetRandomPoint(zone, anchor, 900, nav.DefaultFilters) ?? anchor;
                }
                // Never a roam point under another realm's portal keep guards (AutonomousBattlegroundPortalKeeps).
                if (AutonomousBattlegroundPortalKeeps.EnemyKeepNear(bot.Realm, anchor.X, anchor.Y,
                        AutonomousBattlegroundPortalKeeps.AvoidRadius, out Vector2 enemyKeep) != eRealm.None)
                {
                    Vector2 clear = AutonomousBattlegroundPortalKeeps.RetreatPoint(enemyKeep, new(anchor.X, anchor.Y));
                    anchor = new(clear.X, clear.Y, anchor.Z);
                    if (zone != null && nav.IsAvailable && nav.HasNavmesh(zone))
                        anchor = nav.GetClosestPoint(zone, anchor, 400, 400, 6000, nav.DefaultFilters) ?? anchor;
                }
                _battlegroundRoamPoint = anchor;
                _nextBattlegroundRoamTick = BattlegroundSquads.Enabled
                    ? (window + 1) * BattlegroundSquads.WindowMilliseconds +
                      BattlegroundSquads.RepickOffsetMilliseconds(BattlegroundSquads.SquadOf(BattlegroundSquadKey(bot)), bot.ObjectID)
                    : now + 120_000 + bot.ObjectID % 30_000;
            }
            IssueVariedRvrPath(bot, _battlegroundRoamPoint.Value);
            SetBattlegroundStatus(bot, bracket, "Roaming the battleground",
                central != null ? $"{central.Name} is held by our realm" : "Hunting enemy realms on the field");
            return true;
        }

        // Stormers stop 1,600-2,500 from the lord with the gates down (runs 18-21), though the mesh reaches him from the
        // gates (navmesh probe 2026-10-17). One line every 20 s server-wide says where a stormer stands and what it walks to.
        private static long _nextStormTraceTick;
        private static void TraceStorm(AutonomousWorldBotController controller, GameBot bot, AbstractGameKeep keep, GameKeepGuard lord,
            GameKeepDoor gate, string step)
        {
            long now = GameLoop.GameLoopTime;
            long next = System.Threading.Interlocked.Read(ref _nextStormTraceTick);
            if (now < next || System.Threading.Interlocked.CompareExchange(ref _nextStormTraceTick, now + 20_000, next) != next)
                return;
            var mover = bot.movementComponent;
            Log.Info($"BG_STORM_TRACE bot={bot.Name} keep=\"{keep.Name}\" step={step} at={bot.X},{bot.Y},{bot.Z} " +
                     $"toLord={(int)bot.GetDistanceTo(lord)} lordAt={lord.X},{lord.Y},{lord.Z} " +
                     $"gate={(gate == null ? "none" : $"{gate.DoorId}@{gate.X},{gate.Y},{gate.Z} alive={gate.IsAlive} state={gate.State} toGate={(int)bot.GetDistanceTo(gate)}")} " +
                     $"moving={bot.IsMoving} path={mover?.LastPathfindingStatus} dest={(mover == null ? "-" : $"{mover.Destination.X:0},{mover.Destination.Y:0},{mover.Destination.Z:0}")} " +
                     $"inCombat={bot.InCombat} target={bot.TargetObject?.Name} " +
                     // Why a stormer stands between turns (run 22: 55% of the samples at the gate step stood 3,300 from it).
                     $"issued={Format(controller._issuedRouteDestination)} recovery={Format(controller._routeRecoveryWaypoint)} " +
                     $"orderIn={Math.Max(0, controller._nextMoveOrderTick - now)} stallReplans={controller._routeStallReplans}");

            static string Format(Vector3? point) => point is { } p ? $"{p.X:0},{p.Y:0},{p.Z:0}" : "-";
        }

        private int _battlegroundGateKeep;
        private bool _battlegroundGatePassed;
        public const int BattlegroundLordStrike = 900;

        private static string Holder(AbstractGameKeep keep) =>
            keep.Realm == eRealm.None ? "its neutral guards" : GlobalConstants.RealmToName(keep.Realm);

        /// <summary>The raid's next shut door (KeepRaidDoors: outer gate 01 before keep door 02, posterns never), or null.
        /// Nearest-first sent stormers to an inner door sealed behind the outer gate (BG251-253 S1).</summary>
        private static GameKeepDoor ClosedMainGate(AbstractGameKeep keep) => KeepRaidDoors.NextShutRaidGate(keep);

        private static readonly AbstractGameKeep[] CentralKeeps = new AbstractGameKeep[4];

        /// <summary>The bracket's central keep, looked up once (keeps never change at runtime; it was a keep-list scan and
        /// a LINQ pass on every battleground bot's think, BG251 scale (c)).</summary>
        private static AbstractGameKeep CentralKeep(BattlegroundBrackets.Bracket bracket)
        {
            int index = bracket.RegionId - 250;
            if (index < 0 || index >= CentralKeeps.Length) return null;
            AbstractGameKeep keep = System.Threading.Volatile.Read(ref CentralKeeps[index]);
            if (keep != null) return keep;
            keep = GameServer.KeepManager.GetKeepsOfRegion(bracket.RegionId).FirstOrDefault(candidate => !candidate.IsPortalKeep);
            if (keep != null) System.Threading.Volatile.Write(ref CentralKeeps[index], keep);
            return keep;
        }

        /// <summary>A closed gate of the enemy-held keep that this bot can stand at and hit, like players did.</summary>
        private static GameLiving FindReachableEnemyDoor(GameBot bot, GameKeepDoor door, bool classic) =>
            door != null && bot.IsWithinRadius(door, classic ? BattlegroundDoorMeleeReach(door) : BattlegroundGateGuardRadius) &&
            GameServer.ServerRules.IsAllowedToAttack(bot, door, true) ? door : null;

        private bool HoldBattlegroundAssault(GameBot bot, BattlegroundBrackets.Bracket bracket,
            AbstractGameKeep keep, Vector3 point, string activity, string progress)
        {
            if (Vector3.DistanceSquared(new(bot.X, bot.Y, bot.Z), point) > 250 * 250)
                IssuePath(bot, point);
            else
            {
                bot.StopMovingOnPath();
                bot.StopMoving();
            }
            SetBattlegroundStatus(bot, bracket, activity, $"{keep.Name}: {progress}");
            return true;
        }

        private static int BattlegroundDoorMeleeReach(GameKeepDoor door) =>
            GameBot.ClassicBaseMeleeAttackRange + GameDoorBase.MeleeReachBonus(door, true);

        private bool MarchToBattlegroundGate(GameBot bot, BattlegroundBrackets.Bracket bracket,
            AbstractGameKeep central, GameKeepDoor gate, bool classic)
        {
            Vector3 approach = KeepRaidDoors.StandOff(central, gate);
            if (classic)
            {
                if (_battlegroundApproachDoor != gate.DoorId)
                {
                    _battlegroundApproachDoor = gate.DoorId;
                    _battlegroundDoorApproach = approach;
                    var zone = bot.CurrentRegion?.GetZone((int)approach.X, (int)approach.Y);
                    var nav = PathfindingProvider.Instance;
                    if (zone != null && nav is { IsAvailable: true } && nav.HasNavmesh(zone) &&
                        nav.GetClosestPoint(zone, approach, 48, 48, 96, nav.BlockingDoorAvoidanceFilters) is { } floor &&
                        Vector3.DistanceSquared(floor, new(gate.X, gate.Y, gate.Z)) <=
                            BattlegroundDoorMeleeReach(gate) * BattlegroundDoorMeleeReach(gate) &&
                        !IsPastBattlegroundGate(central, gate, floor))
                        _battlegroundDoorApproach = floor;
                }
                approach = _battlegroundDoorApproach;
            }

            if (!IssuePath(bot, approach, retargetWhileMoving: true))
                return false;
            if (central.Guards.Values.FirstOrDefault(guard => guard is GuardLord && guard.IsAlive) is { } lord)
                TraceStorm(this, bot, central, lord, gate, "closed-gate");
            SetBattlegroundStatus(bot, bracket, "Marching on the keep gate", central.Name, gate.Name);
            return true;
        }

        private static Vector3 BattlegroundBreachPoint(AbstractGameKeep keep, GameKeepDoor door) =>
            BattlegroundBreachPoint(new(keep.X, keep.Y), new(door.X, door.Y, door.Z), door.Heading);

        public static Vector3 BattlegroundBreachPoint(Vector2 keepCentre, Vector3 gate, ushort heading)
        {
            Vector2 inside = KeepRaidDoors.StandOff(new(gate.X, gate.Y), heading, keepCentre, -180);
            return new(inside.X, inside.Y, gate.Z);
        }

        private static bool IsPastBattlegroundGate(AbstractGameKeep keep, GameKeepDoor door, Vector3 point)
        {
            Vector2 entry = new(door.X, door.Y);
            Vector3 beyond = BattlegroundBreachPoint(keep, door);
            return Vector2.Dot(new Vector2(beyond.X, beyond.Y) - entry, new Vector2(point.X, point.Y) - entry) > 0;
        }

        /// <summary>
        /// Teleporter trip into the bracket's battleground (walking to a realm teleporter first when needed). True 1.65
        /// Mode (owner 2026-11-12): as a player, the bot buys the battlegrounds medallion from the medallion merchant
        /// beside its portal keep teleporter and boards the teleporter's own departure (AutonomousFrontierTransport, the
        /// same boarding the frontier medallions use). A bot holding its medallion whose boarding has not departed within
        /// <see cref="AutonomousBattlegroundExit.BoardingMilliseconds"/> enters directly from beside the teleporter, spending
        /// the medallion (AUTONOMOUS_BATTLEGROUND_ENTRY_FALLBACK); a bot that cannot get one (no merchant, cannot afford it,
        /// no room in time) does not go in (AUTONOMOUS_BATTLEGROUND_NO_MEDALLION) and is kept away for 30 minutes.
        /// </summary>
        private bool TravelToBattleground(GameBot bot, BattlegroundBrackets.Bracket bracket, Vector3 goal)
        {
            if (BattlegroundBrackets.IsBattlegroundRegion(bot.CurrentRegionID))
                return LeaveBattleground(bot);
            if (!BattlegroundBrackets.Allows(bracket, bot.Level)) return false;
            bool pvpTour = AutonomousObjectiveAssignments.Is(bot, eAutonomousObjectiveKind.Battleground);
            // Taken out of a battleground with no progress: not back in for a while (never an entry/exit loop).
            if (AutonomousBattlegroundExit.ReentryBlocked(bot.DatabaseID, GameLoop.GameLoopTime))
            {
                ResetBattlegroundBoarding(bot);
                if (pvpTour)
                    AutonomousObjectiveAssignments.EndBattlegroundTour(bot, "Recently taken out of a battleground with no progress; not going back yet");
                else
                    AbandonCamp(bot, $"Recently taken out of a battleground with no progress; not returning to {bracket.Name} yet");
                return true;
            }
            if (TryTownTeleport(bot, bracket.RegionId, goal, BattlegroundNoWalkSeconds)) return true;

            string goalText = pvpTour ? $"Battleground: {bracket.Name}" : GoalText();
            GameNPC porter = NearestRealmTeleporter(bot);
            if (porter == null)
            {
                if (pvpTour)
                    AutonomousObjectiveAssignments.EndBattlegroundTour(bot, "No realm teleporter could be reached for the battlegrounds");
                else
                    AbandonCamp(bot, $"No realm teleporter could be reached for {bracket.Name}");
                return true;
            }
            if (porter is DOL.GS.Scripts.OFTeleporter keepPorter && bot.CurrentRegionID == porter.CurrentRegionID &&
                bot.IsWithinRadius(porter, BattlegroundBoardingReach))
                return BoardBattlegroundPorter(bot, keepPorter, bracket, goalText);
            SetStatus(bot, $"Heading to {porter.Name}", goalText,
                $"Walking to the teleporter for {bracket.Name}", porter.Name, porter.CurrentZone?.Description ?? string.Empty);
            if (bot.CurrentRegionID == porter.CurrentRegionID)
                return IssuePath(bot, AutonomousTownTeleporters.StandOff(porter, bot));
            CampDestination previous = _camp;
            _camp = new("battleground-teleporter", porter.Name, porter.CurrentZone?.Description ?? string.Empty,
                porter.CurrentRegionID, porter.X, porter.Y, porter.Z, 1, false, false);
            try { return TravelAcrossRegions(bot); }
            finally { _camp = previous; }
        }

        /// <summary>Within this of its portal keep teleporter a bot buys the medallion and boards (the merchant stands within 3,000).</summary>
        public const int BattlegroundBoardingReach = 3_000;
        // Owner 2026-11-12: in and out of the battlegrounds without getting stuck (AutonomousBattlegroundExit).
        private long _battlegroundBoardingSince, _battlegroundBoardingLastTick;
        private GameMerchant _battlegroundMedallionMerchant;
        private Vector3? _battlegroundWaitingPoint;
        private long _battlegroundLeavingSince;
        private long _battlegroundProgressTick, _battlegroundProgressXp, _battlegroundProgressRp;
        private Vector3 _battlegroundProgressSpot;
        private ushort _battlegroundProgressRegion;

        private bool BoardBattlegroundPorter(GameBot bot, DOL.GS.Scripts.OFTeleporter porter, BattlegroundBrackets.Bracket bracket, string goal)
        {
            long now = GameLoop.GameLoopTime;
            AutonomousFrontierTransport.Passage passage = AutonomousFrontierTransport.BattlegroundPassage(bot.Realm, bracket);
            if (passage?.Location == null) return GiveUpBattlegroundEntry(bot, bracket, "no-passage");
            // A boarding not worked for 30 s (the bot went elsewhere) starts its clock again.
            if (_battlegroundBoardingSince == 0 || now - _battlegroundBoardingLastTick > 30_000)
            {
                _battlegroundBoardingSince = now;
                _battlegroundWaitingPoint = null;
            }
            _battlegroundBoardingLastTick = now;
            bot.TempProperties.SetProperty(AutonomousFrontierTransport.BattlegroundBoardingKey, now);
            // The direct way in is only for a bot that holds a medallion (it is spent there, as the port spends it): a
            // bot that never got one in the boarding time does not go in at all.
            if (AutonomousBattlegroundExit.BoardingTimedOut(_battlegroundBoardingSince, now) && !bot.InCombat)
                return AutonomousFrontierTransport.Ticket(bot, passage) != null
                    ? EnterBattlegroundDirectly(bot, porter, bracket, goal, "boarding-timeout")
                    : GiveUpBattlegroundEntry(bot, bracket, "no-medallion-in-time");
            string force = bot.TempProperties.GetProperty<string>("RvrEventForce") ?? $"rvr-{bot.DatabaseID}";
            bot.TempProperties.SetProperty(AutonomousFrontierTransport.RequestKey, new AutonomousFrontierTransport.Request(porter, passage, force));
            if (AutonomousFrontierTransport.Ticket(bot, passage) == null)
            {
                if (_battlegroundMedallionMerchant?.ObjectState != GameObject.eObjectState.Active ||
                    _battlegroundMedallionMerchant.CurrentRegion != porter.CurrentRegion)
                    _battlegroundMedallionMerchant = AutonomousFrontierTransport.MedallionMerchant(porter, bot.Realm, passage.Medallion);
                GameMerchant merchant = _battlegroundMedallionMerchant;
                if (merchant == null)
                    return GiveUpBattlegroundEntry(bot, bracket, "no-medallion-merchant");
                if (!ApproachSupplyMerchant(bot, merchant))
                {
                    SetStatus(bot, "Buying a battlegrounds medallion", goal, $"Walking to {merchant.Name} for the {bracket.Name} medallion",
                        merchant.Name, merchant.CurrentZone?.Description ?? string.Empty);
                    return true;
                }
                switch (BuyBattlegroundMedallion(bot, merchant, passage.Medallion))
                {
                    case eMedallionPurchase.Bought:
                        break;
                    case eMedallionPurchase.NeedsRoom:
                        SetStatus(bot, "Making room for a battlegrounds medallion", goal, $"At {merchant.Name}", merchant.Name,
                            merchant.CurrentZone?.Description ?? string.Empty);
                        return true;
                    case eMedallionPurchase.CannotAfford:
                        return GiveUpBattlegroundEntry(bot, bracket, "cannot-afford-medallion");
                    default:
                        return GiveUpBattlegroundEntry(bot, bracket, "medallion-not-sold");
                }
            }
            if (!_battlegroundWaitingPoint.HasValue)
                _battlegroundWaitingPoint = AutonomousFrontierTransport.TryWaitingPoint(bot, porter, out Vector3 waiting)
                    ? waiting : AutonomousTownTeleporters.StandOff(porter, bot);
            if (Vector3.Distance(new(bot.X, bot.Y, bot.Z), _battlegroundWaitingPoint.Value) > 40)
                IssuePath(bot, _battlegroundWaitingPoint.Value);
            else
            {
                bot.StopMovingOnPath();
                bot.StopMoving();
            }
            AutonomousFrontierTransport.WakeBoardingPorter(bot, bot.TempProperties.GetProperty<AutonomousFrontierTransport.Request>(
                AutonomousFrontierTransport.RequestKey));
            SetStatus(bot, $"Boarding {porter.Name} for {bracket.Name}", goal, "Holding a battlegrounds medallion; waiting for the teleporter's departure",
                porter.Name, porter.CurrentZone?.Description ?? string.Empty);
            return true;
        }

        private enum eMedallionPurchase { Bought, NeedsRoom, CannotAfford, NotSold }

        /// <summary>Buys the medallion at its real merchant, paying its price (Price 0 in the period world; a price set later is
        /// paid from the bot's own coin, never free). A full bag sells one vendor-trash item first, as the frontier medallion
        /// purchase does; a full bag with nothing to sell waits (the boarding time then ends the attempt).</summary>
        private static eMedallionPurchase BuyBattlegroundMedallion(GameBot bot, GameMerchant merchant, string medallion)
        {
            DbItemTemplate template = merchant.TradeItems?.GetAllItems().Values.OfType<DbItemTemplate>().FirstOrDefault(item => item.Id_nb == medallion);
            if (template == null || template.Price < 0) return eMedallionPurchase.NotSold;
            if (template.Price > 0 && AutonomousBotEconomy.GetMoney(bot.DatabaseID) < template.Price) return eMedallionPurchase.CannotAfford;
            eInventorySlot slot = bot.Inventory.FindFirstEmptySlot(eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack);
            if (slot == eInventorySlot.Invalid)
            {
                DbInventoryItem trash = AutonomousBotEconomy.FindVendorTrashCandidate(bot);
                if (trash != null)
                    AutonomousBotEconomy.TrySellToVendor(bot, merchant, trash, out _);
                return eMedallionPurchase.NeedsRoom;
            }
            if (template.Price > 0 && !AutonomousBotEconomy.TrySpend(bot.DatabaseID, template.Price, BotMoneyLedger165.Kind.Travel))
                return eMedallionPurchase.CannotAfford;
            DbInventoryItem ticket = GameInventoryItem.Create(template);
            if (ticket == null || !bot.Inventory.AddItem(slot, ticket))
            {
                if (template.Price > 0) AutonomousBotEconomy.AddMoney(bot.DatabaseID, template.Price, BotMoneyLedger165.Kind.Travel);
                return eMedallionPurchase.NeedsRoom;
            }
            AutonomousBotEconomy.MarkInventoryChanged(bot);
            AutonomousBotStatusPersistence.Queue(bot, true);
            return eMedallionPurchase.Bought;
        }

        /// <summary>No medallion, no entry: a grinder drops the battleground camp (it plans another), a PvP tour ends with a
        /// non-battleground goal. Never a free way in.</summary>
        private bool GiveUpBattlegroundEntry(GameBot bot, BattlegroundBrackets.Bracket bracket, string reason)
        {
            ResetBattlegroundBoarding(bot);
            // not straight back to the same teleporter for another failed try (no entry loop)
            AutonomousBattlegroundExit.BlockReentry(bot.DatabaseID, GameLoop.GameLoopTime);
            Log.Info($"AUTONOMOUS_BATTLEGROUND_NO_MEDALLION bot={bot.Name} id={bot.DatabaseID} realm={GlobalConstants.RealmToName(bot.Realm)} " +
                     $"level={bot.Level} battleground={bracket.Name} reason={reason} goal={AutonomousObjectiveAssignments.KindFor(bot)}");
            if (AutonomousObjectiveAssignments.Is(bot, eAutonomousObjectiveKind.Battleground))
                AutonomousObjectiveAssignments.EndBattlegroundTour(bot, $"Could not get a battlegrounds medallion for {bracket.Name} ({reason})");
            else
                AbandonCamp(bot, $"Could not get a battlegrounds medallion for {bracket.Name} ({reason})");
            _nextPlanTick = 0;
            return true;
        }

        /// <summary>The guaranteed way in for a bot HOLDING a medallion whose boarding never departed: from beside the portal
        /// keep teleporter straight to the realm's field arrival, spending the medallion exactly as the port would.</summary>
        private bool EnterBattlegroundDirectly(GameBot bot, GameNPC porter, BattlegroundBrackets.Bracket bracket, string goal, string reason)
        {
            if (!bot.IsWithinRadius(porter, 350) || bot.InCombat)
            {
                IssuePath(bot, AutonomousTownTeleporters.StandOff(porter, bot));
                SetStatus(bot, $"Heading to {porter.Name}", goal, $"Walking to the teleporter for {bracket.Name}", porter.Name,
                    porter.CurrentZone?.Description ?? string.Empty);
                return true;
            }
            GameLocation arrival = BattlegroundBrackets.BotArrival(bracket, bot.Realm);
            bot.StopMovingOnPath();
            bot.StopMoving();
            DbInventoryItem ticket = AutonomousFrontierTransport.Ticket(bot, AutonomousFrontierTransport.BattlegroundPassage(bot.Realm, bracket));
            if (ticket == null)
                return GiveUpBattlegroundEntry(bot, bracket, "no-medallion");
            if (arrival == null || !bot.MoveTo(bracket.RegionId, arrival.X, arrival.Y, arrival.Z, arrival.Heading))
                return false;
            bot.Inventory.RemoveItem(ticket);
            AutonomousBotEconomy.MarkInventoryChanged(bot);
            ResetBattlegroundBoarding(bot);
            Log.Info($"AUTONOMOUS_BATTLEGROUND_ENTRY_FALLBACK bot={bot.Name} id={bot.DatabaseID} realm={GlobalConstants.RealmToName(bot.Realm)} " +
                     $"level={bot.Level} battleground={bracket.Name} reason={reason} porter=\"{porter.Name}\"");
            SetStatus(bot, $"Entering {bracket.Name}", goal, $"Took {porter.Name}'s portal", porter.Name, porter.CurrentZone?.Description ?? string.Empty);
            bot.ForcePathReplot();
            AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Movement);
            AutonomousBotStatusPersistence.Queue(bot, true);
            ResetRouteOrderState();
            return true;
        }

        private void ResetBattlegroundBoarding(GameBot bot)
        {
            _battlegroundBoardingSince = _battlegroundBoardingLastTick = 0;
            _battlegroundWaitingPoint = null;
            if (bot.TempProperties.GetProperty<AutonomousFrontierTransport.Request>(AutonomousFrontierTransport.RequestKey) is { } request &&
                AutonomousFrontierTransport.IsBattlegroundPassage(request.Passage))
                bot.TempProperties.RemoveProperty(AutonomousFrontierTransport.RequestKey);
            bot.TempProperties.RemoveProperty(AutonomousFrontierTransport.BattlegroundBoardingKey);
        }

        /// <summary>The bot's own-realm teleporter: one in this region first, otherwise one in the realm's capital.</summary>
        private static GameNPC NearestRealmTeleporter(GameBot bot)
        {
            // True 1.65 Mode: the freeshard town teleporters are gone; the realm's portal keep teleporter (Master Visur,
            // Stor Gothi, Glasny) takes medallion holders to the battlegrounds, the period way in
            if (Classic165Mode.IsOn)
            {
                // one of its own in the bot's region, otherwise its realm's own portal keep (Castle Sauvage, Svasud
                // Faste, Druim Ligen): never one at the realm's arrival point in an enemy frontier, which the bot can't
                // walk to (an Albion bot in Camelot picked Master Visur in Hibernia's frontier)
                ushort home = bot.Realm switch { eRealm.Albion => 1, eRealm.Midgard => 100, eRealm.Hibernia => 200, _ => (ushort)0 };
                GameNPC keepPorter = PortalKeepTeleporters(bot.Realm)
                    .Where(porter => porter.CurrentRegionID == bot.CurrentRegionID || porter.CurrentRegionID == home)
                    .OrderBy(porter => porter.CurrentRegionID == bot.CurrentRegionID ? 0 : 1)
                    .ThenBy(porter => Vector2.DistanceSquared(new(bot.X, bot.Y), new(porter.X, porter.Y)))
                    .FirstOrDefault();
                if (keepPorter != null) return keepPorter;
            }
            GameNPC local = AutonomousTownTeleporters.Porters(bot.CurrentRegion)
                .Where(porter => porter.Realm == bot.Realm)
                .OrderBy(porter => Vector2.DistanceSquared(new(bot.X, bot.Y), new(porter.X, porter.Y)))
                .FirstOrDefault();
            if (local != null) return local;
            ushort capital = bot.Realm switch { eRealm.Albion => 10, eRealm.Midgard => 101, eRealm.Hibernia => 201, _ => (ushort)0 };
            return capital == 0 ? null : AutonomousTownTeleporters.Porters(WorldMgr.GetRegion(capital))
                .FirstOrDefault(porter => porter.Realm == bot.Realm);
        }

        private static GameNPC[] _portalKeepTeleporters;

        /// <summary>The portal keep teleporters (OFTeleporter) of a realm, looked up once.</summary>
        private static IEnumerable<GameNPC> PortalKeepTeleporters(eRealm realm)
        {
            _portalKeepTeleporters ??= WorldMgr.GetAllRegions()
                .SelectMany(region => region.Objects.OfType<DOL.GS.Scripts.OFTeleporter>())
                .Where(npc => npc.ObjectState == GameObject.eObjectState.Active)
                .Cast<GameNPC>().ToArray();
            return _portalKeepTeleporters.Where(npc => npc.Realm == realm && npc.ObjectState == GameObject.eObjectState.Active);
        }

        /// <summary>
        /// Owner 2026-11-12 ("make sure the gamebots can get in and out of the battlegrounds after a grind task and dont get
        /// stuck forever"): every think of a bot inside a battleground, before its goal runs. A bot leaves when its level
        /// no longer fits (also right after logging in above the bracket), when it is between tasks (selling, repairs,
        /// training, healer, town break), when it has an RvR goal or a party whose business is elsewhere, or when its next
        /// camp is elsewhere; battlegrounds have no zone connections, so the ordinary route code finds no way out. A bot
        /// with no progress inside for 20 minutes is taken out to its border keep (ExitBattlegroundNow).
        /// </summary>
        private bool LeaveBattlegroundWhenDone(GameBot bot)
        {
            ushort region = bot.CurrentRegionID;
            long now = GameLoop.GameLoopTime;
            if (!BattlegroundBrackets.IsBattlegroundRegion(region))
            {
                NoteBattlegroundDeathOutside(bot, now);
                _battlegroundLeavingSince = 0;
                _battlegroundProgressTick = 0;
                // a retreat cut short by death/release must not carry its clock into the next visit
                _portalKeepRetreatSince = 0;
                return false;
            }
            if (!bot.IsAlive || bot.IsTemporaryGroupHelper || bot.IsPlayerLedGroup || HiredCompanions165.IsHired(bot))
                return false;
            _battlegroundLastInsideTick = now;
            BattlegroundBrackets.Bracket here = BattlegroundBrackets.ForRegion(region);
            bool pvpTour = AutonomousObjectiveAssignments.Is(bot, eAutonomousObjectiveKind.Battleground);
            if (_battlegroundProgressTick == 0)
            {
                ResetBattlegroundBoarding(bot);
                Log.Info($"AUTONOMOUS_BATTLEGROUND_ARRIVAL bot={bot.Name} id={bot.DatabaseID} realm={GlobalConstants.RealmToName(bot.Realm)} " +
                         $"level={bot.Level} battleground={here?.Name} goal={AutonomousObjectiveAssignments.KindFor(bot)} camp=\"{_camp?.MonsterName}\"");
            }
            bool inParty = _groupDirective?.IsDynamic == true;
            eBattlegroundStay stay = AutonomousBattlegroundExit.Decide(pvpTour,
                BattlegroundBrackets.Allows(here, bot.Level),
                AutonomousObjectiveAssignments.IsBetweenPveTasks(bot),
                AutonomousObjectiveAssignments.Is(bot, eAutonomousObjectiveKind.RvR),
                inParty, inParty && AutonomousBattlegroundExit.PartyBusinessHere(_groupDirective.Camp != null,
                    _groupDirective.Camp?.RegionId == region,
                    AutonomousBotGroupCoordinator.IsAssemblyPhase(_groupDirective.Phase) || _groupDirective.Phase == "Regrouping",
                    _groupDirective.RendezvousRegion == region),
                _camp != null, _camp?.RegionId == region,
                AutonomousBattlegroundExit.IsStuck(TrackBattlegroundProgress(bot, now, pvpTour), now));
            if (stay == eBattlegroundStay.Stay)
            {
                _battlegroundLeavingSince = 0;
                // Inside another realm's portal keep danger radius (its period guards shoot on sight): walk back out.
                return RetreatFromEnemyPortalKeep(bot);
            }
            if (stay == eBattlegroundStay.Stuck)
            {
                _camp = null;
                _nextPlanTick = 0;
                ResetRouteOrderState();
                return ExitBattlegroundNow(bot, "no XP, realm points or progress inside for twenty minutes", "AUTONOMOUS_BATTLEGROUND_STUCK_EXIT");
            }
            if (_camp?.RegionId == region)
            {
                // Out-levelled or called away: the battleground camp is over (no return to it after leaving).
                _camp = null;
                _nextPlanTick = 0;
                if (bot.PersistentRecord != null) bot.PersistentRecord.CurrentCampId = string.Empty;
            }
            if (_battlegroundLeavingSince == 0)
                Log.Info($"AUTONOMOUS_BATTLEGROUND_LEAVING bot={bot.Name} id={bot.DatabaseID} realm={GlobalConstants.RealmToName(bot.Realm)} " +
                         $"level={bot.Level} battleground={here?.Name} reason={stay} goal={AutonomousObjectiveAssignments.KindFor(bot)}");
            return LeaveBattleground(bot);
        }

        private long _battlegroundLastInsideTick, _battlegroundLastCountedDeath;
        private int _battlegroundDeathsWithoutGain;

        /// <summary>
        /// Outside a battleground: a death that happened inside it (the bot was last seen alive there within 15 s of
        /// dying, then released to its bind) is a strike; <see cref="AutonomousBattlegroundExit.DeathStrikeLimit"/> strikes
        /// with no XP or realm points gained in between block re-entry for a while (a bot dying to portal keep guards, or
        /// anything else, over and over is not making progress; the release and the walk back in are not progress).
        /// </summary>
        private void NoteBattlegroundDeathOutside(GameBot bot, long now)
        {
            long death = bot.LastDeathTick165;
            if (death <= 0 || death == _battlegroundLastCountedDeath || _battlegroundLastInsideTick == 0 ||
                death < _battlegroundLastInsideTick || death - _battlegroundLastInsideTick > 15_000)
                return;
            _battlegroundLastCountedDeath = death;
            _battlegroundDeathsWithoutGain++;
            if (!AutonomousBattlegroundExit.DeathStrikesBlock(_battlegroundDeathsWithoutGain)) return;
            _battlegroundDeathsWithoutGain = 0;
            AutonomousBattlegroundExit.BlockReentry(bot.DatabaseID, now);
            Log.Warn($"AUTONOMOUS_BATTLEGROUND_DEATH_BLOCK bot={bot.Name} id={bot.DatabaseID} realm={GlobalConstants.RealmToName(bot.Realm)} " +
                     $"level={bot.Level} deaths={AutonomousBattlegroundExit.DeathStrikeLimit} goal={AutonomousObjectiveAssignments.KindFor(bot)} " +
                     $"reentryBlockedMinutes={AutonomousBattlegroundExit.ReentryBlockMilliseconds / 60_000}");
        }

        /// <summary>The tick of the bot's last progress inside the battleground (AutonomousBattlegroundExit.IsProgress: XP or
        /// realm points gained; for a PvP-focus bot also moving <see cref="AutonomousBattlegroundExit.ProgressMoveDistance"/>).</summary>
        private long TrackBattlegroundProgress(GameBot bot, long now, bool pvpTour)
        {
            Vector3 spot = new(bot.X, bot.Y, bot.Z);
            bool movedFar = Vector3.DistanceSquared(spot, _battlegroundProgressSpot) >
                (float)AutonomousBattlegroundExit.ProgressMoveDistance * AutonomousBattlegroundExit.ProgressMoveDistance;
            bool gained = bot.Experience > _battlegroundProgressXp || bot.AutonomousRealmPoints > _battlegroundProgressRp;
            if (gained) _battlegroundDeathsWithoutGain = 0;
            if (_battlegroundProgressTick == 0 || _battlegroundProgressRegion != bot.CurrentRegionID ||
                AutonomousBattlegroundExit.IsProgress(bot.Experience, _battlegroundProgressXp, bot.AutonomousRealmPoints,
                    _battlegroundProgressRp, movedFar, pvpTour))
                MarkBattlegroundProgress(bot, now);
            else
            {
                // a death's XP loss lowers the mark without counting as progress
                _battlegroundProgressXp = Math.Min(_battlegroundProgressXp, bot.Experience);
                _battlegroundProgressRp = Math.Min(_battlegroundProgressRp, bot.AutonomousRealmPoints);
            }
            return _battlegroundProgressTick;
        }

        /// <summary>Goal progress inside a battleground (also a keep holder standing at its post, which is its job).</summary>
        private void MarkBattlegroundProgress(GameBot bot, long now)
        {
            _battlegroundProgressTick = now;
            _battlegroundProgressRegion = bot.CurrentRegionID;
            _battlegroundProgressXp = bot.Experience;
            _battlegroundProgressRp = bot.AutonomousRealmPoints;
            _battlegroundProgressSpot = new(bot.X, bot.Y, bot.Z);
        }

        /// <summary>
        /// A bot inside another realm's portal keep danger radius walks straight back out of it (lane BGCONTENT restored
        /// the period portal keep guards, level 90-95, about 2,000 aggro). True while it is walking out.
        /// </summary>
        private long _portalKeepRetreatSince;

        private bool RetreatFromEnemyPortalKeep(GameBot bot)
        {
            if (AutonomousBattlegroundPortalKeeps.EnemyKeepNear(bot.Realm, bot.X, bot.Y, AutonomousBattlegroundPortalKeeps.AvoidRadius,
                    out Vector2 keep) == eRealm.None)
            {
                _portalKeepRetreatSince = 0;
                return false;
            }
            // Review 2026-11-12: a bot that cannot walk out (a pocket, a fight it keeps picking up) is handed to the exit
            // chain after two minutes, so the retreat never holds it under the guards and never hides it from the backstop.
            long now = GameLoop.GameLoopTime;
            if (_portalKeepRetreatSince == 0) _portalKeepRetreatSince = now;
            if (AutonomousBattlegroundExit.RetreatFailed(_portalKeepRetreatSince, now))
            {
                _portalKeepRetreatSince = 0;
                _camp = null;
                _nextPlanTick = 0;
                ResetRouteOrderState();
                if (ExitBattlegroundNow(bot, "could not walk out of an enemy portal keep's guard range in two minutes",
                        "AUTONOMOUS_BATTLEGROUND_PORTAL_KEEP_EXIT"))
                    return true;
            }
            Vector2 away = AutonomousBattlegroundPortalKeeps.RetreatPoint(keep, new(bot.X, bot.Y));
            Vector3 goal = new(away.X, away.Y, bot.Z);
            IPathfindingMgr nav = PathfindingProvider.Instance;
            Zone zone = bot.CurrentRegion?.GetZone((int)goal.X, (int)goal.Y) ?? bot.CurrentZone;
            if (zone != null && nav.IsAvailable && nav.HasNavmesh(zone))
                goal = nav.GetClosestPoint(zone, goal, 400, 400, 6000, nav.DefaultFilters) ?? goal;
            // In a fight with an enemy outside the danger radius the fight itself brings the bot back out; an enemy (or
            // a guard) inside it is dropped: no chase under the guards.
            if (bot.TargetObject is GameLiving target && target.IsAlive && (bot.InCombat || bot.IsAttacking) &&
                !AutonomousBattlegroundPortalKeeps.InEnemyDanger(bot.Realm, target.CurrentRegionID, target.X, target.Y))
                return false;
            if (bot.Brain is BotBrain brain)
            {
                bot.StopAttack();
                brain.ClearAggroList();
                bot.TargetObject = null;
                brain.FSM.SetCurrentState(eFSMStateType.IDLE);
            }
            IssuePath(bot, goal, retargetWhileMoving: true);
            SetStatus(bot, "Keeping clear of an enemy portal keep", GoalText(), "Its guards shoot on sight; walking back out");
            return true;
        }

        /// <summary>
        /// The players' way out: walk back to the realm's exit spot (its field arrival point; the exit portal is there) and
        /// take the exit to the realm's border keep (the same destinations KeepManager.ExitBattleground uses). The fallback
        /// chain (AutonomousBattlegroundExit.ExitStep): a walk that has not arrived in 4 minutes, or any exit still not
        /// done after 10, takes the exit from where the bot stands; with no Teleport row the realm's home portal keep spot
        /// is used. False only while the bot finishes a fight at the exit, or when no exit exists at all.
        /// </summary>
        private bool LeaveBattleground(GameBot bot)
        {
            BattlegroundBrackets.Bracket here = BattlegroundBrackets.ForRegion(bot.CurrentRegionID);
            if (here == null) return false;
            long now = GameLoop.GameLoopTime;
            if (_battlegroundLeavingSince == 0) _battlegroundLeavingSince = now;
            GameLocation entry = BattlegroundBrackets.BotArrival(here, bot.Realm);
            if (entry == null) return false;
            Vector3 exitSpot = AutonomousTownTeleporters.Arrival(new DbTeleport { RegionID = here.RegionId, X = entry.X, Y = entry.Y, Z = entry.Z }, 0);
            float distance = Vector2.Distance(new(bot.X, bot.Y), new(exitSpot.X, exitSpot.Y));
            eBattlegroundExitStep step = AutonomousBattlegroundExit.ExitStep(distance, bot.InCombat, now - _battlegroundLeavingSince);
            if (step == eBattlegroundExitStep.WalkToExit)
            {
                IssuePath(bot, exitSpot);
                SetStatus(bot, "Leaving the battleground", GoalText(), $"Walking to the {here.Name} exit portal");
                return true;
            }
            if (step == eBattlegroundExitStep.FinishFight) return false;
            if (!TryBorderKeepExit(bot.Realm, out ushort region, out Vector3 arrival, out ushort heading, out string destination))
                return false;
            StopForTransfer(bot);
            if (!bot.MoveTo(region, (int)arrival.X, (int)arrival.Y, (int)arrival.Z, heading))
                return false;
            Log.Info($"AUTONOMOUS_BATTLEGROUND_EXIT bot={bot.Name} id={bot.DatabaseID} realm={GlobalConstants.RealmToName(bot.Realm)} level={bot.Level} " +
                     $"battleground={here.Name} destination=\"{destination}\" fallback={step == eBattlegroundExitStep.ExitFromHere} " +
                     $"leavingSeconds={(now - _battlegroundLeavingSince) / 1000}");
            _battlegroundLeavingSince = 0;
            _battlegroundProgressTick = 0;
            bot.ForcePathReplot();
            AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Movement);
            AutonomousBotStatusPersistence.Queue(bot, true);
            ResetRouteOrderState();
            _nextPlanTick = 0;
            return true;
        }

        /// <summary>
        /// The hard backstop (also for the stuck watchdog in a battleground): takes the bot out to its realm's border keep
        /// at once, never to the capital vault; ends a PvP tour and its battleground camp, leaves its party, and blocks
        /// every battleground for it for <see cref="AutonomousBattlegroundExit.ReentryBlockMilliseconds"/>, so it cannot
        /// loop between the entry and the exit. False when the bot is not in a battleground or no exit could be used
        /// (the caller then keeps its own recovery).
        /// </summary>
        public static bool ExitBattlegroundNow(GameBot bot, string reason, string logMarker = "AUTONOMOUS_BATTLEGROUND_FORCED_EXIT")
        {
            if (bot == null || !BattlegroundBrackets.IsBattlegroundRegion(bot.CurrentRegionID)) return false;
            if (!TryBorderKeepExit(bot.Realm, out ushort region, out Vector3 arrival, out ushort heading, out string destination))
                return false;
            BattlegroundBrackets.Bracket here = BattlegroundBrackets.ForRegion(bot.CurrentRegionID);
            (int X, int Y, int Z) old = (bot.X, bot.Y, bot.Z);
            if (bot.Group != null && AutonomousRealmRaid.GetView(bot.Group) == null)
            {
                AutonomousBotGroupCoordinator.EndPvePartyBeforeIndividualRecovery(bot, reason);
                bot.Group?.RemoveMember(bot);
            }
            StopForTransfer(bot);
            if (!bot.MoveTo(region, (int)arrival.X, (int)arrival.Y, (int)arrival.Z, heading))
                return false;
            long now = GameLoop.GameLoopTime;
            AutonomousBattlegroundExit.BlockReentry(bot.DatabaseID, now);
            if (AutonomousObjectiveAssignments.Is(bot, eAutonomousObjectiveKind.Battleground))
                AutonomousObjectiveAssignments.EndBattlegroundTour(bot, $"Taken out of {here?.Name}: {reason}");
            if (bot.PersistentRecord is { } record)
            {
                record.CurrentCampId = record.TargetName = record.TravelDestination = string.Empty;
                record.ObjectivePhase = $"Taken out of {here?.Name} to {destination}: {reason}";
                bot.MarkAutonomousStateDirty();
            }
            Log.Warn($"{logMarker} bot={bot.Name} id={bot.DatabaseID} realm={GlobalConstants.RealmToName(bot.Realm)} level={bot.Level} " +
                     $"battleground={here?.Name} from={old.X},{old.Y},{old.Z} destination=\"{destination}\" reason=\"{reason}\" " +
                     $"reentryBlockedMinutes={AutonomousBattlegroundExit.ReentryBlockMilliseconds / 60_000}");
            bot.ForcePathReplot();
            AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Movement);
            AutonomousBotStatusPersistence.Queue(bot, true);
            return true;
        }

        private static void StopForTransfer(GameBot bot)
        {
            bot.StopAttack();
            bot.StopFollowing();
            bot.StopMovingOnPath();
            bot.StopMoving();
            bot.StopCurrentSpellcast();
            if (bot.Brain is BotBrain brain)
            {
                brain.ClearAggroList();
                brain.FSM.SetCurrentState(eFSMStateType.IDLE);
            }
        }

        /// <summary>The realm's battleground exit: its border keep Teleport row (Castle Sauvage, Svasud Faste, Druim Ligen),
        /// or failing that the realm's home portal keep spot (the Home medallion destination).</summary>
        private static bool TryBorderKeepExit(eRealm realm, out ushort region, out Vector3 arrival, out ushort heading, out string destination)
        {
            DbTeleport exit = BattlegroundExit(realm);
            if (exit != null && WorldMgr.GetRegion((ushort)exit.RegionID) != null)
            {
                region = (ushort)exit.RegionID;
                arrival = AutonomousTownTeleporters.Arrival(exit, 0);
                heading = (ushort)exit.Heading;
                destination = exit.TeleportID;
                return true;
            }
            ushort home = RealmRaidNeutralEvents.HomeRegion(realm);
            if (AutonomousFrontierTransport.Destination(realm, home)?.Location is { } fallback)
            {
                region = fallback.RegionID;
                arrival = new(fallback.X, fallback.Y, fallback.Z);
                heading = fallback.Heading;
                destination = fallback.Name;
                return true;
            }
            region = 0; arrival = default; heading = 0; destination = string.Empty;
            return false;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<eRealm, DbTeleport> BattlegroundExits = new();

        private static DbTeleport BattlegroundExit(eRealm realm)
        {
            string location = realm switch
            {
                eRealm.Albion => "Castle Sauvage",
                eRealm.Midgard => "Svasud Faste",
                eRealm.Hibernia => "Druim Ligen",
                _ => null,
            };
            if (location == null) return null;
            return BattlegroundExits.GetOrAdd(realm, _ =>
                DOLDB<DbTeleport>.SelectObject(DB.Column("TeleportID").IsEqualTo(location)));
        }

        private void SetBattlegroundStatus(GameBot bot, BattlegroundBrackets.Bracket bracket, string activity, string detail, string target = "")
        {
            if (bot.PersistentRecord != null)
                bot.PersistentRecord.ObjectivePhase = activity;
            SetStatus(bot, activity, $"Battleground: {bracket.Name} (levels {bracket.MinLevel}-{bracket.MaxLevel})", detail,
                target, bracket.Name);
        }
    }
}
