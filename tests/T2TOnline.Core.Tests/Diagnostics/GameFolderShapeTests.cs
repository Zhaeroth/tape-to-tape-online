using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class GameFolderShapeTests
{
    [Fact]
    public void A_default_steam_install_is_described_without_its_path()
    {
        string line = GameFolderShape.Describe(@"C:\Program Files (x86)\Steam\steamapps\common\Tape to Tape", @"C:\Users\alex", "fixed");
        Assert.Equal("Game folder: a Steam library on a fixed drive, under Program Files, outside the user profile, ASCII, 58 characters.", line);
        Assert.DoesNotContain(@"C:\", line);
    }

    [Fact]
    public void A_library_inside_the_profile_says_so_but_not_whose()
    {
        string line = GameFolderShape.Describe(@"C:\Users\alex\OneDrive\SteamLibrary\steamapps\common\Tape to Tape\", @"C:\Users\alex", null);
        Assert.Equal("Game folder: a Steam library, inside the user profile, in OneDrive, ASCII, 65 characters.", line);
        Assert.DoesNotContain("alex", line);
    }

    [Fact]
    public void Other_folders_shares_and_non_ascii_paths_are_described()
    {
        Assert.Equal("Game folder: not in a Steam library on a network drive, on a network share, outside the user profile, with non-ASCII characters, 18 characters.",
            GameFolderShape.Describe("\\\\nas\\games\\T\u00e4pe\\x", @"C:\Users\alex", "network"));
        Assert.Equal("Game folder: not in a Steam library, the user profile unknown, ASCII, 8 characters.", GameFolderShape.Describe("E:/Other", null, string.Empty));
        Assert.Equal("Game folder: unknown.", GameFolderShape.Describe(" ", null, null));
    }
}
