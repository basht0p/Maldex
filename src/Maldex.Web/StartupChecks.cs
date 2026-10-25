namespace Maldex.Web;

public static class StartupChecks
{
    public const string MissingConnectionMessage =
        "ConnectionStrings:Maldex is not set; edit /etc/maldex/appsettings.Production.json and restart maldex";
}
