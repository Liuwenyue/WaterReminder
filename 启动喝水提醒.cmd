@echo off
rem 喝水提醒启动脚本：优先运行已发布的 exe
set "EXE=%~dp0publish\WaterReminder.exe"
if exist "%EXE%" (
  start "" "%EXE%"
  exit /b 0
)
set "DOTNET=%~dp0dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"
echo 未找到 %EXE%，正在发布，请稍候...
"%DOTNET%" publish "%~dp0WaterReminder\WaterReminder.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o "%~dp0publish"
if errorlevel 1 (
  echo 发布失败，请查看上面的错误信息。
  pause
  exit /b 1
)
start "" "%EXE%"