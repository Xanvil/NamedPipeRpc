extern alias ClientContracts;
extern alias ServerContracts;

using System.Text.Json;
using Xanvil.NamedPipeRpc;

using ClientExt = ClientContracts::Xanvil.NamedPipeRpc.RpcGeneratedExtensions;
using ServerExt = ServerContracts::Xanvil.NamedPipeRpc.RpcGeneratedExtensions;
using ClientAmountAsString = ClientContracts::NamedPipeRpc.Test.ClientContracts.IQuoteAmountAsString;
using ClientLabelRenamed = ClientContracts::NamedPipeRpc.Test.ClientContracts.IQuoteLabelRenamed;
using ClientAmountOnly = ClientContracts::NamedPipeRpc.Test.ClientContracts.IQuoteAmountOnly;
using ClientDefaults = ClientContracts::NamedPipeRpc.Test.ClientContracts.IQuoteClientDefaults;
using ClientDto = ClientContracts::NamedPipeRpc.Test.ClientContracts.IQuoteDto;
using ClientDtoOmitted = ClientContracts::NamedPipeRpc.Test.ClientContracts.IQuoteDtoOmitted;
using ClientQuoteReq = ClientContracts::NamedPipeRpc.Test.ClientContracts.QuoteReq;
using ClientExtra = ClientContracts::NamedPipeRpc.Test.ClientContracts.IQuoteExtra;
using ClientFull = ClientContracts::NamedPipeRpc.Test.ClientContracts.IQuoteFull;
using ClientMissingCurrency = ClientContracts::NamedPipeRpc.Test.ClientContracts.IQuoteMissingCurrency;
using ClientMissingRetries = ClientContracts::NamedPipeRpc.Test.ClientContracts.IQuoteMissingRetries;
using ClientNoArgs = ClientContracts::NamedPipeRpc.Test.ClientContracts.IQuoteNoArgs;
using ClientRenamed = ClientContracts::NamedPipeRpc.Test.ClientContracts.IQuoteRenamedAmount;
using ClientReordered = ClientContracts::NamedPipeRpc.Test.ClientContracts.IQuoteReordered;
using ServerQuote = ServerContracts::NamedPipeRpc.Test.ServerContracts.IQuote;
using ServerQuoteImpl = ServerContracts::NamedPipeRpc.Test.ServerContracts.QuoteServer;

namespace NamedPipeRpc.Test;

/// <summary>
/// 一个业务参数的载荷就是该参数，不看参数名。
/// 两个及以上业务参数打成字段包。缺字段使用类型默认值，改名等于该字段缺失。
/// 可选成员放在自定义类型里，作为那一个参数传递。
/// </summary>
public class OptionalParameterMismatchTests
{
    /// <summary>与 <see cref="NamedPipePeer"/> 相同的序列化选项，用来单独观察缺字段。</summary>
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// 本地调用会用上 C# 可选参数默认值。这只存在于本进程的编译器插值，不会出现在管道上。
    /// </summary>
    [Fact]
    public async Task LocalCall_UsesCSharpOptionalDefaults()
    {
        var impl = new ServerQuoteImpl();
        Assert.Equal("8|CNY|3", await impl.QuoteAsync(8));
        Assert.Equal("anon", await impl.LabelAsync());
    }

    /// <summary>参数名和个数都对齐时，客户端写出的值原样进入服务端，服务端默认值不参与。</summary>
    [Fact]
    public async Task MatchingShape_SendsExplicitValues()
    {
        string echoed = await CallAsync(peer =>
            ClientExt.GetRequestProxy<ClientFull>(peer).QuoteAsync(8, "EUR", 2));
        Assert.Equal("8|EUR|2", echoed);

        string label = await CallAsync(peer =>
            ClientExt.GetRequestProxy<ClientFull>(peer).LabelAsync("bob"));
        Assert.Equal("bob", label);
    }

    /// <summary>一个参数对上多个参数。数字无法反序列化成参数包。</summary>
    [Fact]
    public async Task OneParam_AgainstMany_FailsShape()
    {
        Exception ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            CallAsync(peer => ClientExt.GetRequestProxy<ClientAmountOnly>(peer).QuoteAsync(8)));
        string message = DecodeErrMessage(ex.Message);
        Assert.Contains("could not be converted", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("QuoteAsync_Args", message, StringComparison.Ordinal);
    }

