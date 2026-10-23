; Candela installer for Windows 10/11 x64 (NSIS 3, Modern UI 2, English and Russian).
;
; Built by packaging/windows/build-installer.sh (Linux) or build-installer.ps1 (Windows), which publish the editor and
; the command-line tool into one folder and run:
;
;   makensis -INPUTCHARSET UTF8 -DVERSION=0.1.0-preview.1 -DFILE_VERSION=0.1.0.1 -DSTAGE=<published folder>
;            -DGENERATED=<folder with uninstall-files.nsh> -DREDIST=<vc_redist.x64.exe> -DREDIST_BUILD=35211
;            -DOUTFILE=<Candela-Setup-...exe> installer.nsi
;
; What it does: installs for all users (Program Files, needs administrator rights) or for the current user only
; (%LOCALAPPDATA%\Programs\Candela); Start-menu and desktop shortcuts; .candela projects open in the editor and .mvr
; scenes get "Open with Candela"; a Windows Firewall rule lets Art-Net / sACN / OSC / PSN in (UDP, private and domain
; networks; when Setup has administrator rights); the Microsoft Visual C++ 2015-2022 runtime is installed when it is missing or
; older than the bundled one (the native UI libraries need it); an older Candela is removed first (upgrade in place,
; settings kept). The uninstaller removes exactly the installed files, the shortcuts, the associations and the firewall
; rule, and the user's settings, caches and logs only when asked to.

Unicode true
Target amd64-unicode
ManifestDPIAware true
ManifestSupportedOS Win10
SetCompressor /SOLID lzma
SetCompressorDictSize 64

!ifndef VERSION
  !error "VERSION is not defined (use -DVERSION=...)"
!endif
!ifndef FILE_VERSION
  !error "FILE_VERSION is not defined (use -DFILE_VERSION=a.b.c.d)"
!endif
!ifndef STAGE
  !error "STAGE (the published folder) is not defined"
!endif
!ifndef GENERATED
  !error "GENERATED (the folder with uninstall-files.nsh) is not defined"
!endif
!ifndef OUTFILE
  !define OUTFILE "Candela-Setup-${VERSION}-win-x64.exe"
!endif

!define PRODUCT "Candela"
!define PUBLISHER "Candela contributors"
!define APP_EXE "candela-app.exe"
!define CLI_EXE "candela.exe"
!define UNINSTALLER "Uninstall Candela.exe"
!define REGKEY "Software\Candela"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Candela"
!define FIREWALL_RULE "Candela (Art-Net, sACN, OSC, PSN)"
!define PROJECT_PROGID "Candela.Project"
!define MVR_PROGID "Candela.Mvr"
!define HOMEPAGE "https://github.com/nikitasa228/gdtf"
!define ISSUES "https://github.com/nikitasa228/gdtf/issues"
!define HERE "${__FILEDIR__}"

Name "${PRODUCT}"
Caption "$(^Name) ${VERSION}"
OutFile "${OUTFILE}"
BrandingText "${PRODUCT} ${VERSION}"
ShowInstDetails show
ShowUninstDetails show

VIProductVersion "${FILE_VERSION}"
VIFileVersion "${FILE_VERSION}"
VIAddVersionKey /LANG=0 "ProductName" "${PRODUCT}"
VIAddVersionKey /LANG=0 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=0 "FileVersion" "${FILE_VERSION}"
VIAddVersionKey /LANG=0 "CompanyName" "${PUBLISHER}"
VIAddVersionKey /LANG=0 "LegalCopyright" "© 2026 ${PUBLISHER}"
VIAddVersionKey /LANG=0 "FileDescription" "${PRODUCT} ${VERSION} Setup"

; ---------------------------------------------------------------------------------------------------- install modes

!define MULTIUSER_EXECUTIONLEVEL Highest
!define MULTIUSER_MUI
!define MULTIUSER_INSTALLMODE_COMMANDLINE
!define MULTIUSER_USE_PROGRAMFILES64
!define MULTIUSER_INSTALLMODE_INSTDIR "${PRODUCT}"
!define MULTIUSER_INSTALLMODE_INSTDIR_REGISTRY_KEY "${REGKEY}"
!define MULTIUSER_INSTALLMODE_INSTDIR_REGISTRY_VALUENAME "InstallDir"
!define MULTIUSER_INSTALLMODE_DEFAULT_REGISTRY_KEY "${REGKEY}"
!define MULTIUSER_INSTALLMODE_DEFAULT_REGISTRY_VALUENAME "InstallMode"
!include MultiUser.nsh
!include MUI2.nsh
!include LogicLib.nsh
!include FileFunc.nsh
!include WordFunc.nsh
!include nsDialogs.nsh
!include WinVer.nsh
!include x64.nsh

