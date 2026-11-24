// Member-level suppressions, each with the reason the rule cannot be met there. Rules turned off for
// whole files are in .editorconfig. #pragma warning disable, [SuppressMessage] in other files and
// #nullable disable fail the build (Directory.Build.targets).
using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Globalization", "CA1303:Do not pass literals as localized parameters",
    Justification = "The tool is English-only (no resource tables); this is the one call the rule can see because Usage is a const.",
    Scope = "member", Target = "~M:Program.<Main>$(System.String[])~System.Int32")]
[assembly: SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
    Justification = "Ownership of the receiver moves to the returned GpsSource, whose Dispose disposes it (a case the CA2000 documentation allows).",
    Scope = "member", Target = "~M:WwanProbe.Infrastructure.Gnss.GpsSource.Start~WwanProbe.Infrastructure.Gnss.GpsSource")]
