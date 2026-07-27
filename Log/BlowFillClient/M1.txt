@echo off

cd /d "%~dp0"

set SERVER=http://10.141.79.185:5050

dotnet "%~dp0publish\BlowFillClient.dll" 1 %SERVER%

pause