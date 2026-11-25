using System.Net;
using System.Net.Http.Json;

using ChessRealms.Platform.Api.Controllers;

using Xunit;

namespace ChessRealms.Platform.Api.Tests.Games;

public sealed class QuickPlayTests : GameApiTestBase
{
    [Fact]
    public async Task FourConcurrentSearchesCreateTwoDistinctBlitzGames()
    {
        var registered = new List<(Guid Id, string Token)>();
        for (int index = 0; index < 4; index++)
        {
            registered.Add(await RegisterAndSignInAsync($"player{index}"));
        }
        using HttpClient first = CreateClient(registered[0].Token);
        using HttpClient second = CreateClient(registered[1].Token);
        using HttpClient third = CreateClient(registered[2].Token);
        using HttpClient fourth = CreateClient(registered[3].Token);
        HttpClient[] clients = [first, second, third, fourth];
        Guid[] requests = clients.Select(_ => Guid.NewGuid()).ToArray();

        await Task.WhenAll(clients.Select((client, index) => JoinQuickPlayAsync(client, requests[index])));
        QuickPlayResponse[] tickets = await Task.WhenAll(clients.Select((client, index) =>
            ReadQuickPlayAsync(client, $"/api/quick-play/{requests[index]}")));
        Assert.All(tickets, ticket => Assert.Equal("Matched", ticket.Status));
        Assert.Equal(2, tickets.Select(ticket => ticket.GameId).Distinct().Count());

        foreach (var pair in tickets.Select((ticket, index) => (ticket, index)).GroupBy(item => item.ticket.GameId))
        {
            var matched = pair.ToArray();
            Assert.Equal(2, matched.Length);
            string url = $"/api/games/{pair.Key}";
            GameResponse game = (await ReadBoardAsync(clients[matched[0].index], url)).Game;
            Assert.Equal(game, (await ReadBoardAsync(clients[matched[1].index], url)).Game);
            Assert.Equal("Active", game.Status);
            Assert.Equal(0, game.Revision);
            Assert.Equal("Blitz5", game.Clock?.TimeControl);
            Assert.Equal(Clock.GetUtcNow().AddMinutes(5), game.Clock?.TurnDeadline);
            Assert.Equal(matched.Select(item => registered[item.index].Id).Order(), new[] { game.WhitePlayerId, game.BlackPlayerId }.Order());
        }
    }

