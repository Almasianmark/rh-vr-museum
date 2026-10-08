using System.Collections.Generic;
using UnityEngine;

namespace RHMuseum
{
    /// <summary>
    /// Per-frame visibility for paintings in the active wings (Unity's frustum culling can't see walls).
    ///
    /// RoomCull: a room's paintings can only be seen through its doorway. From outside the room, a painting is
    /// drawn only if its wall segment falls inside the 2D wedge from the eye through the (slightly widened)
    /// doorway. Walls and the door sign always draw, so nothing ever shows a hole; only paintings, frames,
    /// plaques, stars and text are skipped. This is conservative: it ignores other walls that might also block.
    ///
    /// TextCull: plaque, badge and star text is hidden beyond reading distance (with hysteresis).
    /// </summary>
    public class MuseumCulling : MonoBehaviour
    {
        public float textShowDistance = 6f;
        public float textHideDistance = 7.5f;

        const float DoorMargin = 0.3f;      // widen doorways: head motion within a frame, frame thickness
        const float PaintingMargin = 0.12f; // frame bars beyond the canvas

        MuseumBuilder _builder;
        Transform _head;
        readonly Dictionary<MuseumBuilder.RoomInfo, (Vector2 a, Vector2 b)[]> _segments =
            new Dictionary<MuseumBuilder.RoomInfo, (Vector2, Vector2)[]>();

        public static MuseumCulling Create(MuseumBuilder builder, Transform head)
        {
            var go = new GameObject("MuseumCulling");
            var c = go.AddComponent<MuseumCulling>();
            c._builder = builder;
            c._head = head;
            return c;
        }

        void LateUpdate()
        {
            PaintingView.UpdateAllSurfaces();
            if (_builder == null || _head == null) return;

            bool roomCull = PerfModes.Has(PerfFix.RoomCull);
            bool textCull = PerfModes.Has(PerfFix.TextCull);
            Vector3 eye = _head.position;
            float show2 = textShowDistance * textShowDistance, hide2 = textHideDistance * textHideDistance;

            foreach (var wing in _builder.Wings)
            {
                if (!wing.root.activeSelf) continue;
                var t = wing.root.transform;
                Vector3 local = t.InverseTransformPoint(eye);
                var cam = new Vector2(local.x, local.z);

                foreach (var room in wing.rooms)
                {
                    var segs = Segments(t, room);
                    var view = roomCull ? Doorway.Classify(cam, room.doorA, room.inward, room.area, DoorMargin) : Doorway.View.All;
                    for (int i = 0; i < room.paintings.Count; i++)
                    {
                        var p = room.paintings[i];
                        bool rendered = view == Doorway.View.All ||
                                        (view == Doorway.View.ThroughDoor &&
                                         Doorway.SeesThrough(cam, room.doorA, room.doorB, room.inward, segs[i].a, segs[i].b, DoorMargin));
                        bool text = true;
                        if (textCull)
                        {
                            float d2 = (p.transform.position - eye).sqrMagnitude;
                            text = d2 < show2 || (p.TextRendered && d2 < hide2);
                        }
                        p.SetRendered(rendered, text);
                    }
                }
            }
        }

        (Vector2 a, Vector2 b)[] Segments(Transform wing, MuseumBuilder.RoomInfo room)
        {
            if (_segments.TryGetValue(room, out var segs) && segs.Length == room.paintings.Count) return segs;
            segs = new (Vector2, Vector2)[room.paintings.Count];
            for (int i = 0; i < segs.Length; i++)
            {
                var p = room.paintings[i];
                Vector3 c = wing.InverseTransformPoint(p.transform.position);
                Vector3 r = wing.InverseTransformDirection(p.transform.right);
                var along = new Vector2(r.x, r.z).normalized * (p.size.x / 2 + PaintingMargin);
                var center = new Vector2(c.x, c.z);
                segs[i] = (center - along, center + along);
            }
            _segments[room] = segs;
            return segs;
        }
    }
}
