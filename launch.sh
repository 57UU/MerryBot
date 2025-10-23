#!/bin/bash

restart_code=1001
# 获取当前脚本所在目录
project_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

# 主循环
while true; do
    # 发布优化版本
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
    exit_code=$?

    # 检查退出码
    if [ $exit_code -eq $restart_code ]; then
        echo "程序退出码为 $exit_code，等于重启码，准备重新编译并启动..."
        cd "$project_dir"
        continue  # 继续循环，重新编译并启动
    else
        echo "程序退出码为 $exit_code，不等于重启码，退出脚本"
        break  # 退出循环
    fi
done
