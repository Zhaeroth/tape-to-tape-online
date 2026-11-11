using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Diagnostics;

public class LogScrubTests
{
    private const ulong Alex = 76561199000000001;
    private const ulong Sam = 76561199000000002;
    private const ulong Riley = 76561199000000003;
    private const ulong Casey = 76561199000000004;
    private const ulong Lobby = 109775240000000003;

    private static readonly byte[] Key = Enumerable.Range(1, LogTags.KeyBytes).Select(b => (byte)b).ToArray();

    private readonly LogTags _tags = new(Key, shared: true);
    private readonly LogScrub _scrub;

    public LogScrubTests()
    {
        _scrub = new LogScrub(_tags);
        _scrub.AddFolders(new Dictionary<string, string>
        {
            [@"C:\Program Files (x86)\Steam\steamapps\common\Tape to Tape"] = "<game folder>",
            [@"C:\Users\alex\AppData\Local\T2T Online"] = @"%LOCALAPPDATA%\T2T Online",
            [@"C:\Users\alex\AppData\Local"] = "%LOCALAPPDATA%",
            [@"C:\Users\alex"] = "%USERPROFILE%",
            [@"C:\Program Files (x86)\Steam"] = "<Steam>",
        });
    }

    private string P(ulong id) => _tags.Player(id);

    private static string C(int codePoint) => char.ConvertFromUtf32(codePoint);

    [Theory]
    [InlineData("Online: hosting, as asked; creating a friends-only lobby.")]
    [InlineData("12:34:56.789 INFO  Game build: 26372689 (GameAssembly f756bcb8, metadata 1ac5e68f)")]
    [InlineData("T2T Online 0.11.0-beta, commit 0123456789ab, protocol 9, log format 2; UTC date 2026-11-05.")]
    [InlineData("MelonLoader 0.7.3, .NET 6.0.36, Il2CppInterop 1.4.6.0, Unity 2022.3.62")]
    [InlineData("round trip mean/p95/max 40/55/70 ms; 0.00 % lost; run hash 0123456789abcdef")]
    [InlineData("a run of 16 digits 1234567890123456 and 19 digits 1234567890123456789")]
    public void A_line_with_nothing_to_replace_comes_back_as_the_same_instance(string line)
    {
        _scrub.RegisterName(Alex, "Alex");
        Assert.Same(line, _scrub.Scrub(line));
    }

    [Fact]
    public void Null_and_empty_give_empty()
    {
        Assert.Equal(string.Empty, _scrub.Scrub(null));
        Assert.Equal(string.Empty, _scrub.Scrub(string.Empty));
    }

    [Fact]
    public void SteamIds_become_tags_across_the_whole_individual_range()
    {
        Assert.Equal($"first {_tags.Player(LogScrub.FirstSteamId)}.", _scrub.Scrub("first 76561197960265728."));
        Assert.Equal($"last {_tags.Player(LogScrub.LastSteamId)}.", _scrub.Scrub("last 76561202255233023."));
        Assert.Equal($"id={P(Alex)},x", _scrub.Scrub("id=76561199000000001,x"));
        Assert.Equal($"Avatars: loaded the Medium avatar of {P(Sam)} (64x64)", _scrub.Scrub("Avatars: loaded the Medium avatar of 76561199000000002 (64x64)"));
    }

    [Theory]
    [InlineData("below 76561197960265727 stays")]
    [InlineData("above 76561202255233024 stays")]
    [InlineData("longer 765611990000000011 stays")]
    [InlineData("21 digits 765611990000000012345 stay")]
    public void Numbers_outside_the_range_or_inside_longer_numbers_stay(string line)
    {
        Assert.Same(line, _scrub.Scrub(line));
    }

    [Fact]
    public void An_id_between_letters_is_still_replaced()
    {
        // Safer to blur a hex number than to let an ID through.
        Assert.Equal($"x{P(Alex)}ab", _scrub.Scrub("x76561199000000001ab"));
    }

