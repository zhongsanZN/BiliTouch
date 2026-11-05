@echo off
set "DOTNET_ROOT=%~dp0..\.tools\dotnet"
"%DOTNET_ROOT%\dotnet.exe" "%~dp0BiliDesk\bin\Debug\net10.0-windows\BiliDesk.dll" --mute
