using System.Text;

namespace RagFilingExplorer.Local;

/// <summary>
/// UTF-8 in and out. Without it, output redirected to a log is written in the console's OEM code page, so
/// the filings' non-breaking spaces become a lone 0xFF byte - invalid UTF-8 for the tools that read the logs
/// even inside an answer ("$9.1 billion", with a non-breaking space). Setting these changes
/// the console's code pages for the whole terminal session, so the originals are restored on exit.
/// </summary>
internal static class Utf8Console
{
    /// <summary>Sets UTF-8 (no BOM) for console input and output; disposing restores the originals.</summary>
    public static IDisposable Use()
    {
        Encoding output = Console.OutputEncoding;
        Encoding input = Console.InputEncoding;
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return new Restore(output, input);
    }

    private sealed class Restore(Encoding output, Encoding input) : IDisposable
    {
        public void Dispose()
        {
            Console.OutputEncoding = output;
            Console.InputEncoding = input;
        }
    }
}
