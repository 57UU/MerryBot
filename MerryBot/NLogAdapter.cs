using NapcatClient;
using CommonLib;

namespace MerryBot;

internal class NLogAdapter : ISimpleLogger
{
    private static readonly NLog.Logger logger = NLog.LogManager.GetLogger("NapcatClient");
    public void Debug(string message)
    {
        logger.Debug(message);
    }

    public void Error(string message)
    {
        logger.Error(message);
    }

    public void Fatal(string message)
    {
        logger.Fatal(message);
    }

    public void Info(string message)
    {
        logger.Info(message);
    }

    public void Trace(string message)
    {
        logger.Trace(message);
    }

    public void Warn(string message)
    {
        logger.Warn(message);
    }
}
