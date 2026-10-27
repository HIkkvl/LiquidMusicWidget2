Option Explicit
Dim fs, shell, root, executable
Set fs = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
root = fs.GetParentFolderName(WScript.ScriptFullName)
executable = fs.BuildPath(root, "native\LiquidMusic.exe")
If Not fs.FileExists(executable) Then executable = fs.BuildPath(root, "LiquidMusic.exe")
If Not fs.FileExists(executable) Then
    MsgBox "Liquid Music was not found. Extract the complete app folder from the ZIP before starting it.", 16, "Liquid Music"
    WScript.Quit 1
End If
shell.CurrentDirectory = root
shell.Run Chr(34) & executable & Chr(34), 1, False
