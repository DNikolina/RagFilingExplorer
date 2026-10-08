# Security policy

RagFilingExplorer is a portfolio project that runs entirely on your own machine: it reads SEC filings from `data/`,
talks only to a local Ollama instance, and opens no network port of its own. Only the latest commit on `main` is
supported.

## Reporting a vulnerability

Please report it privately through this repository's **Security** tab ("Report a vulnerability"), not as a public
issue. Include what you found, how to reproduce it, and the commit you tested. I'll reply as soon as I can; this is a
personal project, so there is no guaranteed response time.

Vulnerabilities in a dependency (Ollama, markitdown, a NuGet package) belong with that project; a report here is
still welcome if this repository uses it in an unsafe way.
