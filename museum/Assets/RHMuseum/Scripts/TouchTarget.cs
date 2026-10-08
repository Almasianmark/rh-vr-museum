using System.Collections.Generic;
using UnityEngine;

namespace RHMuseum
{
    /// <summary>
    /// A flat rectangle (local XY, centered, facing -Z) that hands can touch. Fingertips are tested
    /// against every enabled target each frame. That's cheap at museum scale and needs no physics layers.
    /// Local z: negative = in front of the surface, positive = pushed into it.
    /// </summary>
    public abstract class TouchTarget : MonoBehaviour
    {
        public static readonly List<TouchTarget> Active = new List<TouchTarget>();

        public Vector2 size = new Vector2(1, 1);
        public float contactDepth = 0.03f;   // within 3 cm counts as touching
        public float pushDepth = 0.07f;      // 7 cm into the surface = "push through"

        readonly bool[] _touching = new bool[2];
        readonly bool[] _pushed = new bool[2];
        readonly Vector2[] _lastUv = new Vector2[2];

        protected virtual void OnEnable() => Active.Add(this);
        protected virtual void OnDisable()
        {
            Active.Remove(this);
            _touching[0] = _touching[1] = _pushed[0] = _pushed[1] = false;
        }

        /// <summary>Called by PlayerRig for each hand (0 = left, 1 = right).</summary>
        public void ProcessHand(int hand, Vector3 fingertipWorld)
        {
            Vector3 p = transform.InverseTransformPoint(fingertipWorld);
            bool inside = Mathf.Abs(p.x) <= size.x / 2 && Mathf.Abs(p.y) <= size.y / 2;
            bool touching = inside && p.z > -contactDepth && p.z < pushDepth * 3;
            Vector2 uv = new Vector2(p.x / size.x + 0.5f, p.y / size.y + 0.5f);

            if (touching && !_touching[hand])
            {
                OnTouchStart(uv);
                _lastUv[hand] = uv;
            }
            else if (touching && Vector2.Distance(Vector2.Scale(uv, size), Vector2.Scale(_lastUv[hand], size)) > 0.08f)
            {
                OnTouchMove(uv);   // dragging a finger leaves a trail
                _lastUv[hand] = uv;
            }

            bool pushed = touching && p.z > pushDepth;
            if (pushed && !_pushed[hand]) OnPush(uv);
            _touching[hand] = touching;
            _pushed[hand] = pushed;
        }

        /// <summary>Desktop / pointer: a click at a world point = touch + push.</summary>
        public void Click(Vector3 worldPoint)
        {
            Vector3 p = transform.InverseTransformPoint(worldPoint);
            var uv = new Vector2(p.x / size.x + 0.5f, p.y / size.y + 0.5f);
            OnTouchStart(uv);
            OnPush(uv);
        }

        protected virtual void OnTouchStart(Vector2 uv) { }
        protected virtual void OnTouchMove(Vector2 uv) { }
        protected virtual void OnPush(Vector2 uv) { }

        /// <summary>Thin trigger collider so desktop raycasts can find the target.</summary>
        protected void EnsureCollider()
        {
            var bc = GetComponent<BoxCollider>();
            if (bc == null) bc = gameObject.AddComponent<BoxCollider>();   // no ?? on UnityEngine.Object
            bc.isTrigger = true;
            bc.size = new Vector3(size.x, size.y, 0.02f);
            bc.center = Vector3.zero;
        }
    }
}
