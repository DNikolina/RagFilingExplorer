using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace RagFilingExplorer.Core.Chunking;

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

    internal static string StripIxHeader(string html) => IxHeaderRegex().Replace(html, string.Empty);

    public static Task<string> ConvertAsync(FileInfo source, CancellationToken cancellationToken = default) =>
        ConvertHtmlAsync(StripIxHeader(FilingFile.Read(source)), source.Name, cancellationToken);

    /// <summary>
    /// Converts already-decoded, already-cleaned HTML - for a strategy that rewrites the HTML first (the
    /// Linearized strategy replaces tables before conversion). <paramref name="sourceName"/> is only used
    /// for the temp file name and error messages.
    /// </summary>
    public static async Task<string> ConvertHtmlAsync(string cleanedHtml, string sourceName, CancellationToken cancellationToken = default)
    {
        string tempFilePath = Path.Combine(Path.GetTempPath(), $"{Path.GetFileNameWithoutExtension(sourceName)}-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(tempFilePath, cleanedHtml, cancellationToken);

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
            string output = await outputTask;
            string error = await errorTask;
            await process.WaitForExitAsync(cancellationToken);

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
