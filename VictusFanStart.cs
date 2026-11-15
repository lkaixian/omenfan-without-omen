// Tiny non-elevated launcher for the Run key: Windows skips admin-only exes at logon, so this just
// fires the elevated "VictusFan" scheduled task. Exists so Task Manager > Startup shows "VictusFan" + icon. MIT license.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

[assembly: AssemblyTitle("VictusFan")]
[assembly: AssemblyProduct("VictusFan")]
[assembly: AssemblyCompany("lkaixian")]
[assembly: AssemblyVersion("0.2.1.0")]
[assembly: AssemblyFileVersion("0.2.1.0")]

static class Start {
    static void Main() {
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), "/run /tn VictusFan") { CreateNoWindow = true, UseShellExecute = false });
    }
}
