Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")
scriptDirectory = fileSystem.GetParentFolderName(WScript.ScriptFullName)
watcherPath = fileSystem.BuildPath(scriptDirectory, "CodexMonitorAutoStart.ps1")
shell.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -File """ & watcherPath & """", 0, False
