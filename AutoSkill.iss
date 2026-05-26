[Setup]
AppName=AutoSkill
AppVersion=1.0
AppPublisher=Cowforgetfeet
DefaultDirName={autopf}\AutoSkill
DefaultGroupName=AutoSkill
UninstallDisplayIcon={app}\AutoSkill.exe
Compression=lzma2
SolidCompression=yes
OutputDir=C:\Users\Account\Desktop\Rebirth\AutoSkill
OutputBaseFilename=AutoSkill_Setup_v1.0

[Files]
Source: "C:\Users\Account\Desktop\Rebirth\AutoSkill\publish\AutoSkill.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "C:\Users\Account\Desktop\Rebirth\AutoSkill\readme.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\AutoSkill"; Filename: "{app}\AutoSkill.exe"
Name: "{group}\Readme"; Filename: "{app}\readme.txt"
Name: "{group}\Uninstall AutoSkill"; Filename: "{uninstallexe}"
Name: "{autodesktop}\AutoSkill"; Filename: "{app}\AutoSkill.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"

[Run]
Filename: "{app}\readme.txt"; Description: "View Readme (Instructions)"; Flags: shellexec postinstall skipifsilent