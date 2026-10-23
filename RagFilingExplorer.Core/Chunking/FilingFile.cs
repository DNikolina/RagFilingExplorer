using System.Text;
using System.Text.RegularExpressions;

namespace RagFilingExplorer.Core.Chunking;

/// <summary>
/// Opens a filing's HTML in the encoding it was written in - how every reader opens a filing, whichever strategy runs.
/// </summary>
internal static partial class FilingFile
{
    // Matches an HTML <meta charset="X"> or <meta http-equiv="Content-Type" content="...; charset=X">
    // declaration. Charset declarations are always pure ASCII per the HTML5 spec, which is what makes
    // it safe to scan for this with a fixed ASCII decode before the real encoding is known at all.
    [GeneratedRegex(@"<meta[^>]+charset=[""']?([\w-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharsetRegex();

    static FilingFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>A filing's HTML, decoded by <see cref="DetectEncoding"/>.</summary>
    internal static string Read(FileInfo filing)
    {
        byte[] bytes = File.ReadAllBytes(filing.FullName);
        return DetectEncoding(bytes).GetString(bytes);
    }

    // Raw EDGAR HTML (MSFT/ORCL/NDAQ's source here) declares no charset at all and is safely read as
    // UTF-8, but a browser-saved copy can declare (and genuinely be encoded as) something else -
    // NFLX-10K-2025.html is windows-1252. Read as UTF-8, its bytes would silently turn every non-ASCII
    // character (curly quotes, etc.) into U+FFFD. CodePagesEncodingProvider is needed for
    // GetEncoding("windows-1252") to resolve at all - without it, it throws ArgumentException, even
    // though the type is available from the shared framework with no separate package reference.
    internal static Encoding DetectEncoding(byte[] sourceBytes)
    {
        if (sourceBytes.Length >= 3 && sourceBytes[0] == 0xEF && sourceBytes[1] == 0xBB && sourceBytes[2] == 0xBF)
        {
            return Encoding.UTF8;
        }

        string asciiPreview = Encoding.ASCII.GetString(sourceBytes, 0, Math.Min(sourceBytes.Length, 4096));
        Match match = MetaCharsetRegex().Match(asciiPreview);
        if (!match.Success)
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(match.Groups[1].Value);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }
}