; ---------------------------------------------------------------------------------------------------- Modern UI

!define MUI_ICON "${HERE}/icon/candela.ico"
!define MUI_UNICON "${HERE}/icon/candela.ico"
!define MUI_WELCOMEFINISHPAGE_BITMAP "${HERE}/icon/installer-wizard.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "${HERE}/icon/installer-wizard.bmp"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP "${HERE}/icon/installer-header.bmp"
!define MUI_HEADERIMAGE_UNBITMAP "${HERE}/icon/installer-header.bmp"
!define MUI_ABORTWARNING
!define MUI_COMPONENTSPAGE_SMALLDESC
!define MUI_LANGDLL_ALLLANGUAGES
!define MUI_LANGDLL_REGISTRY_ROOT SHCTX
!define MUI_LANGDLL_REGISTRY_KEY "${REGKEY}"
!define MUI_LANGDLL_REGISTRY_VALUENAME "InstallerLanguage"

Var FirewallAllowed
Var FirewallCheckbox
Var PreviousVersion

!define MUI_WELCOMEPAGE_TITLE "$(WelcomeTitle)"
!define MUI_WELCOMEPAGE_TEXT "$(WelcomeText)"
!insertmacro MUI_PAGE_WELCOME

; The read-me page: what this preview is, what gets installed, the system requirements (no licence to accept).
!define MUI_PAGE_HEADER_TEXT "$(ReadmeTitle)"
!define MUI_PAGE_HEADER_SUBTEXT "$(ReadmeSubtitle)"
!define MUI_LICENSEPAGE_TEXT_TOP "$(ReadmeTop)"
!define MUI_LICENSEPAGE_TEXT_BOTTOM " "
!define MUI_LICENSEPAGE_BUTTON "$(^NextBtn)"
!insertmacro MUI_PAGE_LICENSE "$(ReadmeFile)"

!insertmacro MULTIUSER_PAGE_INSTALLMODE
!insertmacro MUI_PAGE_COMPONENTS
Page custom FirewallPageCreate FirewallPageLeave
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES

!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "$(FinishRun)"
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchCandela
!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\$(ReadmeInstalled)"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "$(FinishReadme)"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED
!define MUI_FINISHPAGE_LINK "$(FinishLink)"
!define MUI_FINISHPAGE_LINK_LOCATION "${HOMEPAGE}"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_COMPONENTS
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "Russian"
!insertmacro MUI_RESERVEFILE_LANGDLL

LicenseLangString ReadmeFile ${LANG_ENGLISH} "${HERE}/readme-en.txt"
LicenseLangString ReadmeFile ${LANG_RUSSIAN} "${HERE}/readme-ru.txt"

!include "${HERE}/installer-strings.nsh"

; ---------------------------------------------------------------------------------------------------- helpers

