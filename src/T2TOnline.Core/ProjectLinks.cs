namespace T2TOnline.Core;

/// <summary>
/// Where the project lives online: the one place its address is written in code. Everything a player reads (a word, a prompt, a
/// notice) takes it from here as an argument, and nothing the mod writes to a log or a report holds it. Properties, not
/// constants: the compiler copies a constant into every assembly that reads it, and the address is to be in this assembly only.
/// </summary>
public static class ProjectLinks
{
    private const string RepositoryAddress = "https://github.com/Zhaeroth/tape-to-tape-online";

    /// <summary>The project's page on GitHub, with no trailing slash.</summary>
    public static string Repository => RepositoryAddress;

    /// <summary>Where a problem is reported: the project's issues.</summary>
    public static string Issues => RepositoryAddress + "/issues";

    /// <summary>Where the releases are listed.</summary>
    public static string Releases => RepositoryAddress + "/releases";

    /// <summary>The newest release's own page: the download for a player whose game was updated after the mod's release.</summary>
    public static string LatestRelease => RepositoryAddress + "/releases/latest";

    /// <summary>
    /// <see cref="Issues"/> as a player reads it on a screen: no scheme, so it fits a line and cannot be mistaken for something to click.
    /// Plain letters, digits and <c>.</c>, <c>/</c>, <c>-</c>: <c>tools/checks/lobbywords_check.py</c> proves every one is drawn by the game's fonts in
    /// all 15 languages.
    /// </summary>
    public static string IssuesShort => "github.com/Zhaeroth/tape-to-tape-online/issues";

    /// <summary><see cref="Releases"/> as a player reads it on a screen (see <see cref="IssuesShort"/>).</summary>
    public static string ReleasesShort => "github.com/Zhaeroth/tape-to-tape-online/releases";
}
