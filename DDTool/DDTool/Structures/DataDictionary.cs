using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DDTool.Exceptions;

namespace DDTool.Structures;

public class DataDictionary
{
    /// <summary>
    /// This is purely informative.  You can set it to nonsense if you want.
    /// </summary>
    /// <value></value>
    public string SourceFile { get; }

    /// <summary>
    /// Optional "customname" attribute on root fix tag
    /// </summary>
    public string? CustomName { get; set; }

    public int? MajorVersion { get; set; }
    public int? MinorVersion { get; set; }
    public int? ServicePack { get; set; }
    public bool IsFIXT { get; set; }

    public Dictionary<int, DDField> FieldsByTag { get; } = new();
    public Dictionary<string, DDField> FieldsByName { get; } = new();
    public Dictionary<string, DDMessage> Messages { get; } = new();

    public DataDictionary(string sourceFile)
    {
        SourceFile = sourceFile;
    }

    /// <summary>
    /// A combination of type/Major/Minor/SP.
    /// This is NOT the FIX BeginString.
    /// </summary>
    /// <value>e.g. "FIX.4.2", "FIXT.1.1", "FIX.5.0.SP2"</value>
    public string Identifier
    {
        get
        {
            var prefix = IsFIXT ? "FIXT" : "FIX";
            var svcPack = ServicePack is null ? "" : $"SP{ServicePack}";
            var defaultIdentifier =  $"{prefix}.{MajorVersion}.{MinorVersion}{svcPack}";
            // FP Enhancement: 2026-05-24 — prepend a sort prefix so FixPortal's customised 4.2 / 4.4 dictionaries process after the standard versions (their identifiers must therefore be stripped of the prefix again in IdentifierNoDots below).
            return _fixPortalDesiredOrder.TryGetValue(defaultIdentifier, out var value) ? $"{value}_{defaultIdentifier}" : defaultIdentifier;
        }
    }

    private readonly Dictionary<string, int> _fixPortalDesiredOrder = new()
    {
        { "FIX.4.0", 1 },
        { "FIX.4.1", 2 },
        { "FIX.4.3", 3 },
        { "FIX.5.0", 4 },
        { "FIX.5.0SP1", 5 },
        { "FIX.5.0SP2", 6 },
        { "FIXT.1.1", 7 },
        { "FIX.4.2", 8 },
        { "FIX.4.4", 8 },
    };

    /// <summary>
    /// Returns e.g. "FIX42", "FIXT11", "FIX50SP2"
    /// </summary>
    public string IdentifierNoDots
    {
        get
        {
            // FP Enhancement: 2026-05-24 — strip the leading "<n>_" sort prefix added by Identifier before flattening dots, so generated class/dir names stay clean ("FIX44" not "8_FIX44").
            Regex regex = new(@"(?:1_)?(FIXT?\.\d\.\d(?:SP\d+)?)");

            Match match = regex.Match(Identifier);

            return match.Success ?
                match.Groups[1].Value.Replace(".", "") :
                Identifier.Replace(".", "");
        }
    }

    /// <summary>
    /// Name to use for dir and project
    /// </summary>
    public string Name => CustomName ?? IdentifierNoDots;

    public void AddField(DDField fld) {
        if (FieldsByTag.ContainsKey(fld.Tag))
            throw new ParsingException($"Field tag is defined twice: {fld.Tag}");
        FieldsByTag[fld.Tag] = fld;

        if (FieldsByName.ContainsKey(fld.Name))
            throw new ParsingException($"Field name is defined twice: {fld.Tag}");
        FieldsByName[fld.Name] = fld;
    }

    public void AddMessage(DDMessage msg) {
        if (Messages.ContainsKey(msg.MsgType))
            throw new ParsingException($"Message type is defined twice: {msg.MsgType}");
        Messages[msg.MsgType] = msg;
    }

    /// <summary>
    /// Lookup the field by name.  Throw ParsingException if not found.
    /// </summary>
    /// <param name="fieldName"></param>
    public DDField LookupField(string fieldName) {
        if (!FieldsByName.TryGetValue(fieldName, out var field))
            throw new ParsingException($"Field '{fieldName}' is not defined in <fields> section.");
        return field;
    }
}
