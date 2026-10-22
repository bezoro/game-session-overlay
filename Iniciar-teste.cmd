@echo off
if exist "%~dp0ApolloOverlay.exe" (start "" "%~dp0ApolloOverlay.exe" --pilot) else (start "" "%~dp0artifacts\publish\ApolloOverlay.exe" --pilot)