    [Fact]
    public void Lobby_ids_become_lobby_tags_and_other_18_digit_numbers_stay()
    {
        Assert.Equal($"joined lobby {_tags.Lobby(Lobby)} after 3 polls", _scrub.Scrub("joined lobby 109775240000000003 after 3 polls"));
        Assert.Equal("a counter 123456789012345678", _scrub.Scrub("a counter 123456789012345678"));
        Assert.True(LogScrub.IsLobbyId(Lobby));
        Assert.False(LogScrub.IsLobbyId(Alex));
    }

    [Fact]
    public void A_name_with_its_id_becomes_one_tag()
    {
        _scrub.RegisterName(Sam, "Sam");
        Assert.Equal($"Online: {P(Sam)} joined (T2T Online 0.11.0-beta, GamepadXbox)",
            _scrub.Scrub("Online: \"Sam\" (76561199000000002) joined (T2T Online 0.11.0-beta, GamepadXbox)"));
        Assert.Equal($"Signed in as {P(Alex)}, app 1566200.",
            Register(Alex, "Alex").Scrub("Signed in as \"Alex\" (76561199000000001), app 1566200."));
        Assert.Equal($"answered: host {P(Alex)}, T2T Online", _scrub.Scrub("answered: host Alex (76561199000000001), T2T Online"));
        Assert.Equal($"\"{P(Alex)}\" ({P(Sam)})", _scrub.Scrub("\"Alex\" (76561199000000002)")); // not the same player: both stay
    }

    [Fact]
    public void A_registered_name_is_replaced_bare_quoted_and_cut()
    {
        _scrub.RegisterName(Riley, "Riley the Great Winger");
        string tag = P(Riley);
        Assert.Equal($"Online stream from {tag}: 60 fps", _scrub.Scrub("Online stream from Riley the Great Winger: 60 fps"));
        Assert.Equal($"Online (friend of \"{tag}\"): Joined", _scrub.Scrub("Online (friend of \"Riley the Great Winger\"): Joined"));
        Assert.Equal($"seat 2: {tag} ready", _scrub.Scrub("seat 2: Riley the Gre\u2026 ready"));
        Assert.Equal($"({tag} 3)", _scrub.Scrub("(Riley the Great Winger 3)"));
    }

    [Fact]
    public void A_name_inside_another_word_stays()
    {
        _scrub.RegisterName(Sam, "Sam");
        Assert.Equal("a Samsung pad, Samuel's run, ISam", _scrub.Scrub("a Samsung pad, Samuel's run, ISam"));
        Assert.Equal($"{P(Sam)}'s run", _scrub.Scrub("Sam's run"));
    }

    [Fact]
    public void A_name_with_control_and_direction_characters_is_found_in_its_cleaned_form()
    {
        string name = "Ca" + C(0x202E) + "sey\nX";
        _scrub.RegisterName(Casey, name);
        string cleaned = SafeText.Clean(name);
        Assert.Equal($"host \"{P(Casey)}\" runs", _scrub.Scrub($"host \"{cleaned}\" runs"));
        Assert.Equal($"host \"{P(Casey)}\" runs", _scrub.Scrub($"host \"{name}\" runs")); // raw: its controls become '?' first
    }

    [Fact]
    public void A_name_the_label_tidies_is_found_in_its_label_form()
    {
        _scrub.RegisterName(Alex, "  Alex   the  Skater ");
        Assert.Equal($"seat 1: {P(Alex)}", _scrub.Scrub("seat 1: Alex the Skater"));
    }

    // The lobby draws a name without the characters its font can't (DisplayName.Clean's canDraw): the game's Latin fonts
    // hold Latin and Cyrillic letters, no symbols, no Greek and no CJK, and its smileys only some emoji.
    private static bool LatinFont(int codePoint) => codePoint is < 0x250 or (>= 0x400 and <= 0x4FF);

    private static bool NoSmileys(int codePoint) => codePoint <= 0xFFFF;

