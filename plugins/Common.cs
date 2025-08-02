using HWT;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BotPlugin;

public class RateLimiter
{
    Dictionary<long, int> rateLimit = new();
    public int LimitCount { get; private set; }
    public int LimitTime { get; private set; }
    private object locker = new();
    private HashedWheelTimer countdownManager;
    private TimeSpan timeSpan;
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
            if (rateLimit.ContainsKey(groupId))
            {
                rateLimit[groupId]++;
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
    private Action callback;
    public OnceTimerTask(Action callback)
    {
        this.callback = callback;
    }
    public void Run(HWT.Timeout timeout)
    {
        callback.Invoke();
    }
}