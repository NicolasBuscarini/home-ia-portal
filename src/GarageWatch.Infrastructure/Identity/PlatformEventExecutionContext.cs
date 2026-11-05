using GarageWatch.Application.PlatformEvents;

namespace GarageWatch.Infrastructure.Identity;

/// <summary>
/// Ambient actor for flows that are not driven by an HTTP request. The value lives in an
/// <see cref="AsyncLocal{T}"/> so concurrent scopes do not leak into each other, and disposing the
/// scope restores the previous actor (including nested scopes). It never reads the event draft.
/// </summary>
public sealed class PlatformEventExecutionContext : IPlatformEventExecutionContext
{
    private static readonly AsyncLocal<PlatformEventExecutionActor?> Ambient = new();

    public PlatformEventExecutionActor? Current => Ambient.Value;

    public IDisposable BeginActor(Guid userId)
    {
        var previous = Ambient.Value;
        Ambient.Value = new PlatformEventExecutionActor(userId);
        return new Scope(previous);
    }

    private sealed class Scope(PlatformEventExecutionActor? previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Ambient.Value = previous;
        }
    }
}
