namespace BotPlugin;

public class SessionKey
{
    public string Id { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string ChannelType { get; set; } = string.Empty;
    public static string ToString(string id, string platform = "qq", string channelType = "group")
    {
        return $"{platform}/{channelType}/{id}";
    }
    public static string ToString(long id, string platform = "qq", string channelType = "group")
    {
        return ToString(id.ToString(), platform, channelType);
    }
    public static SessionKey Parse(string key)
    {
        var parts = key.Split('/', StringSplitOptions.None);
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Invalid session key format.", nameof(key));
        }
        return new SessionKey
        {
            Id = parts[2],
            Platform = parts[0],
            ChannelType = parts[1],
        };
    }

}
