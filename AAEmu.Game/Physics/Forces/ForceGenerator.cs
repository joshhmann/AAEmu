using Jitter2;

namespace AAEmu.Game.Physics.Forces;

/// <summary>
/// Base class for physic effect.
/// </summary>
public class ForceGenerator
{
    protected World _world;

    private readonly World.WorldStep _preStep;
    private readonly World.WorldStep _postStep;

    /// <summary>
    /// Number of live <see cref="ForceGenerator"/> instances (diagnostics for
    /// physics telemetry). Incremented on construction, decremented on
    /// <see cref="RemoveEffect"/>. Interlocked so the physics thread can read it
    /// without a lock.
    /// </summary>
    private static int s_activeCount;

    /// <summary>Current number of live force generators (diagnostics).</summary>
    public static int ActiveCount => Volatile.Read(ref s_activeCount);

    public ForceGenerator(World world)
    {
        this._world = world;

        // ReSharper disable RedundantDelegateCreation
        _preStep = new World.WorldStep(PreStep);
        _postStep = new World.WorldStep(PostStep);
        // ReSharper enable RedundantDelegateCreation

        world.PostStep += _postStep;
        world.PreStep += _preStep;

        Interlocked.Increment(ref s_activeCount);
    }

    public virtual void PreStep(float timeStep)
    {
    }

    public virtual void PostStep(float timeStep)
    {
    }

    public void RemoveEffect()
    {
        _world.PostStep -= _postStep;
        _world.PreStep -= _preStep;
        Interlocked.Decrement(ref s_activeCount);
    }
}