    [Theory]
    [InlineData("Riley \U0001F525", "Riley")]
    [InlineData("★ Riley ★", "Riley")]
    [InlineData("Riley 東京", "Riley")]
    [InlineData("Ωmega Riley", "mega Riley")]
    [InlineData("Riley \U0001F525 the Great Winger", "Riley the Great Winger")]
    [InlineData("José 東京", "José")] // accents the Latin fonts hold, beside a script they don't
    [InlineData("Łukasz Ω", "Łukasz")]
    [InlineData("Дима ツ", "Дима")] // Cyrillic beside katakana
    public void A_name_as_the_lobby_draws_it_is_still_replaced(string steamName, string drawn)
    {
        _scrub.RegisterName(Riley, steamName);
        string tag = P(Riley);
        Assert.Equal(drawn, DisplayName.Clean(steamName, 2, canDraw: LatinFont));
        Assert.Equal($"Lobby: start waits: {tag} isn't here yet.", _scrub.Scrub($"Lobby: start waits: {drawn} isn't here yet."));
        Assert.Equal($"seat 2: {tag} ready", _scrub.Scrub($"seat 2: {DisplayName.Clean(steamName, 2, canDraw: LatinFont, maxChars: 8)} ready"));
    }

    [Fact]
    public void A_name_drawn_without_its_emoji_only_is_still_replaced()
    {
        string steamName = "★Sam\U0001F3D2";
        _scrub.RegisterName(Sam, steamName);
        string drawn = DisplayName.Clean(steamName, 2, canDraw: NoSmileys);
        Assert.Equal("★Sam", drawn);
        Assert.Equal($"Lobby: start: {P(Sam)} not here, waited 3 s.", _scrub.Scrub($"Lobby: start: {drawn} not here, waited 3 s."));
    }

    [Fact]
    public void A_drawn_name_left_with_only_the_logs_own_words_is_replaced_only_where_quoted()
    {
        _scrub.RegisterName(Sam, "★ the host ★");
        _scrub.RegisterName(Alex, "李 A");
        string line = "Online: the host is ready. A game.";
        Assert.Same(line, _scrub.Scrub(line));
        Assert.Equal($"seat \"{P(Sam)}\" and \"{P(Alex)}\"", _scrub.Scrub("seat \"the host\" and \"A\""));
    }

    [Fact]
    public void Cjk_emoji_and_right_to_left_names_are_replaced()
    {
        _scrub.RegisterName(Alex, "\u674e\u96f7");
        _scrub.RegisterName(Sam, C(0x1F3D2) + C(0x1F525));
        _scrub.RegisterName(Riley, "\u05e8\u05d9\u05dc\u05d9");
        Assert.Equal($"host \"{P(Alex)}\", from {P(Sam)}: and {P(Riley)}.",
            _scrub.Scrub("host \"\u674e\u96f7\", from " + C(0x1F3D2) + C(0x1F525) + ": and \u05e8\u05d9\u05dc\u05d9."));
    }

    [Fact]
    public void A_two_letter_name_is_a_whole_token()
    {
        _scrub.RegisterName(Alex, "Al");
        Assert.Equal($"Online: {P(Alex)} left; Also all {P(Alex)}-ready, Al2", _scrub.Scrub("Online: Al left; Also all Al-ready, Al2"));
    }

    [Theory]
    [InlineData("Online")]
    [InlineData("host")]
    [InlineData("A")]
    [InlineData("1234")]
    [InlineData("...")]
    [InlineData("the host")]
    [InlineData("Online:")]
    [InlineData("Player 2")]
    public void A_name_that_is_one_of_the_logs_own_words_is_replaced_only_where_quoted(string name)
    {
        _scrub.RegisterName(Sam, name);
        string line = $"Online: the host {name} is ready. A game.";
        Assert.Same(line, _scrub.Scrub(line));
        Assert.Equal($"Online (friend of \"{P(Sam)}\"): Joined", _scrub.Scrub($"Online (friend of \"{name}\"): Joined"));
    }

    [Fact]
    public void A_name_two_players_share_stands_as_unknown()
    {
        _scrub.RegisterName(Alex, "Goalie Guy");
        _scrub.RegisterName(Sam, "Goalie Guy");
        Assert.Equal($"Online: {LogTags.UnknownPlayer} left", _scrub.Scrub("Online: Goalie Guy left"));
        Assert.Equal($"Online: \"{LogTags.UnknownPlayer}\" ({P(Sam)}) left", _scrub.Scrub("Online: \"Goalie Guy\" (76561199000000002) left"));
    }

    [Fact]
    public void The_longer_of_two_names_wins()
    {
        _scrub.RegisterName(Alex, "Sam");
        _scrub.RegisterName(Sam, "Sam Two");
        Assert.Equal($"{P(Sam)} and {P(Alex)}", _scrub.Scrub("Sam Two and Sam"));
    }

