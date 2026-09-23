namespace RagFilingExplorer.Local;

/// <summary>
/// A startup problem the user can fix themselves (Ollama not running, a model not pulled, a stale or
/// incomplete index, ...). Program.cs prints just the message - no stack trace - and exits non-zero.
/// </summary>
internal sealed class StartupException(string message, Exception? innerException = null) : Exception(message, innerException);
