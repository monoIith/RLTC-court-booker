@echo off
setlocal

set "ROCKCLIFFE_DATA=%LOCALAPPDATA%\RockcliffeCourtBooker"

del /f /q "%ROCKCLIFFE_DATA%\court-booker.db" 2>nul
del /f /q "%ROCKCLIFFE_DATA%\court-booker.db-shm" 2>nul
del /f /q "%ROCKCLIFFE_DATA%\court-booker.db-wal" 2>nul
del /f /q "%ROCKCLIFFE_DATA%\logs\worker-*.log" 2>nul
del /f /q "%ROCKCLIFFE_DATA%\requests\*.request.json" 2>nul
del /f /q "%ROCKCLIFFE_DATA%\requests\*.result.json" 2>nul
del /f /q "%ROCKCLIFFE_DATA%\worker-requests\*.request.json" 2>nul
del /f /q "%ROCKCLIFFE_DATA%\worker-requests\*.result.json" 2>nul

for /d %%D in ("%ROCKCLIFFE_DATA%\diagnostics\*") do (
    del /f /q "%%~fD\failure.png" 2>nul
    del /f /q "%%~fD\trace.zip" 2>nul
    rd "%%~fD" 2>nul
)

rd "%ROCKCLIFFE_DATA%\logs" 2>nul
rd "%ROCKCLIFFE_DATA%\requests" 2>nul
rd "%ROCKCLIFFE_DATA%\worker-requests" 2>nul
rd "%ROCKCLIFFE_DATA%\diagnostics" 2>nul
rd "%ROCKCLIFFE_DATA%" 2>nul

exit /b 0
