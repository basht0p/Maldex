using Maldex.Core.Text;
using Xunit;

namespace Maldex.Core.Tests.Agent;

public class GuestTextTests
{
    [Fact]
    public void Clean_StripsNulAndControls() =>
        Assert.Equal("abc\td", GuestText.Clean("a\0b\u0007c\td"));

    [Fact]
    public void Clean_StripsDel() => Assert.Equal("ab", GuestText.Clean("a\u007Fb"));

    [Fact]
    public void Clean_Truncates() =>
        Assert.Equal(32_768 + "…[truncated]".Length, GuestText.Clean(new string('a', 40_000)).Length);

    [Fact]
    public void Clean_Null_IsEmpty() => Assert.Equal("", GuestText.Clean(null));

    [Fact]
    public void Clean_DoesNotSplitSurrogatePair()
    {
        var s = new string('a', 9) + "\uD83D\uDE00" + "zzz";
        var r = GuestText.Clean(s, 10);
        Assert.Equal(new string('a', 9) + "…[truncated]", r);
    }
}
