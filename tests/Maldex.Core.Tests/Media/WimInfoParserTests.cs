using System.Runtime.CompilerServices;
using Maldex.Core.Media;
using Xunit;

namespace Maldex.Core.Tests.Media;

public class WimInfoParserTests
{
    private static string Fixture(string name, [CallerFilePath] string here = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "wiminfo", name));

    private const string H = "Available Images:\n-----------------\n";

    /// <summary>One image block in wiminfo's layout; Name and Architecture are always present on real output.</summary>
    private static string Img(int index, string name, string arch = "x86_64", string extra = "") =>
        $"Index:                  {index}\nName:                   {name}\n{extra}Architecture:           {arch}\n\n";

    private static string Out(int count, params string[] blocks) =>
        $"WIM Information:\n----------------\nImage Count:    {count}\n\n" + H + string.Concat(blocks);

    [Fact]
    public void A_name_with_an_embedded_newline_cannot_forge_a_second_image()
    {
        // Image 1 is Home; its Name carries a forged "Index: 1 / Name: Windows 11 Pro" (duplicate index).
        var shadow = Out(1, "Index: 1\nName: Windows 11 Home\nIndex: 1\nName: Windows 11 Pro\nArchitecture: x86_64\nEdition ID: Professional\n");
        Assert.Empty(WimInfoParser.Parse(shadow));
    }

    [Fact]
    public void A_forged_image_with_a_new_index_breaks_the_image_count()
    {
        var text = Out(1, "Index: 1\nName: Windows 11 Home\nArchitecture: x86_64\nDescription: x\n" + Img(2, "Windows 11 Pro"));
        Assert.Empty(WimInfoParser.Parse(text));
    }

    [Fact]
    public void A_duplicate_index_anywhere_fails_the_whole_parse()
    {
        Assert.Empty(WimInfoParser.Parse(Out(3, Img(1, "A"), Img(2, "B"), Img(1, "C"))));
    }

    [Fact]
    public void A_repeated_key_inside_one_image_fails_the_whole_parse()
    {
        Assert.Empty(WimInfoParser.Parse(Out(1, "Index: 1\nName: Windows 11 Home\nName: Windows 11 Pro\nArchitecture: x86_64\n")));
    }

    [Fact]
    public void An_image_without_architecture_fails_the_whole_parse() =>
        Assert.Empty(WimInfoParser.Parse(Out(2, Img(1, "A"), "Index: 2\nName: B\nEdition ID: Professional\n")));

    [Fact]
    public void An_image_without_name_fails_the_whole_parse() =>
        Assert.Empty(WimInfoParser.Parse(Out(2, Img(1, "A"), "Index: 2\nArchitecture: x86_64\n")));

    [Fact]
    public void Output_without_the_header_image_count_fails_the_whole_parse()
    {
        Assert.Empty(WimInfoParser.Parse(H + Img(1, "Windows 11 Pro")));
        Assert.Single(WimInfoParser.Parse(Out(1, Img(1, "Windows 11 Pro"))));
    }

    [Fact]
    public void A_forged_architecture_in_a_description_does_not_make_an_image_installable()
    {
        // A Description carrying "\nArchitecture: x86_64" next to the real (arm64) line: two Architecture lines, so nothing is trusted.
        var forged = Out(1, "Index: 1\nName: Windows 11 Pro\nDescription: x\nArchitecture: x86_64\n\nArchitecture: arm64\n");
        Assert.Empty(WimInfoParser.Parse(forged));
        // And with no Architecture line at all the image is refused outright.
        Assert.Empty(WimInfoParser.Parse(Out(1, "Index: 1\nName: Windows 11 Pro\nDescription: x\n")));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData("0")]
    [InlineData("abc")]
    public void An_invalid_index_fails_the_whole_parse(string index) =>
        Assert.Empty(WimInfoParser.Parse(Out(2, $"Index: {index}\nName: Bad\nArchitecture: x86_64\n\n", Img(2, "Good"))));

    [Fact]
    public void Indented_lines_and_lines_before_the_images_section_are_ignored()
    {
        var text = "Index: 9\nName: Early\nImage Count: 1\n" + H + "  Index: 8\n" + Img(1, "Real") + "    Name: Indented\n";
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
        var images = WimInfoParser.Parse(Out(1, Img(1, "X", extra: "Build: 22631\n")));
        Assert.Equal("22631", Assert.Single(images).Build);
    }

    [Fact]
    public void Values_are_trimmed_stripped_of_control_characters_and_clipped_to_100()
    {
        var text = Out(1, "Index: 1\nName:   A\u0007B\u001b[31m" + new string('x', 200) + "  \nArchitecture: x86_64\nEdition ID: Pro\n");
        var image = Assert.Single(WimInfoParser.Parse(text));
        Assert.Equal(100, image.Name.Length);
        Assert.StartsWith("AB[31mxxx", image.Name);
        Assert.DoesNotContain(image.Name, char.IsControl);
    }

    [Fact]
    public void At_most_64_images_are_returned()
    {
        var text = Out(80, Enumerable.Range(1, 80).Select(i => Img(i, $"N{i}")).ToArray());
        var images = WimInfoParser.Parse(text);
        Assert.Equal(64, images.Count);
        Assert.Equal(64, images[^1].Index);
    }

    [Fact]
    public void Keys_of_one_block_do_not_leak_into_the_next()
    {
        var text = Out(2, "Index: 1\nName: A\nEdition ID: Core\nArchitecture: x86_64\n\n", Img(2, "B", "arm64"));
        var images = WimInfoParser.Parse(text);
        Assert.Null(images[1].EditionId);
        Assert.Equal("arm64", images[1].Architecture);
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
