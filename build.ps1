param([string]$Out = "VictusFan.exe")
Set-Location $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
Add-Type -ReferencedAssemblies System.Drawing -Path .\FanIcon.cs
[FanIcon]::WriteIco("$PWD\fan.ico")
& $csc /nologo /optimize /target:winexe /out:$Out /win32manifest:app.manifest /win32icon:fan.ico `
  /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll VictusFan.cs FanIcon.cs
& $csc /nologo /optimize /target:winexe /out:VictusFanStart.exe /win32icon:fan.ico VictusFanStart.cs
