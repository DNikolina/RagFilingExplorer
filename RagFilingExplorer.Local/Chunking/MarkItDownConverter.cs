using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace RagFilingExplorer.Local.Chunking;

/// <summary>
/// Shells out to the "markitdown" CLI to get the raw converted Markdown text for a source file.
/// Mirrors what Microsoft.Extensions.DataIngestion's MarkItDownReader does internally, but returns
/// the raw text instead of an already-parsed IngestionDocument, so the text can be split into
/// heading-tagged sections ourselves (see SectionSplitter).
/// </summary>
internal static partial class MarkItDownConverter
{
    // SEC EDGAR filings embed a hidden inline-XBRL metadata block (<ix:header>...</ix:header>) that
    // isn't part of the human-readable filing. markitdown doesn't strip non-visible content, so it
    // gets dumped as a huge garbage line ahead of the real text unless removed from the source first.
    [GeneratedRegex(@"<ix:header\b[^>]*>.*?</ix:header>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex IxHeaderRegex();

    // Matches an HTML <meta charset="X"> or <meta http-equiv="Content-Type" content="...; charset=X">
    // declaration. Charset declarations are always pure ASCII per the HTML5 spec, which is what makes
    // it safe to scan for this with a fixed ASCII decode before the real encoding is known at all.
    [GeneratedRegex(@"<meta[^>]+charset=[""']?([\w-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharsetRegex();

    static MarkItDownConverter() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    internal static string StripIxHeader(string html) => IxHeaderRegex().Replace(html, string.Empty);

    // Raw EDGAR HTML (MSFT/ORCL/NDAQ's source here) declares no charset at all and is safely read as
    // UTF-8 - that was this method's original, unconditional assumption. It broke on
    // NFLX-10K-2025.html, a browser-saved copy that declares (and is genuinely encoded as)
    // windows-1252: reading its bytes as UTF-8 silently replaced every non-ASCII character (curly
    // quotes, etc.) with U+FFFD before markitdown ever saw the content - confirmed directly by
    // comparing chunk output byte-for-byte against the other three filings, which had zero such
    // corruption. CodePagesEncodingProvider is needed for GetEncoding("windows-1252") to resolve at
    // all - confirmed it throws ArgumentException without it, even though the type is available from
    // the shared framework with no separate package reference required.
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

    public static async Task<string> ConvertAsync(FileInfo source, CancellationToken cancellationToken = default)
    {
        byte[] sourceBytes = await File.ReadAllBytesAsync(source.FullName, cancellationToken).ConfigureAwait(false);
        string html = DetectEncoding(sourceBytes).GetString(sourceBytes);
        return await ConvertHtmlAsync(StripIxHeader(html), source.Name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Converts already-decoded, already-cleaned HTML - for a strategy that rewrites the HTML first (the
    /// Linearized strategy replaces tables before conversion). <paramref name="sourceName"/> is only used
    /// for the temp file name and error messages.
    /// </summary>
    public static async Task<string> ConvertHtmlAsync(string cleanedHtml, string sourceName, CancellationToken cancellationToken = default)
    {
        string tempFilePath = Path.Combine(Path.GetTempPath(), $"{Path.GetFileNameWithoutExtension(sourceName)}-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(tempFilePath, cleanedHtml, cancellationToken).ConfigureAwait(false);

        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "markitdown",
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            // Without these, markitdown's UTF-8 output gets mangled on Windows (curly quotes, em-dashes, etc.).
            startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            startInfo.Environment["LC_ALL"] = "C.UTF-8";
            startInfo.Environment["LANG"] = "C.UTF-8";
            startInfo.ArgumentList.Add(tempFilePath);

            using Process process = new() { StartInfo = startInfo };

            // Without this, a missing markitdown surfaces as a bare "The system cannot find the file
            // specified" that never names what's missing.
            try
            {
                process.Start();
            }
            catch (Win32Exception ex)
            {
                throw new InvalidOperationException(
                    "Could not run the `markitdown` CLI - is it installed and on PATH? It needs Python 3.12+ and "
                    + "`pip install markitdown` (a shell opened before installing it may need restarting).", ex);
            }

            // Read both streams concurrently, not sequentially - a process that fills its stderr buffer
            // while we're still draining stdout (or vice versa) would otherwise deadlock.
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            string output = await outputTask.ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"markitdown failed with exit code {process.ExitCode} for '{sourceName}':\n{error}");
            }

            return output;
        }
        finally
        {
            File.Delete(tempFilePath);
        }
    }
}
