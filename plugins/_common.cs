using HWT;
using NapcatClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BotPlugin;

public class RateLimiter
{
    private readonly Dictionary<long, int> rateLimit = new();
    public int LimitCount { get; private set; }
    public int LimitTime { get; private set; }
    private readonly Lock locker = new();
    private readonly HashedWheelTimer countdownManager;
    private readonly TimeSpan timeSpan;
    public RateLimiter(int limitCount = 5, int limitTime = 20)
    {
        LimitCount = limitCount;
        LimitTime = limitTime;
        timeSpan = TimeSpan.FromSeconds(limitTime);
        countdownManager = new HashedWheelTimer(
            tickDuration: TimeSpan.FromSeconds(1),
            ticksPerWheel: 100,
            maxPendingTimeouts:0
            );
    }
    public bool CheckIsLimited(long groupId)
    {
        if (rateLimit.ContainsKey(groupId))
        {
            if (rateLimit[groupId] > LimitCount)
            {
                return true;
            }
        }
        return false;
    }
    public void Increase(long groupId)
    {
        lock (locker)
        {
            if (rateLimit.TryGetValue(groupId, out int value))
            {
                rateLimit[groupId] = ++value;
            }
            else
            {
                rateLimit.Add(groupId, 1);
            }
        }
        SetTimer(groupId);
    }

    void DecreaseCallback(long uid)
    {
        lock (locker)
        {
            rateLimit[uid]--;
        }
    }
    private void SetTimer(long uid)
    {
        countdownManager.NewTimeout(
            new OnceTimerTask( 
                () => { DecreaseCallback(uid); }
                ),
            timeSpan
            );
    }

}

class OnceTimerTask : TimerTask
{
    private readonly Action callback;
    public OnceTimerTask(Action callback)
    {
        this.callback = callback;
    }
    public void Run(HWT.Timeout timeout)
    {
        callback.Invoke();
    }
}


static class PluginUtils
{
    public static string ConstraintLength(string s,int lengthConstraint,string prompt="...")
    {
        if (s.Length > lengthConstraint)
        {
            s = string.Concat(s.AsSpan(0, lengthConstraint), prompt);
        }
        return s;
    }
    public static List<Message> MessageSpan2List(MessageChain span)
    {
        List<Message> list = [.. span];
        return list;
    }
}