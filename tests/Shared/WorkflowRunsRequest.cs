using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Endpoints;
using Daedalus.Api.Services;
using Daedalus.Application.DTOs.WorkflowRuns;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Thalos.Workflow;

namespace Daedalus.Tests.Shared;

/// <summary>
///     One request to a <see cref="WorkflowRunsEndpoints"/> handler called directly. Its <see cref="Context"/> holds, in
///     <see cref="HttpContext.RequestServices"/>, the <see cref="WorkflowRunGateway"/> and the
///     <see cref="IWorkflowRunQuery"/> the handlers resolve there after the engine filter, plus the endpoints' JSON
///     options and logging. Bodies and query values bind through the handler's own parameters, as the request
///     delegate binds them, so a parameter's name and optionality count. Linked into each test project that calls the
///     handlers; dispose it to dispose its services.
/// </summary>
internal sealed class WorkflowRunsRequest : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private WorkflowRunsRequest(ServiceProvider services, ClaimsPrincipal? user)
    {
        _services = services;
        Context = new DefaultHttpContext { RequestServices = services };
        if (user is not null)
        {
            Context.User = user;
        }
    }

    /// <summary>The request the handler is called with.</summary>
    public DefaultHttpContext Context { get; }

    /// <summary>A request whose services hold <paramref name="gateway"/> and, when given, <paramref name="query"/>.</summary>
    public static WorkflowRunsRequest For(WorkflowRunGateway gateway, ClaimsPrincipal? user = null, IWorkflowRunQuery? query = null)
    {
        var services = new ServiceCollection().AddLogging().AddDaedalusEndpoints().AddSingleton(gateway);
        if (query is not null)
        {
            services.AddSingleton(query);
        }

        return new WorkflowRunsRequest(services.BuildServiceProvider(), user);
    }

    /// <summary>
    ///     Binds <paramref name="body"/>, written as web JSON, to the <see cref="ApiBody{T}"/> parameter of
    ///     <paramref name="handler"/>.
    /// </summary>
    public async Task<ApiBody<T>> BodyAsync<T>(string handler, T body)
        where T : class
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, JsonSerializerOptions.Web));
        Context.Request.Method = HttpMethods.Post;
        Context.Request.ContentType = "application/json";
        Context.Request.ContentLength = bytes.Length;
        Context.Request.Body = new MemoryStream(bytes);
        return await BindAsync<ApiBody<T>>(Parameter(handler, typeof(ApiBody<T>)));
    }

    /// <summary>
    ///     Calls <see cref="WorkflowRunsEndpoints.List"/> with each given value as its query parameter, as the request
    ///     delegate binds them from the query string.
    /// </summary>
    public async Task<Results<Ok<WorkflowRunList>, ApiProblemResult>> ListAsync(
        ITaskQueryService tasks, string? status = null, string? process = null, string? after = null,
        int? take = null, string? signal = null, string? updatedSince = null)
    {
        var values = new List<KeyValuePair<string, string?>>();
        Add(values, "status", status);
        Add(values, "process", process);
        Add(values, "after", after);
        Add(values, "take", take?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(values, "signal", signal);
        Add(values, "updatedSince", updatedSince);
        Context.Request.QueryString = QueryString.Create(values);

        return await WorkflowRunsEndpoints.List(
            await QueryAsync<string>("status"), await QueryAsync<string>("process"), await QueryAsync<string>("after"),
            await QueryAsync<string>("signal"), await QueryAsync<string>("updatedSince"), Context, tasks,
            CancellationToken.None, await QueryAsync<int>("take"));

        static void Add(List<KeyValuePair<string, string?>> values, string name, string? value)
        {
            if (value is not null)
            {
                values.Add(new KeyValuePair<string, string?>(name, value));
            }
        }
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();

    private Task<ApiQuery<T>> QueryAsync<T>(string name)
        where T : IParsable<T> =>
        BindAsync<ApiQuery<T>>(typeof(WorkflowRunsEndpoints).GetMethod(nameof(WorkflowRunsEndpoints.List))!
            .GetParameters().Single(p => string.Equals(p.Name, name, StringComparison.Ordinal)));

    private static ParameterInfo Parameter(string handler, Type type) =>
        typeof(WorkflowRunsEndpoints).GetMethod(handler)!.GetParameters().Single(p => p.ParameterType == type);

    private async Task<TSelf> BindAsync<TSelf>(ParameterInfo parameter)
        where TSelf : class, IBindableFromHttpContext<TSelf> =>
        (await TSelf.BindAsync(Context, parameter))!;
}
