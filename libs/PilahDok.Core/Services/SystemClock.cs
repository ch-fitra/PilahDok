using PilahDok.Core.Abstractions;

namespace PilahDok.Core.Services;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
