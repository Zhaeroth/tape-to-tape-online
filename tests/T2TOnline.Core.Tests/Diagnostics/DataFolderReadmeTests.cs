using System;
using System.Linq;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class DataFolderReadmeTests
{
    public static TheoryData<string> Texts => new()
    {
        DataFolderReadme.GameFolderText,
        DataFolderReadme.DataFolderText,
        DataFolderReadme.SharedFolderText,
        DataFolderReadme.DevNoticeText,
    };

    [Theory]
    [MemberData(nameof(Texts))]
    public void Each_text_is_plain_ascii_with_windows_line_ends_and_short_lines(string text)
    {
        Assert.All(text, c => Assert.True(c is '\r' or '\n' || (c >= ' ' && c <= '~'), $"U+{(int)c:X4}"));
        Assert.EndsWith("\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.All(text.Split("\r\n"), line => Assert.True(line.Length <= 80, line));
    }

    [Fact]
    public void The_data_folder_says_what_deleting_loses_and_that_nothing_is_sent()
    {
        string text = DataFolderReadme.DataFolderText;
        Assert.Contains("co-op runs", text, StringComparison.Ordinal);
        Assert.Contains("backups", text, StringComparison.Ordinal);
        Assert.Contains("for good", text, StringComparison.Ordinal);
        Assert.Contains("Nothing in this folder is", text, StringComparison.Ordinal);
        Assert.Contains("sent anywhere", text, StringComparison.Ordinal);
        Assert.Contains("Settings > Online > Problem Report", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_texts_that_list_the_progress_folder_name_its_secret_key_file()
    {
        // D135: the daily limits per host are kept under this PC's own key, in the progress folder.
        foreach (string text in new[] { DataFolderReadme.DataFolderText, DataFolderReadme.SharedFolderText })
        {
            Assert.Contains("host-key.bin", text, StringComparison.Ordinal);
            Assert.Contains("never share it", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_game_folder_says_what_it_holds_and_points_to_the_other()
    {
        string text = DataFolderReadme.GameFolderText;
        foreach (string part in new[] { "settings.cfg", "logs\\", "dev\\", "Nothing in it is sent anywhere", "Settings > Online > Problem Report", "%LOCALAPPDATA%\\T2T Online" })
        {
            Assert.Contains(part, text, StringComparison.Ordinal);
        }

        string shared = DataFolderReadme.SharedFolderText;
        Assert.StartsWith(text[..text.IndexOf("With the game closed", StringComparison.Ordinal)], shared, StringComparison.Ordinal);
        Assert.Contains("co-op runs", shared, StringComparison.Ordinal);
        Assert.Contains("for good", shared, StringComparison.Ordinal);
    }

    [Fact]
    public void The_developer_notice_warns_about_the_friends_list()
    {
        Assert.Contains("Steam friends", DataFolderReadme.DevNoticeText, StringComparison.Ordinal);
        Assert.Contains("ui-check", DataFolderReadme.DevNoticeText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_readme_is_written_only_when_missing_or_different()
    {
        string text = DataFolderReadme.GameFolderText;
        Assert.True(DataFolderReadme.NeedsWrite(null, text));
        Assert.True(DataFolderReadme.NeedsWrite(text.Replace("\r\n", "\n", StringComparison.Ordinal), text));
        Assert.False(DataFolderReadme.NeedsWrite(new string(text.ToArray()), text));
    }
}