    [Fact]
    public void A_renamed_player_keeps_both_names_known()
    {
        _scrub.RegisterName(Alex, "Alex");
        _scrub.RegisterName(Alex, "Alexander");
        Assert.Equal($"{P(Alex)} is {P(Alex)}", _scrub.Scrub("Alex is Alexander"));
        Assert.Equal(1, _scrub.NameCount);
    }

    [Fact]
    public void A_name_made_to_look_like_another_players_tag_never_takes_it_over()
    {
        _scrub.RegisterName(Sam, P(Alex));
        string line = $"Log tags: this PC is {P(Alex)} in lobby {_tags.Lobby(Lobby)}, host {P(Alex)}.";
        Assert.Same(line, _scrub.Scrub(line));
        Assert.Equal($"host \"{P(Sam)}\"", _scrub.Scrub($"host \"{P(Alex)}\"")); // quoted, it's the name
    }

    [Fact]
    public void A_name_made_of_log_words_and_a_name_of_its_own_is_still_replaced_bare()
    {
        _scrub.RegisterName(Sam, "the Great Sam");
        Assert.Equal($"Online: {P(Sam)} left", _scrub.Scrub("Online: the Great Sam left"));
    }

    [Fact]
    public void A_player_renamed_on_every_message_registers_only_so_many_names()
    {
        for (int i = 0; i < LogScrub.MaxNamesPerPlayer; i++)
        {
            Assert.True(_scrub.RegisterName(Alex, $"Alex{i:00}"));
        }

        Assert.False(_scrub.RegisterName(Alex, "Alex99"));
        Assert.Equal(1, _scrub.RefusedNames);
        Assert.Equal($"{P(Alex)} and Alex99", _scrub.Scrub("Alex00 and Alex99"));

        // Back to a name it had: no refusal, and it's still known.
        Assert.False(_scrub.RegisterName(Alex, "Alex03"));
        Assert.Equal(1, _scrub.RefusedNames);
        Assert.Equal(P(Alex), _scrub.Scrub("Alex03"));

        // Another player has a budget of their own.
        Assert.True(_scrub.RegisterName(Sam, "Sam"));
    }

    [Fact]
    public void Two_known_names_of_one_player_read_in_turn_cost_nothing()
    {
        // A friend's Steam name with a direction mark, and the host's board's cleaned copy of it: the lobby reads one ten
        // times a second and every board brings the other.
        string steam = "ريلي‏ Riley";
        string board = SafeText.Clean(steam);
        Assert.NotEqual(steam, board);
        Assert.True(_scrub.RegisterName(Riley, steam));
        Assert.True(_scrub.RegisterName(Riley, board));
        _scrub.RegisterName(Riley, steam); // the first calls compile the methods
        _scrub.RegisterName(Riley, board);

        long before = System.GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++)
        {
            Assert.False(_scrub.RegisterName(Riley, steam));
            Assert.False(_scrub.RegisterName(Riley, board));
        }

        Assert.Equal(0, System.GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(P(Riley), _scrub.Scrub(steam));
        Assert.Equal(P(Riley), _scrub.Scrub(board));
        Assert.Equal(1, _scrub.NameCount);
    }

    [Fact]
    public void Registering_says_what_is_new()
    {
        Assert.True(_scrub.RegisterName(Alex, "Alex"));
        Assert.False(_scrub.RegisterName(Alex, "Alex"));
        Assert.False(_scrub.RegisterName(Sam, "[unknown]"));
        Assert.False(_scrub.RegisterName(Sam, "   "));
        Assert.False(_scrub.RegisterName(0, "Sam"));
    }

