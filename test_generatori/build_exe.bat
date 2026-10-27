@echo off
REM Windows uchun bitta .exe yig'ish (Python 3.10+ o'rnatilgan bo'lishi kerak)
python -m pip install -r requirements-dev.txt || exit /b 1
python -m PyInstaller --noconfirm --onefile --windowed --name TestGeneratori --add-data "fonts;fonts" main.py || exit /b 1
echo.
echo Tayyor: dist\TestGeneratori.exe
