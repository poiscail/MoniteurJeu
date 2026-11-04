@echo off
rem Compile MoniteurJeu.exe avec le compilateur C# fourni avec Windows (.NET Framework 4.8), rien a installer.
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /platform:x64 /codepage:65001 ^
  /win32manifest:app.manifest /r:System.ServiceProcess.dll /out:MoniteurJeu.exe MoniteurJeu.cs
