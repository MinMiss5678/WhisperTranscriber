@echo off
chcp 65001 > nul
echo === WhisperTranscriber 環境安裝 ===
echo.

:: ── Python ──────────────────────────────────────────────
python --version > nul 2>&1
if errorlevel 1 (
    echo Python 未安裝，正在透過 winget 安裝 Python 3.12...
    winget install --id Python.Python.3.12 --source winget --silent --accept-package-agreements --accept-source-agreements
    if errorlevel 1 (
        echo 錯誤：Python 安裝失敗，請至 https://www.python.org/ 手動安裝
        if /i not "%~1"=="/nopause" pause
        exit /b 1
    )
    call refreshenv > nul 2>&1
    set "PATH=%LOCALAPPDATA%\Programs\Python\Python312;%LOCALAPPDATA%\Programs\Python\Python312\Scripts;%PATH%"
    echo Python 安裝完成。
) else (
    echo Python 已安裝，跳過。
)

:: ── .NET 10 Runtime ─────────────────────────────────────
dotnet --list-runtimes 2>nul | findstr /C:"Microsoft.NETCore.App 10." > nul
if errorlevel 1 (
    echo .NET 10 Runtime 未安裝，正在透過 winget 安裝...
    winget install --id Microsoft.DotNet.Runtime.10 --source winget --silent --accept-package-agreements --accept-source-agreements
    if errorlevel 1 (
        echo 錯誤：.NET 10 Runtime 安裝失敗，請至 https://dotnet.microsoft.com/ 手動安裝
        if /i not "%~1"=="/nopause" pause
        exit /b 1
    )
    echo .NET 10 Runtime 安裝完成。
) else (
    echo .NET 10 Runtime 已安裝，跳過。
)

:: ── CUDA 檢測 ────────────────────────────────────────────
nvidia-smi > nul 2>&1
if errorlevel 1 (
    echo [警告] 未偵測到 NVIDIA GPU 或 CUDA 驅動。
    echo         GPU 加速將無法使用，轉錄速度約為 GPU 的 1/10。
    echo         如需 GPU 加速，請至 https://www.nvidia.com/drivers 安裝驅動後重新執行此腳本。
    echo.
) else (
    echo NVIDIA GPU 驅動已安裝，跳過。
)

:: ── Python venv ──────────────────────────────────────────
if not exist venv (
    echo 建立虛擬環境...
    python -m venv venv
    if errorlevel 1 (
        echo 錯誤：無法建立虛擬環境
        if /i not "%~1"=="/nopause" pause
        exit /b 1
    )
)

:: ── pip install ──────────────────────────────────────────
echo 安裝 Python 套件...
venv\Scripts\python.exe -m pip install --upgrade pip --quiet
venv\Scripts\python.exe -m pip install "setuptools>=65,<82" --quiet
venv\Scripts\python.exe -m pip install -r requirements.txt
if errorlevel 1 (
    echo 錯誤：套件安裝失敗
    if /i not "%~1"=="/nopause" pause
    exit /b 1
)

echo.
echo 安裝完成！可以開啟 WhisperGUI.exe 使用。
if /i not "%~1"=="/nopause" pause
