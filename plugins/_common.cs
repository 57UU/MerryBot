using NapcatClient.MessageType;

namespace BotPlugin;

public class RateLimiter : IDisposable
{
    private readonly Dictionary<long, int> rateLimit = new();
    public int LimitCount { get; private set; }
    public int LimitTime { get; private set; }
    private readonly Lock locker = new();
    private readonly Dictionary<long, CancellationTokenSource> timers = new();
    public RateLimiter(int limitCount = 5, int limitTime = 20)
    {
        LimitCount = limitCount;
        LimitTime = limitTime;
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
        CancellationTokenSource? existingCts;
        lock (locker)
        {
            if (timers.TryGetValue(uid, out existingCts))
            {
                existingCts.Cancel();
            }
            var cts = new CancellationTokenSource();
            timers[uid] = cts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(LimitTime), cts.Token);
                    DecreaseCallback(uid);
                }
                catch (TaskCanceledException)
                {
                    // expected when cancelled
                }
                finally
                {
                    lock (locker)
                    {
                        timers.Remove(uid);
                    }
                }
            });
        }
    }

    public void Dispose()
    {
        lock (locker)
        {
            foreach (var cts in timers.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }
            timers.Clear();
        }
    }
}


static class PluginUtils
{
    public static string ConstraintLength(string s, int lengthConstraint, string prompt = "...")
    {
        if (s.Length > lengthConstraint)
        {
            s = string.Concat(s.AsSpan(0, lengthConstraint), prompt);
        }
        return s;
    }
    public static List<TypedMessage> MessageSpan2List(IReadOnlyList<TypedMessage> messageChain)
    {
        List<TypedMessage> list = messageChain.Select(message => message.Clone()).ToList();
        return list;
    }
}