; True when Setup runs with administrator rights (MultiUser.nsh found them; with MULTIUSER_EXECUTIONLEVEL Highest an
; administrator's Setup is elevated in both install modes).
!macro _UserIsAdmin _a _b _t _f
  !insertmacro _LOGICLIB_TEMP
  StrCpy $_LOGICLIB_TEMP $MultiUser.Privileges
  StrCmp $_LOGICLIB_TEMP "Power" 0 +2
    StrCpy $_LOGICLIB_TEMP "Admin"
  StrCmp $_LOGICLIB_TEMP "Admin" `${_t}` `${_f}`
!macroend
!define UserIsAdmin `"" UserIsAdmin ""`

; Fails (asks to close Candela, retry or cancel) while the editor or the command-line tool runs from $INSTDIR: a running
; program file cannot be opened for writing.
!macro CHECK_NOT_RUNNING UN
Function ${UN}CheckNotRunning
  retry:
  ${If} ${FileExists} "$INSTDIR\${APP_EXE}"
    ClearErrors
    FileOpen $0 "$INSTDIR\${APP_EXE}" a
    ${If} ${Errors}
      MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "$(MsgCloseCandela)" /SD IDCANCEL IDRETRY retry
      Abort
    ${EndIf}
    FileClose $0
  ${EndIf}
  ${If} ${FileExists} "$INSTDIR\${CLI_EXE}"
    ClearErrors
    FileOpen $0 "$INSTDIR\${CLI_EXE}" a
    ${If} ${Errors}
      MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "$(MsgCloseCandela)" /SD IDCANCEL IDRETRY retry
      Abort
    ${EndIf}
    FileClose $0
  ${EndIf}
FunctionEnd
!macroend
!insertmacro CHECK_NOT_RUNNING ""
!insertmacro CHECK_NOT_RUNNING "un."

; Removes the file associations this installer made (and nothing another program set).
!macro REMOVE_ASSOCIATIONS
  ReadRegStr $0 SHCTX "Software\Classes\.candela" ""
  ${If} $0 == "${PROJECT_PROGID}"
    DeleteRegKey SHCTX "Software\Classes\.candela"
  ${EndIf}
  DeleteRegKey SHCTX "Software\Classes\${PROJECT_PROGID}"
  DeleteRegValue SHCTX "Software\Classes\.mvr\OpenWithProgids" "${MVR_PROGID}"
  ReadRegStr $0 SHCTX "Software\Classes\.mvr" ""
  ${If} $0 == "${MVR_PROGID}"
    DeleteRegValue SHCTX "Software\Classes\.mvr" ""
  ${EndIf}
  DeleteRegKey /ifempty SHCTX "Software\Classes\.mvr\OpenWithProgids"
  DeleteRegKey /ifempty SHCTX "Software\Classes\.mvr"
  DeleteRegKey SHCTX "Software\Classes\${MVR_PROGID}"
  DeleteRegKey SHCTX "Software\Classes\Applications\${APP_EXE}"
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
!macroend

; ---------------------------------------------------------------------------------------------------- sections

Section "!$(SecProgram)" SecProgram
  SectionIn RO
  Call CheckNotRunning

  ; Upgrade in place: the previous version's uninstaller removes its files (settings stay), then the new files come.
  ReadRegStr $0 SHCTX "${UNINSTKEY}" "InstallLocation"
  ${If} $0 != ""
  ${AndIf} ${FileExists} "$0\${UNINSTALLER}"
    DetailPrint "$(MsgRemovingPrevious)"
    CopyFiles /SILENT "$0\${UNINSTALLER}" "$PLUGINSDIR\previous-uninstaller.exe"
    ExecWait '"$PLUGINSDIR\previous-uninstaller.exe" /S _?=$0' $1
    DetailPrint "$(MsgRemovedPrevious) ($1)"
  ${EndIf}

  SetOutPath "$INSTDIR"
  File /r "${STAGE}/*"

  ; The Microsoft Visual C++ 2015-2022 runtime (vcruntime140.dll, vcruntime140_1.dll) for the native UI libraries.
  !ifdef REDIST
    Call InstallVcRuntime
  !endif

  WriteUninstaller "$INSTDIR\${UNINSTALLER}"

  ; Start menu
  CreateDirectory "$SMPROGRAMS\${PRODUCT}"
  CreateShortcut "$SMPROGRAMS\${PRODUCT}\${PRODUCT}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0 SW_SHOWNORMAL "" "$(ShortcutEditor)"
  ; cmd /k strips the outer pair of quotes when the line has special characters (a folder "Light & Sound", "(x86)"):
  ; the doubled quotes keep the script's path quoted in every case.
  CreateShortcut "$SMPROGRAMS\${PRODUCT}\$(ShortcutPrompt).lnk" "$SYSDIR\cmd.exe" '/k ""$INSTDIR\candela-prompt.cmd""' "$INSTDIR\candela.ico" 0 SW_SHOWNORMAL "" "$(ShortcutPromptDescription)"
  CreateShortcut "$SMPROGRAMS\${PRODUCT}\$(ShortcutReadme).lnk" "$INSTDIR\$(ReadmeInstalled)"
  CreateShortcut "$SMPROGRAMS\${PRODUCT}\$(ShortcutDocs).lnk" "$INSTDIR\docs"
  CreateShortcut "$SMPROGRAMS\${PRODUCT}\$(ShortcutUninstall).lnk" "$INSTDIR\${UNINSTALLER}"

  ; Registry: install folder and mode (for upgrades), Add/Remove Programs entry
  WriteRegStr SHCTX "${REGKEY}" "InstallDir" "$INSTDIR"
  WriteRegStr SHCTX "${REGKEY}" "InstallMode" "$MultiUser.InstallMode"
  WriteRegStr SHCTX "${REGKEY}" "Version" "${VERSION}"
  WriteRegStr SHCTX "${UNINSTKEY}" "DisplayName" "${PRODUCT}"
  WriteRegStr SHCTX "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr SHCTX "${UNINSTKEY}" "VersionNumber" "${FILE_VERSION}"
  WriteRegStr SHCTX "${UNINSTKEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr SHCTX "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\${APP_EXE},0"
  WriteRegStr SHCTX "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr SHCTX "${UNINSTKEY}" "UninstallString" '"$INSTDIR\${UNINSTALLER}" /$MultiUser.InstallMode'
  WriteRegStr SHCTX "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\${UNINSTALLER}" /$MultiUser.InstallMode /S'
  WriteRegStr SHCTX "${UNINSTKEY}" "URLInfoAbout" "${HOMEPAGE}"
  WriteRegStr SHCTX "${UNINSTKEY}" "HelpLink" "${ISSUES}"
  WriteRegStr SHCTX "${UNINSTKEY}" "Comments" "$(ArpComment)"
  WriteRegDWORD SHCTX "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD SHCTX "${UNINSTKEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD SHCTX "${UNINSTKEY}" "EstimatedSize" "$0"

  ; "Open with" lists Candela for its file types
  WriteRegStr SHCTX "Software\Classes\Applications\${APP_EXE}" "FriendlyAppName" "${PRODUCT}"
  WriteRegStr SHCTX "Software\Classes\Applications\${APP_EXE}\DefaultIcon" "" "$INSTDIR\candela.ico"
  WriteRegStr SHCTX "Software\Classes\Applications\${APP_EXE}\shell\open\command" "" '"$INSTDIR\${APP_EXE}" "%1"'
  WriteRegStr SHCTX "Software\Classes\Applications\${APP_EXE}\SupportedTypes" ".candela" ""
  WriteRegStr SHCTX "Software\Classes\Applications\${APP_EXE}\SupportedTypes" ".mvr" ""
SectionEnd

Section "$(SecDesktop)" SecDesktop
  CreateShortcut "$DESKTOP\${PRODUCT}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0 SW_SHOWNORMAL "" "$(ShortcutEditor)"
SectionEnd

Section "$(SecAssocProject)" SecAssocProject
  WriteRegStr SHCTX "Software\Classes\.candela" "" "${PROJECT_PROGID}"
  WriteRegStr SHCTX "Software\Classes\.candela" "Content Type" "application/x-candela-project"
  WriteRegStr SHCTX "Software\Classes\${PROJECT_PROGID}" "" "$(AssocProject)"
  WriteRegStr SHCTX "Software\Classes\${PROJECT_PROGID}\DefaultIcon" "" "$INSTDIR\candela.ico"
  WriteRegStr SHCTX "Software\Classes\${PROJECT_PROGID}\shell\open\command" "" '"$INSTDIR\${APP_EXE}" "%1"'
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
SectionEnd

Section "$(SecAssocMvr)" SecAssocMvr
  WriteRegStr SHCTX "Software\Classes\${MVR_PROGID}" "" "$(AssocMvr)"
  WriteRegStr SHCTX "Software\Classes\${MVR_PROGID}\DefaultIcon" "" "$INSTDIR\candela.ico"
  WriteRegStr SHCTX "Software\Classes\${MVR_PROGID}\shell\open\command" "" '"$INSTDIR\${APP_EXE}" "%1"'
  WriteRegStr SHCTX "Software\Classes\.mvr\OpenWithProgids" "${MVR_PROGID}" ""
  ; Candela becomes the program for .mvr only when no other program has claimed it.
  ReadRegStr $0 HKCR ".mvr" ""
  ${If} $0 == ""
    WriteRegStr SHCTX "Software\Classes\.mvr" "" "${MVR_PROGID}"
  ${EndIf}
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
SectionEnd

; Selected or not on the firewall page. A rule needs administrator rights: Setup has them for every all-users install,
; and for a just-for-me install by an administrator (it asks for elevation at the start); a standard user's
; just-for-me install cannot add one (Windows asks when Candela first receives data).
Section "-Firewall" SecFirewall
  ${If} $FirewallAllowed == 1
  ${AndIf} ${UserIsAdmin}
    DetailPrint "$(MsgFirewall)"
    nsExec::ExecToLog 'netsh advfirewall firewall delete rule name="${FIREWALL_RULE}"'
    Pop $0
    nsExec::ExecToLog 'netsh advfirewall firewall add rule name="${FIREWALL_RULE}" dir=in action=allow program="$INSTDIR\${APP_EXE}" enable=yes profile=private,domain protocol=UDP description="Lets Candela receive Art-Net, sACN, OSC and PSN from lighting consoles."'
    Pop $0
    ${If} $0 != 0
      DetailPrint "$(MsgFirewallFailed) ($0)"
    ${Else}
      WriteRegDWORD SHCTX "${REGKEY}" "FirewallRule" 1
    ${EndIf}
  ${EndIf}
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecProgram} "$(SecProgramDescription)"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "$(SecDesktopDescription)"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecAssocProject} "$(SecAssocProjectDescription)"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecAssocMvr} "$(SecAssocMvrDescription)"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ---------------------------------------------------------------------------------------------------- firewall page

