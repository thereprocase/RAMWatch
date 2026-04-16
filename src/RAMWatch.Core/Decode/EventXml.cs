using System.IO;
using System.Xml;

namespace RAMWatch.Core.Decode;

/// <summary>
/// Helpers for pulling named fields out of a Windows event log record's raw XML.
/// All functions are pure and tolerant of malformed input — failures return null
/// or empty rather than throwing.
/// </summary>
internal static class EventXml
{
    private const string EventNs = "http://schemas.microsoft.com/win/2004/08/events/event";

    /// <summary>
    /// Parse the raw XML and return a name → value map of the EventData/Data nodes.
    /// Empty if the XML is null, malformed, or has no named Data fields.
    /// </summary>
    public static Dictionary<string, string> ReadNamedData(string? rawXml)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(rawXml))
            return fields;

        try
        {
            var doc = new XmlDocument();
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            using var reader = XmlReader.Create(new StringReader(rawXml), settings);
            doc.Load(reader);

            var nsMgr = new XmlNamespaceManager(doc.NameTable);
            nsMgr.AddNamespace("e", EventNs);

            var nodes = doc.SelectNodes("//e:EventData/e:Data[@Name]", nsMgr);
            if (nodes is not null)
            {
                foreach (XmlNode node in nodes)
                {
                    var name = node.Attributes?["Name"]?.Value;
                    if (name is not null)
                        fields[name] = node.InnerText ?? "";
                }
            }
        }
        catch
        {
            // Malformed XML — return whatever we got so far.
        }

        return fields;
    }

    /// <summary>
    /// Read positional EventData/Data nodes (no Name attribute) as a list.
    /// Used for legacy providers like Application Error that emit unnamed fields.
    /// </summary>
    public static List<string> ReadUnnamedData(string? rawXml)
    {
        var values = new List<string>();
        if (string.IsNullOrEmpty(rawXml))
            return values;

        try
        {
            var doc = new XmlDocument();
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            using var reader = XmlReader.Create(new StringReader(rawXml), settings);
            doc.Load(reader);

            var nsMgr = new XmlNamespaceManager(doc.NameTable);
            nsMgr.AddNamespace("e", EventNs);

            var nodes = doc.SelectNodes("//e:EventData/e:Data", nsMgr);
            if (nodes is not null)
            {
                foreach (XmlNode node in nodes)
                {
                    values.Add(node.InnerText ?? "");
                }
            }
        }
        catch
        {
        }

        return values;
    }
}
