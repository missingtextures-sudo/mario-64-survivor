using KinematicCharacterController;
using RoR2;
using UnityEngine;

namespace MarioRoR2
{
    /// <summary>
    /// On Mario's body. Runs one libsm64 Mario at SM64's 30 ticks a second from RoR2's inputs, and makes the
    /// RoR2 body follow him: the body keeps its health, hurtboxes, items and camera, Mario does the moving.
    /// </summary>
    internal sealed class MarioController : MonoBehaviour
    {
        private const uint ActFlagAir = 1u << 11;
        private const float TickTime = 1f / Sheets.TickRate;

        public int MarioId = -1;
        public SM64MarioState State;
        public SM64MarioState PrevState;
        public CharacterBody Body = null!;
        public MarioRenderer? Renderer;

        // test / dev input override (world-space move, buttons) until a time
        public Vector3 OverrideMove;
        public bool OverrideA, OverrideB, OverrideZ;
        // counted in SM64 ticks, not seconds: a test run's background window can drop to a few frames a second
        public int OverrideTicks;     // ticks the override lasts (stick, then a release)
        public int OverrideHoldTicks; // of those, ticks the buttons are held
        public Vector3 MarkPosition;
        public float MaxYSinceMark = float.MinValue;

        public readonly MarioAttacks Attacks = new MarioAttacks();
        public double TickMs; // smoothed cost of one SM64 tick, for status

        private InputBankTest inputs = null!;
        private KinematicCharacterMotor? motor;
        private CharacterDirection? direction;
        private CharacterModel? model;
        private float accumulator;
        private float spawnRetryUntil;
        private Vector3 lastSetPosition;
        private Vector3 lastGround;
        private bool killed;
        private bool hidModel;
        private Vector3 feetToBody; // RoR2 keeps a body's transform at its capsule centre; Mario's position is his feet

        public static MarioController? Local;

        private void Awake()
        {
            Body = GetComponent<CharacterBody>();
            inputs = GetComponent<InputBankTest>();
            motor = GetComponent<KinematicCharacterMotor>();
            direction = GetComponent<CharacterDirection>();
        }

        private void Start()
        {
            var cm = GetComponent<CharacterMotor>(); // its capsule is set up after Awake
            feetToBody = Vector3.up * (cm != null && cm.Motor != null ? cm.capsuleHeight * 0.5f : 0.9f);
            model = Body.modelLocator != null && Body.modelLocator.modelTransform != null ? Body.modelLocator.modelTransform.GetComponent<CharacterModel>() : null;
        }

        private void Update()
        {
            if (!Sm64.Ready)
            {
                return;
            }
            if (Body.hasEffectiveAuthority)
            {
                Local = this;
            }
            if (!hidModel && model != null)
            {
                model.invisibilityCount++;
                hidModel = true;
            }
            if (Body.currentVehicle != null)
            {
                Renderer?.SetVisible(false); // in the drop pod; Mario appears when he steps out
                return;
            }
            if (MarioId < 0 && !TrySpawn())
            {
                return;
            }
            if (motor != null && motor.enabled)
            {
                motor.enabled = false; // RoR2's character physics off: Mario moves the body
            }

            // The game moved the body (out-of-bounds rescue, a teleport): Mario goes there too.
            if ((transform.position - lastSetPosition).sqrMagnitude > 1f)
            {
                Teleport(transform.position - feetToBody);
            }

            accumulator += Time.deltaTime;
            int ticks = 0;
            while (accumulator >= TickTime && ticks < 4)
            {
                accumulator -= TickTime;
                Tick();
                ticks++;
            }
            if (accumulator > TickTime)
            {
                accumulator = 0f; // fell behind (a hitch): don't spiral
            }

            float alpha = Mathf.Clamp01(accumulator / TickTime);
            Vector3 p = Vector3.Lerp(MarioMath.ToUnity(PrevState), MarioMath.ToUnity(State), alpha);
            transform.position = p + feetToBody;
            lastSetPosition = transform.position;
            if (direction != null)
            {
                direction.forward = MarioMath.Forward(State.faceAngle);
            }
            Renderer.Render(alpha, p + Vector3.up);
            // Like RoR2's own survivors fading out: hide Mario when a wall pushes the camera into him.
            Camera? cam = Camera.main;
            Renderer.SetVisible(cam == null || (cam.transform.position - (p + Vector3.up * 0.8f)).sqrMagnitude > 0.9f * 0.9f);
        }