Function FirewallPageCreate
  !insertmacro MUI_HEADER_TEXT "$(FirewallTitle)" "$(FirewallSubtitle)"
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}
  ${NSD_CreateLabel} 0 0 100% 72u "$(FirewallText)"
  Pop $0
  ${If} ${UserIsAdmin}
    ${NSD_CreateCheckbox} 0 80u 100% 24u "$(FirewallCheckbox)"
    Pop $FirewallCheckbox
    ${If} $FirewallAllowed == 1
      ${NSD_Check} $FirewallCheckbox
    ${EndIf}
  ${Else}
    StrCpy $FirewallCheckbox ""
    ${NSD_CreateLabel} 0 80u 100% 36u "$(FirewallCurrentUser)"
    Pop $0
  ${EndIf}
  nsDialogs::Show
FunctionEnd

Function FirewallPageLeave
  ${If} $FirewallCheckbox != ""
    ${NSD_GetState} $FirewallCheckbox $FirewallAllowed
    ${If} $FirewallAllowed == ${BST_CHECKED}
      StrCpy $FirewallAllowed 1
    ${Else}
      StrCpy $FirewallAllowed 0
    ${EndIf}
  ${EndIf}
FunctionEnd

; ---------------------------------------------------------------------------------------------------- VC++ runtime

