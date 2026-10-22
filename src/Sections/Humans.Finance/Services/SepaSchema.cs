using System.Reflection;
using System.Xml;
using System.Xml.Schema;

namespace Humans.Finance.Services;

/// <summary>
/// Embedded, so validation costs no network call and cannot drift. The bank rejects a whole submission, so a malformed
/// file must never reach a browser.
/// </summary>
internal static class SepaSchema
{
    private const string ResourceName = "Humans.Finance.Resources.pain.001.001.09.xsd";

    private static readonly Lazy<XmlSchemaSet> Schemas = new(Load, isThreadSafe: true);

    public static void Validate(string xml)
    {
        var problems = new List<string>();
        var settings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = Schemas.Value,
        };
        settings.ValidationEventHandler += (_, e) => problems.Add(e.Message);

        using var reader = XmlReader.Create(new StringReader(xml), settings);
        while (reader.Read()) { }

        if (problems.Count > 0)
            throw new SepaPaymentFileException(
                "The generated SEPA file does not validate against pain.001.001.09: "
                + string.Join(" ", problems));
    }

    private static XmlSchemaSet Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded schema {ResourceName} is missing.");
        var set = new XmlSchemaSet();
        set.Add(XmlSchema.Read(stream, null)
            ?? throw new InvalidOperationException($"Embedded schema {ResourceName} is unreadable."));
        set.Compile();
        return set;
    }
}
