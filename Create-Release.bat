@echo off
title WARP Game Accelerator - Release Manager
echo ====================================================
echo WARP Game Accelerator - Auto Releaser to GitHub
echo ====================================================
echo.

set /p tag="Nhap phien ban moi (VD: v1.6.0): "

echo.
echo =^> Dang commit cac thay doi vao Git...
git add .
git commit -m "Release %tag%"

echo.
echo =^> Dang tao the (tag) %tag%...
git tag %tag%

echo.
echo =^> Dang Push code len GitHub...
git push origin main
git push origin %tag%

echo.
echo ====================================================
echo HOAN TAT!
echo GitHub Actions dang tu dong Build file .exe và tao
echo Release %tag% tren trang GitHub cua ban.
echo Ban co the kiem tra tien trinh tai tab "Actions".
echo ====================================================
pause
