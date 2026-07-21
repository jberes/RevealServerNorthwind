using Reveal.Sdk.Dom;

namespace RevealAI.Engine.Compilation;

/// <summary>
/// Helpers to materialize an <c>RdashDocument</c> as output. The Reveal SDK only exposes
/// <c>Save(filePath)</c> for the binary .rdash (a zip), so we round-trip through a temp file to
/// obtain the bytes for an HTTP download.
/// </summary>
public static class RdashOutput
{
    /// <summary>Serialize the document to .rdash (zip) bytes.</summary>
    public static byte[] ToRdashBytes(RdashDocument document)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"reveal_{Guid.NewGuid():N}.rdash");
        try
        {
            document.Save(temp);
            return File.ReadAllBytes(temp);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* best-effort cleanup */ }
            }
        }
    }

    /// <summary>Serialize the document's Dashboard.json content as a string (no zip wrapper).</summary>
    public static string ToJson(RdashDocument document) => document.ToJsonString();
}