        private bool TrySpawn()
        {
            if (Time.time < spawnRetryUntil)
            {
                return false;
            }
            LevelCollision.EnsureBuilt();
            Vector3 at = transform.position - feetToBody;
            LevelCollision.Follow(at);
            Vector3 s = MarioMath.ToSm64(at + Vector3.up * 0.5f);
            MarioId = Native.sm64_mario_create(s.x, s.y, s.z);
            if (MarioId < 0)
            {
                spawnRetryUntil = Time.time + 0.5f;
                string under = Physics.Raycast(at + Vector3.up, Vector3.down, out RaycastHit hit, 50f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)
                    ? $"{hit.collider.GetType().Name} '{hit.collider.name}' {hit.distance - 1f:0.00} m below (layer {hit.collider.gameObject.layer}, mesh readable {(hit.collider as MeshCollider)?.sharedMesh?.isReadable})"
                    : "nothing";
                Plugin.Log.LogWarning($"Mario couldn't spawn at {at} (no SM64 floor under him; {LevelCollision.LoadedSurfaces} surfaces loaded; Unity sees {under}); retrying");
                LevelCollision.Describe(at);
                return false;
            }
            Native.sm64_set_mario_water_level(MarioId, (int)Sheets.NoWaterLevel);
            Native.sm64_set_mario_gas_level(MarioId, (int)Sheets.NoWaterLevel);
            Renderer = new MarioRenderer("Mario", null);
            Native.sm64_set_mario_faceangle(MarioId, -direction!.yaw * Mathf.Deg2Rad);
            lastSetPosition = transform.position;
            lastGround = at;
            Tick();
            PrevState = State;
            Plugin.Log.LogInfo($"Mario spawned (id {MarioId}) at {at}, {Renderer.Triangles} triangles");
            return true;
        }

        private void Tick()
        {
            var inp = new SM64MarioInputs();
            Vector3 move;
            bool a, b, z;
            if (OverrideTicks > 0)
            {
                bool hold = OverrideHoldTicks > 0;
                move = hold ? OverrideMove : Vector3.zero;
                a = hold && OverrideA;
                b = hold && OverrideB;
                z = hold && OverrideZ;
                OverrideTicks--;
                OverrideHoldTicks--;
            }
            else
            {
                move = inputs.moveVector;
                a = Pressed("buttonA");
                b = Pressed("buttonB");
                z = Pressed("buttonZ");
            }
            if (killed || !Body.healthComponent.alive)
            {
                move = Vector3.zero;
                a = b = z = false;
            }
            move.y = 0f;
            float mag = Mathf.Clamp01(move.magnitude);
            if (mag > 0.01f)
            {
                // libsm64: intended yaw = stick angle + camera yaw. Stick straight "up", camera opposite the move.
                inp.camLookX = move.x / mag;
                inp.camLookZ = -move.z / mag;
                inp.stickY = mag;
            }
            else
            {
                inp.camLookZ = 1f;
            }
            inp.buttonA = (byte)(a ? 1 : 0);
            inp.buttonB = (byte)(b ? 1 : 0);
            inp.buttonZ = (byte)(z ? 1 : 0);

            PrevState = State;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Native.sm64_mario_tick(MarioId, ref inp, ref State, ref Renderer!.Buffers);
            TickMs = TickMs * 0.95 + 0.05 * (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Renderer.TakeTick(Vector3.zero);

            if (!Body.healthComponent.alive)
            {
                if (!killed)
                {
                    killed = true;
                    Native.sm64_mario_kill(MarioId);
                }
            }
            else
            {
                Native.sm64_set_mario_health(MarioId, (ushort)Sheets.MarioHealth);
            }

            Vector3 p = MarioMath.ToUnity(State);
            MaxYSinceMark = Mathf.Max(MaxYSinceMark, p.y);
            if ((State.action & ActFlagAir) == 0)
            {
                lastGround = p;
            }
            else if (p.y < lastGround.y - Sheets.FallRescue)
            {
                Teleport(lastGround + Vector3.up);
            }
            LevelCollision.Follow(p);
            Attacks.Tick(this);
        }

        private bool Pressed(string field)
        {
            foreach (Sheets.ControlRow row in Sheets.Controls)
            {
                if (row.Field != field)
                {
                    continue;
                }
                foreach (string name in row.Ror2Inputs)
                {
                    if (Button(name).down)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private InputBankTest.ButtonState Button(string name) => name switch
        {
            "jump" => inputs.jump,
            "skill1" => inputs.skill1,
            "skill2" => inputs.skill2,
            "skill3" => inputs.skill3,
            "skill4" => inputs.skill4,
            "sprint" => inputs.sprint,
            "interact" => inputs.interact,
            _ => inputs.activateEquipment,
        };

        public void Teleport(Vector3 to)
        {
            LevelCollision.Follow(to);
            Vector3 s = MarioMath.ToSm64(to);
            Native.sm64_set_mario_position(MarioId, s.x, s.y, s.z);
            Native.sm64_set_mario_velocity(MarioId, 0f, 0f, 0f);
            Native.sm64_set_mario_forward_velocity(MarioId, 0f);
            State.px = s.x;
            State.py = s.y;
            State.pz = s.z;
            PrevState = State;
            lastGround = to;
            transform.position = to + feetToBody;
            lastSetPosition = transform.position;
        }

        public void MarkForTest()
        {
            MarkPosition = MarioMath.ToUnity(State);
            MaxYSinceMark = MarkPosition.y;
        }

        private void OnDestroy()
        {
            if (Local == this)
            {
                Local = null;
            }
            if (MarioId >= 0)
            {
                Native.sm64_mario_delete(MarioId);
                MarioId = -1;
            }
            Renderer?.Dispose();
            Renderer = null;
        }
    }
}
