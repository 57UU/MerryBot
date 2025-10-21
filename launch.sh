#!/bin/bash

# 发布优化版本（自包含，AOT编译）
echo "开始发布优化版本..."
dotnet publish MerryBot/MerryBot.csproj -c Release \
    -r linux-arm64 \
    --self-contained true \
    -p:PublishTrimmed=false \
    -p:TrimMode=link \
    -p:PublishSingleFile=false \
    -p:EnableCompressionInSingleFile=false \
    -p:PublishReadyToRun=true \
    -p:PublishReadyToRunShowWarnings=true \
    -p:PublishAot=false \
    -p:DebugType=None \
    -p:DebugSymbols=true

# 运行应用程序
echo "启动应用程序..."
cd MerryBot/bin/Release/net10.0/linux-arm64/publish
./MerryBot
