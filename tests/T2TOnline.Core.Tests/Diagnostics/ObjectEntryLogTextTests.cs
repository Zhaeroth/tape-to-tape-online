using System;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class ObjectEntryLogTextTests
{
    [Theory]
    [InlineData("F:H:0f2b7633-d0f6-4e06-92c9-5ef01aeb4039", "skater 0f2b7633-d0f6-4e06-92c9-5ef01aeb4039")]
    [InlineData("F:V:0f2b7633-d0f6-4e06-92c9-5ef01aeb4039", "skater 0f2b7633-d0f6-4e06-92c9-5ef01aeb4039")]
    [InlineData("F:?:8c2ebbc0-21be-4fa9-be60-922e24bc530c", "skater 8c2ebbc0-21be-4fa9-be60-922e24bc530c")]
    [InlineData("R:referee_01", "skater referee_01")]
    public void A_skater_a_hosts_table_names_is_written_by_its_keys_asset_id_never_the_hosts_name(string key, string expected)
    {
        Assert.True(ObjectEntryLogText.IsSkaterKey(key));
        string line = ObjectEntryLogText.OfEntry(key, "Riley Casey");
        Assert.Equal(expected, line);
        Assert.DoesNotContain("Riley", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Casey", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("F:H:?")]
    [InlineData("F:H:")]
    [InlineData("F:")]
    [InlineData("F:H")]
    [InlineData("R:")]
    [InlineData("R:Riley Casey")]
    [InlineData("F:H:Riley Casey lives at 12 Example Road")]
    [InlineData("F:H:0123456789012345678901234567890123456789012345678")]
    [InlineData("F:V:line\nbreak")]
    public void A_skater_whose_key_has_no_usable_id_is_a_skater(string key)
    {
        string line = ObjectEntryLogText.OfEntry(key, "Riley Casey");
        Assert.Equal(ObjectEntryLogText.UnnamedSkater, line);
    }

    [Theory]
    [InlineData("G:N", "GoaltenderNorth")]
    [InlineData("P:body", "Puck")]
    [InlineData("go:standard_game/Effects/StickPool/Sticks/Stick(Clone)#3", "Stick(Clone)")]
    [InlineData("", "DeflectVFX")]
    [InlineData(null, "DeflectVFX")]
    public void Any_other_object_of_a_hosts_table_keeps_its_name(string? key, string name)
    {
        Assert.False(ObjectEntryLogText.IsSkaterKey(key));
        Assert.Equal(name, ObjectEntryLogText.OfEntry(key, name));
    }

    [Fact]
    public void An_entry_with_no_name_is_written_as_nothing()
    {
        Assert.Equal(string.Empty, ObjectEntryLogText.OfEntry("G:S", null));
    }

    [Fact]
    public void A_skater_is_written_by_its_asset_id_or_as_a_skater()
    {
        Assert.Equal("skater 0f2b7633-d0f6-4e06-92c9-5ef01aeb4039", ObjectEntryLogText.Skater("0f2b7633-d0f6-4e06-92c9-5ef01aeb4039"));
        Assert.Equal("skater Buster.Brewster_2", ObjectEntryLogText.Skater("Buster.Brewster_2"));
        Assert.Equal(ObjectEntryLogText.UnnamedSkater, ObjectEntryLogText.Skater(null));
        Assert.Equal(ObjectEntryLogText.UnnamedSkater, ObjectEntryLogText.Skater(string.Empty));
        Assert.Equal(ObjectEntryLogText.UnnamedSkater, ObjectEntryLogText.Skater("?"));
        Assert.Equal(ObjectEntryLogText.UnnamedSkater, ObjectEntryLogText.Skater("Riley Casey"));
        Assert.Equal(ObjectEntryLogText.UnnamedSkater, ObjectEntryLogText.Skater(new string('a', ObjectEntryLogText.MaxIdLength + 1)));
        Assert.Equal($"skater {new string('a', ObjectEntryLogText.MaxIdLength)}", ObjectEntryLogText.Skater(new string('a', ObjectEntryLogText.MaxIdLength)));
    }
}

public class PlayerWrittenNamesTests
{
    private static readonly PlayerTextShape Shapes = new(new byte[PlayerTextShape.KeyBytes]);

    private static string Shape(string? text) => Shapes.Describe(text);

    [Fact]
    public void A_text_with_no_kept_name_comes_back_as_it_is()
    {
        var names = new PlayerWrittenNames();
        const string Path = "go:standard_game/Effects/StickPool/Sticks/Stick(Clone)#3";
        Assert.Same(Path, names.Hide(Path, Shape));
        names.Note("Riley Casey");
        Assert.Same(Path, names.Hide(Path, Shape));
        Assert.Equal(string.Empty, names.Hide(null, Shape));
        Assert.Equal(string.Empty, names.Hide(string.Empty, Shape));
    }

    [Fact]
    public void A_kept_name_in_a_scene_path_or_an_assets_name_is_written_as_its_shape()
    {
        var names = new PlayerWrittenNames();
        names.Note("Riley Casey");
        string path = names.Hide("go:standard_game/HomeTeam#0/Riley Casey#0/StickCollider#0", Shape);
        Assert.Equal($"go:standard_game/HomeTeam#0/({Shape("Riley Casey")})#0/StickCollider#0", path);
        Assert.DoesNotContain("Riley", path, StringComparison.Ordinal);
        string asset = names.Hide("asset:Riley Casey", Shape);
        Assert.Equal($"asset:({Shape("Riley Casey")})", asset);
    }

    [Fact]
    public void A_name_inside_a_longer_one_does_not_split_it()
    {
        var names = new PlayerWrittenNames();
        names.Note("Sam");
        names.Note("Sam Alexander");
        string text = names.Hide("go:arena/Sam Alexander#0 and go:arena/Sam#1", Shape);
        Assert.Equal($"go:arena/({Shape("Sam Alexander")})#0 and go:arena/({Shape("Sam")})#1", text);
    }

    [Fact]
    public void Short_empty_and_repeated_names_are_not_kept()
    {
        var names = new PlayerWrittenNames();
        names.Note(null);
        names.Note(string.Empty);
        names.Note("  ");
        names.Note("Al");
        names.Note("Alex");
        names.Note(" Alex ");
        Assert.Equal(1, names.Count);
        Assert.Equal("a stick", names.Hide("a stick", Shape));
    }

    [Fact]
    public void Only_so_many_names_are_kept_the_oldest_forgotten_first()
    {
        var names = new PlayerWrittenNames();
        for (int i = 0; i < PlayerWrittenNames.MaxNames + 5; i++)
        {
            names.Note($"Skater Number {i:000}");
        }

        Assert.Equal(PlayerWrittenNames.MaxNames, names.Count);
        Assert.Equal("go:a/Skater Number 000#0", names.Hide("go:a/Skater Number 000#0", Shape));
        string last = $"Skater Number {PlayerWrittenNames.MaxNames + 4:000}";
        Assert.DoesNotContain(last, names.Hide($"go:a/{last}#0", Shape), StringComparison.Ordinal);
    }

    [Fact]
    public void A_shape_is_needed()
    {
        Assert.Throws<ArgumentNullException>(() => new PlayerWrittenNames().Hide("text", null!));
    }
}
