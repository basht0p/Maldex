using System.Runtime.CompilerServices;
using Maldex.Core.Media;
using Xunit;

namespace Maldex.Core.Tests.Media;

public class WimInfoParserTests
{
    private static string Fixture(string name, [CallerFilePath] string here = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "wiminfo", name));

    private const string H = "Available Images:\n-----------------\n";

    [Fact]
    public void A_name_with_an_embedded_newline_cannot_forge_a_second_image()
    {
        // Image 1 is Home; its Name carries a forged "Index: 1 / Name: Windows 11 Pro" (duplicate index).
        var shadow = H + "Index: 1\nName: Windows 11 Home\u000a" + "Index: 1\nName: Windows 11 Pro\nArchitecture: x86_64\nEdition ID: Professional\n";
        Assert.Empty(WimInfoParser.Parse(shadow));
    }

    [Fact]
    public void A_forged_image_with_a_new_index_breaks_the_image_count()
    {
        var text = "Image Count: 1\n\n" + H + "Index: 1\nName: Windows 11 Home\nDescription: x\nIndex: 2\nName: Windows 11 Pro\n";
        Assert.Empty(WimInfoParser.Parse(text));
    }

    [Fact]
    public void A_duplicate_index_anywhere_fails_the_whole_parse()
    {
        Assert.Empty(WimInfoParser.Parse(H + "Index: 1\nName: A\n\nIndex: 2\nName: B\n\nIndex: 1\nName: C\n"));
    }

    [Fact]
    public void A_repeated_key_inside_one_image_fails_the_whole_parse()
    {
        Assert.Empty(WimInfoParser.Parse(H + "Index: 1\nName: Windows 11 Home\nName: Windows 11 Pro\n"));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData("0")]
    public void Signed_or_zero_indexes_are_not_images(string index)
    {
        var images = WimInfoParser.Parse(H + $"Index: {index}\nName: Bad\n\nIndex: 2\nName: Good\n");
        Assert.Equal(2, Assert.Single(images).Index);
    }

    [Fact]
    public void Indented_lines_and_lines_before_the_images_section_are_ignored()
    {
        var text = "Index: 9\nName: Early\n" + H + "  Index: 8\nIndex: 1\nName: Real\n    Name: Indented\n";
        var image = Assert.Single(WimInfoParser.Parse(text));
        Assert.Equal(1, image.Index);
        Assert.Equal("Real", image.Name);
    }

    [Fact]
    public void Consumer_media_lists_every_edition_with_its_real_index()
    {
        var images = WimInfoParser.Parse(Fixture("consumer-multi.txt"));

        Assert.Equal(8, images.Count);
        Assert.Equal("Windows 11 Home", images[0].Name);
        Assert.Equal("Core", images[0].EditionId);
        var pro = Assert.Single(images, i => i.Name == "Windows 11 Pro");
        Assert.Equal(6, pro.Index);
        Assert.Equal("Professional", pro.EditionId);
        Assert.Equal("26300.1", pro.Build);
        Assert.Equal("x86_64", pro.Architecture);
    }

    [Fact]
    public void Enterprise_evaluation_media_has_one_image()
    {
        var image = Assert.Single(WimInfoParser.Parse(Fixture("enterprise-eval.txt")));
        Assert.Equal(1, image.Index);
        Assert.Equal("Windows 11 Enterprise Evaluation", image.Name);
        Assert.Equal("EnterpriseEval", image.EditionId);
        Assert.Equal("26100.1", image.Build);
    }

    [Fact]
    public void Arm64_media_reports_the_architecture()
    {
        var images = WimInfoParser.Parse(Fixture("arm64.txt"));
        Assert.Equal(2, images.Count);
        Assert.All(images, i => Assert.Equal("arm64", i.Architecture));
    }

    [Fact]
    public void Build_without_service_pack_build_is_the_build_alone()
    {
        var images = WimInfoParser.Parse(H + "Index: 1\nName: X\nBuild: 22631\n");
        Assert.Equal("22631", Assert.Single(images).Build);
    }

    [Fact]
    public void Values_are_trimmed_stripped_of_control_characters_and_clipped_to_100()
    {
        var text = H + "Index: 1\nName:   A\u0007B\u001b[31m" + new string('x', 200) + "  \nEdition ID: Pro\n";
        var image = Assert.Single(WimInfoParser.Parse(text));
        Assert.Equal(100, image.Name.Length);
        Assert.StartsWith("AB[31mxxx", image.Name);
        Assert.DoesNotContain(image.Name, char.IsControl);
    }

    [Fact]
    public void At_most_64_images_are_returned()
    {
        var text = H + string.Concat(Enumerable.Range(1, 80).Select(i => $"Index: {i}\nName: N{i}\n\n"));
        var images = WimInfoParser.Parse(text);
        Assert.Equal(64, images.Count);
        Assert.Equal(64, images[^1].Index);
    }

    [Fact]
    public void An_unparsable_index_skips_the_block()
    {
        var text = H + "Index: abc\nName: Bad\n\nIndex: 2\nName: Good\n";
        var image = Assert.Single(WimInfoParser.Parse(text));
        Assert.Equal(2, image.Index);
        Assert.Equal("Good", image.Name);
    }

    [Fact]
    public void Keys_of_one_block_do_not_leak_into_the_next()
    {
        var text = H + "Index: 1\nName: A\nEdition ID: Core\nArchitecture: x86_64\n\nIndex: 2\nName: B\n";
        var images = WimInfoParser.Parse(text);
        Assert.Null(images[1].EditionId);
        Assert.Null(images[1].Architecture);
    }

    [Fact]
    public void Output_without_images_gives_an_empty_list()
    {
        Assert.Empty(WimInfoParser.Parse(""));
        Assert.Empty(WimInfoParser.Parse("WIM Information:\nImage Count: 0\n"));
    }

    [Fact]
    public void Media_contents_round_trip_as_camel_case_json()
    {
        var contents = new MediaContents([new InstallerImage(6, "Windows 11 Pro", "Professional", "26300.1", "x86_64")], null);
        var json = MediaContentsJson.Serialize(contents);
        Assert.Contains("\"images\"", json);
        Assert.Contains("\"editionId\":\"Professional\"", json);
        var back = MediaContentsJson.Parse(json)!;
        Assert.Equal(contents.Images, back.Images);
        Assert.Null(back.Application);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void Unreadable_contents_json_parses_to_null(string? json) => Assert.Null(MediaContentsJson.Parse(json));
}
