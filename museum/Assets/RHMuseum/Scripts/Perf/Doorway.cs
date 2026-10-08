using UnityEngine;

namespace RHMuseum
{
    /// <summary>2D doorway visibility (top-down, x/z as Vector2). Pure math, no scene access, so it can be tested
    /// outside Unity.</summary>
    public static class Doorway
    {
        public enum View { None, ThroughDoor, All }

        /// <summary>Where the eye is relative to a room: inside it, outside looking in, or behind its walls.</summary>
        public static View Classify(Vector2 cam, Vector2 doorA, Vector2 inward, Rect area, float margin)
        {
            float side = Vector2.Dot(cam - doorA, inward);   // < 0: on the doorway's outer side
            if (side < -margin) return View.ThroughDoor;
            var inside = area;
            inside.xMin -= margin; inside.yMin -= margin; inside.xMax += margin; inside.yMax += margin;
            if (inside.Contains(cam)) return View.All;
            // Standing in the doorway plane outside the room's span: be conservative. Further in = another room.
            return side < margin ? View.All : View.None;
        }

        /// <summary>Does segment p1–p2 intersect the wedge from cam through the doorway (widened by margin),
        /// beyond the doorway?</summary>
        public static bool SeesThrough(Vector2 cam, Vector2 doorA, Vector2 doorB, Vector2 inward, Vector2 p1, Vector2 p2,
                                       float margin)
        {
            Vector2 along = (doorB - doorA).normalized;
            Vector2 a = doorA - along * margin, b = doorB + along * margin;
            Vector2 da = a - cam, db = b - cam;
            float turn = da.x * db.y - da.y * db.x;
            if (Mathf.Abs(turn) < 1e-5f) return true;   // degenerate: looking along the doorway
            float sa = Mathf.Sign(turn);
            // Three half-planes dot(n, q) + k >= 0; the segment survives if something is left after clipping by all.
            Vector2 n2 = new Vector2(-da.y, da.x) * sa;     // on b's side of the ray through a
            Vector2 n3 = new Vector2(db.y, -db.x) * sa;     // on a's side of the ray through b
            return Clip(ref p1, ref p2, inward, -Vector2.Dot(inward, a))
                && Clip(ref p1, ref p2, n2, -Vector2.Dot(n2, cam))
                && Clip(ref p1, ref p2, n3, -Vector2.Dot(n3, cam));
        }

        static bool Clip(ref Vector2 p1, ref Vector2 p2, Vector2 n, float k)
        {
            float f1 = Vector2.Dot(n, p1) + k, f2 = Vector2.Dot(n, p2) + k;
            if (f1 < 0 && f2 < 0) return false;
            if (f1 < 0) p1 = Vector2.Lerp(p1, p2, f1 / (f1 - f2));
            else if (f2 < 0) p2 = Vector2.Lerp(p1, p2, f1 / (f1 - f2));
            return true;
        }
    }
}
