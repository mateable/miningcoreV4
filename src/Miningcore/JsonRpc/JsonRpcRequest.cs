using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Miningcore.JsonRpc;

[JsonObject(MemberSerialization.OptIn)]
public class JsonRpcRequest : JsonRpcRequest<object>
{
    public JsonRpcRequest()
    {
    }

    public JsonRpcRequest(string method, object parameters, object id) : base(method, parameters, id)
    {
    }
}

[JsonObject(MemberSerialization.OptIn)]
public class JsonRpcRequest<T>
{
    public JsonRpcRequest()
    {
    }

    public JsonRpcRequest(string method, T parameters, object id)
    {
        Method = method;
        Params = parameters;
        Id = id;
    }

    [JsonProperty("jsonrpc")]
    public string JsonRpc => "2.0";

    [JsonProperty("method", NullValueHandling = NullValueHandling.Ignore)]
    public string Method { get; set; }

    [JsonProperty("params")]
    public object Params { get; set; }

    [JsonProperty("id")]
    public object Id { get; set; }

    [JsonExtensionData]
    public IDictionary<string, object> Extra { get; set; }

    public TParam ParamsAs<TParam>() where TParam : class
    {
        if(Params is JToken token)
            return token.ToObject<TParam>();

        if(Params is ReadOnlySequence<byte> ros)
        {
            // The fast path only implements string[] (bitcoin-family stratum params).
            // Every other TParam is a JSON object whose DTO carries Newtonsoft
            // [JsonProperty] attributes. System.Text.Json ignores those attributes and
            // is case-sensitive, so it would return an object with all-null properties
            // WITHOUT throwing - defeating the catch below. Route those to Newtonsoft.
            if(typeof(TParam) == typeof(string[]))
            {
                try
                {
                    return FastDeserializeParams<TParam>(ros);
                }
                catch
                {
                    // fall through to the Newtonsoft parse below
                }
            }

            var str = Encoding.UTF8.GetString(ros.ToArray());
            return JsonConvert.DeserializeObject<TParam>(str);
        }

        return (TParam) Params;
    }

    private static TParam FastDeserializeParams<TParam>(ReadOnlySequence<byte> ros) where TParam : class
    {
        // For string arrays (most common stratum param type: ["worker", "password"])
        if(typeof(TParam) == typeof(string[]))
        {
            var list = new List<string>();

            if(ros.IsSingleSegment)
            {
                var reader = new System.Text.Json.Utf8JsonReader(ros.First.Span);
                if(reader.Read() && reader.TokenType == JsonTokenType.StartArray)
                {
                    while(reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if(reader.TokenType == JsonTokenType.String)
                            list.Add(reader.GetString());
                        else if(reader.TokenType == JsonTokenType.Number)
                            list.Add(System.Text.Encoding.UTF8.GetString(reader.ValueSpan));
                        else if(reader.TokenType == JsonTokenType.Null)
                            list.Add(null);
                    }
                }
            }
            else
            {
                var arr = ros.ToArray();
                var reader = new System.Text.Json.Utf8JsonReader(arr);
                if(reader.Read() && reader.TokenType == JsonTokenType.StartArray)
                {
                    while(reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if(reader.TokenType == JsonTokenType.String)
                            list.Add(reader.GetString());
                        else if(reader.TokenType == JsonTokenType.Number)
                            list.Add(System.Text.Encoding.UTF8.GetString(reader.ValueSpan));
                        else if(reader.TokenType == JsonTokenType.Null)
                            list.Add(null);
                    }
                }
            }

            return list.ToArray() as TParam;
        }

        // Unreachable: callers gate on typeof(TParam) == typeof(string[]).
        return null;
    }
}
