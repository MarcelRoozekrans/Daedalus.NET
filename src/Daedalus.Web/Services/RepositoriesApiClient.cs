using System.Net;
using System.Text.Json;
using Daedalus.Web.Auth;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace Daedalus.Web.Services;

/// <summary>
///     What <c>GET /api/repositories</c> answered when it was not an error: the list, or the refusal a user who is not an
///     admin gets. Each is a page state of its own, so neither is a failed Result.
/// </summary>
/// <param name="Repositories">The repositories, or <see langword="null"/> when the read was refused.</param>
/// <param name="Forbidden">Whether the server refused the read, a 403: the user is not an admin.</param>
public sealed record RepositoryListing(IReadOnlyList<RepositoryConfigurationDto>? Repositories, bool Forbidden)
{
    /// <summary>The server refused the read: a 403.</summary>
    public static RepositoryListing Denied { get; } = new(null, Forbidden: true);

    /// <summary>The list arrived.</summary>
    public static RepositoryListing Loaded(IReadOnlyList<RepositoryConfigurationDto> repositories) =>
        new(repositories, Forbidden: false);
}

/// <summary>
///     The Git Repositories API. Its failures keep the page's own text, which differs from <see cref="ApiClient"/>'s: a
///     refusal names the status the server answered, never the server's detail, so the page can say
///     "Failed to … repository. The server answered 500 Internal Server Error.". A request whose token cannot be refreshed
///     returns <see cref="SessionExpiry.SigningInAgain"/>, and a request whose caller went away returns
///     <see cref="Cancelled"/>, which an effect drops.
/// </summary>
public sealed class RepositoriesApiClient(HttpClient httpClient)
{
    private const string Path = "/api/repositories";

    /// <summary>The caller's token was cancelled: its page went, and there is nothing to show. Matches ApiClient's text.</summary>
    public const string Cancelled = "Request was cancelled";

    /// <summary>The read ran out of time: the client's own timeout, not the page going away.</summary>
    public const string TimedOut = "The request timed out.";

    /// <summary>
    ///     A write that got no answer: the request failed or ran out of time. The page shows its own text for it,
    ///     "Failed to … repository." alone, or "Could not test connection." at Error level.
    /// </summary>
    public const string NoAnswer = "The request got no answer.";

    /// <summary>
    ///     Every repository, or <see cref="RepositoryListing.Denied"/> for a 403. Any other refusal reads "The server
    ///     answered {status}.", a timeout <see cref="TimedOut"/>, and a request that failed or a body that is not the list
    ///     the reader's own message.
    /// </summary>
    public async Task<Result<RepositoryListing>> GetAllAsync(CancellationToken ct)
    {
        try
        {
            using var response = await httpClient.GetAsync(new Uri(Path, UriKind.Relative), ct);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return Result<RepositoryListing>.Success(RepositoryListing.Denied);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Result<RepositoryListing>.Failure(Answered(response));
            }

            var repositories = await response.Content.ReadFromJsonAsync<List<RepositoryConfigurationDto>>(ct) ?? [];
            return Result<RepositoryListing>.Success(RepositoryListing.Loaded(repositories));
        }
        catch (AccessTokenNotAvailableException)
        {
            return Result<RepositoryListing>.Failure(SessionExpiry.SigningInAgain);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result<RepositoryListing>.Failure(Cancelled);
        }
        catch (TaskCanceledException)
        {
            // Not cancelled by the caller, so the client's timeout ran out.
            return Result<RepositoryListing>.Failure(TimedOut);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            return Result<RepositoryListing>.Failure(ex.Message);
        }
    }

    /// <summary>Adds a repository. The created repository in the 201's body is not read: the page reloads the list.</summary>
    public Task<Result> CreateAsync(CreateRepositoryConfigurationDto repository, CancellationToken ct = default) =>
        SendAsync(token => httpClient.PostAsJsonAsync(Path, repository, token), ct);

    /// <summary>Updates a repository; the server answers 204.</summary>
    public Task<Result> UpdateAsync(Guid id, UpdateRepositoryConfigurationDto repository, CancellationToken ct = default) =>
        SendAsync(token => httpClient.PutAsJsonAsync($"{Path}/{id}", repository, token), ct);

    /// <summary>Deletes a repository; the server answers 204.</summary>
    public Task<Result> DeleteAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(token => httpClient.DeleteAsync(new Uri($"{Path}/{id}", UriKind.Relative), token), ct);

    /// <summary>
    ///     Tests the connection to a repository. The page branches only on success, so the server's
    ///     <c>TestConnectionResponse</c>, an API type the Web cannot reference, is not read.
    /// </summary>
    public Task<Result> TestConnectionAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(
            token => httpClient.PostAsync(new Uri($"{Path}/{id}/test-connection", UriKind.Relative), content: null, token),
            ct);

    /// <summary>
    ///     Sends a write whose success body is not read. A refusal reads "The server answered {status}.", a request that
    ///     got no answer is <see cref="NoAnswer"/>, and a caller that went away is <see cref="Cancelled"/>.
    /// </summary>
    private static async Task<Result> SendAsync(Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        try
        {
            using var response = await send(ct);
            return response.IsSuccessStatusCode
                ? Result.Success()
                : Result.Failure(Answered(response));
        }
        catch (AccessTokenNotAvailableException)
        {
            return Result.Failure(SessionExpiry.SigningInAgain);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result.Failure(Cancelled);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return Result.Failure(NoAnswer);
        }
    }

    /// <summary>The status a refusal answered with, as the page shows it.</summary>
    private static string Answered(HttpResponseMessage response) =>
        $"The server answered {HttpStatusText.Of(response)}.";
}
