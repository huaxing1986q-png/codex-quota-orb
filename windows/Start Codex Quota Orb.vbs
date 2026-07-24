Option Explicit

Dim shell, fileSystem, packageRoot, monitorPath, command
Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")

packageRoot = fileSystem.GetParentFolderName(WScript.ScriptFullName)
monitorPath = fileSystem.BuildPath(fileSystem.BuildPath(packageRoot, "scripts"), "CodexMonitor.ps1")

If Not fileSystem.FileExists(monitorPath) Then
    MsgBox "Codex monitor files are incomplete.", 16, "Codex Quota Orb"
    WScript.Quit 1
End If

command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File """ & monitorPath & """ -Mode Start"
shell.Run command, 0, False
