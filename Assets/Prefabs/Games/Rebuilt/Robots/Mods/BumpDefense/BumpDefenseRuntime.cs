using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Games.Rebuilt.GamePieceSystem;
using Games.Rebuilt.Robots;
using Games.Rebuilt.Scoring;
using MoSimCore.BaseClasses.GameManagement;
using MoSimCore.BaseClasses.GameManagement.TimerManagement;
using MoSimCore.Enums;
using RobotFramework;
using RobotFramework.Controllers.Drivetrain;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BumpDefenseMod
{
    /// <summary>
    /// Selectable splitscreen P2 robot: disable driver input and play bump D vs the opponent.
    /// </summary>
    public class HubBumpDefenseAutopilot : MonoBehaviour
    {
        public const int TeamNumber = 9000;
        public const string TeamName = "Bump Defense";

        [Header("Defense parameters")]
        [Tooltip("If enabled, the practice menu difficulty sliders override these fields.")]
        public bool UsePracticeMenuSettings;

        public DefenseLevel Level = DefenseLevel.Medium;
        public ShotContest Shot = ShotContest.Normal;

        [Range(0.5f, 1.25f)]
        public float DriveSpeed = 1f;

        [Tooltip("Stay inside the alliance zone instead of climbing onto the bump.")]
        public bool StayOffBump = true;

        [Tooltip("Only pin after real contact while the opponent is actually shooting.")]
        public bool RequireRealPin = true;

        public static bool IsDefenseRobot(RobotBase robot)
        {
            return robot != null && (robot.TeamNumber == TeamNumber || robot.GetComponent<HubBumpDefenseAutopilot>() != null);
        }

        public static void Attach(RobotBase robot)
        {
            if (robot == null) return;
            if (robot.GetComponent<HubBumpDefenseAutopilot>() != null) return;
            if (robot.GetComponent<HubBumpDefenseDriver>() != null) return;
            robot.gameObject.AddComponent<HubBumpDefenseAutopilot>();
        }

        private void Awake()
        {
            DisableDriverInput();
        }

        private System.Collections.IEnumerator Start()
        {
            DisableDriverInput();
            var self = GetComponent<RobotBase>();
            RobotBase opponent = null;
            for (var i = 0; i < 180 && opponent == null; i++)
            {
                yield return null;
                opponent = FindOpponent(self);
            }

            if (self == null || opponent == null)
            {
                Debug.LogWarning("[HubBumpDefense] Defense robot spawned but no opponent was found.");
                yield break;
            }

            StripPreloads(self);
            AddSolidIntakeColliders(self);
            AddSolidIntakeColliders(opponent);

            var driver = gameObject.AddComponent<HubBumpDefenseDriver>();
            driver.Configure(opponent, this);
        }

        private void DisableDriverInput()
        {
            var playerInput = GetComponent<PlayerInput>();
            if (playerInput != null)
            {
                if (playerInput.user.valid) playerInput.user.UnpairDevices();
                playerInput.DeactivateInput();
                playerInput.enabled = false;
            }

            TryCall(GetComponent<RobotBase>(), "SetGameplayInputEnabled", false);
        }

        private static void StripPreloads(RobotBase robot)
        {
            if (robot is not RebuiltRobotBase rebuilt || rebuilt.preloads == null) return;
            foreach (var preload in rebuilt.preloads)
            {
                if (preload != null) Destroy(preload);
            }
        }

        private static void AddSolidIntakeColliders(RobotBase robot)
        {
            if (robot == null) return;
            var robotLayer = LayerMask.NameToLayer("Robot");
            foreach (var intake in robot.GetComponentsInChildren<RebuiltGamePieceIntake>(true))
            {
                var boxes = intake.GetComponentsInChildren<BoxCollider>(true);
                var added = false;
                foreach (var box in boxes)
                {
                    if (box == null || box.name == "IntakeSolid") continue;
                    if (box.transform.Find("IntakeSolid") != null) continue;
                    added = true;
                    var go = new GameObject("IntakeSolid");
                    go.layer = robotLayer >= 0 ? robotLayer : box.gameObject.layer;
                    go.transform.SetParent(box.transform, false);
                    var solid = go.AddComponent<BoxCollider>();
                    solid.center = box.center;
                    solid.size = Vector3.Max(box.size, Vector3.one * 0.08f);
                    solid.isTrigger = false;
                }

                if (added) continue;
                var rend = intake.GetComponentInChildren<Renderer>();
                if (rend == null || intake.transform.Find("IntakeSolid") != null) continue;
                var fallback = new GameObject("IntakeSolid");
                fallback.layer = robotLayer >= 0 ? robotLayer : intake.gameObject.layer;
                fallback.transform.SetParent(intake.transform, false);
                var bounds = rend.bounds;
                var col = fallback.AddComponent<BoxCollider>();
                col.center = intake.transform.InverseTransformPoint(bounds.center);
                col.size = Vector3.Max(bounds.size, new Vector3(0.18f, 0.12f, 0.22f));
                col.isTrigger = false;
            }
        }

        private static void TryCall(object target, string methodName, params object[] args)
        {
            if (target == null) return;
            var types = args.Select(arg => arg.GetType()).ToArray();
            var method = target.GetType().GetMethod(methodName, types);
            method?.Invoke(target, args);
        }

        private static RobotBase FindOpponent(RobotBase self)
        {
            if (self == null) return null;
            var robots = FindObjectsByType<RobotBase>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            return robots.FirstOrDefault(candidate =>
                candidate != null && candidate != self && !IsDefenseRobot(candidate));
        }
    }



    [DefaultExecutionOrder(-50)]
    public class HubBumpDefenseDriver : MonoBehaviour
    {
        private enum Mode
        {
            Cross,
            Screen,
            Wedge,
            ShotCharge,
            ShotHold,
            ShotRetreat,
            Collect,
            Dump
        }

        private enum Lane
        {
            TrenchMinus,
            Bump,
            TrenchPlus
        }

        private struct Disc
        {
            public Vector3 Xz;
            public float Radius;
        }

        private const float BumpX = 3.632f;
        private const float AllianceX = 4.25f;
        private const float TrenchZ = 2.7f;
        private const float FieldHalfZ = 4.1f;
        private const float ScreenHoldX = 5.5f;
        private const float HubX = 6.2f;
        private float _pinSeconds = 3f;
        private float _retreatSeconds = 3f;
        private float _speedScale = 1f;
        private float _ramScale = 1f;
        private float _wedgeSitSeconds = 4f;
        private float _wedgeLockoutSeconds = 1.6f;
        private float _shootSpeedThreshold = 1.15f;
        private ShotContest _shotContest = ShotContest.Normal;
        private int _settingsRevision = -1;
        private bool _stayOffBump = true;
        private bool _requireRealPin = true;
        private HubBumpDefenseAutopilot _settings;
        private const float WedgeInset = 0.55f;
        private const float OffAngleDegrees = 28f;
        private const int HistoryLength = 24;

        private RobotBase _player;
        private RebuiltRobotBase _playerRebuilt;
        private RebuiltRobotBase _selfRebuilt;
        private Rigidbody _playerBody;
        private DriveController _drive;
        private Rigidbody _rb;
        private RebuiltRobotGamePieceController _pieces;

        private float _playerSign;
        private Mode _mode = Mode.Cross;
        private Lane _lane = Lane.Bump;
        private float _commitUntil;
        private float _overshootZ;
        private float _hesitateUntil;
        private float _holdUntil;
        private float _retreatUntil;
        private bool _holdStarted;
        private bool _didPin;
        private float _pinContactSeconds;
        private bool _wedgeSitting;
        private float _wedgeSince;
        private float _wedgeLockoutUntil;
        private Vector3 _retreatTarget;
        private Vector3 _chargeOffset;
        private Vector3 _collectTarget;

        private readonly Vector3[] _histPos = new Vector3[HistoryLength];
        private readonly Vector3[] _histVel = new Vector3[HistoryLength];
        private int _histIndex;
        private int _histCount;
        private Vector3 _lastPlayerPos;
        private bool _hasLastPlayer;

        private float _reactionDelay;
        private float _commitSeconds;
        private float _lookahead;
        private float _biteChance;
        private float _fuelScanAt;
        private Vector3 _cachedFuel;
        private float _stuckTime;
        private readonly List<Disc> _posts = new();

        public void Configure(RobotBase player, HubBumpDefenseAutopilot settings = null)
        {
            _settings = settings != null ? settings : GetComponent<HubBumpDefenseAutopilot>();
            _player = player;
            _playerRebuilt = player as RebuiltRobotBase;
            _selfRebuilt = GetComponent<RebuiltRobotBase>();
            _playerBody = player != null ? player.GetComponent<Rigidbody>() : null;
            _drive = GetComponent<DriveController>();
            _rb = GetComponent<Rigidbody>();
            _pieces = GetComponent<RebuiltRobotGamePieceController>();
            _playerSign = player != null && player.Alliance == Alliance.Blue ? 1f : -1f;
            _mode = Mode.Cross;
            _lane = Lane.Bump;
            _didPin = false;
            _pinContactSeconds = 0f;
            _holdStarted = false;

            ApplyDifficulty(true);

            CachePosts();
            SilenceMechanisms();

            var timer = BaseGameManager.Instance != null
                ? BaseGameManager.Instance.GetComponent<BaseTimerManager>()
                : null;
            if (timer != null) timer.OnMatchStart += MarkCheated;
            MarkCheated();
        }

        private void OnDestroy()
        {
            var timer = BaseGameManager.Instance != null
                ? BaseGameManager.Instance.GetComponent<BaseTimerManager>()
                : null;
            if (timer != null) timer.OnMatchStart -= MarkCheated;
        }

        private static void MarkCheated()
        {
            if (BaseGameManager.Instance != null) BaseGameManager.Instance.wasCheated = true;
        }

        private void ApplyDifficulty(bool resample)
        {
            var useMenu = _settings == null || _settings.UsePracticeMenuSettings;
            var level = useMenu ? DefenseDifficulty.Level : _settings.Level;
            var shot = useMenu ? DefenseDifficulty.Shot : _settings.Shot;
            var speed = useMenu ? DefenseDifficulty.Speed : _settings.DriveSpeed;
            _stayOffBump = _settings == null || _settings.StayOffBump;
            _requireRealPin = _settings == null || _settings.RequireRealPin;

            if (useMenu)
            {
                if (!resample && _settingsRevision == DefenseDifficulty.Revision) return;
                _settingsRevision = DefenseDifficulty.Revision;
            }

            _speedScale = DefenseDifficulty.DriveScale(level, speed);
            _ramScale = DefenseDifficulty.RamScale(level, shot);
            _pinSeconds = DefenseDifficulty.PinSeconds(level);
            _retreatSeconds = DefenseDifficulty.RetreatSeconds(level);
            _wedgeSitSeconds = DefenseDifficulty.WedgeSitSeconds(level);
            _wedgeLockoutSeconds = DefenseDifficulty.WedgeLockoutSeconds(level);
            _shootSpeedThreshold = DefenseDifficulty.ShootSpeedThreshold(level, shot);
            _shotContest = shot;
            if (resample)
            {
                _reactionDelay = DefenseDifficulty.SampleReactionDelay(level);
                _commitSeconds = DefenseDifficulty.SampleCommitSeconds(level);
                _lookahead = DefenseDifficulty.SampleLookahead(level);
                _biteChance = DefenseDifficulty.SampleBiteChance(level);
            }
        }

        private void FixedUpdate()
        {
            if (_drive == null || _rb == null || _player == null) return;
            ApplyDifficulty(false);
            if (BaseGameManager.Instance == null ||
                BaseGameManager.Instance.RobotState != RobotState.Enabled)
            {
                SilenceMechanisms();
                return;
            }

            RecordPlayer();

            var gameState = BaseGameManager.Instance.GameState;
            if (gameState == GameState.Auto)
            {
                SilenceMechanisms();
                DriveToPose(PathAcrossField(0f, 0f), FaceToward(Vector3.zero));
                return;
            }

            if (gameState == GameState.End)
            {
                SilenceMechanisms();
                return;
            }

            TickTeleop();
        }

        private void TickTeleop()
        {
            var playerPos = PlayerPosition();
            var inZone = PlayerInTheirAllianceZone(playerPos);
            var onSide = InPlayerAlliance();
            var bumpThreat = ThreateningBump(playerPos) || PlayerCouldCrossBump(playerPos);
            var shooting = PlayerLikelyShooting(playerPos);
            var now = Time.time;
            var pastBump = PlayerPastBumpInZone(playerPos);
            var comingOver = !pastBump && (PlayerComingOverBump(playerPos) || PlayerOnBump(playerPos));

            if (_stayOffBump && StuckOnBump())
            {
                SetMechanisms(false, false);
                DriveToPose(EscapeBumpPose(), FaceToward(playerPos));
                return;
            }

            if (bumpThreat)
            {
                _lane = Lane.Bump;
                _commitUntil = now + 0.8f;
            }

            if (_mode is Mode.Collect or Mode.Dump)
            {
                _mode = Mode.Screen;
            }

            if (_mode is Mode.ShotCharge or Mode.ShotHold or Mode.ShotRetreat)
            {
                if (!inZone && _mode != Mode.ShotHold && _mode != Mode.ShotRetreat)
                {
                    _mode = comingOver ? Mode.Wedge : Mode.Screen;
                    _holdStarted = false;
                    _wedgeSitting = false;
                    if (_mode == Mode.Wedge) _wedgeSince = now;
                }
            }
            else if (comingOver && now >= _wedgeLockoutUntil)
            {
                if (_mode != Mode.Wedge)
                {
                    _wedgeSitting = false;
                    _wedgeSince = now;
                }

                _mode = Mode.Wedge;
                _lane = Lane.Bump;
            }
            else if (_mode == Mode.Wedge && (pastBump || PlayerClearOfBump(playerPos) || now - _wedgeSince > _wedgeSitSeconds))
            {
                EndWedge(shooting && inZone, playerPos);
            }
            else if (shooting && onSide && inZone && !comingOver &&
                     _shotContest != ShotContest.Off &&
                     _mode is not (Mode.ShotCharge or Mode.ShotHold or Mode.ShotRetreat))
            {
                BeginShotCharge(playerPos);
            }
            else if (_mode == Mode.Cross && onSide)
            {
                _mode = Mode.Screen;
            }
            else if (_mode is not (Mode.Cross or Mode.Screen or Mode.Wedge or Mode.ShotCharge or Mode.ShotHold
                     or Mode.ShotRetreat))
            {
                _mode = Mode.Screen;
            }

            if (TryUnstick(GatePose(_lane), FaceToward(playerPos)))
            {
                SetMechanisms(false, false);
                return;
            }

            switch (_mode)
            {
                case Mode.Cross:
                    SetMechanisms(false, false);
                    DriveToPose(PathAcrossField(ScreenX(), 0f), FaceToward(playerPos));
                    break;
                case Mode.Screen:
                    SetMechanisms(false, false);
                    TickScreen(playerPos);
                    break;
                case Mode.Wedge:
                    SetMechanisms(false, false);
                    TickWedge(playerPos);
                    break;
                case Mode.ShotCharge:
                    SetMechanisms(false, false);
                    TickShotCharge(playerPos);
                    break;
                case Mode.ShotHold:
                    SetMechanisms(false, false);
                    TickShotHold(playerPos);
                    break;
                case Mode.ShotRetreat:
                    SetMechanisms(false, false);
                    TickShotRetreat(playerPos);
                    break;
                case Mode.Collect:
                    TickCollect(playerPos);
                    break;
                case Mode.Dump:
                    TickDump(playerPos);
                    break;
            }
        }

        private void TickScreen(Vector3 playerPos)
        {
            UpdateLaneRead(playerPos);

            var hold = GatePose(_lane);
            if (_lane == Lane.Bump)
            {
                var followZ = playerPos.z;
                if (TryDelayedPlayer(out var seen, out _)) followZ = seen.z;
                hold.z = Mathf.Clamp(followZ, -2.15f, 2.15f);
            }

            hold.z = Mathf.Clamp(hold.z + _overshootZ, -FieldHalfZ + 0.55f, FieldHalfZ - 0.55f);
            hold.x = ScreenX();
            hold = KeepOffBump(ClearPosts(hold, playerPos));
            _overshootZ = Mathf.MoveTowards(_overshootZ, 0f, Time.fixedDeltaTime * 1.1f);

            if (Time.time < _hesitateUntil)
            {
                DriveToPose(_rb.position, FaceToward(playerPos));
                return;
            }

            var pose = InPlayerAlliance() ? PathTo(hold) : PathAcrossField(hold.x, hold.z);
            DriveToPose(pose, FaceToward(playerPos));
        }

        private void TickWedge(Vector3 playerPos)
        {
            var wedge = WedgePose(playerPos);
            var faceBump = Quaternion.LookRotation(new Vector3(-_playerSign, 0f, 0f), Vector3.up).eulerAngles.y;

            if (!_wedgeSitting && Horizontal(_rb.position, wedge) > 0.32f)
            {
                var pose = InPlayerAlliance() ? PathTo(wedge) : PathAcrossField(wedge.x, wedge.z);
                DriveToPose(pose, faceBump);
                return;
            }

            _wedgeSitting = true;
            ApplyDrive(Vector3.zero, faceBump);

            if (PlayerPastBumpInZone(playerPos) || PlayerClearOfBump(playerPos) ||
                Time.time - _wedgeSince > _wedgeSitSeconds)
            {
                EndWedge(false, playerPos);
            }
        }

        private void EndWedge(bool goShot, Vector3 playerPos)
        {
            _wedgeSitting = false;
            _wedgeLockoutUntil = Time.time + _wedgeLockoutSeconds;
            if (goShot)
            {
                BeginShotCharge(playerPos);
            }
            else
            {
                _mode = Mode.Screen;
            }
        }

        private void UpdateLaneRead(Vector3 livePos)
        {
            if (ThreateningBump(livePos))
            {
                _lane = Lane.Bump;
                return;
            }

            if (!TryDelayedPlayer(out var seenPos, out var seenVel))
            {
                seenPos = livePos;
                seenVel = Vector3.zero;
            }

            var liveVel = _playerBody != null ? _playerBody.velocity : Vector3.zero;
            var feint = _histCount > 8
                        && Mathf.Abs(liveVel.z) > 1.15f
                        && Mathf.Abs(seenVel.z) > 0.6f
                        && Mathf.Sign(liveVel.z) != Mathf.Sign(seenVel.z);

            var bump = ScoreLane(Lane.Bump, seenPos, seenVel) * 1.55f;
            var plus = ScoreLane(Lane.TrenchPlus, seenPos, seenVel);
            var minus = ScoreLane(Lane.TrenchMinus, seenPos, seenVel);

            var best = Lane.Bump;
            var bestScore = bump;
            if (plus > bestScore)
            {
                best = Lane.TrenchPlus;
                bestScore = plus;
            }

            if (minus > bestScore)
            {
                best = Lane.TrenchMinus;
                bestScore = minus;
            }

            var now = Time.time;
            var currentScore = _lane switch
            {
                Lane.TrenchPlus => plus,
                Lane.TrenchMinus => minus,
                _ => bump
            };

            var canSwitch = now >= _commitUntil && best != _lane && bestScore > currentScore + 0.55f;
            if (feint)
            {
                _commitUntil = now + _commitSeconds * UnityEngine.Random.Range(1.15f, 1.55f);
                if (UnityEngine.Random.value < _biteChance)
                {
                    var fakeZ = LaneZ(_lane);
                    _overshootZ = Mathf.Sign(fakeZ == 0f ? seenVel.z : fakeZ) * UnityEngine.Random.Range(0.65f, 1.35f);
                }
            }
            else if (canSwitch)
            {
                _lane = best;
                _commitUntil = now + _commitSeconds;
                _hesitateUntil = now + UnityEngine.Random.Range(0.10f, 0.22f);
                _overshootZ = 0f;
            }
        }

        private float ScoreLane(Lane lane, Vector3 pos, Vector3 vel)
        {
            var laneZ = LaneZ(lane);
            var predictedZ = pos.z + vel.z * _lookahead;
            var zErr = Mathf.Abs(predictedZ - laneZ);
            var towardLane = -Mathf.Sign(pos.z - laneZ + 0.001f) * vel.z;
            var screenX = ScreenX();
            var towardGate = -Mathf.Sign(pos.x - screenX) * vel.x;
            var inTrenchBand = Mathf.Abs(pos.z) > TrenchZ - 0.15f;
            var trenchBonus = 0f;
            if (lane != Lane.Bump && inTrenchBand && Mathf.Sign(pos.z) == Mathf.Sign(laneZ))
            {
                trenchBonus = 0.55f;
            }

            return 2.4f / (1f + zErr) + Mathf.Max(0f, towardLane) * 0.28f + Mathf.Max(0f, towardGate) * 0.18f +
                   trenchBonus;
        }

        private void BeginShotCharge(Vector3 playerPos)
        {
            _mode = Mode.ShotCharge;
            _holdStarted = false;
            _didPin = false;
            _pinContactSeconds = 0f;
            var toPlayer = playerPos - _rb.position;
            toPlayer.y = 0f;
            var side = Vector3.Cross(Vector3.up, PlayerForward());
            if (side.sqrMagnitude < 0.01f) side = Vector3.forward;
            side.Normalize();
            if (Vector3.Dot(toPlayer, side) < 0f) side = -side;
            _chargeOffset = side * UnityEngine.Random.Range(0.45f, 0.75f);
        }

        private void TickShotCharge(Vector3 playerPos)
        {
            var gap = Horizontal(_rb.position, playerPos);
            if (!PlayerInTheirAllianceZone(playerPos) || (!PlayerLikelyShooting(playerPos) && gap > 1.2f))
            {
                AbortShot();
                return;
            }

            var through = KeepOffBump(playerPos + _chargeOffset + (playerPos - _rb.position).normalized * 0.55f);
            through.y = _rb.position.y;
            DriveRam(PathTo(through), FaceSideOn(playerPos));

            if (!_requireRealPin && gap < 1.15f && PlayerOffAngle(playerPos))
            {
                _mode = Mode.ShotHold;
                _holdUntil = Time.time + _pinSeconds;
                _holdStarted = true;
                _didPin = true;
                return;
            }

            if (gap < 0.85f && PlayerLikelyShooting(playerPos))
            {
                _pinContactSeconds += Time.fixedDeltaTime;
                if (_pinContactSeconds >= 0.18f)
                {
                    _mode = Mode.ShotHold;
                    _holdUntil = Time.time + _pinSeconds;
                    _holdStarted = true;
                    _didPin = true;
                }
            }
            else
            {
                _pinContactSeconds = 0f;
            }
        }

        private void TickShotHold(Vector3 playerPos)
        {
            var gap = Horizontal(_rb.position, playerPos);
            if (!PlayerInTheirAllianceZone(playerPos) || !_didPin)
            {
                AbortShot();
                return;
            }

            if (!PlayerLikelyShooting(playerPos) && gap > 1.05f)
            {
                AbortShot();
                return;
            }

            if (gap > 1.2f)
            {
                DriveRam(PathTo(KeepOffBump(playerPos)), FaceSideOn(playerPos));
            }
            else
            {
                DriveRam(KeepOffBump(playerPos + _chargeOffset * 0.4f), FaceSideOn(playerPos), 0.55f);
            }

            if (_holdStarted && Time.time >= _holdUntil)
            {
                BeginRetreat(playerPos);
            }
        }

        private void AbortShot()
        {
            _holdStarted = false;
            _didPin = false;
            _pinContactSeconds = 0f;
            _mode = Mode.Screen;
        }

        private void BeginRetreat(Vector3 playerPos)
        {
            if (!_didPin)
            {
                AbortShot();
                return;
            }

            var z = Mathf.Clamp(_rb.position.z, -2.15f, 2.15f);
            if (Mathf.Abs(z - playerPos.z) < 0.4f)
                z += Mathf.Sign(_rb.position.z - playerPos.z + 0.01f) * 0.6f;
            _retreatTarget = KeepOffBump(new Vector3(ScreenX(), _rb.position.y, z));
            _mode = Mode.ShotRetreat;
            _holdStarted = false;
            _retreatUntil = Time.time + Mathf.Min(_retreatSeconds, 1.1f);
        }

        private void TickShotRetreat(Vector3 playerPos)
        {
            if (PlayerComingOverBump(playerPos) || PlayerOnBump(playerPos) || ThreateningBump(playerPos))
            {
                _didPin = false;
                _mode = Mode.Wedge;
                _wedgeSince = Time.time;
                return;
            }

            DriveToPose(PathTo(_retreatTarget), FaceToward(playerPos));

            var atBackup = Horizontal(_rb.position, _retreatTarget) < 0.55f;
            var clearOfPlayer = Horizontal(_rb.position, playerPos) >= 1.4f;
            if (!atBackup && Time.time < _retreatUntil) return;
            if (!clearOfPlayer && !atBackup) return;

            _didPin = false;
            if (PlayerLikelyShooting(playerPos))
            {
                BeginShotCharge(playerPos);
            }
            else
            {
                _mode = Mode.Screen;
            }
        }

        private void TickCollect(Vector3 playerPos)
        {
            if (!TryFuel(playerPos, out var fuel))
            {
                _mode = Mode.Screen;
                SetMechanisms(false, false);
                return;
            }

            _collectTarget = fuel;
            SetMechanisms(true, false);
            DriveToPose(PathTo(fuel), FaceToward(fuel));
            if (HasFuel()) _mode = Mode.Dump;
        }

        private void TickDump(Vector3 playerPos)
        {
            if (!HasFuel())
            {
                SetMechanisms(false, false);
                _mode = Mode.Screen;
                return;
            }

            var dump = new Vector3(_playerSign * 2.35f, _rb.position.y, Mathf.Clamp(_rb.position.z, -2.2f, 2.2f));
            dump = ClearPosts(dump, playerPos);
            var inNeutral = Mathf.Abs(_rb.position.x) < 3.45f;
            SetMechanisms(false, inNeutral);
            DriveToPose(PathTo(dump), FaceToward(new Vector3(0f, _rb.position.y, dump.z)));
        }

        private void DriveToPose(Vector3 worldTarget, float holdYaw)
        {
            var error = worldTarget - _rb.position;
            error.y = 0f;
            var dist = error.magnitude;
            var maxSpeed = _drive.GetMaxVelocity();
            float speed;
            if (dist < 0.05f) speed = 0f;
            else
            {
                const float decel = 7.5f;
                var brake = Mathf.Sqrt(2f * decel * dist);
                speed = Mathf.Min(maxSpeed, brake, dist / 0.2f);
            }

            var worldVel = dist > 0.05f ? error / dist * (speed * _speedScale) : Vector3.zero;
            ApplyDrive(worldVel, holdYaw);
        }

        private void DriveRam(Vector3 worldTarget, float holdYaw, float speedScale = 1f)
        {
            var error = worldTarget - _rb.position;
            error.y = 0f;
            var dir = error.sqrMagnitude > 0.01f ? error.normalized : PlayerForward();
            ApplyDrive(dir * (_drive.GetMaxVelocity() * speedScale * _speedScale * Mathf.Max(0.35f, _ramScale)), holdYaw);
        }

        private void ApplyDrive(Vector3 worldVel, float holdYaw)
        {
            var yawError = Mathf.DeltaAngle(_rb.rotation.eulerAngles.y, holdYaw);
            var maxOmega = _drive.GetMaxOmega();
            var omega = Mathf.Clamp(yawError * 6f, -maxOmega, maxOmega);
            _drive.OverrideInput(worldVel.x, worldVel.z, omega, DriveController.DrivePerspective.FieldOriented);
        }

        private Vector3 PathTo(Vector3 goal)
        {
            var p = _rb.position;
            goal = KeepOffBump(goal);
            goal.y = p.y;
            var playerPos = PlayerPosition();

            if (StuckOnBump() || InTrenchThroat(p))
            {
                return EscapeBumpPose();
            }

            if (CrossesBumpX(p, goal) && Mathf.Abs(p.z) < TrenchZ && Mathf.Abs(goal.z) < TrenchZ)
            {
                return EscapeBumpPose();
            }

            if (LineHitsPost(p, goal, out var around))
            {
                return KeepOffBump(around);
            }

            return KeepOffBump(ClearPosts(goal, playerPos));
        }

        /// <summary>
        /// Cross the bump X-band through a trench, then sit at the goal.
        /// Going over the bump at z=0 leaves the bot stuck on its own side.
        /// </summary>
        private Vector3 PathAcrossField(float goalX, float goalZ)
        {
            var p = _rb.position;
            goalZ = Mathf.Clamp(goalZ, -2.15f, 2.15f);
            var goal = new Vector3(goalX, p.y, goalZ);

            if (!CrossesBumpX(p, goal))
            {
                if (InTrenchThroat(p))
                {
                    return new Vector3(goalX, p.y, p.z);
                }

                if (Mathf.Abs(p.z) > 2.25f && Mathf.Abs(p.x) > AllianceX)
                {
                    return new Vector3(p.x, p.y, goalZ);
                }

                return ClearPosts(goal, PlayerPosition());
            }

            var trenchZ = (p.z >= 0f ? 1f : -1f) * (TrenchZ + 0.5f);
            if (Mathf.Abs(p.z) < TrenchZ + 0.15f)
            {
                return new Vector3(p.x, p.y, trenchZ);
            }

            return new Vector3(goalX, p.y, p.z);
        }

        private bool TryUnstick(Vector3 goal, float yaw)
        {
            var speed = new Vector2(_rb.velocity.x, _rb.velocity.z).magnitude;
            var far = Horizontal(_rb.position, goal) > 0.7f;
            var onBump = StuckOnBump();
            if ((speed < 0.28f && far) || onBump)
            {
                _stuckTime += Time.fixedDeltaTime;
            }
            else
            {
                _stuckTime = 0f;
            }

            var wait = onBump ? 0.12f : 0.4f;
            if (_stuckTime < wait) return false;
            if (!onBump && _mode is not (Mode.Cross or Mode.Screen or Mode.Dump or Mode.Collect)) return false;

            DriveToPose(EscapeBumpPose(), yaw);
            return true;
        }

        private bool StuckOnBump()
        {
            var p = _rb.position;
            if (OnBumpStrip(p)) return true;
            var onBumpX = Mathf.Abs(Mathf.Abs(p.x) - BumpX) < 0.85f;
            return onBumpX && Mathf.Abs(p.z) < TrenchZ && p.y > 0.12f;
        }

        private Vector3 EscapeBumpPose()
        {
            var p = _rb.position;
            var signedX = p.x * _playerSign;
            if (signedX >= BumpX - 0.2f)
                return new Vector3(ScreenX(), p.y, Mathf.Clamp(p.z, -2.15f, 2.15f));

            var trenchZ = (Mathf.Abs(p.z) < 0.15f ? 1f : Mathf.Sign(p.z)) * (TrenchZ + 0.55f);
            if (Mathf.Abs(p.z) < TrenchZ + 0.2f)
                return new Vector3(p.x, p.y, trenchZ);
            return new Vector3(ScreenX(), p.y, p.z);
        }

        private Vector3 KeepOffBump(Vector3 goal)
        {
            if (!_stayOffBump) return goal;
            goal.y = _rb != null ? _rb.position.y : goal.y;
            if (!OnBumpStrip(goal) && Mathf.Abs(Mathf.Abs(goal.x) - BumpX) >= 0.55f)
            {
                if (Mathf.Abs(goal.x) < AllianceX + 0.35f && Mathf.Abs(goal.z) < TrenchZ)
                    goal.x = _playerSign * (AllianceX + WedgeInset);
                return goal;
            }

            goal.x = _playerSign * Mathf.Max(Mathf.Abs(goal.x), AllianceX + WedgeInset);
            if (OnBumpStrip(goal))
                goal.x = ScreenX();
            return goal;
        }

        private static bool InTrenchThroat(Vector3 p)
        {
            return Mathf.Abs(Mathf.Abs(p.x) - BumpX) < 0.7f && Mathf.Abs(p.z) > TrenchZ - 0.25f;
        }

        private void CachePosts()
        {
            _posts.Clear();
            foreach (var climb in FindObjectsByType<TowerClimbScoring>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                AddPostFromRoot(climb.transform);
            }

            foreach (var hub in FindObjectsByType<HubScoring>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                AddPostFromRoot(hub.transform);
            }

            foreach (var named in new[] { "BlueTower", "RedTower" })
            {
                var found = GameObject.Find(named);
                if (found != null) AddPostFromRoot(found.transform);
            }
        }

        private void AddPostFromRoot(Transform root)
        {
            if (root == null) return;
            var cols = root.GetComponentsInChildren<Collider>();
            if (cols == null || cols.Length == 0) return;
            var bounds = cols[0].bounds;
            foreach (var col in cols)
            {
                if (col != null && col.enabled) bounds.Encapsulate(col.bounds);
            }

            if (Mathf.Abs(bounds.center.x) < 3.2f) return;
            var radius = Mathf.Clamp(Mathf.Max(bounds.extents.x, bounds.extents.z) + 0.45f, 0.55f, 1.45f);
            _posts.Add(new Disc { Xz = new Vector3(bounds.center.x, 0f, bounds.center.z), Radius = radius });
        }

        private Vector3 ClearPosts(Vector3 goal, Vector3 playerPos)
        {
            goal.y = _rb.position.y;
            foreach (var post in _posts)
            {
                var dx = goal.x - post.Xz.x;
                var dz = goal.z - post.Xz.z;
                var mag = Mathf.Sqrt(dx * dx + dz * dz);
                if (mag >= post.Radius) continue;
                var side = Mathf.Sign(playerPos.z - post.Xz.z);
                if (Mathf.Abs(side) < 0.01f) side = Mathf.Sign(_rb.position.z - post.Xz.z);
                if (Mathf.Abs(side) < 0.01f) side = 1f;
                goal.z = post.Xz.z + side * (post.Radius + 0.25f);
                goal.z = Mathf.Clamp(goal.z, -FieldHalfZ + 0.45f, FieldHalfZ - 0.45f);
            }

            return goal;
        }

        private bool LineHitsPost(Vector3 from, Vector3 to, out Vector3 around)
        {
            around = to;
            foreach (var post in _posts)
            {
                if (!SegmentHitsDisc(from, to, post)) continue;
                var playerPos = PlayerPosition();
                var side = Mathf.Sign(playerPos.z - post.Xz.z);
                if (Mathf.Abs(side) < 0.01f) side = Mathf.Sign(from.z - post.Xz.z);
                if (Mathf.Abs(side) < 0.01f) side = 1f;
                around = new Vector3(post.Xz.x, from.y, post.Xz.z + side * (post.Radius + 0.55f));
                around.z = Mathf.Clamp(around.z, -FieldHalfZ + 0.45f, FieldHalfZ - 0.45f);
                return true;
            }

            return false;
        }

        private static bool SegmentHitsDisc(Vector3 from, Vector3 to, Disc post)
        {
            var a = new Vector2(from.x, from.z);
            var b = new Vector2(to.x, to.z);
            var c = new Vector2(post.Xz.x, post.Xz.z);
            var ab = b - a;
            var t = ab.sqrMagnitude > 0.001f ? Mathf.Clamp01(Vector2.Dot(c - a, ab) / ab.sqrMagnitude) : 0f;
            var closest = a + ab * t;
            return (closest - c).sqrMagnitude < post.Radius * post.Radius;
        }

        private static bool OnBumpStrip(Vector3 p)
        {
            return Mathf.Abs(Mathf.Abs(p.x) - BumpX) < 0.55f && Mathf.Abs(p.z) < TrenchZ;
        }

        private static bool CrossesBumpX(Vector3 from, Vector3 to)
        {
            var minX = Mathf.Min(from.x, to.x);
            var maxX = Mathf.Max(from.x, to.x);
            return (minX < 3.9f && maxX > 3.4f) || (minX < -3.4f && maxX > -3.9f);
        }

        private bool ThreateningBump(Vector3 pos)
        {
            var vel = _playerBody != null ? _playerBody.velocity : Vector3.zero;
            var inBumpZ = Mathf.Abs(pos.z) < TrenchZ - 0.05f;
            var nearBump = Mathf.Abs(Mathf.Abs(pos.x) - BumpX) < 1.8f;
            var leaving = vel.x * -_playerSign > 0.35f;
            var approaching = vel.x * _playerSign > 0.35f && inBumpZ && pos.x * _playerSign < AllianceX + 0.6f;
            return inBumpZ && (nearBump || leaving || approaching);
        }

        private bool PlayerCouldCrossBump(Vector3 pos)
        {
            var onBumpLane = Mathf.Abs(pos.z) < TrenchZ;
            var notYetInside = pos.x * _playerSign < AllianceX + 0.2f;
            return onBumpLane && notYetInside;
        }

        private bool CoveringBump()
        {
            return Mathf.Abs(_rb.position.x - ScreenX()) < 1.25f && Mathf.Abs(_rb.position.z) < 2.3f;
        }

        private bool PlayerFarFromBump(Vector3 pos)
        {
            return Mathf.Abs(pos.z) > 2.5f || pos.x * _playerSign > 6.1f;
        }

        private bool PlayerComingOverBump(Vector3 pos)
        {
            var vel = _playerBody != null ? _playerBody.velocity : Vector3.zero;
            var bumpLane = Mathf.Abs(pos.z) < TrenchZ - 0.05f;
            var towardAlliance = vel.x * _playerSign > 0.28f;
            var signedX = pos.x * _playerSign;
            var stillFieldSide = signedX > 0.6f && signedX < BumpX + 0.55f;
            return bumpLane && towardAlliance && stillFieldSide;
        }

        private bool PlayerOnBump(Vector3 pos)
        {
            var onBand = Mathf.Abs(Mathf.Abs(pos.x) - BumpX) < 0.7f && Mathf.Abs(pos.z) < TrenchZ;
            return onBand && pos.y > 0.10f;
        }

        private bool PlayerPastBumpInZone(Vector3 pos)
        {
            return pos.x * _playerSign > AllianceX && pos.y < 0.12f;
        }

        private bool PlayerClearOfBump(Vector3 pos)
        {
            if (PlayerOnBump(pos) || PlayerComingOverBump(pos)) return false;
            var signedX = pos.x * _playerSign;
            return signedX > AllianceX || signedX < 2.9f || Mathf.Abs(pos.z) > TrenchZ + 0.15f;
        }

        private Vector3 WedgePose(Vector3 playerPos)
        {
            var z = Mathf.Clamp(playerPos.z, -2.15f, 2.15f);
            return KeepOffBump(new Vector3(_playerSign * (AllianceX + WedgeInset), _rb.position.y, z));
        }

        private bool PlayerLikelyShooting(Vector3 pos)
        {
            if (!PlayerInTheirAllianceZone(pos)) return false;
            if (_shotContest == ShotContest.Off) return false;
            if (PlayerOnBump(pos) || PlayerComingOverBump(pos)) return false;
            if (pos.x * _playerSign < AllianceX + 0.85f) return false;

            if (_playerRebuilt != null && (_playerRebuilt.OuttakeAction || _playerRebuilt.ManualShoot))
                return true;

            var speed = _playerBody != null ? new Vector2(_playerBody.velocity.x, _playerBody.velocity.z).magnitude : 0f;
            if (speed >= Mathf.Min(_shootSpeedThreshold, 1.05f)) return false;
            if (PlayerOffAngle(pos)) return false;

            var hub = new Vector3(_playerSign * HubX, pos.y, 0f);
            return Horizontal(pos, hub) < 2.6f;
        }

        private bool PlayerOffAngle(Vector3 playerPos)
        {
            var hub = new Vector3(_playerSign * HubX, playerPos.y, 0f);
            var toHub = hub - playerPos;
            toHub.y = 0f;
            if (toHub.sqrMagnitude < 0.05f) return false;

            var hubYaw = Quaternion.LookRotation(toHub, Vector3.up).eulerAngles.y;
            var playerYaw = _playerBody != null ? _playerBody.rotation.eulerAngles.y : _player.transform.eulerAngles.y;
            return Mathf.Abs(Mathf.DeltaAngle(playerYaw, hubYaw)) > OffAngleDegrees;
        }

        private void SilenceMechanisms() => SetMechanisms(false, false);

        private void SetMechanisms(bool intake, bool pass)
        {
            if (_selfRebuilt == null) return;
            TryForcePressed(_selfRebuilt.IntakeAction, intake);
            TryForcePressed(_selfRebuilt.StowAction, !intake);
            TryForcePressed(_selfRebuilt.AutoPass, pass);
            TryForcePressed(_selfRebuilt.OuttakeAction, false);
            TryForcePressed(_selfRebuilt.ManualShoot, false);
        }

        private static void TryForcePressed(object input, bool pressed)
        {
            if (input == null) return;
            var type = input.GetType();
            var method = type.GetMethod("ForcePressed", new[] { typeof(bool?) })
                         ?? type.GetMethod("ForcePressed", new[] { typeof(bool) })
                         ?? type.GetMethod("ForcePressed", BindingFlags.Instance | BindingFlags.Public, null,
                             Type.EmptyTypes, null);
            if (method == null)
            {
                var matches = type.GetMethods(BindingFlags.Instance | BindingFlags.Public);
                foreach (var candidate in matches)
                {
                    if (candidate.Name != "ForcePressed") continue;
                    var parameters = candidate.GetParameters();
                    if (parameters.Length != 1) continue;
                    method = candidate;
                    break;
                }
            }

            if (method == null) return;
            var parameter = method.GetParameters();
            if (parameter.Length == 1 && parameter[0].ParameterType == typeof(bool?))
            {
                method.Invoke(input, new object[] { (bool?)pressed });
                return;
            }

            if (parameter.Length >= 1)
            {
                method.Invoke(input, new object[] { pressed });
            }
        }

        private bool HasFuel()
        {
            var node = _pieces != null ? _pieces.GetPieceByName("Fuel") : null;
            return node != null && node.HasPiece();
        }

        private bool TryFuel(Vector3 playerPos, out Vector3 fuel)
        {
            fuel = Vector3.zero;
            if (Time.time < _fuelScanAt)
            {
                fuel = _cachedFuel;
                return _cachedFuel.sqrMagnitude > 0.01f;
            }

            _fuelScanAt = Time.time + 0.18f;
            _cachedFuel = Vector3.zero;
            var pieces = FindObjectsByType<RebuiltGamePieceController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            var best = float.MaxValue;
            foreach (var piece in pieces)
            {
                if (piece == null || piece.IsScored) continue;
                if (piece.parentBody != null && piece.parentBody.GetComponentInParent<RobotBase>() != null) continue;
                var pos = piece.transform.position;
                if (!PlayerInTheirAllianceZone(pos)) continue;
                if (pos.y > 1.15f) continue;
                var d = Horizontal(_rb.position, pos);
                if (d >= best || d > 5.5f) continue;
                best = d;
                _cachedFuel = pos;
            }

            fuel = _cachedFuel;
            return fuel.sqrMagnitude > 0.01f;
        }

        private void RecordPlayer()
        {
            var pos = PlayerPosition();
            Vector3 vel;
            if (_playerBody != null) vel = _playerBody.velocity;
            else if (_hasLastPlayer) vel = (pos - _lastPlayerPos) / Time.fixedDeltaTime;
            else vel = Vector3.zero;

            _histPos[_histIndex] = pos;
            _histVel[_histIndex] = vel;
            _histIndex = (_histIndex + 1) % HistoryLength;
            if (_histCount < HistoryLength) _histCount++;
            _lastPlayerPos = pos;
            _hasLastPlayer = true;
        }

        private bool TryDelayedPlayer(out Vector3 pos, out Vector3 vel)
        {
            pos = Vector3.zero;
            vel = Vector3.zero;
            if (_histCount < 3) return false;

            var steps = Mathf.Clamp(Mathf.RoundToInt(_reactionDelay / Time.fixedDeltaTime), 1, _histCount);
            var index = _histIndex - steps;
            if (index < 0) index += HistoryLength;
            pos = _histPos[index];
            vel = _histVel[index];
            return true;
        }

        private Vector3 PlayerPosition()
        {
            if (_playerBody != null) return _playerBody.position;
            return _player.transform.position;
        }

        private Vector3 PlayerForward()
        {
            var yaw = _playerBody != null ? _playerBody.rotation.eulerAngles.y : _player.transform.eulerAngles.y;
            return Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
        }

        private bool PlayerInTheirAllianceZone(Vector3 pos)
        {
            return _playerSign > 0f ? pos.x > AllianceX : pos.x < -AllianceX;
        }

        private bool InPlayerAlliance()
        {
            return _playerSign > 0f ? _rb.position.x > AllianceX : _rb.position.x < -AllianceX;
        }

        private float ScreenX() => _playerSign * ScreenHoldX;

        private float LaneZ(Lane lane)
        {
            return lane switch
            {
                Lane.TrenchPlus => TrenchZ + 0.55f,
                Lane.TrenchMinus => -(TrenchZ + 0.55f),
                _ => 0f
            };
        }

        private Vector3 GatePose(Lane lane)
        {
            return new Vector3(ScreenX(), _rb.position.y, LaneZ(lane));
        }

        private float FaceToward(Vector3 worldPoint)
        {
            var delta = worldPoint - _rb.position;
            delta.y = 0f;
            if (delta.sqrMagnitude < 0.04f) return _rb.rotation.eulerAngles.y;
            return Quaternion.LookRotation(delta, Vector3.up).eulerAngles.y;
        }

        private float FaceSideOn(Vector3 worldPoint)
        {
            return FaceToward(worldPoint) + 80f;
        }

        private static float Horizontal(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }
    }
}
