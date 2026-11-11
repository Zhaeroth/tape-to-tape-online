using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class SafeErrorTests
{
    private readonly LogScrub _scrub = new(LogTags.ForLaunch());

    public SafeErrorTests()
    {
        _scrub.AddFolders(new Dictionary<string, string>
        {
            [@"C:\Users\alex\AppData\Local\T2T Online"] = @"%LOCALAPPDATA%\T2T Online",
            [@"C:\Users\alex"] = "%USERPROFILE%",
            [@"C:\Program Files (x86)\Steam\steamapps\common\Tape to Tape"] = "<game folder>",
        });
    }

    [Fact]
    public void A_file_error_keeps_its_type_and_message_with_the_folder_labelled()
    {
        var ex = new FileNotFoundException(@"Could not find file 'C:\Users\alex\AppData\Local\T2T Online\coop\coop-1.es3'.");
        Assert.Equal(@"FileNotFoundException: Could not find file '%LOCALAPPDATA%\T2T Online\coop\coop-1.es3'.", SafeError.Text(ex, _scrub));
    }

    [Fact]
    public void A_quoted_path_outside_the_known_folders_keeps_only_its_file_name()
    {
        var ex = new IOException(@"The process cannot access the file 'E:\Games\Other\HostRun.es3' because it is being used by another process.");
        Assert.Equal(@"IOException: The process cannot access the file '<folder>\HostRun.es3' because it is being used by another process.",
            SafeError.Text(ex, _scrub));
        Assert.Equal(@"IOException: Access to the path ""<folder>\x"" is denied.",
            SafeError.Text(new IOException(@"Access to the path ""\\server\share\x"" is denied."), _scrub));
    }

    [Fact]
    public void It_is_one_line_of_at_most_160_characters()
    {
        string text = SafeError.Text(new InvalidOperationException("first line\nsecond line " + new string('x', 300)), _scrub);
        Assert.DoesNotContain('\n', text);
        Assert.Equal(SafeError.MaxLength, text.Length);
        Assert.StartsWith("InvalidOperationException: first line?second line", text);
        Assert.EndsWith("\u2026", text);
    }

    [Fact]
    public void A_wrapper_shows_what_it_wraps_and_an_inner_exception_is_named()
    {
        var wrapped = new TargetInvocationException(new ArgumentException("bad seat"));
        Assert.Equal("ArgumentException: bad seat", SafeError.Text(wrapped, _scrub));
        var outer = new InvalidOperationException("load failed", new IOException(@"disk full at C:\Users\alex\x"));
        Assert.Equal(@"InvalidOperationException: load failed (inner IOException: disk full at %USERPROFILE%\x)", SafeError.Text(outer, _scrub));
    }

    [Fact]
    public void Without_a_scrubber_every_quoted_path_keeps_only_its_file_name()
    {
        // Core's parts: the plugin's backstop labels folders and tags IDs in the line afterwards.
        Assert.Equal(@"FileNotFoundException: Could not find file '<folder>\coop-1.es3'.",
            SafeError.Text(new FileNotFoundException(@"Could not find file 'C:\Users\alex\AppData\Local\T2T Online\coop\coop-1.es3'.")));
        Assert.Equal("InvalidOperationException: derived", SafeError.Text(new InvalidOperationException("derived")));
        Assert.Equal(string.Empty, SafeError.Text(null));
    }

    [Fact]
    public void Ids_in_a_message_become_tags_and_null_gives_empty()
    {
        string text = SafeError.Text(new InvalidOperationException("no seat for 76561199000000001"), _scrub);
        Assert.Matches("^InvalidOperationException: no seat for P#[0-9a-f]{6}$", text);
        Assert.Equal(string.Empty, SafeError.Text(null, _scrub));
    }
}
