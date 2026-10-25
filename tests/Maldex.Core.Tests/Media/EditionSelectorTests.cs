using Maldex.Core.Media;
using Xunit;

namespace Maldex.Core.Tests.Media;

public class EditionSelectorTests
{
    private const string EnterpriseKey = "NPPR9-FWDCX-D2C8J-H872K-2YT43";
    private const string ProKey = "W269N-WFGWX-YVC9B-4J6C9-T83GX";
    private const string EducationKey = "NW6C2-QMPVW-D7KKK-3GKT6-VCFB2";

    private static InstallerImage Img(int i, string name, string arch = "x86_64") => new(i, name, null, "26100.1", arch);

    private static readonly InstallerImage[] Consumer =
    [
        Img(1, "Windows 11 Home"), Img(2, "Windows 11 Home N"), Img(4, "Windows 11 Education"),
        Img(6, "Windows 11 Pro"), Img(7, "Windows 11 Pro N"),
    ];

    [Fact]
    public void Evaluation_media_prefers_enterprise_evaluation_without_a_key()
    {
        var choice = EditionSelector.Preferred([Img(1, "Windows 11 Enterprise Evaluation")]);
        Assert.Equal(new EditionChoice(1, "Windows 11 Enterprise Evaluation", null), choice);
    }

    [Fact]
    public void Consumer_media_prefers_pro_with_its_key()
    {
        Assert.Equal(new EditionChoice(6, "Windows 11 Pro", ProKey), EditionSelector.Preferred(Consumer));
    }

    [Fact]
    public void Preferred_order_is_evaluation_then_enterprise_then_pro()
    {
        var all = new[] { Img(1, "Windows 11 Pro"), Img(2, "Windows 11 Enterprise"), Img(3, "Windows 11 Enterprise Evaluation") };
        Assert.Equal(3, EditionSelector.Preferred(all)!.Index);
        Assert.Equal(new EditionChoice(2, "Windows 11 Enterprise", EnterpriseKey), EditionSelector.Preferred(all.Take(2).ToArray()));
    }

    [Fact]
    public void Preferred_never_picks_education()
    {
        Assert.Null(EditionSelector.Preferred([Img(1, "Windows 11 Education")]));
    }

    [Fact]
    public void Preferred_is_null_when_nothing_is_supported()
    {
        Assert.Null(EditionSelector.Preferred([Img(1, "Windows 11 Home"), Img(2, "Windows 11 Pro", "arm64")]));
        Assert.Null(EditionSelector.Preferred([]));
    }

    [Fact]
    public void Education_is_chosen_only_by_index_and_carries_its_key()
    {
        Assert.Equal(new EditionChoice(4, "Windows 11 Education", EducationKey), EditionSelector.Choose(Consumer, 4));
    }

    [Fact]
    public void Choose_without_an_index_uses_the_preferred_edition()
    {
        Assert.Equal(6, EditionSelector.Choose(Consumer, null).Index);
    }

    [Fact]
    public void Choosing_home_by_index_explains_why_it_is_unsupported()
    {
        var ex = Assert.Throws<EditionNotSupportedException>(() => EditionSelector.Choose(Consumer, 1));
        Assert.Equal("Windows 11 Home is not supported: it has no KMS client setup key and ignores the policies Maldex sets", ex.Message);
    }

    [Fact]
    public void Home_n_gets_the_generic_message()
    {
        var ex = Assert.Throws<EditionNotSupportedException>(() => EditionSelector.Choose(Consumer, 2));
        Assert.Equal("Windows 11 Home N is not supported; choose Enterprise Evaluation, Enterprise, Pro or Education", ex.Message);
    }

    [Fact]
    public void Arm64_is_refused_even_for_a_supported_name()
    {
        var arm = new[] { Img(1, "Windows 11 Pro", "arm64") };
        Assert.Equal("only x64 images can be installed", EditionSelector.Unsupported(arm[0]));
        var ex = Assert.Throws<EditionNotSupportedException>(() => EditionSelector.Choose(arm, 1));
        Assert.Equal("only x64 images can be installed", ex.Message);
    }

    [Fact]
    public void No_supported_image_and_no_index_is_refused()
    {
        var ex = Assert.Throws<EditionNotSupportedException>(() => EditionSelector.Choose([Img(1, "Windows 11 Home")], null));
        Assert.Equal("no Enterprise or Pro image on this media", ex.Message);
    }

    [Fact]
    public void Unknown_index_is_refused()
    {
        var ex = Assert.Throws<EditionNotSupportedException>(() => EditionSelector.Choose(Consumer, 9));
        Assert.Equal("the media has no image with index 9", ex.Message);
    }

    [Theory]
    [InlineData("x86_64")]
    [InlineData("x64")]
    [InlineData("AMD64")]
    [InlineData("amd64")]
    public void X64_spellings_are_supported(string arch) => Assert.Null(EditionSelector.Unsupported(Img(1, "Windows 11 Pro", arch)));

    [Fact]
    public void Missing_architecture_is_not_x64()
    {
        Assert.Equal("only x64 images can be installed",
            EditionSelector.Unsupported(new InstallerImage(1, "Windows 11 Pro", null, null, null)));
    }

    [Fact]
    public void Names_must_match_exactly()
    {
        Assert.NotNull(EditionSelector.Unsupported(Img(1, "Windows 11 Pro N")));
        Assert.NotNull(EditionSelector.Unsupported(Img(1, "windows 11 pro")));
        Assert.Null(EditionSelector.Unsupported(Img(1, "Windows 11 Enterprise")));
    }
}
