@echo off
set "DOTNET_ROOT=%~dp0..\.tools\dotnet"
"%DOTNET_ROOT%\dotnet.exe" "%~dp0BiliTouch\bin\Debug\net10.0-windows\BiliTouch.dll" --mute
