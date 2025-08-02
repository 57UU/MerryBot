using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace CommonLib;

public static class JsonUtils
{
    public static dynamic GetActualValue(JsonElement jsonElement)
    {
        return jsonElement.ValueKind switch
        {
            JsonValueKind.Object => jsonElement.Deserialize<Dictionary<string, dynamic>>(),
            JsonValueKind.Array => jsonElement.EnumerateArray().Select(GetActualValue).ToList(),
            JsonValueKind.String => jsonElement.GetString(),
            JsonValueKind.Number => ConvertNumber(jsonElement),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => throw new ArgumentException("Invalid JSON element"),
        };
    }
    private static dynamic ConvertNumber(JsonElement jsonElement)
    {
        if(jsonElement.TryGetInt32(out var result))
        {
            return result;
        }
        if(jsonElement.TryGetInt64(out var result2))
        {
            return result2;
        }
        if(jsonElement.TryGetDouble(out var result3))
        {
            return result3;
        }
        throw new Exception("the number can not be converted");
    }
}
