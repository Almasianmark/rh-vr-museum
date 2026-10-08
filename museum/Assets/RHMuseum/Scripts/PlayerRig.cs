using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
#if ENABLE_INPUT_SYSTEM
// Aliases, not the whole namespace: InputSystem's InputDevice and CommonUsages collide with UnityEngine.XR's.
using Keyboard = UnityEngine.InputSystem.Keyboard;
using Mouse = UnityEngine.InputSystem.Mouse;
#endif

namespace RHMuseum
{
    /// <summary>
    /// Minimal rig with no XR Interaction Toolkit dependency. Head and hands come from UnityEngine.XR.InputDevices
    /// (works with the OpenXR plugin on Quest). Left stick moves, right stick snap-turns, B/Y = back,
    /// fingertips touch paintings. Without a headset: WASD + right-drag mouse look, click to touch, Esc = back.
    /// </summary>
    public class PlayerRig : MonoBehaviour
    {
        public float moveSpeed = 2.2f;
        public float snapTurnDegrees = 30f;
        public float desktopEyeHeight = 1.6f;

        public Camera Head { get; private set; }
        public Transform LeftHand { get; private set; }
        public Transform RightHand { get; private set; }
        public bool XRActive { get; private set; }

        public event Action BackPressed;

        CharacterController _cc;
        Transform _tracking;
        bool _snapReady = true, _backHeld;
        float _yaw, _pitch, _fallSpeed;

        // Fingertip offset from the controller grip pose (roughly where an index finger rests).
        static readonly Vector3 FingertipOffset = new Vector3(0, -0.02f, 0.075f);

        public static PlayerRig Create(Vector3 position, float yaw)
        {
            var go = new GameObject("Player");
            go.transform.SetPositionAndRotation(position, Quaternion.Euler(0, yaw, 0));
            return go.AddComponent<PlayerRig>();
        }

        void Awake()
        {
            _cc = gameObject.AddComponent<CharacterController>();
            _cc.height = 1.7f;
            _cc.radius = 0.25f;
            _cc.center = new Vector3(0, 0.85f, 0);
            _cc.stepOffset = 0.3f;

            _tracking = new GameObject("TrackingSpace").transform;
            _tracking.SetParent(transform, false);

            var headGo = new GameObject("Head");
            headGo.transform.SetParent(_tracking, false);
            Head = headGo.AddComponent<Camera>();
            Head.nearClipPlane = 0.05f;
            Head.farClipPlane = 400f;
            Head.clearFlags = CameraClearFlags.SolidColor;
            Head.backgroundColor = Palette.Sky;
            headGo.tag = "MainCamera";
            headGo.AddComponent<AudioListener>();

            LeftHand = MakeHand("LeftHand", new Color(0.35f, 0.75f, 1f));
            RightHand = MakeHand("RightHand", new Color(1f, 0.55f, 0.35f));
        }

        Transform MakeHand(string name, Color color)
        {
            var hand = new GameObject(name).transform;
            hand.SetParent(_tracking, false);
            var tip = Greybox.Box(hand, "Fingertip", FingertipOffset, Vector3.one * 0.025f, color, false);
            tip.isStatic = false;
            var body = Greybox.Box(hand, "Grip", Vector3.zero, new Vector3(0.05f, 0.05f, 0.11f), color * 0.7f, false);
            body.isStatic = false;
            return hand;
        }

        bool _hooked;

        void Start()
        {
            _yaw = transform.eulerAngles.y;
            XRActive = XRSettings.isDeviceActive;
            if (XRActive) RequestFloorOrigin();
            else Head.transform.localPosition = new Vector3(0, desktopEyeHeight, 0);
            LeftHand.gameObject.SetActive(XRActive);
            RightHand.gameObject.SetActive(XRActive);
            if (!_hooked) Application.onBeforeRender += UpdateTrackedPoses;
            _hooked = true;
        }

        void OnDestroy() => Application.onBeforeRender -= UpdateTrackedPoses;

        static void RequestFloorOrigin()
        {
            var subsystems = new List<XRInputSubsystem>();
            SubsystemManager.GetSubsystems(subsystems);
            foreach (var s in subsystems)
                if ((s.GetSupportedTrackingOriginModes() & TrackingOriginModeFlags.Floor) != 0)
                    s.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
        }

        void Update()
        {
            if (!XRActive && XRSettings.isDeviceActive) Start();   // headset connected late (Link)
            if (XRActive) UpdateXR();
            else UpdateDesktop();
            ApplyGravity();
        }

        // ---------------- XR ----------------

        void UpdateTrackedPoses()
        {
            if (!XRActive) return;
            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (head.TryGetFeatureValue(CommonUsages.centerEyePosition, out Vector3 hp) ||
                head.TryGetFeatureValue(CommonUsages.devicePosition, out hp))
                Head.transform.localPosition = hp;
            if (head.TryGetFeatureValue(CommonUsages.centerEyeRotation, out Quaternion hr) ||
                head.TryGetFeatureValue(CommonUsages.deviceRotation, out hr))
                Head.transform.localRotation = hr;
            Pose(XRNode.LeftHand, LeftHand);
            Pose(XRNode.RightHand, RightHand);
        }

