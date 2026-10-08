// Minimal stand-ins for the UnityEngine math used by Doorway.cs, matching Unity's documented behavior.
using System;

namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public float magnitude => (float)Math.Sqrt(x * x + y * y);
        public Vector2 normalized { get { float m = magnitude; return m > 1e-5f ? new Vector2(x / m, y / m) : new Vector2(0, 0); } }
        public static float Dot(Vector2 a, Vector2 b) => a.x * b.x + a.y * b.y;
        public static Vector2 Lerp(Vector2 a, Vector2 b, float t) { t = Math.Clamp(t, 0, 1); return a + (b - a) * t; }
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static Vector2 operator *(Vector2 a, float d) => new Vector2(a.x * d, a.y * d);
    }

    public struct Rect
    {
        public float xMin, yMin, xMax, yMax;
        public static Rect MinMaxRect(float xmin, float ymin, float xmax, float ymax) =>
            new Rect { xMin = xmin, yMin = ymin, xMax = xmax, yMax = ymax };
        // Unity: x >= xMin && x < xMax && y >= yMin && y < yMax
        public bool Contains(Vector2 p) => p.x >= xMin && p.x < xMax && p.y >= yMin && p.y < yMax;
    }

    public static class Mathf
    {
        public static float Abs(float f) => Math.Abs(f);
        public static float Sign(float f) => f >= 0f ? 1f : -1f;   // Unity returns 1 for 0
    }
}
