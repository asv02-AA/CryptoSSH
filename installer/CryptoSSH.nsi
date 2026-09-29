Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
Name "CryptoSSH"
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey "ProductName" "CryptoSSH"
VIAddVersionKey "FileDescription" "CryptoSSH Installer"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "LegalCopyright" "Copyright (c) 2026 CryptoSSH contributors"
OutFile "..\release\CryptoSSH-${APP_VERSION}-Setup-x64.exe"
InstallDir "$PROGRAMFILES64\CryptoSSH"
InstallDirRegKey HKLM "Software\RutokenSSH" "InstallDir"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "license-unicode.txt"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "Требуется Windows x64."
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_ICONSTOP "Требуется Windows 10 (2004+) или Windows 11."
    Abort
  ${EndIf}
  SetRegView 64
  ; Upgrade in place, including the pre-CryptoSSH installation directory.
  ReadRegStr $1 HKLM "Software\RutokenSSH" "InstallDir"
  ${If} $1 != ""
    StrCpy $INSTDIR $1
  ${EndIf}
  ReadRegStr $0 HKLM "SOFTWARE\Microsoft\Windows NT\CurrentVersion" "CurrentBuildNumber"
  ${If} $0 < 19041
    MessageBox MB_ICONSTOP "Требуется Windows 10 версии 2004 или новее."
    Abort
  ${EndIf}
FunctionEnd

Section "CryptoSSH" Main
  SetShellVarContext all
  SetOutPath "$INSTDIR"
  File /r /x "*.pdb" "${PUBLISH_DIR}\*"
  !include "legacy-cleanup.nsh"
  ; Remove the old client-local bootstrapper on upgrade, not system software.
  Delete "$INSTDIR\vendor\rtDrivers.exe"
  Delete "$INSTDIR\vendor\rtpkcs11ecp.dll"
  Delete "$INSTDIR\vendor\jcPKCS11-2.dll"
  Delete "$INSTDIR\vendor\Rutoken-License.pdf"
  Delete "$INSTDIR\vendor\JaCarta-License.txt"
  Delete "$INSTDIR\vendor\manifest.json"
  Delete "$INSTDIR\vendor\jacarta-manifest.json"
  RMDir "$INSTDIR\vendor"
  Delete "$INSTDIR\RutokenSSH.exe"
  Delete "$INSTDIR\RutokenSSH.dll"
  Delete "$INSTDIR\RutokenSSH.deps.json"
  Delete "$INSTDIR\RutokenSSH.runtimeconfig.json"
  Delete "$DESKTOP\Rutoken SSH.lnk"
  Delete "$SMPROGRAMS\Rutoken SSH\Rutoken SSH.lnk"
  RMDir "$SMPROGRAMS\Rutoken SSH"
  WriteRegStr HKLM "Software\RutokenSSH" "InstallDir" "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\RutokenSSH" "DisplayName" "CryptoSSH"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\RutokenSSH" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\RutokenSSH" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\RutokenSSH" "DisplayIcon" "$INSTDIR\CryptoSSH.exe"
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\RutokenSSH" "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\RutokenSSH" "NoRepair" 1
  CreateDirectory "$SMPROGRAMS\CryptoSSH"
  CreateShortcut "$SMPROGRAMS\CryptoSSH\CryptoSSH.lnk" "$INSTDIR\CryptoSSH.exe"
  CreateShortcut "$DESKTOP\CryptoSSH.lnk" "$INSTDIR\CryptoSSH.exe"
SectionEnd

Section "Uninstall"
  SetRegView 64
  SetShellVarContext all
  Delete "$DESKTOP\CryptoSSH.lnk"
  Delete "$SMPROGRAMS\CryptoSSH\CryptoSSH.lnk"
  RMDir "$SMPROGRAMS\CryptoSSH"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\RutokenSSH"
  DeleteRegKey HKLM "Software\RutokenSSH"
  ; User data and shared Rutoken driver are intentionally retained.
  !include "generated-uninstall.nsh"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
