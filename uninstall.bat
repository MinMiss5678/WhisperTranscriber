@echo off
chcp 65001 > nul
echo === WhisperTranscriber 解除安裝 ===
echo.
echo 此程式將刪除：
echo   - venv\（Python 虛擬環境與套件）
echo   - HuggingFace 模型快取（約 3 GB）
echo.
echo Python 與 .NET Runtime 為系統元件，不會自動移除。
echo 若需移除請至「控制台 → 新增或移除程式」手動操作。
echo.
set /p confirm=確定要繼續？(y/N)
if /i not "%confirm%"=="y" (
    echo 已取消。
    pause
    exit /b 0
)

:: ── 刪除 venv ────────────────────────────────────────────
if exist venv (
    echo 正在刪除 venv\...
    rmdir /s /q venv
    echo 完成。
) else (
    echo venv\ 不存在，跳過。
)

:: ── 刪除 HuggingFace 模型快取 ────────────────────────────
set "HF_CACHE=%USERPROFILE%\.cache\huggingface\hub"
if exist "%HF_CACHE%" (
    echo 正在刪除 HuggingFace 模型快取...
    rmdir /s /q "%HF_CACHE%"
    echo 完成。
) else (
    echo HuggingFace 快取不存在，跳過。
)

echo.
echo 解除安裝完成。
pause
