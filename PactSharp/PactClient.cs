using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PactSharp.Services;
using PactSharp.Types;

namespace PactSharp;

public enum ServerType
{
    Chainweb,
    LocalPact
}

public class PactClient
{
    private readonly HttpClient _http;
    private readonly ISettingsService _settingsService;

    public string ApiHost { get; set; }
    public string NetworkId { get; set; }
    public ServerType ServerType { get; set; }

    public int DefaultGasLimit { get; set; } = 1500;
    public decimal DefaultGasPrice { get; set; } = 0.00000001m;
    
    public static JsonSerializerOptions PactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public List<string> RecognizedChains { get; set; }
    
    public PactClient(HttpClient http, ISettingsService settingsService)
    {
        this._http = http;
        this._settingsService = settingsService;
        
        RecognizedChains = Enumerable.Range(0, 20).Select(i => i.ToString()).ToList();
    }

    public async Task Initialize()
    {
        var settings = await _settingsService.GetSettingsAsync();
        SetNetwork(settings.Network);
    }

    public void SetNetwork(Network network)
    {
        switch (network)
        {
            case Network.Mainnet:
                ApiHost = "api.chainweb.com";
                NetworkId = "mainnet01";
                ServerType = ServerType.Chainweb;
                break;
            case Network.Testnet:
                ApiHost = "api.testnet.chainweb.com";
                NetworkId = "testnet04";
                ServerType = ServerType.Chainweb;
                break;
            case Network.Local:
                ApiHost = "localhost:8080";
                NetworkId = "";
                ServerType = ServerType.LocalPact;
                break;
            default:
                throw new InvalidOperationException();
        }
    }

    private string GetApiUrl(string endpoint, string chain)
    {
        switch (ServerType)
        {
            case ServerType.Chainweb:
                return $"https://{ApiHost}/chainweb/0.0/{NetworkId}/chain/{chain}/pact{endpoint}";
            case ServerType.LocalPact:
                return $"http://{ApiHost}{endpoint}";
            default:
                return null;
        }
    }

    public async Task<string> SendTransactionAsync(PactCommand command)
    {
        var chain = command.Command.Metadata.ChainId;

        var req = new {cmds = new[] {command}};
        Console.WriteLine($"Encoded: {JsonSerializer.Serialize(req, PactJsonOptions)}");
        var resp = await _http.PostAsJsonAsync(GetApiUrl($"/api/v1/send", chain), req, PactJsonOptions);

        var respString = await resp.Content.ReadAsStringAsync();
        
        try
        {
            var parsedResponse = JsonDocument.Parse(respString);
            return parsedResponse.RootElement.GetProperty("requestKeys")[0].GetString();
        }
        catch (Exception e)
        {
            return e.Message + "\n" + respString;
        }
        
    }

    public async Task<PactCommandResponse> ExecuteLocalAsync(PactCommand command)
    {
        var chain = command.Command.Metadata.ChainId;

        Console.WriteLine($"Encoded: {JsonSerializer.Serialize(command, PactJsonOptions)}");
        var resp = await _http.PostAsJsonAsync(GetApiUrl($"/api/v1/local", chain), command, PactJsonOptions);

        var respString = await resp.Content.ReadAsStringAsync();
        
        try
        {
            var parsedResponse = await JsonSerializer.DeserializeAsync<PactCommandResponse>(await resp.Content.ReadAsStreamAsync(), PactJsonOptions);
            parsedResponse.SourceCommand = command;
            return parsedResponse;
        }
        catch (Exception e)
        {
            return new PactCommandResponse()
            {
                Result = new PactCommandResult() {Status = "failure", Error = new PactError() {Message = respString, Info = e.Message, CallStack = e.StackTrace?.Split('\n')}},
                SourceCommand = command
            };
        }
    }

    public PactCommand GenerateAccountDetailQuery(string chain, string module, string account)
    {
        var code = $"({module}.details (read-msg 'account))";
        var cmd = GenerateExecCommand(chain, code, new {account});

        return BuildCommand(cmd);
    }

    public PactCmd GenerateExecCommand(string chain, string code, object data = null)
    {
        var cmd = new PactCmd()
        {
            Metadata = GenerateMetadata(chain),
            NetworkId = NetworkId,
            Nonce = DateTime.UtcNow.ToLongDateString().HashEncoded(),
            Signers = new List<PactSigner>(),
            Payload = new PactPayload()
            {
                Exec = new PactExecPayload()
                {
                    Code = code,
                    Data = JsonObject.Create(JsonSerializer.SerializeToElement(data, PactJsonOptions)) ?? new JsonObject()
                }
            }
        };

        return cmd;
    }

    public PactMetadata GenerateMetadata(string chain, int gasLimit = -1, decimal gasPrice = -1m,
        string sender = "", int ttl = 3600,
        DateTime creationTime = default)
    {
        return new PactMetadata()
        {
            ChainId = chain,
            CreationTime = (long) ((creationTime != default ? creationTime : DateTime.UtcNow) - DateTime.UnixEpoch).TotalSeconds,
            GasLimit = gasLimit < 0 ? DefaultGasLimit : gasLimit,
            GasPrice = gasPrice < 0 ? DefaultGasPrice : gasPrice,
            Sender = sender,
            Ttl = ttl
        };
    }

    public PactCommand BuildCommand(PactCmd cmd, PactSignature[] signatures = null)
    {
        var ret = new PactCommand();
        
        var cmdEscaped = JsonSerializer.Serialize(cmd, PactJsonOptions);

        ret.Command = cmd;
        ret.Signatures = signatures ?? Array.Empty<PactSignature>();
        ret.CommandEncoded = cmdEscaped;
        ret.Hash = cmdEscaped.HashEncoded();

        return ret;
    }
}