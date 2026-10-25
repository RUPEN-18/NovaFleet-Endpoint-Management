using System.Text.Json;
namespace AutoShutdown.Admin.Services;
internal static class ApiError
{
    internal static string Message(string body)
    {
        try
        {
            using var json=JsonDocument.Parse(body);
            if(json.RootElement.ValueKind==JsonValueKind.String)return json.RootElement.GetString()??"Request failed.";
            if(json.RootElement.ValueKind==JsonValueKind.Object && json.RootElement.TryGetProperty("error",out var error) && error.ValueKind==JsonValueKind.String)
                return error.GetString()??"Request failed.";
        }catch(JsonException){}
        return string.IsNullOrWhiteSpace(body)?"Request failed.":body;
    }
}