    [Fact]
    public void Folders_become_labels_in_either_slash_style_and_any_case()
    {
        Assert.Equal(@"Session log: <game folder>\UserData\T2TOnline\logs\session.log",
            _scrub.Scrub(@"Session log: C:\Program Files (x86)\Steam\steamapps\common\Tape to Tape\UserData\T2TOnline\logs\session.log"));
        Assert.Equal("Loading player data from <game folder>/Tape to Tape_Data/data.unity3d",
            _scrub.Scrub("Loading player data from c:/program files (x86)/steam/steamapps/common/tape to tape/Tape to Tape_Data/data.unity3d"));
        Assert.Equal(@"slot in %LOCALAPPDATA%\T2T Online\coop\coop-1.es3, temp %LOCALAPPDATA%\Temp, profile %USERPROFILE%\Desktop, Steam <Steam>\steam.exe",
            _scrub.Scrub(@"slot in C:\Users\alex\AppData\Local\T2T Online\coop\coop-1.es3, temp C:\Users\alex\AppData\Local\Temp, profile C:\USERS\ALEX\Desktop, Steam C:\Program Files (x86)\Steam\steam.exe"));
    }

    [Fact]
    public void A_folder_is_only_replaced_as_a_whole_name()
    {
        Assert.Equal(@"<user folder>\x.txt", _scrub.Scrub(@"C:\Users\alexandra\x.txt"));
        Assert.Equal(@"in C:\Program Files (x86)\Steamworks\x", _scrub.Scrub(@"in C:\Program Files (x86)\Steamworks\x"));
    }

    [Fact]
    public void Any_other_users_profile_folder_loses_the_user_name()
    {
        Assert.Equal(@"Could not find '<user folder>\SteamLibrary\x.dll'", _scrub.Scrub(@"Could not find 'D:\Users\Sam Smith\SteamLibrary\x.dll'"));
        Assert.Equal("at <user folder>/a", _scrub.Scrub("at E:/Users/riley/a"));
    }

    [Theory]
    [InlineData("connection to 192.168.1.20:27015 timed out", "connection to <ip>:27015 timed out")]
    [InlineData("peer 10.0.0.1, relay 198.51.100.39.", "peer <ip>, relay <ip>.")]
    [InlineData("v6 fe80::1c2b:3d4e:5f60:7a8b%12 up", "v6 <ip>%12 up")]
    [InlineData("v6 [2001:db8:85a3:0:0:8a2e:370:7334]:27015", "v6 [<ip>]:27015")]
    public void Ip_addresses_are_replaced(string line, string expected)
    {
        Assert.Equal(expected, _scrub.Scrub(line));
    }

    [Theory]
    [InlineData("assembly 1.0.0.0 and 6.0.36.0, Unity 2022.3.62f1, 1.2.3.4.5, 0.11.0.0")]
    [InlineData("12:34:56.789 at 03:10:00 and 1:2:3")]
    [InlineData("UnityEngine.Object::Destroy, Face::Add, std::move")]
    [InlineData("ratio 1.5.6 at 256.1.1.1")]
    [InlineData("Rewired 1.2.3.4.U5000: Windows input from RawInput")]
    public void Version_numbers_times_and_code_names_are_not_addresses(string line)
    {
        Assert.Same(line, _scrub.Scrub(line));
    }

    [Theory]
    [InlineData("history page (UTC+01:00, 12 runs)", "history page (UTC±<offset>, 12 runs)")]
    [InlineData("offset UTC-7 and GMT+10:30 and UTC\u221205:00", "offset UTC±<offset> and GMT±<offset> and UTC±<offset>")]
    public void Time_zone_offsets_are_replaced(string line, string expected)
    {
        Assert.Equal(expected, _scrub.Scrub(line));
    }

    [Theory]
    [InlineData("UTC date 2026-11-05; UTCs; a UTC-based clock; 20261105T100000Z")]
    [InlineData("UTC+1:5 is not an offset")]
    public void Utc_without_an_offset_stays(string line)
    {
        Assert.Same(line, _scrub.Scrub(line));
    }

