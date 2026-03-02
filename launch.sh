#!/bin/bash

restart_code=101

case $(uname -m) in
    x86_64)
        runtime="linux-x64"
        ;;
    aarch64|arm64)
        runtime="linux-arm64"
        ;;
    *)
        echo "Unknown architecture: $(uname -m), defaulting to linux-arm64"
        runtime="linux-arm64"
        ;;
esac
# 获取当前脚本所在目录
project_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

# 主循环
while true; do
    # 记录编译开始时间
    start_time=$(date +%s)
    
    # 先发布 HistoryWebFrontend 以生成 wwwroot 资源
    echo "开始发布 HistoryWebFrontend..."
    if ! dotnet publish HistoryWebFrontend/HistoryWebFrontend.csproj -c Release \
        -r $runtime \
        --self-contained false \
        -p:PublishTrimmed=false \
        -p:TrimMode=link \
        -p:PublishSingleFile=false \
        -p:EnableCompressionInSingleFile=false \
        -p:PublishReadyToRun=false \
        -p:PublishAot=false \
        -p:DebugType=None \
        -p:DebugSymbols=false; then
        echo "dotnet publish HistoryWebFrontend 失败，退出脚本"
        exit 1
    fi

    # 发布优化版本
    echo "开始发布MerryBot优化版本..."
    if ! dotnet publish MerryBot/MerryBot.csproj -c Release \
        -r $runtime \
        --self-contained false \
        -p:PublishTrimmed=false \
        -p:TrimMode=link \
        -p:PublishSingleFile=false \
        -p:EnableCompressionInSingleFile=false \
        -p:PublishReadyToRun=true \
        -p:PublishReadyToRunShowWarnings=true \
        -p:PublishAot=false \
        -p:DebugType=None \
        -p:DebugSymbols=true; then
        echo "dotnet publish 失败，退出脚本"
        exit 1
    fi

    # 复制 wwwroot 资源到 MerryBot 的 publish 目录
    echo "复制 wwwroot 资源..."
    if ! cp -r HistoryWebFrontend/bin/Release/net10.0/$runtime/publish/wwwroot MerryBot/bin/Release/net10.0/$runtime/publish/; then
        echo "复制 wwwroot 失败，退出脚本"
        exit 1
    fi
    
    # 计算编译总时间
    end_time=$(date +%s)
    total_time=$((end_time - start_time))
    minutes=$((total_time / 60))
    seconds=$((total_time % 60))
    echo "========================================"
    echo "编译完成！总耗时: ${minutes}分${seconds}秒"
    echo "========================================"

    # 运行应用程序
    echo "启动应用程序..."
    cd MerryBot/bin/Release/net10.0/$runtime/publish
    ./MerryBot
    exit_code=$?

    # 检查退出码
    if [ $exit_code -eq $restart_code ]; then
        echo "程序退出码为 $exit_code，等于重启码，准备重新编译并启动..."
        cd "$project_dir"
        # 添加短暂延迟确保资源释放
        sleep 1
        continue  # 继续循环，重新编译并启动
    else
        echo "程序退出码为 $exit_code，不等于重启码，退出脚本"
        break  # 退出循环
    fi
done
