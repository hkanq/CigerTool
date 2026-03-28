@echo off
setlocal
title CigerTool by hkannq

if exist X:\Windows\explorer.exe (
  start "" X:\Windows\explorer.exe
)

if exist X:\CigerTool\CigerTool.exe (
  start "" X:\CigerTool\CigerTool.exe
  exit /b 0
)

echo CigerTool baslatilamadi. Fallback shell aciliyor.
start "" cmd.exe
