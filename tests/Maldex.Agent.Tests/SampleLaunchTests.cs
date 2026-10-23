using Maldex.Agent.Commands;
using Xunit;

namespace Maldex.Agent.Tests;

public class SampleLaunchTests
{
    private const string Root = @"C:\Windows";

    [Fact]
    public void Ps1_RunsInPowerShell_NotTheDefaultEditor()
    {
        var l = SampleLaunch.For(@"C:\Users\analyst\Desktop\a b.ps1", Root);
        Assert.False(l.UseShellExecute);
        Assert.Equal(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", l.FileName);
        Assert.Equal(["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", @"C:\Users\analyst\Desktop\a b.ps1"], l.Arguments);
    }

    [Theory]
    [InlineData("x.bat")]
    [InlineData("x.CMD")]
    public void BatchFiles_RunInCmd(string name)
    {
        var l = SampleLaunch.For(@"C:\s\" + name, Root);
        Assert.Equal(@"C:\Windows\System32\cmd.exe", l.FileName);
        Assert.Equal(["/c", @"C:\s\" + name], l.Arguments);
    }

    [Theory]
    [InlineData("x.vbs")]
    [InlineData("x.vbe")]
    [InlineData("x.js")]
    [InlineData("x.jse")]
    [InlineData("x.wsf")]
    public void WshScripts_RunInWscript(string name)
    {
        var l = SampleLaunch.For(@"C:\s\" + name, Root);
        Assert.Equal(@"C:\Windows\System32\wscript.exe", l.FileName);
        Assert.Equal([@"C:\s\" + name], l.Arguments);
    }

    [Fact]
    public void Hta_RunsInMshta()
    {
        var l = SampleLaunch.For(@"C:\s\x.hta", Root);
        Assert.Equal(@"C:\Windows\System32\mshta.exe", l.FileName);
        Assert.Equal([@"C:\s\x.hta"], l.Arguments);
    }

    [Theory]
    [InlineData(@"C:\s\x.exe")]
    [InlineData(@"C:\s\x.docm")]
    [InlineData(@"C:\s\x.pdf")]
    [InlineData(@"C:\s\noextension")]
    public void EverythingElse_UsesTheShell(string path)
    {
        var l = SampleLaunch.For(path, Root);
        Assert.True(l.UseShellExecute);
        Assert.Equal(path, l.FileName);
        Assert.Empty(l.Arguments);
    }
}
