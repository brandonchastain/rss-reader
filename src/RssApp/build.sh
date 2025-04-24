#!/bin/zsh

rm -rf bin/release/
dotnet build -c Release -r win-x64 --output bin/release/net9.0/win-x64