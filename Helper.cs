using System.Text;

using System.Text.Json.Nodes;
using Azure.Communication.Identity;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

public static class Helper
{
    public static JsonObject GetJsonObject(BinaryData data)
    {
        return JsonNode.Parse(data).AsObject();
    }
    public static async Task<string> ProvisionAcsIdentity(string connectionString)
    {
        var client = new CommunicationIdentityClient(connectionString);
        var user = await client.CreateUserAsync().ConfigureAwait(false);
        return user.Value.Id;
    }
    public static string GetCallerId(JsonObject jsonObject)
    {
        return (string)(jsonObject["from"]["rawId"]);
    }

    public static string GetIncomingCallContext(JsonObject jsonObject)
    {
        return (string)jsonObject["incomingCallContext"];
    }
}