using PilahDok.Core.Abstractions;

namespace PilahDok.Core.Services;

public sealed class UnavailablePaginationEngine : IWordPaginationEngine
{
    public string ProviderName => "none";
    public bool IsAvailable => false;
}
