Option Explicit
Dim fs, shell, root, executable, shortcut, desktopFolder
Set fs = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
root = fs.GetParentFolderName(WScript.ScriptFullName)
executable = fs.BuildPath(root, "LiquidMusic.exe")
If Not fs.FileExists(executable) Then executable = fs.BuildPath(root, "native\LiquidMusic.exe")
If Not fs.FileExists(executable) Then
    MsgBox "Extract the complete Liquid Music folder from the ZIP first.", 16, "Liquid Music"
    WScript.Quit 1
End If
desktopFolder = shell.SpecialFolders("Desktop")
If WScript.Arguments.Named.Exists("folder") Then desktopFolder = WScript.Arguments.Named.Item("folder")
Set shortcut = shell.CreateShortcut(fs.BuildPath(desktopFolder, "Liquid Music.lnk"))
shortcut.TargetPath = executable
shortcut.WorkingDirectory = root
shortcut.IconLocation = executable & ",0"
shortcut.Description = "Liquid Music C#"
shortcut.WindowStyle = 1
shortcut.Save
If Not WScript.Arguments.Named.Exists("quiet") Then MsgBox "Liquid Music shortcut created on your desktop. Run this file again if you move the app folder.", 64, "Liquid Music"
