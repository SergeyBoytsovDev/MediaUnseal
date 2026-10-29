using System.Linq;

namespace TrackExtractor.Services;

// A display value can be rendered again after the interface culture changes.
public sealed record LocalizedMessage(string Key, params object?[] Arguments)
{
    internal string Resolve() => Arguments.Length == 0
        ? LocalizationManager.Active.Get(Key)
        : LocalizationManager.Active.Format(Key,
            Arguments.Select(arg => arg is LocalizedMessage nested ? nested.Resolve() : arg).ToArray());
}
