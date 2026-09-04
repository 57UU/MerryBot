using MerryBot.Contracts;
using DataService;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MerryBot.WebUI.Api;

/// <summary>Bot 连接状态（由主程序在注册时通过工厂提供，避免 WebUI 依赖 NapcatClient）。</summary>
public sealed record BotStatusDto(
    bool Connected,
    string SelfId,
    string Nickname,
    string NapcatAddress);

/// <summary>概览页状态：Bot 连接 + 群聊数 + 系统运行信息 + git 版本信息。</summary>
public sealed record SystemStatusDto(
    BotStatusDto Bot,
    int GroupCount,
    string OsVersion,
    string Framework,
    string MachineName,
    int ProcessId,
    double UptimeSeconds,
    long WorkingSetBytes,
    long GcMemoryBytes,
    string Version,
    string GitInfo);

/// <summary>概览页状态 API；系统信息直接取自当前进程，Bot 状态由主程序注入，git 信息来自 HostLifecycle。</summary>
public static class StatusApiMapper
{
    private static readonly DateTime ProcessStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public static void Map(WebApplication app, Func<BotStatusDto> botStatusProvider, HistoryRecorder historyRecorder, IHostLifecycle hostLifecycle)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(botStatusProvider);
        ArgumentNullException.ThrowIfNull(historyRecorder);
        ArgumentNullException.ThrowIfNull(hostLifecycle);

        app.MapGet("/api/status", async () =>
        {
            var groups = await historyRecorder.GetAllGroupIdsAsync();
            // git 信息获取失败不应影响整体状态展示，失败时返回空串
            string gitInfo;
            try
            {
                gitInfo = await hostLifecycle.GetVersionInfoAsync();
            }
            catch
            {
                gitInfo = string.Empty;
            }
            return Results.Ok(new SystemStatusDto(
                Bot: botStatusProvider(),
                GroupCount: groups.Count,
                OsVersion: Environment.OSVersion.ToString(),
                Framework: RuntimeInformation.FrameworkDescription,
                MachineName: Environment.MachineName,
                ProcessId: Environment.ProcessId,
                UptimeSeconds: (DateTime.UtcNow - ProcessStartUtc).TotalSeconds,
                WorkingSetBytes: Environment.WorkingSet,
                GcMemoryBytes: GC.GetTotalMemory(forceFullCollection: false),
                Version: typeof(StatusApiMapper).Assembly.GetName().Version?.ToString() ?? "unknown",
                GitInfo: gitInfo));
        });
    }
}
