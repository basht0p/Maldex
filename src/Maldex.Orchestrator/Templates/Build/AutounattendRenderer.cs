using System.Xml.Linq;

namespace Maldex.Orchestrator.Templates.Build;

/// <summary>Turns the checked-in Autounattend.xml into the one for a given install medium: the image index and, for non-evaluation editions, the generic setup key.</summary>
public static class AutounattendRenderer
{
    private static readonly XNamespace Unattend = "urn:schemas-microsoft-com:unattend";

    public static string Render(string templateXml, int imageIndex, string? productKey)
    {
        var doc = XDocument.Parse(templateXml);

        var metaData = doc.Descendants(Unattend + "ImageInstall").Descendants(Unattend + "OSImage").Descendants(Unattend + "InstallFrom")
            .Elements(Unattend + "MetaData").FirstOrDefault()
            ?? throw new InvalidOperationException("Autounattend template has no ImageInstall/OSImage/InstallFrom/MetaData");
        metaData.SetElementValue(Unattend + "Key", "/IMAGE/INDEX");
        metaData.SetElementValue(Unattend + "Value", imageIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // The template's marker comments only say what the host rewrites here.
        foreach (var c in doc.DescendantNodes().OfType<XComment>().Where(c => c.Value.Contains("MX-IMAGE-", StringComparison.Ordinal)).ToList())
            c.Remove();

        if (!string.IsNullOrWhiteSpace(productKey))
        {
            var pk = doc.Descendants(Unattend + "UserData").Elements(Unattend + "ProductKey").FirstOrDefault()
                ?? throw new InvalidOperationException("Autounattend template has no UserData/ProductKey");
            pk.Elements(Unattend + "Key").Remove();
            pk.AddFirst(new XElement(Unattend + "Key", productKey.Trim()));
        }

        doc.Declaration = new XDeclaration("1.0", "utf-8", null);
        return doc.Declaration + Environment.NewLine + doc.ToString();
    }
}
