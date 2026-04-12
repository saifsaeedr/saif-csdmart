#!/bin/bash
dotnet clean dmart.csproj
rm -rf bin obj
dotnet publish dmart.csproj -r linux-x64 -p:PublishAot=true -c Release
