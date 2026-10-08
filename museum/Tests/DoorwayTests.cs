// Run: dotnet test museum/Tests. Doorway culling geometry for one north device room as MuseumBuilder lays it out:
// x0 = 1, 7 m wide, doorway x 3.3–5.7 on the corridor wall z = 2, back wall z = 9. Corridor is z -2..2.
using RHMuseum;
using UnityEngine;
using Xunit;

public class DoorwayTests
{
    const float Margin = 0.3f;
    static readonly Vector2 DoorA = new Vector2(3.3f, 2), DoorB = new Vector2(5.7f, 2), Inward = new Vector2(0, 1);
    static readonly Rect Area = Rect.MinMaxRect(1, 2, 8, 9);

    // Painting wall segments (canvas half-width 0.8 + frame).
    static readonly (Vector2, Vector2) Back = (new Vector2(3.58f, 8.83f), new Vector2(5.42f, 8.83f));
    static readonly (Vector2, Vector2) LeftNear = (new Vector2(1.17f, 3.18f), new Vector2(1.17f, 5.02f));
    static readonly (Vector2, Vector2) RightFar = (new Vector2(7.83f, 5.84f), new Vector2(7.83f, 7.68f));

    static bool Sees(float x, float z, (Vector2 a, Vector2 b) seg) =>
        Doorway.SeesThrough(new Vector2(x, z), DoorA, DoorB, Inward, seg.a, seg.b, Margin);

    static Doorway.View Classify(float x, float z) => Doorway.Classify(new Vector2(x, z), DoorA, Inward, Area, Margin);

    [Fact]
    public void FacingTheDoorwaySeesEveryWall()
    {
        Assert.True(Sees(4.5f, 0, Back));
        Assert.True(Sees(4.5f, 0, LeftNear));
        Assert.True(Sees(4.5f, 0, RightFar));
    }

    [Fact]
    public void FarDownTheCorridorAlongTheSameWallSeesNothing()
    {
        Assert.False(Sees(20, 1.5f, Back));
        Assert.False(Sees(20, 1.5f, LeftNear));
        Assert.False(Sees(20, 1.5f, RightFar));
    }

    [Fact]
    public void FromTheOppositeCorridorWallTheFarSideWallShowsAtAGrazingAngle()
    {
        // Line from (20,-1.8) through the widened doorway reaches x = 1.17 at z ≈ 2.4–3.4: the near-left painting.
        Assert.True(Sees(20, -1.8f, LeftNear));
        Assert.False(Sees(20, -1.8f, RightFar));
        Assert.False(Sees(20, -1.8f, Back));
    }

    [Fact]
    public void JustBesideTheDoorwaySeesTheFarSideWall()
    {
        Assert.True(Sees(7, 1.5f, LeftNear));
        Assert.False(Sees(7, 1.5f, RightFar));   // the wall on your own side is behind the door jamb
    }

    [Fact]
    public void ClassifiesInsideCorridorNeighbourAndDoorway()
    {
        Assert.Equal(Doorway.View.All, Classify(4, 5));
        Assert.Equal(Doorway.View.ThroughDoor, Classify(4, 0));
        Assert.Equal(Doorway.View.None, Classify(12, 5));       // next room along: walls in between
        Assert.Equal(Doorway.View.All, Classify(4.5f, 2.1f));   // standing in the doorway
        Assert.Equal(Doorway.View.All, Classify(9, 2.1f));      // on the wall line, outside the span: conservative
    }
}