!ifdef REDIST
; Installs the bundled Microsoft Visual C++ 2015-2022 x64 runtime when it is missing or older than the bundled build
; (the runtime is shared by all programs; its own installer keeps a newer one). Needs administrator rights: a
; per-user install asks Windows for them (UAC) only in that case.
; The x64 runtime registers itself under SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64; depending on the
; redistributable's version that key is in the 64-bit or in the 32-bit (WOW6432Node) view of the registry, so both are
; read. Both DLLs the native libraries import must also be in System32.
Function InstallVcRuntime
  ClearErrors
  SetRegView 64
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64" "Installed"
  ReadRegDWORD $1 HKLM "SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64" "Bld"
  ${If} $0 != 1
    SetRegView 32
    ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64" "Installed"
    ReadRegDWORD $1 HKLM "SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64" "Bld"
    SetRegView 64
  ${EndIf}
  ${If} $0 == 1
  ${AndIf} $1 >= ${REDIST_BUILD}
  ${AndIf} ${FileExists} "$SYSDIR\vcruntime140.dll"
  ${AndIf} ${FileExists} "$SYSDIR\vcruntime140_1.dll"
    DetailPrint "$(MsgVcPresent) ($1)"
    Return
  ${EndIf}
  DetailPrint "$(MsgVcInstalling)"
  SetOutPath "$PLUGINSDIR"
  File "/oname=vc_redist.x64.exe" "${REDIST}"
  ClearErrors
  ExecWait '"$PLUGINSDIR\vc_redist.x64.exe" /install /quiet /norestart' $2
  ; The redistributable could not even be started (the exit code is undefined then).
  ${If} ${Errors}
    StrCpy $2 "$(MsgVcNotStarted)"
  ${EndIf}
  ; 0 = installed, 1638 = a newer version is present, 3010 = installed, restart needed later
  ${If} $2 == 0
  ${OrIf} $2 == 1638
  ${OrIf} $2 == 3010
    DetailPrint "$(MsgVcDone) ($2)"
  ${Else}
    DetailPrint "$(MsgVcFailed) ($2)"
    MessageBox MB_OK|MB_ICONEXCLAMATION "$(MsgVcFailedBox)" /SD IDOK
  ${EndIf}
  SetOutPath "$INSTDIR"