    /// <summary>单个参数改名不影响载荷。服务端收到的就是这个字符串。</summary>
    [Fact]
    public async Task SingleParameter_Renamed_StillBindsValue()
    {
        string label = await CallAsync(peer =>
            ClientExt.GetRequestProxy<ClientLabelRenamed>(peer).LabelAsync("bob"));
        Assert.Equal("bob", label);
    }

    /// <summary>没有载荷时，多个参数的参数包无法从 null 拆箱。</summary>
    [Fact]
    public async Task ZeroParams_AgainstMany_Fails()
    {
        Exception ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            CallAsync(peer => ClientExt.GetRequestProxy<ClientNoArgs>(peer).QuoteAsync()));
        string message = DecodeErrMessage(ex.Message);
        Assert.True(
            message.IndexOf("Object reference not set to an instance", StringComparison.Ordinal) >= 0
            || message.IndexOf("未将对象引用", StringComparison.Ordinal) >= 0,
            message);
    }

    /// <summary>没有载荷时，单个引用类型参数得到 null，不会回落到 <c>= "anon"</c>。</summary>
    [Fact]
    public async Task ZeroParams_AgainstSingleReference_PassesNull()
    {
        string label = await CallAsync(peer =>
            ClientExt.GetRequestProxy<ClientNoArgs>(peer).LabelAsync());
        Assert.Equal("<null>", label);
    }

    /// <summary>少了尾部参数 <c>retries</c>。得到 <c>int</c> 的默认值 0，不是方法上的 3。</summary>
    [Fact]
    public async Task MissingTrailingField_UsesTypeDefault()
    {
        string echoed = await CallAsync(peer =>
            ClientExt.GetRequestProxy<ClientMissingRetries>(peer).QuoteAsync(8, "EUR"));
        Assert.Equal("8|EUR|0", echoed);
    }

    /// <summary>少了中间参数 <c>currency</c>。得到 null，已发送的 <c>retries</c> 仍保留。</summary>
    [Fact]
    public async Task MissingMiddleField_UsesTypeDefault()
    {
        string echoed = await CallAsync(peer =>
            ClientExt.GetRequestProxy<ClientMissingCurrency>(peer).QuoteAsync(8, 4));
        Assert.Equal("8|<null>|4", echoed);
    }

    /// <summary>参数名不同。对不上的字段使用类型默认值，多出来的字段不代替它。</summary>
    [Fact]
    public async Task RenamedParameter_UsesTypeDefault()
    {
        string echoed = await CallAsync(peer =>
            ClientExt.GetRequestProxy<ClientRenamed>(peer).QuoteAsync(8, "EUR", 4));
        Assert.Equal("0|EUR|4", echoed);
    }

    /// <summary>
    /// 自定义类型是唯一的业务参数，载荷就是这个类型。参数名不同不影响，缺掉的成员由方法补默认值。
    /// </summary>
    [Fact]
    public async Task CustomType_MissingMembers_UseJsonDefaults()
    {
        string echoed = await CallAsync(peer =>
            ClientExt.GetRequestProxy<ClientDto>(peer).QuoteDtoAsync(new ClientQuoteReq { Amount = 8 }));
        Assert.Equal("8|CNY|3", echoed);
    }

    /// <summary>不写载荷时，单个引用类型参数得到 null。</summary>
    [Fact]
    public async Task CustomType_ParameterOmitted_PassesNull()
    {
        string echoed = await CallAsync(peer =>
            ClientExt.GetRequestProxy<ClientDtoOmitted>(peer).QuoteDtoAsync());
        Assert.Equal("<null>", echoed);
    }

    /// <summary>字段名一致时，参数声明顺序不影响绑定。</summary>
    [Fact]
    public async Task ReorderedParameters_BindByName()
    {
        string echoed = await CallAsync(peer =>
            ClientExt.GetRequestProxy<ClientReordered>(peer).QuoteAsync("EUR", 4, 8));
        Assert.Equal("8|EUR|4", echoed);
    }

    /// <summary>客户端多出来的字段被忽略，其余字段正常绑定。</summary>
    [Fact]
    public async Task ExtraParameter_IsIgnored()
    {
        string echoed = await CallAsync(peer =>
            ClientExt.GetRequestProxy<ClientExtra>(peer).QuoteAsync(8, "EUR", 4, "note"));
        Assert.Equal("8|EUR|4", echoed);
    }

    /// <summary>
    /// 可选参数写在客户端接口上时，省略实参会在进代理前被编译器填好，服务端收到的是填好的值。
    /// </summary>
    [Fact]
    public async Task ClientOptionalDefaults_AreSerialized()
    {
        string echoed = await CallAsync(peer =>
            ClientExt.GetRequestProxy<ClientDefaults>(peer).QuoteAsync(8));
        Assert.Equal("8|USD|9", echoed);
    }

    /// <summary>JSON 字符串无法写入服务端的 int 字段，请求失败。</summary>
    [Fact]
    public async Task IncompatibleJsonType_Fails()
    {
        Exception ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            CallAsync(peer =>
                ClientExt.GetRequestProxy<ClientAmountAsString>(peer).QuoteAsync("8", "EUR", 4)));

        Assert.Contains("could not be converted", DecodeErrMessage(ex.Message), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>字段名大小写不同仍能绑定。</summary>
    [Fact]
    public void Json_FieldNameMatch_IsCaseInsensitive()
    {
        var value = JsonSerializer.Deserialize<SampleArgs>("{\"Amount\":8,\"CURRENCY\":\"EUR\"}", JsonOptions);
        Assert.Equal(8, value.amount);
        Assert.Equal("EUR", value.currency);
        Assert.Equal(0, value.retries);
    }

    /// <summary>
    /// 缺字段按 JSON 的普通规则处理：struct 得到 0，带初始化器的 class 保留初始值。
    /// 生成的多参参数包是没有初始化器的 struct，因此缺少的参数是类型默认值。
    /// </summary>
    [Fact]
    public void Json_MissingMember_OnStructIsZero_OnClassKeepsInitializer()
    {
        var asStruct = JsonSerializer.Deserialize<SampleArgs>("{\"amount\":8}", JsonOptions);
        Assert.Equal(8, asStruct.amount);
        Assert.Equal(0, asStruct.retries);
        Assert.Null(asStruct.currency);

        var asClass = JsonSerializer.Deserialize<SampleArgsClass>("{\"amount\":8}", JsonOptions);
        Assert.NotNull(asClass);
        Assert.Equal(8, asClass!.amount);
        Assert.Equal(3, asClass.retries);
        Assert.Equal("CNY", asClass.currency);

        var explicitZero = JsonSerializer.Deserialize<SampleArgsClass>("{\"amount\":8,\"retries\":0,\"currency\":null}", JsonOptions);
        Assert.NotNull(explicitZero);
        Assert.Equal(0, explicitZero!.retries);
        Assert.Null(explicitZero.currency);
    }

    /// <summary>
    /// 服务端 <c>ReplyErrAsync</c> 把异常文本按 JSON 字符串写出，客户端按原文放进 <see cref="Exception.Message"/>。
    /// </summary>
    static string DecodeErrMessage(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<string>(raw) ?? raw;
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    static async Task<T> CallAsync<T>(Func<NamedPipePeer, Task<T>> call)
    {
        string pipe = "nprpc-opt-" + Guid.NewGuid().ToString("N");
        var serverRouter = new HandlerRouter();
        ServerExt.AddRequestHandler<ServerQuote>(serverRouter, _ => new ServerQuoteImpl());

        using var service = new RpcService(pipe, serverRouter);
        service.Start();

        using var client = new RpcClient(pipe, new HandlerRouter());
        var peer = await client.ConnectAsync();
        try
        {
            return await call(peer);
        }
        finally
        {
            peer.Close();
            service.Close();
        }
    }

    /// <summary>模拟生成器现在产出的多参参数包：public 字段、无初始化器。</summary>
#pragma warning disable CS0649
    struct SampleArgs
    {
        public int amount;
        public string currency;
        public int retries;
    }
#pragma warning restore CS0649

    /// <summary>若要把 C# 默认值保留到缺字段的 JSON 上，需要 class 字段初始化器，而不是 struct。</summary>
    sealed class SampleArgsClass
    {
#pragma warning disable CS0649
        public int amount;
#pragma warning restore CS0649
        public string currency = "CNY";
        public int retries = 3;
    }
}
