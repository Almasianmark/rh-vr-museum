using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace RHMuseum
{
    /// <summary>
    /// Procedural greybox layout from museum.json.
    ///
    ///   Lobby (origin) ── spine corridor north (+Z) ── wings branch east/west in pairs, one per year.
    ///   Wing: corridor along its local +X, device exhibit rooms on both sides (≤5 paintings each),
    ///         the year's archive hall (uncapped, Watch-only) at the far end.
    /// </summary>
    public class MuseumBuilder
    {
        public const float RoomW = 7f, RoomD = 7f, WallH = 4f, CorridorW = 4f, SpineW = 6f;
        const float LobbyHalf = 8f, SlotGap = 2f, WallInset = 0.17f;

        public readonly Dictionary<string, PaintingView> Paintings = new Dictionary<string, PaintingView>();
        public readonly List<WingInfo> Wings = new List<WingInfo>();
        public Vector3 SpawnPoint { get; private set; }
        public float SpawnYaw { get; private set; }

        public class WingInfo
        {
            public Wing data;
            public GameObject root;
            public Bounds bounds;   // world space
            public readonly List<PaintingView> paintings = new List<PaintingView>();
            public readonly List<RoomInfo> rooms = new List<RoomInfo>();
        }

        /// <summary>A room you can only see into through one doorway. 2D, in wing-local XZ (Vector2 = x, z).</summary>
        public class RoomInfo
        {
            public Vector2 doorA, doorB;   // doorway edges
            public Vector2 inward;         // unit normal from the doorway into the room
            public Rect area;              // room floor (x, z)
            public readonly List<PaintingView> paintings = new List<PaintingView>();
        }

        readonly MuseumDoc _doc;
        readonly Transform _root;

        public MuseumBuilder(MuseumDoc doc, Transform root)
        {
            _doc = doc;
            _root = root;
        }

        public void Build()
        {
            BuildLobby();

            // Pair wings into slots along the spine: even index east, odd index west.
            float z = LobbyHalf + SlotGap;
            for (int i = 0; i < _doc.wings.Count; i += 2)
            {
                float halfDepth = Mathf.Max(WingHalfDepth(_doc.wings[i]),
                                            i + 1 < _doc.wings.Count ? WingHalfDepth(_doc.wings[i + 1]) : 0);
                float centerZ = z + halfDepth;
                BuildWing(_doc.wings[i], centerZ, east: true);
                if (i + 1 < _doc.wings.Count) BuildWing(_doc.wings[i + 1], centerZ, east: false);
                z = centerZ + halfDepth + SlotGap;
            }
            BuildSpine(LobbyHalf, z);
        }

        // ------------------------------------------------------------------ lobby + spine

        void BuildLobby()
        {
            var t = new GameObject("Lobby").transform;
            t.SetParent(_root, false);
            Greybox.Floor(t, new Rect(-LobbyHalf, -LobbyHalf, LobbyHalf * 2, LobbyHalf * 2), Palette.Floor);
            Greybox.WallX(t, -LobbyHalf, LobbyHalf, LobbyHalf, WallH, Palette.Wall, new[] { 0f }, SpineW, 3.2f);
            Greybox.WallX(t, -LobbyHalf, LobbyHalf, -LobbyHalf, WallH, Palette.Wall);
            Greybox.WallZ(t, -LobbyHalf, LobbyHalf, -LobbyHalf, WallH, Palette.Wall);
            Greybox.WallZ(t, -LobbyHalf, LobbyHalf, LobbyHalf, WallH, Palette.Wall);

            int total = _doc.projects.Count;
            int exhibits = _doc.wings.Sum(w => w.exhibits.Count);
            var title = Greybox.Label(t, "<b>Reality Hack VR Museum</b>",
                new Vector3(0, 3.55f, LobbyHalf - 0.12f), Quaternion.identity, 4.5f, 12f, Palette.Text, TextAlignmentOptions.Center, 0.9f);
            title.fontStyle = FontStyles.Bold;
            Greybox.Label(t, $"{total} projects · {_doc.wings.Count} years · {exhibits} exhibits\n" +
                             "Touch a painting to make it ripple. Push your hand through to jump in.",
                new Vector3(0, 2.0f, LobbyHalf - 0.12f), Quaternion.identity, 1.4f, 9f, Palette.Text, TextAlignmentOptions.Center, 1f);

            // Fidelity legend on the west wall
            var lines = new[] { "Native", "Ported", "Ported-reduced", "Simulated", "Browser", "Watch" }
                .Select(f => $"<color=#{ColorUtility.ToHtmlStringRGB(Palette.Fidelity(f))}>■</color> <b>{f}</b>  {Palette.FidelityBlurb(f)}");
            Greybox.Label(t, "<b>Frame colors</b>\n" + string.Join("\n", lines),
                new Vector3(-LobbyHalf + 0.12f, 1.9f, 0), Quaternion.Euler(0, -90, 0), 1.2f, 7f, Palette.Text, TextAlignmentOptions.TopLeft, 3f);

            // Wing directory on the east wall
            var dir = _doc.wings.Select(w =>
                $"<b>{w.title}</b>  {w.project_count} projects · {w.exhibits.Count(e => !e.IsArchive)} device rooms" +
                (w.exhibits.Any(e => e.IsArchive) ? $" + archive ({w.exhibits.First(e => e.IsArchive).project_ids.Count})" : ""));
            Greybox.Label(t, "<b>Wings</b>\n" + string.Join("\n", dir),
                new Vector3(LobbyHalf - 0.12f, 1.9f, 0), Quaternion.Euler(0, 90, 0), 1.2f, 7f, Palette.Text, TextAlignmentOptions.TopLeft, 3f);

            // Top-rated board on the south wall (behind spawn), live from the ratings backend.
            TopRatedBoard.Create(t, _doc, new Vector3(0, 3.3f, -LobbyHalf + 0.12f), Quaternion.Euler(0, 180, 0));

            SpawnPoint = new Vector3(0, 0, -LobbyHalf + 3f);
            SpawnYaw = 0;
        }

        readonly List<(float z, bool east)> _spineOpenings = new List<(float, bool)>();

        void BuildSpine(float z0, float z1)
        {
            var t = new GameObject("Spine").transform;
            t.SetParent(_root, false);
            Greybox.Floor(t, new Rect(-SpineW / 2, z0, SpineW, z1 - z0), Palette.Floor);
            // Spine walls run along Z at x = ±SpineW/2, with openings where wings attach.
            Greybox.WallZ(t, z0, z1, SpineW / 2, WallH, Palette.Wall,
                _spineOpenings.Where(o => o.east).Select(o => o.z).ToList(), CorridorW, 3.0f);
            Greybox.WallZ(t, z0, z1, -SpineW / 2, WallH, Palette.Wall,
                _spineOpenings.Where(o => !o.east).Select(o => o.z).ToList(), CorridorW, 3.0f);
            Greybox.WallX(t, -SpineW / 2, SpineW / 2, z1, WallH, Palette.Wall);
        }

        // ------------------------------------------------------------------ wings

        static int DeviceRooms(Wing w) => w.exhibits.Count(e => !e.IsArchive);
        static Exhibit Archive(Wing w) => w.exhibits.FirstOrDefault(e => e.IsArchive);

        static float ArchiveSide(int n)
        {
            // Two tiers of compact paintings on back + side walls (+ both halves of the front wall).
            float side = 10f;
            while (Capacity(side) < n) side += 1.5f;
            return side;
        }

        const float ArchivePitch = 1.45f, ArchiveMargin = 0.9f;

        static int PerWall(float len) => Mathf.Max(0, Mathf.FloorToInt((len - 2 * ArchiveMargin) / ArchivePitch) + 1);

        static int Capacity(float side) =>
            2 * (3 * PerWall(side) + 2 * PerWall((side - CorridorW) / 2));

        static float WingHalfDepth(Wing w)
        {
            float rooms = CorridorW / 2 + RoomD;
            var a = Archive(w);
            return Mathf.Max(rooms, a != null ? ArchiveSide(a.project_ids.Count) / 2 : 0);
        }

        void BuildWing(Wing wing, float centerZ, bool east)
        {
            var root = new GameObject($"Wing {wing.title}");
            root.transform.SetParent(_root, false);
            root.transform.localPosition = new Vector3(east ? SpineW / 2 : -SpineW / 2, 0, centerZ);
            root.transform.localRotation = Quaternion.Euler(0, east ? 0 : 180, 0);
            _spineOpenings.Add((centerZ, east));
            var t = root.transform;
            var info = new WingInfo { data = wing, root = root };

            var deviceExhibits = wing.exhibits.Where(e => !e.IsArchive).ToList();
            int columns = Mathf.CeilToInt(deviceExhibits.Count / 2f);
            const float roomsStart = 1f;
            float corridorEnd = roomsStart + columns * RoomW + 1f;
            float zc = CorridorW / 2;

            // Corridor side walls with a doorway per room
            var northDoors = new List<float>();
            var southDoors = new List<float>();
            for (int i = 0; i < deviceExhibits.Count; i++)
            {
                float cx = roomsStart + (i / 2) * RoomW + RoomW / 2;
                (i % 2 == 0 ? northDoors : southDoors).Add(cx);
            }
            Greybox.WallX(t, 0, corridorEnd, zc, WallH, Palette.Wall, northDoors);
            Greybox.WallX(t, 0, corridorEnd, -zc, WallH, Palette.Wall, southDoors);

            // Rooms
            for (int i = 0; i < deviceExhibits.Count; i++)
            {
                int col = i / 2;
                bool north = i % 2 == 0;
                BuildDeviceRoom(t, info, deviceExhibits[i], roomsStart + col * RoomW, north);
            }

            // Year sign over the spine opening + floor
            var yearSign = Greybox.Label(t, $"<b>{wing.title}</b>", new Vector3(-0.15f, 3.45f, 0), Quaternion.Euler(0, 90, 0),
                6f, CorridorW, Palette.Text, TextAlignmentOptions.Center, 1f);
            yearSign.fontStyle = FontStyles.Bold;

            float halfDepth = WingHalfDepth(wing);
            float farEnd = corridorEnd;
            var archive = Archive(wing);
            if (archive != null)
            {
                float side = ArchiveSide(archive.project_ids.Count);
                BuildArchive(t, info, archive, corridorEnd, side);
                farEnd = corridorEnd + side;
            }
            else
            {
                Greybox.WallZ(t, -zc, zc, corridorEnd, WallH, Palette.Wall);
            }
            Greybox.Floor(t, new Rect(0, -halfDepth, farEnd, halfDepth * 2), Palette.Floor);

            // World-space bounds for streaming
            var b = new Bounds(t.TransformPoint(new Vector3(farEnd / 2, WallH / 2, 0)), Vector3.zero);
            foreach (var corner in new[] { new Vector3(0, 0, -halfDepth), new Vector3(farEnd, WallH, halfDepth) })
                b.Encapsulate(t.TransformPoint(corner));
            info.bounds = b;
            Wings.Add(info);
        }

        void BuildDeviceRoom(Transform wing, WingInfo info, Exhibit ex, float x0, bool north)
        {
            float s = north ? 1 : -1;          // room extends toward +Z (north) or -Z (south) in wing space
            float zFront = s * CorridorW / 2, zBack = s * (CorridorW / 2 + RoomD);
            var room = new GameObject($"Exhibit {ex.id}").transform;
            room.SetParent(wing, false);
            const float doorHalf = 1.2f;   // WallX's default door width / 2
            var cell = new RoomInfo
            {
                doorA = new Vector2(x0 + RoomW / 2 - doorHalf, zFront),
                doorB = new Vector2(x0 + RoomW / 2 + doorHalf, zFront),
                inward = new Vector2(0, s),
                area = Rect.MinMaxRect(x0, Mathf.Min(zFront, zBack), x0 + RoomW, Mathf.Max(zFront, zBack)),
            };
            info.rooms.Add(cell);

            Greybox.WallX(room, x0, x0 + RoomW, zBack, WallH, Palette.Wall);
            float za = Mathf.Min(zFront, zBack), zb = Mathf.Max(zFront, zBack);
            Greybox.WallZ(room, za, zb, x0, WallH, Palette.Wall);
            Greybox.WallZ(room, za, zb, x0 + RoomW, WallH, Palette.Wall);

            // Door sign, readable from the corridor
            float cx = x0 + RoomW / 2;
            Quaternion faceRoom = Quaternion.Euler(0, north ? 0 : 180, 0);
            Greybox.Label(room, $"<b>{ex.title}</b>\n<size=55%>{ex.subtitle}</size>",
                new Vector3(cx, 3.35f, zFront - s * 0.12f), faceRoom, 2.0f, RoomW - 1f, Palette.Text, TextAlignmentOptions.Center, 1.1f);

            // Slots: back wall, then the far and near spots on each side wall.
            float midY = 1.65f;
            var slots = new List<(Vector3 pos, Quaternion rot)>
            {
                (new Vector3(cx, midY, zBack - s * WallInset), faceRoom),
                (new Vector3(x0 + WallInset, midY, zFront + s * RoomD * 0.68f), Quaternion.Euler(0, -90, 0)),
                (new Vector3(x0 + RoomW - WallInset, midY, zFront + s * RoomD * 0.68f), Quaternion.Euler(0, 90, 0)),
                (new Vector3(x0 + WallInset, midY, zFront + s * RoomD * 0.30f), Quaternion.Euler(0, -90, 0)),
                (new Vector3(x0 + RoomW - WallInset, midY, zFront + s * RoomD * 0.30f), Quaternion.Euler(0, 90, 0)),
            };
            for (int i = 0; i < ex.project_ids.Count && i < slots.Count; i++)
                AddPainting(room, info, cell, ex.project_ids[i], slots[i].pos, slots[i].rot, 1.6f, false);
        }

        void BuildArchive(Transform wing, WingInfo info, Exhibit ex, float x0, float side)
        {
            var room = new GameObject($"Exhibit {ex.id}").transform;
            room.SetParent(wing, false);
            float h = side / 2, x1 = x0 + side, zc = CorridorW / 2;
            var cell = new RoomInfo
            {
                doorA = new Vector2(x0, -zc),
                doorB = new Vector2(x0, zc),
                inward = new Vector2(1, 0),
                area = Rect.MinMaxRect(x0, -h, x1, h),
            };
            info.rooms.Add(cell);

            Greybox.WallZ(room, -h, h, x1, WallH, Palette.ArchiveWall);              // back
            Greybox.WallX(room, x0, x1, h, WallH, Palette.ArchiveWall);              // north
            Greybox.WallX(room, x0, x1, -h, WallH, Palette.ArchiveWall);             // south
            Greybox.WallZ(room, -h, h, x0, WallH, Palette.ArchiveWall, new[] { 0f }, CorridorW, 3.0f);  // front with door

            Greybox.Label(room, $"<b>{ex.title}</b>\n<size=50%>{ex.subtitle}</size>",
                new Vector3(x0 - 0.12f, 3.4f, 0), Quaternion.Euler(0, 90, 0), 2.2f, CorridorW + 2f, Palette.Text, TextAlignmentOptions.Center, 1.2f);

            // Wall runs: (start, direction along wall, into-wall rotation, length)
            var runs = new List<(Vector3 start, Vector3 dir, Quaternion rot, float len)>
            {
                (new Vector3(x1 - WallInset, 0, h), Vector3.back, Quaternion.Euler(0, 90, 0), side),             // back wall, north→south
                (new Vector3(x0, 0, h - WallInset), Vector3.right, Quaternion.Euler(0, 0, 0), side),             // north wall
                (new Vector3(x0, 0, -h + WallInset), Vector3.right, Quaternion.Euler(0, 180, 0), side),          // south wall
                (new Vector3(x0 + WallInset, 0, h), Vector3.back, Quaternion.Euler(0, -90, 0), h - zc),          // front wall, north half
                (new Vector3(x0 + WallInset, 0, -zc), Vector3.back, Quaternion.Euler(0, -90, 0), h - zc),        // front wall, south half
            };
            float[] tiers = { 1.2f, 2.45f };
            int k = 0;
            foreach (float y in tiers)
                foreach (var run in runs)
                {
                    int n = PerWall(run.len);
                    for (int i = 0; i < n && k < ex.project_ids.Count; i++, k++)
                    {
                        Vector3 pos = run.start + run.dir * (ArchiveMargin + i * ArchivePitch) + Vector3.up * y;
                        AddPainting(room, info, cell, ex.project_ids[k], pos, run.rot, 1.1f, true);
                    }
                }
        }

        void AddPainting(Transform parent, WingInfo info, RoomInfo cell, string projectId, Vector3 localPos, Quaternion rot,
                         float width, bool compact)
        {
            var p = _doc.Project(projectId);
            if (p == null) return;
            var view = PaintingView.Create(parent, p, localPos, rot, width, compact);
            Paintings[p.id] = view;
            info.paintings.Add(view);
            cell.paintings.Add(view);
        }

        /// <summary>Where to stand to look at a painting: 1.6 m in front of it.</summary>
        public static (Vector3 pos, float yaw) ViewpointFor(PaintingView v)
        {
            Vector3 fwd = Vector3.ProjectOnPlane(v.transform.forward, Vector3.up).normalized;   // into the wall
            Vector3 p = v.transform.position - fwd * 1.6f;
            return (new Vector3(p.x, 0, p.z), Quaternion.LookRotation(fwd).eulerAngles.y);
        }
    }
}