FunctionEnd
!endif

; ---------------------------------------------------------------------------------------------------- start / finish

Function .onInit
  ; /D=<folder> on the command line sets $INSTDIR before .onInit; the install-mode initialisation would replace it.
  StrCpy $0 $INSTDIR
  !insertmacro MULTIUSER_INIT
  ${If} $0 != ""
    StrCpy $INSTDIR $0
  ${EndIf}
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "$(MsgNeeds64Bit)" /SD IDOK
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "$(MsgNeedsWindows10)" /SD IDOK
    Abort
  ${EndIf}
  !insertmacro MUI_LANGDLL_DISPLAY
  StrCpy $FirewallAllowed 1

  ; A newer Candela is not replaced by an older one without asking.
  ReadRegStr $PreviousVersion SHCTX "${UNINSTKEY}" "VersionNumber"
  ${If} $PreviousVersion != ""
    ${VersionCompare} "$PreviousVersion" "${FILE_VERSION}" $0
    ${If} $0 == 1
      MessageBox MB_YESNO|MB_ICONQUESTION "$(MsgDowngrade)" /SD IDYES IDYES +2
      Abort
    ${EndIf}
  ${EndIf}
FunctionEnd

; The editor starts as the signed-in user, not with the installer's administrator rights (through Explorer).
Function LaunchCandela
  Exec '"$WINDIR\explorer.exe" "$INSTDIR\${APP_EXE}"'
FunctionEnd

Function .onInstSuccess
  ; Silent installs (/S) leave a log of what was done for support.
  IfSilent 0 +2
    DetailPrint "${PRODUCT} ${VERSION} installed in $INSTDIR"
FunctionEnd

; ---------------------------------------------------------------------------------------------------- uninstaller

Section "un.$(SecProgram)" UnSecProgram
  SectionIn RO
  Call un.CheckNotRunning

  !include /CHARSET=UTF8 "${GENERATED}/uninstall-files.nsh"
  Delete "$INSTDIR\${UNINSTALLER}"
  RMDir "$INSTDIR"

  Delete "$SMPROGRAMS\${PRODUCT}\*.lnk"
  RMDir "$SMPROGRAMS\${PRODUCT}"
  Delete "$DESKTOP\${PRODUCT}.lnk"

  !insertmacro REMOVE_ASSOCIATIONS

  ReadRegDWORD $0 SHCTX "${REGKEY}" "FirewallRule"
  ${If} $0 == 1
    nsExec::ExecToLog 'netsh advfirewall firewall delete rule name="${FIREWALL_RULE}"'
    Pop $0
  ${EndIf}

  DeleteRegKey SHCTX "${UNINSTKEY}"
  DeleteRegKey SHCTX "${REGKEY}"
SectionEnd

; Off by default (and in silent uninstalls, e.g. an upgrade): settings, layouts, caches, autosaves and logs of the
; signed-in user. Projects and the fixture library folder are never touched.
Section /o "un.$(SecRemoveSettings)" UnSecSettings
  SetShellVarContext current
  RMDir /r "$APPDATA\${PRODUCT}"
  RMDir /r "$LOCALAPPDATA\${PRODUCT}"
  ${If} $MultiUser.InstallMode == "AllUsers"
    SetShellVarContext all
  ${EndIf}
SectionEnd

!insertmacro MUI_UNFUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${UnSecProgram} "$(UnSecProgramDescription)"
  !insertmacro MUI_DESCRIPTION_TEXT ${UnSecSettings} "$(UnSecSettingsDescription)"
!insertmacro MUI_UNFUNCTION_DESCRIPTION_END

Function un.onInit
  !insertmacro MULTIUSER_UNINIT
  !insertmacro MUI_UNGETLANGUAGE
FunctionEnd
