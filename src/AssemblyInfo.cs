// Program identity, and where Windows libraries may be loaded from: only the system folder.
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Watchkeeper")]
[assembly: AssemblyProduct("Watchkeeper")]
[assembly: AssemblyDescription("Windows service: folder policies, watchdog, scripts, vertical conversion, Axia GPIO and alerts")]
[assembly: AssemblyCompany("The Watchkeeper contributors")]
[assembly: AssemblyCopyright("Copyright (c) 2026 The Watchkeeper contributors. MIT License.")]
[assembly: AssemblyVersion("3.2.2.0")]
[assembly: AssemblyFileVersion("3.2.2.0")]
[assembly: AssemblyInformationalVersion("3.2.2")]
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
