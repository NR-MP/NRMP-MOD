using System.Numerics;

namespace NightRunnersMP.Shared;

// game's shifted local coords <-> true world coords. ApplyWorldShift moves the world by a delta and takes it off the offset,
// so local + offset stays put through a shift
public sealed class OriginFrame
{
    public Vector3 ToWorld(Vector3 local, Vector3 offset) => local + offset;

    public Vector3 ToLocal(Vector3 world, Vector3 offset) => world - offset;
}