    [Fact]
    public async Task QuickPlayRecoversMatchesAcrossTabsAndKeepsThemOutOfTheInvitationLimit()
    {
        (Guid firstId, string firstToken) = await RegisterAndSignInAsync("first");
        (Guid secondId, string secondToken) = await RegisterAndSignInAsync("second");
        using HttpClient first = CreateClient(firstToken);
        using HttpClient second = CreateClient(secondToken);
        using HttpClient anonymous = CreateClient();
        using HttpResponseMessage rejected = await anonymous.PostAsJsonAsync("/api/quick-play",
            new JoinQuickPlayRequest(Guid.NewGuid()), Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        Guid key = Guid.NewGuid();
        Guid otherTab = Guid.NewGuid();
        QuickPlayResponse waiting = await JoinQuickPlayAsync(first, key);
        Assert.Equal("Waiting", waiting.Status);
        Assert.Equal(waiting, await JoinQuickPlayAsync(first, otherTab));
        using HttpResponseMessage hidden = await second.GetAsync($"/api/quick-play/{key}", Cancellation);
        await AssertErrorAsync(hidden, HttpStatusCode.NotFound, "NotFound");
        using HttpResponseMessage hiddenCancel = await SendActionAsync(second, $"/api/quick-play/{key}", "cancel", 0);
        await AssertErrorAsync(hiddenCancel, HttpStatusCode.NotFound, "NotFound");

        Guid secondKey = Guid.NewGuid();
        QuickPlayResponse opponent = await JoinQuickPlayAsync(second, secondKey);
        QuickPlayResponse matched = await ReadQuickPlayAsync(first);
        Assert.Equal("Matched", matched.Status);
        Assert.Equal(opponent.GameId, matched.GameId);
        Assert.Equal(matched, await JoinQuickPlayAsync(first, key));
        Assert.Equal(matched, await JoinQuickPlayAsync(first, otherTab));
        Assert.Equal(opponent, await JoinQuickPlayAsync(second, secondKey));
        Assert.Equal(matched, await ChangeQuickPlayAsync(first, key, "cancel"));
        Assert.Single((await ListGamesAsync(first, "?status=Active")).Items);

        GameResponse game = (await ReadBoardAsync(first, $"/api/games/{matched.GameId}")).Game;
        HttpClient creator = game.WhitePlayerId == firstId ? first : second;
        Guid invitee = game.WhitePlayerId == firstId ? secondId : firstId;
        for (int index = 0; index < 5; index++)
        {
            GameResponse invite = await CreateGameAsync(creator, invitee);
            await ApplyActionAsync(creator, $"/api/games/{invite.Id}", "cancel", invite.Revision);
        }
        using HttpResponseMessage limited = await creator.PostAsJsonAsync("/api/games", new CreateGameRequest(invitee), Cancellation);
        await AssertErrorAsync(limited, HttpStatusCode.TooManyRequests, "InvitationRateLimited");
    }

    [Fact]
    public async Task OnlyHeartbeatsExtendQuickPlayAndAnExpiredRequestNeverRejoins()
    {
        (_, string firstToken) = await RegisterAndSignInAsync("first");
        (_, string secondToken) = await RegisterAndSignInAsync("second");
        using HttpClient first = CreateClient(firstToken);
        using HttpClient second = CreateClient(secondToken);
        Guid key = Guid.NewGuid();
        QuickPlayResponse waiting = await JoinQuickPlayAsync(first, key);
        Assert.Equal(Clock.GetUtcNow().AddSeconds(45), waiting.ExpiresAt);
        Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(waiting, await ReadQuickPlayAsync(first));
        Assert.Equal(waiting, await JoinQuickPlayAsync(first, key));

        QuickPlayResponse renewed = await ChangeQuickPlayAsync(first, key, "heartbeat");
        Assert.Equal(Clock.GetUtcNow().AddSeconds(45), renewed.ExpiresAt);
        Assert.Equal(waiting.EnqueuedAt, renewed.EnqueuedAt);
        Clock.Advance(TimeSpan.FromSeconds(45));
        Assert.Equal("Expired", (await ReadQuickPlayAsync(first)).Status);
        Assert.Equal("Expired", (await ChangeQuickPlayAsync(first, key, "heartbeat")).Status);

        QuickPlayResponse outsider = await JoinQuickPlayAsync(second, Guid.NewGuid());
        Assert.Equal("Waiting", outsider.Status);
        Assert.Equal("Expired", (await JoinQuickPlayAsync(first, key)).Status);
        QuickPlayResponse matched = await JoinQuickPlayAsync(first, Guid.NewGuid(), previous: key);
        Assert.Equal("Matched", matched.Status);
        Assert.Equal(matched.GameId, (await ReadQuickPlayAsync(second)).GameId);
    }

    [Fact]
    public async Task ConcurrentQuickPlayCancellationAndMatchingHaveOneOutcome()
    {
        (_, string firstToken) = await RegisterAndSignInAsync("first");
        (_, string secondToken) = await RegisterAndSignInAsync("second");
        using HttpClient first = CreateClient(firstToken);
        using HttpClient second = CreateClient(secondToken);
        Guid key = Guid.NewGuid();
        await JoinQuickPlayAsync(first, key);

        QuickPlayResponse[] results = await Task.WhenAll(
            ChangeQuickPlayAsync(first, key, "cancel"), JoinQuickPlayAsync(second, Guid.NewGuid()));
        if (results[0].Status == "Cancelled")
        {
            Assert.Equal("Waiting", results[1].Status);
            Assert.Null(results[1].GameId);
            Assert.Empty((await ListGamesAsync(first, "?status=Active")).Items);
        }
        else
        {
            Assert.Equal("Matched", results[0].Status);
            Assert.Equal("Matched", results[1].Status);
            Assert.Equal(Assert.IsType<Guid>(results[0].GameId), results[1].GameId);
        }
    }

    private async Task<QuickPlayResponse> JoinQuickPlayAsync(HttpClient client, Guid requestId, Guid? previous = null)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/quick-play",
            new JoinQuickPlayRequest(requestId, previous), Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<QuickPlayResponse>(await response.Content.ReadFromJsonAsync<QuickPlayResponse>(Cancellation));
    }

    private async Task<QuickPlayResponse> ReadQuickPlayAsync(HttpClient client, string url = "/api/quick-play") =>
        Assert.IsType<QuickPlayResponse>(await client.GetFromJsonAsync<QuickPlayResponse>(url, Cancellation));

    private async Task<QuickPlayResponse> ChangeQuickPlayAsync(HttpClient client, Guid id, string action)
    {
        using HttpResponseMessage response = await client.PostAsync($"/api/quick-play/{id}/{action}", null, Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<QuickPlayResponse>(await response.Content.ReadFromJsonAsync<QuickPlayResponse>(Cancellation));
    }
}