    [Theory]
    [InlineData("T2T Online 0.11.0-beta, commit 0123456789ab, protocol 9, log format 2; UTC date 2026-11-05.")]
    [InlineData("Housekeeping check: Unity's analytics keeps 3 local file(s), last written 2026-10-20 08:00 UTC, before this game started.")]
    [InlineData("Online: P#3fa9c1 (Rewired player 2, game player 1): 120 pad packets/s; session Connected, relayed, Steam ping 30 ms")]
    public void A_line_with_nothing_to_replace_allocates_nothing(string line)
    {
        _scrub.RegisterName(Alex, "Alex");
        _scrub.Scrub(line); // the first call compiles the methods
        long before = System.GC.GetAllocatedBytesForCurrentThread();
        string scrubbed = _scrub.Scrub(line);
        long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Same(line, scrubbed);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Control_characters_are_neutralised_but_a_stack_traces_lines_stay()
    {
        Assert.Equal("a?b?c", _scrub.Scrub("a" + C(0x202E) + "b" + C(0x1B) + "c"));
        Assert.Equal("Error: x\n   at A.B()\r\n   at C.D()", _scrub.Scrub("Error: x\n   at A.B()\r\n   at C.D()"));
        Assert.Equal("fake??12:00 INFO  forged", LogScrub.NeutraliseControls("fake\r\n12:00 INFO  forged", keepLineBreaks: false));
        string emoji = "x" + C(0x1F3D2);
        Assert.Same(emoji, LogScrub.NeutraliseControls(emoji, keepLineBreaks: false));
        Assert.Equal("a?b", LogScrub.NeutraliseControls("a" + (char)0xD83C + "b", keepLineBreaks: true));
    }

    [Fact]
    public void The_log_tag_key_is_never_written()
    {
        string hex = LogTags.ToHex(Key);
        Assert.Equal($"refused lobby data t2t.logtag = {LogScrub.KeyLabel}.", _scrub.Scrub($"refused lobby data t2t.logtag = {hex}."));
        Assert.Equal($"[{LogScrub.KeyLabel}]", _scrub.Scrub($"[{hex.ToUpperInvariant()}]"));
        string other = LogTags.ToHex(LogTags.NewKey());
        Assert.Equal($"another key {other}", _scrub.Scrub($"another key {other}")); // only the key in use is known
    }

    [Fact]
    public void New_tags_rename_everyone_from_then_on()
    {
        _scrub.RegisterName(Alex, "Alex");
        string before = _scrub.Scrub("Alex 76561199000000001");
        var lobby = new LogTags(LogTags.NewKey(), shared: true);
        _scrub.Tags = lobby;
        Assert.Equal($"{lobby.Player(Alex)} {lobby.Player(Alex)}", _scrub.Scrub("Alex 76561199000000001"));
        Assert.NotEqual(before, _scrub.Scrub("Alex 76561199000000001"));
    }

    [Fact]
    public void Counts_say_what_was_replaced()
    {
        _scrub.RegisterName(Alex, "Alex");
        var counts = new LogScrubCounts();
        _scrub.Scrub(@"""Alex"" (76561199000000001) in lobby 109775240000000003 from C:\Users\alex\x at 10.1.2.3 UTC+02:00" + C(0x1B), counts);
        Assert.Equal(1, counts.Names);
        Assert.Equal(1, counts.SteamIds);
        Assert.Equal(1, counts.Lobbies);
        Assert.Equal(1, counts.Folders);
        Assert.Equal(1, counts.Addresses);
        Assert.Equal(1, counts.Offsets);
        Assert.Equal(1, counts.Controls);
        Assert.Equal(7, counts.Total);
    }

    [Fact]
    public void A_real_shaped_host_line_carries_nothing_personal()
    {
        _scrub.RegisterName(Alex, "Alex");
        _scrub.RegisterName(Sam, "Sam");
        string line = _scrub.Scrub("Online: invite to lobby 109775240000000003 sent to \"Sam\" (76561199000000002); host \"Alex\" (76561199000000001), " +
            @"log C:\Program Files (x86)\Steam\steamapps\common\Tape to Tape\UserData\T2TOnline\logs");
        Assert.Equal($"Online: invite to lobby {_tags.Lobby(Lobby)} sent to {P(Sam)}; host {P(Alex)}, log <game folder>\\UserData\\T2TOnline\\logs", line);
        Assert.DoesNotContain("7656", line);
    }

    private static string TextShape(string? text) => $"{text?.Length ?? 0} characters, #test";

    private static string Label(string text) => $"{LogScrub.PlayerTextLabel} ({TextShape(text)})";

    [Fact]
    public void A_custom_skaters_name_is_replaced_bare_quoted_and_cut_by_its_label_never_a_tag()
    {
        Assert.True(_scrub.RegisterPlayerText("Riley Casey", TextShape));
        string label = Label("Riley Casey");
        Assert.Equal($"Player markers: game player 1 blue on {label}", _scrub.Scrub("Player markers: game player 1 blue on Riley Casey"));
        Assert.Equal($"the host's \"{label}\" (F:H:0f2b7633) isn't in this match", _scrub.Scrub("the host's \"Riley Casey\" (F:H:0f2b7633) isn't in this match"));
        Assert.Equal($"Hockey/Teams/Home/{label}/Body", _scrub.Scrub("Hockey/Teams/Home/Riley Casey/Body"));
        Assert.Equal($"seat 2: {label} ready", _scrub.Scrub("seat 2: Riley Ca\u2026 ready"));
        Assert.DoesNotContain("P#", _scrub.Scrub("on Riley Casey"), System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_custom_name_in_its_drawn_and_cleaned_forms_is_still_replaced()
    {
        _scrub.RegisterPlayerText("Riley Casey \u2605", TextShape);
        string label = Label("Riley Casey \u2605");
        Assert.Equal($"on {label}", _scrub.Scrub("on Riley Casey \u2605"));
        Assert.Equal($"on {label}.", _scrub.Scrub("on Riley Casey.")); // as a font without the star draws it
        _scrub.RegisterPlayerText("Sam\u200F Park", TextShape);
        Assert.Equal($"on {Label("Sam\u200F Park")}", _scrub.Scrub("on Sam\u200F Park"));
    }

    [Theory]
    [InlineData("Rockets", "Online: the Rockets are hosting; logo Rockets")] // a single word
    [InlineData("logo", "Custom content: loaded the host's logo (12 characters, #4be1)")]
    [InlineData("Hockey", "Hockey/Teams/Calaveras")]
    [InlineData("Online Match", "Online: match started; Online Match")] // only the log's own words
    [InlineData("Al", "Al ok")] // very short
    [InlineData("Setup", "Match setup: on (match loads are read for friends). Setup done")]
    public void A_custom_name_that_is_one_word_short_or_the_logs_own_words_is_replaced_only_where_quoted(string text, string line)
    {
        _scrub.RegisterPlayerText(text, TextShape);
        Assert.Same(line, _scrub.Scrub(line));
        Assert.Equal($"the host's \"{Label(text)}\"", _scrub.Scrub($"the host's \"{text}\""));
    }

    [Fact]
    public void Ordinary_log_lines_keep_their_words_with_many_custom_names_known()
    {
        foreach (string text in new[] { "Rockets", "Hockey", "logo", "Team", "Match", "Online", "Riley Casey", "Sam Park", "custom_Riley logo.png", "Riley logo.png", "Riley logo", "Top", "Cursor", "Check" })
        {
            _scrub.RegisterPlayerText(text, TextShape);
        }

        string[] lines =
        {
            "Online: sent this match's setup to 1 friend(s) (Calaveras vs Top Cheese, 3 periods of 2.5 min, difficulty 2; setup 0123456789abcdef)",
            "Custom content: loaded P#3fa9c1's logo (12 characters, #4be1; 512x512) with the game's own loader; it's first in this game's logo list for the session.",
            "Lobby check: cursor on Top tab, check 12 passed; Hockey FC picked.",
            "Match setup: the match waits for the host's custom content (team, 2 skaters); it loads as soon as that's here.",
            "Stream recorder: \"skater 0f2b7633-914e-4a2d-b41e-0778b6da5357\"'s replay components changed",
        };
        foreach (string line in lines)
        {
            Assert.Same(line, _scrub.Scrub(line));
        }
    }

    [Fact]
    public void A_logo_key_and_its_file_are_replaced_everywhere()
    {
        _scrub.RegisterPlayerText("custom_Riley Casey.png", TextShape);
        _scrub.RegisterPlayerText("Riley Casey.png", TextShape);
        Assert.Equal($"couldn't load {Label("custom_Riley Casey.png")}", _scrub.Scrub("couldn't load custom_Riley Casey.png"));
        Assert.Equal($"<game data>\\CustomLogos\\{Label("Riley Casey.png")} is missing", _scrub.Scrub(@"<game data>\CustomLogos\Riley Casey.png is missing"));
    }

    [Fact]
    public void A_steam_names_form_stays_its_players_tag_whichever_came_first()
    {
        _scrub.RegisterPlayerText("Alex Park", TextShape);
        Assert.Equal($"on {Label("Alex Park")}", _scrub.Scrub("on Alex Park"));
        _scrub.RegisterName(Alex, "Alex Park");
        Assert.Equal($"on {P(Alex)}", _scrub.Scrub("on Alex Park"));

        _scrub.RegisterName(Sam, "Sam Lee");
        Assert.True(_scrub.RegisterPlayerText("Sam Lee", TextShape));
        Assert.Equal($"on {P(Sam)}", _scrub.Scrub("on Sam Lee"));
    }

    [Fact]
    public void Registering_a_custom_name_says_what_is_new_and_a_known_one_costs_nothing()
    {
        Assert.True(_scrub.RegisterPlayerText("Riley Casey", TextShape));
        Assert.False(_scrub.RegisterPlayerText("Riley Casey", TextShape));
        Assert.False(_scrub.RegisterPlayerText("   ", TextShape));
        Assert.False(_scrub.RegisterPlayerText(null, TextShape));
        Assert.Equal(1, _scrub.PlayerTextCount);

        long before = System.GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++)
        {
            _scrub.RegisterPlayerText("Riley Casey", TextShape);
        }

        Assert.Equal(0, System.GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void A_shape_that_fails_leaves_the_label_alone_and_none_needs_no_shape()
    {
        _scrub.RegisterPlayerText("Riley Casey", _ => throw new System.InvalidOperationException("test"));
        Assert.Equal($"on {LogScrub.PlayerTextLabel}", _scrub.Scrub("on Riley Casey"));
        _scrub.RegisterPlayerText("Sam Park");
        Assert.Equal($"on {LogScrub.PlayerTextLabel}", _scrub.Scrub("on Sam Park"));
    }

    [Fact]
    public void Custom_names_are_capped_and_the_refusals_counted()
    {
        for (int i = 0; i < LogScrub.MaxPlayerTexts + 10; i++)
        {
            _scrub.RegisterPlayerText($"Riley Casey {i:D4}");
        }

        Assert.Equal(LogScrub.MaxPlayerTexts, _scrub.PlayerTextCount);
        Assert.Equal(10, _scrub.RefusedPlayerTexts);
        Assert.Equal($"on {LogScrub.PlayerTextLabel}", _scrub.Scrub("on Riley Casey 0000"));
        Assert.Equal($"on Riley Casey {LogScrub.MaxPlayerTexts + 5:D4}", _scrub.Scrub($"on Riley Casey {LogScrub.MaxPlayerTexts + 5:D4}"));
    }

    [Theory]
    [InlineData("Online: P#3fa9c1 (Rewired player 2, game player 1): 120 pad packets/s; session Connected, relayed, Steam ping 30 ms")]
    [InlineData("Stream player: skater 0f2b7633-914e-4a2d-b41e-0778b6da5357 shown by the stream before its first frame here")]
    public void A_line_with_nothing_to_replace_allocates_nothing_with_many_custom_names_known(string line)
    {
        for (int i = 0; i < 300; i++)
        {
            _scrub.RegisterPlayerText($"Riley Casey {i}");
            _scrub.RegisterPlayerText($"Team {i} Rockets");
        }

        _scrub.Scrub(line); // the first call compiles the methods
        long before = System.GC.GetAllocatedBytesForCurrentThread();
        string scrubbed = _scrub.Scrub(line);
        Assert.Equal(0, System.GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Same(line, scrubbed);
    }

    [Fact]
    public void Counts_say_how_many_custom_names_were_replaced()
    {
        _scrub.RegisterName(Alex, "Alex");
        _scrub.RegisterPlayerText("Riley Casey", TextShape);
        var counts = new LogScrubCounts();
        _scrub.Scrub("\"Alex\" passed to Riley Casey and Riley Casey scored", counts);
        Assert.Equal(1, counts.Names);
        Assert.Equal(2, counts.Texts);
        Assert.Equal(3, counts.Total);
    }

    private LogScrub Register(ulong id, string name)
    {
        _scrub.RegisterName(id, name);
        return _scrub;
    }
}