        static void Pose(XRNode node, Transform t)
        {
            var d = InputDevices.GetDeviceAtXRNode(node);
            bool ok = d.isValid
                      && d.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 p)
                      && d.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion r);
            if (ok)
            {
                d.TryGetFeatureValue(CommonUsages.devicePosition, out p);
                d.TryGetFeatureValue(CommonUsages.deviceRotation, out r);
                t.localPosition = p;
                t.localRotation = r;
            }
            if (t.gameObject.activeSelf != ok) t.gameObject.SetActive(ok);
        }

        void UpdateXR()
        {
            UpdateTrackedPoses();
            var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);

            // Keep the capsule under the head as the user walks around their play space.
            Vector3 headLocal = _tracking.localPosition + Head.transform.localPosition;
            _cc.center = new Vector3(headLocal.x, _cc.height / 2, headLocal.z);

            if (left.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 stick) && stick.sqrMagnitude > 0.04f)
            {
                Vector3 fwd = Vector3.ProjectOnPlane(Head.transform.forward, Vector3.up).normalized;
                Vector3 rightDir = Vector3.Cross(Vector3.up, fwd);
                _cc.Move((fwd * stick.y + rightDir * stick.x) * moveSpeed * Time.deltaTime);
            }

            if (right.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 turn))
            {
                if (_snapReady && Mathf.Abs(turn.x) > 0.7f)
                {
                    transform.RotateAround(Head.transform.position, Vector3.up, Mathf.Sign(turn.x) * snapTurnDegrees);
                    _snapReady = false;
                }
                else if (Mathf.Abs(turn.x) < 0.3f) _snapReady = true;
            }

            bool back = Pressed(left, CommonUsages.secondaryButton) || Pressed(right, CommonUsages.secondaryButton);
            if (back && !_backHeld) BackPressed?.Invoke();
            _backHeld = back;

            foreach (var target in TouchTarget.Active.ToArray())
            {
                if (target == null || !target.isActiveAndEnabled) continue;
                if (LeftHand.gameObject.activeSelf) target.ProcessHand(0, LeftHand.TransformPoint(FingertipOffset));
                if (RightHand.gameObject.activeSelf) target.ProcessHand(1, RightHand.TransformPoint(FingertipOffset));
            }
        }

        static bool Pressed(InputDevice d, InputFeatureUsage<bool> usage) => d.TryGetFeatureValue(usage, out bool v) && v;

        // ---------------- Desktop ----------------

        void UpdateDesktop()
        {
            Vector2 move = DesktopInput.Move();
            Vector2 look = DesktopInput.LookDelta();
            _yaw += look.x * 0.15f;
            _pitch = Mathf.Clamp(_pitch - look.y * 0.15f, -80, 80);
            transform.rotation = Quaternion.Euler(0, _yaw, 0);
            Head.transform.localRotation = Quaternion.Euler(_pitch, 0, 0);

            Vector3 dir = transform.forward * move.y + transform.right * move.x;
            _cc.Move(dir * moveSpeed * 1.4f * Time.deltaTime);

            if (DesktopInput.BackPressed()) BackPressed?.Invoke();
            if (DesktopInput.ClickPressed(out Vector2 screen))
            {
                var ray = Head.ScreenPointToRay(screen);
                var hits = Physics.RaycastAll(ray, 12f, ~0, QueryTriggerInteraction.Collide);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));   // RaycastAll is unordered
                foreach (var hit in hits)
                {
                    var target = hit.collider.GetComponent<TouchTarget>();
                    if (target != null && target.isActiveAndEnabled)
                    {
                        target.Click(hit.point);
                        break;
                    }
                    if (!hit.collider.isTrigger) break;   // a wall is in the way
                }
            }
        }

        void ApplyGravity()
        {
            _fallSpeed = _cc.isGrounded ? -0.5f : _fallSpeed - 9.81f * Time.deltaTime;
            _cc.Move(Vector3.up * _fallSpeed * Time.deltaTime);
        }

        /// <summary>Place the rig so the user's head ends up at position (xz), looking along yaw.</summary>
        public void TeleportTo(Vector3 position, float yaw)
        {
            _cc.enabled = false;
            float headYaw = XRActive ? Head.transform.localEulerAngles.y : 0;
            transform.rotation = Quaternion.Euler(0, yaw - headYaw, 0);
            Vector3 headOffset = XRActive
                ? Vector3.ProjectOnPlane(transform.TransformVector(_tracking.localPosition + Head.transform.localPosition), Vector3.up)
                : Vector3.zero;
            transform.position = new Vector3(position.x, position.y, position.z) - headOffset;
            _yaw = yaw;
            _pitch = 0;
            _fallSpeed = 0;
            _cc.enabled = true;
        }
    }

    /// <summary>Keyboard/mouse for editor testing; Input System when enabled, legacy Input otherwise.</summary>
    static class DesktopInput
    {
#if ENABLE_INPUT_SYSTEM
        public static Vector2 Move()
        {
            var k = Keyboard.current;
            if (k == null) return Vector2.zero;
            return new Vector2((k.dKey.isPressed ? 1 : 0) - (k.aKey.isPressed ? 1 : 0),
                               (k.wKey.isPressed ? 1 : 0) - (k.sKey.isPressed ? 1 : 0));
        }

        public static Vector2 LookDelta()
        {
            var m = Mouse.current;
            return m != null && m.rightButton.isPressed ? m.delta.ReadValue() : Vector2.zero;
        }

        public static bool BackPressed()
        {
            var k = Keyboard.current;
            return k != null && (k.escapeKey.wasPressedThisFrame || k.backspaceKey.wasPressedThisFrame);
        }

        public static bool ClickPressed(out Vector2 screen)
        {
            var m = Mouse.current;
            screen = m != null ? m.position.ReadValue() : Vector2.zero;
            return m != null && m.leftButton.wasPressedThisFrame;
        }
#else
        public static Vector2 Move() => new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
        public static Vector2 LookDelta() => Input.GetMouseButton(1) ? new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 10f : Vector2.zero;
        public static bool BackPressed() => Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace);
        public static bool ClickPressed(out Vector2 screen)
        {
            screen = Input.mousePosition;
            return Input.GetMouseButtonDown(0);
        }
#endif
    }
}
