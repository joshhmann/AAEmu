using System.Net;
using AAEmu.Game.Services.WebApi;
using AAEmu.Game.Services.WebApi.Controllers;
using NetCoreServer;

namespace AAEmu.UnitTests.Services.WebApi;

/// <summary>Route registration, authentication, and request-binding coverage for the MCP actor batch.</summary>
[NotInParallel]
public sealed class BotActionControllerRouteTests
{
    private static readonly (string Path, string Method, string Body, string MissingField)[] Routes =
    [
        ("/api/actors/discover_quests", "DiscoverQuests", "{\"bot\":\"McpBot01\",\"targetObjId\":42}", "targetObjId"),
        ("/api/actors/discover_self_quests", "DiscoverSelfQuests", "{\"bot\":\"McpBot01\"}", "bot"),
        ("/api/actors/interact_with", "InteractWith", "{\"bot\":\"McpBot01\",\"doodadObjId\":42}", "doodadObjId"),
        ("/api/actors/talk", "Talk", "{\"bot\":\"McpBot01\",\"npcObjId\":42}", "npcObjId"),
        ("/api/actors/equip", "Equip", "{\"bot\":\"McpBot01\",\"itemTemplateId\":42}", "itemTemplateId"),
        ("/api/actors/deposit_money", "DepositMoney", "{\"bot\":\"McpBot01\",\"amount\":100}", "amount"),
        ("/api/actors/withdraw_money", "WithdrawMoney", "{\"bot\":\"McpBot01\",\"amount\":100}", "amount"),
        ("/api/actors/deposit_item", "DepositItem", "{\"bot\":\"McpBot01\",\"itemTemplateId\":42}", "itemTemplateId"),
        ("/api/actors/withdraw_item", "WithdrawItem", "{\"bot\":\"McpBot01\",\"itemTemplateId\":42}", "itemTemplateId"),
        ("/api/actors/plant", "Plant", "{\"bot\":\"McpBot01\",\"seedItemTemplateId\":42,\"x\":1,\"y\":2,\"z\":3}", "seedItemTemplateId"),
        ("/api/actors/harvest", "Harvest", "{\"bot\":\"McpBot01\",\"doodadObjId\":42}", "doodadObjId"),
        ("/api/actors/craft", "Craft", "{\"bot\":\"McpBot01\",\"craftId\":42}", "craftId"),
        ("/api/actors/buy", "Buy", "{\"bot\":\"McpBot01\",\"merchantNpcObjId\":42,\"itemTemplateId\":7}", "merchantNpcObjId"),
        ("/api/actors/sell", "Sell", "{\"bot\":\"McpBot01\",\"merchantNpcObjId\":42,\"itemId\":1001}", "merchantNpcObjId"),
        ("/api/actors/pack_pickup", "PackPickup", "{\"bot\":\"McpBot01\",\"doodadObjId\":42}", "doodadObjId"),
        ("/api/actors/put_down", "PutDown", "{\"bot\":\"McpBot01\",\"packItemTemplateId\":42}", "packItemTemplateId"),
        ("/api/actors/load_pack_onto_vehicle", "LoadPackOntoVehicle", "{\"bot\":\"McpBot01\",\"slaveObjId\":42}", "slaveObjId"),
        ("/api/actors/board_vehicle", "BoardVehicle", "{\"bot\":\"McpBot01\",\"vehicleObjId\":42}", "vehicleObjId"),
        ("/api/actors/unboard_vehicle", "UnboardVehicle", "{\"bot\":\"McpBot01\"}", "bot"),
        ("/api/actors/drive_vehicle", "DriveVehicle", "{\"bot\":\"McpBot01\",\"vehicleObjId\":42,\"x\":1,\"y\":2,\"z\":3}", "vehicleObjId"),
    ];

    private string? _oldEnabled;
    private string? _oldToken;

    [Before(Test)]
    public void EnableApi()
    {
        _oldEnabled = Environment.GetEnvironmentVariable("AAEMU_BOT_CTRL");
        _oldToken = Environment.GetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN");
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", "1");
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", "route-test-token");
    }

    [After(Test)]
    public void RestoreApi()
    {
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", _oldEnabled);
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", _oldToken);
    }

    [Test]
    public async Task NewActorRoutes_AreRegisteredAsAuthenticatedPostRoutes()
    {
        var mapper = new RouteMapper();
        mapper.DiscoverRoutesFromType(typeof(BotActionController));

        foreach (var (path, method, _, _) in Routes)
        {
            var (route, _) = mapper.GetRoute(path, HttpMethod.Post);
            await Assert.That(route).IsNotNull();
            await Assert.That(route!.TargetMethod.Name).IsEqualTo(method);

            var unauthorized = Invoke(route.TargetMethod, new HttpRequest("POST", path, "HTTP/1.1"));
            await Assert.That(unauthorized.Status).IsEqualTo((int)HttpStatusCode.Unauthorized);
        }
    }

    [Test]
    public async Task NewActorRoutes_BindRequiredRequestFieldsAndRejectMissingFields()
    {
        var mapper = new RouteMapper();
        mapper.DiscoverRoutesFromType(typeof(BotActionController));

        foreach (var (path, _, body, missingField) in Routes)
        {
            var (route, _) = mapper.GetRoute(path, HttpMethod.Post);
            var missing = missingField switch
            {
                "bot" => body.Replace("\"bot\":\"McpBot01\"", string.Empty),
                "targetObjId" => body.Replace("\"targetObjId\":42", string.Empty),
                "doodadObjId" => body.Replace("\"doodadObjId\":42", string.Empty),
                "npcObjId" => body.Replace("\"npcObjId\":42", string.Empty),
                "merchantNpcObjId" => body.Replace("\"merchantNpcObjId\":42", string.Empty),
                "itemTemplateId" => body.Replace("\"itemTemplateId\":42", string.Empty),
                "packItemTemplateId" => body.Replace("\"packItemTemplateId\":42", string.Empty),
                "slaveObjId" => body.Replace("\"slaveObjId\":42", string.Empty),
                "vehicleObjId" => body.Replace("\"vehicleObjId\":42", string.Empty),
                "seedItemTemplateId" => body.Replace("\"seedItemTemplateId\":42", string.Empty),
                "craftId" => body.Replace("\"craftId\":42", string.Empty),
                "amount" => body.Replace("\"amount\":100", string.Empty),
                _ => throw new InvalidOperationException(missingField),
            };
            var invalid = new HttpRequest("POST", path, "HTTP/1.1");
            invalid.SetHeader("X-Auth-Token", "route-test-token");
            invalid.SetBody(missing);
            var rejected = Invoke(route!.TargetMethod, invalid);
            await Assert.That(rejected.Status).IsEqualTo((int)HttpStatusCode.BadRequest);
        }
    }

    private static HttpResponse Invoke(System.Reflection.MethodInfo method, HttpRequest request)
        => (HttpResponse)method.Invoke(new BotActionController(), [request])!;
}
