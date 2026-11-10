using System.Reflection;
using MediaBrowser.Model.Globalization;

// Minimal stand-in for Jellyfin's LocalizationManager: FindLanguageInfo for a few languages, like its iso6392 table.
public class FakeLocalization : DispatchProxy
{
    private static readonly (string Two, string Name, string[] Three)[] Languages =
    {
        ("zh", "Chinese", new[] { "zho", "chi" }), ("en", "English", new[] { "eng" }), ("fr", "French", new[] { "fra", "fre" }),
        ("ja", "Japanese", new[] { "jpn" }), ("es", "Spanish", new[] { "spa" }),
        ("zh-CN", "Chinese (Simplified)", new[] { "zho" }), ("zh-Hans", "Chinese (Simplified Han)", new[] { "zho" }),
    };

    public static ILocalizationManager Create() => Create<ILocalizationManager, FakeLocalization>();

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        if (targetMethod.Name == nameof(ILocalizationManager.FindLanguageInfo))
        {
            var language = args[0]?.ToString();
            foreach (var (two, name, three) in Languages)
            {
                if (string.Equals(language, two, StringComparison.OrdinalIgnoreCase) || string.Equals(language, name, StringComparison.OrdinalIgnoreCase)
                    || three.Contains(language, StringComparer.OrdinalIgnoreCase))
                {
                    return new CultureDto(name, name, two, three);
                }
            }

            return null;
        }

        if (targetMethod.Name == nameof(ILocalizationManager.GetCultures))
        {
            return Languages.Select(l => new CultureDto(l.Name, l.Name, l.Two, l.Three)).ToArray();
        }

        if (targetMethod.Name == nameof(ILocalizationManager.TryGetISO6392TFromB))
        {
            args[1] = null;
            return false;
        }

        throw new NotSupportedException(targetMethod.Name);
    }
}
