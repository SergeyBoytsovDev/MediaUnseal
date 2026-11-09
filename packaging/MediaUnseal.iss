#ifndef AppVersion
  #define AppVersion "1.2.2"
#endif
#ifndef ReleaseSource
  #error ReleaseSource must be supplied by Build-Installer.ps1
#endif

[Setup]
AppId={{D8AF4D2A-89B2-4D64-9019-5C36A41E6E48}
AppName=Media Unseal
AppVersion={#AppVersion}
AppVerName=Media Unseal {#AppVersion}
AppPublisher=Sergey Boytsov
DefaultDirName={localappdata}\Programs\Media Unseal
DefaultGroupName=Media Unseal
DisableDirPage=no
DisableProgramGroupPage=no
UsePreviousAppDir=yes
UsePreviousGroup=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0
OutputBaseFilename=MediaUnseal-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
SetupIconFile=..\Assets\Icons\MediaUnseal.ico
UninstallDisplayIcon={app}\MediaUnseal.exe
LicenseFile={#ReleaseSource}\LICENSE.md
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#ReleaseSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Media Unseal"; Filename: "{app}\MediaUnseal.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Media Unseal"; Filename: "{app}\MediaUnseal.exe"; WorkingDir: "{app}"; Tasks: desktopicon
